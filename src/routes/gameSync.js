const crypto = require('node:crypto');
const logger = require('../logger');
const { permittedLines, adminLines } = require('../gameLists');
const builds = require('../gameBuilds');

/**
 * Списки доступа для самого игрового сервера: мод забирает их отсюда по токену.
 *
 * Сайт и так пишет оба файла в папку сохранений, когда стоит на одной машине с
 * игрой. Этот маршрут — для случая, когда не стоит, и заодно страховка на случай,
 * когда запись файла сорвалась: мод раз в минуту спрашивает, что должно лежать в
 * списках, и сам кладёт это рядом с игрой.
 *
 * Ответ — простой текст с двумя секциями, а не JSON: разбирать его будет C# без
 * сторонних библиотек, и строки в нём ровно в том виде, в каком их читает игра.
 *
 * Here too, by the same token: what players may build (src/gameBuilds.js). The mod
 * pulls it every few seconds and pushes back what an admin changed in game.
 */

// Токен знает только развёртывание — здесь и в конфиге мода. Нет токена — маршрута
// нет вовсе: пустой токен совпал бы с пустым заголовком.
function expectedToken() {
  return process.env.GAME_LISTS_TOKEN || '';
}

// Сравнение за постоянное время: по времени ответа токен не подбирается буква за
// буквой.
function tokenMatches(given) {
  const expected = expectedToken();
  if (!expected || typeof given !== 'string') return false;
  const a = Buffer.from(given);
  const b = Buffer.from(expected);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

function authorized(req) {
  const header = req.headers.authorization || '';
  const given = header.startsWith('Bearer ') ? header.slice('Bearer '.length).trim() : '';
  return tokenMatches(given);
}

function sendPlain(res, status, text) {
  res.writeHead(status, { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(text);
}

// The mod sends all its rules and every template's access at once when it seeds; a
// quarter of a megabyte is a hundred times that.
const MAX_PUSH_BYTES = 256 * 1024;

// Chunks are joined as bytes and decoded once: a Cyrillic letter split across two
// chunks would otherwise turn into two replacement characters in a template's name.
function readText(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    let tooBig = false;
    req.on('data', (chunk) => {
      if (tooBig) return;
      size += chunk.length;
      if (size > MAX_PUSH_BYTES) {
        tooBig = true;
        const err = new Error('too large');
        err.tooLarge = true;
        reject(err);
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => { if (!tooBig) resolve(Buffer.concat(chunks).toString('utf8')); });
    req.on('error', (err) => { if (!tooBig) reject(err); });
  });
}

async function lists(req, res) {
  try {
    const [permitted, admins] = await Promise.all([permittedLines(), adminLines()]);
    sendPlain(res, 200, ['[permitted]', ...permitted, '[admins]', ...admins, ''].join('\n'));
  } catch (err) {
    logger.error('[lists] не собрать списки для мода:', err.message);
    sendPlain(res, 500, 'error\n');
  }
}

async function buildsPull(res, parsedUrl) {
  try {
    sendPlain(res, 200, await builds.pullText(parsedUrl.searchParams.get('rev')));
  } catch (err) {
    logger.error('[builds] не собрать состояние для мода:', err.message);
    sendPlain(res, 500, 'error\n');
  }
}

async function buildsPush(req, res) {
  let text;
  try {
    text = await readText(req);
  } catch (err) {
    return sendPlain(res, err.tooLarge ? 413 : 400, 'bad body\n');
  }
  try {
    const result = await builds.pushFromMod(text);
    return sendPlain(res, result.status, result.text);
  } catch (err) {
    logger.error('[builds] не принять присланное модом:', err.message);
    return sendPlain(res, 500, 'error\n');
  }
}

module.exports = async function handleGameSync(req, res, sessionUser, parsedUrl, method) {
  if (!expectedToken()) return sendPlain(res, 404, 'not found\n');

  const isLists = parsedUrl.pathname === '/api/game/lists' && method === 'GET';
  const isBuilds = parsedUrl.pathname === '/api/game/builds' && (method === 'GET' || method === 'POST');
  if (!isLists && !isBuilds) return sendPlain(res, 404, 'not found\n');
  if (!authorized(req)) return sendPlain(res, 401, 'unauthorized\n');

  if (isLists) return lists(req, res);
  return method === 'GET' ? buildsPull(res, parsedUrl) : buildsPush(req, res);
};
