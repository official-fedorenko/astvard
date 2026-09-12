const sqlite3 = require('sqlite3').verbose();
const path = require('path');
const crypto = require('crypto');
const logger = require('./src/logger');

// Allows tests to point at an isolated, disposable database file instead of
// the real db.sqlite (which would otherwise get seeded/mutated by every test run).
const dbPath = process.env.DB_PATH ? path.resolve(process.env.DB_PATH) : path.join(__dirname, 'db.sqlite');
const db = new sqlite3.Database(dbPath);

// Resolves once schema creation + default-data seeding below has finished.
// server.js doesn't wait on this (by the time a real request arrives it's
// long done), but tests that fire requests immediately after startup need it
// to avoid racing the initial seed.
let resolveDbReady;
const dbReady = new Promise((resolve) => { resolveDbReady = resolve; });

// === Простая система миграций (для надёжности) ===
const MIGRATIONS = [
  {
    version: 1,
    description: 'Initial schema + default data',
    up: () => {
      // The existing CREATEs and seeds are run below.
      // This migration is considered applied on first run.
    }
  },
  {
    version: 2,
    description: 'Add image_url to support_messages for chat images',
    up: () => {
      db.run("ALTER TABLE support_messages ADD COLUMN image_url TEXT", () => {});
    }
  },
  {
    version: 3,
    description: 'Add two-factor auth columns to users',
    up: () => {
      db.run("ALTER TABLE users ADD COLUMN two_factor_secret TEXT", () => {});
      db.run("ALTER TABLE users ADD COLUMN two_factor_enabled INTEGER NOT NULL DEFAULT 0", () => {});
    }
  },
  {
    version: 6,
    description: 'Add category to media',
    up: () => {
      db.run("ALTER TABLE media ADD COLUMN category TEXT NOT NULL DEFAULT 'general'", () => {});
    }
  },
  {
    version: 8,
    description: 'Add uploaded_by to media',
    up: () => {
      db.run("ALTER TABLE media ADD COLUMN uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL", () => {});
    }
  },
  {
    version: 17,
    description: 'Create notifications + notification_reads (внутренние уведомления от администрации)',
    up: () => {
      // Уведомления от администрации в личном кабинете. user_id = NULL —
      // уведомление для всех пользователей. Прочтения храним отдельной
      // таблицей (a не колонкой is_read на самой notifications), чтобы
      // бродкаст на всех не плодил по строке на каждого пользователя —
      // один уведомление = одна строка независимо от адресатов.
      db.run(`
        CREATE TABLE IF NOT EXISTS notifications (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          user_id INTEGER REFERENCES users(id) ON DELETE CASCADE,
          message TEXT NOT NULL,
          created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          created_at DATETIME DEFAULT CURRENT_TIMESTAMP
        )
      `, () => {});
      db.run(`
        CREATE TABLE IF NOT EXISTS notification_reads (
          notification_id INTEGER NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
          user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          read_at DATETIME DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (notification_id, user_id)
        )
      `, () => {});
    }
  },
  {
    version: 18,
    description: 'Add read_by_user to support_messages (непрочитанные ответы админа в личном чате пользователя)',
    up: () => {
      // is_read на support_messages отвечает только за «прочитано админом»
      // (используется в списке тикетов) — нельзя переиспользовать для
      // обратного направления. read_by_user — отдельная колонка: отмечает,
      // видел ли пользователь ответы администрации в своём чате.
      db.run("ALTER TABLE support_messages ADD COLUMN read_by_user INTEGER NOT NULL DEFAULT 0", () => {});
    }
  },
  {
    version: 19,
    description: 'Add scheduled_at to notifications (отложенная отправка)',
    up: () => {
      // NULL — отправлено сразу. Если задано, уведомление появляется у
      // получателя (GET /api/cabinet/notifications) только после наступления
      // этого момента — никакого фонового планировщика не нужно, просто
      // фильтр в выборке. Таблица notifications создаётся миграцией 17,
      // которая гарантированно уже отработала к этому моменту.
      db.run("ALTER TABLE notifications ADD COLUMN scheduled_at DATETIME", () => {});
    }
  },
  {
    version: 20,
    description: 'Add support_tickets (статус тикета поддержки: open/closed)',
    up: () => {
      // Тикет как отдельная сущность не хранился — только ticket_id внутри
      // support_messages. Строка здесь создаётся лениво (INSERT OR IGNORE)
      // при первом обращении к статусу тикета, а не при каждой вставке
      // сообщения.
      db.run(`
        CREATE TABLE IF NOT EXISTS support_tickets (
          ticket_id TEXT PRIMARY KEY,
          status TEXT NOT NULL DEFAULT 'open',
          closed_at DATETIME,
          closed_by INTEGER REFERENCES users(id) ON DELETE SET NULL
        )
      `, () => {});
    }
  },
  {
    version: 21,
    description: 'Steam sign-in and the game whitelist: SteamID on users, email/password optional, servers table',
    up: () => {
      // Users are handled by ensureUsersGameColumns() instead of here: a migration
      // records itself as applied the moment it is issued, and this one has to open
      // a PRAGMA first, so a crash in between left the version written and the work
      // undone — after which it would never run again. The ensure function runs at
      // every start and is safe to repeat.
      db.run(`
        CREATE TABLE IF NOT EXISTS servers (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          name TEXT NOT NULL,
          host TEXT NOT NULL,
          port INTEGER NOT NULL,
          probe TEXT NOT NULL DEFAULT 'a2s',
          is_online INTEGER,
          players INTEGER,
          max_players INTEGER,
          last_checked_at DATETIME,
          created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
          UNIQUE (host, port)
        )
      `, () => {});
    }
  },
  {
    version: 22,
    description: 'Drop leftover electrician-panel tables and users.account_type — the game portal never uses them (confirmed empty on the only real deployment)',
    up: () => {
      const deadTables = [
        'employees', 'tools', 'tool_assignments', 'tool_categories', 'tool_photos',
        'vehicles', 'vehicle_assignments', 'vehicle_categories', 'vehicle_photos',
        'work_logs', 'requests', 'tool_requests', 'catalog_models', 'brands', 'category_icons'
      ];
      deadTables.forEach((t) => db.run(`DROP TABLE IF EXISTS ${t}`, () => {}));
      // Fails silently on a fresh DB where the base CREATE TABLE never added this
      // column in the first place — same fire-and-forget pattern as the other ALTERs here.
      db.run("ALTER TABLE users DROP COLUMN account_type", () => {});
    }
  }
];

