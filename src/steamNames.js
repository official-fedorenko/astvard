const { db } = require('../db');
const logger = require('./logger');
const { fetchPersonaName, fallbackNickname } = require('./steamProfile');

/**
 * Asking Steam again for the nicknames the site had to invent.
 *
 * A Steam account arrives with the persona name from the public profile, and the
 * login path asks a second time while the row still carries the placeholder. Both
 * are moments the player has to turn up for. When the profile is private, or Steam
 * is slow, or simply down in that minute, the placeholder stays: «Викинг 90066»
 * sat on the live site for eight days while Steam held the name «Vallerii» the
 * whole time — that player never came back for a second login.
 *
 * So the site asks on its own as well. Only a placeholder is ever replaced: a name
 * somebody typed for themselves is theirs, and renaming it behind their back would
 * be rude.
 */

// Six hours rather than minutes: what is being waited for is Steam having a better
// day, and the set is normally empty — a name, once found, leaves it forever.
const RECHECK_INTERVAL_MS = 6 * 60 * 60 * 1000;

// One request per placeholder, one after another. There are rarely more than a
// couple, and a burst towards Steam buys nothing.
const PAUSE_MS = 300;

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Walks every Steam-linked account still named by the site and asks Steam about it.
 * `fetchName` and `pauseMs` exist for the tests: the logic here is worth checking
 * without a live Steam on the other end.
 */
async function recheckNicknames(options = {}) {
  const fetchName = options.fetchName || fetchPersonaName;
  const pauseMs = options.pauseMs === undefined ? PAUSE_MS : options.pauseMs;

  const rows = await all('SELECT id, username, steam_id FROM users WHERE steam_id IS NOT NULL');
  // Whether a row still carries a placeholder is decided by the very function that
  // makes one. Spelling the format out in SQL would keep it in two places, and the
  // second copy would go stale without a sound.
  const waiting = rows.filter((row) => row.username === fallbackNickname(row.steam_id));

  const renamed = [];
  let silent = 0;
  let taken = 0;

  for (let i = 0; i < waiting.length; i += 1) {
    const row = waiting[i];
    if (i > 0 && pauseMs > 0) await sleep(pauseMs);

    const persona = await fetchName(row.steam_id);
    if (!persona || persona === row.username) {
      silent += 1;
      continue;
    }

    try {
      await run('UPDATE users SET username = ? WHERE id = ?', [persona, row.id]);
      renamed.push({ id: row.id, from: row.username, to: persona });
    } catch (err) {
      // Somebody else already goes by that name. A placeholder is ugly, not broken,
      // so this is a line in the log rather than a failure of the whole sweep.
      taken += 1;
      logger.warn(`Ник из Steam не занять (${row.username} → ${persona}): ${err.message}`);
    }
  }

  return { checked: waiting.length, renamed, silent, taken };
}

module.exports = { recheckNicknames, RECHECK_INTERVAL_MS };
