// === Astvard: вайтлист игрового сервера и состояние серверов ===
//
// Отдельный файл, а не строки в app.js: игровая часть пришла из портала, живёт
// своими маршрутами и правится отдельно от учётной части панели.
//
// Таблицы здесь играют по тем же правилам, что и остальная панель: класс
// .cards-mobile сворачивает строку в карточку на узком экране, .mobile-primary —
// то, что в карточке видно, .mobile-hidden и .no-label — то, что прячется, а
// полное содержимое строки открывается по тапу в общей модалке showRowDetail
// (обе живут в app.js). Кнопки в модалке носят те же классы, что и в строке,
// поэтому их ловит тот же делегированный обработчик.

const WHITELIST_LABELS = {
  none: 'нет доступа',
  pending: 'ждёт решения',
  approved: 'одобрен',
  rejected: 'отклонён'
};

// Цвет статуса в общих бейджах панели: зелёный — пускают, голубой — ждёт ответа,
// красный — отказ. «Нет доступа» намеренно без цвета: это не событие.
const WHITELIST_BADGES = {
  none: '',
  pending: 'badge-warning',
  approved: 'badge-success',
  rejected: 'badge-danger'
};

let whitelistPeople = [];
let astvardServers = [];
let astvardHandlersBound = false;

function astvardDate(value) {
  if (!value) return '—';
  const d = new Date(value.replace(' ', 'T') + (value.includes('Z') ? '' : 'Z'));
  return Number.isNaN(d.getTime()) ? value : d.toLocaleString();
}

// Карточка открывается только там, где таблица и правда свёрнута в карточку:
// ширина та же, что в CSS у .cards-mobile. На широком экране всё видно в строке,
// и модалка только мешала бы.
function astvardCardsMode() {
  return window.matchMedia('(max-width: 640px)').matches;
}

// Скачивание списка идёт через fetch, а не переходом по ссылке: выгрузка пустого
// permittedlist.txt отклоняется с 409 и причиной, и её надо показать, а не открыть
// JSON на весь экран.
async function downloadList(url, filename) {
  try {
    const res = await fetch(url);
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      showToast(data.message || 'Не удалось выгрузить файл', 'error');
      return;
    }
    const blob = await res.blob();
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
    showToast(`${filename} выгружен`);
  } catch (err) {
    showToast('Не удалось выгрузить файл', 'error');
  }
}

async function loadWhitelist() {
  try {
    const res = await fetch('/api/admin/whitelist');
    if (!res.ok) return;
    const data = await res.json();
    whitelistPeople = data.people || [];
    renderWhitelist();
  } catch (err) {
    showToast('Ошибка загрузки вайтлиста', 'error');
  }
}

function whitelistStatusBadge(status) {
  const label = WHITELIST_LABELS[status] || status;
  const cls = WHITELIST_BADGES[status];
  return cls
    ? `<span class="badge ${cls}">${escapeHtml(label)}</span>`
    : `<span class="muted">${escapeHtml(label)}</span>`;
}

// Колонка «В игре» показывает только состояние. Выдают и забирают админку явные
// кнопки в строке: галочку в таблице легко не заметить и легко задеть.
function whitelistAdminCell(p) {
  return p.server_admin ? '<span class="badge badge-success">админ</span>' : '—';
}

const isSuperadmin = () => currentUser && currentUser.role === 'Superadmin';

function whitelistButtons(p) {
  // Админку в игре выдаёт только суперадмин — это спавн, бан и кик, — и только тому,
  // у кого есть доступ: админ, которого сервер не пускает, это забытое право.
  // Забрать — у любого, у кого она есть: админ без доступа как раз и есть забытое
  // право, которое надо уметь снять. Дать — только тому, у кого доступ есть.
  let adminButton = '';
  if (isSuperadmin() && p.server_admin) {
    adminButton = `<button class="btn btn-sm btn-secondary wl-admin-revoke" data-id="${p.id}">Забрать админку</button>`;
  } else if (isSuperadmin() && p.whitelist_status === 'approved') {
    adminButton = `<button class="btn btn-sm wl-admin-grant" data-id="${p.id}">Дать админку</button>`;
  }
  if (p.whitelist_status === 'pending') {
    return `<button class="btn btn-sm wl-approve" data-id="${p.id}">Одобрить</button>
            <button class="btn btn-sm btn-secondary wl-reject" data-id="${p.id}">Отклонить</button>`;
  }
  return p.whitelist_status === 'approved'
    ? `${adminButton}<button class="btn btn-sm btn-danger wl-revoke" data-id="${p.id}">Забрать доступ</button>`
    : `${adminButton}<button class="btn btn-sm wl-approve" data-id="${p.id}">Дать доступ</button>`;
}

function whitelistRow(p, middleCell) {
  // Номер в Steam дублируется под ником: в свёрнутой карточке своей колонки у
  // него нет, а это единственное, чем игроки различаются наверняка.
  const manual = p.steam_id_verified
    ? ''
    : '<div class="cell-note cell-note-warn">вписан руками</div>';
  return `
    <tr data-id="${p.id}">
      <td class="mobile-primary">
        <strong>${escapeHtml(p.username)}</strong>
        <div class="row-note"><code>${escapeHtml(p.steam_id || '—')}</code></div>
        ${manual}
      </td>
      <td class="mobile-hidden"><code>${escapeHtml(p.steam_id || '—')}</code></td>
      ${middleCell}
      <td class="mobile-hidden">${astvardDate(p.whitelist_decided_at || p.whitelist_requested_at)}</td>
      <td class="mobile-hidden">${whitelistAdminCell(p)}</td>
      <td class="no-label"><div class="row-actions">${whitelistButtons(p)}</div></td>
    </tr>`;
}

function whitelistEmptyRow(text) {
  return `<tr class="empty-row"><td colspan="6" class="empty-state">${text}</td></tr>`;
}

// Ищем и по нику, и по номеру: админ приходит сюда либо с именем из разговора,
// либо с номером из чужого сообщения.
function whitelistMatches(p, query) {
  if (!query) return true;
  return `${p.username} ${p.steam_id || ''}`.toLowerCase().includes(query);
}

function renderWhitelist() {
  const searchInput = document.getElementById('whitelistSearch');
  const query = (searchInput ? searchInput.value : '').trim().toLowerCase();
  const people = whitelistPeople.filter(p => whitelistMatches(p, query));
  const pending = people.filter(p => p.whitelist_status === 'pending');
  const rest = people.filter(p => p.whitelist_status !== 'pending');
  const approved = whitelistPeople.filter(p => p.whitelist_status === 'approved').length;

  const requestsBody = document.getElementById('whitelistRequestsBody');
  const accessBody = document.getElementById('whitelistAccessBody');
  if (!requestsBody || !accessBody) return;

  requestsBody.innerHTML = pending.length
    ? pending.map(p => whitelistRow(
      p,
      `<td class="mobile-hidden">${p.whitelist_request_note ? escapeHtml(p.whitelist_request_note) : '—'}</td>`
    )).join('')
    : whitelistEmptyRow(query ? 'Среди заявок никто не нашёлся' : 'Новых заявок нет');

  accessBody.innerHTML = rest.length
    ? rest.map(p => whitelistRow(p, `<td>${whitelistStatusBadge(p.whitelist_status)}</td>`)).join('')
    : whitelistEmptyRow(query ? 'Никто не нашёлся' : 'Никто ещё не привязал Steam');

  const counter = document.getElementById('whitelistCount');
  if (counter) counter.textContent = `— открыт ${approved} из ${whitelistPeople.length}`;
}

