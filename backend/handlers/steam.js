const { buildAuthUrl, verifyAssertion } = require('../steam-openid');
const { parseSteamId64 } = require('../steamid');
const { fetchPersonaName, fallbackNickname } = require('../steam-profile');
const { issueSession } = require('../auth');
const {
  findUserById,
  findUserBySteamId,
  createSteamUser,
  attachSteamId,
  markSteamIdVerified,
} = require('../users');

// Steam sends the player back to an absolute address, and that address is signed
// into the response — so it has to be configured, not guessed from the Host header,
// which the caller writes. Wrong value here means Steam refuses the round trip;
// a header-derived one would mean accepting a response minted for another site.
const APP_URL = (process.env.APP_URL || 'http://127.0.0.1:3001').replace(/\/+$/, '');
const RETURN_TO = `${APP_URL}/api/auth/steam/return`;

if (!process.env.APP_URL) {
  console.warn(`APP_URL is not set — Steam sign-in will send players to ${RETURN_TO}`);
}

function redirect(res, location) {
  res.writeHead(302, { Location: location });
  res.end();
}

// The player is in a browser mid-redirect, so a JSON error would be a blank page
// with text on it. The message rides in the query string and the page prints it
// with textContent; it is our own wording, but it has been through the URL bar.
function redirectWithError(res, path, message) {
  redirect(res, `${path}?steam_error=${encodeURIComponent(message)}`);
}

function start(req, res) {
  redirect(res, buildAuthUrl({ returnTo: RETURN_TO, realm: APP_URL }));
}

async function complete(req, res) {
  const query = new URL(req.url, APP_URL).searchParams;

  const verified = await verifyAssertion(query, { expectedReturnTo: RETURN_TO });
  if (verified.error) {
    return redirectWithError(res, '/login.html', verified.error);
  }

  // Steam would not sign nonsense, but the range check is the same one the rest of
  // the site trusts, and it lives in one module. Cheap insurance against the day
  // the shape of that id changes.
  const parsed = parseSteamId64(verified.steamId);
  if (parsed.error) {
    return redirectWithError(res, '/login.html', parsed.error);
  }
  const steamId = parsed.id;

  const actor = req.user ? await findUserById(req.user.sub) : null;
  const owner = await findUserBySteamId(steamId);

  // The row may have been typed in by an admin, for an owner who had never been
  // here. Steam has just signed for the number, so it stops being a guess.
  if (owner && !owner.steam_id_verified) {
    await markSteamIdVerified(owner.id);
  }

  // Signed in already: this is a binding, not a login.
  if (actor) {
    if (owner && owner.id !== actor.id) {
      return redirectWithError(res, '/cabinet.html', 'Этот Steam уже привязан к другому аккаунту');
    }
    if (!owner) {
      await attachSteamId(actor.id, steamId);
    }
    return redirect(res, '/cabinet.html');
  }

  if (owner) {
    issueSession(res, owner);
    return redirect(res, '/cabinet.html');
  }

  // First visit through Steam: the account is created here. The persona name is
  // best-effort — a private profile just means the fallback name.
  const persona = await fetchPersonaName(steamId);
  const created = await createSteamUser({
    nickname: persona || fallbackNickname(steamId),
    steamId,
    // Steam has just signed for this id — verifyAssertion went back to Steam with
    // check_authentication and got is_valid:true. Left false, the very first visit
    // tells the owner an admin typed their number in, which nobody did.
    verified: true,
  });
  if (!created) {
    return redirectWithError(res, '/login.html', 'Не удалось создать аккаунт, попробуй ещё раз');
  }
  issueSession(res, created);
  redirect(res, '/cabinet.html');
}

module.exports = { start, complete, RETURN_TO, APP_URL };
