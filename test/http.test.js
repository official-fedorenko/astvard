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
// Токен, по которому мод игрового сервера забирает списки доступа.
process.env.GAME_LISTS_TOKEN = 'test-lists-token-0123456789';

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

// === Мод забирает списки доступа по токену ===
test('списки для мода отдаются только по верному токену и в виде, который читает игра', async () => {
  const lines = (text) => text.split(String.fromCharCode(10)).map((l) => l.trim()).filter(Boolean);

  const noToken = await fetch(`${baseUrl}/api/game/lists`);
  assert.strictEqual(noToken.status, 401, 'без токена — отказ');

  const wrong = await fetch(`${baseUrl}/api/game/lists`, { headers: { Authorization: 'Bearer test-lists-token-WRONG-6789' } });
  assert.strictEqual(wrong.status, 401, 'чужой токен той же длины — тоже отказ');

  await new Promise((resolve, reject) => {
    db.run(
      `INSERT INTO users (username, role, steam_id, steam_id_verified, whitelist_status, whitelist_decided_at, server_admin)
       VALUES (?, 'User', ?, 1, 'approved', now(), 1)`,
      ['Сванхильд', '76561198000000920'],
      (err) => (err ? reject(err) : resolve())
    );
  });

  const ok = await fetch(`${baseUrl}/api/game/lists`, { headers: { Authorization: 'Bearer test-lists-token-0123456789' } });
  assert.strictEqual(ok.status, 200);
  const got = lines(await ok.text());
  const permittedAt = got.indexOf('[permitted]');
  const adminsAt = got.indexOf('[admins]');
  assert.ok(permittedAt === 0 && adminsAt > permittedAt, 'две секции по порядку');
  assert.ok(got.slice(permittedAt + 1, adminsAt).includes('V_76561198000000920'), 'доступ — с префиксом V_');
  const admins = got.slice(adminsAt + 1);
  assert.ok(admins.includes('V_76561198000000920') && admins.includes('Steam_76561198000000920'), 'админ — обеими формами');
});

// ---------------------------------------------------------------- search and previews

const { DEFAULT_SETTINGS: SEO_DEFAULTS } = require('../db');

async function setSetting(key, value) {
  await pool.query(
    'INSERT INTO settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value',
    [key, value]
  );
}

async function resetSetting(key) {
  await setSetting(key, SEO_DEFAULTS.find(([k]) => k === key)[1]);
}

async function seededArticleIds() {
  const { rows } = await pool.query("SELECT id, status FROM articles ORDER BY id");
  return {
    published: rows.find((r) => r.status === 'published').id,
    draft: rows.find((r) => r.status === 'draft').id
  };
}

test('главная уходит с сервера готовой: тексты из настроек, заголовок для поиска, schema.org, ссылки на статьи', async () => {
  const { published } = await seededArticleIds();
  await setSetting('hero_title', 'Сервер <b>Valheim</b>');
  await setSetting('seo_title', 'Тестовый заголовок для поиска');
  try {
    const res = await fetch(`${baseUrl}/`);
    assert.strictEqual(res.status, 200);
    assert.strictEqual(res.headers.get('x-robots-tag'), null, 'главная не закрыта от индекса');
    const html = await res.text();

    assert.ok(html.includes('<title>Тестовый заголовок для поиска</title>'));
    assert.ok(html.includes('Сервер &lt;b&gt;Valheim&lt;/b&gt;'), 'текст из настроек экранирован');
    assert.ok(!html.includes('{{'), 'в странице не осталось незаполненных мест');
    assert.ok(!html.includes('г. Вильнюс'), 'заглушка панели не доезжает до поисковика');
    assert.match(html, /<link rel="canonical" href="http[^"]+\/">/);
    assert.match(html, /<meta name="description" content="[^"]{40,}">/);

    const ld = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)[1]);
    assert.ok(ld['@graph'].some((node) => node['@type'] === 'WebSite'));

    assert.ok(html.includes(`href="/news/${published}-dobro-pozhalovat-v-astvard"`), 'статья — ссылка на свою страницу');
    // Главная теперь витрина: сами разделы живут по своим адресам.
    for (const link of ['/join', '/mod', '/server', '/news']) {
      assert.ok(html.includes(`href="${link}"`), `на главной нет ссылки на ${link}`);
    }
  } finally {
    await resetSetting('hero_title');
    await resetSetting('seo_title');
  }
});

