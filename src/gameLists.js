const fs = require('node:fs/promises');
const path = require('node:path');
const { db } = require('../db');
const logger = require('./logger');
const { toPermittedListId, toAdminListIds } = require('./steamId');

/**
 * Списки доступа на игровой сервер: кого пускать (`permittedlist.txt`) и кто в
 * игре админ (`adminlist.txt`).
 *
 * Раньше их можно было только выгрузить файлом и положить на сервер руками. Между
 * «выдал админку в панели» и «она появилась в игре» стоял человек с scp, и пока он
 * не дошёл, сайт показывал одно, а сервер жил по другому. Теперь сайт пишет оба
 * файла сам: он стоит на той же машине, а игра перечитывает их на ходу — примерно
 * раз в десять секунд, если время изменения файла свежее прочитанного (SyncedList.Load
 * в assembly_utils.dll). Запись через временный файл и переименование как раз даёт
 * новое время изменения, так что «положили со старой датой и сервер не заметил» —
 * не наш случай.
 *
 * Чего эта запись не делает и делать не должна: она не трогает никакие другие файлы
 * игрового сервера. Два имени, одна папка, и та задаётся развёртыванием.
 */

// Папку знает только развёртывание. В базе её нет намеренно: путь, выбранный через
// админку, — это файл, который сайту прикажет перезаписать кто угодно с правами.
const SAVES_DIR = process.env.VALHEIM_SAVES_DIR || '/srv/valheim/saves';

const PERMITTED_FILE = 'permittedlist.txt';
const ADMIN_FILE = 'adminlist.txt';

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});

/**
 * Запись, переживающая обрыв: сначала во временный файл рядом, потом
 * переименование. Переименование в пределах одной папки атомарно, так что игра
 * никогда не прочитает половину списка — и получит свежее время изменения.
 *
 * Прежний файл сохраняется рядом как `.bak`: списки доступа — это то, чем можно
 * запереть всех снаружи, и вернуть предыдущий должно быть просто.
 */
async function writeAtomic(name, text) {
  const target = path.join(SAVES_DIR, name);
  const temp = `${target}.tmp`;

  try {
    await fs.copyFile(target, `${target}.bak`);
  } catch (err) {
    // Файла ещё нет — значит и сохранять нечего.
    if (err.code !== 'ENOENT') throw err;
  }

  await fs.writeFile(temp, text, 'utf8');
  await fs.rename(temp, target);
}

async function permittedLines() {
  const rows = await all(
    `SELECT steam_id FROM users
     WHERE whitelist_status = 'approved' AND steam_id IS NOT NULL
     ORDER BY whitelist_decided_at`
  );
  return rows.map((r) => toPermittedListId(r.steam_id));
}

async function adminLines() {
  const rows = await all(
    'SELECT steam_id FROM users WHERE server_admin = 1 AND steam_id IS NOT NULL ORDER BY username'
  );
  // Обе формы номера: `ZNet.PlayerIsAdmin` сравнивает id сырым и совпадает только
  // с той формой, которую прислал клиент.
  return rows.flatMap((r) => toAdminListIds(r.steam_id));
}

/**
 * Кладёт оба списка на игровой сервер. Возвращает, что произошло с каждым файлом,
 * — вызывающий показывает это человеку, а не гадает.
 *
 * Пустой `permittedlist.txt` не пишется никогда: для игры пустой файл означает
 * «пускать всех», то есть ровно противоположное тому, зачем список собирали.
 * Пустой `adminlist.txt`, наоборот, безопасен и означает «админов нет» — снимать
 * права тоже надо уметь.
 */
async function applyGameLists() {
  const [permitted, admins] = await Promise.all([permittedLines(), adminLines()]);
  const result = {
    dir: SAVES_DIR,
    permitted: { count: permitted.length, written: false, reason: null },
    admins: { count: admins.length / 2, written: false, reason: null }
  };

  if (permitted.length === 0) {
    result.permitted.reason = 'Ни одной одобренной заявки: пустой список открыл бы сервер всем, поэтому файл не тронут';
  } else {
    await writeAtomic(PERMITTED_FILE, `${permitted.join('\n')}\n`);
    result.permitted.written = true;
  }

  await writeAtomic(ADMIN_FILE, admins.length ? `${admins.join('\n')}\n` : '');
  result.admins.written = true;

  logger.info(`[lists] записано в ${SAVES_DIR}: доступ ${permitted.length}, админов ${result.admins.count}`);
  return result;
}

/**
 * То же, но для случая «список изменился сам собой»: выдали доступ, забрали
 * админку. Ошибку сюда пускать нельзя — иначе неудачная запись файла отменяла бы
 * уже принятое решение, — но и молчать о ней нельзя.
 */
async function applyGameListsQuietly(reason) {
  try {
    return await applyGameLists();
  } catch (err) {
    logger.error(`[lists] не удалось обновить списки на сервере (${reason}): ${err.message}`);
    return null;
  }
}

module.exports = { applyGameLists, applyGameListsQuietly, permittedLines, adminLines, SAVES_DIR };
