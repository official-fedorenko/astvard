#!/bin/bash
# First install of the portal on the VPS (Ubuntu 24.04). Run as root.
#
# The portal is the owner's vanilla-admin-panel, moved onto Postgres: Node without
# a framework, no build step. Nothing here is installed system-wide that the other
# projects on this machine might depend on — Node is checked, never replaced, and
# the database goes into the Postgres that already runs here, as its own role and
# its own database.
#
#   SUPERADMIN_STEAM_ID=76561198XXXXXXXXX POSTGRES_CONTAINER=postgres-shared \
#     ./deploy/install-site.sh
#
# Idempotent: a second run breaks nothing, keeps .env and never drops the database.
set -euo pipefail

REPO=${REPO:-https://github.com/official-fedorenko/astvard.git}
BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}
# Which container holds Postgres. Empty means "the role and the database are
# already there" — then this script only writes .env and never touches the server.
PG_CONTAINER=${POSTGRES_CONTAINER:-}
# The superuser inside that container. Connecting over its local socket needs no
# password: the official image trusts local connections.
PG_ADMIN=${POSTGRES_ADMIN_USER:-postgres}

say() { printf '\n== %s\n' "$1"; }
die() { echo "   $1" >&2; exit 1; }

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

say "Node"
command -v node >/dev/null 2>&1 || die "Node не найден. Нужен Node 18 или новее."
NODE_BIN=$(command -v node)
node -e 'process.exit(Number(process.versions.node.split(".")[0]) >= 18 ? 0 : 1)' \
  || die "$NODE_BIN $(node -v): панель просит Node 18 или новее."
echo "   $NODE_BIN $(node -v)"

say "Пользователь $SITE_USER"
if ! id -u "$SITE_USER" >/dev/null 2>&1; then
  useradd --system --create-home --shell /usr/sbin/nologin "$SITE_USER"
fi

say "Код в $DIR (ветка $BRANCH)"
command -v git >/dev/null 2>&1 || apt-get install -y -qq git
# git refuses to work in a directory owned by someone else ("dubious ownership"):
# the checkout belongs to $SITE_USER while this runs as root.
git config --global --add safe.directory "$DIR"
if [ -d "$DIR/.git" ]; then
  git -C "$DIR" fetch origin "$BRANCH"
  git -C "$DIR" checkout "$BRANCH"
  git -C "$DIR" reset --hard "origin/$BRANCH"
else
  git clone --branch "$BRANCH" "$REPO" "$DIR"
fi

say "Загрузки"
# Uploads live inside the checkout because the panel serves them from there by an
# absolute path in the code. They survive `git reset --hard` of an update — the
# directory is in .gitignore — but not `git clean -xdf`, so that one is forbidden here.
mkdir -p "$DIR/uploads"
echo "   $DIR/uploads"

say ".env"
# Keys are added one by one rather than rewritten: a second run must not replace a
# secret that is already signing sessions, and must not lose what someone tuned.
ensure_env() {
  local key="$1" value="$2"
  if grep -q "^$key=" "$DIR/.env" 2>/dev/null; then
    echo "   $key — уже есть"
  else
    echo "$key=$value" >> "$DIR/.env"
    echo "   $key — добавлен"
  fi
}
touch "$DIR/.env"
ensure_env PORT 3001
ensure_env APP_URL "https://astvard.online"
ensure_env POSTGRES_HOST 127.0.0.1
ensure_env POSTGRES_PORT 5432
ensure_env POSTGRES_USER astvard_panel
ensure_env POSTGRES_PASSWORD "$(openssl rand -hex 24)"
ensure_env POSTGRES_DB astvard_panel
# nginx terminates HTTPS and forwards X-Forwarded-Proto; without this the session
# cookie never gets the Secure flag.
ensure_env TRUST_PROXY true
ensure_env VALHEIM_LOG_FILE /srv/valheim/logs/server.log
ensure_env SUPERADMIN_STEAM_ID "${SUPERADMIN_STEAM_ID:-}"
chown -R "$SITE_USER:$SITE_USER" "$DIR"
chmod 600 "$DIR/.env"

# .env is the only place the password is written down; everything below reads it
# from there, so a second run keeps the database in step with the file.
set -a
# shellcheck disable=SC1091  # written just above, never in the repository
. "$DIR/.env"
set +a

say "Postgres"
if [ -n "$PG_CONTAINER" ]; then
  [ "$(docker inspect -f '{{.State.Running}}' "$PG_CONTAINER" 2>/dev/null)" = true ] \
    || die "контейнер $PG_CONTAINER не запущен — базу класть некуда."
  # Names go into SQL as bare identifiers, so they have to be names and not quoting
  # tricks. The alternative is escaping quotes through three levels of shell.
  for name in "$POSTGRES_USER" "$POSTGRES_DB"; do
    printf '%s' "$name" | grep -Eq '^[a-z_][a-z0-9_]*$' \
      || die "имя «$name» не годится для SQL: только строчные буквы, цифры и _"
  done
  psql_q() { docker exec -i "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PG_ADMIN" -d postgres -tAc "$1"; }

  if [ "$(psql_q "SELECT 1 FROM pg_roles WHERE rolname = '$POSTGRES_USER'")" != 1 ]; then
    psql_q "CREATE ROLE $POSTGRES_USER LOGIN"
    echo "   роль $POSTGRES_USER заведена"
  else
    echo "   роль $POSTGRES_USER — уже есть"
  fi
  # Through stdin, not -c: an argument would show the password in `ps` to everyone
  # on this machine, and the machine is shared.
  docker exec -i "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -q -U "$PG_ADMIN" -d postgres <<SQL
