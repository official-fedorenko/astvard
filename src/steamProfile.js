const { safeAvatarUrl } = require('./avatar');

// Steam's public profile XML carries the persona name and the avatar, and needs no
// API key, which is the only reason a new account can arrive looking like its owner.
// It is a convenience, never a condition: a private profile, a rename, or Steam
// being slow must not stop a login Steam has already vouched for.
const PERSONA_RE = /<steamID>(?:<!\[CDATA\[)?([\s\S]*?)(?:\]\]>)?<\/steamID>/;
// 184×184 — the largest of the three the profile offers, and the one the cabinet
// shows big. The two smaller ones would have to be upscaled there.
const AVATAR_RE = /<avatarFull>(?:<!\[CDATA\[)?([\s\S]*?)(?:\]\]>)?<\/avatarFull>/;
const MAX_NICKNAME_LENGTH = 32;

// Cc is control characters, Cf the format ones: zero-width spaces, bidi overrides,
// the soft hyphen, the BOM. All invisible, all legal in a Steam name, and a name
// built only out of them would read as blank in every list on the site. Written as
// categories rather than a literal class on purpose — the literals would sit in
// this file unreadable, and nobody could review the diff. Names reach the page
// through escapeHtml, so this is about the column and the layout, not safety.
// No-break space is deliberately left out: the \s+ collapse below folds it.
const INVISIBLE_RE = /[\p{Cc}\p{Cf}]/gu;

function tidyPersona(raw) {
  const cleaned = String(raw ?? '')
    .replace(INVISIBLE_RE, '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, MAX_NICKNAME_LENGTH)
    .trim();
  return cleaned || null;
}

/**
 * The name and the avatar in one go: both come from the same page, and asking twice
 * would double the requests to Steam for nothing. Either half can be null — a
 * profile set to private still answers, just with less in it.
 */
async function fetchProfile(steamId) {
  const nothing = { name: null, avatar: null };
  try {
    const res = await fetch(`https://steamcommunity.com/profiles/${steamId}/?xml=1`, {
      signal: AbortSignal.timeout(8000),
    });
    if (!res.ok) return nothing;

    const xml = await res.text();
    const name = PERSONA_RE.exec(xml);
    const avatar = AVATAR_RE.exec(xml);
    return {
      name: name ? tidyPersona(name[1]) : null,
      // Addresses from somebody else's server are input like any other: what does not
      // look like a Steam avatar is not stored at all.
      avatar: avatar ? safeAvatarUrl(String(avatar[1]).trim()) : null,
    };
  } catch {
    return nothing;
  }
}

// Used when Steam gives us no usable name. The tail of the id reads well enough
// and is nearly unique; the database still has the last word on collisions.
function fallbackNickname(steamId) {
  return `Викинг ${String(steamId).slice(-5)}`;
}

module.exports = { fetchProfile, fallbackNickname, tidyPersona, MAX_NICKNAME_LENGTH };
