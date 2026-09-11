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

# Схему накатить обязательно: свежий код может опираться на колонки, которых в базе
# ещё нет. Поэтому способ выясняем ДО того, как что-то менять — иначе останется
# полусостояние: файлы новые, сервис на старом коде, база без колонок.
compose_postgres_up() {
  command -v docker >/dev/null 2>&1 \
    && docker info >/dev/null 2>&1 \
    && docker compose -f "$DIR/docker-compose.yml" ps --status running 2>/dev/null | grep -q postgres
}

if compose_postgres_up; then
  SCHEMA_VIA=docker
elif command -v psql >/dev/null 2>&1; then
  SCHEMA_VIA=psql
else
  echo "Нечем накатить схему: контейнер postgres не запущен и psql не установлен." >&2
  echo "Ничего не менял. Подними базу (docker compose -f $DIR/docker-compose.yml up -d)" >&2
  echo "или поставь postgresql-client, и запусти снова." >&2
  exit 1
fi
echo "== Схему накатываю через: $SCHEMA_VIA"

apply_schema() {
  if [ "$SCHEMA_VIA" = docker ]; then
    docker compose -f "$DIR/docker-compose.yml" exec -T postgres \
      psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" < "$DIR/db/schema.sql" >/dev/null
  else
    PGPASSWORD="$POSTGRES_PASSWORD" PGOPTIONS="-c client_min_messages=warning" \
      psql -v ON_ERROR_STOP=1 \
      -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
      -f "$DIR/db/schema.sql" >/dev/null
  fi
}

# Креды лежат в том же .env, что читает бэкенд, — второго списка значений нет.
if [ ! -f "$DIR/.env" ]; then
  echo "Нет $DIR/.env — сначала install-site.sh." >&2
  exit 1
fi
set -a
# shellcheck disable=SC1091  # файл появляется при установке, не в репозитории
. "$DIR/.env"
set +a

echo "== Код"
# git отказывается работать в чужом каталоге ("dubious ownership"): владелец
# репозитория — $SITE_USER, а скрипт под root. Без этой строки повторная установка
# и каждое обновление падают на первом же fetch.
git config --global --add safe.directory "$DIR"
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
apply_schema
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
