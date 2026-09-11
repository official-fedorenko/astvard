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

module.exports = { USER_FIELDS, createUser, findUserByEmail, findUserById, listUsers, updateUserRole };
