const { db } = require('../db');
const logger = require('./logger');
const { fetchProfile, fallbackNickname } = require('./steamProfile');
const { isSteamAvatar } = require('./avatar');

/**
 * Asking Steam again about the rows the site filled in for itself.
 *
 * A Steam account gets its name and its avatar from the public profile when the row
 * is created, and the login path asks a second time while the row still carries the
 * placeholder name. Both are moments the player has to turn up for. When the profile
 * is private, or Steam is slow, or simply down in that minute, the placeholder stays:
 * «Викинг 90066» sat on the live site for eight days while Steam held «Vallerii» the
 * whole time — that player never came back for a second login.
 *
 * So the site asks on its own as well, and asks about two things at once, because
 * both live on the same page:
 *
 *  - the name, but only while it is still the placeholder. A name somebody typed for
 *    themselves is theirs, and renaming it behind their back would be rude;
 *  - the avatar, while it is empty or still the one Steam gave us. A player who
 *    picked a standard avatar or uploaded their own has answered the question, and
 *    the answer holds until they change it. Where the picture came from is written
 *    in the address itself, so nothing extra has to be remembered.
 *
 * Asking about every Steam-dressed row keeps the avatar current when a player
 * changes it — at the size of this server that is a handful of requests every six
 * hours, and a list long enough for that to matter would need pacing, not this.
 */

// Six hours rather than minutes: what is being waited for is Steam having a better
// day, or a player picking a new picture.
const RECHECK_INTERVAL_MS = 6 * 60 * 60 * 1000;

// One request per row, one after another. A burst towards Steam buys nothing.
const PAUSE_MS = 300;

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Whether the name is still ours is decided by the very function that makes one.
// Spelling the format out in SQL would keep it in two places, and the second copy
// would go stale without a sound.
const wearsOurName = (row) => row.username === fallbackNickname(row.steam_id);
const wearsOurAvatar = (row) => !row.avatar_url || isSteamAvatar(row.avatar_url);

/**
 * Walks every Steam-linked row the site still dresses and asks Steam about it.
 * `fetchOne` and `pauseMs` exist for the tests: the logic here is worth checking
 * without a live Steam on the other end.
 */
async function refreshFromSteam(options = {}) {
  const fetchOne = options.fetchOne || fetchProfile;
  const pauseMs = options.pauseMs === undefined ? PAUSE_MS : options.pauseMs;

  const rows = await all(
    'SELECT id, username, steam_id, avatar_url FROM users WHERE steam_id IS NOT NULL'
  );
  const waiting = rows.filter((row) => wearsOurName(row) || wearsOurAvatar(row));

  const renamed = [];
  const dressed = [];
  let silent = 0;
  let taken = 0;

  for (let i = 0; i < waiting.length; i += 1) {
    const row = waiting[i];
    if (i > 0 && pauseMs > 0) await sleep(pauseMs);

    const profile = await fetchOne(row.steam_id) || {};
    if (!profile.name && !profile.avatar) {
      silent += 1;
      continue;
    }

    if (wearsOurName(row) && profile.name && profile.name !== row.username) {
      try {
        await run('UPDATE users SET username = ? WHERE id = ?', [profile.name, row.id]);
        renamed.push({ id: row.id, from: row.username, to: profile.name });
      } catch (err) {
        // Somebody else already goes by that name. A placeholder is ugly, not broken,
        // so this is a line in the log rather than a failure of the whole sweep.
        taken += 1;
        logger.warn(`Ник из Steam не занять (${row.username} → ${profile.name}): ${err.message}`);
      }
    }

    if (wearsOurAvatar(row) && profile.avatar && profile.avatar !== row.avatar_url) {
      await run('UPDATE users SET avatar_url = ? WHERE id = ?', [profile.avatar, row.id]);
      dressed.push({ id: row.id, avatar: profile.avatar });
    }
  }

  return { checked: waiting.length, renamed, dressed, silent, taken };
}

module.exports = { refreshFromSteam, RECHECK_INTERVAL_MS };
