const { pool } = require('./db');
const { queryA2SInfo } = require('./protocols/a2s');
const { readValheimLogStatus } = require('./protocols/valheim-log');

// Only the deployment knows where the game server writes; the database keeps the
// decision to read a log at all, not the path to it.
const VALHEIM_LOG_FILE = process.env.VALHEIM_LOG_FILE || '/srv/valheim/logs/server.log';

function probeServer(server) {
  if (server.probe === 'valheim-log') {
    return readValheimLogStatus(VALHEIM_LOG_FILE);
  }
  return queryA2SInfo(server.host, server.port);
}

function listServers() {
  return pool
    .query('SELECT * FROM servers ORDER BY created_at DESC')
    .then((r) => r.rows);
}

function createServer({ name, host, port, probe }) {
  return pool
    .query(
      'INSERT INTO servers (name, host, port, probe) VALUES ($1, $2, $3, $4) RETURNING *',
      [name, host, port, probe || 'a2s']
    )
    .then((r) => r.rows[0]);
}

function deleteServer(id) {
  return pool.query('DELETE FROM servers WHERE id = $1', [id]);
}

async function refreshServerStatus(server) {
  const info = await probeServer(server);
  const { rows } = await pool.query(
    `UPDATE servers
     SET is_online = $1, players = $2, max_players = $3, last_checked_at = now()
     WHERE id = $4
     RETURNING *`,
    [info.online, info.players ?? null, info.maxPlayers ?? null, server.id]
  );
  return rows[0];
}

async function refreshAllServers() {
  const servers = await listServers();
  return Promise.all(servers.map(refreshServerStatus));
}

module.exports = { listServers, createServer, deleteServer, refreshServerStatus, refreshAllServers };
