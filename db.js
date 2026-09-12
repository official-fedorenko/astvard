// .env читается и здесь, а не только в server.js: этот модуль подключают тесты и
// служебные скрипты, у которых своего чтения настроек нет, и без пароля к базе
// они падали с невнятным «client password must be a string». dotenv не
// перезаписывает уже заданные переменные, так что окружение остаётся главным.
require('dotenv').config({ quiet: true });

const { Pool } = require('pg');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const logger = require('./src/logger');

// === Почему этот файл выглядит как sqlite3 ===
//
// Панель написана под sqlite3: `db.run/get/all(sql, params, cb)`, вопросительные
// знаки вместо номеров, `this.lastID`, `this.changes`, `db.serialize` и
// `db.prepare` — 335 обращений в двух десятках маршрутов. Переписать их все под
// драйвер Postgres значит переписать панель, поэтому наружу этот модуль оставляет
// прежнюю форму, а внутри говорит с Postgres. Слой делает ровно четыре вещи и
// больше ничего волшебного:
//
//   ?                    → $1, $2 … (кавычки уважаются: «?» внутри строки — текст)
//   INSERT OR IGNORE     → INSERT … ON CONFLICT DO NOTHING
//   INSERT без RETURNING → добавляется RETURNING *, чтобы работал lastID
//   this.lastID = id вставленной строки, this.changes = число затронутых
//
// Порядок выполнения: одно соединение (pool max: 1) и очередь — как было у sqlite3
// в serialized-режиме. Поэтому `db.serialize()` продолжает означать то же, что
// означал, а не превращается в ложь.

const pool = new Pool({
  host: process.env.POSTGRES_HOST || '127.0.0.1',
  port: Number(process.env.POSTGRES_PORT || 5432),
  user: process.env.POSTGRES_USER || 'astvard',
  password: process.env.POSTGRES_PASSWORD,
  database: process.env.POSTGRES_DB || 'astvard',
  // Одно соединение: очередь запросов остаётся такой же, как была. Панель не под
  // нагрузкой, а предсказуемый порядок здесь дороже параллелизма.
  max: 1
});

pool.on('error', (err) => logger.error('[db] соединение с Postgres:', err.message));

// Ошибка одного запроса не должна рвать очередь следующих.
let chain = Promise.resolve();
function enqueue(task) {
  const result = chain.then(task, task);
  chain = result.then(() => undefined, () => undefined);
  return result;
}

// Вопросительные знаки в номера. Строки в кавычках пропускаются.
function toNumberedParams(sql) {
  let out = '';
  let index = 0;
  let inSingle = false;
  let inDouble = false;
  for (let i = 0; i < sql.length; i += 1) {
    const ch = sql[i];
    if (ch === "'" && !inDouble) inSingle = !inSingle;
    else if (ch === '"' && !inSingle) inDouble = !inDouble;
    if (ch === '?' && !inSingle && !inDouble) {
      index += 1;
      out += `$${index}`;
    } else {
      out += ch;
    }
  }
  return out;
}

function translate(sql) {
  let text = String(sql).trim();
  let ignoreConflict = false;

  if (/^insert\s+or\s+ignore\s+into/i.test(text)) {
    text = text.replace(/^insert\s+or\s+ignore\s+into/i, 'INSERT INTO');
    ignoreConflict = true;
  }

  const isInsert = /^insert\s+into/i.test(text);
  const hasReturning = /\breturning\b/i.test(text);
  text = text.replace(/;\s*$/, '');

  if (ignoreConflict && !/\bon\s+conflict\b/i.test(text)) {
    text += ' ON CONFLICT DO NOTHING';
  }
  // RETURNING *, а не RETURNING id: id есть не у каждой таблицы (составной ключ у
  // notification_reads), а звёздочка работает везде.
  if (isInsert && !hasReturning) {
    text += ' RETURNING *';
  }
  return toNumberedParams(text);
}

