CREATE TABLE IF NOT EXISTS users (
  id            SERIAL PRIMARY KEY,
  nickname      TEXT NOT NULL UNIQUE,
  -- Both are empty for an account that arrived through Steam: OpenID hands over a
  -- SteamID64 and nothing else, and inventing an address to satisfy a constraint
  -- would put a lie in the column. The check below is what keeps a row reachable.
  email         TEXT UNIQUE,
  password_hash TEXT,
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
  whitelist_note         TEXT,
  -- What the player wrote when asking; whitelist_note is the admin's answer back.
  whitelist_request_note TEXT,
  -- True only when Steam itself signed for this number. An admin typing one in by
  -- hand leaves it false, and the difference is shown wherever the number is: a
  -- typo in an admin's entry lands on a real stranger's account.
  steam_id_verified      BOOLEAN NOT NULL DEFAULT false,
  -- Admin rights inside the game, written to adminlist.txt. Nothing to do with
  -- `role`, which is rights on this site: a site admin who never plays has no
  -- business kicking people, and a trusted player may need to without touching
  -- the portal.
  server_admin           BOOLEAN NOT NULL DEFAULT false,

  -- Every row must keep at least one way in. Dropping NOT NULL from both columns
  -- without this would allow a row nobody — not even its owner — could log into.
  CONSTRAINT users_has_a_way_in CHECK (
    (email IS NOT NULL AND password_hash IS NOT NULL) OR steam_id IS NOT NULL
  )
);

CREATE TABLE IF NOT EXISTS servers (
  id             SERIAL PRIMARY KEY,
  name           TEXT NOT NULL,
  host           TEXT NOT NULL,
  port           INTEGER NOT NULL,
  -- How this server's status is checked. 'a2s' asks it over the network; a
  -- Valheim server with -public 0 answers nothing there, so 'valheim-log' reads the
  -- heartbeat out of the log file the server on this machine writes.
  probe          TEXT NOT NULL DEFAULT 'a2s' CHECK (probe IN ('a2s', 'valheim-log')),
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
ALTER TABLE users ADD COLUMN IF NOT EXISTS whitelist_request_note TEXT;
-- Default false on purpose: a row that already carries a number got it before this
-- column existed, and claiming Steam vouched for it would be a guess.
ALTER TABLE users ADD COLUMN IF NOT EXISTS steam_id_verified BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE users ADD COLUMN IF NOT EXISTS server_admin BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE servers ADD COLUMN IF NOT EXISTS probe TEXT NOT NULL DEFAULT 'a2s'
  CHECK (probe IN ('a2s', 'valheim-log'));

-- Steam accounts have neither of these; see the comment on the columns above.
-- DROP NOT NULL on a column that is already nullable does nothing, so this is
-- safe to re-run.
ALTER TABLE users ALTER COLUMN email DROP NOT NULL;
ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;

-- ADD CONSTRAINT has no IF NOT EXISTS, hence the lookup.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_has_a_way_in') THEN
    ALTER TABLE users ADD CONSTRAINT users_has_a_way_in CHECK (
      (email IS NOT NULL AND password_hash IS NOT NULL) OR steam_id IS NOT NULL
    );
  END IF;
END $$;
