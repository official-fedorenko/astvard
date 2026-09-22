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

test('новый мир означает случайный сид, и об этом сказано', () => {
  const plan = planServer({ ...GOOD, newWorld: true });
  assert.ok(plan.warnings.some((w) => w.includes('СЛУЧАЙНЫЙ')));
  assert.ok(plan.files['server.env'].includes('ALLOW_NEW_WORLD=yes'));
  // И тем же планом ключ снимается: оставленный, он превращает опечатку в имени
  // мира обратно в молча созданный пустой мир.
  assert.ok(plan.commands.some((c) => c.code.includes('ALLOW_NEW_WORLD')));
  // Мир кладут только тогда, когда он есть.
  assert.ok(!plan.commands.some((c) => c.title.includes('Положить мир')));
  assert.ok(planServer(GOOD).commands.some((c) => c.title.includes('Положить мир')));
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
