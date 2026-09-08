(async function init() {
  const user = await getCurrentUser();
  if (!user) {
    window.location.href = '/login.html';
    return;
  }

  const nav = document.getElementById('nav');
  const links = [];
  if (user.role === 'admin' || user.role === 'superadmin') {
    links.push('<a href="/admin.html">Админка</a>');
  }
  links.push('<a href="#" id="logout">Выйти</a>');
  nav.innerHTML = links.join(' · ');
  document.getElementById('logout').addEventListener('click', async (e) => {
    e.preventDefault();
    await apiFetch('/api/logout', { method: 'POST' });
    window.location.href = '/';
  });

  document.getElementById('profile-card').innerHTML = `
    <h2>${escapeHtml(user.nickname)}</h2>
    <p>Email: ${escapeHtml(user.email)}</p>
    <p>Роль: ${escapeHtml(user.role)}</p>
  `;

  const { servers } = await apiFetch('/api/servers');
  const list = document.getElementById('servers-list');
  if (servers.length === 0) {
    list.textContent = 'Пока нет серверов.';
    return;
  }
  list.innerHTML = servers.map((s) => `
    <div class="card server-row">
      <strong>${escapeHtml(s.name)}</strong> — ${escapeHtml(s.host)}:${escapeHtml(s.port)}
      <span class="status ${s.is_online ? 'online' : 'offline'}">
        ${s.is_online ? `● online (${s.players}/${s.max_players})` : '○ offline'}
      </span>
    </div>
  `).join('');
})();
