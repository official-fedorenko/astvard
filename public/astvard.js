// === Astvard: вайтлист игрового сервера и состояние серверов ===
//
// Отдельный файл, а не строки в app.js: игровая часть пришла из портала, живёт
// своими маршрутами и правится отдельно от учётной части панели.

const WHITELIST_LABELS = {
  none: 'нет доступа',
  pending: 'ждёт решения',
  approved: 'одобрен',
  rejected: 'отклонён'
};

let whitelistPeople = [];
let astvardServers = [];
let astvardHandlersBound = false;

function astvardDate(value) {
  if (!value) return '—';
  const d = new Date(value.replace(' ', 'T') + (value.includes('Z') ? '' : 'Z'));
  return Number.isNaN(d.getTime()) ? value : d.toLocaleString();
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

function whitelistRow(p, actions) {
  const admin = currentUser && currentUser.role === 'Superadmin'
    ? `<label class="switch-label"><input type="checkbox" class="wl-server-admin" data-id="${p.id}" ${p.server_admin ? 'checked' : ''}> в игре</label>`
    : (p.server_admin ? 'да' : '—');
  const verified = p.steam_id_verified
    ? ''
    : '<br><small style="color: hsl(var(--warning, 38 92% 50%));">вписан руками</small>';
  return `
    <tr>
      <td>${escapeHtml(p.username)}</td>
      <td><code>${escapeHtml(p.steam_id || '—')}</code>${verified}</td>
      ${actions.middle}
      <td>${astvardDate(p.whitelist_decided_at || p.whitelist_requested_at)}</td>
      <td>${admin}</td>
      <td style="display:flex; gap:6px; flex-wrap:wrap;">${actions.buttons}</td>
    </tr>`;
}

function renderWhitelist() {
  const pending = whitelistPeople.filter(p => p.whitelist_status === 'pending');
  const rest = whitelistPeople.filter(p => p.whitelist_status !== 'pending');
  const approved = whitelistPeople.filter(p => p.whitelist_status === 'approved').length;

  const requestsBody = document.getElementById('whitelistRequestsBody');
  const accessBody = document.getElementById('whitelistAccessBody');
  if (!requestsBody || !accessBody) return;

  requestsBody.innerHTML = pending.length
    ? pending.map(p => whitelistRow(p, {
      middle: `<td>${p.whitelist_request_note ? escapeHtml(p.whitelist_request_note) : '—'}</td>`,
      buttons: `
        <button class="btn btn-sm btn-primary wl-approve" data-id="${p.id}">Одобрить</button>
        <button class="btn btn-sm btn-secondary wl-reject" data-id="${p.id}">Отклонить</button>`
    })).join('')
    : '<tr class="empty-row"><td colspan="6" style="text-align:center; padding:24px; color: hsl(var(--text-muted));">Новых заявок нет</td></tr>';

  accessBody.innerHTML = rest.length
    ? rest.map(p => whitelistRow(p, {
      middle: `<td>${escapeHtml(WHITELIST_LABELS[p.whitelist_status] || p.whitelist_status)}</td>`,
      buttons: p.whitelist_status === 'approved'
        ? `<button class="btn btn-sm btn-danger wl-revoke" data-id="${p.id}">Забрать доступ</button>`
        : `<button class="btn btn-sm btn-primary wl-approve" data-id="${p.id}">Дать доступ</button>`
    })).join('')
    : '<tr class="empty-row"><td colspan="6" style="text-align:center; padding:24px; color: hsl(var(--text-muted));">Никто ещё не привязал Steam</td></tr>';

  const counter = document.getElementById('whitelistCount');
  if (counter) counter.textContent = `— открыт ${approved} из ${whitelistPeople.length}`;
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

function renderServers() {
  const tbody = document.getElementById('serversTableBody');
  if (!tbody) return;
  tbody.innerHTML = astvardServers.length
    ? astvardServers.map(s => `
      <tr>
        <td>${escapeHtml(s.name)}</td>
        <td><code>${escapeHtml(s.host)}:${escapeHtml(String(s.port))}</code></td>
        <td>${s.is_online
          ? '<span style="color:#4ade80;">● online</span>'
          : '<span style="color: hsl(var(--text-muted));">○ offline</span>'}</td>
        <td>${s.is_online && s.players !== null ? `${s.players}/${s.max_players}` : '—'}</td>
        <td>${astvardDate(s.last_checked_at)}</td>
        <td><button class="btn btn-sm btn-danger server-delete" data-id="${s.id}">Удалить</button></td>
      </tr>`).join('')
    : '<tr class="empty-row"><td colspan="6" style="text-align:center; padding:24px; color: hsl(var(--text-muted));">Серверов пока нет</td></tr>';
}

// Обработчики вешаются один раз и на весь документ: таблицы перерисовываются
// целиком после каждого действия, и слушатели на самих кнопках умирали бы вместе
// со строками.
function bindAstvardHandlers() {
  if (astvardHandlersBound) return;
  astvardHandlersBound = true;

  document.addEventListener('click', async (e) => {
    const approve = e.target.closest('.wl-approve');
    if (approve) return whitelistDecide(approve.dataset.id, 'approved', null);

    const reject = e.target.closest('.wl-reject');
    if (reject) {
      // Эту строку увидит игрок, поэтому её стоит написать. Отмена — это отмена,
      // пустая причина допустима.
      const note = window.prompt('Причина отказа (увидит игрок, можно оставить пустым):');
      if (note === null) return;
      return whitelistDecide(reject.dataset.id, 'rejected', note);
    }

    const revoke = e.target.closest('.wl-revoke');
    if (revoke) {
      const row = revoke.closest('tr');
      const who = row ? row.firstElementChild.textContent.trim() : 'игрока';
      if (!window.confirm(`Забрать доступ у ${who}? Он пропадёт из permittedlist.txt, и админка в игре тоже снимется.`)) return;
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
      if (!window.confirm('Удалить сервер из списка?')) return;
      await fetch(`/api/admin/servers/${del.dataset.id}`, { method: 'DELETE' });
      return loadServersSection();
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
    }
  });
}

bindAstvardHandlers();
