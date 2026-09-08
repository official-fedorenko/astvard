const ROLES = ['player', 'admin', 'superadmin'];

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

(async function init() {
  const user = await getCurrentUser();
  if (!user || (user.role !== 'admin' && user.role !== 'superadmin')) {
    window.location.href = '/';
    return;
  }
  loadUsers();
  loadServers();
})();
