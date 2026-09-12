const { db } = require('../../db');
const { sendJson, getJsonBody, logAction } = require('../utils');
const { parseSteamId64, toPermittedListId, toAdminListIds } = require('../steamId');
const { fetchPersonaName, fallbackNickname } = require('../steamProfile');

const MAX_NOTE_LENGTH = 500;

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});
const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

function readNote(value, field) {
  if (value === undefined || value === null || value === '') return { note: null };
  if (typeof value !== 'string' || value.length > MAX_NOTE_LENGTH) {
    return { error: `${field} — текст до ${MAX_NOTE_LENGTH} символов` };
  }
  return { note: value };
}

function sendText(res, filename, text) {
  res.writeHead(200, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Disposition': `attachment; filename="${filename}"`
  });
  res.end(text);
}

// The rank is read from the database rather than from the session: a session keeps
// the role it was created with, and a demotion has to take effect at once.
async function actorOf(sessionUser) {
  if (!sessionUser) return null;
  return get('SELECT id, username, role FROM users WHERE id = ?', [sessionUser.id]);
}

const isAdmin = (actor) => actor && (actor.role === 'Admin' || actor.role === 'Superadmin');

function grant(userId, actorId) {
  return run(
    `UPDATE users
     SET whitelist_status = 'approved', whitelist_decided_at = CURRENT_TIMESTAMP,
         whitelist_decided_by = ?, whitelist_note = NULL
     WHERE id = ? AND steam_id IS NOT NULL`,
    [actorId, userId]
  );
}

// A player asking for access. The body is optional — the button works on its own,
// and a couple of words about who invited them is most of what an admin needs.
async function request(req, res, sessionUser) {
  const actor = sessionUser && await get('SELECT * FROM users WHERE id = ?', [sessionUser.id]);
  if (!actor) return sendJson(res, 401, { success: false, message: 'Не авторизован' });
  if (!actor.steam_id) {
    return sendJson(res, 400, { success: false, message: 'Сначала войди через Steam — номер должен подтвердить сам Steam' });
  }
  if (actor.whitelist_status === 'approved') {
    return sendJson(res, 409, { success: false, message: 'Доступ уже открыт' });
  }

  let body = {};
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  const note = readNote(body.note, 'Сообщение');
  if (note.error) return sendJson(res, 400, { success: false, message: note.error });

  await run(
    `UPDATE users
     SET whitelist_status = 'pending', whitelist_requested_at = CURRENT_TIMESTAMP,
         whitelist_request_note = ?, whitelist_note = NULL
     WHERE id = ?`,
    [note.note, actor.id]
  );
  logAction(actor.username, 'Запросил доступ на сервер');
  sendJson(res, 200, { success: true });
}

// Everyone who linked Steam, not only those who asked: an admin hands access to a
// player who never pressed the button as often as he answers an application, and a
// row missing from this list could only be reached by typing its number in again.
async function list(req, res) {
  const people = await all(
    `SELECT id, username, role, steam_id, steam_id_verified, whitelist_status,
            whitelist_requested_at, whitelist_decided_at, whitelist_note,
            whitelist_request_note, server_admin
     FROM users
     WHERE steam_id IS NOT NULL
     ORDER BY (whitelist_status = 'pending') DESC,
              (whitelist_status = 'approved') DESC,
              coalesce(whitelist_requested_at, whitelist_decided_at) DESC,
              username`
  );
  sendJson(res, 200, { success: true, people });
}

// An admin adding someone who never came to the site. The one place a SteamID64 is
// still typed by hand, so the answer carries the Steam nickname back: a typo shows
// up as a stranger's name rather than as silence.
async function add(req, res, actor) {
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  const parsed = parseSteamId64(body.steam_id);
  if (parsed.error) return sendJson(res, 400, { success: false, message: parsed.error });

  let user = await get('SELECT * FROM users WHERE steam_id = ?', [parsed.id]);
  if (!user) {
    const persona = await fetchPersonaName(parsed.id);
    const base = persona || fallbackNickname(parsed.id);
    for (let attempt = 1; attempt <= 10 && !user; attempt += 1) {
      const username = attempt === 1 ? base : `${base} (${attempt})`;
      try {
        // steam_id_verified stays 0: a number typed by a person is a guess until its
        // owner signs in through Steam, and the tables say which is which.
        const inserted = await run(
          `INSERT INTO users (username, role, steam_id, steam_id_verified)
           VALUES (?, 'User', ?, 0)`,
          [username, parsed.id]
        );
        user = await get('SELECT * FROM users WHERE id = ?', [inserted.lastID]);
      } catch (err) {
        if (!String(err.message).includes('users.username')) throw err;
      }
    }
    if (!user) return sendJson(res, 409, { success: false, message: 'Не удалось создать запись' });
  }

  await grant(user.id, actor.id);
  logAction(actor.username, `Выдал доступ: ${user.username}`);
  sendJson(res, 201, { success: true, user: await get('SELECT * FROM users WHERE id = ?', [user.id]) });
}

