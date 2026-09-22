const { db } = require('../../db');
const logger = require('../logger');
const { sendJson, getJsonBody, logAction } = require('../utils');
const { queryA2SInfo } = require('../protocols/a2s');
const { readValheimLogStatus } = require('../protocols/valheimLog');
const { planServer, PRESETS, MODIFIERS, MODIFIER_OPTIONS, SLOT_RE, DEFAULTS } = require('../gameServerPlan');
const { readWorldPassport, generateSeed } = require('../valheimWorldFile');

// Only the deployment knows where the game server writes; the database keeps the
// decision to read a log at all, never the path to it — a path stored in a row
// would be a file for the site to read chosen by whoever can edit servers.
const VALHEIM_LOG_FILE = process.env.VALHEIM_LOG_FILE || '/srv/valheim/logs/server.log';

const PROBES = ['a2s', 'valheim-log'];

// A second server on this machine lives in /srv/valheim-<slot>, so its log is where
// its slot says. The row still holds no path: the slot has to match SLOT_RE, and the
// rest of the path is written here - a path stored in a row would be a file for the
// site to read chosen by whoever can edit servers, which is the whole reason the
// column above never existed.
function logFileFor(server) {
  const slot = server && server.slot;
  if (!slot || !SLOT_RE.test(slot)) return VALHEIM_LOG_FILE;
  return `/srv/valheim-${slot}/logs/server.log`;
}

// Кто сейчас в игре, по номерам из лога, разложенным на людей нашей базы.
// Держится в памяти, а не в строке сервера: это снимок момента, живущий до
// следующего опроса (полминуты), и хранить его в базе значило бы возить туда-сюда
// то, что устаревает быстрее, чем читается. После перезапуска сайта список пуст
// ровно до первого опроса.
const onlineNow = new Map();

// Игру мы спрашиваем номерами, а показывать надо людей. Никого, кроме наших, в
// ответе нет: незнакомый номер остаётся числом и в публичный ответ не попадает —
// SteamID64 это личный идентификатор, и на открытой странице ему не место.
async function playersBySteamId(steamIds) {
  if (!steamIds || !steamIds.length) return [];
  const placeholders = steamIds.map(() => '?').join(', ');
  const rows = await all(
    `SELECT steam_id, username, avatar_url FROM users WHERE steam_id IN (${placeholders})`,
    steamIds
  );
  const byId = new Map(rows.map((r) => [String(r.steam_id), r]));
  return steamIds.map((id) => {
    const ours = byId.get(String(id));
    return {
      steam_id: id,
      username: ours ? ours.username : null,
      avatar_url: ours ? ours.avatar_url : null
    };
  });
}

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

function listServers() {
  return all('SELECT * FROM servers ORDER BY created_at DESC');
}

// A Valheim server started with -public 0 answers no A2S query at all: the game
// turns Steam's advertising on only for a public server. Its own log is what is
// left, and it is on this machine.
function probeServer(server) {
  if (server.probe === 'valheim-log') return readValheimLogStatus(logFileFor(server));
  return queryA2SInfo(server.host, server.port);
}

async function refreshServer(server) {
  const info = await probeServer(server);
  await run(
    `UPDATE servers
     SET is_online = ?, players = ?, max_players = ?, last_checked_at = CURRENT_TIMESTAMP
     WHERE id = ?`,
    [info.online ? 1 : 0, info.players ?? null, info.maxPlayers ?? null, server.id]
  );

  onlineNow.set(server.id, {
    players: info.online ? await playersBySteamId(info.steamIds) : [],
    saveNumber: info.saveNumber ?? null,
    gameVersion: info.gameVersion ?? null
  });
}

// Пустая заготовка, чтобы читающим не приходилось помнить про undefined у сервера,
// который ещё ни разу не опрашивали.
function gameInfo(id) {
  return onlineNow.get(id) || { players: [], saveNumber: null, gameVersion: null };
}

async function refreshAllServers() {
  const servers = await listServers();
  await Promise.all(servers.map((s) => refreshServer(s).catch((err) => {
    logger.error(`Не удалось опросить сервер ${s.name}:`, err.message);
  })));
}

// What a visitor sees: the address to type into "Подключиться по IP" and whether
// anyone is on. How the status is obtained is nobody's business but ours.
async function publicList(req, res) {
  const servers = await listServers();
  sendJson(res, 200, {
    success: true,
    servers: servers.map((s) => {
      const game = gameInfo(s.id);
      return {
        id: s.id,
        name: s.name,
        host: s.host,
        port: s.port,
        is_online: !!s.is_online,
        players: s.players,
        max_players: s.max_players,
        last_checked_at: s.last_checked_at,
        // Только наши и только ник с картинкой: по номеру из лога человек
        // узнаётся, но на открытой странице ему полагается ник, а не Steam ID.
        players_online: game.players
          .filter((p) => p.username)
          .map((p) => ({ name: p.username, avatar: p.avatar_url || null })),
        save_number: game.saveNumber,
        game_version: game.gameVersion
      };
    })
  });
}

async function adminList(req, res) {
  const servers = await listServers();
  sendJson(res, 200, {
    success: true,
    // Админу — то же самое, но с номерами: незнакомый номер в списке онлайна это
    // ровно тот, кого стоит завести в вайтлисте, и по нему он и заводится.
    servers: servers.map((s) => ({ ...s, game: gameInfo(s.id) }))
  });
}

