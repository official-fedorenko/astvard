const { readJsonBody } = require('../util/body');
const { parseSteamId64, toPermittedListId, toAdminListIds } = require('../steamid');
const { fetchPersonaName, fallbackNickname } = require('../steam-profile');
const { findUserById, findUserBySteamId, createSteamUser } = require('../users');
const {
  requestWhitelist,
  grantWhitelist,
  revokeWhitelist,
  listWhitelistPeople,
  decideWhitelist,
  listApprovedSteamIds,
  setServerAdmin,
  listServerAdmins,
} = require('../whitelist');

// Three things an admin can do to a row: let in, refuse a request, take access
// back. 'none' is the last one — see revokeWhitelist for why it is not 'rejected'.
const VALID_DECISIONS = ['approved', 'rejected', 'none'];
const MAX_NOTE_LENGTH = 500;

function readNote(value, field) {
  if (value === undefined || value === null || value === '') return { note: null };
  if (typeof value !== 'string' || value.length > MAX_NOTE_LENGTH) {
    return { error: `${field} — текст до ${MAX_NOTE_LENGTH} символов` };
  }
  return { note: value };
}

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

async function requestAccess(req, res) {
  if (!req.user) {
    return sendJson(res, 401, { error: 'Не авторизован' });
  }

  // Read the row rather than the token: the Steam id is what this request is
  // about, and the token was signed before it was ever bound.
  const actor = await findUserById(req.user.sub);
  if (!actor) {
    return sendJson(res, 401, { error: 'Не авторизован' });
  }
  if (!actor.steam_id) {
    return sendJson(res, 400, { error: 'Сначала войди через Steam — номер должен подтвердить сам Steam' });
  }
  if (actor.whitelist_status === 'approved') {
    return sendJson(res, 409, { error: 'Доступ уже открыт' });
  }

  // The body is optional here: the button works on its own, and a player who wants
  // to say who they are can.
  let body = {};
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  const requestNote = readNote(body.note, 'Сообщение');
  if (requestNote.error) {
    return sendJson(res, 400, { error: requestNote.error });
  }

  const user = await requestWhitelist(actor.id, requestNote.note);
  if (!user) {
    return sendJson(res, 409, { error: 'Заявку сейчас не принять' });
  }
  sendJson(res, 200, { user });
}

// An admin adding someone who never came to the site. This is the one place a
// SteamID64 is still typed by hand, and the answer carries the Steam nickname back
// so a typo shows up as a stranger's name rather than as silence.
async function postEntry(req, res) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }

  const parsed = parseSteamId64(body.steam_id);
  if (parsed.error) {
    return sendJson(res, 400, { error: parsed.error });
  }
  const steamId = parsed.id;

  let user = await findUserBySteamId(steamId);
  if (!user) {
    const persona = await fetchPersonaName(steamId);
    user = await createSteamUser({
      nickname: persona || fallbackNickname(steamId),
      steamId,
      // Typed in by an admin, not signed for by Steam. It flips the first time the
      // owner signs in through Steam.
      verified: false,
    });
    if (!user) {
      return sendJson(res, 409, { error: 'Не удалось создать запись, попробуй ещё раз' });
    }
  }

  const granted = await grantWhitelist({ userId: user.id, actorId: req.user.sub });
  if (!granted) {
    return sendJson(res, 409, { error: 'Не удалось выдать доступ' });
  }
  sendJson(res, 201, { user: granted });
}

// Rights inside the game: spawning, banning, kicking. Whoever can hand them out
// can hand them to themselves, so this one is superadmin-only even though a plain
// site admin may already answer whitelist requests.
async function patchServerAdmin(req, res, params) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  if (typeof body.server_admin !== 'boolean') {
    return sendJson(res, 400, { error: 'server_admin: true или false' });
  }

  const id = Number(params.id);
  if (!Number.isInteger(id) || id < 1) {
    return sendJson(res, 400, { error: 'Некорректный идентификатор' });
  }

  const user = await setServerAdmin(id, body.server_admin);
  if (!user) {
    return sendJson(res, 404, { error: 'Игрок не найден или у него не привязан Steam' });
  }
  sendJson(res, 200, { user });
}

// Unlike permittedlist.txt, an empty adminlist.txt is harmless — it means nobody is
// an admin, not that everybody is — so a list with no entries is written out rather
// than refused. Taking rights away has to be possible.
async function getAdminList(req, res) {
  const admins = await listServerAdmins();
  const lines = admins.flatMap((a) => toAdminListIds(a.steam_id));
  const text = lines.length ? `${lines.join('\n')}\n` : '';
  res.writeHead(200, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Disposition': 'attachment; filename="adminlist.txt"',
  });
  res.end(text);
}

async function getRequests(req, res) {
  const people = await listWhitelistPeople();
  sendJson(res, 200, { people });
}

async function decide(req, res, params) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  if (!VALID_DECISIONS.includes(body.status)) {
    return sendJson(res, 400, { error: 'Решение: approved, rejected или none' });
  }

  const id = Number(params.id);
  if (!Number.isInteger(id) || id < 1) {
    return sendJson(res, 400, { error: 'Некорректный идентификатор' });
  }

  const decided = readNote(body.note, 'Комментарий');
  if (decided.error) {
    return sendJson(res, 400, { error: decided.error });
  }
  const note = decided.note;

  // Letting someone in goes through grantWhitelist, which asks only for a steam_id:
  // an admin hands access to a player who never applied as often as he answers an
  // application, and decideWhitelist refuses a row that never asked on purpose.
  // Refusing stays with decideWhitelist and keeps that guard — there is nothing to
  // refuse where nothing was asked.
  let user;
  if (body.status === 'approved') {
    user = await grantWhitelist({ userId: id, actorId: req.user.sub });
  } else if (body.status === 'none') {
    user = await revokeWhitelist({ userId: id, actorId: req.user.sub });
  } else {
    user = await decideWhitelist({ userId: id, status: body.status, note, actorId: req.user.sub });
  }
  if (!user) {
    return sendJson(res, 404, { error: 'Заявка не найдена' });
  }
  sendJson(res, 200, { user });
}

async function getPermittedList(req, res) {
  const ids = await listApprovedSteamIds();

  // An empty permittedlist.txt does not mean "nobody in": the server reads it as
  // "let everyone in", and the list only starts applying from its first entry. A
  // file with no entries would therefore hand the server to the whole internet,
  // which is the opposite of what whoever pressed the button wanted. Refuse.
  if (ids.length === 0) {
    return sendJson(res, 409, {
      error: 'Ни одной одобренной заявки. Пустой permittedlist.txt открывает сервер для всех, поэтому файл не выгружается.',
    });
  }

  // Entries only, no header comment. A commented line almost certainly does not
  // count as an entry — the file the game writes itself starts with comments and
  // still lets everyone in — but that has not been checked against 1.0 here, and
  // being wrong about it opens the server.
  const text = ids.map(toPermittedListId).join('\n') + '\n';
  res.writeHead(200, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Disposition': 'attachment; filename="permittedlist.txt"',
  });
  res.end(text);
}

module.exports = { requestAccess, postEntry, getRequests, decide, patchServerAdmin, getPermittedList, getAdminList };
