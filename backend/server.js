const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { pool } = require('./db');
const { verifyToken } = require('./auth');
const { parseCookies } = require('./util/cookies');
const { Router } = require('./router');
const authHandlers = require('./handlers/auth');
const admin = require('./handlers/admin');
const { listServers, refreshAllServers } = require('./servers');

const PORT = process.env.PORT || 3001;
const FRONTEND_DIR = path.join(__dirname, '..', 'frontend');

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
};

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

// decodeURIComponent throws URIError on a stray percent sign, and both call sites
// sit in the synchronous part of the request listener, where a throw is an uncaught
// exception and takes the process down. "GET /%" was enough.
function safeDecode(value) {
  try {
    return decodeURIComponent(value);
  } catch {
    return null;
  }
}

function serveStatic(req, res) {
  const urlPath = safeDecode(req.url.split('?')[0]);
  if (urlPath === null) {
    res.writeHead(400, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('Bad request');
    return;
  }
  const relativePath = urlPath === '/' ? '/index.html' : urlPath;

  const filePath = path.normalize(path.join(FRONTEND_DIR, relativePath));
  if (filePath !== FRONTEND_DIR && !filePath.startsWith(FRONTEND_DIR + path.sep)) {
    res.writeHead(403);
    res.end('Forbidden');
    return;
  }

  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Not found');
      return;
    }
    const ext = path.extname(filePath);
    res.writeHead(200, { 'Content-Type': CONTENT_TYPES[ext] || 'application/octet-stream' });
    res.end(data);
  });
}

const router = new Router();

router.get('/api/health', (req, res) => {
  pool.query('SELECT 1')
    .then(() => sendJson(res, 200, { status: 'ok', db: 'ok' }))
    .catch((err) => sendJson(res, 500, { status: 'ok', db: 'error', error: err.message }));
});

router.post('/api/register', authHandlers.register);
router.post('/api/login', authHandlers.login);
router.post('/api/logout', authHandlers.logout);
router.get('/api/me', authHandlers.me);

router.get('/api/servers', (req, res) => {
  listServers().then((servers) => sendJson(res, 200, { servers }));
});

const requireAdmin = admin.requireRole('admin', 'superadmin');
router.get('/api/admin/users', requireAdmin(admin.getUsers));
router.patch('/api/admin/users/:id/role', requireAdmin(admin.patchUserRole));
router.get('/api/admin/servers', requireAdmin(admin.getServers));
router.post('/api/admin/servers', requireAdmin(admin.postServer));
router.delete('/api/admin/servers/:id', requireAdmin(admin.removeServer));
router.post('/api/admin/servers/refresh', requireAdmin(admin.refreshServers));

const server = http.createServer((req, res) => {
  // Everything here runs synchronously inside the listener, so a single throw
  // would end the process rather than the request. Belt as well as braces: the
  // two known throwers are handled above, and this catches the next one.
  try {
    const pathname = req.url.split('?')[0];

    const cookies = parseCookies(req);
    req.user = cookies.token ? verifyToken(cookies.token) : null;

    const match = router.match(req.method, pathname);
    if (match) {
      Promise.resolve(match.handler(req, res, match.params)).catch((err) => {
        console.error(err);
        sendJson(res, 500, { error: 'Internal server error' });
      });
      return;
    }

    if (pathname.startsWith('/api/')) {
      sendJson(res, 404, { error: 'Not found' });
      return;
    }

    serveStatic(req, res);
  } catch (err) {
    console.error(err);
    if (!res.headersSent) sendJson(res, 400, { error: 'Bad request' });
    else res.end();
  }
});

server.listen(PORT, () => {
  console.log(`astvard backend listening on port ${PORT}`);
});

// Periodic server-status polling (crude TCP check for now).
const STATUS_POLL_INTERVAL_MS = 30_000;
setInterval(() => {
  refreshAllServers().catch((err) => console.error('status poll failed', err));
}, STATUS_POLL_INTERVAL_MS);
