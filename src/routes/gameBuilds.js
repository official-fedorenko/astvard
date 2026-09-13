const path = require('node:path');
const { db } = require('../../db');
const logger = require('../logger');
const { sendJson, getJsonBody, logAction } = require('../utils');
const builds = require('../gameBuilds');
const { readSharedTemplates } = require('../protocols/valheimMod');

/**
 * «Постройки» in the admin panel: the rules the mod applies to players and who may
 * build each shared template. The state itself lives in src/gameBuilds.js; this is
 * who may change it and how the answer is shaped.
 *
 * Admins and the superadmin alike: none of this makes anyone an admin in game. What a
 * player may build is the everyday work of running the server, and the switches that
 * could do damage - demolition, say - are the same switches the in-game panel gives
 * every server admin.
 */

const MOD_CONFIG_DIR = process.env.VALHEIM_MOD_CONFIG || '/srv/valheim/server/BepInEx/config';
const TEMPLATES_DIR = path.join(MOD_CONFIG_DIR, 'astvard-templates-shared');

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});

// The rank is read from the database rather than from the session, as everywhere in
// the game routes: a demotion has to take effect at once.
async function actorOf(sessionUser) {
  if (!sessionUser) return null;
  return get('SELECT id, username, role FROM users WHERE id = ?', [sessionUser.id]);
}

const isAdmin = (actor) => actor && (actor.role === 'Admin' || actor.role === 'Superadmin');

// Player-sent templates are listed too: an admin may well want to open one that a
// player sent in. The public page still leaves them out.
const templateFiles = () => readSharedTemplates(TEMPLATES_DIR, { includeSubmitted: true });

async function readBody(req, res) {
  try {
    return await getJsonBody(req);
  } catch (err) {
    sendJson(res, 400, { success: false, message: 'Некорректный запрос' });
    return null;
  }
}

module.exports = async function handleGameBuilds(req, res, sessionUser, parsedUrl, method) {
  const actor = await actorOf(sessionUser);
  if (!actor) return sendJson(res, 401, { success: false, message: 'Не авторизован' });
  if (!isAdmin(actor)) return sendJson(res, 403, { success: false, message: 'Недостаточно прав' });

  const pathname = parsedUrl.pathname;
  try {
    if (pathname === '/api/admin/builds' && method === 'GET') {
      return sendJson(res, 200, { success: true, ...(await builds.overview(await templateFiles())) });
    }

    const ruleMatch = pathname.match(/^\/api\/admin\/builds\/rules\/([a-z]{1,24})$/);
    if (ruleMatch && method === 'PATCH') {
      const body = await readBody(req, res);
      if (!body) return undefined;
      const result = await builds.setRule(ruleMatch[1], body, actor.username);
      if (result.error) return sendJson(res, result.status, { success: false, message: result.error });
      logAction(actor, `Постройки: «${result.title}» — ${result.said}`);
      return sendJson(res, 200, { success: true, revision: result.revision });
    }

    if (pathname === '/api/admin/builds/template' && method === 'PATCH') {
      const body = await readBody(req, res);
      if (!body) return undefined;
      const result = await builds.setTemplate(body, await templateFiles(), actor.username);
      if (result.error) return sendJson(res, result.status, { success: false, message: result.error });
      logAction(actor, `Постройки: «${result.name}» — всем: ${result.forAll ? 'да' : 'нет'}, выбранным: ${result.count}`);
      return sendJson(res, 200, { success: true, revision: result.revision });
    }

    return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
  } catch (err) {
    logger.error('[builds] админка:', err.message);
    return sendJson(res, 500, { success: false, message: 'Ошибка базы данных' });
  }
};
