// Steam's public profile XML carries the persona name and needs no API key, which
// is the only reason a new account can arrive under the name its owner recognises.
// It is a convenience, never a condition: a private profile, a rename, or Steam
// being slow must not stop a login Steam has already vouched for.
const PERSONA_RE = /<steamID>(?:<!\[CDATA\[)?([\s\S]*?)(?:\]\]>)?<\/steamID>/;
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

async function fetchPersonaName(steamId) {
  try {
    const res = await fetch(`https://steamcommunity.com/profiles/${steamId}/?xml=1`, {
      signal: AbortSignal.timeout(8000),
    });
    if (!res.ok) return null;
    const xml = await res.text();
    const m = PERSONA_RE.exec(xml);
    return m ? tidyPersona(m[1]) : null;
  } catch {
    return null;
  }
}

// Used when Steam gives us no usable name. The tail of the id reads well enough
// and is nearly unique; the database still has the last word on collisions.
function fallbackNickname(steamId) {
  return `Викинг ${String(steamId).slice(-5)}`;
}

module.exports = { fetchPersonaName, fallbackNickname, tidyPersona, MAX_NICKNAME_LENGTH };
