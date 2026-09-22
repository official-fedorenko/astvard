-- Схема портала для Postgres. Переведена из SQLite панели 12.09.2026 механически:
-- INTEGER PRIMARY KEY AUTOINCREMENT → SERIAL, DATETIME → TIMESTAMPTZ,
-- CURRENT_TIMESTAMP → now(); остальное осталось как было.
--
-- Порядок таблиц — по зависимостям, а не по алфавиту: Postgres, в отличие от
-- SQLite, проверяет REFERENCES при создании, и таблица со ссылкой не встанет
-- раньше той, на которую ссылается.
--
-- Накатывается целиком при каждом старте, поэтому каждый оператор идемпотентен.
-- Новая колонка добавляется сюда же, отдельным ALTER ... ADD COLUMN IF NOT EXISTS
-- в конце файла: миграций с номерами здесь нет намеренно — база заведена с нуля
-- 12.09.2026, а нумерованная цепочка осталась в истории вместе со SQLite.

CREATE TABLE IF NOT EXISTS articles (
      id SERIAL PRIMARY KEY,
      title TEXT NOT NULL,
      content TEXT,
      status TEXT NOT NULL DEFAULT 'draft',
      created_at TIMESTAMPTZ DEFAULT now(),
      updated_at TIMESTAMPTZ
    );

CREATE TABLE IF NOT EXISTS logs (
      id SERIAL PRIMARY KEY,
      -- Не `user`: в Postgres это зарезервированное слово, и колонку с таким
      -- именем пришлось бы брать в кавычки в каждом запросе.
      username TEXT NOT NULL,
      action TEXT NOT NULL,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        description TEXT,
        applied_at TIMESTAMPTZ DEFAULT now()
      );

CREATE TABLE IF NOT EXISTS servers (
      id SERIAL PRIMARY KEY,
      name TEXT NOT NULL,
      host TEXT NOT NULL,
      port INTEGER NOT NULL,
      probe TEXT NOT NULL DEFAULT 'a2s',
      slot TEXT,
      is_online INTEGER,
      players INTEGER,
      max_players INTEGER,
      last_checked_at TIMESTAMPTZ,
      created_at TIMESTAMPTZ DEFAULT now(),
      UNIQUE (host, port)
    );

CREATE TABLE IF NOT EXISTS sessions (
      token TEXT PRIMARY KEY,
      user_id INTEGER,
      username TEXT,
      role TEXT,
      expires_at TIMESTAMPTZ
    );

CREATE TABLE IF NOT EXISTS settings (
      key TEXT PRIMARY KEY,
      value TEXT,
      description TEXT
    );

CREATE TABLE IF NOT EXISTS support_messages (
      id SERIAL PRIMARY KEY,
      ticket_id TEXT NOT NULL,
      user_id INTEGER,
      name TEXT,
      email TEXT,
      message TEXT NOT NULL,
      sender_role TEXT NOT NULL,
      is_read INTEGER DEFAULT 0,
      image_url TEXT,
      created_at TIMESTAMPTZ DEFAULT now()
    , read_by_user INTEGER NOT NULL DEFAULT 0
    );

CREATE TABLE IF NOT EXISTS users (
          id SERIAL PRIMARY KEY,
          username TEXT UNIQUE NOT NULL,
          email TEXT UNIQUE,
          password_hash TEXT,
          role TEXT NOT NULL DEFAULT 'User',
          avatar_url TEXT,
          two_factor_secret TEXT,
          two_factor_enabled INTEGER NOT NULL DEFAULT 0,
          created_at TIMESTAMPTZ DEFAULT now(),
          steam_id TEXT,
          steam_id_verified INTEGER NOT NULL DEFAULT 0,
          whitelist_status TEXT NOT NULL DEFAULT 'none',
          whitelist_requested_at TIMESTAMPTZ,
          whitelist_decided_at TIMESTAMPTZ,
          whitelist_decided_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          whitelist_note TEXT,
          whitelist_request_note TEXT,
          server_admin INTEGER NOT NULL DEFAULT 0,
          CHECK ((email IS NOT NULL AND password_hash IS NOT NULL) OR steam_id IS NOT NULL)
        );

CREATE TABLE IF NOT EXISTS media (
      id SERIAL PRIMARY KEY,
      filename TEXT NOT NULL,
      file_path TEXT NOT NULL,
      file_size INTEGER NOT NULL,
      mime_type TEXT NOT NULL,
      category TEXT NOT NULL DEFAULT 'general',
      created_at TIMESTAMPTZ DEFAULT now()
    , uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL
    );

CREATE TABLE IF NOT EXISTS notifications (
          id SERIAL PRIMARY KEY,
          user_id INTEGER REFERENCES users(id) ON DELETE CASCADE,
          message TEXT NOT NULL,
          created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          created_at TIMESTAMPTZ DEFAULT now(),
      scheduled_at TIMESTAMPTZ
    );

CREATE TABLE IF NOT EXISTS support_tickets (
          ticket_id TEXT PRIMARY KEY,
          status TEXT NOT NULL DEFAULT 'open',
          closed_at TIMESTAMPTZ,
          closed_by INTEGER REFERENCES users(id) ON DELETE SET NULL
        );

