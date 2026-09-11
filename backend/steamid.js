// A SteamID64 does not survive Number. The individual-account base alone is
// 76561197960265728, past 2^53, so every comparison or round trip through a
// float would quietly shift the last digits and let a neighbouring account
// through. Bounds are checked on BigInt; everywhere else the id is a string.
const STEAM64_BASE = 76561197960265728n; // 0x0110000100000000, individual accounts
const STEAM64_MIN = STEAM64_BASE + 1n;
const STEAM64_MAX = STEAM64_BASE + 4294967295n; // the account id is 32 bits wide

const DIGITS_RE = /^[0-9]{17}$/;
const PROFILE_URL_RE = /^(?:https?:\/\/)?(?:www\.)?steamcommunity\.com\/profiles\/([0-9]{17})\/?$/i;
const VANITY_URL_RE = /steamcommunity\.com\/id\//i;

// Returns { id } with the bare 17 digits, or { error } with a line for the player.
// Players paste whatever Steam showed them, so a profile link is accepted as well
// as the number itself.
function parseSteamId64(raw) {
  if (typeof raw !== 'string' || !raw.trim()) {
    return { error: 'Укажите Steam ID' };
  }
  const value = raw.trim();

  // A vanity link carries a nickname, not an id, and resolving one needs a Steam
  // API key the portal does not have. Say so instead of failing on the digits.
  if (VANITY_URL_RE.test(value)) {
    return { error: 'Ссылка вида /id/ не подходит — нужен номер SteamID64 или ссылка вида /profiles/. Открой профиль в Steam: «Изменить профиль» → адрес страницы.' };
  }

  const fromUrl = PROFILE_URL_RE.exec(value);
  const digits = fromUrl ? fromUrl[1] : value;

  if (!DIGITS_RE.test(digits)) {
    return { error: 'SteamID64 — это 17 цифр. Можно вставить ссылку на профиль вида steamcommunity.com/profiles/…' };
  }

  const id = BigInt(digits);
  if (id < STEAM64_MIN || id > STEAM64_MAX) {
    return { error: 'Это не похоже на SteamID64 личного аккаунта' };
  }
  return { id: digits };
}

// The server matches V_<SteamID64> and nothing else: ZNet.ListContainsId runs the
// incoming id through FilterPlatformUserID, where the Steam platform becomes the
// V prefix, and the last branch assigns the match flag rather than adding to it —
// so a bare number or Steam_… checked before it is thrown away. Root CLAUDE.md
// has the full reading of that code.
function toPermittedListId(steamId) {
  return `V_${steamId}`;
}

module.exports = { parseSteamId64, toPermittedListId };
