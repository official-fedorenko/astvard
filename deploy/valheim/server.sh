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
# Since 22.09.2026 the optional ones are keys in server.env, because the site can
# now write a server.env for a second server. Every one of them defaults to off,
# so this server runs the same line it always did - a key set here is a decision
# somebody made, not a default that drifted:
#
#   CROSSPLAY     off. It swaps the whole transport for PlayFab Party: no
#                 listening socket, no A2S server info at all, and the peer's
#                 identity arrives as a field the client wrote instead of a ticket
#                 verified through BeginAuthSession.
#   WORLD_PRESET  off, and WORLD_MODIFIERS and WORLD_KEYS with it. All three are
#                 written INTO the world on every start and stay until
#                 -resetmodifiers, so they change the save, not the run.
#   seed          no flag exists, and no key can invent one. A world the server
#                 creates gets ten random characters from World.GenerateSeed(); to
#                 choose a seed, make the world in the client and drop its folder
#                 into worlds_local.
#   SERVER_PASSWORD  pointless while SERVER_PUBLIC=0: the game demands a password
#                 only from a public server, and refuses to start a public one
#                 without it. Entry here is the permitted list.
#   BACKUPS...    the same three values as start_astvard.bat on the Windows server.
set -euo pipefail

SERVER_DIR=${SERVER_DIR:-/srv/valheim/server}
SAVE_DIR=${SAVE_DIR:-/srv/valheim/saves}
LOG_DIR=${LOG_DIR:-/srv/valheim/logs}
SERVER_NAME=${SERVER_NAME:-Astvard}
SAVE_INTERVAL=${SAVE_INTERVAL:-900}
BEPINEX=${BEPINEX:-no}
# Every default below is what this server ran with before these keys existed, so
# an old server.env that names none of them starts exactly the same server as
# yesterday. The port has to match what the compose file publishes: nothing
# forwards a port the game binds behind Docker back.
SERVER_PORT=${SERVER_PORT:-2456}
SERVER_PUBLIC=${SERVER_PUBLIC:-0}
BACKUPS=${BACKUPS:-4}
BACKUP_SHORT=${BACKUP_SHORT:-3600}
BACKUP_LONG=${BACKUP_LONG:-43200}

update() {
  # 896660 is the dedicated server. 892970, the game itself, is what the server has
  # to present as SteamAppId when it runs — see start().
  #
  # SteamCMD can fail an app_update in a fresh container with "Missing
  # configuration" (exit 8): the very first install on the VPS did, and the same
  # command in the next container went through. So a few tries before giving up.
  local attempt
  for attempt in 1 2 3; do
    if /opt/steamcmd/steamcmd.sh +force_install_dir "$SERVER_DIR" +login anonymous \
         +app_update 896660 validate +quit; then
      break
    fi
    if [ "$attempt" -eq 3 ]; then
      echo "SteamCMD не поставил игру с трёх попыток." >&2
      exit 1
    fi
    echo "SteamCMD: попытка $attempt не удалась, пробую снова" >&2
    sleep 5
  done
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
  #
  # ALLOW_NEW_WORLD is for the first start of a brand new server, and only for
  # it: there is no world yet, and the refusal below would be the one thing in
  # the way. It is loud on purpose, and the site that writes the key also tells
  # the owner to take it out again - left in, it turns a typo in WORLD_NAME back
  # into a silent empty world.
  if [ "${ALLOW_NEW_WORLD:-no}" = yes ]; then
    echo "ALLOW_NEW_WORLD=yes: мира '$WORLD_NAME' может не быть, игра создаст его со случайным сидом." >&2
    echo "После первого запуска этот ключ надо убрать из server.env." >&2
  elif ! compgen -G "$world/_main.*.ok" >/dev/null && [ ! -f "$world.fwl" ]; then
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
  # The optional flags are collected into an array instead of being written into
  # the line below, because an absent flag is a decision here: an empty -password
  # or -preset "" is not the same as not passing it at all.
  local extra=()
  if [ "$SERVER_PUBLIC" = 1 ]; then
    # The game does not complain about a bad public password, it calls
    # Application.Quit() - so a public server without one never starts.
    if [ -z "${SERVER_PASSWORD:-}" ]; then
      echo "SERVER_PUBLIC=1 без SERVER_PASSWORD: игра с таким не запускается вовсе." >&2
      exit 1
    fi
    extra+=(-password "$SERVER_PASSWORD")
  fi
  # if/then, not `[ ... ] && ...`: a failing test makes that list return 1, and
  # under set -e the script would end there instead of skipping the flag.
  if [ "${CROSSPLAY:-no}" = yes ]; then extra+=(-crossplay); fi
  if [ -n "${INSTANCE_ID:-}" ]; then extra+=(-instanceid "$INSTANCE_ID"); fi
  if [ -n "${WORLD_PRESET:-}" ]; then extra+=(-preset "$WORLD_PRESET"); fi
  # These two are stamped INTO the world on every start and stay there until
  # -resetmodifiers: "Combat:Hard,Raids:Less" changes the save, not the run.
  if [ -n "${WORLD_MODIFIERS:-}" ]; then
    local pair
    for pair in ${WORLD_MODIFIERS//,/ }; do
      extra+=(-modifier "${pair%%:*}" "${pair##*:}")
    done
  fi
  if [ -n "${WORLD_KEYS:-}" ]; then
    local key
    for key in ${WORLD_KEYS//,/ }; do extra+=(-setkey "$key"); done
  fi

  exec ./valheim_server.x86_64 \
    -nographics -batchmode \
    -name "$SERVER_NAME" \
    -port "$SERVER_PORT" \
    -world "$WORLD_NAME" \
    -public "$SERVER_PUBLIC" \
    -savedir "$SAVE_DIR" \
    -logFile "$LOG_DIR/server.log" \
    -saveinterval "$SAVE_INTERVAL" \
    -backups "$BACKUPS" \
    -backupshort "$BACKUP_SHORT" \
    -backuplong "$BACKUP_LONG" \
    "${extra[@]}"
}

case "${1:-start}" in
  start) start ;;
  update) update ;;
  *) exec "$@" ;;
esac