// Полная строка в модалке — то же, что видно в таблице на широком экране, плюс
// кнопки. Без неё тап по карточке обещал бы стрелкой то, чего не происходит.
function showWhitelistDetail(p) {
  const rows = [
    ['Steam ID', `<code>${escapeHtml(p.steam_id || '—')}</code>`, true],
    ['Номер', p.steam_id_verified ? 'подтверждён Steam' : 'вписан админом'],
    ['Состояние', whitelistStatusBadge(p.whitelist_status), true],
    ['Админка в игре', p.server_admin ? 'да' : 'нет'],
    ['Когда', astvardDate(p.whitelist_decided_at || p.whitelist_requested_at)]
  ];
  if (p.whitelist_request_note) rows.push(['Сообщение игрока', p.whitelist_request_note, 'block']);
  if (p.whitelist_note) rows.push(['Ответ админа', p.whitelist_note, 'block']);

  showRowDetail(p.username, rows, whitelistButtons(p));
}

async function whitelistDecide(id, status, note) {
  try {
    const res = await fetch(`/api/admin/whitelist/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ status, note })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      showToast(data.message || 'Не получилось', 'error');
      return;
    }
    loadWhitelist();
  } catch (err) {
    showToast('Не получилось', 'error');
  }
}

async function loadServersSection() {
  try {
    const res = await fetch('/api/admin/servers');
    if (!res.ok) return;
    const data = await res.json();
    astvardServers = data.servers || [];
    renderServers();
  } catch (err) {
    showToast('Ошибка загрузки серверов', 'error');
  }
}

function serverStatusBadge(s) {
  return s.is_online
    ? '<span class="badge badge-success">online</span>'
    : '<span class="badge badge-muted">offline</span>';
}

function serverPlayers(s) {
  if (!s.is_online) return '—';
  // null — не «никого», а «сервер ещё не сказал»: до первой записи в логе числа
  // просто нет.
  return s.players === null ? 'считает' : `${s.players}/${s.max_players}`;
}

// Кто сейчас в игре. Номера приходят из лога игрового сервера; известные нам
// превращаются в ники, незнакомый номер так и показывается — это ровно тот, кого
// стоит завести в вайтлисте, и по этому номеру он заводится.
function serverOnlineNames(s) {
  const list = (s.game && s.game.players) || [];
  if (!list.length) return '';
  return list.map(p => p.username || `не на сайте: ${p.steam_id}`).join(', ');
}

function renderServers() {
  const tbody = document.getElementById('serversTableBody');
  if (!tbody) return;
  tbody.innerHTML = astvardServers.length
    ? astvardServers.map(s => `
      <tr data-id="${s.id}">
        <td class="mobile-primary">
          <strong>${escapeHtml(s.name)}</strong>
          <div class="row-note"><code>${escapeHtml(s.host)}:${escapeHtml(String(s.port))}</code></div>
        </td>
        <td class="mobile-hidden"><code>${escapeHtml(s.host)}:${escapeHtml(String(s.port))}</code></td>
        <td>${serverStatusBadge(s)}</td>
        <td class="mobile-hidden">
          ${serverPlayers(s)}
          ${serverOnlineNames(s) ? `<div class="cell-note">${escapeHtml(serverOnlineNames(s))}</div>` : ''}
        </td>
        <td class="mobile-hidden">${astvardDate(s.last_checked_at)}</td>
        <td class="no-label"><div class="row-actions"><button class="btn btn-sm btn-danger server-delete" data-id="${s.id}">Удалить</button></div></td>
      </tr>`).join('')
    : '<tr class="empty-row"><td colspan="6" class="empty-state">Серверов пока нет</td></tr>';
}

function showServerDetail(s) {
  const probe = s.probe === 'valheim-log' ? 'чтение лога Valheim' : 'запрос к серверу (A2S)';
  const rows = [
    ['Адрес', `<code>${escapeHtml(s.host)}:${escapeHtml(String(s.port))}</code>`, true],
    ['Состояние', serverStatusBadge(s), true],
    ['Игроки', serverPlayers(s)]
  ];
  const names = serverOnlineNames(s);
  if (names) rows.push(['Сейчас в игре', names, 'block']);
  if (s.game && s.game.saveNumber) rows.push(['Сохранение мира', `№ ${s.game.saveNumber}`]);
  if (s.game && s.game.gameVersion) rows.push(['Версия игры', s.game.gameVersion]);
  rows.push(['Как проверяем', probe], ['Проверен', astvardDate(s.last_checked_at)]);
  showRowDetail(s.name, rows, `<button class="btn btn-sm btn-danger server-delete" data-id="${s.id}">Удалить</button>`);
}

// Обработчики вешаются один раз и на весь документ: таблицы перерисовываются
// целиком после каждого действия, и слушатели на самих кнопках умирали бы вместе
// со строками. По той же причине тап по карточке ловится здесь, а не вешается на
// каждую строку, как в app.js.
function bindAstvardHandlers() {
  if (astvardHandlersBound) return;
  astvardHandlersBound = true;

  // Поиск фильтрует уже загруженный список: людей тут десятки, а не тысячи, и
  // ходить за этим на сервер при каждой букве незачем.
  document.addEventListener('input', (e) => {
    if (e.target.id === 'whitelistSearch') renderWhitelist();
  });

  document.addEventListener('click', async (e) => {
    const approve = e.target.closest('.wl-approve');
    if (approve) {
      closeRowDetail();
      return whitelistDecide(approve.dataset.id, 'approved', null);
    }

    const reject = e.target.closest('.wl-reject');
    if (reject) {
      closeRowDetail();
      // Эту строку увидит игрок, поэтому её стоит написать. Отмена — это отмена,
      // пустая причина допустима.
      const note = await promptDialog('Причина отказа — её увидит игрок. Можно оставить пустым.', '', {
        title: 'Отклонить заявку', okText: 'Отклонить', danger: true
      });
      if (note === null) return;
      return whitelistDecide(reject.dataset.id, 'rejected', note);
    }

    const adminGrant = e.target.closest('.wl-admin-grant');
    const adminRevoke = e.target.closest('.wl-admin-revoke');
    if (adminGrant || adminRevoke) {
      closeRowDetail();
      const button = adminGrant || adminRevoke;
      const giving = !!adminGrant;
      const person = whitelistPeople.find(p => String(p.id) === String(button.dataset.id));
      const who = person ? person.username : 'игрока';
      const yes = await confirmDialog(
        giving
          ? `${who} получит в игре админку: спавн, бан и кик. Сервер перечитает список секунд за десять.`
          : `${who} потеряет админку в игре. Доступ на сервер у него останется.`,
        { title: giving ? 'Дать админку?' : 'Забрать админку?', okText: giving ? 'Дать' : 'Забрать', danger: !giving }
      );
      if (!yes) return;
      const res = await fetch(`/api/admin/whitelist/${button.dataset.id}/server-admin`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ server_admin: giving })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) return showToast(data.message || 'Не получилось', 'error');
      // Файл на сервер пишется сразу после решения; если не записался — решение в
      // базе всё равно принято, и об этом надо сказать, а не промолчать.
      showToast(data.applied
        ? `${who}: админка ${giving ? 'выдана' : 'снята'}, список на сервере обновлён`
        : `${who}: админка ${giving ? 'выдана' : 'снята'}, но список на сервер не записался — нажми «Применить на сервере»`,
        data.applied ? 'success' : 'error');
      return loadWhitelist();
    }

    const revoke = e.target.closest('.wl-revoke');
    if (revoke) {
      closeRowDetail();
      // Имя берётся из списка, а не из соседней ячейки: кнопка бывает и в модалке,
      // где никакой соседней ячейки нет.
      const person = whitelistPeople.find(p => String(p.id) === String(revoke.dataset.id));
      const who = person ? person.username : 'игрока';
      const yes = await confirmDialog(
        `${who} пропадёт из permittedlist.txt, и админка в игре снимется тоже.`,
        { title: 'Забрать доступ?', okText: 'Забрать', danger: true }
      );
      if (!yes) return;
      return whitelistDecide(revoke.dataset.id, 'none', null);
    }

    if (e.target.closest('#whitelistAddBtn')) {
      const input = document.getElementById('whitelistSteamId');
      const steamId = input.value.trim();
      if (!steamId) return showToast('Впиши SteamID64 или ссылку на профиль', 'error');
      const res = await fetch('/api/admin/whitelist', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ steam_id: steamId })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) return showToast(data.message || 'Не получилось', 'error');
      input.value = '';
      // Имя приходит из Steam: опечатка в номере всплывает как чужое имя, а не
      // как тишина.
      showToast(`Доступ выдан: ${data.user ? data.user.username : 'готово'}`);
      return loadWhitelist();
    }

    // Списки и так уезжают на сервер после каждого решения; кнопка нужна, когда
    // файлы разъехались с базой — сервер переустановили или файл вернули из копии.
    if (e.target.closest('#applyListsBtn')) {
      const res = await fetch('/api/admin/whitelist/apply', { method: 'POST' });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) return showToast(data.message || 'Не получилось', 'error');
      const r = data.result || {};
      const permitted = r.permitted && r.permitted.written
        ? `доступ: ${r.permitted.count}`
        : `доступ не тронут (${r.permitted ? r.permitted.reason : 'нет данных'})`;
      return showToast(`Списки на сервере обновлены — ${permitted}, админов: ${r.admins ? r.admins.count : 0}`);
    }

    if (e.target.closest('#permittedListBtn')) {
      return downloadList('/api/admin/whitelist/permittedlist', 'permittedlist.txt');
    }
    if (e.target.closest('#adminListBtn')) {
      return downloadList('/api/admin/whitelist/adminlist', 'adminlist.txt');
    }

    if (e.target.closest('#serverAddBtn')) {
      const name = document.getElementById('serverName').value.trim();
      const host = document.getElementById('serverHost').value.trim();
      const port = Number(document.getElementById('serverPort').value);
      const probe = document.getElementById('serverProbe').value;
      const res = await fetch('/api/admin/servers', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, host, port, probe })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) return showToast(data.message || 'Не получилось', 'error');
      document.getElementById('serverName').value = '';
      document.getElementById('serverHost').value = '';
      document.getElementById('serverPort').value = '';
      return loadServersSection();
    }

    if (e.target.closest('#serversRefreshBtn')) {
      await fetch('/api/admin/servers/refresh', { method: 'POST' });
      return loadServersSection();
    }

    const del = e.target.closest('.server-delete');
    if (del) {
      closeRowDetail();
      const yes = await confirmDialog('Сервер пропадёт из списка на сайте. На сам игровой сервер это не влияет.', {
        title: 'Удалить сервер?', okText: 'Удалить', danger: true
      });
      if (!yes) return;
      await fetch(`/api/admin/servers/${del.dataset.id}`, { method: 'DELETE' });
      return loadServersSection();
    }

    // Тап по свёрнутой карточке. Кнопки внутри строки обрабатываются выше и сюда
    // не доходят, но проверка на них всё равно нужна: у кнопки в строке есть свой
    // смысл, и открывать поверх неё карточку — не то, чего от неё ждут.
    if (!astvardCardsMode() || e.target.closest('button, a, label, input')) return;

    const row = e.target.closest('tr[data-id]');
    if (!row || row.classList.contains('empty-row')) return;

    const body = row.parentElement ? row.parentElement.id : '';
    if (body === 'whitelistRequestsBody' || body === 'whitelistAccessBody') {
      const person = whitelistPeople.find(p => String(p.id) === row.dataset.id);
      if (person) showWhitelistDetail(person);
    } else if (body === 'serversTableBody') {
      const server = astvardServers.find(s => String(s.id) === row.dataset.id);
      if (server) showServerDetail(server);
    }
  });
}

bindAstvardHandlers();

// === Постройки для игроков ===
//
// What the mod on the game server applies to players, kept on the site
// (src/gameBuilds.js). A change here goes to the database at once and reaches the
// server on the mod's next pull, a few seconds later; what an admin changes in game
// comes back the same way. A row says «ждёт сервер» until the mod reports the
// revision that carries it.

const BUILD_GROUPS = [
  ['build', 'Постройки для игроков'],
  ['terrain', 'Рельеф для игроков'],
  ['features', 'Функции для игроков']
];
const BUILD_CHOICES = [[0, 'Нельзя'], [1, 'Даром'], [2, 'Платно']];

// The mod asks every few seconds; two minutes of silence is a stopped server or a
// site it cannot reach, not a slow one.
const BUILDS_SILENT_MS = 2 * 60 * 1000;

let buildsState = null;
let buildsHandlersBound = false;

// quiet: the refresh below; a lost session or a site restart must not raise a toast
// every ten seconds.
async function loadBuildsSection({ quiet = false } = {}) {
  try {
    const res = await fetch('/api/admin/builds');
    const data = await res.json().catch(() => ({}));
    if (!res.ok) return quiet ? undefined : showToast(data.message || 'Не удалось загрузить постройки', 'error');
    buildsState = data;
    renderBuilds({ onlyIfChanged: quiet });
  } catch (err) {
    if (!quiet) showToast('Не удалось загрузить постройки', 'error');
  }
}

// While the section is open it looks again every ten seconds, so «ждёт сервер» turns into
// «применено» by itself and a change made in game shows up without a click. Not while a
// number is being typed or the players dialog is open: a redraw would throw that away.
const BUILDS_REFRESH_MS = 10 * 1000;
setInterval(() => {
  const section = document.getElementById('section-builds');
  if (!section || !section.classList.contains('active') || document.visibilityState !== 'visible') return;
  const modal = document.getElementById('rowDetailModalOverlay');
  if (modal && modal.classList.contains('active')) return;
  const focused = document.activeElement;
  if (focused && section.contains(focused) && focused.matches('input, select')) return;
  loadBuildsSection({ quiet: true });
}, BUILDS_REFRESH_MS);

function buildsSyncText(sync) {
  if (!sync.mod_seen_at) {
    return 'Сервер ещё ни разу не приходил сюда за правилами: нужен мод с обменом построек и перезапуск сервера. '
      + 'До этого изменения копятся здесь и доедут, когда он придёт.';
  }
  const seen = astvardDate(sync.mod_seen_at);
  if (Date.now() - new Date(sync.mod_seen_at).getTime() > BUILDS_SILENT_MS) {
    return `Сервер последний раз заходил ${seen} — он остановлен или не достаёт до сайта. Изменения дождутся его.`;
  }
  return sync.mod_applied_revision < sync.revision
    ? `Сервер заходил ${seen}. Последние изменения ещё в пути — обычно это секунды.`
    : `Сервер заходил ${seen}, всё изменённое у него.`;
}

function buildAppliedBadge(item) {
  if (!buildsState.sync.mod_seen_at) return '<span class="badge badge-muted">сервер не подключался</span>';
  return item.applied
    ? '<span class="badge badge-success">применено</span>'
    : '<span class="badge badge-warning">ждёт сервер</span>';
}

function buildAccessBadges(t) {
  const parts = [];
  if (t.for_all) parts.push('<span class="badge badge-success">всем игрокам</span>');
  if (t.players.length) parts.push(`<span class="badge badge-warning">выбранным: ${t.players.length}</span>`);
  if (!parts.length) parts.push('<span class="badge badge-muted">только админ</span>');
  return parts.join(' ');
}

function buildPlayerNames(t) {
  return t.players.map(p => p.username || `номер ${p.steam_id}`).join(', ');
}

// Buttons stay on a row whose file the site cannot see: the site keeps who may build
// it by name, and the mod finds the file by that name whether the site sees it or not.
function buildTemplateButtons(t, index) {
  return `<button class="btn btn-sm ${t.for_all ? 'btn-secondary' : ''} build-all" data-index="${index}">${t.for_all ? 'Не для всех' : 'Открыть всем'}</button>`
    + `<button class="btn btn-sm btn-secondary build-players" data-index="${index}">Кому…</button>`
    + `<button class="btn btn-sm btn-secondary build-settings" data-index="${index}">Настроить</button>`;
}

// Что уже попрошено у сервера и ещё не сделано. Без этой строки «Удалить» выглядит как
// нажатие, которое ничего не сделало: файл убирает мод, и до его прихода постройка
// стоит в списке как ни в чём не бывало.
function buildJobNote(t) {
  const jobs = (buildsState.jobs || []).filter((j) => j.name === t.name);
  if (!jobs.length) return '';

  const said = jobs.map((j) => (j.kind === 'delete' ? 'убрать'
    : j.kind === 'rename' ? `переименовать в «${j.value}»`
      : `категория «${j.value}»`));

  return `<div class="cell-note cell-note-warn">Ждёт сервер: ${escapeHtml(said.join(', '))}</div>`;
}

/**
 * Настройка одной постройки: имя, категория и «Убрать».
 *
 * Ничего из этого сайт не делает сам — файлы построек пишет только мод, и прав на его
 * папку у сайта нет. Отсюда уезжает просьба, а сделает её сервер на ближайшем круге.
 */
function showBuildSettingsDialog(t, index) {
  showRowDetail(`Настройка «${t.name}»`, [],
    `<button class="btn btn-sm build-settings-save" data-index="${index}">Сохранить</button>`
    + `<button class="btn btn-sm btn-danger build-delete" data-index="${index}">Убрать постройку</button>`);

  document.getElementById('rowDetailBody').innerHTML = `
    <p class="muted" style="margin:0 0 10px;">Имя и категорию меняет игровой сервер: отсюда уезжает просьба, и через несколько секунд он её сделает. Имя постройки — это и имя её файла, поэтому занятое имя он не возьмёт.</p>
    <div class="form-group">
      <label for="buildNewName">Имя</label>
      <input type="text" class="form-control" id="buildNewName" value="${escapeHtml(t.name)}" maxlength="80">
    </div>
    <div class="form-group">
      <label for="buildNewCategory">Категория</label>
      <input type="text" class="form-control" id="buildNewCategory" value="${escapeHtml(t.category || '')}" maxlength="80" placeholder="Разное">
    </div>
    ${t.on_server ? '' : '<p class="muted">Сайт не видит файл этой постройки, так что менять в ней нечего — сперва пусть сервер пришлёт её заново.</p>'}`;
}

async function askBuildJob(kind, name, value) {
  try {
    const res = await fetch('/api/admin/builds/template/job', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, name, value })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.success) {
      showToast(data.message || 'Не удалось попросить сервер', 'error');
      return false;
    }
    return true;
  } catch (err) {
    showToast('Ошибка сети', 'error');
    return false;
  }
}

// What the tables were last drawn from. The refresh redraws them only when this changes:
// the server comes by every few seconds, and a row replaced that often vanishes from
// under a click that happens to land on the redraw.
let buildsDrawn = '';

function renderBuilds({ onlyIfChanged = false } = {}) {
  if (!buildsState) return;

  const line = document.getElementById('buildsSyncLine');
  if (line) line.textContent = buildsSyncText(buildsState.sync);

  const drawn = JSON.stringify([buildsState.templates, buildsState.rules, buildsState.players, buildsState.jobs, !!buildsState.sync.mod_seen_at]);
  if (onlyIfChanged && drawn === buildsDrawn) return;
  buildsDrawn = drawn;

  const tbody = document.getElementById('buildsTemplatesBody');
  if (tbody) {
    const list = buildsState.templates;
    tbody.innerHTML = list.length
      ? list.map((t, i) => `
        <tr data-index="${i}">
          <td class="mobile-primary">
            <strong>${escapeHtml(t.name)}</strong>
            <div class="row-note">${buildAccessBadges(t)}</div>
            ${t.on_server ? '' : '<div class="cell-note cell-note-warn">Сайт не видит файл этой постройки: её переименовали или убрали в игре, или сайту не видна папка шаблонов</div>'}
            ${t.submitted ? '<div class="cell-note">Прислал игрок</div>' : ''}
            ${buildJobNote(t)}
          </td>
          <td class="mobile-hidden">${escapeHtml(t.category || '—')}</td>
          <td class="mobile-hidden">${t.pieces === null ? '—' : t.pieces}</td>
          <td class="mobile-hidden">
            ${buildAccessBadges(t)}
            ${t.players.length ? `<div class="cell-note">${escapeHtml(buildPlayerNames(t))}</div>` : ''}
          </td>
          <td class="mobile-hidden">${buildAppliedBadge(t)}</td>
          <td class="no-label"><div class="row-actions">${buildTemplateButtons(t, i)}</div></td>
        </tr>`).join('')
      : '<tr class="empty-row"><td colspan="6" class="empty-state">Общих построек на сервере пока нет — их выкладывает админ из игры</td></tr>';
  }

  const box = document.getElementById('buildsRules');
  if (box) box.innerHTML = renderBuildRules(buildsState.rules);
}

function renderBuildRules(rules) {
  if (!rules.length) {
    return '<h3 style="margin:24px 0 8px;">Правила</h3><div class="table-container"><div class="empty-state">'
      + 'Список правил присылает мод сервера, когда запускается. Пока сервер с ним не приходил, менять здесь нечего.'
      + '</div></div>';
  }
  return BUILD_GROUPS.map(([group, title]) => {
    const list = rules.filter(r => r.group === group);
    if (!list.length) return '';
    return `<h3 style="margin:24px 0 8px;">${title}</h3>
      <div class="table-container"><div class="rule-list">${list.map(buildRuleRow).join('')}</div></div>`;
  }).join('');
}

function buildRuleControl(r) {
  const number = (cls, value) => `<input type="number" class="form-control rule-number ${cls}" data-key="${r.key}"`
    + ` min="${r.min === null ? 0 : r.min}"${r.max === null ? '' : ` max="${r.max}"`} value="${value === null ? '' : value}">`;
  const unit = r.word ? `<span class="muted">${escapeHtml(r.word)}${r.kind === 'number' ? '' : ', м'}</span>` : '';
  const choice = () => `<select class="form-control rule-choice" data-key="${r.key}">`
    + BUILD_CHOICES.map(([v, label]) => `<option value="${v}"${r.value === v ? ' selected' : ''}>${label}</option>`).join('')
    + '</select>';

  switch (r.kind) {
    case 'toggle':
      return `<label class="check-chip"><input type="checkbox" class="rule-open" data-key="${r.key}"${r.value ? ' checked' : ''}> да</label>`;
    case 'limit':
      return `<label class="check-chip"><input type="checkbox" class="rule-open" data-key="${r.key}"${r.value ? ' checked' : ''}> можно</label>`
        + ` ${unit} ${number('rule-limit', r.limit)}`;
    case 'choice':
      return choice();
    case 'choicelimit':
      return `${choice()} ${unit} ${number('rule-limit', r.limit)}`;
    default:
      return `${number('rule-value', r.value)} ${unit}`;
  }
}

function buildRuleRow(r) {
  const who = r.updated_by
    ? `<div class="cell-note">${escapeHtml(r.updated_by === 'game' ? 'из игры' : r.updated_by)}, ${astvardDate(r.updated_at)}</div>`
    : '';
  return `
    <div class="rule-row" data-key="${r.key}">
      <div class="rule-info">
        <div class="rule-title">${escapeHtml(r.title)}</div>
        ${r.note ? `<div class="cell-note">${escapeHtml(r.note)}</div>` : ''}
      </div>
      <div class="rule-controls">${buildRuleControl(r)}</div>
      <div class="rule-state">${buildAppliedBadge(r)}${who}</div>
    </div>`;
}

async function saveBuildRule(key) {
  const row = document.querySelector(`.rule-row[data-key="${key}"]`);
  const rule = buildsState && buildsState.rules.find(r => r.key === key);
  if (!row || !rule) return;

  const body = {};
  const open = row.querySelector('.rule-open');
  const choice = row.querySelector('.rule-choice');
  const value = row.querySelector('.rule-value');
  const limit = row.querySelector('.rule-limit');
  if (open) body.value = open.checked ? 1 : 0;
  if (choice) body.value = Number(choice.value);
  if (value) body.value = value.value === '' ? null : Number(value.value);
  if (limit) body.limit = limit.value === '' ? null : Number(limit.value);

  const res = await fetch(`/api/admin/builds/rules/${key}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) showToast(data.message || 'Не получилось', 'error');
  else showToast(`${rule.title}: сохранено, сервер применит через несколько секунд`);
  return loadBuildsSection();
}

async function saveBuildTemplate(t, patch) {
  const res = await fetch('/api/admin/builds/template', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: t.name, ...patch })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    showToast(data.message || 'Не получилось', 'error');
    return false;
  }
  return true;
}