test('у каждого раздела свой адрес: заголовок, канонический адрес, h1 и подсвеченный пункт меню', async () => {
  const base = process.env.APP_URL ? process.env.APP_URL.replace(/\/+$/, '') : 'http://localhost:3001';
  const pages = [
    ['/', null],
    ['/server', 'Наш сервер'],
    ['/mod', 'Что умеет мод'],
    ['/join', 'Как попасть'],
    ['/news', 'Новости'],
    ['/about', 'О проекте']
  ];

  for (const [pagePath, navLabel] of pages) {
    const res = await fetch(`${baseUrl}${pagePath}`);
    assert.strictEqual(res.status, 200, pagePath);
    assert.strictEqual(res.headers.get('x-robots-tag'), null, `${pagePath} закрыт от индекса`);
    const html = await res.text();

    assert.ok(!html.includes('{{'), `${pagePath}: остались незаполненные места`);
    assert.ok(html.includes(`<link rel="canonical" href="${base}${pagePath}">`), `${pagePath}: канонический адрес`);
    assert.match(html, /<h1[^>]*>[^<]/, `${pagePath}: нет заголовка h1`);
    assert.match(html, /<meta name="description" content="[^"]{40,}">/, `${pagePath}: описание для поиска`);
    assert.ok(html.includes('aria-current="page"'), `${pagePath}: пункт меню не подсвечен`);

    const ld = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)[1]);
    assert.ok(ld['@graph'].some((node) => node['@type'] === 'WebSite'), `${pagePath}: нет WebSite`);
    if (navLabel) {
      assert.ok(html.includes(`<span>${navLabel}</span>`), `${pagePath}: нет пункта меню «${navLabel}»`);
      // У внутренней страницы есть хлебные крошки, у главной их быть не должно.
      assert.ok(ld['@graph'].some((node) => node['@type'] === 'BreadcrumbList'), `${pagePath}: нет хлебных крошек`);
    } else {
      assert.ok(!ld['@graph'].some((node) => node['@type'] === 'BreadcrumbList'), 'у главной крошек нет');
    }
  }
});

test('один адрес на раздел: со слэшем на конце уводит на адрес без него', async () => {
  for (const pagePath of ['/server/', '/mod/', '/join/', '/news/', '/about/']) {
    const res = await fetch(`${baseUrl}${pagePath}`, { redirect: 'manual' });
    assert.strictEqual(res.status, 301, pagePath);
    assert.strictEqual(res.headers.get('location'), pagePath.slice(0, -1), pagePath);
  }
});

test('содержимое разъехалось по страницам: мод, заявка и лента — каждое у себя', async () => {
  const { published } = await seededArticleIds();

  const mod = await (await fetch(`${baseUrl}/mod`)).text();
  assert.ok(mod.split('data-feature=').length - 1 >= 20, 'на странице мода плитки возможностей');
  assert.ok(mod.includes('id="featureModal"'), 'на странице мода есть окно с пояснением');

  const join = await (await fetch(`${baseUrl}/join`)).text();
  assert.ok(join.includes('Заявку смотрит админ'), 'шаг про заявку на своей странице');

  const news = await (await fetch(`${baseUrl}/news`)).text();
  assert.ok(news.includes(`href="/news/${published}-dobro-pozhalovat-v-astvard"`), 'статья в ленте');
  assert.ok(!news.includes('id="featureModal"'), 'окно мода не таскается по всем страницам');
  assert.ok(!news.includes('data-feature='), 'плитки мода не таскаются по всем страницам');
});

