const { readJsonBody } = require('../util/body');
const { toPermittedListId } = require('../steamid');
const { findUserById } = require('../users');
const {
  requestWhitelist,
  listWhitelistRequests,
  decideWhitelist,
  listApprovedSteamIds,
} = require('../whitelist');

const VALID_DECISIONS = ['approved', 'rejected'];
const MAX_NOTE_LENGTH = 500;

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

  const user = await requestWhitelist(actor.id);
  if (!user) {
    return sendJson(res, 409, { error: 'Заявку сейчас не принять' });
  }
  sendJson(res, 200, { user });
}

async function getRequests(req, res) {
  const requests = await listWhitelistRequests();
  sendJson(res, 200, { requests });
}

async function decide(req, res, params) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  if (!VALID_DECISIONS.includes(body.status)) {
    return sendJson(res, 400, { error: 'Решение: approved или rejected' });
  }

  const id = Number(params.id);
  if (!Number.isInteger(id) || id < 1) {
    return sendJson(res, 400, { error: 'Некорректный идентификатор' });
  }

  let note = null;
  if (body.note !== undefined && body.note !== null && body.note !== '') {
    if (typeof body.note !== 'string' || body.note.length > MAX_NOTE_LENGTH) {
      return sendJson(res, 400, { error: `Комментарий — текст до ${MAX_NOTE_LENGTH} символов` });
    }
    note = body.note;
  }

  const user = await decideWhitelist({ userId: id, status: body.status, note, actorId: req.user.sub });
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

module.exports = { requestAccess, getRequests, decide, getPermittedList };