// The shared row modal, with a checklist for a body: the players with access to the
// server, and anyone still named on the template who has lost it since.
function showBuildPlayersDialog(t, index) {
  const chosen = new Set(t.players.map(p => p.steam_id));
  const people = buildsState.players.map(p => ({ steam_id: p.steam_id, label: p.username }));
  for (const p of t.players) {
    if (!people.some(q => q.steam_id === p.steam_id)) {
      people.push({ steam_id: p.steam_id, label: p.username ? `${p.username} (без доступа)` : `номер ${p.steam_id}` });
    }
  }

  showRowDetail(`Кому открыта «${t.name}»`, [],
    `<button class="btn btn-sm build-players-save" data-index="${index}">Сохранить</button>`);
  document.getElementById('rowDetailBody').innerHTML = `
    <p class="muted" style="margin:0 0 10px;">Отмеченные игроки смогут поставить эту постройку, даже если она не открыта всем.${t.for_all ? ' Сейчас она открыта всем, и список пригодится, когда её закроют.' : ''}</p>
    ${people.length > 8 ? '<input type="text" class="form-control" id="buildPlayersSearch" placeholder="Поиск по нику..." style="margin-bottom:10px;">' : ''}
    <div class="build-players" id="buildPlayersList">
      ${people.length
        ? people.map(p => `
          <label class="check-chip build-player" data-name="${escapeHtml(p.label.toLowerCase())}">
            <input type="checkbox" value="${p.steam_id}"${chosen.has(p.steam_id) ? ' checked' : ''}> ${escapeHtml(p.label)}
          </label>`).join('')
        : '<div class="empty-state">Игроков с доступом на сервер пока нет</div>'}
    </div>`;
}