test('у статьи один адрес: старые и неполные ведут на него, черновик и мусор — 404', async () => {
  const { published, draft } = await seededArticleIds();
  const canonical = `/news/${published}-dobro-pozhalovat-v-astvard`;

  let res = await fetch(`${baseUrl}/index.html?utm_source=vk`, { redirect: 'manual' });
  assert.strictEqual(res.status, 301);
  assert.strictEqual(res.headers.get('location'), '/?utm_source=vk');

  res = await fetch(`${baseUrl}/news/${published}?utm_campaign=test`, { redirect: 'manual' });
  assert.strictEqual(res.status, 301);
  assert.strictEqual(res.headers.get('location'), `${canonical}?utm_campaign=test`, 'метки кампании не теряются');

  res = await fetch(`${baseUrl}/news/${published}-staroe-nazvanie/`, { redirect: 'manual' });
  assert.strictEqual(res.status, 301);
  assert.strictEqual(res.headers.get('location'), canonical);

  res = await fetch(`${baseUrl}${canonical}`);
  assert.strictEqual(res.status, 200);
  const html = await res.text();
  assert.ok(html.includes('<h1 class="article-page__title">Добро пожаловать в Astvard</h1>'));
  assert.ok(html.includes(`<link rel="canonical" href="${process.env.APP_URL ? process.env.APP_URL.replace(/\/+$/, '') : 'http://localhost:3001'}${canonical}">`));
  assert.ok(html.includes('<meta property="og:type" content="article">'));
  assert.ok(!html.includes('{{'));
  const ld = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)[1]);
  const posting = ld['@graph'].find((node) => node['@type'] === 'BlogPosting');
  assert.strictEqual(posting.headline, 'Добро пожаловать в Astvard');

  res = await fetch(`${baseUrl}/news/${draft}-chernovik`);
  assert.strictEqual(res.status, 404, 'черновик не открывается по адресу');
  res = await fetch(`${baseUrl}/news/abc`);
  assert.strictEqual(res.status, 404);
});

test('robots.txt и sitemap.xml: служебное закрыто, в карте только опубликованное', async () => {
  const { published, draft } = await seededArticleIds();

  let res = await fetch(`${baseUrl}/robots.txt`);
  assert.strictEqual(res.status, 200);
  assert.match(res.headers.get('content-type'), /^text\/plain/);
  const robots = await res.text();
  assert.ok(robots.includes('Disallow: /admin'));
  assert.ok(robots.includes('Allow: /api/public/'));
  assert.match(robots, /^Sitemap: https?:\/\/.+\/sitemap\.xml$/m);

  res = await fetch(`${baseUrl}/sitemap.xml`);
  assert.strictEqual(res.status, 200);
  assert.match(res.headers.get('content-type'), /^application\/xml/);
  const xml = await res.text();
  for (const pagePath of ['/', '/server', '/mod', '/join', '/news', '/about']) {
    assert.ok(xml.includes(`${pagePath}</loc>`), `в карте сайта нет ${pagePath}`);
  }
  assert.ok(xml.includes(`/news/${published}-dobro-pozhalovat-v-astvard</loc>`));
  assert.ok(!xml.includes(`/news/${draft}-`), 'черновика в карте нет');
  assert.match(xml, /<lastmod>\d{4}-\d{2}-\d{2}T/);
});

test('кабинет, вход, админка и API помечены noindex, публичное — нет', async () => {
  for (const pagePath of ['/cabinet.html', '/login.html', '/admin/login.html', '/api/public/settings']) {
    const res = await fetch(`${baseUrl}${pagePath}`, { redirect: 'manual' });
    assert.strictEqual(res.headers.get('x-robots-tag'), 'noindex, nofollow', pagePath);
  }
  for (const pagePath of ['/', '/robots.txt', '/favicon.ico', '/site.webmanifest']) {
    const res = await fetch(`${baseUrl}${pagePath}`);
    assert.strictEqual(res.status, 200, pagePath);
    assert.strictEqual(res.headers.get('x-robots-tag'), null, pagePath);
  }
});

