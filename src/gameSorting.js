const { db } = require('../db');
const logger = require('./logger');

/**
 * Куда сортировщик кладёт предмет: выбор админа на сайте.
 *
 * Мод раскладывает по типу предмета, и для большинства вещей этого хватает. Но тип —
 * это слово игры, а не хозяина базы: оленья шкура для игры такой же «материал», как
 * камень, смола лежит у кого-то с зельями, а у кого-то с материалами. Спорить с этим
 * моду нечем, поэтому решение вынесено сюда.
 *
 * Каталог присылает сам мод: какие в игре предметы, как они зовутся по-русски, какого
 * они типа и что мод положил бы сам. Сайт не знает этого списка и не должен — в новой
 * версии игры предметы появятся, и появятся они здесь без единой правки сайта. Это тот
 * же приём, что у правил построек (game_build_rule_meta).
 *
 * Обратно уезжает только выбранное: «ключ=номер», по строке на предмет. Всё, чего в
 * ответе нет, мод решает по-своему — и это не то же самое, что «положить в Разное».
 */

const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows || [])));
});

// Сколько предметов мы готовы принять за один раз. В ванили их около четырёхсот;
// запас на моды есть, но не бесконечный.
const MAX_ITEMS = 4000;

// Ключ перевода: `$item_wood`. Держим формат строго, потому что он же уезжает обратно
// в строку «ключ=номер», и пробел или знак равенства в нём разорвал бы её.
const KIND_RE = /^[A-Za-z0-9_$.\-]{2,64}$/;

async function state() {
  await run('INSERT INTO game_sort_sync (id) VALUES (1) ON CONFLICT (id) DO NOTHING');
  return get('SELECT revision, seeded, categories, mod_seen_at, mod_applied_revision'
             + ' FROM game_sort_sync WHERE id = 1');
}

async function nextRevision() {
  const row = await get('UPDATE game_sort_sync SET revision = revision + 1 WHERE id = 1 RETURNING revision');
  return row.revision;
}

function categoryList(text) {
  return String(text || '').split('|').map((title) => title.trim()).filter(Boolean);
}

// Имя категории. Точка с запятой и знак равенства разорвали бы строку, которой они
// едут в мод, вертикальная черта — засев, а длинное имя не влезает на кнопку в игре.
const TITLE_RE = /^[^;=|\r\n]{1,24}$/;

/** Категории как они есть в базе, включая убранные. */
function categoryRows() {
  return all('SELECT id, title, built_in, removed, updated_by, updated_at'
             + ' FROM game_sort_categories ORDER BY id');
}

/**
 * Живые категории списком, где место в списке — это номер.
 *
 * Пустая строка на месте убранной и на месте пропущенного номера: страница и проверка
 * ждут именно этого, а «сдвинуть, чтобы не было дыр» здесь значит переименовать чужие
 * сундуки по всей базе.
 */
function titlesFrom(rows) {
  const titles = [];
  for (const row of rows) {
    if (row.removed) continue;
    while (titles.length <= row.id) titles.push('');
    titles[row.id] = row.title;
  }

  return titles;
}

async function categoryTitles() {
  return titlesFrom(await categoryRows());
}

/**
 * Засев категорий из того, что прислал мод. Только пока их нет вовсе: дальше список
 * здешний, и второй засев его не трогает — иначе перезапуск игрового сервера возвращал
 * бы имена, которые админ переименовал утром.
 */
async function seedCategories(titles) {
  if (!titles.length) return;

  const have = await get('SELECT count(*)::int AS n FROM game_sort_categories');
  if (have && have.n > 0) return;

  for (let id = 0; id < titles.length; id += 1) {
    await run('INSERT INTO game_sort_categories (id, title, built_in) VALUES (?, ?, true)'
              + ' ON CONFLICT (id) DO NOTHING', [id, titles[id]]);
  }

  logger.info(`[sorting] категории засеяны модом: ${titles.length}`);
}

/**
 * Что мод должен применить. Только выбранное человеком: список в несколько сотен строк
 * «этот предмет там, где и был» — это лишний килобайт раз в минуту и ни одного решения.
 */
async function pullText(reportedRevision) {
  const now = await state();

  const applied = Number.parseInt(reportedRevision, 10);
  await run('UPDATE game_sort_sync SET mod_seen_at = now(), mod_applied_revision = ? WHERE id = 1',
            [Number.isFinite(applied) && applied >= 0 ? applied : 0]);

  if (!now.seeded) return 'seed needed\n';

  const rows = await all('SELECT kind, category FROM game_sort_items'
                         + ' WHERE category IS NOT NULL ORDER BY kind');

  // Категории идут впереди выбора: выбор ссылается на их номера. Убранные не шлются
  // вовсе — мод оставит их номер пустым местом, и сундук с такой пометкой останется
  // приёмником без имени, а не превратится в источник.
  const cats = (await categoryRows())
    .filter((row) => !row.removed)
    .map((row) => `cat ${row.id}=${row.title}`);

  return [`rev ${now.revision}`, ...cats, ...rows.map((row) => `${row.kind}=${row.category}`), '']
    .join('\n');
}

