const crypto = require('node:crypto');
const logger = require('../logger');
const { permittedLines, adminLines } = require('../gameLists');

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

function sendPlain(res, status, text) {
  res.writeHead(status, { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(text);
}

async function lists(req, res) {
  const header = req.headers.authorization || '';
  const given = header.startsWith('Bearer ') ? header.slice('Bearer '.length).trim() : '';
  if (!tokenMatches(given)) return sendPlain(res, 401, 'unauthorized\n');

  try {
    const [permitted, admins] = await Promise.all([permittedLines(), adminLines()]);
    sendPlain(res, 200, ['[permitted]', ...permitted, '[admins]', ...admins, ''].join('\n'));
  } catch (err) {
    logger.error('[lists] не собрать списки для мода:', err.message);
    sendPlain(res, 500, 'error\n');
  }
}

module.exports = async function handleGameSync(req, res, sessionUser, parsedUrl, method) {
  if (!expectedToken()) return sendPlain(res, 404, 'not found\n');
  if (parsedUrl.pathname === '/api/game/lists' && method === 'GET') return lists(req, res);
  return sendPlain(res, 404, 'not found\n');
};