test('Метрика и коды подтверждения встают, только когда заданы, и только в своём виде', async () => {
  // Люди вставляют то, что им показал сервис: код счётчика целиком, тег целиком.
  await setSetting('yandex_metrika_id', '<script>ym(12345678, "init", { clickmap:true })</script>');
  await setSetting('yandex_verification', '<meta name="yandex-verification" content="0123abcd4567ef89" />');
  await setSetting('google_verification', 'x"><script>alert(1)</script>');
  try {
    let res = await fetch(`${baseUrl}/`);
    let html = await res.text();
    assert.ok(html.includes("ym(12345678, 'init'"), 'счётчик с номером из вставленного кода');
    assert.ok(html.includes('https://mc.yandex.ru/metrika/tag.js'));
    assert.ok(res.headers.get('content-security-policy').includes('https://mc.yandex.ru'), 'CSP пускает Метрику');
    assert.ok(html.includes('<meta name="yandex-verification" content="0123abcd4567ef89">'));
    assert.ok(!html.includes('google-site-verification'), 'кривой код не вставляется вовсе');
    assert.ok(!html.includes('alert(1)'));

    res = await fetch(`${baseUrl}/cabinet.html`);
    assert.ok((await res.text()).includes('mc.yandex.ru/metrika/tag.js'), 'цель заявки считается в кабинете');

    res = await fetch(`${baseUrl}/admin/login.html`);
    assert.ok(!(await res.text()).includes('mc.yandex.ru'), 'в админке счётчика нет');
    assert.ok(!res.headers.get('content-security-policy').includes('mc.yandex.ru'));
  } finally {
    await resetSetting('yandex_metrika_id');
    await resetSetting('yandex_verification');
    await resetSetting('google_verification');
  }

  const res = await fetch(`${baseUrl}/`);
  assert.ok(!(await res.text()).includes('mc.yandex.ru'), 'без номера счётчика нет');
  assert.ok(!res.headers.get('content-security-policy').includes('mc.yandex.ru'));
});

// Quill is let through by its exact package path, so bumping the version in the page
// alone would switch the article editor off without a word in the logs. A redirect
// that leaves the listed host (what broke it once) is beyond a test without network.
test('CSP пускает каждый скрипт и стиль, который страницы берут с чужих адресов', async () => {
  const fs = require('node:fs');
  const path = require('node:path');
  const csp = (await fetch(`${baseUrl}/admin/login.html`)).headers.get('content-security-policy');
  const allows = (directive, url) => {
    const sources = csp.split(';').map((part) => part.trim().split(/\s+/))
      .find(([name]) => name === directive).slice(1);
    const target = new URL(url);
    return sources.some((source) => {
      if (!source.startsWith('https://')) return false;
      const { origin, pathname } = new URL(source);
      if (target.origin !== origin) return false;
      if (pathname === '/') return true;
      return pathname.endsWith('/') ? target.pathname.startsWith(pathname) : target.pathname === pathname;
    });
  };

  const checked = [];
  const pages = [];
  const walk = (place) => {
    for (const entry of fs.readdirSync(place, { withFileTypes: true })) {
      const full = path.join(place, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith('.html')) pages.push(full);
    }
  };
  // src/templates — разметка публичных страниц: оболочка, куски и страница статьи.
  for (const dir of ['public', 'client', 'src/templates']) walk(path.join(__dirname, '..', dir));
  {
    for (const file of pages) {
      const dir = path.relative(path.join(__dirname, '..'), path.dirname(file));
      const html = fs.readFileSync(file, 'utf8');
      for (const [, url] of html.matchAll(/<script\b[^>]*\bsrc="(https?:\/\/[^"]+)"/g)) {
        assert.ok(allows('script-src', url), `${dir}/${path.basename(file)}: ${url}`);
        checked.push(url);
      }
      for (const [tag] of html.matchAll(/<link\b[^>]*>/g)) {
        const href = /\bhref="(https?:\/\/[^"]+)"/.exec(tag);
        if (!href || !/\brel="stylesheet"/.test(tag)) continue;
        assert.ok(allows('style-src', href[1]), `${dir}/${path.basename(file)}: ${href[1]}`);
        checked.push(href[1]);
      }
    }
  }
  assert.ok(checked.some((url) => url.includes('quill.min.js')), 'редактор статей среди проверенного');
  assert.ok(checked.some((url) => url.includes('quill.snow.css')));
});

// ---------------------------------------------------------------- builds for players

const TAB = String.fromCharCode(9);
const LF = String.fromCharCode(10);
const buildRow = (...fields) => fields.join(TAB);
const MOD_AUTH = { Authorization: 'Bearer test-lists-token-0123456789' };

const BUILD_META = [
  buildRow('meta', 'floor', 'Пол', 'build', 'choice', '0', '0', '', 'Даром — с паузой между постройками'),
  buildRow('meta', 'copy', 'Копирование', 'build', 'choicelimit', '1', '64', 'радиус', ''),
  buildRow('meta', 'clear', 'Снос', 'terrain', 'toggle', '0', '0', '', ''),
  buildRow('meta', 'pause', 'Пауза между постройками', 'build', 'number', '0', '1440', 'мин', '')
];

