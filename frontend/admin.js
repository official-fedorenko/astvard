const ROLES = ['player', 'admin', 'superadmin'];

const WHITELIST_LABELS = {
  none: 'не привязан',
  pending: 'ждёт решения',
  approved: 'одобрен',
  rejected: 'отклонён',
};

async function loadWhitelist() {
  const { requests } = await apiFetch('/api/admin/whitelist');
  const tbody = document.querySelector('#whitelist-table tbody');
  if (requests.length === 0) {
    tbody.innerHTML = '<tr><td colspan="6" class="muted">Заявок пока нет.</td></tr>';
    return;
  }
  tbody.innerHTML = requests.map((r) => `
    <tr>
      <td>${escapeHtml(r.nickname)}</td>
      <td>${escapeHtml(r.email)}</td>
      <td><code>${escapeHtml(r.steam_id || '—')}</code></td>
      <td class="wl-${escapeHtml(r.whitelist_status)}">${escapeHtml(WHITELIST_LABELS[r.whitelist_status] || r.whitelist_status)}</td>
      <td>${r.whitelist_requested_at ? new Date(r.whitelist_requested_at).toLocaleString() : '—'}</td>
      <td>
        <button data-id="${escapeHtml(r.id)}" class="wl-approve">Одобрить</button>
        <button data-id="${escapeHtml(r.id)}" class="wl-reject">Отклонить</button>
      </td>
    </tr>
  `).join('');

  tbody.querySelectorAll('.wl-approve').forEach((btn) => {
    btn.addEventListener('click', () => decide(btn.dataset.id, 'approved', null));
  });
  tbody.querySelectorAll('.wl-reject').forEach((btn) => {
    btn.addEventListener('click', () => {
      // The player is shown this line, so it is worth a word. Cancel means cancel;
      // an empty note is fine.
      const note = window.prompt('Причина отказа (увидит игрок, можно оставить пустым):');
      if (note === null) return;
      decide(btn.dataset.id, 'rejected', note);
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

// Not apiFetch: this answer is text/plain on success and JSON on refusal, and the
// refusal is the interesting case — a list with no entries is never written out,
// because the server reads an empty permittedlist.txt as "let everyone in".
async function loadPermittedList() {
  const error = document.getElementById('permittedlist-error');
  const out = document.getElementById('permittedlist-out');
  const hint = document.getElementById('permittedlist-hint');
  error.textContent = '';
  out.hidden = true;
  hint.hidden = true;

  const res = await fetch('/api/admin/whitelist/permittedlist', { credentials: 'same-origin' });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    error.textContent = data.error || `Ошибка ${res.status}`;
    return;
  }
  out.textContent = await res.text();
  out.hidden = false;
  hint.hidden = false;
}


async function loadUsers() {
  const { users } = await apiFetch('/api/admin/users');
  const tbody = document.querySelector('#users-table tbody');
  tbody.innerHTML = users.map((u) => `
    <tr>
      <td>${escapeHtml(u.nickname)}</td>
      <td>${escapeHtml(u.email)}</td>
      <td>
        <select data-id="${escapeHtml(u.id)}" class="role-select">
          ${ROLES.map((r) => `<option value="${r}" ${r === u.role ? 'selected' : ''}>${r}</option>`).join('')}
        </select>
      </td>
      <td></td>
    </tr>
  `).join('');

  tbody.querySelectorAll('.role-select').forEach((sel) => {
    sel.addEventListener('change', async () => {
      await apiFetch(`/api/admin/users/${sel.dataset.id}/role`, {
        method: 'PATCH',
        body: JSON.stringify({ role: sel.value }),
      });
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
      <td><button data-id="${escapeHtml(s.id)}" class="delete-server">Удалить</button></td>
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
    }),
  });
  form.reset();
  loadServers();
});

document.getElementById('refresh-btn').addEventListener('click', async () => {
  await apiFetch('/api/admin/servers/refresh', { method: 'POST' });
  loadServers();
});

document.getElementById('permittedlist-btn').addEventListener('click', loadPermittedList);

(async function init() {
  const user = await getCurrentUser();
  if (!user || (user.role !== 'admin' && user.role !== 'superadmin')) {
    window.location.href = '/';
    return;
  }
  loadWhitelist();
  loadUsers();
  loadServers();
})();
