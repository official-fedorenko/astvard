const ROLES = ['player', 'admin', 'superadmin'];

const WHITELIST_LABELS = {
  // The table holds everyone who linked Steam, so 'none' is about access, not about
  // Steam: "не привязан" here would be about the one thing that is definitely true.
  none: 'нет доступа',
  pending: 'ждёт решения',
  approved: 'одобрен',
  rejected: 'отклонён',
};

// Set in init before the table is drawn: handing out in-game admin is a superadmin
// action, so a plain admin is not shown a checkbox that would answer 403.
let isSuperadmin = false;

// Two tables out of one list, because they are read at different moments: заявки
// are what someone has to answer today, доступ is who can get onto the server right
// now. Mixed in one table the pending rows kept sinking under the granted ones.
function renderRow(r, actions) {
  const when = r.whitelist_decided_at || r.whitelist_requested_at;
  const adminCell = isSuperadmin
    ? `<label class="admin-toggle"><input type="checkbox" class="wl-server-admin"
         data-id="${escapeHtml(r.id)}" ${r.server_admin ? 'checked' : ''}> в игре</label>`
    : (r.server_admin ? 'да' : '—');
  return `
    <tr>
      <td>${escapeHtml(r.nickname)}</td>
      <td>
        <code>${escapeHtml(r.steam_id || '—')}</code>
        ${r.steam_id && !r.steam_id_verified ? '<br><span class="wl-unverified">вписан руками</span>' : ''}
      </td>
      <td class="wl-${escapeHtml(r.whitelist_status)}">${escapeHtml(WHITELIST_LABELS[r.whitelist_status] || r.whitelist_status)}</td>
      <td class="muted">${r.whitelist_request_note ? escapeHtml(r.whitelist_request_note) : '—'}</td>
      <td>${when ? new Date(when).toLocaleString() : '—'}</td>
      <td>${adminCell}</td>
      <td class="row-actions">${actions}</td>
    </tr>`;
}

function fill(tbody, rows, emptyText) {
  tbody.innerHTML = rows.length
    ? rows.join('')
    : `<tr><td colspan="7" class="muted">${emptyText}</td></tr>`;
}

async function loadWhitelist() {
  const { people } = await apiFetch('/api/admin/whitelist');

  const pending = people.filter((r) => r.whitelist_status === 'pending');
  const rest = people.filter((r) => r.whitelist_status !== 'pending');

  fill(
    document.querySelector('#requests-table tbody'),
    pending.map((r) => renderRow(r, `
      <button data-id="${escapeHtml(r.id)}" class="btn btn-sm btn-primary wl-approve">Одобрить</button>
      <button data-id="${escapeHtml(r.id)}" class="btn btn-sm btn-ghost wl-reject">Отклонить</button>`)),
    'Новых заявок нет.'
  );

  fill(
    document.querySelector('#access-table tbody'),
    rest.map((r) => renderRow(r, r.whitelist_status === 'approved'
      ? `<button data-id="${escapeHtml(r.id)}" class="btn btn-sm btn-danger wl-revoke">Забрать доступ</button>`
      : `<button data-id="${escapeHtml(r.id)}" class="btn btn-sm btn-primary wl-approve">Выдать доступ</button>`)),
    'Никто ещё не привязал Steam.'
  );

  document.getElementById('access-count').textContent =
    `Доступ открыт: ${people.filter((r) => r.whitelist_status === 'approved').length}`
    + ` из ${people.length} привязавших Steam.`;

  document.querySelectorAll('.wl-server-admin').forEach((box) => {
    box.addEventListener('change', async () => {
      try {
        await apiFetch(`/api/admin/whitelist/${box.dataset.id}/server-admin`, {
          method: 'PATCH',
          body: JSON.stringify({ server_admin: box.checked }),
        });
      } catch (err) {
        // Put the box back where it was: it must not claim rights nobody granted.
        box.checked = !box.checked;
        document.getElementById('add-entry-error').textContent = err.message;
      }
    });
  });

  document.querySelectorAll('.wl-approve').forEach((btn) => {
    btn.addEventListener('click', () => decide(btn.dataset.id, 'approved', null));
  });
  document.querySelectorAll('.wl-reject').forEach((btn) => {
    btn.addEventListener('click', () => {
      // The player is shown this line, so it is worth a word. Cancel means cancel;
      // an empty note is fine.
      const note = window.prompt('Причина отказа (увидит игрок, можно оставить пустым):');
      if (note === null) return;
      decide(btn.dataset.id, 'rejected', note);
    });
  });
  // Taking access away kicks whoever is on the server as soon as the list is put
  // back, so it asks first.
  document.querySelectorAll('.wl-revoke').forEach((btn) => {
    btn.addEventListener('click', () => {
      const row = btn.closest('tr');
      const who = row ? row.firstElementChild.textContent.trim() : 'игрока';
      if (!window.confirm(`Забрать доступ у ${who}? Он пропадёт из permittedlist.txt, и админка в игре тоже снимется.`)) return;
      decide(btn.dataset.id, 'none', null);
    });
  });
}

