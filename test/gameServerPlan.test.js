/**
 * План нового игрового сервера. Проверяется не то, как выглядит форма, а то, что
 * она обещает ровно те флаги, которые игра правда читает: список снят с
 * FejdStartup.ParseServerArguments, и разъехаться с ней он может только молча.
 */

const { test } = require('node:test');
const assert = require('node:assert');

const { planServer, PRESETS, MODIFIERS } = require('../src/gameServerPlan');

const GOOD = {
  slot: 'vtoroy',
  serverName: 'Аствард 2',
  worldName: 'AstvardTwo',
  port: 2458
};

function flagValue(plan, flag) {
  const found = plan.flags.find(([f]) => f === flag);
  return found ? found[1] : null;
}

test('обычный второй сервер собирается, и флаги те самые', () => {
  const plan = planServer(GOOD);
  assert.ok(plan.ok, plan.errors.join('; '));
  assert.strictEqual(flagValue(plan, '-port'), '2458');
  assert.strictEqual(flagValue(plan, '-world'), 'AstvardTwo');
  assert.strictEqual(flagValue(plan, '-public'), '0');
  assert.strictEqual(flagValue(plan, '-savedir'), '/srv/valheim-vtoroy/saves');
  assert.deepStrictEqual(plan.ports, [2458, 2459]);
  // Флага сида не существует, и придумать его план не имеет права.
  assert.ok(!plan.flags.some(([f]) => f === '-seed'));
});

test('порты уже работающего сервера не отдаются', () => {
  for (const port of [2455, 2456, 2457]) {
    const plan = planServer({ ...GOOD, port });
    // 2455 занимает пару 2455–2456, то есть тоже наезжает.
    assert.ok(!plan.ok, `порт ${port} прошёл, а не должен`);
  }
  assert.ok(planServer({ ...GOOD, port: 2458 }).ok);
});

test('публичный сервер без пароля не проходит: игра с таким просто выходит', () => {
  const noPass = planServer({ ...GOOD, public: true });
  assert.ok(!noPass.ok);
  const short = planServer({ ...GOOD, public: true, password: 'abc' });
  assert.ok(!short.ok);
  const ok = planServer({ ...GOOD, public: true, password: 'vikings' });
  assert.ok(ok.ok, ok.errors.join('; '));
  assert.strictEqual(flagValue(ok, '-password'), 'vikings');
});

test('пароль внутри имени мира игра отвергает — и мы вместе с ней', () => {
  const plan = planServer({ ...GOOD, public: true, password: 'Astvard' });
  assert.ok(!plan.ok);
});

test('ключ сервера — не путь и не кусок команды', () => {
  for (const slot of ['../etc', 'a b', 'Верх', '-x', 'a;rm', '']) {
    assert.ok(!planServer({ ...GOOD, slot }).ok, `ключ «${slot}» прошёл`);
  }
});

test('незнакомый пресет и незнакомое правило мира отклоняются', () => {
  assert.ok(!planServer({ ...GOOD, preset: 'Nope' }).ok);
  assert.ok(!planServer({ ...GOOD, modifiers: [{ name: 'Weather', option: 'More' }] }).ok);
  assert.ok(!planServer({ ...GOOD, modifiers: [{ name: 'Combat', option: 'Ужасно' }] }).ok);
  const ok = planServer({ ...GOOD, preset: PRESETS[3], modifiers: [{ name: MODIFIERS[0], option: 'Hard' }] });
  assert.ok(ok.ok, ok.errors.join('; '));
  assert.strictEqual(flagValue(ok, '-modifier'), 'Combat Hard');
});

test('правка мира не проходит молча: о ней предупреждают', () => {
  const plan = planServer({ ...GOOD, modifiers: [{ name: 'Resources', option: 'More' }] });
  assert.ok(plan.warnings.some((w) => w.includes('-resetmodifiers')));
  // Ставку ресурсов держит мод, и об этом надо сказать прежде, чем они подерутся.
  assert.ok(plan.warnings.some((w) => w.includes('мод')));
});

