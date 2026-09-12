/**
 * Live HTTP tests for the auth/cabinet flows (login, 2FA, register, logout).
 * Runs the real server against its own Postgres database and a random free
 * port, so it touches neither the developer's data nor a server already
 * running on PORT.
 *
 * База берётся из POSTGRES_TEST_DB (по умолчанию astvard_test) — отдельная, и
 * перед прогоном она очищается: тесты рассчитывают на свежие данные, а
 * повторный запуск не должен спотыкаться о прошлый.
 *
 * TRUST_PROXY is turned on here so each test group can use a distinct
 * X-Forwarded-For IP and get its own rate-limit bucket, instead of tests
 * tripping each other's login/register rate limits.
 */

const { test, before, after } = require('node:test');
const assert = require('node:assert');

process.env.POSTGRES_DB = process.env.POSTGRES_TEST_DB || 'astvard_test';
process.env.TRUST_PROXY = 'true';
// Файлы мода: в тестах вместо настоящих берутся фикстуры того же формата, снятые
// с боевого сервера. Путь читается при загрузке маршрута, поэтому задаётся до
// require('../server').
process.env.VALHEIM_MOD_CONFIG = require('node:path').join(__dirname, 'fixtures', 'valheim-config');
// Списки доступа пишутся в папку сохранений игрового сервера. В тестах это своя
// временная папка: настоящую трогать нельзя, а проверять надо именно запись.
const os = require('node:os');
const fsSync = require('node:fs');
const SAVES_DIR = fsSync.mkdtempSync(require('node:path').join(os.tmpdir(), 'astvard-saves-'));
process.env.VALHEIM_SAVES_DIR = SAVES_DIR;

const server = require('../server');
const { db, pool, dbReady, seedDefaults, hashPassword } = require('../db');
const { totp } = require('../src/totp');

let baseUrl;
// id инструмента, который тесты публичной карточки создают для себя сами:
// автосид демо-инструмента отключён, поэтому на свежей БД tools пустая.
let publicToolId;

before(async () => {
  // Schema creation + default-user/article seeding in db.js is async — wait
  // for it, otherwise the first request or two can race an empty database.
  await dbReady;

  // Пустая база на старте: данные прошлого прогона сбили бы счёт и уникальные
  // поля. Порядок не важен — CASCADE снимает внешние ключи.
  // settings тоже: тест переключателей публичной карточки оставляет их
  // выключенными, и следующий прогон падал на наследстве прошлого.
  await pool.query(`
    TRUNCATE users, sessions, settings, articles, media, logs, notifications,
             notification_reads, support_messages, support_tickets, tools,
             tool_assignments, tool_photos, tool_requests, requests, servers,
             employees, work_logs, vehicles, vehicle_assignments, vehicle_photos
    RESTART IDENTITY CASCADE`);
  await seedDefaults();

  await new Promise((resolve, reject) => {
    server.listen(0, '127.0.0.1', (err) => (err ? reject(err) : resolve()));
  });
  const { port } = server.address();
  baseUrl = `http://127.0.0.1:${port}`;

  // Инструмент для тестов публичной карточки (/api/public/tool).
  publicToolId = await new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO tools (name, category, brand, model, serial_number,
                          inventory_number, status, purchase_date, notes)
       VALUES (?, ?, ?, ?, ?, ?, 'available', '2024-01-15', ?)`,
      ['Тестовый перфоратор', 'Перфоратор', 'Bosch', 'GBH 2-28',
       'TEST-SN-0001', 'INV-TEST-0001', 'Служебная заметка (не для публичной карточки)'],
      function (err) { err ? reject(err) : resolve(this.lastID); }
    );
  });
});

after(async () => {
  await new Promise((resolve) => server.close(() => resolve()));
  await pool.end();
});

function extractCookie(res) {
  const raw = res.headers.get('set-cookie');
  return raw ? raw.split(';')[0] : null;
}

async function api(urlPath, { method = 'GET', body, cookie, ip } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (cookie) headers.Cookie = cookie;
  if (ip) headers['X-Forwarded-For'] = ip;

  const res = await fetch(`${baseUrl}${urlPath}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined
  });

  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch (_) { /* not JSON */ }

  return { status: res.status, json, cookie: extractCookie(res) };
}