function showBuildTemplateDetail(t, index) {
  showRowDetail(t.name, [
    ['Категория', t.category || '—'],
    ['Деталей', t.pieces === null ? '—' : String(t.pieces)],
    ['Кому открыта', buildAccessBadges(t), true],
    ['Выбранные', buildPlayerNames(t), 'block'],
    ['На сервере', buildAppliedBadge(t), true]
  ], buildTemplateButtons(t, index));
}

function bindBuildsHandlers() {
  if (buildsHandlersBound) return;
  buildsHandlersBound = true;

  document.addEventListener('change', (e) => {
    const control = e.target.closest('.rule-row .rule-open, .rule-row .rule-choice, .rule-row .rule-value, .rule-row .rule-limit');
    if (control) saveBuildRule(control.dataset.key);
  });

  document.addEventListener('input', (e) => {
    if (e.target.id !== 'buildPlayersSearch') return;
    const query = e.target.value.trim().toLowerCase();
    document.querySelectorAll('#buildPlayersList .build-player').forEach((el) => {
      el.style.display = !query || el.dataset.name.includes(query) ? '' : 'none';
    });
  });

  document.addEventListener('click', async (e) => {
    if (e.target.closest('#buildsRefreshBtn')) return loadBuildsSection();

    const templateAt = (el) => buildsState && buildsState.templates[Number(el.dataset.index)];

    const allButton = e.target.closest('.build-all');
    if (allButton) {
      const t = templateAt(allButton);
      if (!t) return;
      closeRowDetail();
      if (await saveBuildTemplate(t, { for_all: !t.for_all })) {
        showToast(t.for_all ? `«${t.name}»: больше не открыта всем` : `«${t.name}»: открыта всем игрокам`);
      }
      return loadBuildsSection();
    }

    const playersButton = e.target.closest('.build-players');
    if (playersButton) {
      const t = templateAt(playersButton);
      if (t) showBuildPlayersDialog(t, Number(playersButton.dataset.index));
      return;
    }

    const settingsButton = e.target.closest('.build-settings');
    if (settingsButton) {
      const t = templateAt(settingsButton);
      if (t) showBuildSettingsDialog(t, Number(settingsButton.dataset.index));
      return;
    }

    const settingsSave = e.target.closest('.build-settings-save');
    if (settingsSave) {
      const t = templateAt(settingsSave);
      if (!t) return;

      const name = (document.getElementById('buildNewName')?.value || '').trim();
      const category = (document.getElementById('buildNewCategory')?.value || '').trim();
      let asked = 0;

      if (category && category !== (t.category || '') && await askBuildJob('category', t.name, category)) asked++;
      // Имя — последним: после него постройка зовётся иначе, и просьба про категорию
      // ушла бы к имени, которого уже нет.
      if (name && name !== t.name && await askBuildJob('rename', t.name, name)) asked++;

      closeRowDetail();
      showToast(asked ? 'Отправлено серверу' : 'Менять нечего');
      return loadBuildsSection();
    }

    const deleteButton = e.target.closest('.build-delete');
    if (deleteButton) {
      const t = templateAt(deleteButton);
      if (!t) return;

      closeRowDetail();
      const yes = await confirmDialog(
        'Сервер уберёт её из общих. Файл он не стирает, а откладывает в подпапку «deleted» — вернуть можно руками.',
        { title: `Убрать «${t.name}»?`, okText: 'Убрать', danger: true }
      );
      if (!yes) return;

      if (await askBuildJob('delete', t.name, '')) showToast(`«${t.name}»: сервер уберёт её`);
      return loadBuildsSection();
    }

    const saveButton = e.target.closest('.build-players-save');
    if (saveButton) {
      const t = templateAt(saveButton);
      if (!t) return;
      const ids = [...document.querySelectorAll('#buildPlayersList input:checked')].map(input => input.value);
      if (await saveBuildTemplate(t, { players: ids })) {
        closeRowDetail();
        showToast(`«${t.name}»: выбранных игроков — ${ids.length}`);
      }
      return loadBuildsSection();
    }

    // A tap on a folded card, as in the tables above.
    if (!astvardCardsMode() || e.target.closest('button, a, label, input, select')) return;
    const row = e.target.closest('#buildsTemplatesBody tr[data-index]');
    const t = row && templateAt(row);
    if (t) showBuildTemplateDetail(t, Number(row.dataset.index));
  });
}

