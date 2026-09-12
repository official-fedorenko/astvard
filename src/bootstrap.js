const { db } = require('../db');
const logger = require('./logger');
const { parseSteamId64 } = require('./steamId');
const { fetchPersonaName, fallbackNickname } = require('./steamProfile');

// A fresh install of the panel seeds superadmin/admin/user with a password that is
// printed in its own README. For a site on the open internet that is not a way in,
// it is a hole with a sign on it. Astvard's way in is the owner's Steam account:
// SUPERADMIN_STEAM_ID in .env, and db.js skips the demo accounts when it is set.
//
// A Steam number rather than a password, because that is how the site signs people
// in and because a password in .env is a password lying on disk.
//
// It runs at every start and only does something while the site has no Superadmin.
// A restart must never undo a deliberate demotion; what must never happen is a site
// nobody can administer.

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

async function ensureSuperadmin() {
  const raw = process.env.SUPERADMIN_STEAM_ID;
  if (!raw) return;

  const parsed = parseSteamId64(raw);
  if (parsed.error) {
    logger.error(`SUPERADMIN_STEAM_ID: ${parsed.error}. Суперадмин не создан.`);
    return;
  }

  const existing = await get("SELECT COUNT(*) AS n FROM users WHERE role = 'Superadmin'");
  if (existing && existing.n > 0) {
    logger.info(`Суперадмин уже есть (${existing.n}), SUPERADMIN_STEAM_ID не понадобился.`);
    return;
  }

  let user = await get('SELECT * FROM users WHERE steam_id = ?', [parsed.id]);
  if (!user) {
    // The name is a convenience, like everywhere else it is fetched: a private
    // profile or a slow Steam must not leave the site without an administrator.
    const persona = await fetchPersonaName(parsed.id);
    const base = persona || fallbackNickname(parsed.id);
    for (let attempt = 1; attempt <= 10 && !user; attempt += 1) {
      const username = attempt === 1 ? base : `${base} (${attempt})`;
      try {
        const inserted = await run(
          `INSERT INTO users (username, role, account_type, steam_id, steam_id_verified)
           VALUES (?, 'Superadmin', 'client', ?, 0)`,
          [username, parsed.id]
        );
        user = await get('SELECT * FROM users WHERE id = ?', [inserted.lastID]);
      } catch (err) {
        if (!String(err.message).includes('users.username')) throw err;
      }
    }
    if (!user) {
      logger.error('Не удалось завести аккаунт суперадмина — занят никнейм?');
      return;
    }
  }

  // The first Superadmin is the owner of the server: rights on the site, a place in
  // permittedlist.txt and admin in the game. Nobody granted this — the deployment did.
  await run(
    `UPDATE users
     SET role = 'Superadmin', whitelist_status = 'approved', server_admin = 1,
         whitelist_decided_at = CURRENT_TIMESTAMP
     WHERE id = ?`,
    [user.id]
  );
  logger.info(
    `Суперадмин заведён: ${user.username}, SteamID ${parsed.id}. `
    + 'Вход — «Войти через Steam» этим аккаунтом.'
  );
}

module.exports = { ensureSuperadmin };
