#!/bin/bash
# Entrypoint of the astvard-valheim container.
#
#   start   (default) run the server
#   update  install or update the game in $SERVER_DIR through SteamCMD
#
# Every flag in start() was chosen against the game's own code, and the flags that
# are NOT there are decisions too — the reasoning lives in the root CLAUDE.md,
# "Сервер: как он собран и почему именно так". In short:
#
#   -crossplay    not passed. It swaps the whole transport for PlayFab Party: no
#                 listening socket, no A2S server info at all, and the peer's
#                 identity arrives as a field the client wrote instead of a ticket
#                 verified through BeginAuthSession.
#   -preset       not passed. A preset is written INTO the world on every start and
#                 stays until -resetmodifiers.
#   seed          no flag exists. To choose one, create the world in the client and
#                 drop its folder into worlds_local.
#   -password     pointless here. The game only demands one from a public server,
#                 and this one is -public 0 with a permitted list.
#   -backups...   the same three values as start_astvard.bat on the Windows server.
set -euo pipefail

SERVER_DIR=${SERVER_DIR:-/srv/valheim/server}
SAVE_DIR=${SAVE_DIR:-/srv/valheim/saves}
LOG_DIR=${LOG_DIR:-/srv/valheim/logs}
SERVER_NAME=${SERVER_NAME:-Astvard}
SAVE_INTERVAL=${SAVE_INTERVAL:-900}
BEPINEX=${BEPINEX:-no}
# Fixed, because compose.yml publishes exactly these ports.
SERVER_PORT=2456

update() {
  # 896660 is the dedicated server. 892970, the game itself, is what the server has
  # to present as SteamAppId when it runs — see start().
  /opt/steamcmd/steamcmd.sh +force_install_dir "$SERVER_DIR" +login anonymous \
    +app_update 896660 validate +quit
  if [ ! -x "$SERVER_DIR/valheim_server.x86_64" ]; then
    echo "valheim_server.x86_64 не появился — смотри вывод SteamCMD выше." >&2
    exit 1
  fi
  grep -m1 '"buildid"' "$SERVER_DIR/steamapps/appmanifest_896660.acf" || true
}

start() {
  # WORLD_NAME is the name of the save FOLDER, and that folder name IS the world's
  # identity: SaveSystem.GetChunkedSaveName is literally the directory name. Get it
  # wrong and the server does not fail — it quietly creates a brand new world and
  # everyone spawns on an empty island. So refuse instead.
  if [ -z "${WORLD_NAME:-}" ]; then
    echo "WORLD_NAME не задан в server.env — он должен совпадать с именем папки мира." >&2
    exit 1
  fi
  local world="$SAVE_DIR/worlds_local/$WORLD_NAME"
  # A chunked 1.0 save is a folder whose finished saves are marked by _main.N.ok;
  # a world from before 1.0 is a pair of files next to the folders.
  if ! compgen -G "$world/_main.*.ok" >/dev/null && [ ! -f "$world.fwl" ]; then
    echo "Мира '$WORLD_NAME' нет в $SAVE_DIR/worlds_local — там только:" >&2
    ls -1 "$SAVE_DIR/worlds_local" >&2 || true
    echo "Не запускаюсь: иначе сервер молча создал бы новый пустой мир." >&2
    exit 1
  fi

  mkdir -p "$LOG_DIR"
  cd "$SERVER_DIR"

  # Steam's own launcher normally sets these; a server started directly does not.
  export SteamAppId=892970
  export LD_LIBRARY_PATH="$SERVER_DIR/linux64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

  if [ "$BEPINEX" = yes ]; then
    # A vanilla start when the mod is expected looks like "the buttons do nothing",
    # which is a long way from the real cause. Refuse instead.
    if [ ! -f BepInEx/core/BepInEx.Preloader.dll ] || [ ! -f doorstop_libs/libdoorstop_x64.so ]; then
      echo "BEPINEX=yes, но в $SERVER_DIR нет BepInEx/core или doorstop_libs." >&2
      exit 1
    fi
    # The same four lines as start_server_bepinex.sh from BepInExPack_Valheim
    # (doorstop 4.4.0): the preloader is injected through LD_PRELOAD.
    export DOORSTOP_ENABLED=1
    export DOORSTOP_TARGET_ASSEMBLY="$SERVER_DIR/BepInEx/core/BepInEx.Preloader.dll"
    export LD_LIBRARY_PATH="$SERVER_DIR/doorstop_libs:$LD_LIBRARY_PATH"
    export LD_PRELOAD="libdoorstop_x64.so${LD_PRELOAD:+:$LD_PRELOAD}"
  fi

  # exec, and it is not cosmetic: the server has to be the process that receives the
  # SIGINT Docker sends on stop, because Ctrl+C is the only thing that makes it
  # write the world on the way out.
  exec ./valheim_server.x86_64 \
    -nographics -batchmode \
    -name "$SERVER_NAME" \
    -port "$SERVER_PORT" \
    -world "$WORLD_NAME" \
    -public 0 \
    -savedir "$SAVE_DIR" \
    -logFile "$LOG_DIR/server.log" \
    -saveinterval "$SAVE_INTERVAL" \
    -backups 4 \
    -backupshort 3600 \
    -backuplong 43200
}

case "${1:-start}" in
  start) start ;;
  update) update ;;
  *) exec "$@" ;;
esac