bindBuildsHandlers();

// ---------------- Сортировка ----------------
//
// По какой полке сортировщик раскладывает предмет. Список предметов сайт не придумывает
// и придумывать не должен: его присылает мод, который один знает, что в игре есть, — и
// в новой версии игры новые предметы появятся здесь сами, без правки сайта. Поэтому тут
// нельзя «добавить предмет», только выбрать полку среди присланного.
//
// Пустой выбор — не «Разное». Это «решает мод», то есть по типу предмета, и разница
// видна на первом же незнакомом предмете: «Разное» — это ответ, а пустой выбор — его
// отсутствие.
//
// Предметов под тысячу (982 на боевом), и отсюда всё остальное здесь: полки сверху
// говорят, сколько предметов ляжет на каждую, и они же фильтр; отмеченное правится
// пачкой — одним запросом и одной ревизией; сохранённая строка перерисовывается одна,
// а не вся таблица.

let sortingState = null;
let sortingHandlersBound = false;

// Отметки живут отдельно от отрисовки: список сам перечитывается раз в десять секунд,
// и собранная пачка не должна от этого рассыпаться.
const sortingMarks = new Set();

// Фильтр по полке: номер категории или null — «все».
let sortingShelf = null;
let sortingSearchTimer = null;

// Тип — слово игры (ItemType), и приходит оно по-английски. Перевод нужен только для
// фильтра, а незнакомый тип показывается как есть: новая версия игры добавит строку без
// перевода, а не сломает страницу. Сверено по самим предметам с боевого сервера, а не по
// памяти: TwoHandedWeaponLeft — это посохи, Customization — причёски и бороды.
const SORTING_TYPES = {
  Material: 'материалы',
  Consumable: 'еда и зелья',
  OneHandedWeapon: 'одноручное оружие',
  TwoHandedWeapon: 'двуручное оружие',
  TwoHandedWeaponLeft: 'посохи',
  Bow: 'луки и арбалеты',
  Shield: 'щиты',
  Ammo: 'стрелы и болты',
  AmmoNonEquipable: 'снаряды баллисты',
  Chest: 'нагрудники',
  Helmet: 'шлемы',
  Legs: 'поножи',
  Shoulder: 'плащи',
  Utility: 'пояса и амулеты',
  Trinket: 'подвески и обереги',
  Trophy: 'трофеи',
  Tool: 'инструменты',
  Torch: 'факелы и фонари',
  Fish: 'рыба',
  Customization: 'причёски и бороды',
  Misc: 'прочее'
};

