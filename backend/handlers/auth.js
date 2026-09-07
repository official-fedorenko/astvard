const { readJsonBody } = require('../util/body');
const { serializeCookie } = require('../util/cookies');
const { hashPassword, verifyPassword, signToken, TOKEN_MAX_AGE_SECONDS } = require('../auth');
const { createUser, findUserByEmail, findUserById } = require('../users');

const NICKNAME_RE = /^[a-zA-Zа-яА-Я0-9_ -]{2,32}$/;
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

function setAuthCookie(res, token) {
  const cookie = serializeCookie('token', token, {
    maxAgeSeconds: TOKEN_MAX_AGE_SECONDS,
    httpOnly: true,
    secure: process.env.APP_ENV === 'production',
  });
  res.setHeader('Set-Cookie', cookie);
}

async function register(req, res) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  const { nickname, email, password } = body;

  if (!nickname || !NICKNAME_RE.test(nickname)) {
    return sendJson(res, 400, { error: 'Никнейм: 2-32 символа, буквы/цифры/подчёркивание/дефис' });
  }
  if (!email || !EMAIL_RE.test(email)) {
    return sendJson(res, 400, { error: 'Некорректный email' });
  }
  if (!password || password.length < 6) {
    return sendJson(res, 400, { error: 'Пароль минимум 6 символов' });
  }

  const existing = await findUserByEmail(email);
  if (existing) {
    return sendJson(res, 409, { error: 'Пользователь с таким email уже существует' });
  }

  const passwordHash = await hashPassword(password);
  let user;
  try {
    user = await createUser({ nickname, email, passwordHash });
  } catch (err) {
    if (err.code === '23505') {
      return sendJson(res, 409, { error: 'Никнейм или email уже заняты' });
    }
    throw err;
  }

  const token = signToken(user);
  setAuthCookie(res, token);
  sendJson(res, 201, { user });
}

async function login(req, res) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  const { email, password } = body;
  if (!email || !password) {
    return sendJson(res, 400, { error: 'Введите email и пароль' });
  }

  const user = await findUserByEmail(email);
  if (!user || !(await verifyPassword(password, user.password_hash))) {
    return sendJson(res, 401, { error: 'Неверный email или пароль' });
  }

  const token = signToken(user);
  setAuthCookie(res, token);
  sendJson(res, 200, {
    user: { id: user.id, nickname: user.nickname, email: user.email, role: user.role, created_at: user.created_at },
  });
}

function logout(req, res) {
  res.setHeader('Set-Cookie', serializeCookie('token', '', { maxAgeSeconds: 0 }));
  sendJson(res, 200, { ok: true });
}

async function me(req, res) {
  if (!req.user) {
    return sendJson(res, 401, { error: 'Не авторизован' });
  }
  const user = await findUserById(req.user.sub);
  if (!user) {
    return sendJson(res, 401, { error: 'Не авторизован' });
  }
  sendJson(res, 200, { user });
}

module.exports = { register, login, logout, me };
