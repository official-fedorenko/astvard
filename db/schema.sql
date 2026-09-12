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
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS catalog_models (
          id SERIAL PRIMARY KEY,
          category TEXT NOT NULL,
          brand TEXT NOT NULL,
          model TEXT NOT NULL,
          name TEXT,
          line TEXT,
          power_type TEXT,
          power_w INTEGER,
          voltage_v INTEGER,
          brushless INTEGER NOT NULL DEFAULT 0,
          impact INTEGER NOT NULL DEFAULT 0,
          chuck TEXT,
          disc_mm INTEGER,
          image_url TEXT,
          created_at TIMESTAMPTZ DEFAULT now()
          , specs TEXT
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

CREATE TABLE IF NOT EXISTS tool_categories (
      id SERIAL PRIMARY KEY,
      name TEXT UNIQUE NOT NULL,
      is_default INTEGER NOT NULL DEFAULT 0,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS tools (
      id SERIAL PRIMARY KEY,
      name TEXT NOT NULL,
      category TEXT,
      brand TEXT,
      model TEXT,
      serial_number TEXT,
      inventory_number TEXT,
      status TEXT NOT NULL DEFAULT 'available',
      purchase_date DATE,
      photo_url TEXT,
      notes TEXT,
      created_at TIMESTAMPTZ DEFAULT now()
      , specs TEXT
    );

CREATE TABLE IF NOT EXISTS users (
          id SERIAL PRIMARY KEY,
          username TEXT UNIQUE NOT NULL,
          email TEXT UNIQUE,
          password_hash TEXT,
          role TEXT NOT NULL DEFAULT 'User',
          account_type TEXT NOT NULL DEFAULT 'client',
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

CREATE TABLE IF NOT EXISTS vehicle_categories (
      id SERIAL PRIMARY KEY,
      name TEXT UNIQUE NOT NULL,
      is_default INTEGER NOT NULL DEFAULT 0,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS vehicles (
      id SERIAL PRIMARY KEY,
      name TEXT NOT NULL,
      category TEXT,
      brand TEXT,
      model TEXT,
      year INTEGER,
      plate_number TEXT,
      vin TEXT,
      fuel_type TEXT,
      mileage INTEGER,
      status TEXT NOT NULL DEFAULT 'available',
      purchase_date DATE,
      insurance_until DATE,
      inspection_until DATE,
      photo_url TEXT,
      notes TEXT,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS brands (
          id SERIAL PRIMARY KEY,
          name TEXT NOT NULL UNIQUE,
          icon_url TEXT,
          is_preset INTEGER NOT NULL DEFAULT 0,
          created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          created_at TIMESTAMPTZ DEFAULT now(),
          updated_at TIMESTAMPTZ DEFAULT now()
        );

CREATE TABLE IF NOT EXISTS category_icons (
          category TEXT PRIMARY KEY,
          image_url TEXT NOT NULL,
          updated_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          updated_at TIMESTAMPTZ DEFAULT now()
        );

CREATE TABLE IF NOT EXISTS employees (
      id SERIAL PRIMARY KEY,
      first_name TEXT NOT NULL,
      last_name TEXT NOT NULL,
      position TEXT,
      department TEXT,
      phone TEXT,
      email TEXT,
      hire_date DATE,
      status TEXT NOT NULL DEFAULT 'active',
      user_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
      notes TEXT,
      created_at TIMESTAMPTZ DEFAULT now()
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

CREATE TABLE IF NOT EXISTS requests (
          id SERIAL PRIMARY KEY,
          type TEXT NOT NULL,
          title TEXT,
          payload TEXT,
          status TEXT NOT NULL DEFAULT 'pending',
          requested_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          reviewed_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          reviewed_at TIMESTAMPTZ,
          review_note TEXT,
          result_ref TEXT,
          created_at TIMESTAMPTZ DEFAULT now()
        , received_at TIMESTAMPTZ
    );

CREATE TABLE IF NOT EXISTS support_tickets (
          ticket_id TEXT PRIMARY KEY,
          status TEXT NOT NULL DEFAULT 'open',
          closed_at TIMESTAMPTZ,
          closed_by INTEGER REFERENCES users(id) ON DELETE SET NULL
        );

CREATE TABLE IF NOT EXISTS tool_photos (
          id SERIAL PRIMARY KEY,
          tool_id INTEGER NOT NULL REFERENCES tools(id) ON DELETE CASCADE,
          photo_url TEXT NOT NULL,
          uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          created_at TIMESTAMPTZ DEFAULT now()
        );

CREATE TABLE IF NOT EXISTS tool_requests (
          id SERIAL PRIMARY KEY,
          name TEXT NOT NULL,
          category TEXT,
          brand TEXT,
          model TEXT,
          serial_number TEXT,
          inventory_number TEXT,
          photo_url TEXT,
          notes TEXT,
          status TEXT NOT NULL DEFAULT 'pending',
          requested_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          reviewed_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
          reviewed_at TIMESTAMPTZ,
          review_note TEXT,
          created_tool_id INTEGER REFERENCES tools(id) ON DELETE SET NULL,
          created_at TIMESTAMPTZ DEFAULT now()
        );

CREATE TABLE IF NOT EXISTS vehicle_photos (
      id SERIAL PRIMARY KEY,
      vehicle_id INTEGER NOT NULL REFERENCES vehicles(id) ON DELETE CASCADE,
      photo_url TEXT NOT NULL,
      uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS work_logs (
      id SERIAL PRIMARY KEY,
      user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      work_date DATE NOT NULL,
      hours DOUBLE PRECISION NOT NULL,
      note TEXT,
      created_at TIMESTAMPTZ DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS notification_reads (
          notification_id INTEGER NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
          user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          read_at TIMESTAMPTZ DEFAULT now(),
          PRIMARY KEY (notification_id, user_id)
        );

CREATE TABLE IF NOT EXISTS tool_assignments (
      id SERIAL PRIMARY KEY,
      tool_id INTEGER NOT NULL REFERENCES tools(id) ON DELETE CASCADE,
      employee_id INTEGER REFERENCES employees(id) ON DELETE SET NULL,
      issued_at TIMESTAMPTZ DEFAULT now(),
      returned_at TIMESTAMPTZ,
      issued_by TEXT,
      notes TEXT
    );

CREATE TABLE IF NOT EXISTS vehicle_assignments (
      id SERIAL PRIMARY KEY,
      vehicle_id INTEGER NOT NULL REFERENCES vehicles(id) ON DELETE CASCADE,
      employee_id INTEGER REFERENCES employees(id) ON DELETE SET NULL,
      issued_at TIMESTAMPTZ DEFAULT now(),
      returned_at TIMESTAMPTZ,
      issued_by TEXT,
      notes TEXT
    );

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_steam_id ON users(steam_id);
-- Колонки, появившиеся после первого выпуска схемы. Здесь, отдельными ALTER-ами,
-- потому что CREATE TABLE IF NOT EXISTS пропускает существующую таблицу целиком:
-- на уже заведённой базе новая колонка иначе не появится никогда.
ALTER TABLE media ADD COLUMN IF NOT EXISTS uploaded_by INTEGER REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE requests ADD COLUMN IF NOT EXISTS received_at TIMESTAMPTZ;
ALTER TABLE tools ADD COLUMN IF NOT EXISTS specs TEXT;
ALTER TABLE catalog_models ADD COLUMN IF NOT EXISTS specs TEXT;
ALTER TABLE support_messages ADD COLUMN IF NOT EXISTS read_by_user INTEGER NOT NULL DEFAULT 0;
ALTER TABLE notifications ADD COLUMN IF NOT EXISTS scheduled_at TIMESTAMPTZ;
