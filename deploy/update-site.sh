#!/bin/bash
# Portal update on the VPS: pull the code, apply the schema, restart. Run as root.
set -euo pipefail

BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

# Credentials live in the same .env the backend reads — there is no second list of
# values to drift.
if [ ! -f "$DIR/.env" ]; then
  echo "Нет $DIR/.env — сначала install-site.sh." >&2
  exit 1
fi
set -a
# shellcheck disable=SC1091  # created by install-site.sh, never in the repository
. "$DIR/.env"
set +a
# shellcheck source=deploy/db.sh
. "$DIR/deploy/db.sh"

# The schema has to be applied: new code may rely on columns the database does not
# have yet. So the way to apply it is worked out BEFORE anything changes — otherwise
# a failure leaves new files, the service on old code and a database without columns.
SCHEMA_VIA=$(db_mode)
if [ -z "$SCHEMA_VIA" ]; then
  echo "Нечем накатить схему: ни контейнера ${POSTGRES_CONTAINER:-postgres из compose}, ни psql." >&2
  echo "Ничего не менял. Подними базу и запусти снова." >&2
  exit 1
fi
echo "== Схему накатываю через: $SCHEMA_VIA"

echo "== Код"
# git refuses to work in a directory owned by someone else ("dubious ownership"):
# the checkout belongs to $SITE_USER while this runs as root.
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

# The schema is idempotent and meant to be applied over a live database — see CLAUDE.md.
echo "== Схема"
db_apply_schema
echo "   накатана"

echo "== Перезапуск"
systemctl restart astvard-backend
sleep 3

# A live request rather than "the process exists": the backend starts happily
# against a dead database and answers 500 on /api/health.
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