test('public articles endpoint returns the seeded article without auth', async () => {
  const { status, json } = await api('/api/public/articles');
  assert.strictEqual(status, 200);
  assert.ok(Array.isArray(json));
  assert.ok(json.some(a => a.title.includes('Добро пожаловать')));
});

test('public settings endpoint returns key/value pairs without auth', async () => {
  const { status, json } = await api('/api/public/settings');
  assert.strictEqual(status, 200);
  assert.ok(json.some(s => s.key === 'site_name'));
});

test('public tool card endpoint returns identification fields without auth and hides service data', async () => {
  const { status, json } = await api(`/api/public/tool?id=${publicToolId}`);
  assert.strictEqual(status, 200);
  assert.strictEqual(json.success, true);
  assert.ok(json.tool);
  assert.strictEqual(json.tool.id, publicToolId);
  assert.ok(json.tool.name);
  // Служебные данные не должны утекать в публичную карточку.
  assert.strictEqual(json.tool.notes, undefined);
  assert.strictEqual(json.history, undefined);
  assert.strictEqual(json.stats, undefined);
});

test('public tool card endpoint returns 404 for a missing tool', async () => {
  const { status } = await api('/api/public/tool?id=999999');
  assert.strictEqual(status, 404);
});

test('login rejects a wrong password', async () => {
  const { status, json } = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.1.1',
    body: { username: 'superadmin', password: 'wrong-password' }
  });
  assert.strictEqual(status, 401);
  assert.strictEqual(json.success, false);
});

let superadminCookie;

test('login succeeds with correct credentials, sets a session cookie, and warns about the default account', async () => {
  const { status, json, cookie } = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.1.2',
    body: { username: 'superadmin', password: '1234qwer' }
  });
  assert.strictEqual(status, 200);
  assert.strictEqual(json.success, true);
  assert.strictEqual(json.user.username, 'superadmin');
  assert.ok(json.securityWarning, 'default account should carry a security warning');
  assert.ok(cookie && cookie.startsWith('session='));
  superadminCookie = cookie;
});

test('auth/me rejects requests without a session cookie', async () => {
  const { status } = await api('/api/auth/me');
  assert.strictEqual(status, 401);
});

test('auth/me returns the logged-in user for a valid session cookie', async () => {
  const { status, json } = await api('/api/auth/me', { cookie: superadminCookie });
  assert.strictEqual(status, 200);
  assert.strictEqual(json.user.username, 'superadmin');
  assert.strictEqual(json.user.role, 'Superadmin');
});

test('cabinet/me returns the full profile for the logged-in user', async () => {
  const { status, json } = await api('/api/cabinet/me', { cookie: superadminCookie });
  assert.strictEqual(status, 200);
  assert.strictEqual(json.user.email, 'superadmin@example.com');
});

test('superadmin-only /api/users works with a superadmin session', async () => {
  const { status, json } = await api('/api/users', { cookie: superadminCookie });
  assert.strictEqual(status, 200);
  assert.ok(Array.isArray(json));
  assert.ok(json.some(u => u.username === 'superadmin'));
});

test('logout invalidates the session server-side, not just on the client', async () => {
  const loggedOut = await api('/api/auth/logout', { method: 'POST', cookie: superadminCookie });
  assert.strictEqual(loggedOut.status, 200);

  const after2 = await api('/api/cabinet/me', { cookie: superadminCookie });
  assert.strictEqual(after2.status, 401, 'the old cookie must be rejected once the session is destroyed server-side');
});

test('rapid repeated login attempts from the same IP get rate-limited', async () => {
  const first = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.2.1',
    body: { username: 'superadmin', password: 'wrong' }
  });
  assert.strictEqual(first.status, 401);

  const second = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.2.1',
    body: { username: 'superadmin', password: 'wrong' }
  });
  assert.strictEqual(second.status, 429);
});

