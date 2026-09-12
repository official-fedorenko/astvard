const { db } = require('../../db');
const logger = require('../logger');
const { logAction } = require('../utils');
const {
  createSession,
  buildSessionCookie,
  SESSION_MAX_AGE_SECONDS
} = require('../session');
const { buildAuthUrl, verifyAssertion } = require('../steamOpenid');
const { parseSteamId64 } = require('../steamId');
const { fetchPersonaName, fallbackNickname } = require('../steamProfile');

// Steam signs its answer for one exact address, and that address is ours to state:
// taking it from the Host header would mean accepting a response minted for another
// site. Locally the default is enough; on the VPS APP_URL is https://astvard.online.
const APP_URL = (process.env.APP_URL || 'http://127.0.0.1:3001').replace(/\/+$/, '');
const RETURN_TO = `${APP_URL}/api/auth/steam/return`;

if (!process.env.APP_URL) {
  logger.warn(`APP_URL не задан — вход через Steam вернёт игрока на ${RETURN_TO}`);
}

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

function redirect(res, location) {
  res.writeHead(302, { Location: location });
  res.end();
}

// The player is in a browser mid-redirect, so a JSON error would be a blank page.
// The message travels in the address bar and is printed with textContent there.
function redirectWithError(res, page, message) {
  redirect(res, `${page}?steam_error=${encodeURIComponent(message)}`);
}

function startSession(req, res, userRow, target) {
  const { token } = createSession(userRow);
  logAction(userRow.username, 'Вход через Steam');
  res.writeHead(302, {
    'Set-Cookie': buildSessionCookie(token, req, { maxAgeSeconds: SESSION_MAX_AGE_SECONDS }),
    Location: target
  });
  res.end();
}

// Two players can carry the same Steam persona name, and username is unique here.
// Only a name clash is worth another go: a clash on steam_id means the caller should
// have found the existing row instead of papering over it.
async function createSteamUser(steamId, baseName) {
  for (let attempt = 1; attempt <= 10; attempt += 1) {
    const username = attempt === 1 ? baseName : `${baseName} (${attempt})`;
    try {
      const result = await run(
        `INSERT INTO users (username, role, account_type, steam_id, steam_id_verified)
         VALUES (?, 'User', 'client', ?, 1)`,
        [username, steamId]
      );
      return get('SELECT * FROM users WHERE id = ?', [result.lastID]);
    } catch (err) {
      if (String(err.message).includes('users.username')) continue;
      throw err;
    }
  }
  return null;
}

function start(req, res) {
  redirect(res, buildAuthUrl({ returnTo: RETURN_TO, realm: APP_URL }));
}

async function complete(req, res, sessionUser) {
  const query = new URL(req.url, APP_URL).searchParams;

  const verified = await verifyAssertion(query, { expectedReturnTo: RETURN_TO });
  if (verified.error) {
    return redirectWithError(res, '/login.html', verified.error);
  }

  // Steam would not sign nonsense, but the range check is the one the rest of the
  // site trusts and it lives in one module.
  const parsed = parseSteamId64(verified.steamId);
  if (parsed.error) {
    return redirectWithError(res, '/login.html', parsed.error);
  }
  const steamId = parsed.id;

  const owner = await get('SELECT * FROM users WHERE steam_id = ?', [steamId]);

  // The row may have been typed in by an admin for someone who had never been here.
  // Steam has just signed for the number, so it stops being a guess.
  if (owner && !owner.steam_id_verified) {
    await run('UPDATE users SET steam_id_verified = 1 WHERE id = ?', [owner.id]);
  }

  // Already signed in: this is a binding, not a login.
  if (sessionUser) {
    if (owner && owner.id !== sessionUser.id) {
      return redirectWithError(res, '/cabinet.html', 'Этот Steam уже привязан к другому аккаунту');
    }
    if (!owner) {
      // Binding a different Steam takes the whitelist answer with it: the previous
      // decision was about the previous number. 'none' rather than 'pending' —
      // asking for access is something the player does on purpose.
      await run(
        `UPDATE users SET steam_id = ?, steam_id_verified = 1,
                          whitelist_status = 'none', whitelist_note = NULL
         WHERE id = ?`,
        [steamId, sessionUser.id]
      );
      logAction(sessionUser.username, 'Привязал Steam');
    }
    return redirect(res, '/cabinet.html');
  }

  if (owner) {
    // An account can carry the placeholder name: the bootstrap creates the owner's
    // row before anyone has logged in, and Steam is asked for the persona name then
    // — it may be private, slow or simply down. That is how the site ended up
    // greeting its owner as «Викинг 08760». Only the placeholder is replaced; a name
    // somebody chose is theirs, and renaming it behind their back would be rude.
    if (owner.username === fallbackNickname(steamId)) {
      const persona = await fetchPersonaName(steamId);
      if (persona && persona !== owner.username) {
        try {
          await run('UPDATE users SET username = ? WHERE id = ?', [persona, owner.id]);
          owner.username = persona;
        } catch (err) {
          // Taken by somebody else — the placeholder is not worth a failed login.
          logger.warn(`Ник из Steam не занять: ${err.message}`);
        }
      }
    }
    return startSession(req, res, owner, '/cabinet.html');
  }

  // First visit through Steam: the account is created here. The persona name is
  // best-effort — a private profile just means the fallback name.
  const persona = await fetchPersonaName(steamId);
  const created = await createSteamUser(steamId, persona || fallbackNickname(steamId));
  if (!created) {
    return redirectWithError(res, '/login.html', 'Не удалось создать аккаунт, попробуй ещё раз');
  }
  logAction(created.username, 'Регистрация через Steam');
  startSession(req, res, created, '/cabinet.html');
}

module.exports = async function handleSteam(req, res, sessionUser, parsedUrl, method) {
  const pathname = parsedUrl.pathname;

  if (pathname === '/api/auth/steam' && method === 'GET') return start(req, res);
  if (pathname === '/api/auth/steam/return' && method === 'GET') {
    try {
      return await complete(req, res, sessionUser);
    } catch (err) {
      // Steam unreachable, the database busy: anything here reads as "not signed in",
      // never as success.
      logger.error('Вход через Steam не удался:', err.message);
      return redirectWithError(res, '/login.html', 'Steam сейчас недоступен, попробуй позже');
    }
  }

  res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify({ success: false, message: 'API endpoint не найден' }));
};
