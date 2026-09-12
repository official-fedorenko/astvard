const { sendJson, logAction } = require('../utils');
const { dumpDatabase } = require('../../db');

/**
 * Выгрузка базы одним JSON.
 *
 * Раньше здесь отдавался файл db.sqlite — копия базы была просто файлом на диске.
 * С Postgres такого файла нет, и «настоящий» бэкап — это pg_dump на самой машине,
 * который умеет и схему, и права; делать его через веб-запрос незачем.
 *
 * Что здесь есть: снимок содержимого таблиц, которого хватает, чтобы посмотреть
 * данные глазами или перенести их руками. Сессии в выгрузку не попадают — это
 * действующие ключи от чужих аккаунтов, и в файле, который скачивают в браузер,
 * им делать нечего.
 *
 * Восстановления через API нет намеренно: приём произвольного файла, который потом
 * заменит рабочие данные, — слишком большая поверхность атаки при небольшой выгоде
 * по сравнению с обычным восстановлением из pg_dump на сервере.
 */
module.exports = async function handleBackup(req, res, user, parsedUrl, method) {
  if (!user || user.role !== 'Superadmin') {
    return sendJson(res, 403, { success: false, message: 'Только Superadmin' });
  }
  if (method !== 'GET') {
    return sendJson(res, 405, { success: false, message: 'Метод не поддерживается' });
  }

  try {
    const dump = await dumpDatabase();
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
    const body = JSON.stringify({ created_at: new Date().toISOString(), tables: dump }, null, 2);

    logAction(user.username, 'Выгрузил базу данных');
    res.writeHead(200, {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Disposition': `attachment; filename="astvard-backup-${timestamp}.json"`,
      'Content-Length': Buffer.byteLength(body)
    });
    res.end(body);
  } catch (err) {
    sendJson(res, 500, { success: false, message: 'Не удалось выгрузить базу' });
  }
};
