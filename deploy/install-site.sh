#!/bin/bash
# First install of the portal on the VPS (Ubuntu 24.04). Run as root.
#
# The portal is the owner's vanilla-admin-panel: Node without a framework, SQLite,
# no build step. Nothing here is installed system-wide that the machine's other
# projects might depend on — Node is checked, never replaced.
#
#   SUPERADMIN_STEAM_ID=76561198XXXXXXXXX ./deploy/install-site.sh
#
# Idempotent: a second run breaks nothing, keeps .env and never touches the database.
set -euo pipefail

REPO=${REPO:-https://github.com/official-fedorenko/astvard.git}
BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
# The database lives outside the checkout on purpose: updates run
# `git reset --hard`, and one day somebody will reach for `git clean -xdf`.
DATA_DIR=${DATA_DIR:-/srv/astvard-data}
SITE_USER=${SITE_USER:-astvard}

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

say "Данные в $DATA_DIR"
mkdir -p "$DATA_DIR"
chown -R "$SITE_USER:$SITE_USER" "$DATA_DIR"
echo "   база SQLite и то, что переживает обновление"

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
ensure_env DB_PATH "$DATA_DIR/db.sqlite"
# nginx terminates HTTPS and forwards X-Forwarded-Proto; without this the session
# cookie never gets the Secure flag.
ensure_env TRUST_PROXY true
ensure_env VALHEIM_LOG_FILE /srv/valheim/logs/server.log
ensure_env SUPERADMIN_STEAM_ID "${SUPERADMIN_STEAM_ID:-}"
chown -R "$SITE_USER:$SITE_USER" "$DIR"
chmod 600 "$DIR/.env"

say "Зависимости"
# sqlite3 is a native module. It ships prebuilt binaries for current Node on
# x86_64 Linux; if there are none for this combination npm falls back to building,
# which needs python3, make and a compiler.
if ! sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR" 2>&1 | tail -3; then
  die "npm ci не прошёл. Если он собирал sqlite3 — нужны python3, make и g++."
fi

say "systemd"
install -m 644 "$DIR/deploy/systemd/astvard-backend.service" /etc/systemd/system/
sed -i "s#^ExecStart=.*node #ExecStart=$NODE_BIN #" /etc/systemd/system/astvard-backend.service
sed -i "s#/srv/astvard-data#$DATA_DIR#g; s#/srv/astvard\b#$DIR#g" /etc/systemd/system/astvard-backend.service
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
