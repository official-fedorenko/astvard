const { pool } = require('./db');
const { USER_FIELDS } = require('./users');

// Any new id starts the request over, even for a player already approved. Without
// the reset an approved player could swap their own Steam id for someone else's
// and carry the approval across to them.
function requestWhitelist(userId, steamId) {
  return pool
    .query(
      `UPDATE users
       SET steam_id = $2,
           whitelist_status = 'pending',
           whitelist_requested_at = now(),
           whitelist_decided_at = NULL,
           whitelist_decided_by = NULL,
           whitelist_note = NULL
       WHERE id = $1
       RETURNING ${USER_FIELDS}`,
      [userId, steamId]
    )
    .then((r) => r.rows[0]);
}

// Pending first: that is the only part of the list anyone has to act on.
function listWhitelistRequests() {
  return pool
    .query(
      `SELECT ${USER_FIELDS} FROM users
       WHERE whitelist_status <> 'none'
       ORDER BY (whitelist_status = 'pending') DESC, whitelist_requested_at DESC`
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

module.exports = { requestWhitelist, listWhitelistRequests, decideWhitelist, listApprovedSteamIds };
