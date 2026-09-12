const { db } = require('../../db');
const logger = require('../logger');
const { sendJson, getJsonBody, logAction } = require('../utils');
const { queryA2SInfo } = require('../protocols/a2s');
const { readValheimLogStatus } = require('../protocols/valheimLog');

// Only the deployment knows where the game server writes; the database keeps the
// decision to read a log at all, never the path to it — a path stored in a row
// would be a file for the site to read chosen by whoever can edit servers.
const VALHEIM_LOG_FILE = process.env.VALHEIM_LOG_FILE || '/srv/valheim/logs/server.log';

const PROBES = ['a2s', 'valheim-log'];

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
    `SELECT steam_id, username FROM users WHERE steam_id IN (${placeholders})`,
    steamIds
  );
  const byId = new Map(rows.map((r) => [String(r.steam_id), r.username]));
  return steamIds.map((id) => ({ steam_id: id, username: byId.get(String(id)) || null }));
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
  if (server.probe === 'valheim-log') return readValheimLogStatus(VALHEIM_LOG_FILE);
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
        // Только имена и только наших: по номеру из лога человек узнаётся, но на
        // открытой странице ему полагается ник, а не Steam ID.
        players_online: game.players.filter((p) => p.username).map((p) => p.username),
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
  if (!name || !host || !Number.isInteger(port) || port < 1 || port > 65535) {
    return sendJson(res, 400, { success: false, message: 'Нужны название, адрес и порт' });
  }
  if (!PROBES.includes(probe)) {
    return sendJson(res, 400, { success: false, message: 'Неизвестный способ проверки статуса' });
  }

  try {
    const inserted = await run(
      'INSERT INTO servers (name, host, port, probe) VALUES (?, ?, ?, ?)',
      [name, host, port, probe]
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

  const idMatch = pathname.match(/^\/api\/admin\/servers\/(\d+)$/);
  if (idMatch && method === 'DELETE') return remove(req, res, sessionUser, Number(idMatch[1]));

  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
}

module.exports = handleServers;
module.exports.refreshAllServers = refreshAllServers;
