// 'none' means two different things now — no Steam bound yet, or bound and not
// asked about — so the unbound case gets its own line instead of sharing one.
const WHITELIST_TITLES = {
  none: 'Доступ не запрошен',
  pending: 'Заявка на рассмотрении',
  approved: 'Доступ открыт',
  rejected: 'Заявка отклонена',
};
const TITLE_UNBOUND = 'Steam не привязан';

function renderWhitelist(user) {
  const card = document.getElementById('whitelist-card');
  const status = user.steam_id ? user.whitelist_status : 'none';
  const title = user.steam_id ? (WHITELIST_TITLES[status] || status) : TITLE_UNBOUND;
  const lines = [`<p class="wl-status wl-${escapeHtml(status)}">${escapeHtml(title)}</p>`];

  if (!user.steam_id) {
    lines.push('<p class="muted">Вход на сервер — по списку, пароля нет. Войди через Steam: номер подтвердит сам Steam, и его не придётся вводить руками.</p>');
    lines.push('<a class="steam-button" href="/api/auth/steam">Привязать Steam</a>');
    card.innerHTML = lines.join('');
    return;
  }

  // Honest about where the number came from: an admin can type one in, and until
  // its owner signs in through Steam nobody has proved it is theirs.
  const mark = user.steam_id_verified
    ? '<span class="wl-verified">подтверждён Steam</span>'
    : '<span class="wl-unverified">вписан админом, войди через Steam чтобы подтвердить</span>';
  lines.push(`<p>Steam ID: <code>${escapeHtml(user.steam_id)}</code> ${mark}</p>`);

  if (user.server_admin) {
    lines.push('<p class="wl-approved">Админка на сервере выдана</p>');
  }

  if (status === 'rejected' && user.whitelist_note) {
    lines.push(`<p class="muted">Причина: ${escapeHtml(user.whitelist_note)}</p>`);
  }
  if (status === 'none' || status === 'rejected') {
    if (status === 'none') {
      lines.push('<p class="muted">Steam привязан. Осталось попросить доступ — админ увидит заявку.</p>');
    }
    lines.push(`
      <label>Пара слов о себе — необязательно, но с ними решают быстрее
        <textarea id="wl-note" rows="2" maxlength="500" placeholder="Кто пригласил, откуда знаешь ребят"></textarea>
      </label>
      <button id="wl-request">${status === 'rejected' ? 'Подать заново' : 'Запросить доступ'}</button>
    `);
  }
  if (status === 'pending') {
    lines.push('<p class="muted">Админ ещё не решил.</p>');
  }
  if (status === 'approved') {
    lines.push('<p class="muted">Заходи на сервер — адрес в списке ниже.</p>');
  }

  lines.push('<p class="error" id="whitelist-error"></p>');
  lines.push('<p class="muted">Сменил аккаунт Steam? <a href="/api/auth/steam">Привязать другой</a> — доступ придётся запросить заново.</p>');

  card.innerHTML = lines.join('');

  const requestBtn = document.getElementById('wl-request');
  if (requestBtn) {
    requestBtn.addEventListener('click', async () => {
      const error = document.getElementById('whitelist-error');
      error.textContent = '';
      try {
        const noteField = document.getElementById('wl-note');
        const { user: updated } = await apiFetch('/api/whitelist/request', {
          method: 'POST',
          body: JSON.stringify({ note: noteField ? noteField.value : '' }),
        });
        renderWhitelist(updated);
      } catch (err) {
        error.textContent = err.message;
      }
    });
  }
}

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

  // An account that came through Steam has no email, and an empty "Email:" line
  // reads as a page that failed to load something.
  document.getElementById('profile-card').innerHTML = `
    <h2>${escapeHtml(user.nickname)}</h2>
    ${user.email ? `<p>Email: ${escapeHtml(user.email)}</p>` : '<p class="muted">Вход через Steam</p>'}
    <p>Роль: ${escapeHtml(user.role)}</p>
  `;

  renderWhitelist(user);
  showSteamError('whitelist-error');

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
