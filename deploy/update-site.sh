#!/bin/bash
# Обновление портала на VPS: подтянуть код, накатить схему, перезапустить.
# Запускать под root.
set -euo pipefail

BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

echo "== Код"
git -C "$DIR" fetch origin "$BRANCH"
BEFORE=$(git -C "$DIR" rev-parse HEAD)
git -C "$DIR" checkout "$BRANCH"
git -C "$DIR" reset --hard "origin/$BRANCH"
AFTER=$(git -C "$DIR" rev-parse HEAD)
echo "   ${BEFORE:0:7} → ${AFTER:0:7}"
chown -R "$SITE_USER:$SITE_USER" "$DIR"

echo "== Зависимости"
if ! sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR/backend" 2>/dev/null; then
  sudo -u "$SITE_USER" npm install --omit=dev --prefix "$DIR/backend"
fi

# Схема идемпотентна и рассчитана на накат поверх живой базы — см. CLAUDE.md.
echo "== Схема"
docker compose -f "$DIR/docker-compose.yml" exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U astvard -d astvard < "$DIR/db/schema.sql" >/dev/null
echo "   накатана"

echo "== Перезапуск"
systemctl restart astvard-backend
sleep 3

# Проверяем живым запросом, а не тем, что процесс есть: бэкенд поднимается и с
# мёртвой базой, отдавая 500 на /api/health.
echo "== Проверка"
if curl -fsS --max-time 5 http://127.0.0.1:3001/api/health; then
  echo
  echo "   готово"
else
  echo "   /api/health не ответил, откатываю на ${BEFORE:0:7}"
  git -C "$DIR" reset --hard "$BEFORE"
  chown -R "$SITE_USER:$SITE_USER" "$DIR"
  systemctl restart astvard-backend
  journalctl -u astvard-backend -n 20 --no-pager
  exit 1
fi