async function resetBuilds() {
  await pool.query('DELETE FROM game_build_rules');
  await pool.query('DELETE FROM game_build_rule_meta');
  await pool.query('DELETE FROM game_template_access');
  await pool.query('INSERT INTO game_build_sync (id) VALUES (1) ON CONFLICT (id) DO NOTHING');
  await pool.query('UPDATE game_build_sync SET revision = 0, seeded = false, mod_seen_at = NULL, mod_applied_revision = 0');
}

async function modPull(rev) {
  const res = await fetch(`${baseUrl}/api/game/builds?rev=${rev}`, { headers: MOD_AUTH });
  return { status: res.status, lines: (await res.text()).split(LF).filter(Boolean) };
}

async function modPush(lines) {
  const res = await fetch(`${baseUrl}/api/game/builds`, {
    method: 'POST',
    headers: { ...MOD_AUTH, 'Content-Type': 'text/plain; charset=utf-8' },
    body: lines.join(LF) + LF
  });
  return { status: res.status, lines: (await res.text()).split(LF).filter(Boolean) };
}

async function loginAs(username, password, ip) {
  const login = await api('/api/auth/login', { method: 'POST', ip, body: { username, password } });
  assert.strictEqual(login.status, 200, `вход ${username}`);
  return login.cookie;
}

test('постройки: без токена — отказ, а пустой сайт просит у мода его состояние', async () => {
  await resetBuilds();

  assert.strictEqual((await fetch(`${baseUrl}/api/game/builds?rev=0`)).status, 401);
  const noToken = await fetch(`${baseUrl}/api/game/builds`, { method: 'POST', body: buildRow('kind', 'change') + LF });
  assert.strictEqual(noToken.status, 401);

  const pull = await modPull(0);
  assert.strictEqual(pull.status, 200);
  assert.deepStrictEqual(pull.lines, [buildRow('revision', '0'), buildRow('seed', 'needed')]);

  // Изменение из игры до засева накладывать не на что: сайт просит засеять.
  const early = await modPush([buildRow('kind', 'change'), buildRow('rule', 'floor', '1', '')]);
  assert.strictEqual(early.status, 409);
  assert.ok(early.lines.includes(buildRow('seed', 'needed')));

  assert.strictEqual((await modPush([buildRow('rule', 'floor', '1', '')])).status, 400, 'без строки kind — отказ');
});