async function loadSortingSection({ quiet = false } = {}) {
  try {
    const res = await fetch('/api/admin/sorting');
    const data = await res.json().catch(() => ({}));
    if (!res.ok) return quiet ? undefined : showToast(data.message || 'Не удалось загрузить сортировку', 'error');
    sortingState = data;

    // Отметка на предмете, которого в каталоге больше нет, — это пачка, которая молча
    // не доедет: сервер такой ключ пропустит, а в счётчике он будет.
    const known = new Set((data.items || []).map((item) => item.kind));
    Array.from(sortingMarks).forEach((kind) => { if (!known.has(kind)) sortingMarks.delete(kind); });

    renderSorting();
  } catch (err) {
    if (!quiet) showToast('Не удалось загрузить сортировку', 'error');
  }
  return undefined;
}

function sortingSyncText() {
  if (!sortingState.seeded || !sortingState.modSeenAt) {
    return 'Игровой сервер ещё ни разу не присылал сюда список предметов. Нужен мод, который умеет '
      + 'этот обмен, и перезапуск сервера — до тех пор выбирать не из чего.';
  }
  const seen = astvardDate(sortingState.modSeenAt);
  if (Date.now() - new Date(sortingState.modSeenAt).getTime() > BUILDS_SILENT_MS) {
    return `Сервер последний раз заходил ${seen} — он остановлен или не достаёт до сайта. Выбранное дождётся его.`;
  }
  return sortingState.modAppliedRevision < sortingState.revision
    ? `Сервер заходил ${seen}. Последнее изменение ещё в пути — обычно это до минуты.`
    : `Сервер заходил ${seen}, всё выбранное у него.`;
}

function sortingTitle(category) {
  const titles = (sortingState && sortingState.categories) || [];
  return titles[category] || `№${category}`;
}

// Куда предмет ляжет на самом деле: выбор админа, а если его нет — решение мода по типу.
// Перепись по полкам считается по этому: сундуки готовят под итог, а не под то, что мод
// думал до правок.
function sortingShelfOf(item) {
  return item.category === null ? item.modCategory : item.category;
}

const sortingTypeShort = (type) => SORTING_TYPES[type] || type;
const sortingTypeTitle = (type) => (SORTING_TYPES[type] ? `${SORTING_TYPES[type]} (${type})` : type);

function sortingFilters() {
  const value = (id) => {
    const el = document.getElementById(id);
    return el ? el.value : '';
  };
  return {
    query: value('sortingSearch').trim().toLowerCase(),
    decided: value('sortingDecided'),
    type: value('sortingType')
  };
}

function sortingRows() {
  const { query, decided, type } = sortingFilters();

  return (sortingState.items || []).filter((item) => {
    if (sortingShelf !== null && sortingShelfOf(item) !== sortingShelf) return false;
    if (decided === 'hand' && item.category === null) return false;
    if (decided === 'mod' && item.category !== null) return false;
    if (type && item.type !== type) return false;
    if (!query) return true;
    return `${item.title} ${item.kind} ${item.type}`.toLowerCase().includes(query);
  });
}

function sortingOptions(chosen) {
  return (sortingState.categories || [])
    .map((title, at) => `<option value="${at}"${chosen === at ? ' selected' : ''}>${escapeHtml(title)}</option>`)
    .join('');
}

function renderSortingSync() {
  const line = document.getElementById('sortingSyncLine');
  if (line) line.textContent = sortingSyncText();
}

// Полки: число — сколько предметов на неё ляжет, подсказка — сколько из них решено
// руками. Нажатие оставляет в таблице только эту полку, повторное снимает фильтр:
// иначе с полки нет дороги назад, кроме «Всех».
function renderSortingShelves() {
  const box = document.getElementById('sortingShelves');
  if (!box) return;

  const items = sortingState.items || [];
  const titles = sortingState.categories || [];
  const total = titles.map(() => 0);
  const byHand = titles.map(() => 0);

  items.forEach((item) => {
    const at = sortingShelfOf(item);
    if (total[at] === undefined) return;
    total[at] += 1;
    if (item.category !== null) byHand[at] += 1;
  });

  const chip = (label, count, value, hint) => `<button type="button" class="shelf-chip`
    + `${sortingShelf === value ? ' active' : ''}" data-shelf="${value === null ? '' : value}"`
    + ` title="${escapeHtml(hint)}">${escapeHtml(label)}`
    + ` <span class="shelf-chip__count">${count}</span></button>`;

  box.innerHTML = [chip('Все полки', items.length, null, 'Показать все предметы')]
    .concat(titles.map((title, at) => chip(
      title, total[at], at, `Выбрано руками: ${byHand[at]}, остальное решает сервер`
    )))
    .join('');
}

