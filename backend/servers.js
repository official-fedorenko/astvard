const { pool } = require('./db');
const { queryA2SInfo } = require('./protocols/a2s');

function listServers() {
  return pool
    .query('SELECT * FROM servers ORDER BY created_at DESC')
    .then((r) => r.rows);
}

function createServer({ name, host, port }) {
  return pool
    .query(
      'INSERT INTO servers (name, host, port) VALUES ($1, $2, $3) RETURNING *',
      [name, host, port]
    )
    .then((r) => r.rows[0]);
}

function deleteServer(id) {
  return pool.query('DELETE FROM servers WHERE id = $1', [id]);
}

async function refreshServerStatus(server) {
  const info = await queryA2SInfo(server.host, server.port);
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
