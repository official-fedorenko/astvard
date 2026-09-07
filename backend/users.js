const { pool } = require('./db');

function createUser({ nickname, email, passwordHash }) {
  return pool
    .query(
      `INSERT INTO users (nickname, email, password_hash)
       VALUES ($1, $2, $3)
       RETURNING id, nickname, email, role, created_at`,
      [nickname, email, passwordHash]
    )
    .then((r) => r.rows[0]);
}

function findUserByEmail(email) {
  return pool.query('SELECT * FROM users WHERE email = $1', [email]).then((r) => r.rows[0]);
}

function findUserById(id) {
  return pool
    .query('SELECT id, nickname, email, role, created_at FROM users WHERE id = $1', [id])
    .then((r) => r.rows[0]);
}

function listUsers() {
  return pool
    .query('SELECT id, nickname, email, role, created_at FROM users ORDER BY created_at DESC')
    .then((r) => r.rows);
}

function updateUserRole(id, role) {
  return pool
    .query(
      'UPDATE users SET role = $1 WHERE id = $2 RETURNING id, nickname, email, role, created_at',
      [role, id]
    )
    .then((r) => r.rows[0]);
}

module.exports = { createUser, findUserByEmail, findUserById, listUsers, updateUserRole };
