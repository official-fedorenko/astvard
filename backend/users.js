const { pool } = require('./db');

// One list, used by every read that answers a client. A column added here reaches
// /api/me and the admin list at once; a second hand-written list would not.
// password_hash is deliberately absent — only findUserByEmail needs it.
const USER_FIELDS = `id, nickname, email, role, created_at,
  steam_id, whitelist_status, whitelist_requested_at, whitelist_decided_at, whitelist_note`;

function createUser({ nickname, email, passwordHash }) {
  return pool
    .query(
      `INSERT INTO users (nickname, email, password_hash)
       VALUES ($1, $2, $3)
       RETURNING ${USER_FIELDS}`,
      [nickname, email, passwordHash]
    )
    .then((r) => r.rows[0]);
}

function findUserByEmail(email) {
  return pool.query('SELECT * FROM users WHERE email = $1', [email]).then((r) => r.rows[0]);
}

function findUserById(id) {
  return pool
    .query(`SELECT ${USER_FIELDS} FROM users WHERE id = $1`, [id])
    .then((r) => r.rows[0]);
}

function findUserBySteamId(steamId) {
  return pool
    .query(`SELECT ${USER_FIELDS} FROM users WHERE steam_id = $1`, [steamId])
    .then((r) => r.rows[0]);
}

// Steam is the only thing vouching for this row, so it carries no email and no
// password — the check constraint in db/schema.sql is what keeps that legal.
async function createSteamUser({ nickname, steamId }) {
  // Two players can carry the same Steam persona name, and nickname is unique.
  // Only a nickname clash is worth another go: a clash on steam_id means the
  // caller should have found an existing row and must not paper over it here.
  for (let attempt = 1; attempt <= 10; attempt += 1) {
    const candidate = attempt === 1 ? nickname : `${nickname} (${attempt})`;
    try {
      const { rows } = await pool.query(
        `INSERT INTO users (nickname, steam_id) VALUES ($1, $2) RETURNING ${USER_FIELDS}`,
        [candidate, steamId]
      );
      return rows[0];
    } catch (err) {
      if (err.code === '23505' && err.constraint === 'users_nickname_key') continue;
      throw err;
    }
  }
  return null;
}

// Binding a different Steam account takes the whitelist answer with it — the old
// decision was about the old id. It lands on 'none' rather than 'pending' because
// asking for access is something the player does on purpose.
function attachSteamId(userId, steamId) {
  return pool
    .query(
      `UPDATE users
       SET steam_id = $2,
           whitelist_status = 'none',
           whitelist_requested_at = NULL,
           whitelist_decided_at = NULL,
           whitelist_decided_by = NULL,
           whitelist_note = NULL
       WHERE id = $1
       RETURNING ${USER_FIELDS}`,
      [userId, steamId]
    )
    .then((r) => r.rows[0]);
}

function listUsers() {
  return pool
    .query(`SELECT ${USER_FIELDS} FROM users ORDER BY created_at DESC`)
    .then((r) => r.rows);
}

function updateUserRole(id, role) {
  return pool
    .query(
      `UPDATE users SET role = $1 WHERE id = $2 RETURNING ${USER_FIELDS}`,
      [role, id]
    )
    .then((r) => r.rows[0]);
}

module.exports = {
  USER_FIELDS,
  createUser,
  findUserByEmail,
  findUserById,
  findUserBySteamId,
  createSteamUser,
  attachSteamId,
  listUsers,
  updateUserRole,
};