// Типы — из того же каталога, что и предметы. Список пересобирается, только когда он и
// правда изменился: иначе выбранный фильтр слетал бы при каждом обновлении раздела.
function renderSortingTypes() {
  const select = document.getElementById('sortingType');
  if (!select) return;

  const types = Array.from(new Set((sortingState.items || []).map((item) => item.type).filter(Boolean)));
  types.sort((a, b) => sortingTypeTitle(a).localeCompare(sortingTypeTitle(b), 'ru'));

  const signature = types.join('|');
  if (select.dataset.signature === signature) return;

  const chosen = select.value;
  select.dataset.signature = signature;
  select.innerHTML = '<option value="">Любой тип</option>'
    + types.map((type) => `<option value="${escapeHtml(type)}">${escapeHtml(sortingTypeTitle(type))}</option>`).join('');
  select.value = types.includes(chosen) ? chosen : '';
}

/**
 * Редактор полок. Номер показан рядом с именем нарочно: он и есть то, чем помечены
 * сундуки в игре, и по нему потом читается лог сервера.
 */
function renderSortingCategories() {
  const box = document.getElementById('sortingCategoriesBody');
  if (!box) return;

  const rows = sortingState.categoryRows || [];
  if (!rows.length) {
    box.innerHTML = '<p class="muted">Сервер ещё не присылал список полок.</p>';
    return;
  }

  box.innerHTML = rows.map((row) => {
    const marks = [];
    if (row.builtIn) marks.push('<span class="badge badge-muted">встроенная</span>');
    if (row.removed) marks.push('<span class="badge badge-warning">убрана</span>');

    // Встроенную убрать нельзя: по ней сервер раскладывает сам, когда о предмете
    // ничего не сказано, и полка без имени осталась бы над половиной базы.
    const toggle = row.builtIn
      ? ''
      : `<button class="btn btn-sm btn-secondary shelf-toggle" data-id="${row.id}"`
        + ` data-removed="${row.removed}">${row.removed ? 'Вернуть' : 'Убрать'}</button>`;

    return `<div class="shelf-row${row.removed ? ' removed' : ''}" data-id="${row.id}">`
      + `<span class="shelf-row__id">№${row.id}</span>`
      + `<input type="text" class="form-control shelf-title" maxlength="24"`
      + ` data-id="${row.id}" value="${escapeHtml(row.title)}">`
      + marks.join(' ') + toggle
      + '</div>';
  }).join('');
}

function renderSortingCount(shown) {
  const line = document.getElementById('sortingCount');
  if (!line) return;

  const items = sortingState.items || [];
  const hand = items.filter((item) => item.category !== null).length;
  line.textContent = `Показано ${shown} из ${items.length} · выбрано руками ${hand}`;
}

function renderSortingBulk() {
  const bar = document.getElementById('sortingBulk');
  if (!bar) return;

  bar.hidden = sortingMarks.size === 0;

  const count = document.getElementById('sortingBulkCount');
  if (count) count.textContent = `Отмечено ${sortingMarks.size}`;

  const pick = document.getElementById('sortingBulkPick');
  const titles = String((sortingState.categories || []).length);
  if (pick && pick.dataset.filled !== titles) {
    pick.dataset.filled = titles;
    pick.innerHTML = '<option value="">решает сервер</option>' + sortingOptions(null);
  }
}

// Куда предмет ляжет, словами. Одно место на бейдж в строке и на подпись в карточке:
// разъехаться им нельзя, это один и тот же ответ.
function sortingWhere(item) {
  return item.category === null
    ? `по типу: ${sortingTitle(item.modCategory)}`
    : sortingTitle(item.category);
}

// Ячейки строки отдельно от самой строки: после сохранения перерисовываются только они.
//
// В свёрнутой карточке имени достаётся то, что осталось от соседей, а колонка «Сейчас»
// не сжимается — на 375 px имени доставалось 46. Поэтому на телефоне полка уходит
// подписью под имя, как в остальных таблицах панели, а сама колонка прячется.
function sortingRowCells(item) {
  const marked = sortingMarks.has(item.kind) ? ' checked' : '';
  const now = item.category === null
    ? `<span class="badge badge-muted">${escapeHtml(sortingWhere(item))}</span>`
    : `<span class="badge badge-success">${escapeHtml(sortingWhere(item))}</span>`;
  // Кто и когда — только у выбранного руками: у решения сервера автора нет.
  const who = item.category !== null && item.updatedBy
    ? `<div class="cell-note">${escapeHtml(item.updatedBy)}, ${astvardDate(item.updatedAt)}</div>`
    : '';

  return `<td class="sorting-check">`
      + `<input type="checkbox" class="sorting-mark" data-kind="${escapeHtml(item.kind)}"${marked}`
      + ` aria-label="Отметить предмет"></td>`
    + `<td class="mobile-primary"><div>${escapeHtml(item.title || item.kind)}</div>`
      + `<div class="cell-note hide-mobile">${escapeHtml(item.kind)}`
      + `${item.type ? ' · ' + escapeHtml(sortingTypeShort(item.type)) : ''}</div>`
      + `<div class="row-note">${escapeHtml(sortingWhere(item))}</div></td>`
    + `<td class="mobile-hidden">${now}${who}</td>`
    + `<td class="mobile-hidden"><select class="sorting-pick" data-kind="${escapeHtml(item.kind)}">`
      + `<option value=""${item.category === null ? ' selected' : ''}>решает сервер</option>`
      + `${sortingOptions(item.category)}</select></td>`;
}

function renderSorting() {
  if (!sortingState) return;

  renderSortingSync();
  renderSortingShelves();
  renderSortingCategories();
  renderSortingTypes();
  renderSortingBulk();

  const body = document.getElementById('sortingItemsBody');
  if (!body) return;

  const items = sortingRows();
  renderSortingCount(items.length);

  body.innerHTML = items.length
    ? items.map((item) => `<tr data-kind="${escapeHtml(item.kind)}">${sortingRowCells(item)}</tr>`).join('')
    : `<tr class="empty-row"><td colspan="4" class="empty-state">`
      + (sortingState.seeded ? 'Ничего не нашлось' : 'Сервер ещё не присылал список предметов')
      + `</td></tr>`;
}

// Одна строка вместо всей таблицы: строк под тысячу, и перерисовать их ради одной
// ячейки — значит увести из-под руки и прокрутку, и место, где человек работал.
// Предмет, переставший подходить под фильтр, останется на виду до следующего
// обновления — а оно не приходит, пока курсор в этом же разделе.
function refreshSortingRow(kind) {
  const item = (sortingState.items || []).find((row) => row.kind === kind);
  const tr = document.querySelector(`#sortingItemsBody tr[data-kind="${kind}"]`);
  if (item && tr) tr.innerHTML = sortingRowCells(item);

  renderSortingSync();
  renderSortingShelves();
  renderSortingCount(sortingRows().length);
}

// Полная строка в модалке. На телефоне в карточке видно имя и полку, а выбрать полку
// надо уметь и там — поэтому тот же список идёт кнопкой диалога.
function showSortingDetail(kind) {
  const item = (sortingState.items || []).find((row) => row.kind === kind);
  if (!item) return;

  const rows = [
    ['Ключ', `<code>${escapeHtml(item.kind)}</code>`, true],
    ['Тип в игре', item.type ? sortingTypeTitle(item.type) : '—'],
    ['Ляжет на полку', sortingTitle(sortingShelfOf(item))],
    ['Сервер положил бы', sortingTitle(item.modCategory)]
  ];
  if (item.category !== null && item.updatedBy) {
    rows.push(['Выбрал', `${item.updatedBy}, ${astvardDate(item.updatedAt)}`]);
  }

  showRowDetail(item.title || item.kind, rows,
    `<select class="sorting-pick form-control" data-kind="${escapeHtml(item.kind)}">`
    + `<option value=""${item.category === null ? ' selected' : ''}>решает сервер</option>`
    + `${sortingOptions(item.category)}</select>`);
}

