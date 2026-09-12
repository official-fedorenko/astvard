const fs = require('node:fs/promises');

// The only way to see a Valheim server that runs with -public 0: it answers no A2S
// query at all, because the game turns Steam's advertising on only for a public
// server (ZSteamMatchmaking.RegisterServer; the whole reasoning is in CLAUDE.md).
// Its own log is left, and the game writes this line into it every ten minutes.
//
// The trailing "ZDOS:" matters. The dungeon generator writes its own "Connections N"
// about doorways between rooms, and counting those as players would report a full
// server every time someone walks into a crypt.
const HEARTBEAT_RE = /Connections (\d+) ZDOS:/g;

// After a clean shutdown the log is history, not a heartbeat that merely went quiet.
const SHUTDOWN_RE = /(OnApplicationQuit|ZNet Shutdown)/g;

// Enough for many minutes of log at the end of the file; the whole file is tens of
// megabytes after a long run and is read every 30 seconds.
const TAIL_BYTES = 64 * 1024;

// The heartbeat comes every ten minutes, so fifteen still means alive. Anything
// longer would keep a dead server "online" for a quarter of an hour.
const STALE_MS = 15 * 60 * 1000;

// SteamGameServer.SetMaxPlayerCount(10) in ZSteamMatchmaking.RegisterServer — the
// game reports it to Steam, and nothing on our side can change it.
const MAX_PLAYERS = 10;

function lastMatch(text, re) {
  re.lastIndex = 0;
  let found = null;
  let m;
  while ((m = re.exec(text)) !== null) found = m;
  return found;
}

// Freshness is judged by the file's own modification time rather than by the
// timestamps inside it: those are written in whatever timezone the container runs
// in, and a timezone that changes under us would quietly turn a live server off.
async function readValheimLogStatus(logPath) {
  let handle;
  try {
    handle = await fs.open(logPath, 'r');
    const { size, mtimeMs } = await handle.stat();
    const length = Math.min(TAIL_BYTES, size);
    const buffer = Buffer.alloc(length);
    await handle.read(buffer, 0, length, size - length);
    const tail = buffer.toString('utf8');

    const heartbeat = lastMatch(tail, HEARTBEAT_RE);
    const shutdown = lastMatch(tail, SHUTDOWN_RE);
    const stopped = shutdown && (!heartbeat || shutdown.index > heartbeat.index);
    const fresh = Date.now() - mtimeMs < STALE_MS;

    if (stopped || !fresh || !heartbeat) {
      return { online: false };
    }
    return { online: true, players: Number(heartbeat[1]), maxPlayers: MAX_PLAYERS };
  } catch {
    // No log, no permission, a directory in its place: all of them mean the same
    // thing to a player looking at the page.
    return { online: false };
  } finally {
    await handle?.close();
  }
}

module.exports = { readValheimLogStatus };
