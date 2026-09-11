#!/bin/bash
# Игровой сервер Valheim на VPS (Ubuntu 24.04), ваниль, без мода.
# Запускать под root. Сервер НЕ запускается: сначала на него надо перенести мир,
# иначе он создаст новый и пустой. Порядок — в deploy/README.md.
set -euo pipefail

DIR=${DIR:-/srv/valheim}
GAME_USER=${GAME_USER:-valheim}
REPO_DIR=${REPO_DIR:-/srv/astvard}
STEAM_APP=896660   # Valheim dedicated server. 892970 — это сама игра, он нужен
                   # уже при запуске, как SteamAppId (см. start-valheim.sh).

if [ "$(id -u)" -ne 0 ]; then
  echo "Нужен root." >&2
  exit 1
fi

echo "== Память"
TOTAL_MB=$(free -m | awk '/^Mem:/ {print $2}')
echo "   на машине ${TOTAL_MB} МБ"
if [ "$TOTAL_MB" -lt 3000 ]; then
  echo "   Valheim держит мир целиком в памяти и на живом мире просит 2-4 ГБ ТОЛЬКО"
  echo "   под себя, а рядом уже стоят Postgres и Node. На ${TOTAL_MB} МБ он либо не"
  echo "   стартует, либо его прибьёт OOM — а прибитый процесс теряет всё после"
  echo "   последнего автосохранения, до 15 минут игры."
  echo "   Останови меня (Ctrl+C), если это не осознанное решение. 15 секунд."
  sleep 15
fi

echo "== SteamCMD"
if ! command -v steamcmd >/dev/null 2>&1; then
  add-apt-repository -y multiverse
  dpkg --add-architecture i386
  apt-get update -qq
  # Лицензию Steam иначе спрашивают в диалоге, и установка встанет насмерть.
  echo steam steam/question select "I AGREE" | debconf-set-selections
  echo steam steam/license note '' | debconf-set-selections
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq steamcmd lib32gcc-s1
fi
echo "   $(command -v steamcmd)"

echo "== Пользователь $GAME_USER"
if ! id -u "$GAME_USER" >/dev/null 2>&1; then
  useradd --system --create-home --shell /usr/sbin/nologin "$GAME_USER"
fi

echo "== Папки"
mkdir -p "$DIR/server" "$DIR/saves" "$DIR/logs"
chown -R "$GAME_USER:$GAME_USER" "$DIR"

echo "== Сервер из Steam"
sudo -u "$GAME_USER" steamcmd \
  +force_install_dir "$DIR/server" \
  +login anonymous \
  +app_update "$STEAM_APP" validate \
  +quit
if [ ! -x "$DIR/server/valheim_server.x86_64" ]; then
  echo "   valheim_server.x86_64 не появился — смотри вывод steamcmd выше." >&2
  exit 1
fi
echo "   $("$DIR/server/valheim_server.x86_64" -version 2>/dev/null | head -1 || echo 'установлен')"

echo "== Обёртка и настройки"
install -m 755 -o "$GAME_USER" -g "$GAME_USER" "$REPO_DIR/deploy/start-valheim.sh" "$DIR/start-valheim.sh"
if [ -f "$DIR/server.env" ]; then
  echo "   server.env уже есть — не трогаю"
else
  install -m 640 -o "$GAME_USER" -g "$GAME_USER" "$REPO_DIR/deploy/valheim.env.example" "$DIR/server.env"
  echo "   создан $DIR/server.env — СВЕРЬ В НЁМ WORLD_NAME с именем папки мира"
fi

echo "== systemd"
install -m 644 "$REPO_DIR/deploy/systemd/astvard-valheim.service" /etc/systemd/system/
sed -i "s#/srv/valheim#$DIR#g" /etc/systemd/system/astvard-valheim.service
# EnvironmentFile в юните не прописан, чтобы путь был один — добавляем здесь.
if ! grep -q 'EnvironmentFile=' /etc/systemd/system/astvard-valheim.service; then
  sed -i "s#^ExecStart=#EnvironmentFile=$DIR/server.env\nExecStart=#" /etc/systemd/system/astvard-valheim.service
fi
systemctl daemon-reload
echo "   юнит поставлен, но НЕ включён: сервер запускать после переноса мира"

echo "== Порты"
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
  # Valheim — UDP, и ему нужны три порта подряд: игровой, +1 для запросов и +2.
  ufw allow 2456:2458/udp comment 'Valheim'
  echo "   2456:2458/udp открыты в ufw"
else
  echo "   ufw не активен — открой 2456:2458/udp там, где у тебя фильтр"
fi

echo
echo "== Дальше"
echo "1. Перенести мир: deploy/README.md, раздел «Перенос карты»."
echo "2. Сверить WORLD_NAME в $DIR/server.env с именем папки в $DIR/saves/worlds_local."
echo "3. systemctl enable --now astvard-valheim"
echo "4. tail -f $DIR/logs/server.log — ждём 'Game server connected'."
