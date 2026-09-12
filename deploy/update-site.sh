#!/bin/bash
# Portal update on the VPS: pull the code, reinstall dependencies, restart.
# Run as root.
#
# There is no step for the database here: the panel applies db/schema.sql itself at
# startup, and the data sits in Postgres, which `git reset --hard` below cannot reach.
# Uploads are inside the checkout but in .gitignore, so the reset leaves them alone.
set -euo pipefail

BRANCH=${BRANCH:-valheim-mod}
DIR=${DIR:-/srv/astvard}
SITE_USER=${SITE_USER:-astvard}

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi
if [ ! -f "$DIR/.env" ]; then
  echo "Нет $DIR/.env — сначала install-site.sh." >&2
  exit 1
fi

echo "== Код"
# git refuses to work in a directory owned by someone else ("dubious ownership").
git config --global --add safe.directory "$DIR"
git -C "$DIR" fetch origin "$BRANCH"
BEFORE=$(git -C "$DIR" rev-parse HEAD)
git -C "$DIR" checkout "$BRANCH"
git -C "$DIR" reset --hard "origin/$BRANCH"
AFTER=$(git -C "$DIR" rev-parse HEAD)
echo "   ${BEFORE:0:7} → ${AFTER:0:7}"
chown -R "$SITE_USER:$SITE_USER" "$DIR"

echo "== Зависимости"
sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR" 2>&1 | tail -2

# The unit changes rarely, but when it does an update that ignored it would leave
# the service running yesterday's command line.
if ! cmp -s "$DIR/deploy/systemd/astvard-backend.service" /etc/systemd/system/astvard-backend.service; then
  echo "== Юнит systemd изменился — оставляю прежний"
  echo "   ExecStart и пути правит install-site.sh; запусти его, если дело в них"
fi

echo "== Перезапуск"
systemctl restart astvard-backend
sleep 4

# A live request rather than "the process exists": the panel starts happily with a
# database it cannot write and would answer 500 on /api/health.
echo "== Проверка"
if curl -fsS --max-time 5 http://127.0.0.1:3001/api/health; then
  echo
  echo "   готово"
else
  echo "   /api/health не ответил, откатываю на ${BEFORE:0:7}"
  git -C "$DIR" reset --hard "$BEFORE"
  chown -R "$SITE_USER:$SITE_USER" "$DIR"
  sudo -u "$SITE_USER" npm ci --omit=dev --prefix "$DIR" >/dev/null 2>&1 || true
  systemctl restart astvard-backend
  journalctl -u astvard-backend -n 30 --no-pager
  exit 1
fi
