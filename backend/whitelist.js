const { pool } = require('./db');
const { USER_FIELDS } = require('./users');

// The id is not an argument any more: it reaches the row through Steam OpenID,
// where Steam signed for it, and typing one by hand is no longer a way in. What is
// left here is the request itself. Re-binding a different Steam account resets the
// answer in attachSteamId, so nothing carries an old approval forward.
function requestWhitelist(userId, requestNote) {
  return pool
    .query(
      `UPDATE users
       SET whitelist_status = 'pending',
           whitelist_requested_at = now(),
           whitelist_request_note = $2,
           whitelist_decided_at = NULL,
           whitelist_decided_by = NULL,
           whitelist_note = NULL
       WHERE id = $1 AND steam_id IS NOT NULL AND whitelist_status <> 'approved'
       RETURNING ${USER_FIELDS}`,
      [userId, requestNote]
    )
    .then((r) => r.rows[0]);
}

// An admin putting someone in directly, with no request to answer. decideWhitelist
// refuses a row that never asked — on purpose, so a stray id cannot be approved by
// accident — so granting is its own statement rather than a loosened check there.
function grantWhitelist({ userId, actorId }) {
  return pool
    .query(
      `UPDATE users
       SET whitelist_status = 'approved',
           whitelist_decided_at = now(),
           whitelist_decided_by = $2,
           whitelist_note = NULL
       WHERE id = $1 AND steam_id IS NOT NULL
       RETURNING ${USER_FIELDS}`,
      [userId, actorId]
    )
    .then((r) => r.rows[0]);
}

// Rights inside the game, not on this site. Without a Steam id there is nothing to
// write into adminlist.txt, so the row has to carry one.
function setServerAdmin(userId, value) {
  return pool
    .query(
      `UPDATE users SET server_admin = $2
       WHERE id = $1 AND steam_id IS NOT NULL
       RETURNING ${USER_FIELDS}`,
      [userId, Boolean(value)]
    )
    .then((r) => r.rows[0]);
}

function listServerAdmins() {
  return pool
    .query(
      `SELECT nickname, steam_id, whitelist_status FROM users
       WHERE server_admin = true AND steam_id IS NOT NULL
       ORDER BY nickname`
    )
    .then((r) => r.rows);
}

// Everyone with a Steam id, not only those who asked: an admin hands access to a
// player who signed in and never pressed the button as often as he answers an
// application, and a row invisible in the table can only be reached by copying the
// number into the "add by SteamID64" field. A row without a Steam id has nothing to
// put into permittedlist.txt, so it stays out.
//
// Pending first — that is the only part anyone has to act on today. Rows added by
// hand never asked and have no whitelist_requested_at, so they sort by the decision;
// coalesce keeps them from sinking under a NULLS LAST ordering.
function listWhitelistPeople() {
  return pool
    .query(
      `SELECT ${USER_FIELDS} FROM users
       WHERE steam_id IS NOT NULL
       ORDER BY (whitelist_status = 'pending') DESC,
                (whitelist_status = 'approved') DESC,
                coalesce(whitelist_requested_at, whitelist_decided_at) DESC NULLS LAST,
                nickname`
    )
    .then((r) => r.rows);
}

// The status guard keeps a decision from landing on someone who never applied —
// approving a row with no steam_id would put an empty id in the permitted list.
function decideWhitelist({ userId, status, note, actorId }) {
  return pool
    .query(
      `UPDATE users
       SET whitelist_status = $2,
           whitelist_note = $3,
           whitelist_decided_at = now(),
           whitelist_decided_by = $4
       WHERE id = $1 AND whitelist_status <> 'none' AND steam_id IS NOT NULL
       RETURNING ${USER_FIELDS}`,
      [userId, status, note, actorId]
    )
    .then((r) => r.rows[0]);
}

// Taking access back. Not 'rejected': that answers a request, and a player who was
// let in by hand never made one — the line the cabinet shows him would be a lie. The
// row returns to 'none', where it started, and who took it away stays on the row.
//
// In-game admin goes with it. The two are separate rights, but an admin who is not
// allowed on the server is a right waiting to be forgotten: permittedlist.txt lets
// everyone in the moment it is empty, and that is exactly when a stale line in
// adminlist.txt turns a stranger into an admin.
function revokeWhitelist({ userId, actorId }) {
  return pool
    .query(
      `UPDATE users
       SET whitelist_status = 'none',
           whitelist_note = NULL,
           whitelist_decided_at = now(),
           whitelist_decided_by = $2,
           server_admin = false
       WHERE id = $1 AND steam_id IS NOT NULL
       RETURNING ${USER_FIELDS}`,
      [userId, actorId]
    )
    .then((r) => r.rows[0]);
}

function listApprovedSteamIds() {
  return pool
    .query(
      `SELECT steam_id FROM users
       WHERE whitelist_status = 'approved' AND steam_id IS NOT NULL
       ORDER BY whitelist_decided_at`
    )
    .then((r) => r.rows.map((row) => row.steam_id));
}

module.exports = {
  requestWhitelist,
  grantWhitelist,
  revokeWhitelist,
  listWhitelistPeople,
  decideWhitelist,
  listApprovedSteamIds,
  setServerAdmin,
  listServerAdmins,
};
