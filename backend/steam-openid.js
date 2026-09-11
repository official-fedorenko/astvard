// Steam speaks OpenID 2.0 and nothing else — no OAuth, no API key needed for this
// part. The flow is two hops: send the player to Steam, then take what Steam sends
// back and ask Steam whether it really wrote it.
const STEAM_OPENID_ENDPOINT = 'https://steamcommunity.com/openid/login';
const OPENID_NS = 'http://specs.openid.net/auth/2.0';
const IDENTIFIER_SELECT = 'http://specs.openid.net/auth/2.0/identifier_select';

// Steam always answers with this shape; the 17 digits are the SteamID64.
const CLAIMED_ID_RE = /^https:\/\/steamcommunity\.com\/openid\/id\/([0-9]{17})$/;

function buildAuthUrl({ returnTo, realm }) {
  const params = new URLSearchParams({
    'openid.ns': OPENID_NS,
    'openid.mode': 'checkid_setup',
    'openid.return_to': returnTo,
    'openid.realm': realm,
    // We do not know who is coming, so Steam picks the identity and tells us.
    'openid.identity': IDENTIFIER_SELECT,
    'openid.claimed_id': IDENTIFIER_SELECT,
  });
  return `${STEAM_OPENID_ENDPOINT}?${params}`;
}

// Everything in `query` came off the player's own URL bar, so none of it is
// evidence until Steam says so. Returns { steamId } or { error }.
async function verifyAssertion(query, { expectedReturnTo }) {
  if (query.get('openid.mode') !== 'id_res') {
    // The player pressed Cancel on Steam's page, or something mangled the URL.
    return { error: 'Вход через Steam не завершён' };
  }

  // The signature only covers the fields openid.signed lists. A response whose
  // claimed_id is not in that list carries an unsigned SteamID64 — that is, one
  // the sender chose — and check_authentication would still say the rest is fine.
  const signed = (query.get('openid.signed') || '').split(',');
  if (!signed.includes('claimed_id') || !signed.includes('identity')) {
    return { error: 'Ответ Steam не подписан целиком' };
  }

  // return_to is signed, so comparing it here is what stops a response minted for
  // another site from being replayed against this one.
  if (query.get('openid.return_to') !== expectedReturnTo) {
    return { error: 'Ответ Steam выписан не для этого адреса' };
  }

  // Hand the whole thing back to Steam with the mode swapped. This is the only
  // step that proves anything; skipping it means trusting a query string.
  const body = new URLSearchParams(query);
  body.set('openid.mode', 'check_authentication');

  let text;
  try {
    const res = await fetch(STEAM_OPENID_ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body,
      signal: AbortSignal.timeout(15000),
    });
    text = await res.text();
  } catch {
    // Steam being unreachable must read as "not signed in", never as "signed in".
    return { error: 'Steam не отвечает, попробуй ещё раз' };
  }

  // The body is key:value lines, not JSON. Match the whole line so that a stray
  // "is_valid:true" inside some other value cannot pass for the verdict.
  if (!/^is_valid:true$/m.test(text)) {
    return { error: 'Steam не подтвердил вход' };
  }

  const m = CLAIMED_ID_RE.exec(query.get('openid.claimed_id') || '');
  if (!m) {
    return { error: 'Steam вернул неожиданный профиль' };
  }
  return { steamId: m[1] };
}

module.exports = { buildAuthUrl, verifyAssertion, STEAM_OPENID_ENDPOINT };
