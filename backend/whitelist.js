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

// Pending first: that is the only part anyone has to act on. Rows an admin added
// by hand never asked for anything, so they have no whitelist_requested_at — they
// sort by the decision instead, and coalesce keeps them from falling to the bottom
// of a NULLS LAST ordering.
function listWhitelistRequests() {
  return pool
    .query(
      `SELECT ${USER_FIELDS} FROM users
       WHERE whitelist_status <> 'none' OR server_admin = true
       ORDER BY (whitelist_status = 'pending') DESC,
                coalesce(whitelist_requested_at, whitelist_decided_at) DESC`
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
  listWhitelistRequests,
  decideWhitelist,
  listApprovedSteamIds,
  setServerAdmin,
  listServerAdmins,
};
