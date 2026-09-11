#!/bin/bash
# Первая установка портала на VPS (Ubuntu 24.04). Запускать под root.
#
# Идемпотентный: повторный запуск ничего не ломает и не перезаписывает .env —
# секрет, сгенерированный один раз, разлогинил бы всех при замене.
#
# Postgres поднимается тем же docker compose, что и локально: одна механика в двух
# местах дешевле, чем две разные. Если докера нет и ставить его не хочется — шаг
# пропускается с подсказкой, база тогда на тебе.
set -euo pipefail

REPO=${REPO:-https://github.com/official-fedorenko/astvard.git}
BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}

say() { printf '\n== %s\n' "$1"; }

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

say "Node"
if ! command -v node >/dev/null 2>&1 || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 18 ]; then
  # В репозитории Ubuntu 24.04 лежит Node 18; backend просит >=18, так что штатного
  # хватает, и внешний репозиторий добавлять незачем.
  apt-get update -qq
  apt-get install -y -qq nodejs npm
fi
NODE_BIN=$(command -v node)
echo "   $NODE_BIN $(node -v)"

say "Пользователь $SITE_USER"
if ! id -u "$SITE_USER" >/dev/null 2>&1; then
  useradd --system --create-home --shell /usr/sbin/nologin "$SITE_USER"
fi

say "Код в $DIR (ветка $BRANCH)"
apt-get install -y -qq git
# git отказывается работать в чужом каталоге ("dubious ownership"): владелец
# репозитория — $SITE_USER, а скрипт под root. Без этой строки повторная установка
# и каждое обновление падают на первом же fetch.
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
  # openssl, а не $RANDOM: секрет подписывает сессии всех игроков.
  cat > "$DIR/.env" <<ENV
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_USER=astvard
POSTGRES_PASSWORD=$(openssl rand -hex 24)
POSTGRES_DB=astvard
PORT=3001
JWT_SECRET=$(openssl rand -hex 32)
APP_ENV=production
APP_URL=https://astvard.online
ENV
  echo "   создан, секреты сгенерированы"
fi
chown -R "$SITE_USER:$SITE_USER" "$DIR"
chmod 600 "$DIR/.env"

say "Зависимости"
if ! sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR/backend" 2>/dev/null; then
  sudo -u "$SITE_USER" npm install --omit=dev --prefix "$DIR/backend"
fi

say "Postgres"
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  (cd "$DIR" && docker compose up -d)
  # База должна быть готова раньше схемы, иначе /api/health отдаст 500 и это
  # спишут на код.
  for _ in $(seq 1 30); do
    if docker compose -f "$DIR/docker-compose.yml" exec -T postgres pg_isready -U astvard >/dev/null 2>&1; then
      break
    fi
    sleep 2
  done
  say "Схема"
  docker compose -f "$DIR/docker-compose.yml" exec -T postgres \
    psql -v ON_ERROR_STOP=1 -U astvard -d astvard < "$DIR/db/schema.sql" >/dev/null
  echo "   накатана"
else
  echo "   докера нет или демон не отвечает."
  echo "   Поставь docker (apt install docker.io docker-compose-v2) и запусти скрипт снова,"
  echo "   либо подними Postgres сам и накати $DIR/db/schema.sql."
fi

say "systemd"
install -m 644 "$DIR/deploy/systemd/astvard-backend.service" /etc/systemd/system/
# Путь к node на VPS может отличаться от того, что в юните.
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
if ! curl -fsS --max-time 5 http://127.0.0.1:3001/api/health; then
  echo "   /api/health не ответил"
  exit 1
fi
echo

say "nginx"
install -m 644 "$DIR/deploy/nginx/astvard.conf" /etc/nginx/sites-available/astvard
ln -sfn /etc/nginx/sites-available/astvard /etc/nginx/sites-enabled/astvard
# Заглушка больше не нужна, но файл оставляем на месте — вдруг понадобится вернуть.
rm -f /etc/nginx/sites-enabled/astvard-placeholder /etc/nginx/sites-enabled/default
nginx -t
systemctl reload nginx
echo "   перезагружен"

say "Готово"
echo "Проверь снаружи:"
echo "  curl -s https://astvard.online/api/health     # {\"status\":\"ok\",\"db\":\"ok\"}"
echo "Первого суперадмина назначить руками, после регистрации на сайте:"
echo "  docker compose -f $DIR/docker-compose.yml exec -T postgres \\"
echo "    psql -U astvard -d astvard -c \"UPDATE users SET role='superadmin' WHERE email='ТВОЙ_EMAIL';\""
