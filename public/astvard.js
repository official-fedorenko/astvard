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

// Галочка «админка в игре» — только суперадмину: выдать её значит выдать спавн,
// бан и кик. Остальные видят, есть она или нет.
function whitelistAdminCell(p) {
  return currentUser && currentUser.role === 'Superadmin'
    ? `<label class="check-chip"><input type="checkbox" class="wl-server-admin" data-id="${p.id}" ${p.server_admin ? 'checked' : ''}><span>в игре</span></label>`
    : (p.server_admin ? 'да' : '—');
}

function whitelistButtons(p) {
  if (p.whitelist_status === 'pending') {
    return `<button class="btn btn-sm wl-approve" data-id="${p.id}">Одобрить</button>
            <button class="btn btn-sm btn-secondary wl-reject" data-id="${p.id}">Отклонить</button>`;
  }
  return p.whitelist_status === 'approved'
    ? `<button class="btn btn-sm btn-danger wl-revoke" data-id="${p.id}">Забрать доступ</button>`
    : `<button class="btn btn-sm wl-approve" data-id="${p.id}">Дать доступ</button>`;
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

  const adminToggle = currentUser && currentUser.role === 'Superadmin'
    ? `<label class="check-chip"><input type="checkbox" class="wl-server-admin" data-id="${p.id}" ${p.server_admin ? 'checked' : ''}><span>админка в игре</span></label>`
    : '';
  showRowDetail(p.username, rows, `${adminToggle}${whitelistButtons(p)}`);
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

  // Права в игре — отдельным слушателем: это не кнопка, а переключатель, и при
  // отказе сервера он должен вернуться назад, а не показывать выданное право.
  document.addEventListener('change', async (e) => {
    const box = e.target.closest('.wl-server-admin');
    if (!box) return;
    const res = await fetch(`/api/admin/whitelist/${box.dataset.id}/server-admin`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ server_admin: box.checked })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      box.checked = !box.checked;
      showToast(data.message || 'Не получилось', 'error');
      return;
    }
    // Список перечитывается, чтобы галочка в таблице и в модалке не разошлись.
    loadWhitelist();
  });
}

bindAstvardHandlers();
