const { readJsonBody } = require('../util/body');
const { listUsers, updateUserRole } = require('../users');
const { listServers, createServer, deleteServer, refreshServerStatus, refreshAllServers } = require('../servers');

const VALID_ROLES = ['player', 'admin', 'superadmin'];

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

function requireRole(...roles) {
  return (handler) => (req, res, params) => {
    if (!req.user || !roles.includes(req.user.role)) {
      return sendJson(res, 403, { error: 'Недостаточно прав' });
    }
    return handler(req, res, params);
  };
}

async function getUsers(req, res) {
  const users = await listUsers();
  sendJson(res, 200, { users });
}

async function patchUserRole(req, res, params) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  if (!VALID_ROLES.includes(body.role)) {
    return sendJson(res, 400, { error: 'Некорректная роль' });
  }
  const user = await updateUserRole(Number(params.id), body.role);
  if (!user) return sendJson(res, 404, { error: 'Пользователь не найден' });
  sendJson(res, 200, { user });
}

async function getServers(req, res) {
  const servers = await listServers();
  sendJson(res, 200, { servers });
}

async function postServer(req, res) {
  let body;
  try {
    body = await readJsonBody(req);
  } catch {
    return sendJson(res, 400, { error: 'Некорректный запрос' });
  }
  const { name, host, port } = body;
  if (!name || !host || !Number.isInteger(port)) {
    return sendJson(res, 400, { error: 'name, host, port обязательны' });
  }
  const server = await createServer({ name, host, port });
  await refreshServerStatus(server);
  sendJson(res, 201, { server });
}

async function removeServer(req, res, params) {
  await deleteServer(Number(params.id));
  sendJson(res, 200, { ok: true });
}

async function refreshServers(req, res) {
  const servers = await refreshAllServers();
  sendJson(res, 200, { servers });
}

module.exports = {
  requireRole,
  getUsers,
  patchUserRole,
  getServers,
  postServer,
  removeServer,
  refreshServers,
};
