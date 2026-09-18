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

  return [`rev ${now.revision}`, ...rows.map((row) => `${row.kind}=${row.category}`), ''].join('\n');
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

  return {
    revision: now.revision,
    seeded: Boolean(now.seeded),
    modSeenAt: now.mod_seen_at,
    modAppliedRevision: now.mod_applied_revision,
    categories: categoryList(now.categories),
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

/**
 * Выбор по одному предмету. null снимает выбор — предмет возвращается моду, а не
 * уезжает в «Разное»: это разные вещи, и путать их значит тихо ломать раскладку.
 */
async function setCategory(kind, category, by) {
  if (!KIND_RE.test(String(kind || ''))) return { error: 'Неизвестный предмет', status: 400 };

  const row = await get('SELECT kind, title, mod_category FROM game_sort_items WHERE kind = ?', [kind]);
  if (!row) return { error: 'Такого предмета сервер не присылал', status: 404 };

  const now = await state();
  const titles = categoryList(now.categories);

  let value = null;
  if (category !== null && category !== undefined && category !== '') {
    value = Number.parseInt(category, 10);
    if (!Number.isFinite(value) || value < 0 || (titles.length && value >= titles.length)) {
      return { error: 'Нет такой категории', status: 400 };
    }
  }

  const revision = await nextRevision();
  await run('UPDATE game_sort_items SET category = ?, updated_at = now(), updated_by = ?, '
            + 'revision = ? WHERE kind = ?', [value, by || null, revision, kind]);

  const said = value === null
    ? 'решает сервер'
    : `«${titles[value] || value}»`;

  return { revision, title: row.title || kind, said, value };
}

module.exports = { pullText, pushCatalogue, overview, setCategory };