test('check-username reports an existing username as unavailable', async () => {
  const { status, json } = await api('/api/auth/check-username?username=superadmin', { ip: '10.0.4.1' });
  assert.strictEqual(status, 200);
  assert.strictEqual(json.available, false);
});

test('check-username reports a fresh username as available', async () => {
  const { status, json } = await api('/api/auth/check-username?username=brandnewuser', { ip: '10.0.4.2' });
  assert.strictEqual(status, 200);
  assert.strictEqual(json.available, true);
});

test('full 2FA setup + login flow works end-to-end', async () => {
  const login1 = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.5.1',
    body: { username: 'user', password: '1234qwer' }
  });
  assert.strictEqual(login1.status, 200);
  assert.strictEqual(login1.json.requires2FA, undefined);
  const cookie = login1.cookie;

  const setup = await api('/api/cabinet/2fa/setup', {
    method: 'POST', cookie,
    body: { currentPassword: '1234qwer' }
  });
  assert.strictEqual(setup.status, 200);
  const { secret } = setup.json;
  assert.ok(secret);

  const verify = await api('/api/cabinet/2fa/verify', {
    method: 'POST', cookie,
    body: { code: totp(secret) }
  });
  assert.strictEqual(verify.status, 200);
  assert.strictEqual(verify.json.success, true);

  await api('/api/auth/logout', { method: 'POST', cookie });

  // Different IP for the second login so it isn't rate-limited by the first.
  const login2 = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.5.2',
    body: { username: 'user', password: '1234qwer' }
  });
  assert.strictEqual(login2.status, 200);
  assert.strictEqual(login2.json.requires2FA, true);
  assert.ok(login2.json.pendingToken);
  assert.strictEqual(login2.cookie, null, 'no session should be issued before the 2FA code is verified');

  const finish = await api('/api/auth/login-2fa', {
    method: 'POST',
    body: { pendingToken: login2.json.pendingToken, code: totp(secret) }
  });
  assert.strictEqual(finish.status, 200);
  assert.strictEqual(finish.json.success, true);
  assert.ok(finish.cookie && finish.cookie.startsWith('session='));

  const me = await api('/api/auth/me', { cookie: finish.cookie });
  assert.strictEqual(me.status, 200);
  assert.strictEqual(me.json.user.username, 'user');
});

test('admin static pages redirect to login when there is no session', async () => {
  const res = await fetch(`${baseUrl}/admin/`, { redirect: 'manual' });
  assert.strictEqual(res.status, 302);
  assert.ok(res.headers.get('location').includes('/admin/login.html'));
});

test('public tool card respects GLOBAL visibility settings and enable switch', async () => {
  // Свежий логин админа (superadmin-сессию к этому моменту уже разлогинили).
  const login = await api('/api/auth/login', {
    method: 'POST', ip: '10.0.9.9',
    body: { username: 'admin', password: '1234qwer' }
  });
  assert.strictEqual(login.status, 200);
  const cookie = login.cookie;

  // По умолчанию карточка включена и показывает все поля.
  const def = await api(`/api/public/tool?id=${publicToolId}`);
  assert.strictEqual(def.status, 200);
  assert.ok(def.json.tool.serial_number);

  // Глобально прячем серийный/инвентарный номера и статус.
  const saved = await api('/api/settings', {
    method: 'POST', cookie,
    body: {
      public_card_enabled: 'true',
      public_card_show_serial: 'false',
      public_card_show_inventory: 'false',
      public_card_show_status: 'false'
    }
  });
  assert.strictEqual(saved.status, 200);

  // Публичная карточка больше не отдаёт скрытые поля, но имя/бренд на месте.
  const pub = await api(`/api/public/tool?id=${publicToolId}`);
  assert.strictEqual(pub.status, 200);
  assert.ok(pub.json.tool.name);
  assert.ok(pub.json.tool.brand);
  assert.strictEqual(pub.json.tool.serial_number, undefined);
  assert.strictEqual(pub.json.tool.inventory_number, undefined);
  assert.strictEqual(pub.json.tool.status, undefined);

  // Глобально выключаем карточку — публичный доступ закрыт (404).
  const off = await api('/api/settings', {
    method: 'POST', cookie, body: { public_card_enabled: 'false' }
  });
  assert.strictEqual(off.status, 200);
  const pubOff = await api(`/api/public/tool?id=${publicToolId}`);
  assert.strictEqual(pubOff.status, 404);
});

