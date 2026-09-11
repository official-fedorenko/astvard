const bcrypt = require('bcryptjs');
const jwt = require('jsonwebtoken');
const { serializeCookie } = require('./util/cookies');

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

// Signing a token and setting the cookie always go together. Registration, the
// password login and the Steam return all land here, so the flags — httpOnly, and
// secure outside development — are decided once.
function issueSession(res, user) {
  res.setHeader('Set-Cookie', serializeCookie('token', signToken(user), {
    maxAgeSeconds: TOKEN_MAX_AGE_SECONDS,
    httpOnly: true,
    secure: process.env.APP_ENV === 'production',
  }));
}

module.exports = {
  hashPassword, verifyPassword, signToken, verifyToken, issueSession, TOKEN_MAX_AGE_SECONDS,
};