async function add(req, res, actor) {
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  const { name, host } = body;
  const port = Number(body.port);
  const probe = body.probe || 'a2s';
  const slot = String(body.slot || '').trim() || null;
  if (!name || !host || !Number.isInteger(port) || port < 1 || port > 65535) {
    return sendJson(res, 400, { success: false, message: 'Нужны название, адрес и порт' });
  }
  if (!PROBES.includes(probe)) {
    return sendJson(res, 400, { success: false, message: 'Неизвестный способ проверки статуса' });
  }
  if (slot && !SLOT_RE.test(slot)) {
    return sendJson(res, 400, { success: false, message: 'Ключ сервера: латиница в нижнем регистре, цифры и дефис' });
  }

  try {
    const inserted = await run(
      'INSERT INTO servers (name, host, port, probe, slot) VALUES (?, ?, ?, ?, ?)',
      [name, host, port, probe, slot]
    );
    const servers = await all('SELECT * FROM servers WHERE id = ?', [inserted.lastID]);
    await refreshServer(servers[0]).catch(() => {});
    logAction(actor.username, `Добавил сервер: ${name}`);
    sendJson(res, 201, { success: true });
  } catch (err) {
    if (String(err.message).includes('UNIQUE')) {
      return sendJson(res, 409, { success: false, message: 'Такой адрес и порт уже есть' });
    }
    throw err;
  }
}

// The site never starts anything: it works out what a new server would need and
// hands the owner the files and the commands. The answer depends on nothing but the
// request, so nothing is stored here either - a plan is worth exactly as much as the
// moment it was asked for.
async function plan(req, res, actor) {
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  const result = planServer(body);
  // Only a saved plan is worth a line in the journal; asking is just looking, and a
  // form that reports on every keystroke would bury everything else in there.
  if (result.ok && body.remember === true) {
    logAction(actor.username, `Собрал план сервера: ${result.slot}`);
  }
  sendJson(res, 200, { success: true, plan: result });
}

// What the form has to offer, straight from the game's own enums: a list written out
// in the page would go stale the day the game adds a modifier.
async function planOptions(req, res) {
  sendJson(res, 200, {
    success: true,
    presets: PRESETS,
    modifiers: MODIFIERS,
    modifierOptions: MODIFIER_OPTIONS,
    defaults: DEFAULTS
  });
}

// Сид, придуманный по правилам игры: те же десять знаков и тот же алфавит, что у
// World.GenerateSeed. Придумывает его сайт, а не сервер, ровно затем, чтобы он был
// записан: мир, сид которого никто не видел, не повторить.
async function planSeed(req, res) {
  sendJson(res, 200, { success: true, seed: generateSeed() });
}

// Свой файл мира с компьютера: сайт читает из него сид и имя. Дальше человек решает
// сам — взять этот сид новому миру или везти всю папку целиком.
async function worldRead(req, res) {
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  // Паспорт мира — это сотня байт. Всё, что больше, это уже не он, а, скорее всего,
  // сама карта, и разбирать её здесь нечем и незачем.
  const raw = Buffer.from(String(body.base64 || ''), 'base64');
  if (!raw.length || raw.length > 64 * 1024) {
    return sendJson(res, 400, { success: false, message: 'Нужен файл .fwl2 или .fwl — он маленький, рядом с ним лежит сама карта' });
  }
  try {
    sendJson(res, 200, { success: true, world: readWorldPassport(raw) });
  } catch (err) {
    sendJson(res, 400, { success: false, message: `Не читается как файл мира: ${err.message}` });
  }
}

async function remove(req, res, actor, id) {
  const result = await run('DELETE FROM servers WHERE id = ?', [id]);
  if (!result.changes) return sendJson(res, 404, { success: false, message: 'Сервер не найден' });
  logAction(actor.username, `Удалил сервер id ${id}`);
  sendJson(res, 200, { success: true });
}

async function refreshNow(req, res) {
  await refreshAllServers();
  sendJson(res, 200, { success: true, servers: await listServers() });
}

async function handleServers(req, res, sessionUser, parsedUrl, method) {
  const pathname = parsedUrl.pathname;

  // The list is public on purpose: deciding whether to come back should not need
  // an account.
  if (pathname === '/api/public/servers' && method === 'GET') return publicList(req, res);

  const role = sessionUser && sessionUser.role;
  if (role !== 'Admin' && role !== 'Superadmin') {
    return sendJson(res, 403, { success: false, message: 'Недостаточно прав' });
  }

  if (pathname === '/api/admin/servers' && method === 'GET') return adminList(req, res);
  if (pathname === '/api/admin/servers' && method === 'POST') return add(req, res, sessionUser);
  if (pathname === '/api/admin/servers/refresh' && method === 'POST') return refreshNow(req, res);
  if (pathname === '/api/admin/servers/plan/options' && method === 'GET') return planOptions(req, res);

  // Planning a server is a superadmin's business: what comes out of it is the
  // command line of a machine, and the same hand that may hand out admin in the
  // game is the one that may do this.
  if (pathname === '/api/admin/servers/plan' && method === 'POST') {
    if (role !== 'Superadmin') {
      return sendJson(res, 403, { success: false, message: 'Создавать серверы может только суперадмин' });
    }
    return plan(req, res, sessionUser);
  }
  if (pathname === '/api/admin/servers/plan/seed' && method === 'POST') {
    if (role !== 'Superadmin') {
      return sendJson(res, 403, { success: false, message: 'Создавать серверы может только суперадмин' });
    }
    return planSeed(req, res);
  }
  if (pathname === '/api/admin/servers/plan/world/read' && method === 'POST') {
    if (role !== 'Superadmin') {
      return sendJson(res, 403, { success: false, message: 'Создавать серверы может только суперадмин' });
    }
    return worldRead(req, res);
  }

  const idMatch = pathname.match(/^\/api\/admin\/servers\/(\d+)$/);
  if (idMatch && method === 'DELETE') return remove(req, res, sessionUser, Number(idMatch[1]));

  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
}

module.exports = handleServers;
module.exports.refreshAllServers = refreshAllServers;
