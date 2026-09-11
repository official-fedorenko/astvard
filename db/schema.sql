CREATE TABLE IF NOT EXISTS users (
  id            SERIAL PRIMARY KEY,
  nickname      TEXT NOT NULL UNIQUE,
  email         TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  role          TEXT NOT NULL DEFAULT 'player' CHECK (role IN ('player', 'admin', 'superadmin')),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),

  -- The whitelist lives on the user rather than in a table of its own: a player
  -- has one Steam account and one standing answer about it, and the history of
  -- who asked when is not something anyone has needed yet.
  steam_id               TEXT UNIQUE,
  whitelist_status       TEXT NOT NULL DEFAULT 'none'
                           CHECK (whitelist_status IN ('none', 'pending', 'approved', 'rejected')),
  whitelist_requested_at TIMESTAMPTZ,
  whitelist_decided_at   TIMESTAMPTZ,
  whitelist_decided_by   INTEGER REFERENCES users(id) ON DELETE SET NULL,
  whitelist_note         TEXT
);

CREATE TABLE IF NOT EXISTS servers (
  id             SERIAL PRIMARY KEY,
  name           TEXT NOT NULL,
  host           TEXT NOT NULL,
  port           INTEGER NOT NULL,
  is_online      BOOLEAN,
  players        INTEGER,
  max_players    INTEGER,
  last_checked_at TIMESTAMPTZ,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (host, port)
);

-- The whitelist columns arrived after the first databases were already created,
-- and there is no migration runner here. CREATE TABLE IF NOT EXISTS skips an
-- existing table whole — columns and all — so an older database needs them added
-- explicitly. Every statement below is a no-op on a database that has them.
ALTER TABLE users ADD COLUMN IF NOT EXISTS steam_id TEXT UNIQUE;
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_status TEXT NOT NULL DEFAULT 'none'
  CHECK (whitelist_status IN ('none', 'pending', 'approved', 'rejected'));
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_requested_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_decided_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_decided_by INTEGER REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_note TEXT;
