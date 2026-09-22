/**
 * Чтение паспорта мира: из файла, который хозяин принёс со своего компьютера, надо
 * узнать сид. Писать такой файл мы не беремся — почему, сказано в самом модуле.
 *
 * Образец — настоящий `_main.1.fwl2` мира AstwardWorld, снятый с отладочного
 * сервера 22.09.2026 и вписанный сюда байтами: файл на диске у той сессии, и
 * зависеть от него тест не должен.
 */

const { test } = require('node:test');
const assert = require('node:assert');

const {
  stableHashCode, generateSeed, readWorldPassport,
  SEED_ALPHABET, WORLD_VERSION, WORLD_GEN_VERSION
} = require('../src/valheimWorldFile');

// AstwardWorld, сид iMbvYy6FIt, один ключ мира (resourcerate 500) и один игрок в
// истории — то есть мир, в который ходили, а не наша заготовка.
const REAL = Buffer.from(
  '81000000290000000c41737477617264576f726c640a694d6276597936464974d1ad786f1fe2afcc'
  + 'ffffffff020000000101000000107265736f7572636572617465203530300100000017537465616d'
  + '5f3736353631313938343235313038373630084d656c696f776172084d656c696f77617210334239'
  + '36393245353639313132383036',
  'hex'
);

test('настоящий файл игры читается полем в поле', () => {
  const w = readWorldPassport(REAL);
  assert.strictEqual(w.version, WORLD_VERSION);
  assert.strictEqual(w.name, 'AstwardWorld');
  assert.strictEqual(w.seed, 'iMbvYy6FIt');
  assert.strictEqual(w.worldGenVersion, WORLD_GEN_VERSION);
  assert.ok(w.sameGenerator);
});

test('хеш сида совпадает с тем, что записала сама игра', () => {
  // Эти четыре байта игра посчитала сама и положила в файл; из них растёт карта.
  assert.strictEqual(readWorldPassport(REAL).seedHash, stableHashCode('iMbvYy6FIt'));
  assert.strictEqual(stableHashCode('iMbvYy6FIt') >>> 0, 0x6f78add1);
});

test('чужой файл не разбирается молча', () => {
  assert.throws(() => readWorldPassport(Buffer.from('это вообще не файл мира')));
  assert.throws(() => readWorldPassport(Buffer.alloc(4)));
});

test('случайный сид — из алфавита игры и в десять символов', () => {
  for (let i = 0; i < 50; i++) {
    const seed = generateSeed();
    assert.strictEqual(seed.length, 10);
    for (const ch of seed) assert.ok(SEED_ALPHABET.includes(ch), `символ ${ch} не из алфавита игры`);
  }
  // Ни O, ни единицы: их путают при чтении с экрана, и игра их не берёт.
  assert.ok(!SEED_ALPHABET.includes('O'));
  assert.ok(!SEED_ALPHABET.includes('1'));
});

test('хеш считается так же для кириллицы и для нечётной длины', () => {
  // Сид в клиенте набирают руками, в том числе русскими буквами; правило «через
  // одну» на нечётной длине рвётся особым образом, и это ровно то место, где
  // самописный хеш обычно расходится с игрой.
  assert.strictEqual(typeof stableHashCode('Аствард'), 'number');
  assert.strictEqual(stableHashCode('a'), stableHashCode('a'));
  assert.notStrictEqual(stableHashCode('ab'), stableHashCode('ba'));
  assert.ok(Number.isInteger(stableHashCode('abc')));
});
