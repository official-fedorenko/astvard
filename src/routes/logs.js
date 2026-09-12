const { sendJson } = require('../utils');
const { db } = require('../../db');

module.exports = async function handleLogs(req, res, user, parsedUrl, method) {
  if (!user) return sendJson(res, 401, { success: false, message: 'Неавторизован' });
  // The log says who did what in the panel, down to names. Being logged in was
  // never enough to read that; only the admin screen ever asked for it.
  if (user.role !== 'Admin' && user.role !== 'Superadmin') {
    return sendJson(res, 403, { success: false, message: 'Только Admin и Superadmin' });
  }

  if (method === 'GET') {
    const requested = parseInt(parsedUrl.searchParams.get('limit'), 10);
    const limit = Number.isInteger(requested) && requested > 0
      ? Math.min(requested, 500)
      : 500;
    db.all("SELECT * FROM logs ORDER BY id DESC LIMIT ?", [limit], (err, rows) => {
      if (err) return sendJson(res, 500, { message: 'Ошибка базы данных' });
      sendJson(res, 200, rows);
    });
  } else if (method === 'DELETE' && user.role === 'Superadmin') {
    db.run("DELETE FROM logs", [], () => sendJson(res, 200, { success: true }));
  }
};
