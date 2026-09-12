#!/bin/bash
# First install of the portal on the VPS (Ubuntu 24.04). Run as root.
#
# Idempotent: a second run breaks nothing and never rewrites .env — a secret
# generated once and then replaced would log every player out.
#
# The machine is shared with other projects, so this script installs nothing
# system-wide that they might depend on: it checks Node rather than installing it,
# and on the VPS it puts the database into the Postgres that is already running
# there (POSTGRES_CONTAINER=postgres-shared) instead of starting a second one.
#
#   POSTGRES_CONTAINER=postgres-shared ./deploy/install-site.sh
set -euo pipefail

REPO=${REPO:-https://github.com/official-fedorenko/astvard.git}
BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}

say() { printf '\n== %s\n' "$1"; }
die() { echo "   $1" >&2; exit 1; }

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

say "Node"
# The unit starts the backend with --env-file, which Node has had only since 20.6.
# Ubuntu 24.04's own nodejs package is 18, so installing it would not help, and
# replacing whatever Node is already here could break the other projects' apps.
command -v node >/dev/null 2>&1 || die "Node не найден. Нужен Node 20.6 или новее."
NODE_BIN=$(command -v node)
if ! node -e 'const [a, b] = process.versions.node.split(".").map(Number); process.exit(a > 20 || (a === 20 && b >= 6) ? 0 : 1)'; then
  die "$NODE_BIN $(node -v): нужен 20.6 или новее — бэкенд запускается с --env-file."
fi
echo "   $NODE_BIN $(node -v)"

say "Пользователь $SITE_USER"
if ! id -u "$SITE_USER" >/dev/null 2>&1; then
  useradd --system --create-home --shell /usr/sbin/nologin "$SITE_USER"
fi

say "Код в $DIR (ветка $BRANCH)"
command -v git >/dev/null 2>&1 || apt-get install -y -qq git
# git refuses to work in a directory owned by someone else ("dubious ownership"):
# the checkout belongs to $SITE_USER while this runs as root. Without this line a
# repeat install and every update die on the first fetch.
git config --global --add safe.directory "$DIR"
if [ -d "$DIR/.git" ]; then
  git -C "$DIR" fetch origin "$BRANCH"
  git -C "$DIR" checkout "$BRANCH"
  git -C "$DIR" reset --hard "origin/$BRANCH"
else
  git clone --branch "$BRANCH" "$REPO" "$DIR"
fi

say ".env"
if [ -f "$DIR/.env" ]; then
  echo "   уже есть — не трогаю"
else
  # openssl rather than $RANDOM: the secret signs every player's session.
  cat > "$DIR/.env" <<ENV
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_USER=astvard
POSTGRES_PASSWORD=$(openssl rand -hex 24)
POSTGRES_DB=astvard
POSTGRES_CONTAINER=${POSTGRES_CONTAINER:-}
PORT=3001
JWT_SECRET=$(openssl rand -hex 32)
APP_ENV=production
APP_URL=https://astvard.online
SUPERADMIN_STEAM_ID=${SUPERADMIN_STEAM_ID:-}
ENV
  echo "   создан, секреты сгенерированы"
fi
chown -R "$SITE_USER:$SITE_USER" "$DIR"
chmod 600 "$DIR/.env"

set -a
# shellcheck disable=SC1091  # created above, never in the repository
. "$DIR/.env"
set +a
# shellcheck source=deploy/db.sh
. "$DIR/deploy/db.sh"

say "Зависимости"
if ! sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR/backend" 2>/dev/null; then
  sudo -u "$SITE_USER" npm install --omit=dev --prefix "$DIR/backend"
fi

say "Postgres"
command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1 \
  || die "докера нет или демон не отвечает. Поставь docker и запусти скрипт снова."
if [ -n "${POSTGRES_CONTAINER:-}" ]; then
  db_container_running "$POSTGRES_CONTAINER" \
    || die "контейнер $POSTGRES_CONTAINER не запущен — базу класть некуда."
  db_ensure_in_container
  echo "   роль и база $POSTGRES_DB в контейнере $POSTGRES_CONTAINER"
else
  # Someone else on the port means the compose container cannot bind it; better to
  # say so than to leave a half-created container behind.
  if ! db_compose_running && ss -ltnH "sport = :$POSTGRES_PORT" | grep -q .; then
    die "порт $POSTGRES_PORT уже занят: $(docker ps --filter "publish=$POSTGRES_PORT" --format '{{.Names}}' | paste -sd' '). Если там общий Postgres — POSTGRES_CONTAINER=<имя> в $DIR/.env и снова."
  fi
  (cd "$DIR" && docker compose up -d)
  # The database has to be ready before the schema, or /api/health answers 500 and
  # the code gets the blame.
  for _ in $(seq 1 30); do
    docker compose -f "$DIR/docker-compose.yml" exec -T postgres pg_isready -U "$POSTGRES_USER" >/dev/null 2>&1 && break
    sleep 2
  done
fi

say "Схема"
db_apply_schema || die "схема не накатилась"
echo "   накатана"

say "systemd"
install -m 644 "$DIR/deploy/systemd/astvard-backend.service" /etc/systemd/system/
# The node path on the VPS may differ from the one in the unit.
sed -i "s#^ExecStart=.*node #ExecStart=$NODE_BIN #" /etc/systemd/system/astvard-backend.service
sed -i "s#/srv/astvard#$DIR#g" /etc/systemd/system/astvard-backend.service
systemctl daemon-reload
systemctl enable --now astvard-backend
sleep 3
if systemctl is-active --quiet astvard-backend; then
  echo "   бэкенд поднят"
else
  echo "   бэкенд не поднялся:"
  journalctl -u astvard-backend -n 20 --no-pager
  exit 1
fi

say "Проверка бэкенда живым запросом"
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
# The placeholder's own link, if it was ever made one; its files stay in /var/www.
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
if [ -n "${SUPERADMIN_STEAM_ID:-}" ]; then
  echo "Суперадмин: SteamID $SUPERADMIN_STEAM_ID — бэкенд заводит его при старте,"
  echo "смотри journalctl -u astvard-backend. Войти на сайт — «Войти через Steam»."
else
  echo "Суперадмина нет: впиши SUPERADMIN_STEAM_ID=<SteamID64> в $DIR/.env и"
  echo "  systemctl restart astvard-backend — бэкенд заведёт его сам."
fi