function runMigrations() {
  db.serialize(() => {
    db.run(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        description TEXT,
        applied_at DATETIME DEFAULT CURRENT_TIMESTAMP
      )
    `);

    db.get("SELECT MAX(version) as current FROM schema_migrations", (err, row) => {
      const currentVersion = row && row.current ? row.current : 0;

      MIGRATIONS.forEach(migration => {
        if (migration.version > currentVersion) {
          logger.info(`[db] Running migration ${migration.version}: ${migration.description}`);
          migration.up();
          db.run(
            "INSERT INTO schema_migrations (version, description) VALUES (?, ?)",
            [migration.version, migration.description]
          );
        }
      });

      // Вызываем здесь (а не в верхнеуровневом db.serialize() ниже), чтобы
      // PRAGMA-проверка гарантированно шла в очереди sqlite3 ПОСЛЕ операторов
      // самих миграций (включая CREATE TABLE notifications и ALTER ADD COLUMN
      // scheduled_at выше) — иначе на свежей базе она может выполниться раньше.
      ensureNotificationsScheduledAtColumn();
    });
  });
}

// Хеширование пароля с помощью встроенного модуля pbkdf2
// 1000 → подняли до 120000 итераций для лучшей стойкости (формат хранения не изменился)
function hashPassword(password) {
  const salt = crypto.randomBytes(16).toString('hex');
  const hash = crypto.pbkdf2Sync(password, salt, 120000, 64, 'sha512').toString('hex');
  return `${salt}:${hash}`;
}

// Проверка пароля (поддерживает как старые, так и новые хэши)
function verifyPassword(password, storedHash) {
  if (!storedHash || !storedHash.includes(':')) return false;
  const [salt, originalHash] = storedHash.split(':');
  const hash = crypto.pbkdf2Sync(password, salt, 120000, 64, 'sha512').toString('hex');
  // Если не совпало — попробуем со старыми 1000 итерациями (для существующих аккаунтов)
  if (hash !== originalHash) {
    const oldHash = crypto.pbkdf2Sync(password, salt, 1000, 64, 'sha512').toString('hex');
    return oldHash === originalHash;
  }
  return true;
}

// Аккаунт из Steam не имеет ни email, ни пароля, а в исходной схеме обе колонки
// были обязательными. SQLite не умеет снимать NOT NULL, поэтому таблица
// перестраивается: внешние ключи здесь не включены (PRAGMA foreign_keys), и
// именно поэтому DROP/RENAME безопасны.
//
// Функция, а не миграция: миграция отмечается выполненной в тот же миг, когда
// выдана, а тут сначала нужен ответ PRAGMA — падение между этим оставило бы базу
// старой навсегда. Эта же проверка идёт при каждом старте и повторов не боится.
function ensureUsersGameColumns() {
  db.all("PRAGMA table_info(users)", [], (err, cols) => {
    if (err || !cols || cols.length === 0) return;
    if (cols.some((c) => c.name === 'steam_id')) {
      db.run("CREATE UNIQUE INDEX IF NOT EXISTS idx_users_steam_id ON users(steam_id)", () => {});
      return;
    }

    logger.info('[db] Перестраиваю users: email и пароль станут необязательными');
    db.serialize(() => {
      db.run('BEGIN');
      db.run(`
        CREATE TABLE users_new (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          username TEXT UNIQUE NOT NULL,
          email TEXT UNIQUE,
          password_hash TEXT,
          role TEXT NOT NULL DEFAULT 'User',
          avatar_url TEXT,
          two_factor_secret TEXT,
          two_factor_enabled INTEGER NOT NULL DEFAULT 0,
          created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
          steam_id TEXT,
          steam_id_verified INTEGER NOT NULL DEFAULT 0,
          whitelist_status TEXT NOT NULL DEFAULT 'none',
          whitelist_requested_at DATETIME,
          whitelist_decided_at DATETIME,
          whitelist_decided_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          whitelist_note TEXT,
          whitelist_request_note TEXT,
          server_admin INTEGER NOT NULL DEFAULT 0,
          CHECK ((email IS NOT NULL AND password_hash IS NOT NULL) OR steam_id IS NOT NULL)
        )
      `, () => {});
      db.run(`
        INSERT INTO users_new
          (id, username, email, password_hash, role, avatar_url,
           two_factor_secret, two_factor_enabled, created_at)
        SELECT id, username, email, password_hash, role, avatar_url,
               two_factor_secret, two_factor_enabled, created_at
        FROM users
      `, () => {});
      db.run('DROP TABLE users', () => {});
      db.run('ALTER TABLE users_new RENAME TO users', () => {});
      db.run("CREATE UNIQUE INDEX IF NOT EXISTS idx_users_steam_id ON users(steam_id)", () => {});
      db.run('COMMIT', (commitErr) => {
        if (commitErr) {
          logger.error('[db] Перестройка users не удалась:', commitErr.message);
          db.run('ROLLBACK', () => {});
        } else {
          logger.info('[db] users перестроена');
        }
      });
    });
  });
}

// Тот же самолечащийся паттерн, что и у catalog_models.specs/requests.received_at
// (см. git history) — миграция 19 (ALTER TABLE notifications ADD COLUMN
// scheduled_at), идущая следом за CREATE TABLE notifications в той же
// последовательности миграции 17, тоже иногда отмечается применённой, но
// колонку не добавляет. Без неё падает любое чтение уведомлений в кабинете.
function ensureNotificationsScheduledAtColumn() {
  db.all("PRAGMA table_info(notifications)", (err, rows) => {
    if (err || !rows) return;
    const hasColumn = rows.some((r) => r.name === 'scheduled_at');
    if (!hasColumn) {
      db.run("ALTER TABLE notifications ADD COLUMN scheduled_at DATETIME", (alterErr) => {
        if (alterErr) logger.error('[db] Не удалось добавить notifications.scheduled_at:', alterErr.message);
        else logger.info('[db] Восстановлена отсутствовавшая колонка notifications.scheduled_at');
      });
    }
  });
}

// Инициализация базы данных
db.serialize(() => {
  runMigrations();
  // 1. Таблица пользователей
  db.run(`
    CREATE TABLE IF NOT EXISTS users (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      username TEXT UNIQUE NOT NULL,
      -- Обе пустые у того, кто пришёл через Steam: OpenID отдаёт SteamID64 и
      -- больше ничего, а выдуманный адрес — ложь в колонке. Что строка остаётся
      -- досягаемой, следит CHECK внизу.
      email TEXT UNIQUE,
      password_hash TEXT,
      role TEXT NOT NULL DEFAULT 'User',
      avatar_url TEXT,
      two_factor_secret TEXT,
      two_factor_enabled INTEGER NOT NULL DEFAULT 0,
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP,

      -- Игровая часть: номер Steam и ответ по вайтлисту сервера. steam_id_verified
      -- отличает номер, за который расписался сам Steam, от вписанного админом.
      steam_id TEXT,
      steam_id_verified INTEGER NOT NULL DEFAULT 0,
      whitelist_status TEXT NOT NULL DEFAULT 'none',
      whitelist_requested_at DATETIME,
      whitelist_decided_at DATETIME,
      whitelist_decided_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
      whitelist_note TEXT,
      whitelist_request_note TEXT,
      -- Права в игре (adminlist.txt). Это не роль на сайте: одно про портал,
      -- другое про сервер, и совпадать они не обязаны.
      server_admin INTEGER NOT NULL DEFAULT 0,

      CHECK ((email IS NOT NULL AND password_hash IS NOT NULL) OR steam_id IS NOT NULL)
    )
  `);

  // Игровые серверы и их состояние. probe — чем спрашивать: 'a2s' по сети или
  // 'valheim-log' из лога сервера на этой же машине (закрытый сервер на A2S молчит).
  db.run(`
    CREATE TABLE IF NOT EXISTS servers (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      host TEXT NOT NULL,
      port INTEGER NOT NULL,
      probe TEXT NOT NULL DEFAULT 'a2s',
      is_online INTEGER,
      players INTEGER,
      max_players INTEGER,
      last_checked_at DATETIME,
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
      UNIQUE (host, port)
    )
  `);

  // 2. Таблица медиафайлов
  db.run(`
    CREATE TABLE IF NOT EXISTS media (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      filename TEXT NOT NULL,
      file_path TEXT NOT NULL,
      file_size INTEGER NOT NULL,
      mime_type TEXT NOT NULL,
      category TEXT NOT NULL DEFAULT 'general',
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP
    )
  `);

  // 3. Таблица настроек (ключ-значение)
  db.run(`
    CREATE TABLE IF NOT EXISTS settings (
      key TEXT PRIMARY KEY,
      value TEXT,
      description TEXT
    )
  `);

  // 4. Демо-таблица статей для CRUD
  db.run(`
    CREATE TABLE IF NOT EXISTS articles (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      title TEXT NOT NULL,
      content TEXT,
      status TEXT NOT NULL DEFAULT 'draft',
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP
    )
  `);

  // 5. Таблица логов действий (Activity Log)
  db.run(`
    CREATE TABLE IF NOT EXISTS logs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      user TEXT NOT NULL,
      action TEXT NOT NULL,
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP
    )
  `);

  // Создаем администраторов и пользователя по умолчанию, если таблица пуста
  db.get("SELECT COUNT(*) as count FROM users", (err, row) => {
    if (err) {
      logger.error("Ошибка при проверке пользователей:", err);
      return;
    }

    // Аккаунты по умолчанию — с известным всему интернету паролем, и это
    // единственный способ войти в свежую панель. У Astvard способ другой:
    // SUPERADMIN_STEAM_ID заводит хозяина по его номеру Steam (src/bootstrap.js),
    // и тогда демо-аккаунты не нужны — а пароль 1234qwer на живом сайте не нужен
    // тем более.
    if (row.count === 0 && process.env.SUPERADMIN_STEAM_ID) {
      logger.info('SUPERADMIN_STEAM_ID задан — аккаунты по умолчанию не создаю');
    } else if (row.count === 0) {
      const defaultPass = '1234qwer';
      const hashedPassword = hashPassword(defaultPass);

      const usersToCreate = [
        { username: 'superadmin', email: 'superadmin@example.com', role: 'Superadmin' },
        { username: 'admin', email: 'admin@example.com', role: 'Admin' },
        { username: 'user', email: 'user@example.com', role: 'User' }
      ];

      db.serialize(() => {
        usersToCreate.forEach((u) => {
          db.run(
            "INSERT INTO users (username, email, password_hash, role) VALUES (?, ?, ?, ?)",
            [u.username, u.email, hashedPassword, u.role],
            (err) => {
              if (err) {
                logger.error(`Не удалось создать пользователя ${u.username}:`, err);
              } else {
                logger.info(`Создан аккаунт по умолчанию: ${u.username} (${u.role})`);
              }
            }
          );
        });
      });
    }
  });

  // 6. Таблица сообщений техподдержки / обратной связи (Чат)
  db.run(`
    CREATE TABLE IF NOT EXISTS support_messages (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      ticket_id TEXT NOT NULL,
      user_id INTEGER,
      name TEXT,
      email TEXT,
      message TEXT NOT NULL,
      sender_role TEXT NOT NULL,
      is_read INTEGER DEFAULT 0,
      image_url TEXT,
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP
    )
  `, () => {
    db.run("ALTER TABLE support_messages ADD COLUMN is_read INTEGER DEFAULT 0", () => {});
    db.run("ALTER TABLE support_messages ADD COLUMN image_url TEXT", () => {});
    db.run("ALTER TABLE support_messages ADD COLUMN read_by_user INTEGER NOT NULL DEFAULT 0", () => {});
  });

  // 7. Простая таблица сессий (для надёжности — переживают перезапуск сервера)
  db.run(`
    CREATE TABLE IF NOT EXISTS sessions (
      token TEXT PRIMARY KEY,
      user_id INTEGER,
      username TEXT,
      role TEXT,
      expires_at DATETIME
    )
  `);

  // Удаляем старые настройки статистики
  db.run("DELETE FROM settings WHERE key LIKE 'stat_%'");

  // Заполняем настройки сайта
  const stmt = db.prepare("INSERT OR IGNORE INTO settings (key, value, description) VALUES (?, ?, ?)");
  stmt.run("site_name", "Astvard", "Название вашего веб-ресурса");
  stmt.run("maintenance_mode", "false", "Включить/выключить режим обслуживания");
  stmt.run("allow_registration", "true", "Разрешить самостоятельную регистрацию пользователей");
  stmt.run("hero_title", "Astvard — портал игровых серверов", "Заголовок главного баннера");
  stmt.run("site_description", "Наши серверы, доступ к ним и всё, что вокруг игры.", "Описание под заголовком баннера");
  stmt.run("about_title", "Об Astvard", "О блоге: Заголовок раздела");
  stmt.run("about_subtitle", "Небольшая компания, свои серверы и свои правила", "О блоге: Подзаголовок");
  stmt.run("about_card1_title", "Свои серверы", "О блоге: Заголовок карточки 1");
  stmt.run("about_card1_text", "Valheim и то, что появится дальше. Вход по списку: доступ выдаётся вручную, чтобы на сервере были свои.", "О блоге: Текст карточки 1");
  stmt.run("about_card2_title", "Заявка в пару кликов", "О блоге: Заголовок карточки 2");
  stmt.run("about_card2_text", "Вход через Steam, кнопка «Запросить доступ» — и заявка уходит админу. Номер подтверждает сам Steam.", "О блоге: Текст карточки 2");
  stmt.run("contact_title", "Обратная связь", "Контакты: Заголовок раздела");
  stmt.run("contact_subtitle", "Вопрос по серверу или хочешь к нам — напиши", "Контакты: Подзаголовок");
  stmt.run("contact_email", "info@astvard.online", "Контакты: Электронная почта");
  stmt.run("contact_address", "astvard.online", "Контакты: Адрес");
  stmt.finalize();

  // Заполняем тестовые статьи
  db.get("SELECT COUNT(*) as count FROM articles", (err, row) => {
    if (!err && row.count === 0) {
      const stmt = db.prepare("INSERT INTO articles (title, content, status) VALUES (?, ?, ?)");
      stmt.run("Добро пожаловать в новую админку!", "Это демонстрационная статья, созданная автоматически для проверки работы CRUD панели.", "published");
      stmt.run("Черновик важной публикации", "Контент этой статьи еще не готов для публикации.", "draft");
      stmt.finalize(() => { ensureUsersGameColumns(); resolveDbReady(); });
    } else {
      ensureUsersGameColumns();
      resolveDbReady();
    }
  });

  // Тестовые сотрудники и тестовый инструмент больше НЕ добавляются
  // автоматически при старте на свежей БД. Используйте кнопки
  // «Добавить тестовых сотрудников» / «Добавить тестовые инструменты»
  // в настройках (Superadmin) — см. src/routes/testEmployees.js
  // и src/routes/testTools.js.
});

// === Простые helpers для персистентных сессий (надёжность) ===
function saveSession(token, user, ttlHours = 24) {
  const expires = new Date(Date.now() + ttlHours * 3600 * 1000).toISOString();
  db.run(
    "INSERT OR REPLACE INTO sessions (token, user_id, username, role, expires_at) VALUES (?, ?, ?, ?, ?)",
    [token, user.id, user.username, user.role, expires]
  );
}

function loadSessionsIntoMap(sessionsMap) {
  db.all("SELECT * FROM sessions WHERE expires_at > datetime('now')", [], (err, rows) => {
    if (err || !rows) return;
    rows.forEach(row => {
      sessionsMap.set(row.token, {
        id: row.user_id,
        username: row.username,
        role: row.role
      });
    });
    if (rows.length) logger.info(`[sessions] Восстановлено ${rows.length} сессий из БД`);
  });
}

function deleteSession(token) {
  db.run("DELETE FROM sessions WHERE token = ?", [token]);
}

function cleanupExpiredSessions() {
  db.run("DELETE FROM sessions WHERE expires_at <= datetime('now')");
}

module.exports = {
  db,
  dbPath,
  dbReady,
  hashPassword,
  verifyPassword,
  saveSession,
  loadSessionsIntoMap,
  deleteSession,
  cleanupExpiredSessions
};
