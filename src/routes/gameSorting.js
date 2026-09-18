const logger = require('../logger');
const { sendJson, getJsonBody, logAction } = require('../utils');
const { db } = require('../../db');
const sorting = require('../gameSorting');

/**
 * «Сортировка» in the admin panel: which shelf each item belongs on.
 *
 * Admins and the superadmin alike, like «Постройки» next door: choosing that tar goes
 * with the materials is the everyday work of running a server, and the worst it can do
 * is send somebody looking in the wrong chest.
 *
 * The list of items is not this site's to invent - it comes from the running mod, which
 * is the only thing that knows what the game has. So there is no «add an item» here,
 * only a choice among what the server sent.
 */

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});

// Rank from the database and not from the session, as everywhere in the game routes:
// a demotion has to take effect at once.
async function actorOf(sessionUser) {
  if (!sessionUser) return null;
  return get('SELECT id, username, role FROM users WHERE id = ?', [sessionUser.id]);
}

const isAdmin = (actor) => actor && (actor.role === 'Admin' || actor.role === 'Superadmin');

module.exports = async function handleGameSorting(req, res, sessionUser, parsedUrl, method) {
  const actor = await actorOf(sessionUser);
  if (!actor) return sendJson(res, 401, { success: false, message: 'Не авторизован' });
  if (!isAdmin(actor)) return sendJson(res, 403, { success: false, message: 'Недостаточно прав' });

  try {
    if (parsedUrl.pathname === '/api/admin/sorting' && method === 'GET') {
      return sendJson(res, 200, { success: true, ...(await sorting.overview()) });
    }

    if (parsedUrl.pathname === '/api/admin/sorting/item' && method === 'PATCH') {
      let body;
      try {
        body = await getJsonBody(req);
      } catch (err) {
        return sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
      }

      const result = await sorting.setCategory(body.kind, body.category, actor.username);
      if (result.error) return sendJson(res, result.status, { success: false, message: result.error });

      logAction(actor, `Сортировка: «${result.title}» — ${result.said}`);
      return sendJson(res, 200, { success: true, revision: result.revision });
    }

    return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
  } catch (err) {
    logger.error('[sorting] админка:', err.message);
    return sendJson(res, 500, { success: false, message: 'Ошибка базы данных' });
  }
};