// Раз в десять секунд, пока раздел открыт: «ещё в пути» само становится «у него», а
// смена, сделанная в другой вкладке, видна без нажатий. Не во время набора в поиске и
// не когда открыт какой-нибудь список: перерисовка стёрла бы и то и другое.
const SORTING_REFRESH_MS = 10 * 1000;
setInterval(() => {
  const section = document.getElementById('section-sorting');
  if (!section || !section.classList.contains('active') || document.visibilityState !== 'visible') return;
  const focused = document.activeElement;
  if (focused && section.contains(focused) && focused.matches('input, select')) return;
  loadSortingSection({ quiet: true });
}, SORTING_REFRESH_MS);

async function saveSortingPick(kind, value) {
  try {
    const res = await fetch('/api/admin/sorting/item', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, category: value === '' ? null : Number(value) }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.success) {
      showToast(data.message || 'Не удалось сохранить', 'error');
      return null;
    }
    return data;
  } catch (err) {
    showToast('Ошибка сети', 'error');
    return null;
  }
}

async function sortingBulkApply() {
  const kinds = Array.from(sortingMarks);
  if (!kinds.length) return undefined;

  const pick = document.getElementById('sortingBulkPick');
  const value = pick ? pick.value : '';
  const said = value === '' ? 'Вернуть решение серверу' : `Переложить в «${sortingTitle(Number(value))}»`;

  // Промах в списке на тысячу строк уводит не туда сразу сотню предметов, и кнопки
  // «как было» у этого нет.
  const yes = await confirmDialog(`Отмечено предметов: ${kinds.length}. ${said}?`, { okText: 'Применить' });
  if (!yes) return undefined;

  try {
    const res = await fetch('/api/admin/sorting/items', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kinds, category: value === '' ? null : Number(value) }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.success) return showToast(data.message || 'Не удалось переложить', 'error');

    sortingMarks.clear();
    showToast(`Предметов переложено: ${data.changed}`);
    return loadSortingSection();
  } catch (err) {
    return showToast('Ошибка сети', 'error');
  }
}

function bindSortingHandlers() {
  if (sortingHandlersBound) return;
  sortingHandlersBound = true;

  document.addEventListener('click', (e) => {
    if (e.target.closest('#sortingRefreshBtn')) return loadSortingSection();
    if (!sortingState) return undefined;

    const chip = e.target.closest('.shelf-chip');
    if (chip) {
      const value = chip.dataset.shelf === '' ? null : Number(chip.dataset.shelf);
      sortingShelf = sortingShelf === value ? null : value;
      return renderSorting();
    }

    if (e.target.closest('#sortingMarkFound')) {
      sortingRows().forEach((item) => sortingMarks.add(item.kind));
      return renderSorting();
    }

    if (e.target.closest('#sortingBulkClear')) {
      sortingMarks.clear();
      return renderSorting();
    }

    if (e.target.closest('#sortingBulkApply')) return sortingBulkApply();

    const toggle = e.target.closest('.shelf-toggle');
    if (toggle) return sortingToggleCategory(Number(toggle.dataset.id), toggle.dataset.removed !== 'true');

    // Тап по свёрнутой карточке — как в таблицах выше. Галочка и список из этого
    // исключены: по ним тап значит своё.
    if (!astvardCardsMode() || e.target.closest('button, a, label, input, select')) return undefined;
    const row = e.target.closest('#sortingItemsBody tr[data-kind]');
    if (row) showSortingDetail(row.dataset.kind);
    return undefined;
  });

  document.addEventListener('submit', (e) => {
    if (e.target.id !== 'sortingCategoryAdd') return;
    e.preventDefault();
    sortingAddCategory();
  });

  document.addEventListener('input', (e) => {
    if (e.target.id !== 'sortingSearch' || !sortingState) return;
    // Перерисовка почти тысячи строк на каждую букву чувствуется пальцами.
    clearTimeout(sortingSearchTimer);
    sortingSearchTimer = setTimeout(renderSorting, 150);
  });

  document.addEventListener('change', async (e) => {
    if (!sortingState) return undefined;

    if (e.target.id === 'sortingDecided' || e.target.id === 'sortingType') return renderSorting();

    const mark = e.target.closest('.sorting-mark');
    if (mark) {
      if (mark.checked) sortingMarks.add(mark.dataset.kind);
      else sortingMarks.delete(mark.dataset.kind);
      return renderSortingBulk();
    }

    const title = e.target.closest('.shelf-title');
    if (title) return sortingRenameCategory(Number(title.dataset.id), title.value);

    const pick = e.target.closest('.sorting-pick');
    if (!pick) return undefined;

    const kind = pick.dataset.kind;
    const item = (sortingState.items || []).find((row) => row.kind === kind);
    const saved = await saveSortingPick(kind, pick.value);
    if (!saved) return loadSortingSection();

    sortingState.revision = saved.revision;
    if (item) {
      item.category = pick.value === '' ? null : Number(pick.value);
      item.updatedBy = item.category === null ? null : (currentUser ? currentUser.username : null);
      item.updatedAt = new Date().toISOString();
    }

    closeRowDetail();
    refreshSortingRow(kind);
    showToast(`${item ? item.title || kind : kind}: ${pick.value === '' ? 'решает сервер' : `«${sortingTitle(Number(pick.value))}»`}`);
    return undefined;
  });
}

/** Новая полка. Номер выдаёт сервер — здесь его не выбирают и выбрать нельзя. */
async function sortingAddCategory() {
  const field = document.getElementById('sortingCategoryTitle');
  const title = field ? field.value.trim() : '';
  if (!title) return;

  const res = await fetch('/api/admin/sorting/categories', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title })
  });

  const data = await res.json().catch(() => ({}));
  if (!res.ok) return showToast(data.message || 'Не удалось завести полку', 'error');

  if (field) field.value = '';
  showToast(`Полка «${title}» заведена под номером ${data.id}`);
  return loadSortingSection();
}

async function sortingRenameCategory(id, title) {
  const name = String(title || '').trim();
  const was = (sortingState.categoryRows || []).find((row) => row.id === id);
  if (!was || name === was.title) return undefined;

  const res = await fetch('/api/admin/sorting/categories', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id, title: name })
  });

  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    showToast(data.message || 'Не удалось переименовать', 'error');
    return loadSortingSection();
  }

  showToast(`«${was.title}» теперь «${name}»`);
  return loadSortingSection();
}

async function sortingToggleCategory(id, removed) {
  const row = (sortingState.categoryRows || []).find((one) => one.id === id);
  if (!row) return undefined;

  if (removed) {
    const yes = await confirmDialog(
      `Сундуки, помеченные «${row.title}», останутся помеченными — сервер знает их по номеру, `
      + 'а не по имени, и разнесёт то, что в них лежит, по остальным полкам. Предметы, '
      + 'отправленные на эту полку, вернутся под решение сервера.',
      { title: `Убрать полку «${row.title}»?`, okText: 'Убрать', danger: true }
    );

    if (!yes) return undefined;
  }

  const res = await fetch('/api/admin/sorting/categories', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id, removed })
  });

  const data = await res.json().catch(() => ({}));
  if (!res.ok) return showToast(data.message || 'Не вышло', 'error');

  showToast(removed ? `Полка «${row.title}» убрана` : `Полка «${row.title}» вернулась`);
  return loadSortingSection();
}

bindSortingHandlers();
