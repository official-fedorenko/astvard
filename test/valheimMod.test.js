/**
 * Разбор файлов мода — без сети и без базы: только чтение того, что игровой
 * сервер пишет себе на диск.
 *
 * Проверяется главным образом одно: совпадает ли наше чтение с тем, как эти же
 * файлы читает и пишет сам мод (valheim-mod/AstvardServerMod/Plugin.Currency.cs,
 * Plugin.SharedTemplates.cs). Расхождение здесь — это не «некрасивый JSON», а
 * сайт, уверенно рассказывающий про сервер то, чего в игре нет.
 */

const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  readRunes,
  readSharedTemplates,
  readMinutesPerRune,
  DEFAULT_MINUTES_PER_RUNE
} = require('../src/protocols/valheimMod');

// Своя папка на каждый случай: кэш разбора живёт по пути, и один и тот же путь
// с разным содержимым в пределах прогона давал бы прошлый ответ.
function tempDir(name) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `astvard-${name}-`));
  return dir;
}

function write(dir, file, text) {
  const full = path.join(dir, file);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, text, 'utf8');
  return full;
}

test('руны: строки разбираются, мусор пропускается, имя с пробелами цело', async () => {
  const dir = tempDir('runes');
  const file = write(dir, 'astvard-currency.txt',
    '# astvard runes: id, balance, seconds towards the next, name\r\n'
    + '76561198000000101\t5\t600\tSkald Kamennaya Ruka\r\n'
    + '76561198000000102\t0\t0\t\r\n'
    + 'строка без табов\r\n'
    + 'не-номер\t1\t2\tКто-то\r\n');

  const runes = await readRunes(file);
  assert.strictEqual(runes.length, 2);
  assert.deepStrictEqual(runes[0], {
    steamId: '76561198000000101', runes: 5, seconds: 600, character: 'Skald Kamennaya Ruka'
  });
  assert.strictEqual(runes[1].character, '', 'пустое имя — это пустое имя, а не сбой');
});

test('руны: файла нет — пусто, а не ошибка', async () => {
  const dir = tempDir('runes-missing');
  assert.deepStrictEqual(await readRunes(path.join(dir, 'нет-такого.txt')), []);
});

test('ставка рун зажимается так же, как её зажимает мод', async () => {
  // Mathf.Clamp(значение, 1, 1440) в Plugin.Currency.cs. Число вне границ сервер
  // зажимает у себя, а в файле оставляет как есть — поверив файлу, сайт считал бы
  // часы не по той ставке, по которой они начислялись.
  const cases = [
    ['MinutesPerRune = 30', 30],
    ['MinutesPerRune = 0', 1],
    ['MinutesPerRune = -5', 1],
    ['MinutesPerRune = 5000', 1440],
    ['MinutesPerRune = 1440', 1440],
    ['ДругойКлюч = 10', DEFAULT_MINUTES_PER_RUNE]
  ];

  for (const [line, expected] of cases) {
    const dir = tempDir('cfg');
    const file = write(dir, 'astvard.servermod.cfg', `[Руны]\n${line}\n`);
    assert.strictEqual(await readMinutesPerRune(file), expected, line);
  }

  const dir = tempDir('cfg-missing');
  assert.strictEqual(await readMinutesPerRune(path.join(dir, 'нет.cfg')), DEFAULT_MINUTES_PER_RUNE);
});

test('постройки: наружу идёт только то, что админ разобрал', async () => {
  const dir = tempDir('templates');
  const shared = path.join(dir, 'shared');

  write(shared, 'Дом.txt',
    '# astvard shared template\n#name Дом на холме\n#category Дома\n#author Skald\n#players yes\n'
    // Вторая деталь — сундук: у него девятым полем едет то, что внутри.
    + 'wood_wall;0;0;0;0;0;0;1\npiece_chest_wood;1;0;0;0;0;0;1;AQIDBAU=\n');
  // Прислано игроком: мод помечает «#from» и держит закрытым до решения админа.
  write(shared, 'Прислано.txt',
    '# astvard shared template\n#name Реклама\n#category Дома\n#author Bjorn\n#from 76561198000000102\n'
    + 'wood_wall;0;0;0;0;0;0;1\n');
  // Корзина мода.
  write(shared, 'deleted/Снесённое.txt',
    '# astvard shared template\n#name Снесённое\n#category Дома\nwood_wall;0;0;0;0;0;0;1\n');
  // Без имени — имя файла на сайт не отдаём.
  write(shared, 'БезИмени.txt', '# astvard shared template\n#category Дома\nwood_wall;0;0;0;0;0;0;1\n');
  // Пустые заголовки поле не затирают: мод при пустом значении его не трогает.
  write(shared, 'Пустые.txt',
    '# astvard shared template\n#name Кузница\n#category\n#author\n#players no\n'
    + 'forge;0;0;0;0;0;0;1\n');

  const list = await readSharedTemplates(shared);
  assert.deepStrictEqual(list.map((t) => t.name).sort(), ['Дом на холме', 'Кузница']);

  const house = list.find((t) => t.name === 'Дом на холме');
  assert.strictEqual(house.pieces, 2);
  assert.strictEqual(house.forPlayers, true);

  const forge = list.find((t) => t.name === 'Кузница');
  assert.strictEqual(forge.category, '', 'пустой #category не выдумывает значение');
  assert.strictEqual(forge.forPlayers, false, '«#players no» — это «не открыта»');
});

test('постройки: длинные заголовки обрезаются, папки нет — пусто', async () => {
  const dir = tempDir('templates-long');
  const shared = path.join(dir, 'shared');
  write(shared, 'Длинная.txt',
    `# astvard shared template\n#name ${'о'.repeat(500)}\n#category Дома\n`
    + 'wood_wall;0;0;0;0;0;0;1\n');

  const [one] = await readSharedTemplates(shared);
  assert.ok(one.name.length <= 121, `имя обрезано, а не отдано целиком: ${one.name.length}`);

  assert.deepStrictEqual(await readSharedTemplates(path.join(dir, 'нет-папки')), []);
});
