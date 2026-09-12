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

// Written once, when the server finishes loading the world. It is the only sign of
// life during the first ten minutes: the heartbeat above comes at that interval, so
// a server restarted a minute ago has none yet and used to read as offline — which
// is exactly when somebody is looking at the page.
const STARTED_RE = /Game server connected/g;

// After a clean shutdown the log is history, not a heartbeat that merely went quiet.
const SHUTDOWN_RE = /(OnApplicationQuit|ZNet Shutdown)/g;

// Who is on the server right now, and this is the thing the heartbeat cannot say.
// The game prints an arrival and a departure at the moment they happen, with the
// bare SteamID64 — the same number our users carry (users.steam_id), so a number
// becomes a person without asking Steam anything.
//
// The count from the heartbeat is ten minutes stale at worst; these two lines are
// exact. Someone who came and left between heartbeats never showed up on the site
// at all before this.
const JOINED_RE = /Got connection SteamID (\d+)/g;
const LEFT_RE = /Closing socket (\d+)/g;

// The world's own numbers, for the page: which save the world is on and which build
// the server runs. Both are printed by the game, both are free to read.
const SAVE_RE = /=> Save number (\d+)/g;
// The Linux build prefixes its own letter: "Valheim version: l-1.0.12 (network
// version 40)". The number is what we show, so everything before it is skipped.
const VERSION_RE = /Valheim version: \D*(\d[\d.]*)/g;

// The game truncates this file at every start (-logFile in deploy/valheim/server.sh),
// so after hours of play it is still tens of kilobytes and reading it whole is the
// honest way to know who is on: the arrivals we would cut off are exactly the people
// who have been playing longest. The cap is there only so a log that somehow grew
// (a crash loop printing stack traces) cannot be read into memory in full.
const MAX_READ_BYTES = 8 * 1024 * 1024;

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

/**
 * Who is on the server, by walking arrivals and departures in the order the game
 * wrote them. A Set rather than a count: the same player reconnecting must not
 * count twice, and a departure must remove the right person.
 *
 * The list is only as complete as the part of the file we read; when the log was
 * cut short, the caller still has the heartbeat count to fall back on.
 */
function playersOnline(text) {
  const events = [];
  let m;

  JOINED_RE.lastIndex = 0;
  while ((m = JOINED_RE.exec(text)) !== null) events.push({ at: m.index, id: m[1], joined: true });

  LEFT_RE.lastIndex = 0;
  while ((m = LEFT_RE.exec(text)) !== null) events.push({ at: m.index, id: m[1], joined: false });

  events.sort((a, b) => a.at - b.at);

  const online = new Set();
  for (const e of events) {
    if (e.joined) online.add(e.id);
    else online.delete(e.id);
  }
  return { ids: [...online], sawEvents: events.length > 0 };
}

// Freshness is judged by the file's own modification time rather than by the
// timestamps inside it: those are written in whatever timezone the container runs
// in, and a timezone that changes under us would quietly turn a live server off.
async function readValheimLogStatus(logPath) {
  let handle;
  try {
    handle = await fs.open(logPath, 'r');
    const { size, mtimeMs } = await handle.stat();
    const length = Math.min(MAX_READ_BYTES, size);
    const buffer = Buffer.alloc(length);
    await handle.read(buffer, 0, length, size - length);
    const tail = buffer.toString('utf8');

    const heartbeat = lastMatch(tail, HEARTBEAT_RE);
    const started = lastMatch(tail, STARTED_RE);
    const shutdown = lastMatch(tail, SHUTDOWN_RE);

    // Whichever sign of life came last in the file wins; a shutdown after it means
    // the server is gone rather than quiet.
    const alive = [heartbeat, started]
      .filter(Boolean)
      .sort((a, b) => a.index - b.index)
      .pop();
    const stopped = shutdown && (!alive || shutdown.index > alive.index);
    const fresh = Date.now() - mtimeMs < STALE_MS;

    if (stopped || !fresh || !alive) {
      return { online: false };
    }

    const { ids, sawEvents } = playersOnline(tail);
    const save = lastMatch(tail, SAVE_RE);
    const version = lastMatch(tail, VERSION_RE);

    // Arrivals and departures are exact, so they answer first. Without a single one
    // of them in the file (a log we only saw the end of) the ten-minute heartbeat is
    // all there is, and between the start and the first heartbeat the count is
    // genuinely unknown — saying "0 players" then would be a number nobody measured.
    const players = sawEvents
      ? ids.length
      : (alive === heartbeat ? Number(heartbeat[1]) : null);

    return {
      online: true,
      players,
      maxPlayers: MAX_PLAYERS,
      steamIds: ids,
      saveNumber: save ? Number(save[1]) : null,
      gameVersion: version ? version[1] : null
    };
  } catch {
    // No log, no permission, a directory in its place: all of them mean the same
    // thing to a player looking at the page.
    return { online: false };
  } finally {
    await handle?.close();
  }
}

module.exports = { readValheimLogStatus };