/**
 * Каталог от мода. Присылается при каждом старте сервера, и это правильно: предметы
 * могли появиться с обновлением игры, а имена — поменяться с переводом.
 *
 * Выбор админа при этом не трогается ни разу: обновляются только те колонки, которые
 * знает мод. Иначе перезапуск сервера стирал бы вечером то, что решили утром.
 */
async function pushCatalogue(text) {
  const lines = String(text || '').split('\n').map((line) => line.trim()).filter(Boolean);
  if (!lines.length) return { status: 400, text: 'empty\n' };

  let categories = '';
  const items = [];

  for (const line of lines) {
    if (line.startsWith('#categories ')) {
      categories = categoryList(line.slice('#categories '.length).replace(/\|/g, '|')).join('|');
      continue;
    }

    const parts = line.split('|');
    if (parts.length < 4) continue;

    const kind = parts[0].trim();
    if (!KIND_RE.test(kind)) continue;

    const category = Number.parseInt(parts[3], 10);
    items.push({
      kind,
      title: parts[1].trim().slice(0, 120),
      type: parts[2].trim().slice(0, 40),
      category: Number.isFinite(category) && category >= 0 ? category : 0,
    });

    if (items.length >= MAX_ITEMS) break;
  }

  if (!items.length) return { status: 400, text: 'no items\n' };

  for (const item of items) {
    await run(
      `INSERT INTO game_sort_items (kind, title, item_type, mod_category)
       VALUES (?, ?, ?, ?)
       ON CONFLICT (kind) DO UPDATE SET title = EXCLUDED.title,
         item_type = EXCLUDED.item_type, mod_category = EXCLUDED.mod_category`,
      [item.kind, item.title, item.type, item.category]
    );
  }

  await seedCategories(categoryList(categories));

  await run('UPDATE game_sort_sync SET seeded = true, categories = ?, mod_seen_at = now() WHERE id = 1',
            [categories]);

  logger.info(`[sorting] каталог от мода: ${items.length} предметов, категорий ${categoryList(categories).length}`);
  return { status: 200, text: `ok ${items.length}\n` };
}

/** Всё для страницы админки: и что прислал мод, и что выбрали здесь. */
async function overview() {
  const now = await state();
  const rows = await all(
    'SELECT kind, title, item_type, mod_category, category, updated_by, updated_at'
    + ' FROM game_sort_items ORDER BY title, kind'
  );

  const cats = await categoryRows();

  return {
    revision: now.revision,
    seeded: Boolean(now.seeded),
    modSeenAt: now.mod_seen_at,
    modAppliedRevision: now.mod_applied_revision,
    // Двумя видами нарочно: таблица предметов читает список по номеру, а редактор
    // категорий — строки со всем, что о них известно.
    categories: titlesFrom(cats),
    categoryRows: cats.map((row) => ({
      id: row.id,
      title: row.title,
      builtIn: Boolean(row.built_in),
      removed: Boolean(row.removed),
      updatedBy: row.updated_by,
      updatedAt: row.updated_at,
    })),
    items: rows.map((row) => ({
      kind: row.kind,
      title: row.title,
      type: row.item_type,
      modCategory: row.mod_category,
      category: row.category === null || row.category === undefined ? null : row.category,
      updatedBy: row.updated_by,
      updatedAt: row.updated_at,
    })),
  };
}

/** Разбор номера полки. Пустое — «решает мод», и это не «Разное». */
function pickCategory(category, titles) {
  if (category === null || category === undefined || category === '') return { value: null };

  const value = Number.parseInt(category, 10);
  if (!Number.isFinite(value) || value < 0 || value >= titles.length || !titles[value]) {
    return { error: 'Нет такой категории', status: 400 };
  }

  return { value };
}

const saidOf = (value, titles) => (value === null ? 'решает сервер' : `«${titles[value] || value}»`);

/**
 * Выбор по одному предмету. null снимает выбор — предмет возвращается моду, а не
 * уезжает в «Разное»: это разные вещи, и путать их значит тихо ломать раскладку.
 */
async function setCategory(kind, category, by) {
  if (!KIND_RE.test(String(kind || ''))) return { error: 'Неизвестный предмет', status: 400 };

  const row = await get('SELECT kind, title, mod_category FROM game_sort_items WHERE kind = ?', [kind]);
  if (!row) return { error: 'Такого предмета сервер не присылал', status: 404 };

  const titles = await categoryTitles();
  const picked = pickCategory(category, titles);
  if (picked.error) return picked;

  const revision = await nextRevision();
  await run('UPDATE game_sort_items SET category = ?, updated_at = now(), updated_by = ?, '
            + 'revision = ? WHERE kind = ?', [picked.value, by || null, revision, kind]);

  return {
    revision,
    title: row.title || kind,
    said: saidOf(picked.value, titles),
    value: picked.value,
  };
}

