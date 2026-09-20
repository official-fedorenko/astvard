const { sendJson, getJsonBody, logAction } = require('../utils');
const { db, verifyPassword, hashPassword } = require('../../db');
const { nicknameError } = require('../nickname');
const { safeAvatarUrl } = require('../avatar');
const { fetchProfile } = require('../steamProfile');

function getMe(req, res, user) {
  db.get(
    `SELECT id, username, email, role, avatar_url, created_at,
            steam_id, steam_id_verified, whitelist_status, whitelist_note,
            password_hash IS NOT NULL AS has_password
     FROM users WHERE id = ?`,
    [user.id],
    (err, row) => {
      if (err || !row) return sendJson(res, 404, { success: false, message: 'Пользователь не найден' });
      sendJson(res, 200, { success: true, user: row });
    }
  );
}






/**
 * Профиль игрока: ник, почта и пароль.
 *
 * Аккаунт заводится входом через Steam и живёт без почты и пароля — на сервер
 * пускают по номеру Steam. Всё остальное здесь необязательно и добавляется по
 * желанию: почта — чтобы было куда написать, пароль — чтобы войти, когда Steam
 * недоступен.
 *
 * Отсюда и правило про текущий пароль: спрашиваем его только у того, у кого
 * пароль уже есть. Требовать «текущий» у человека, который задаёт первый,
 * означало бы не дать задать его вовсе.
 */
async function updateProfile(req, res, user) {
  try {
    const body = await getJsonBody(req);
    const { username, email, password, currentPassword, avatar_url } = body;

    db.get("SELECT * FROM users WHERE id = ?", [user.id], (err, dbUser) => {
      if (err || !dbUser) {
        return sendJson(res, 404, { success: false, message: 'Пользователь не найден' });
      }

      const fields = [];
      const values = [];

      if (typeof username === 'string' && username.trim() && username.trim() !== dbUser.username) {
        const problem = nicknameError(username);
        if (problem) return sendJson(res, 400, { success: false, message: problem });
        fields.push('username = ?');
        values.push(username.trim());
      }

      if (typeof email === 'string' && email.trim()) {
        // Приводим к нижнему регистру: иначе Ivan@mail.ru и ivan@mail.ru — два
        // разных адреса для базы и один для почтового сервера.
        const clean = email.trim().toLowerCase();
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(clean)) {
          return sendJson(res, 400, { success: false, message: 'Почта выглядит неправильно' });
        }
        if (clean !== dbUser.email) {
          fields.push('email = ?');
          values.push(clean);
        }
      }

      if (avatar_url) {
        // До 20.09.2026 сюда принималось что угодно: строка, которую игрок вписал
        // себе в профиль, доезжала до браузера админа в списке обращений и вставала
        // в атрибут без экранирования. Проверять на входе дешевле, чем в каждом
        // месте, где аватар рисуют.
        const clean = safeAvatarUrl(avatar_url);
        if (!clean) {
          return sendJson(res, 400, { success: false, message: 'Такой адрес аватара не подходит' });
        }
        fields.push('avatar_url = ?');
        values.push(clean);
      }

      if (password) {
        if (password.length < 8) {
          return sendJson(res, 400, { success: false, message: 'Пароль должен быть минимум 8 символов' });
        }
        // Пароль уже есть — меняет его только тот, кто знает прежний: кука могла
        // остаться на чужом экране.
        if (dbUser.password_hash) {
          if (!currentPassword) {
            return sendJson(res, 400, { success: false, message: 'Для смены пароля укажите текущий' });
          }
          if (!verifyPassword(currentPassword, dbUser.password_hash)) {
            return sendJson(res, 400, { success: false, message: 'Неверный текущий пароль' });
          }
        }
        fields.push('password_hash = ?');
        values.push(hashPassword(password));
      }

      if (fields.length === 0) {
        return sendJson(res, 400, { success: false, message: 'Нет изменений для сохранения' });
      }

      values.push(user.id);

      db.run(
        `UPDATE users SET ${fields.join(', ')} WHERE id = ?`,
        values,
        function (updateErr) {
          if (updateErr) {
            const text = String(updateErr.message);
            const msg = text.includes('users_username_key') || text.includes('username')
              ? 'Такой ник уже занят'
              : (text.includes('UNIQUE') || text.includes('email')
                ? 'Такая почта уже используется'
                : 'Ошибка сохранения профиля');
            return sendJson(res, 400, { success: false, message: msg });
          }
          logAction(user.username, 'Обновил свой профиль');
          db.get(
            "SELECT id, username, email, role, avatar_url, created_at, password_hash IS NOT NULL AS has_password FROM users WHERE id = ?",
            [user.id],
            (e2, fresh) => {
              sendJson(res, 200, { success: true, message: 'Профиль обновлён', user: fresh });
            }
          );
        }
      );
    });
  } catch (e) {
    sendJson(res, 400, { success: false, message: 'Невалидный запрос' });
  }
}

/**
 * «Взять из Steam» в кабинете.
 *
 * Сайт и сам ставит аватар из Steam и держит его свежим, но только пока он там
 * наш: выбрал человек стандартный или загрузил свой — обход к нему больше не
 * подходит. Обратной дороги от этого не было вовсе, и эта кнопка и есть она.
 *
 * Ответ здесь важнее самой картинки: Steam молчит по разным причинам — закрытый
 * профиль, номер, вписанный админом с опечаткой, недоступный Steam, — и все они
 * снаружи выглядят как «кнопка не работает».
 */
function takeSteamAvatar(req, res, user) {
  db.get('SELECT id, steam_id FROM users WHERE id = ?', [user.id], async (err, row) => {
    if (err || !row) return sendJson(res, 404, { success: false, message: 'Пользователь не найден' });
    if (!row.steam_id) {
      return sendJson(res, 400, {
        success: false,
        message: 'Сначала войди через Steam — брать аватар пока неоткуда'
      });
    }

    const profile = await fetchProfile(row.steam_id);
    if (!profile.avatar) {
      return sendJson(res, 502, {
        success: false,
        message: 'Steam не дал картинку: профиль закрыт или Steam сейчас недоступен'
      });
    }

    db.run('UPDATE users SET avatar_url = ? WHERE id = ?', [profile.avatar, row.id], (e2) => {
      if (e2) return sendJson(res, 500, { success: false, message: 'Не удалось сохранить аватар' });
      sendJson(res, 200, { success: true, avatar_url: profile.avatar });
    });
  });
}

module.exports = async function handleCabinet(req, res, user, parsedUrl, method) {
  if (!user) {
    return sendJson(res, 401, { success: false, message: 'Неавторизован' });
  }

  const pathname = parsedUrl.pathname;

  if (pathname === '/api/cabinet/me' && method === 'GET') return getMe(req, res, user);
  if (pathname === '/api/cabinet/profile' && method === 'PUT') return updateProfile(req, res, user);
  if (pathname === '/api/cabinet/avatar/steam' && method === 'POST') return takeSteamAvatar(req, res, user);

  return sendJson(res, 404, { success: false, message: 'API endpoint не найден' });
};
