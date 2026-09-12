const { sendJson, getJsonBody } = require('../utils');
const { db } = require('../../db');

module.exports = async function handleSettings(req, res, user, parsedUrl, method, { reloadSettingsCache }) {
  if (!user) return sendJson(res, 401, { success: false, message: 'Неавторизован' });

  if (method === 'GET') {
    db.all("SELECT * FROM settings", [], (err, rows) => {
      if (err) return sendJson(res, 500, { message: 'Ошибка базы данных' });
      sendJson(res, 200, rows);
    });
  } else if (method === 'POST') {
    try {
      const settings = await getJsonBody(req);
      // INSERT OR REPLACE был SQLite-выражением; в Postgres то же самое — это
      // ON CONFLICT по ключу. Описание настройки при этом не затирается: его
      // пишет схема, а форма настроек присылает только значения.
      const stmt = db.prepare(
        `INSERT INTO settings (key, value) VALUES (?, ?)
         ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value`
      );
      let writeError = null;
      db.serialize(() => {
        for (const [key, value] of Object.entries(settings)) {
          stmt.run(key, value, (err) => { if (err && !writeError) writeError = err; });
        }
        // Отвечаем только из колбэка finalize(): stmt.run() асинхронный, и без
        // этого 200 уходил клиенту раньше, чем настройки реально попадали в БД
        // (следующий же GET мог вернуть старые значения).
        stmt.finalize((err) => {
          if (writeError || err) return sendJson(res, 500, { message: 'Ошибка сохранения' });
          if (typeof reloadSettingsCache === 'function') {
            reloadSettingsCache();
          }
          sendJson(res, 200, { success: true });
        });
      });
    } catch (e) {
      sendJson(res, 500, { message: 'Ошибка сохранения' });
    }
  }
};