/**
 * То же самое пачкой: «все трофеи — на полку трофеев». Предметов в каталоге под
 * тысячу, и по одному это тысяча запросов.
 *
 * Ревизия на всю пачку одна, и не ради экономии: мод забирает выбор целиком, так что
 * тридцать ревизий подряд значат для него ровно то же, что одна, — зато «ждёт сервер»
 * в админке гаснет разом, а не тридцатью морганиями.
 *
 * Незнакомый ключ молча пропускается, а не отменяет всю правку: каталог мог смениться,
 * пока страница была открыта, и терять из-за одного исчезнувшего предмета решение по
 * остальной сотне незачем. Сколько строк легло — в ответе.
 */
async function setCategories(kinds, category, by) {
  const list = Array.from(new Set((Array.isArray(kinds) ? kinds : [])
    .map((kind) => String(kind || '').trim())
    .filter((kind) => KIND_RE.test(kind))));

  if (!list.length) return { error: 'Не выбрано ни одного предмета', status: 400 };
  if (list.length > MAX_ITEMS) return { error: 'Слишком много предметов за раз', status: 400 };

  const now = await state();
  const titles = categoryList(now.categories);
  const picked = pickCategory(category, titles);
  if (picked.error) return picked;

  const revision = await nextRevision();
  const marks = list.map(() => '?').join(', ');
  const done = await run(
    'UPDATE game_sort_items SET category = ?, updated_at = now(), updated_by = ?, '
    + `revision = ? WHERE kind IN (${marks})`,
    [picked.value, by || null, revision, ...list]
  );

  return { revision, changed: done.changes || 0, said: saidOf(picked.value, titles) };
}

/**
 * Новая категория. Номер — следующий за самым большим из бывших, и он не переиспользует
 * номер убранной: в сундуках по всей базе лежат именно номера, и вернуть чужой значит
 * молча переименовать чужое добро.
 */
async function addCategory(title, by) {
  const name = String(title || '').trim();
  if (!TITLE_RE.test(name)) return { error: 'Имя от 1 до 24 знаков, без ; = |', status: 400 };

  const rows = await categoryRows();
  if (rows.some((row) => !row.removed && row.title.toLowerCase() === name.toLowerCase())) {
    return { error: 'Такая категория уже есть', status: 409 };
  }

  // Столько же, сколько терпит мод: дальше он ответ просто не читает.
  const id = rows.length ? Math.max(...rows.map((row) => row.id)) + 1 : 0;
  if (id >= 64) return { error: 'Больше 64 категорий мод не примет', status: 409 };

  const revision = await nextRevision();
  await run('INSERT INTO game_sort_categories (id, title, built_in, revision, updated_at, updated_by)'
            + ' VALUES (?, ?, false, ?, now(), ?)', [id, name, revision, by || null]);

  return { revision, id, title: name };
}

/** Переименование. Номер не трогается никогда — на нём держится всё остальное. */
async function renameCategory(id, title, by) {
  const at = Number.parseInt(id, 10);
  const name = String(title || '').trim();
  if (!TITLE_RE.test(name)) return { error: 'Имя от 1 до 24 знаков, без ; = |', status: 400 };

  const rows = await categoryRows();
  const row = rows.find((one) => one.id === at);
  if (!row) return { error: 'Нет такой категории', status: 404 };

  if (rows.some((one) => one.id !== at && !one.removed && one.title.toLowerCase() === name.toLowerCase())) {
    return { error: 'Такая категория уже есть', status: 409 };
  }

  const revision = await nextRevision();
  await run('UPDATE game_sort_categories SET title = ?, revision = ?, updated_at = now(),'
            + ' updated_by = ? WHERE id = ?', [name, revision, by || null, at]);

  return { revision, id: at, title: name, was: row.title };
}

/**
 * Убрать категорию или вернуть её обратно.
 *
 * Встроенные не убираются: по ним мод раскладывает сам, когда о предмете ничего не
 * сказано, и оставить его решение без имени значило бы получить «Категория 1» над
 * половиной базы. Убранная своя остаётся строкой с removed: сундуки, помеченные ею,
 * остаются приёмниками без имени, и то, что в них лежит, мод разнесёт по местам сам.
 */
async function removeCategory(id, removed, by) {
  const at = Number.parseInt(id, 10);
  const rows = await categoryRows();
  const row = rows.find((one) => one.id === at);
  if (!row) return { error: 'Нет такой категории', status: 404 };
  if (row.built_in && removed) return { error: 'Встроенную категорию убрать нельзя', status: 409 };

  const revision = await nextRevision();
  await run('UPDATE game_sort_categories SET removed = ?, revision = ?, updated_at = now(),'
            + ' updated_by = ? WHERE id = ?', [Boolean(removed), revision, by || null, at]);

  // Выбор предметов, который ссылался на убранную, снимается: иначе он остался бы
  // указывать в пустоту, и мод молча решал бы по-своему, а страница показывала бы номер.
  if (removed) {
    await run('UPDATE game_sort_items SET category = NULL, revision = ?, updated_at = now(),'
              + ' updated_by = ? WHERE category = ?', [revision, by || null, at]);
  }

  return { revision, id: at, title: row.title, removed: Boolean(removed) };
}

module.exports = {
  pullText, pushCatalogue, overview, setCategory, setCategories,
  addCategory, renameCategory, removeCategory,
};
