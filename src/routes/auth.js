const crypto = require('crypto');
const { sendJson, getJsonBody, logAction } = require('../utils');
const { db, verifyPassword, hashPassword } = require('../../db');
const { verifyTotp } = require('../totp');
const logger = require('../logger');
const { getClientIP, isRateLimited } = require('../rateLimit');
const {
  getSessionToken,
  buildSessionCookie,
  createSession,
  destroySession,
  SESSION_MAX_AGE_SECONDS
} = require('../session');

// Необязательный IP allowlist для входа в админку.
// Если переменная задана, вход разрешён только с этих IP.
// Полезно для приватных/внутренних установок. Пусто/не задано — без ограничений.
const ADMIN_IP_ALLOWLIST = (process.env.ADMIN_IP_ALLOWLIST || '')
  .split(',')
  .map(s => s.trim())
  .filter(Boolean);

function isIpAllowed(ip) {
  if (ADMIN_IP_ALLOWLIST.length === 0) return true;
  return ADMIN_IP_ALLOWLIST.includes(ip);
}

// Параметры конкретных rate-limit'ов (попыток / минимальный интервал между ними, мс)
const LOGIN_RATE_LIMIT = { maxAttempts: 8, minIntervalMs: 1800 };
const CHECK_USERNAME_RATE_LIMIT = { maxAttempts: 20, minIntervalMs: 1200 };

// Короткоживущие токены между "пароль принят" и "код 2FA подтверждён".
// Полноценную сессию не выдаём, пока не введён код.
const pendingTwoFactorLogins = new Map(); // pendingToken -> { userId, expires }
const PENDING_2FA_TTL_MS = 5 * 60 * 1000;

function issueSession(req, res, userRow) {
  const { token } = createSession(userRow);
  logAction(userRow.username, 'Вход в систему');

  const isDefaultAccount = ['superadmin', 'admin', 'user'].includes(userRow.username.toLowerCase());
  const securityNote = isDefaultAccount
    ? '⚠️ This is a default account. Change the password immediately via the Users section (Superadmin) or profile.'
    : null;

  res.writeHead(200, {
    'Set-Cookie': buildSessionCookie(token, req, { maxAgeSeconds: SESSION_MAX_AGE_SECONDS }),
    'Content-Type': 'application/json; charset=utf-8'
  });
  res.end(JSON.stringify({
    success: true,
    user: { username: userRow.username, role: userRow.role },
    securityWarning: securityNote
  }));
}

// Login (с защитой от брутфорса)
async function login(req, res) {
  try {
    const ip = getClientIP(req);
    if (!isIpAllowed(ip)) {
      return sendJson(res, 403, { success: false, message: 'Вход с этого IP-адреса запрещён' });
    }
    const loginLimit = isRateLimited(ip, 'login', LOGIN_RATE_LIMIT.maxAttempts, LOGIN_RATE_LIMIT.minIntervalMs);
    if (loginLimit.limited) {
      return sendJson(res, 429, { success: false, message: 'Слишком много попыток входа. Подождите немного.' });
    }

    const { username, password } = await getJsonBody(req);
    if (!username || !password) {
      return sendJson(res, 400, { success: false, message: 'Имя пользователя и пароль обязательны' });
    }

    db.get("SELECT * FROM users WHERE LOWER(username) = LOWER(?)", [username], (err, userRow) => {
      if (err || !userRow) {
        return sendJson(res, 401, { success: false, message: 'Неверное имя пользователя или пароль' });
      }

      const isValid = verifyPassword(password, userRow.password_hash);
      if (!isValid) {
        return sendJson(res, 401, { success: false, message: 'Неверное имя пользователя или пароль' });
      }

      if (userRow.two_factor_enabled) {
        // Заодно выметаем просроченные записи — карта живёт долго, а
        // отдельного таймера для неё нет смысла заводить.
        const now = Date.now();
        for (const [k, v] of pendingTwoFactorLogins) {
          if (v.expires < now) pendingTwoFactorLogins.delete(k);
        }

        const pendingToken = crypto.randomBytes(32).toString('hex');
        pendingTwoFactorLogins.set(pendingToken, { userId: userRow.id, expires: now + PENDING_2FA_TTL_MS });
        return sendJson(res, 200, { success: true, requires2FA: true, pendingToken });
      }

      issueSession(req, res, userRow);
    });
  } catch (e) {
    sendJson(res, 500, { success: false, message: 'Внутренняя ошибка сервера' });
  }
}