test('worklogs: user adds own entry, sees it; admin sees summary; user is forbidden from summary', async () => {
  // Свежий пользователь заводится прямо в базе: формы регистрации в портале нет,
  // аккаунт появляется входом через Steam. У дефолтного `user` предыдущий тест
  // включил 2FA, поэтому нужен чистый.
  await new Promise((resolve, reject) => {
    db.run(
      "INSERT INTO users (username, email, password_hash, role) VALUES (?, ?, ?, 'User')",
      ['worker_wl', 'worker_wl@example.com', hashPassword('password123')],
      (err) => (err ? reject(err) : resolve())
    );
  });
  const reg = await api('/api/auth/login', {
    method: 'POST', ip: '10.20.1.1',
    body: { username: 'worker_wl', password: 'password123' }
  });
  assert.strictEqual(reg.status, 200);
  const uc = reg.cookie;
  assert.ok(uc && uc.startsWith('session='));

  // Добавляем запись
  const add = await api('/api/worklogs', {
    method: 'POST', cookie: uc,
    body: { work_date: '2026-08-22', hours: 8, note: 'Тест' }
  });
  assert.strictEqual(add.status, 201);

  // Некорректные часы отклоняются
  const bad = await api('/api/worklogs', {
    method: 'POST', cookie: uc, body: { work_date: '2026-08-22', hours: 99 }
  });
  assert.strictEqual(bad.status, 400);

  // Свои записи + итог
  const mine = await api('/api/worklogs/mine', { cookie: uc });
  assert.strictEqual(mine.status, 200);
  assert.ok(mine.json.entries.length >= 1);
  assert.ok(mine.json.total >= 8);

  // Пользователю нельзя смотреть сводку по всем
  const denied = await api('/api/worklogs/summary', { cookie: uc });
  assert.strictEqual(denied.status, 403);

  // Админ видит сводку с этим пользователем
  const alogin = await api('/api/auth/login', {
    method: 'POST', ip: '10.20.2.2',
    body: { username: 'admin', password: '1234qwer' }
  });
  assert.strictEqual(alogin.status, 200);
  const sum = await api('/api/worklogs/summary', { cookie: alogin.cookie });
  assert.strictEqual(sum.status, 200);
  assert.ok(sum.json.users.some(u => u.username === 'worker_wl' && u.total_hours >= 8));
});

