const { readJsonBody } = require('../util/body');
const { listUsers, updateUserRole, findUserById } = require('../users');
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

  const id = Number(params.id);
  if (!Number.isInteger(id) || id < 1) {
    return sendJson(res, 400, { error: 'Некорректный идентификатор' });
  }

  // Without this an admin could simply promote themselves: the route is open to
  // admins, and superadmin was just another valid string.
  if (id === req.user.sub) {
    return sendJson(res, 400, { error: 'Нельзя менять собственную роль' });
  }

  // The rank has to be read from the database, not from the caller's token — a
  // token keeps the role it was signed with for a week after a demotion.
  const actor = await findUserById(req.user.sub);
  if (!actor) {
    return sendJson(res, 401, { error: 'Не авторизован' });
  }

  const target = await findUserById(id);
  if (!target) {
    return sendJson(res, 404, { error: 'Пользователь не найден' });
  }

  // Only a superadmin may hand out that rank, or take it away.
  const touchesSuperadmin = body.role === 'superadmin' || target.role === 'superadmin';
  if (touchesSuperadmin && actor.role !== 'superadmin') {
    return sendJson(res, 403, { error: 'Роль суперадмина меняет только суперадмин' });
  }

  const user = await updateUserRole(id, body.role);
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