// Завершение входа вторым фактором (TOTP-код из приложения-аутентификатора)
async function loginTwoFactor(req, res) {
  try {
    const { pendingToken, code } = await getJsonBody(req);
    const pending = pendingToken && pendingTwoFactorLogins.get(pendingToken);

    if (!pending || pending.expires < Date.now()) {
      if (pendingToken) pendingTwoFactorLogins.delete(pendingToken);
      return sendJson(res, 400, { success: false, message: 'Сессия подтверждения истекла, войдите заново' });
    }

    db.get("SELECT * FROM users WHERE id = ?", [pending.userId], (err, userRow) => {
      if (err || !userRow || !userRow.two_factor_enabled) {
        pendingTwoFactorLogins.delete(pendingToken);
        return sendJson(res, 400, { success: false, message: 'Сессия подтверждения истекла, войдите заново' });
      }

      if (!verifyTotp(userRow.two_factor_secret, code)) {
        return sendJson(res, 400, { success: false, message: 'Неверный код подтверждения' });
      }

      pendingTwoFactorLogins.delete(pendingToken);
      issueSession(req, res, userRow);
    });
  } catch (e) {
    sendJson(res, 500, { success: false, message: 'Внутренняя ошибка сервера' });
  }
}

// Регистрации в портале нет: аккаунт заводится сам при первом входе через Steam
// (src/routes/steam.js). Так решено 12.09.2026 и вот почему: на игровой сервер
// пускают по номеру Steam, а не по почте, так что аккаунт без Steam не может ни
// попросить доступ, ни получить его — он просто не тот человек, которого сервер
// узнаёт. Прежняя форма (почта, пароль, ФИО, тип аккаунта, honeypot и
// арифметическая капча) осталась от учётной системы компании и заводила ровно
// такие пустые аккаунты.
//
// Пароль при этом никуда не делся: он есть у аккаунтов, которые админ заводит в
// панели, и его может задать себе любой игрок в кабинете — «дополнить аккаунт».

// Проверка доступности username (с защитой от перебора)
async function checkUsername(req, res, parsedUrl) {
  const username = parsedUrl.searchParams.get('username') || '';
  const ip = getClientIP(req);

  // Лёгкий rate limit на проверку имён (чтобы не спамили)
  const limitCheck = isRateLimited(ip, 'check-username', CHECK_USERNAME_RATE_LIMIT.maxAttempts, CHECK_USERNAME_RATE_LIMIT.minIntervalMs);
  if (limitCheck.limited) {
    return sendJson(res, 429, { success: false, available: false });
  }

  if (!/^[a-zA-Z0-9_]{3,}$/.test(username)) {
    return sendJson(res, 200, { success: true, available: false });
  }

  db.get(
    "SELECT 1 FROM users WHERE LOWER(username) = LOWER(?)",
    [username],
    (err, row) => {
      sendJson(res, 200, { success: true, available: !row });
    }
  );
}

function me(req, res, user) {
  if (!user) {
    return sendJson(res, 401, { success: false, message: 'Неавторизован' });
  }
  return sendJson(res, 200, { success: true, user });
}

function logout(req, res) {
  const token = getSessionToken(req);
  if (token) {
    destroySession(token);
  }
  res.writeHead(200, {
    'Set-Cookie': buildSessionCookie('', req, { clear: true }),
    'Content-Type': 'application/json; charset=utf-8'
  });
  res.end(JSON.stringify({ success: true }));
}

module.exports = async function handleAuth(req, res, user, parsedUrl, method) {
  const pathname = parsedUrl.pathname;

  if (pathname === '/api/auth/login' && method === 'POST') return login(req, res);
  if (pathname === '/api/auth/login-2fa' && method === 'POST') return loginTwoFactor(req, res);
  if (pathname === '/api/auth/check-username' && method === 'GET') return checkUsername(req, res, parsedUrl);
  if (pathname === '/api/auth/me' && method === 'GET') return me(req, res, user);
  if (pathname === '/api/auth/logout' && method === 'POST') return logout(req, res);

  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
};