// === Наш сервер: руны и общие постройки из файлов мода ===
test('game info: руны и постройки читаются из файлов мода, номера наружу не идут', async () => {
  // Один из игроков файла — наш: его должно быть видно под ником с сайта.
  await new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO users (username, email, password_hash, role, steam_id, steam_id_verified)
       VALUES (?, ?, ?, 'User', ?, 1)`,
      ['Скальд', 'skald@example.com', 'x', '76561198000000101'],
      (err) => (err ? reject(err) : resolve())
    );
  });

  const res = await api('/api/public/game');
  assert.strictEqual(res.status, 200);

  // Ставка берётся из конфига мода, а не из значения по умолчанию.
  assert.strictEqual(res.json.runes.minutes_per_rune, 30);

  const players = res.json.runes.players;
  assert.strictEqual(players.length, 3, 'строка без табов не считается игроком');
  // Порядок — по рунам: сначала тот, у кого их больше.
  assert.deepStrictEqual(players.map(p => p.runes), [12, 5, 0]);

  const ours = players.find(p => p.known);
  assert.strictEqual(ours.name, 'Скальд', 'свой игрок показывается ником с сайта');
  // 5 рун по 30 минут плюс 600 секунд остатка = 2,67 часа.
  assert.strictEqual(ours.hours, 2.7);

  const guest = players.find(p => !p.known);
  assert.strictEqual(guest.name, 'Гость', 'чужого игрока не называем: имя персонажа он нам не давал');
  assert.ok(!('character' in guest), 'имени персонажа в ответе нет вовсе');

  const builds = res.json.builds;
  const names = builds.map(b => b.name).sort();
  assert.deepStrictEqual(names, ['Дом на холме', 'Кузница'],
    'показываем только разобранное админом: без корзины deleted, без присланного игроком (#from) и без файлов без #name');
  const house = builds.find(b => b.name === 'Дом на холме');
  assert.strictEqual(house.category, 'Дома');
  assert.strictEqual(house.author, 'Skald-Testovyi');
  assert.strictEqual(house.pieces, 3);
  assert.strictEqual(house.for_players, true);
  // «#players no» — это «не открыта»: мод судит по значению, а не по наличию строки.
  assert.strictEqual(builds.find(b => b.name === 'Кузница').for_players, false);

  // Главное: наружу не уходит ни один SteamID — ни из рун, ни из «#from».
  assert.ok(!JSON.stringify(res.json).includes('76561'), 'номеров Steam в ответе нет');
});

// === Списки доступа доезжают до игрового сервера ===
test('списки доступа пишутся в папку сервера, а пустой permittedlist не пишется', async () => {
  const path = require('node:path');
  const readList = (name) => {
    try { return fsSync.readFileSync(path.join(SAVES_DIR, name), 'utf8'); } catch { return null; }
  };

  await new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO users (username, role, steam_id, steam_id_verified, whitelist_status,
                          whitelist_decided_at, server_admin)
       VALUES (?, 'User', ?, 1, 'approved', now(), 1)`,
      ['Ярл', '76561198000000900'],
      (err) => (err ? reject(err) : resolve())
    );
  });

  const admin = await api('/api/auth/login', {
    method: 'POST', ip: '10.30.1.1', body: { username: 'superadmin', password: '1234qwer' }
  });
  assert.strictEqual(admin.status, 200);

  const applied = await api('/api/admin/whitelist/apply', { method: 'POST', cookie: admin.cookie });
  assert.strictEqual(applied.status, 200);
  assert.strictEqual(applied.json.result.permitted.written, true);

  assert.match(readList('permittedlist.txt'), /^V_76561198000000900$/m, 'в списке доступа — номер с префиксом V_');
  const admins = readList('adminlist.txt');
  // Обе формы: ZNet.PlayerIsAdmin сравнивает id сырым и видит только ту, что прислал клиент.
  assert.match(admins, /^V_76561198000000900$/m);
  assert.match(admins, /^Steam_76561198000000900$/m);

  // Снимаем у всех доступ: пустой permittedlist.txt для игры значит «пускать всех»,
  // поэтому файл обязан остаться прежним.
  await new Promise((resolve, reject) => {
    db.run("UPDATE users SET whitelist_status = 'none'", [], (err) => (err ? reject(err) : resolve()));
  });
  const again = await api('/api/admin/whitelist/apply', { method: 'POST', cookie: admin.cookie });
  assert.strictEqual(again.status, 200);
  assert.strictEqual(again.json.result.permitted.written, false);
  assert.match(readList('permittedlist.txt'), /76561198000000900/, 'прежний список цел');
});