async function decide(id, status, note) {
  await apiFetch(`/api/admin/whitelist/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ status, note }),
  });
  loadWhitelist();
}

// Not apiFetch: these answer text/plain on success and JSON on refusal, and the
// refusal is the interesting case — a permitted list with no entries is never
// written out, because the server reads an empty permittedlist.txt as "let
// everyone in". An empty adminlist.txt is fine and does come back empty.
async function loadServerList(path, emptyMessage) {
  const error = document.getElementById('permittedlist-error');
  const out = document.getElementById('permittedlist-out');
  const hint = document.getElementById('permittedlist-hint');
  error.textContent = '';
  out.hidden = true;
  hint.hidden = true;

  const res = await fetch(path, { credentials: 'same-origin' });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    error.textContent = data.error || `Ошибка ${res.status}`;
    return;
  }
  const text = await res.text();
  if (!text.trim()) {
    error.textContent = emptyMessage;
    return;
  }
  out.textContent = text;
  out.hidden = false;
  hint.hidden = false;
}


async function loadUsers() {
  const { users } = await apiFetch('/api/admin/users');
  const tbody = document.querySelector('#users-table tbody');

  const named = (role) => users.filter((u) => u.role === role).map((u) => u.nickname);
  const supers = named('superadmin');
  const admins = named('admin');
  document.getElementById('roles-summary').textContent =
    `Суперадмины: ${supers.length ? supers.join(', ') : 'нет'}. `
    + `Админы: ${admins.length ? admins.join(', ') : 'нет'}. `
    + `Всего аккаунтов: ${users.length}.`;

  const rank = (u) => ROLES.length - ROLES.indexOf(u.role);
  const sorted = [...users].sort((a, b) => rank(a) - rank(b) || a.nickname.localeCompare(b.nickname));

  tbody.innerHTML = sorted.map((u) => `
    <tr>
      <td>${escapeHtml(u.nickname)}</td>
      <td>${u.email ? escapeHtml(u.email) : '<span class="muted">через Steam</span>'}</td>
      <td>
        <select data-id="${escapeHtml(u.id)}" class="role-select">
          ${ROLES.map((r) => `<option value="${r}" ${r === u.role ? 'selected' : ''}>${r}</option>`).join('')}
        </select>
      </td>
      <td></td>
    </tr>
  `).join('');

  tbody.querySelectorAll('.role-select').forEach((sel) => {
    // What the select shows has to be what the server agreed to. A refusal — the
    // rank of superadmin from a plain admin, or your own role — used to leave the
    // new value sitting in the box as if it had been applied.
    const was = sel.value;
    sel.addEventListener('change', async () => {
      document.getElementById('users-error').textContent = '';
      try {
        await apiFetch(`/api/admin/users/${sel.dataset.id}/role`, {
          method: 'PATCH',
          body: JSON.stringify({ role: sel.value }),
        });
        loadUsers();
      } catch (err) {
        sel.value = was;
        document.getElementById('users-error').textContent = err.message;
      }
    });
  });
}

async function loadServers() {
  const { servers } = await apiFetch('/api/admin/servers');
  const tbody = document.querySelector('#servers-table tbody');
  tbody.innerHTML = servers.map((s) => `
    <tr>
      <td>${escapeHtml(s.name)}</td>
      <td>${escapeHtml(s.host)}:${escapeHtml(s.port)}</td>
      <td class="status ${s.is_online ? 'online' : 'offline'}">${s.is_online ? '● online' : '○ offline'}</td>
      <td>${s.is_online && s.players !== null ? `${s.players}/${s.max_players}` : '—'}</td>
      <td>${s.last_checked_at ? new Date(s.last_checked_at).toLocaleTimeString() : '—'}</td>
      <td><button data-id="${escapeHtml(s.id)}" class="btn btn-sm btn-danger delete-server">Удалить</button></td>
    </tr>
  `).join('');

  tbody.querySelectorAll('.delete-server').forEach((btn) => {
    btn.addEventListener('click', async () => {
      await apiFetch(`/api/admin/servers/${btn.dataset.id}`, { method: 'DELETE' });
      loadServers();
    });
  });
}

document.getElementById('add-server-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target;
  await apiFetch('/api/admin/servers', {
    method: 'POST',
    body: JSON.stringify({
      name: form.name.value,
      host: form.host.value,
      port: Number(form.port.value),
      probe: form.probe.value,
    }),
  });
  form.reset();
  loadServers();
});

document.getElementById('refresh-btn').addEventListener('click', async () => {
  await apiFetch('/api/admin/servers/refresh', { method: 'POST' });
  loadServers();
});

document.getElementById('permittedlist-btn').addEventListener('click', () =>
  loadServerList('/api/admin/whitelist/permittedlist', 'Список пуст.'));

document.getElementById('adminlist-btn').addEventListener('click', () =>
  loadServerList('/api/admin/whitelist/adminlist',
    'Ни одного админа. Пустой adminlist.txt снимет админку на сервере со всех — если это и нужно, положи пустой файл руками.'));

document.getElementById('add-entry-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target;
  const error = document.getElementById('add-entry-error');
  error.textContent = '';
  try {
    const { user } = await apiFetch('/api/admin/whitelist', {
      method: 'POST',
      body: JSON.stringify({ steam_id: form.steam_id.value }),
    });
    form.reset();
    // The nickname comes from Steam, so showing it is how a mistyped number gets
    // noticed — it turns up as somebody else's name.
    error.textContent = `Добавлен: ${user.nickname}. Сверь, тот ли это человек.`;
    loadWhitelist();
  } catch (err) {
    error.textContent = err.message;
  }
});

(async function init() {
  const user = await getCurrentUser();
  if (!user || (user.role !== 'admin' && user.role !== 'superadmin')) {
    window.location.href = '/';
    return;
  }
  renderNav(document.getElementById('nav'), user);
  isSuperadmin = user.role === 'superadmin';
  loadWhitelist();
  loadUsers();
  loadServers();
})();