test('мир по сиду: это работа мода, и она видна в его конфиге', () => {
  const plan = planServer({ ...GOOD, bepinex: true, worldSource: 'seed', seed: 'iMbvYy6FIt' });
  assert.ok(plan.ok, plan.errors.join('; '));
  assert.ok(plan.files['astvard.servermod.cfg'].includes('Seed = iMbvYy6FIt'));
  assert.ok(plan.commands.some((c) => c.title.includes('конфиг мода')));
  // Законченного сохранения у нового мира нет, а server.sh ищет именно его.
  assert.ok(plan.files['server.env'].includes('ALLOW_NEW_WORLD=yes'));
});

test('без мода ни сид, ни слоты не обещаются', () => {
  assert.ok(!planServer({ ...GOOD, worldSource: 'seed', seed: 'abc' }).ok);
  assert.ok(!planServer({ ...GOOD, playerLimit: 20 }).ok);
  // А десять слотов — это просто игра, и мод для них не нужен.
  assert.ok(planServer({ ...GOOD, playerLimit: 10 }).ok);
});

test('слоты: границы и молчание, когда их не трогали', () => {
  for (const n of [0, -1, 65, 2.5]) {
    assert.ok(!planServer({ ...GOOD, bepinex: true, playerLimit: n }).ok, `${n} прошло`);
  }
  const twenty = planServer({ ...GOOD, bepinex: true, playerLimit: 20 });
  assert.ok(twenty.ok, twenty.errors.join('; '));
  assert.strictEqual(twenty.playerLimit, 20);
  assert.ok(twenty.files['astvard.servermod.cfg'].includes('PlayerLimit = 20'));
  // Больше десяти — вопрос к машине, а не к игре, и об этом сказано.
  assert.ok(twenty.warnings.some((w) => w.includes('машина')));
  // Десять — это игра как есть, и лишнего куска конфига быть не должно.
  assert.ok(!planServer({ ...GOOD, bepinex: true }).files['astvard.servermod.cfg']);
});

test('сид спрашивается только тогда, когда он нужен', () => {
  assert.ok(!planServer({ ...GOOD, bepinex: true, worldSource: 'seed', seed: '' }).ok);
  // Случайный сид придумывает не форма, а сервер при выдаче паспорта, поэтому
  // пустое поле здесь — не ошибка.
  assert.ok(planServer({ ...GOOD, bepinex: true, worldSource: 'random', seed: '' }).ok);
  // У привезённого мира сид уже внутри него.
  assert.ok(planServer({ ...GOOD, worldSource: 'have' }).ok);
});

test('привезённый мир — это папка, и о ней сказано', () => {
  const plan = planServer(GOOD);
  assert.strictEqual(plan.worldSource, 'have');
  assert.ok(plan.commands.some((c) => c.title === 'Положить мир'));
  assert.ok(!plan.files['server.env'].includes('ALLOW_NEW_WORLD'));
});

test('файлы говорят про свою папку, и порт в compose тот же, что в флагах', () => {
  const plan = planServer({ ...GOOD, port: 2460 });
  const compose = plan.files['compose.yml'];
  assert.ok(compose.includes('"2460-2461:2460-2461/udp"'));
  assert.ok(compose.includes('container_name: astvard-valheim-vtoroy'));
  // Мир пишется только по SIGINT — потерять эту строку значит терять мир при
  // каждом перезапуске, и молча.
  assert.ok(compose.includes('stop_signal: SIGINT'));
  assert.ok(plan.files['server.env'].includes('SERVER_PORT=2460'));
});

test('ошибочный план не отдаёт ни файлов, ни команд', () => {
  const plan = planServer({ slot: '', worldName: '', serverName: '' });
  assert.ok(!plan.ok);
  assert.strictEqual(plan.files, null);
  assert.deepStrictEqual(plan.commands, []);
});