test('автоприём заявки выдаёт доступ сразу и обновляет список сервера', async () => {
  const path = require('node:path');
  await new Promise((resolve, reject) => {
    db.run("UPDATE settings SET value = 'true' WHERE key = 'whitelist_auto_approve'", [],
      (err) => (err ? reject(err) : resolve()));
  });
  await new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO users (username, email, password_hash, role, steam_id, steam_id_verified)
       VALUES (?, ?, ?, 'User', ?, 1)`,
      ['Скальди', 'skaldi@example.com', hashPassword('password123'), '76561198000000901'],
      (err) => (err ? reject(err) : resolve())
    );
  });

  const player = await api('/api/auth/login', {
    method: 'POST', ip: '10.30.2.1', body: { username: 'Скальди', password: 'password123' }
  });
  assert.strictEqual(player.status, 200);

  const asked = await api('/api/cabinet/whitelist/request', {
    method: 'POST', cookie: player.cookie, body: { note: 'Пустите, я тихий' }
  });
  assert.strictEqual(asked.status, 200);
  assert.strictEqual(asked.json.auto, true, 'заявка принята автоматически');

  const me = await api('/api/cabinet/me', { cookie: player.cookie });
  assert.strictEqual(me.json.user.whitelist_status, 'approved');
  assert.match(
    fsSync.readFileSync(path.join(SAVES_DIR, 'permittedlist.txt'), 'utf8'),
    /^V_76561198000000901$/m,
    'номер уехал на сервер сам, без кнопки'
  );

  await new Promise((resolve, reject) => {
    db.run("UPDATE settings SET value = 'false' WHERE key = 'whitelist_auto_approve'", [],
      (err) => (err ? reject(err) : resolve()));
  });
});

// === Админка в игре: явная выдача и снятие ===
test('админку в игре даёт только суперадмин и только тому, у кого есть доступ', async () => {
  const path = require('node:path');
  const insert = (username, steamId, status) => new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO users (username, role, steam_id, steam_id_verified, whitelist_status, whitelist_decided_at)
       VALUES (?, 'User', ?, 1, ?, now()) RETURNING id`,
      [username, steamId, status],
      function (err) { return err ? reject(err) : resolve(this.lastID); }
    );
  });
  const withAccess = await insert('Хельга', '76561198000000910', 'approved');
  const withoutAccess = await insert('Торстейн', '76561198000000911', 'none');

  const superadmin = await api('/api/auth/login', {
    method: 'POST', ip: '10.40.1.1', body: { username: 'superadmin', password: '1234qwer' }
  });
  assert.strictEqual(superadmin.status, 200);

  // Без доступа на сервер админка не выдаётся: это право, которое сработает в день,
  // когда список опустеет и сервер откроется всем.
  const refused = await api(`/api/admin/whitelist/${withoutAccess}/server-admin`, {
    method: 'PATCH', cookie: superadmin.cookie, body: { server_admin: true }
  });
  assert.strictEqual(refused.status, 409);

  const granted = await api(`/api/admin/whitelist/${withAccess}/server-admin`, {
    method: 'PATCH', cookie: superadmin.cookie, body: { server_admin: true }
  });
  assert.strictEqual(granted.status, 200);
  assert.strictEqual(granted.json.applied, true, 'список на сервер записан сразу');
  const admins = fsSync.readFileSync(path.join(SAVES_DIR, 'adminlist.txt'), 'utf8');
  assert.match(admins, /^V_76561198000000910$/m);
  assert.match(admins, /^Steam_76561198000000910$/m);

  // Обычный админ сайта отвечает на заявки, но админку в игре не выдаёт.
  const admin = await api('/api/auth/login', {
    method: 'POST', ip: '10.40.1.2', body: { username: 'admin', password: '1234qwer' }
  });
  assert.strictEqual(admin.status, 200);
  const forbidden = await api(`/api/admin/whitelist/${withAccess}/server-admin`, {
    method: 'PATCH', cookie: admin.cookie, body: { server_admin: false }
  });
  assert.strictEqual(forbidden.status, 403);

  // Забрать можно, и файл это видит.
  const revoked = await api(`/api/admin/whitelist/${withAccess}/server-admin`, {
    method: 'PATCH', cookie: superadmin.cookie, body: { server_admin: false }
  });
  assert.strictEqual(revoked.status, 200);
  assert.doesNotMatch(fsSync.readFileSync(path.join(SAVES_DIR, 'adminlist.txt'), 'utf8'), /76561198000000910/);
});