function normalizeArgs(params, callback) {
  if (typeof params === 'function') return { values: [], cb: params };
  return { values: params === undefined ? [] : params, cb: callback };
}

function query(sql, values) {
  return enqueue(() => pool.query(translate(sql), values));
}

const db = {
  run(sql, params, callback) {
    const { values, cb } = normalizeArgs(params, callback);
    query(sql, values).then(
      (res) => {
        if (!cb) return;
        // sqlite3 отдавал lastID и changes через this — маршруты рассчитывают
        // именно на это.
        const context = {
          lastID: res.rows && res.rows[0] && res.rows[0].id !== undefined ? res.rows[0].id : null,
          changes: res.rowCount
        };
        cb.call(context, null);
      },
      (err) => {
        if (cb) cb.call({ lastID: null, changes: 0 }, err);
        else logger.error('[db] запрос без обработчика ошибки:', err.message, '|', String(sql).trim().slice(0, 90));
      }
    );
    return db;
  },

  get(sql, params, callback) {
    const { values, cb } = normalizeArgs(params, callback);
    query(sql, values).then(
      (res) => cb && cb(null, res.rows[0]),
      (err) => (cb ? cb(err) : logger.error('[db] get:', err.message))
    );
    return db;
  },

  all(sql, params, callback) {
    const { values, cb } = normalizeArgs(params, callback);
    query(sql, values).then(
      (res) => cb && cb(null, res.rows),
      (err) => (cb ? cb(err, []) : logger.error('[db] all:', err.message))
    );
    return db;
  },

  // В sqlite3 это означало «выполняй по очереди». Здесь очередь одна и так,
  // поэтому достаточно вызвать переданное: порядок сохранится сам.
  serialize(fn) {
    if (typeof fn === 'function') fn();
    return db;
  },

  // Подготовленных выражений в том же виде у pg нет, а панели от них нужен только
  // повторный запуск с разными значениями.
  prepare(sql) {
    return {
      run(...args) {
        const cb = typeof args[args.length - 1] === 'function' ? args.pop() : undefined;
        const values = args.length === 1 && Array.isArray(args[0]) ? args[0] : args;
        db.run(sql, values, cb);
        return this;
      },
      finalize(cb) {
        if (cb) enqueue(() => Promise.resolve()).then(() => cb(null));
      }
    };
  },

  close(cb) {
    pool.end().then(() => cb && cb(null), (err) => cb && cb(err));
  }
};

// Хеширование пароля встроенным pbkdf2; формат хранения «соль:хеш» тот же, что был.
function hashPassword(password) {
  const salt = crypto.randomBytes(16).toString('hex');
  const hash = crypto.pbkdf2Sync(password, salt, 120000, 64, 'sha512').toString('hex');
  return `${salt}:${hash}`;
}