test('постройки: засев от мода, правка на сайте и правка из игры — действует последняя', async () => {
  await resetBuilds();
  const cookie = await loginAs('superadmin', '1234qwer', '10.50.1.1');

  // Мод при старте описывает свои правила; нечитаемая строка не валит остальные.
  assert.strictEqual((await modPush([buildRow('kind', 'hello'), buildRow('meta', 'BAD'), ...BUILD_META])).status, 200);

  let push = await modPush([buildRow('kind', 'seed'),
    buildRow('rule', 'floor', '2', ''), buildRow('rule', 'copy', '2', '20'),
    buildRow('rule', 'clear', '0', ''), buildRow('rule', 'pause', '5', ''),
    buildRow('tpl', 'Дом на холме', '1', '76561198000000901,мусор')]);
  assert.deepStrictEqual(push.lines, [buildRow('revision', '1')]);

  push = await modPush([buildRow('kind', 'seed'), buildRow('rule', 'floor', '0', '')]);
  assert.deepStrictEqual(push.lines, [buildRow('revision', '1'), 'ignored'], 'второй засев ничего не меняет');

  let pull = await modPull(0);
  assert.ok(pull.lines.includes(buildRow('rule', 'floor', '2', '')));
  assert.ok(pull.lines.includes(buildRow('rule', 'copy', '2', '20')));
  assert.ok(pull.lines.includes(buildRow('tpl', 'Дом на холме', '1', '76561198000000901')), 'мусор из списка игроков выброшен');
  assert.deepStrictEqual((await modPull(1)).lines, [buildRow('revision', '1'), 'unchanged']);

  // Сайт закрывает пол; неверные значения ревизию не тратят.
  let r = await api('/api/admin/builds/rules/floor', { method: 'PATCH', cookie, body: { value: 0 } });
  assert.strictEqual(r.status, 200);
  assert.strictEqual(r.json.revision, 2);
  r = await api('/api/admin/builds/rules/floor', { method: 'PATCH', cookie, body: { value: 5 } });
  assert.strictEqual(r.status, 400);
  r = await api('/api/admin/builds/rules/copy', { method: 'PATCH', cookie, body: { value: 1, limit: 500 } });
  assert.strictEqual(r.status, 400, 'радиус больше, чем знает мод');
  r = await api('/api/admin/builds/rules/nosuch', { method: 'PATCH', cookie, body: { value: 1 } });
  assert.strictEqual(r.status, 404);

  let view = await api('/api/admin/builds', { cookie });
  assert.strictEqual(view.status, 200);
  assert.strictEqual(view.json.rules.length, 4, 'правила — ровно те, что описал мод');
  const floor = view.json.rules.find((x) => x.key === 'floor');
  assert.strictEqual(floor.value, 0);
  assert.strictEqual(floor.applied, false, 'сервер это ещё не забирал');
  assert.strictEqual(view.json.rules.find((x) => x.key === 'pause').applied, true);

  // В игре админ снова открыл пол — уже после сайта — и снял «всем» с дома.
  push = await modPush([buildRow('kind', 'change'), buildRow('rule', 'floor', '1', ''), buildRow('tplall', 'Дом на холме', '0')]);
  assert.deepStrictEqual(push.lines, [buildRow('revision', '3')]);

  pull = await modPull(1);
  assert.ok(pull.lines.includes(buildRow('rule', 'floor', '1', '')), 'правка из игры новее правки на сайте');
  assert.ok(pull.lines.includes(buildRow('tpl', 'Дом на холме', '0', '76561198000000901')),
    'переключатель в игре не трогает выбранных игроков');

  await modPull(3);
  view = await api('/api/admin/builds', { cookie });
  assert.strictEqual(view.json.sync.mod_applied_revision, 3);
  assert.ok(view.json.rules.every((x) => x.applied), 'всё применено, когда мод сказал ревизию 3');
  assert.strictEqual(view.json.rules.find((x) => x.key === 'floor').updated_by, 'game');
});

test('постройки: кому открыта постройка решает админ сайта, игроку раздел закрыт', async () => {
  const cookie = await loginAs('superadmin', '1234qwer', '10.50.1.2');
  const view = await api('/api/admin/builds', { cookie });

  const house = view.json.templates.find((t) => t.name === 'Дом на холме');
  assert.ok(house && house.on_server && !house.submitted);
  assert.deepStrictEqual(house.players.map((p) => p.steam_id), ['76561198000000901']);
  assert.ok(view.json.templates.some((t) => t.name === 'Реклама от игрока' && t.submitted),
    'присланное игроком админ тоже видит');
  assert.ok(!view.json.templates.some((t) => t.name === 'Снесённое'), 'корзины мода в списке нет');

  let r = await api('/api/admin/builds/template', {
    method: 'PATCH', cookie,
    body: { name: 'Кузница', players: ['76561198000000902', 'https://steamcommunity.com/profiles/76561198000000901'] }
  });
  assert.strictEqual(r.status, 200);
  const pull = await modPull(0);
  assert.ok(pull.lines.includes(buildRow('tpl', 'Кузница', '0', '76561198000000901,76561198000000902')),
    'ссылка на профиль превращается в номер, список — по порядку');

  r = await api('/api/admin/builds/template', { method: 'PATCH', cookie, body: { name: 'Кузница', players: ['12345'] } });
  assert.strictEqual(r.status, 400);
  r = await api('/api/admin/builds/template', { method: 'PATCH', cookie, body: { name: 'Нет такой', for_all: true } });
  assert.strictEqual(r.status, 404);
  r = await api('/api/admin/builds/template', { method: 'PATCH', cookie, body: { name: 'Кузница', for_all: 'yes' } });
  assert.strictEqual(r.status, 400);

  await pool.query(
    `INSERT INTO users (username, email, password_hash, role) VALUES ($1, $2, $3, 'User')`,
    ['Строитель', 'builder@example.com', hashPassword('builder-pass-123')]
  );
  const player = await loginAs('Строитель', 'builder-pass-123', '10.50.1.3');
  assert.strictEqual((await api('/api/admin/builds', { cookie: player })).status, 403);
  assert.strictEqual((await api('/api/admin/builds')).status, 401);
});
