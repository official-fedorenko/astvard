#!/bin/bash
# Wrapper for the Valheim dedicated server, called by astvard-valheim.service.
#
# Every flag below was chosen against the game's own code, and the flags that are
# NOT here are decisions too — the reasoning lives in the root CLAUDE.md, section
# "Сервер: как он собран и почему именно так". In short:
#
#   -crossplay    not passed. It swaps the whole transport for PlayFab Party: no
#                 listening socket, no A2S server info at all, and the peer's
#                 identity arrives as a field the client wrote instead of a ticket
#                 verified through BeginAuthSession.
#   -preset       not passed. A preset is written INTO the world on every start and
#                 stays until -resetmodifiers. One run with -preset Hammer is why
#                 the old world still carries nobuildcost and passivemobs.
#   seed          no flag exists. A world made by the server gets ten random
#                 characters; to choose one, create the world in the client and drop
#                 the folder into the save directory.
#   -password     pointless here. The game only demands one from a public server,
#                 and this one is -public 0 with a permitted list.
#
# WORLD_NAME is the name of the save FOLDER, and that folder name IS the world's
# identity: SaveSystem.GetChunkedSaveName is literally the directory name. Get it
# wrong and the server does not fail — it quietly creates a brand new world and
# everyone spawns on an empty island.
set -euo pipefail

SERVER_DIR=${SERVER_DIR:-/srv/valheim/server}
SAVE_DIR=${SAVE_DIR:-/srv/valheim/saves}
LOG_DIR=${LOG_DIR:-/srv/valheim/logs}
SERVER_NAME=${SERVER_NAME:-Astvard}
SERVER_PORT=${SERVER_PORT:-2456}
SAVE_INTERVAL=${SAVE_INTERVAL:-900}

if [ -z "${WORLD_NAME:-}" ]; then
  echo "WORLD_NAME is not set. It must match the save folder name exactly," >&2
  echo "or the server will create a new empty world instead of loading ours." >&2
  exit 1
fi

if [ ! -d "$SAVE_DIR/worlds_local" ]; then
  echo "No $SAVE_DIR/worlds_local — the world has not been copied over yet." >&2
  echo "Refusing to start: an empty save directory means a new world." >&2
  exit 1
fi

# The world is three files sharing a name, and the folder is what identifies it.
if ! ls "$SAVE_DIR/worlds_local/$WORLD_NAME/${WORLD_NAME}"* >/dev/null 2>&1 \
   && ! ls "$SAVE_DIR/worlds_local/${WORLD_NAME}"* >/dev/null 2>&1; then
  echo "WORLD_NAME='$WORLD_NAME' matches nothing under $SAVE_DIR/worlds_local:" >&2
  ls -1 "$SAVE_DIR/worlds_local" >&2 || true
  echo "Refusing to start rather than generating a new world." >&2
  exit 1
fi

mkdir -p "$LOG_DIR"
cd "$SERVER_DIR"

# Steam's own launcher normally sets these; running the binary straight from
# systemd does not.
export SteamAppId=892970
export LD_LIBRARY_PATH="$SERVER_DIR/linux64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# exec, and it is not cosmetic: without it bash stays as PID 1 of the unit, takes
# the SIGINT systemd sends on stop, and the game never hears the one signal that
# makes it write the world. With exec the server IS the unit's process.
exec ./valheim_server.x86_64 \
  -nographics -batchmode \
  -name "$SERVER_NAME" \
  -port "$SERVER_PORT" \
  -world "$WORLD_NAME" \
  -public 0 \
  -savedir "$SAVE_DIR" \
  -logFile "$LOG_DIR/server.log" \
  -saveinterval "$SAVE_INTERVAL"
