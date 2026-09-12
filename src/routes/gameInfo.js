const path = require('node:path');
const { db } = require('../../db');
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

/**
 * Ник на сайте по номеру Steam. Игрок, которого у нас нет, остаётся под именем
 * своего персонажа — оно и так видно всем на сервере, а вот номер наружу не
 * уходит ни в каком виде: страница открыта всему интернету.
 */
async function namesBySteamId(steamIds) {
  if (!steamIds.length) return new Map();
  const placeholders = steamIds.map(() => '?').join(', ');
  const rows = await all(
    `SELECT steam_id, username FROM users WHERE steam_id IN (${placeholders})`,
    steamIds
  );
  return new Map(rows.map((r) => [String(r.steam_id), r.username]));
}

async function gameInfo(req, res) {
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
    return {
      name: names.get(p.steamId) || p.character || 'Викинг',
      character: p.character || null,
      runes: p.runes,
      hours: Math.round((seconds / 3600) * 10) / 10,
      // Свой ли это человек на сайте — видно по тому, нашлось ли имя. Полезно
      // для строки «этого игрока у нас нет», а номер для этого не нужен.
      known: names.has(p.steamId)
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
      category: b.category || 'Без категории',
      author: b.author || null,
      pieces: b.pieces,
      for_players: b.forPlayers
    }))
  });
}

module.exports = async function handleGameInfo(req, res, sessionUser, parsedUrl, method) {
  // Открыто всем, как и список серверов: смотреть, чем живёт сервер, должно быть
  // можно до того, как заводишь аккаунт. Наружу идут только ники и числа.
  if (parsedUrl.pathname === '/api/public/game' && method === 'GET') return gameInfo(req, res);
  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
};
