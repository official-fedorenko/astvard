#!/bin/bash
# Valheim dedicated server on the VPS, in Docker. Run as root from the checkout:
#
#   ./deploy/valheim/install.sh
#
# Builds the image and installs the game, but does NOT start the server: without
# the world in place the game would create a new, empty one. Copy the world first
# (deploy/README.md), then `docker compose -f deploy/valheim/compose.yml up -d`.
#
# Safe to re-run; server.env is never overwritten. It also serves as the update:
# it refuses while the server is running, because SteamCMD would be replacing
# files under a live process.
set -euo pipefail

GAME_USER=${GAME_USER:-valheim}
HERE=$(cd "$(dirname "$0")" && pwd)
COMPOSE=(docker compose -f "$HERE/compose.yml")

say() { printf '\n== %s\n' "$1"; }

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi
if [ "$(docker inspect -f '{{.State.Running}}' astvard-valheim 2>/dev/null)" = true ]; then
  echo "Сервер запущен — останови его (docker compose -f $HERE/compose.yml stop) и запусти меня снова." >&2
  exit 1
fi

say "Память"
TOTAL_MB=$(free -m | awk '/^Mem:/ {print $2}')
echo "   на машине ${TOTAL_MB} МБ"
if [ "$TOTAL_MB" -lt 3000 ]; then
  echo "   Valheim держит мир целиком в памяти и на живом мире просит 2-4 ГБ только под"
  echo "   себя. На ${TOTAL_MB} МБ его прибьёт OOM, а прибитый процесс теряет всё после"
  echo "   последнего автосохранения. Останови меня (Ctrl+C), если это не осознанно. 15 с."
  sleep 15
fi

say "Пользователь $GAME_USER"
if ! id -u "$GAME_USER" >/dev/null 2>&1; then
  useradd --system --create-home --shell /usr/sbin/nologin "$GAME_USER"
fi
echo "   uid $(id -u "$GAME_USER"), gid $(id -g "$GAME_USER")"
# Compose reads .env next to compose.yml for the build arguments.
printf 'VALHEIM_UID=%s\nVALHEIM_GID=%s\n' "$(id -u "$GAME_USER")" "$(id -g "$GAME_USER")" > "$HERE/.env"

say "Папки"
mkdir -p /srv/valheim/server /srv/valheim/saves/worlds_local /srv/valheim/logs
chown -R "$GAME_USER:$GAME_USER" /srv/valheim
if [ -f /srv/valheim/server.env ]; then
  echo "   server.env уже есть — не трогаю"
else
  install -m 640 -o "$GAME_USER" -g "$GAME_USER" "$HERE/server.env.example" /srv/valheim/server.env
  echo "   создан /srv/valheim/server.env — сверь в нём WORLD_NAME с именем папки мира"
fi

say "Образ"
"${COMPOSE[@]}" build

say "Игра через SteamCMD"
"${COMPOSE[@]}" run --rm valheim update

say "Порты"
# Docker opens published ports in iptables on its own. Only a host firewall that
# filters before Docker's chains needs telling.
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
  ufw allow 2456:2457/udp comment 'Valheim'
  echo "   2456:2457/udp открыты в ufw"
else
  echo "   ufw не активен; если у хостера свой фильтр — открой там 2456:2457/udp"
fi

say "Дальше"
echo "1. Положить мир в /srv/valheim/saves/worlds_local/<имя папки> — deploy/README.md."
echo "2. Сверить WORLD_NAME в /srv/valheim/server.env с именем этой папки."
echo "3. ${COMPOSE[*]} up -d"
echo "4. tail -f /srv/valheim/logs/server.log — ждём 'Game server connected'."
