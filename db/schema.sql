CREATE TABLE IF NOT EXISTS users (
  id SERIAL PRIMARY KEY,
  nickname TEXT UNIQUE NOT NULL,
  first_name TEXT,
  last_name TEXT,
  email TEXT UNIQUE,
  password_hash TEXT,
  steam_id TEXT UNIQUE,
  avatar_url TEXT,
  role TEXT NOT NULL DEFAULT 'player' CHECK (role IN ('player', 'admin', 'superadmin')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- обычный аккаунт обязан иметь email+пароль, Steam-аккаунт — steam_id;
  -- одного из двух способов входа достаточно
  CHECK (steam_id IS NOT NULL OR (email IS NOT NULL AND password_hash IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS games (
  id SERIAL PRIMARY KEY,
  name TEXT NOT NULL,
  slug TEXT UNIQUE NOT NULL,
  description TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS servers (
  id SERIAL PRIMARY KEY,
  game_id INTEGER NOT NULL REFERENCES games(id),
  name TEXT NOT NULL,
  host TEXT NOT NULL,
  port INTEGER NOT NULL,
  description TEXT,
  -- путь к тому Docker-контейнера на VPS (только для игр, где мы умеем управлять
  -- игровыми админами напрямую через файл — сейчас только Valheim/adminlist.txt)
  docker_volume_path TEXT,
  -- нужны для реальной пересборки контейнера при смене пароля подключения
  -- (сейчас поддерживается только для Valheim)
  docker_container_name TEXT,
  docker_world_name TEXT,
  connect_password TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (host, port)
);

CREATE TABLE IF NOT EXISTS server_admins (
  id SERIAL PRIMARY KEY,
  user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (user_id, server_id)
);

CREATE TABLE IF NOT EXISTS server_status (
  id SERIAL PRIMARY KEY,
  server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
  online BOOLEAN NOT NULL,
  players_online INTEGER,
  players_max INTEGER,
  reported_name TEXT,
  checked_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS articles (
  id SERIAL PRIMARY KEY,
  title TEXT NOT NULL,
  slug TEXT UNIQUE NOT NULL,
  content TEXT NOT NULL,
  author_id INTEGER REFERENCES users(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