function verifyPassword(password, stored) {
  if (!stored || !String(stored).includes(':')) return false;
  const [salt, hash] = String(stored).split(':');
  const candidate = crypto.pbkdf2Sync(password, salt, 120000, 64, 'sha512').toString('hex');
  // Сравнение постоянного времени: обычное === выдаёт длину общего префикса.
  const a = Buffer.from(hash, 'hex');
  const b = Buffer.from(candidate, 'hex');
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

// Демо-данные компанейских модулей. Модули спрятаны, но их маршруты ещё в дереве и
// эти списки импортируют.
const TEST_EMPLOYEES = [
  { first_name: 'Иван', last_name: 'Петров', position: 'Электрик', department: 'Монтаж', email: 'ivan.test@example.com', phone: '+370 600 00001', status: 'active' },
  { first_name: 'Пётр', last_name: 'Сидоров', position: 'Бригадир', department: 'Монтаж', email: 'petr.test@example.com', phone: '+370 600 00002', status: 'active' },
  { first_name: 'Алексей', last_name: 'Козлов', position: 'Электрик', department: 'Сервис', email: 'alex.test@example.com', phone: '+370 600 00003', status: 'vacation' },
  { first_name: 'Мария', last_name: 'Иванова', position: 'Инженер', department: 'Проект', email: 'maria.test@example.com', phone: '+370 600 00004', status: 'active' },
  { first_name: 'Сергей', last_name: 'Волков', position: 'Электрик', department: 'Монтаж', email: 'sergey.test@example.com', phone: '+370 600 00005', status: 'inactive' }
];

const TEST_TOOLS = [
  { name: 'Перфоратор', category: 'Перфораторы', brand: 'Bosch', model: 'GBH 2-26', serial_number: 'TEST-0001', status: 'available' },
  { name: 'Шуруповёрт', category: 'Дрели-шуруповёрты', brand: 'Makita', model: 'DDF482', serial_number: 'TEST-0002', status: 'available' },
  { name: 'УШМ 125', category: 'УШМ (болгарки)', brand: 'DeWalt', model: 'DWE4237', serial_number: 'TEST-0003', status: 'available' },
  { name: 'Лазерный уровень', category: 'Измерительный инструмент', brand: 'Bosch', model: 'GLL 3-80', serial_number: 'TEST-0004', status: 'available' },
  { name: 'Штроборез', category: 'Штроборезы', brand: 'Makita', model: 'SG1251J', serial_number: 'TEST-0005', status: 'available' }
];

// === Схема и начальные данные ===

const SCHEMA_FILE = path.join(__dirname, 'db', 'schema.sql');

// Панель называет себя из базы, поэтому имя Astvard живёт здесь, а не только в
// вёрстке: на чистой базе иначе вернулось бы чужое имя.
const DEFAULT_SETTINGS = [
  ['site_name', 'Astvard', 'Название вашего веб-ресурса'],
  ['maintenance_mode', 'false', 'Включить/выключить режим обслуживания'],
  ['allow_registration', 'true', 'Разрешить самостоятельную регистрацию пользователей'],
  ['hero_title', 'Astvard — портал игровых серверов', 'Заголовок главного баннера'],
  ['site_description', 'Наши серверы, доступ к ним и всё, что вокруг игры.', 'Описание под заголовком баннера'],
  ['about_title', 'Об Astvard', 'О блоге: Заголовок раздела'],
  ['about_subtitle', 'Небольшая компания, свои серверы и свои правила', 'О блоге: Подзаголовок'],
  ['about_card1_title', 'Свои серверы', 'О блоге: Заголовок карточки 1'],
  ['about_card1_text', 'Valheim и то, что появится дальше. Вход по списку: доступ выдаётся вручную, чтобы на сервере были свои.', 'О блоге: Текст карточки 1'],
  ['about_card2_title', 'Заявка в пару кликов', 'О блоге: Заголовок карточки 2'],
  ['about_card2_text', 'Вход через Steam, кнопка «Запросить доступ» — и заявка уходит админу. Номер подтверждает сам Steam.', 'О блоге: Текст карточки 2'],
  ['contact_title', 'Обратная связь', 'Контакты: Заголовок раздела'],
  ['contact_subtitle', 'Вопрос по серверу или хочешь к нам — напиши', 'Контакты: Подзаголовок'],
  ['contact_email', 'info@astvard.online', 'Контакты: Электронная почта'],
  ['contact_address', 'astvard.online', 'Контакты: Адрес'],
  ['public_card_enabled', 'true', 'Публичная карточка: доступна всем по QR'],
  ['public_card_show_photo', 'true', 'Публичная карточка: показывать фото'],
  ['public_card_show_brand', 'true', 'Публичная карточка: показывать бренд'],
  ['public_card_show_model', 'true', 'Публичная карточка: показывать модель'],
  ['public_card_show_serial', 'true', 'Публичная карточка: показывать серийный №'],
  ['public_card_show_inventory', 'true', 'Публичная карточка: показывать инвентарный №'],
  ['public_card_show_status', 'true', 'Публичная карточка: показывать статус'],
  ['public_card_show_category', 'true', 'Публичная карточка: показывать категорию'],
  ['public_card_show_purchase_date', 'false', 'Публичная карточка: показывать дату покупки'],
  ['public_card_show_notes', 'false', 'Публичная карточка: показывать заметки'],
  ['public_vehicle_card_enabled', 'true', 'Публичная карточка авто: доступна всем по QR'],
  ['public_vehicle_card_show_photo', 'true', 'Публичная карточка авто: показывать фото'],
  ['public_vehicle_card_show_brand', 'true', 'Публичная карточка авто: показывать бренд'],
  ['public_vehicle_card_show_model', 'true', 'Публичная карточка авто: показывать модель'],
  ['public_vehicle_card_show_plate', 'true', 'Публичная карточка авто: показывать гос. номер'],
  ['public_vehicle_card_show_vin', 'false', 'Публичная карточка авто: показывать VIN'],
  ['public_vehicle_card_show_status', 'true', 'Публичная карточка авто: показывать статус'],
  ['public_vehicle_card_show_category', 'true', 'Публичная карточка авто: показывать тип'],
  ['public_vehicle_card_show_year', 'true', 'Публичная карточка авто: показывать год выпуска'],
  ['public_vehicle_card_show_mileage', 'false', 'Публичная карточка авто: показывать пробег']
];

let resolveDbReady;
let rejectDbReady;
const dbReady = new Promise((resolve, reject) => { resolveDbReady = resolve; rejectDbReady = reject; });

// Операторы схемы разделяются по «;» в конце строки: в файле нет ни функций, ни
// долларовых литералов, где точка с запятой значила бы что-то другое.
function schemaStatements() {
  const text = fs.readFileSync(SCHEMA_FILE, 'utf8');
  return text
    .split(/;\s*$/m)
    .map((s) => s.trim())
    .filter((s) => s && !s.split('\n').every((line) => line.trim().startsWith('--')));
}

// Вся подготовка базы — одна задача в очереди, а не череда мелких. Сессии
// восстанавливаются и серверы опрашиваются сразу при старте; когда схема вставала
// по оператору за раз, эти запросы вклинивались между ними и получали
// «relation does not exist» на пустой базе. Внутри задачи обращаемся к pool
// напрямую: очередь уже занята нами, и вложенный enqueue ждал бы сам себя.
async function initialize() {
  for (const statement of schemaStatements()) {
    await pool.query(statement);
  }
  await seedDefaults();
}

// Настройки и первый вход. Вынесено отдельно, потому что тесты очищают базу и
// заводят то же самое заново — иначе они проверяли бы пустоту.
async function seedDefaults() {
  for (const [key, value, description] of DEFAULT_SETTINGS) {
    await pool.query(
      'INSERT INTO settings (key, value, description) VALUES ($1, $2, $3) ON CONFLICT (key) DO NOTHING',
      [key, value, description]
    );
  }

  // Свежий сайт не должен встречать пустой лентой: одна статья про то, что здесь
  // происходит, и один черновик, чтобы было видно, как это выглядит в админке.
  const { rows: articleCount } = await pool.query('SELECT count(*)::int AS n FROM articles');
  if (articleCount[0].n === 0) {
    await pool.query(
      'INSERT INTO articles (title, content, status) VALUES ($1, $2, $3)',
      ['Добро пожаловать в Astvard',
       'Здесь будут новости серверов и всё, что вокруг игры. Доступ на сервер — через «Войти через Steam» и кнопку «Запросить доступ» в кабинете.',
       'published']
    );
    await pool.query(
      'INSERT INTO articles (title, content, status) VALUES ($1, $2, $3)',
      ['Черновик', 'Эта статья ещё не опубликована — так выглядит черновик в админке.', 'draft']
    );
  }

  // Аккаунты по умолчанию — с паролем, напечатанным в README самой панели. Это
  // единственный вход в свежую установку, но у Astvard вход другой:
  // SUPERADMIN_STEAM_ID заводит хозяина по номеру Steam (src/bootstrap.js).
  const { rows } = await pool.query('SELECT count(*)::int AS n FROM users');
  if (rows[0].n === 0 && process.env.SUPERADMIN_STEAM_ID) {
    logger.info('SUPERADMIN_STEAM_ID задан — аккаунты по умолчанию не создаю');
  } else if (rows[0].n === 0) {
    const hashed = hashPassword('1234qwer');
    for (const u of [
      { username: 'superadmin', email: 'superadmin@example.com', role: 'Superadmin' },
      { username: 'admin', email: 'admin@example.com', role: 'Admin' },
      { username: 'user', email: 'user@example.com', role: 'User' }
    ]) {
      await pool.query(
        'INSERT INTO users (username, email, password_hash, role) VALUES ($1, $2, $3, $4)',
        [u.username, u.email, hashed, u.role]
      );
      logger.info(`Создан аккаунт по умолчанию: ${u.username} (${u.role})`);
    }
  }
}

enqueue(initialize).then(
  () => {
    logger.info('[db] Postgres готов');
    resolveDbReady();
  },
  (err) => {
    // Молчать нельзя: без схемы панель отвечала бы 500 на каждый запрос и
    // выглядела бы сломанной без объяснения.
    logger.error('[db] не удалось подготовить базу:', err.message);
    rejectDbReady(err);
  }
);
// Отказ dbReady должен иметь обработчик и здесь: без него один незакрытый промис
// роняет весь процесс, и настоящая причина теряется под стеком pg.
dbReady.catch(() => {});

// === Сессии ===
// Живут в базе, чтобы переживать перезапуск. Таблица та же, что была в SQLite.

function saveSession(token, sessionUser) {
  db.run(
    `INSERT INTO sessions (token, user_id, username, role, expires_at)
     VALUES (?, ?, ?, ?, now() + interval '24 hours')
     ON CONFLICT (token) DO UPDATE
       SET user_id = EXCLUDED.user_id, username = EXCLUDED.username,
           role = EXCLUDED.role, expires_at = EXCLUDED.expires_at`,
    [token, sessionUser.id, sessionUser.username, sessionUser.role]
  );
}

function loadSessionsIntoMap(map) {
  db.all('SELECT * FROM sessions WHERE expires_at > now()', [], (err, rows) => {
    if (err) {
      logger.error('[db] сессии не загрузились:', err.message);
      return;
    }
    rows.forEach((r) => map.set(r.token, { id: r.user_id, username: r.username, role: r.role }));
    if (rows.length) logger.info(`[db] восстановлено сессий: ${rows.length}`);
  });
}

function deleteSession(token) {
  db.run('DELETE FROM sessions WHERE token = ?', [token]);
}

function cleanupExpiredSessions() {
  db.run('DELETE FROM sessions WHERE expires_at <= now()');
}

// Выгрузка базы одним JSON — вместо копии файла SQLite, которой больше нет.
async function dumpDatabase() {
  const { rows: tables } = await pool.query(
    "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename"
  );
  const dump = {};
  for (const { tablename } of tables) {
    // Сессии не выгружаем: это действующие ключи от чужих аккаунтов.
    if (tablename === 'sessions') continue;
    const { rows } = await pool.query(`SELECT * FROM ${tablename}`);
    dump[tablename] = rows;
  }
  return dump;
}

module.exports = {
  db,
  pool,
  dbReady,
  seedDefaults,
  dumpDatabase,
  TEST_EMPLOYEES,
  TEST_TOOLS,
  hashPassword,
  verifyPassword,
  saveSession,
  loadSessionsIntoMap,
  deleteSession,
  cleanupExpiredSessions
};