// Letting someone in asks only for a steam_id, because an admin grants access to
// players who never applied. Refusing answers an application, so it refuses to
// decide for a row that never asked. Taking access back returns the row to where it
// started — and takes in-game admin with it: an admin who cannot enter the server is
// a right waiting to be forgotten, and an empty permittedlist.txt lets everyone in.
async function decide(req, res, actor, id) {
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  const status = body.status;
  if (!['approved', 'rejected', 'none'].includes(status)) {
    return sendJson(res, 400, { success: false, message: 'Решение: approved, rejected или none' });
  }
  const note = readNote(body.note, 'Комментарий');
  if (note.error) return sendJson(res, 400, { success: false, message: note.error });

  let result;
  if (status === 'approved') {
    result = await grant(id, actor.id);
  } else if (status === 'none') {
    result = await run(
      `UPDATE users
       SET whitelist_status = 'none', whitelist_note = NULL, server_admin = 0,
           whitelist_decided_at = CURRENT_TIMESTAMP, whitelist_decided_by = ?
       WHERE id = ? AND steam_id IS NOT NULL`,
      [actor.id, id]
    );
  } else {
    result = await run(
      `UPDATE users
       SET whitelist_status = 'rejected', whitelist_note = ?,
           whitelist_decided_at = CURRENT_TIMESTAMP, whitelist_decided_by = ?
       WHERE id = ? AND whitelist_status <> 'none' AND steam_id IS NOT NULL`,
      [note.note, actor.id, id]
    );
  }
  if (!result.changes) return sendJson(res, 404, { success: false, message: 'Заявка не найдена' });

  const user = await get('SELECT * FROM users WHERE id = ?', [id]);
  logAction(actor.username, `Вайтлист ${status}: ${user ? user.username : id}`);
  sendJson(res, 200, { success: true, user });
}

// Rights inside the game: spawning, banning, kicking. Whoever hands them out can
// hand them to himself, so this one is Superadmin-only even though a plain admin
// already answers whitelist requests.
async function setServerAdmin(req, res, actor, id) {
  if (actor.role !== 'Superadmin') {
    return sendJson(res, 403, { success: false, message: 'Админку в игре выдаёт только Superadmin' });
  }
  let body;
  try {
    body = await getJsonBody(req);
  } catch {
    return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
  }
  if (typeof body.server_admin !== 'boolean') {
    return sendJson(res, 400, { success: false, message: 'server_admin: true или false' });
  }
  const result = await run(
    'UPDATE users SET server_admin = ? WHERE id = ? AND steam_id IS NOT NULL',
    [body.server_admin ? 1 : 0, id]
  );
  if (!result.changes) {
    return sendJson(res, 404, { success: false, message: 'Игрок не найден или у него не привязан Steam' });
  }
  logAction(actor.username, `Админка в игре ${body.server_admin ? 'выдана' : 'снята'}: id ${id}`);
  sendJson(res, 200, { success: true });
}

// An empty permittedlist.txt does not mean "nobody in": the server reads it as "let
// everyone in", and the list only starts applying from its first entry. A file with
// no entries would hand the server to the whole internet, which is the opposite of
// what whoever pressed the button wanted. Refuse.
async function permittedList(req, res) {
  const rows = await all(
    `SELECT steam_id FROM users
     WHERE whitelist_status = 'approved' AND steam_id IS NOT NULL
     ORDER BY whitelist_decided_at`
  );
  if (rows.length === 0) {
    return sendJson(res, 409, {
      success: false,
      message: 'Ни одной одобренной заявки. Пустой permittedlist.txt открывает сервер для всех, поэтому файл не выгружается.'
    });
  }
  sendText(res, 'permittedlist.txt', `${rows.map((r) => toPermittedListId(r.steam_id)).join('\n')}\n`);
}

// Unlike the permitted list, an empty adminlist.txt is harmless — it means nobody is
// an admin, not that everybody is — so it is written out rather than refused. Taking
// rights away has to be possible. Both forms of every id go in: ZNet.PlayerIsAdmin
// compares raw and only ever matches the form the client sent.
async function adminList(req, res) {
  const rows = await all(
    'SELECT steam_id FROM users WHERE server_admin = 1 AND steam_id IS NOT NULL ORDER BY username'
  );
  const lines = rows.flatMap((r) => toAdminListIds(r.steam_id));
  sendText(res, 'adminlist.txt', lines.length ? `${lines.join('\n')}\n` : '');
}

module.exports = async function handleWhitelist(req, res, sessionUser, parsedUrl, method) {
  const pathname = parsedUrl.pathname;

  if (pathname === '/api/cabinet/whitelist/request' && method === 'POST') {
    return request(req, res, sessionUser);
  }

  const actor = await actorOf(sessionUser);
  if (!isAdmin(actor)) {
    return sendJson(res, 403, { success: false, message: 'Недостаточно прав' });
  }

  if (pathname === '/api/admin/whitelist' && method === 'GET') return list(req, res);
  if (pathname === '/api/admin/whitelist' && method === 'POST') return add(req, res, actor);
  if (pathname === '/api/admin/whitelist/permittedlist' && method === 'GET') return permittedList(req, res);
  if (pathname === '/api/admin/whitelist/adminlist' && method === 'GET') return adminList(req, res);

  const serverAdminMatch = pathname.match(/^\/api\/admin\/whitelist\/(\d+)\/server-admin$/);
  if (serverAdminMatch && method === 'PATCH') {
    return setServerAdmin(req, res, actor, Number(serverAdminMatch[1]));
  }
  const decideMatch = pathname.match(/^\/api\/admin\/whitelist\/(\d+)$/);
  if (decideMatch && method === 'PATCH') {
    return decide(req, res, actor, Number(decideMatch[1]));
  }

  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
};
