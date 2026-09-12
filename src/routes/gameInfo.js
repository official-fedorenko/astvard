const path = require('node:path');
const { db } = require('../../db');
const logger = require('../logger');
const { sendJson } = require('../utils');
const {
  readRunes,
  readSharedTemplates,
  readMinutesPerRune,
  DEFAULT_MINUTES_PER_RUNE
} = require('../protocols/valheimMod');

// Где мод держит свои файлы. Только развёртывание знает этот путь, и в базе его
// нет намеренно: путь, выбранный через админку, — это файл на сервере, который
// сайту прикажет читать кто угодно с правами админа.
const MOD_CONFIG_DIR = process.env.VALHEIM_MOD_CONFIG || '/srv/valheim/server/BepInEx/config';

const RUNES_FILE = path.join(MOD_CONFIG_DIR, 'astvard-currency.txt');
const TEMPLATES_DIR = path.join(MOD_CONFIG_DIR, 'astvard-templates-shared');
const MOD_CFG_FILE = path.join(MOD_CONFIG_DIR, 'astvard.servermod.cfg');

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});

// Файл рун копится годами и не чистится, так что список номеров в нём —
// величина неизвестная. Один запрос с IN на все сразу однажды упрётся в предел
// параметров Postgres, поэтому спрашиваем пачками.
const NAME_LOOKUP_CHUNK = 500;

/**
 * Ник на сайте по номеру Steam. Номер наружу не уходит ни в каком виде: он нужен
 * только чтобы узнать своего игрока, а страница открыта всему интернету.
 */
async function namesBySteamId(steamIds) {
  const found = new Map();
  for (let i = 0; i < steamIds.length; i += NAME_LOOKUP_CHUNK) {
    const chunk = steamIds.slice(i, i + NAME_LOOKUP_CHUNK);
    if (!chunk.length) continue;
    const placeholders = chunk.map(() => '?').join(', ');
    const rows = await all(
      `SELECT steam_id, username FROM users WHERE steam_id IN (${placeholders})`,
      chunk
    );
    rows.forEach((r) => found.set(String(r.steam_id), r.username));
  }
  return found;
}

async function gameInfoBody(res) {
  const [purses, builds, minutesPerRune] = await Promise.all([
    readRunes(RUNES_FILE),
    readSharedTemplates(TEMPLATES_DIR),
    readMinutesPerRune(MOD_CFG_FILE)
  ]);

  const names = await namesBySteamId(purses.map((p) => p.steamId));

  // Наигранное время считается из рун: мод начисляет руну за каждые
  // MinutesPerRune минут в мире и хранит остаток часа. Счёт верен, пока ставку не
  // меняли — прошлые руны при её смене мод не пересчитывает, и вычислить это
  // задним числом неоткуда. Поэтому цифра честно называется «примерно».
  const players = purses.map((p) => {
    const seconds = p.runes * minutesPerRune * 60 + p.seconds;
    const known = names.has(p.steamId);
    return {
      // Имя показывается только своим: ник на сайте человек дал нам сам. Имя
      // персонажа чужого игрока видно на сервере, но это не повод писать его на
      // странице, открытой всему интернету, — так же решает и список серверов.
      name: known ? names.get(p.steamId) : 'Гость',
      runes: p.runes,
      hours: Math.round((seconds / 3600) * 10) / 10,
      known
    };
  }).sort((a, b) => b.runes - a.runes || b.hours - a.hours || a.name.localeCompare(b.name, 'ru'));

  sendJson(res, 200, {
    success: true,
    runes: {
      minutes_per_rune: minutesPerRune,
      default_minutes_per_rune: DEFAULT_MINUTES_PER_RUNE,
      players
    },
    builds: builds.map((b) => ({
      name: b.name,
      // Мод показывает такой шаблон в «Разном» — пусть и на сайте называется так же.
      category: b.category || 'Разное',
      author: b.author || null,
      pieces: b.pieces,
      for_players: b.forPlayers
    }))
  });
}

// Раздел «Наш сервер» переживёт отсутствие данных — страница это умеет, — но не
// переживёт запроса, который не ответил. Ошибку здесь ловим у себя, чтобы она
// стала пятисоткой, а не повисшим соединением.
async function gameInfo(req, res) {
  try {
    await gameInfoBody(res);
  } catch (err) {
    logger.error('[game] не собрать сведения о сервере:', err.message);
    sendJson(res, 500, { success: false, message: 'Сведения о сервере сейчас недоступны' });
  }
}

module.exports = async function handleGameInfo(req, res, sessionUser, parsedUrl, method) {
  // Открыто всем, как и список серверов: смотреть, чем живёт сервер, должно быть
  // можно до того, как заводишь аккаунт. Наружу идут только ники и числа.
  if (parsedUrl.pathname === '/api/public/game' && method === 'GET') return gameInfo(req, res);
  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
};