CREATE TABLE IF NOT EXISTS notification_reads (
          notification_id INTEGER NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
          user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          read_at TIMESTAMPTZ DEFAULT now(),
          PRIMARY KEY (notification_id, user_id)
        );

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_steam_id ON users(steam_id);
-- Колонки, появившиеся после первого выпуска схемы. Здесь, отдельными ALTER-ами,
-- потому что CREATE TABLE IF NOT EXISTS пропускает существующую таблицу целиком:
-- на уже заведённой базе новая колонка иначе не появится никогда.
ALTER TABLE media ADD COLUMN IF NOT EXISTS uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE support_messages ADD COLUMN IF NOT EXISTS read_by_user INTEGER NOT NULL DEFAULT 0;
ALTER TABLE notifications ADD COLUMN IF NOT EXISTS scheduled_at TIMESTAMPTZ;
-- When an article last changed, for sitemap.xml and dateModified. NULL on rows
-- written before the column: those fall back to created_at.
ALTER TABLE articles ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
ALTER TABLE servers ADD COLUMN IF NOT EXISTS slot TEXT;

-- What players may build on the game server (src/gameBuilds.js): the mod's rules for
-- players and who may build each shared template. The mod pulls this by token and
-- pushes back what an admin changed in game. Every change takes the next revision from
-- game_build_sync, and that order is what "the last change wins" means.
CREATE TABLE IF NOT EXISTS game_build_rules (
      key TEXT PRIMARY KEY,
      value INTEGER NOT NULL,
      limit_value INTEGER,
      revision INTEGER NOT NULL DEFAULT 0,
      updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
      updated_by TEXT
    );

-- What each rule is, as the running mod describes it on start: the admin page draws
-- exactly the rules the server knows.
CREATE TABLE IF NOT EXISTS game_build_rule_meta (
      key TEXT PRIMARY KEY,
      sort_order INTEGER NOT NULL DEFAULT 0,
      title TEXT NOT NULL,
      grp TEXT NOT NULL,
      kind TEXT NOT NULL,
      limit_min INTEGER,
      limit_max INTEGER,
      limit_word TEXT,
      note TEXT
    );

-- players: SteamID64 numbers, comma separated, the way the mod writes them into «#allow».
CREATE TABLE IF NOT EXISTS game_template_access (
      name TEXT PRIMARY KEY,
      for_all BOOLEAN NOT NULL DEFAULT false,
      players TEXT NOT NULL DEFAULT '',
      revision INTEGER NOT NULL DEFAULT 0,
      updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
      updated_by TEXT
    );

CREATE TABLE IF NOT EXISTS game_build_sync (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      revision INTEGER NOT NULL DEFAULT 0,
      seeded BOOLEAN NOT NULL DEFAULT false,
      mod_seen_at TIMESTAMPTZ,
      mod_applied_revision INTEGER NOT NULL DEFAULT 0
    );

INSERT INTO game_build_sync (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
-- Что админ попросил сделать с постройкой на сайте: переименовать, сменить категорию,
-- убрать. Сайт этого сделать не может — файлы построек пишет только мод, и прав на его
-- папку у сайта нет, — поэтому просьба лежит здесь с номером, а мод возвращает номер,
-- когда сделал. Строка живёт от просьбы до «сделано» и не дольше.
CREATE TABLE IF NOT EXISTS game_build_jobs (
      id SERIAL PRIMARY KEY,
      kind TEXT NOT NULL,
      name TEXT NOT NULL,
      value TEXT NOT NULL DEFAULT '',
      revision INTEGER NOT NULL DEFAULT 0,
      made_at TIMESTAMPTZ NOT NULL DEFAULT now(),
      made_by TEXT
    );


-- Куда сортировщик кладёт предмет (src/gameSorting.js). Каталог присылает сам мод при
-- каждом старте: какие в игре предметы, как они зовутся и что мод положил бы сам. Выбор
-- админа живёт в category, и NULL там значит «решает мод» — это не то же самое, что 0
-- («Разное»), и путать их значит тихо менять раскладку на всей базе.
CREATE TABLE IF NOT EXISTS game_sort_items (
      kind TEXT PRIMARY KEY,
      title TEXT NOT NULL DEFAULT '',
      item_type TEXT NOT NULL DEFAULT '',
      mod_category INTEGER NOT NULL DEFAULT 0,
      category INTEGER,
      revision INTEGER NOT NULL DEFAULT 0,
      updated_at TIMESTAMPTZ,
      updated_by TEXT
    );

-- Категории сортировщика. Номер — это то, что лежит в самом сундуке в игре, поэтому
-- он выдаётся раз и навсегда: переименовать можно, а сдвинуть нельзя, иначе стена
-- помеченных сундуков разом станет хранить не то, что на ней написано. Убранная
-- категория остаётся строкой с removed = true — её номер не переиспользуется никогда.
CREATE TABLE IF NOT EXISTS game_sort_categories (
      id INTEGER PRIMARY KEY,
      title TEXT NOT NULL,
      built_in BOOLEAN NOT NULL DEFAULT false,
      removed BOOLEAN NOT NULL DEFAULT false,
      revision INTEGER NOT NULL DEFAULT 0,
      updated_at TIMESTAMPTZ,
      updated_by TEXT
    );

CREATE TABLE IF NOT EXISTS game_sort_sync (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      revision INTEGER NOT NULL DEFAULT 0,
      seeded BOOLEAN NOT NULL DEFAULT false,
      categories TEXT NOT NULL DEFAULT '',
      mod_seen_at TIMESTAMPTZ,
      mod_applied_revision INTEGER NOT NULL DEFAULT 0
    );

INSERT INTO game_sort_sync (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