ALTER ROLE $POSTGRES_USER WITH LOGIN PASSWORD '$POSTGRES_PASSWORD';
SQL

  if [ "$(psql_q "SELECT 1 FROM pg_database WHERE datname = '$POSTGRES_DB'")" != 1 ]; then
    psql_q "CREATE DATABASE $POSTGRES_DB OWNER $POSTGRES_USER"
    echo "   база $POSTGRES_DB создана"
  else
    echo "   база $POSTGRES_DB — уже есть"
  fi
  # This server holds other projects' databases too, and by default any role may
  # connect to any of them. Their tables stay out of reach anyway, but there is no
  # reason to leave the door open.
  psql_q "REVOKE ALL ON DATABASE $POSTGRES_DB FROM PUBLIC" >/dev/null
else
  echo "   POSTGRES_CONTAINER не задан — считаю, что роль и база заведены заранее"
fi

say "Зависимости"
# Nothing native any more: pg is plain JavaScript, so no compiler, no python3.
if ! sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR" 2>&1 | tail -3; then
  die "npm ci не прошёл."
fi

say "Проверка базы"
# The panel would start with an unreachable database and only fail on the first
# request. Ask Postgres now, with exactly the credentials from .env: this catches a
# container that does not publish 5432 to the host and a password that did not stick.
if ! (cd "$DIR" && sudo -u "$SITE_USER" \
      --preserve-env=POSTGRES_HOST,POSTGRES_PORT,POSTGRES_USER,POSTGRES_PASSWORD,POSTGRES_DB \
      node -e 'const { Pool } = require("pg");
const pool = new Pool({ host: process.env.POSTGRES_HOST, port: Number(process.env.POSTGRES_PORT), user: process.env.POSTGRES_USER, password: process.env.POSTGRES_PASSWORD, database: process.env.POSTGRES_DB, connectionTimeoutMillis: 5000 });
pool.query("select 1").then(() => pool.end()).catch((e) => { console.error("   " + e.message); process.exit(1); });'); then
  die "подключиться к $POSTGRES_DB на $POSTGRES_HOST:$POSTGRES_PORT не вышло"
fi
echo "   $POSTGRES_USER@$POSTGRES_HOST:$POSTGRES_PORT/$POSTGRES_DB отвечает"

say "systemd"
install -m 644 "$DIR/deploy/systemd/astvard-backend.service" /etc/systemd/system/
sed -i "s#^ExecStart=.*node #ExecStart=$NODE_BIN #" /etc/systemd/system/astvard-backend.service
sed -i "s#/srv/astvard\b#$DIR#g" /etc/systemd/system/astvard-backend.service
systemctl daemon-reload
systemctl enable --now astvard-backend
systemctl restart astvard-backend
sleep 4
if ! systemctl is-active --quiet astvard-backend; then
  echo "   портал не поднялся:"
  journalctl -u astvard-backend -n 30 --no-pager
  exit 1
fi
echo "   поднят"

say "Проверка живым запросом"
curl -fsS --max-time 5 http://127.0.0.1:3001/api/health || die "/api/health не ответил"
echo

say "nginx"
# The enabled astvard vhost can be a plain file rather than a link — the placeholder
# was put there that way — and ln -sfn replaces it for good. Keep copies, and put
# them back if the new config fails nginx -t: this nginx serves the other projects
# too, and a broken file left on disk would take them down at the next reload.
BACKUP=/root/nginx-astvard-backup-$(date +%Y%m%d-%H%M%S)
mkdir -p "$BACKUP"
for f in sites-enabled/astvard sites-available/astvard; do
  if [ -e "/etc/nginx/$f" ] || [ -L "/etc/nginx/$f" ]; then
    cp -a "/etc/nginx/$f" "$BACKUP/${f%%/*}.astvard"
  fi
done
echo "   прежние файлы — в $BACKUP"
install -m 644 "$DIR/deploy/nginx/astvard.conf" /etc/nginx/sites-available/astvard
ln -sfn /etc/nginx/sites-available/astvard /etc/nginx/sites-enabled/astvard
rm -f /etc/nginx/sites-enabled/astvard-placeholder
if ! nginx -t; then
  rm -f /etc/nginx/sites-enabled/astvard
  for f in sites-enabled sites-available; do
    if [ -e "$BACKUP/$f.astvard" ] || [ -L "$BACKUP/$f.astvard" ]; then
      cp -a "$BACKUP/$f.astvard" "/etc/nginx/$f/astvard"
    fi
  done
  die "новый конфиг не прошёл nginx -t — вернул прежний, nginx не перезагружал"
fi
systemctl reload nginx
echo "   перезагружен"

say "Готово"
echo "Проверь снаружи:"
echo "  curl -s https://astvard.online/api/health     # {\"status\":\"ok\",\"db\":\"ok\"}"
if grep -q '^SUPERADMIN_STEAM_ID=.\+' "$DIR/.env"; then
  echo "Суперадмин заводится при старте по SUPERADMIN_STEAM_ID — смотри"
  echo "  journalctl -u astvard-backend -n 30 --no-pager"
  echo "Вход на сайт — «Войти через Steam» этим аккаунтом."
else
  echo "SUPERADMIN_STEAM_ID пуст: панель завела аккаунты superadmin/admin/user"
  echo "с паролем 1234qwer. Смени их немедленно или впиши SteamID в $DIR/.env,"
  echo "удали демо-аккаунты и перезапусти службу."
fi
