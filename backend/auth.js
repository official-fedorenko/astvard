const bcrypt = require('bcryptjs');
const jwt = require('jsonwebtoken');

const JWT_SECRET = process.env.JWT_SECRET;

// Without this the process starts happily, health checks pass, and the failure
// surfaces as an unexplained 500 on the first login while every request quietly
// becomes anonymous. Far better to refuse to start.
if (!JWT_SECRET) {
  console.error('JWT_SECRET is not set — refusing to start.');
  process.exit(1);
}
const TOKEN_MAX_AGE_SECONDS = 7 * 24 * 60 * 60; // 7 days

function hashPassword(password) {
  return bcrypt.hash(password, 10);
}

function verifyPassword(password, hash) {
  return bcrypt.compare(password, hash);
}

function signToken(user) {
  return jwt.sign(
    { sub: user.id, nickname: user.nickname, role: user.role },
    JWT_SECRET,
    { expiresIn: TOKEN_MAX_AGE_SECONDS }
  );
}

function verifyToken(token) {
  try {
    return jwt.verify(token, JWT_SECRET);
  } catch {
    return null;
  }
}

module.exports = { hashPassword, verifyPassword, signToken, verifyToken, TOKEN_MAX_AGE_SECONDS };
