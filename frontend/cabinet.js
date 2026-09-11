// Status line shown to the player, plus the wording of the button that submits a
// new id. The four statuses come from the users table.
const WHITELIST_VIEW = {
  none: { title: 'Steam не привязан', submit: 'Отправить заявку' },
  pending: { title: 'Заявка на рассмотрении', submit: 'Исправить номер' },
  approved: { title: 'Доступ открыт', submit: 'Привязать другой Steam' },
  rejected: { title: 'Заявка отклонена', submit: 'Подать заново' },
};

function renderWhitelist(user) {
  const view = WHITELIST_VIEW[user.whitelist_status] || WHITELIST_VIEW.none;
  const card = document.getElementById('whitelist-card');

  const lines = [`<p class="wl-status wl-${escapeHtml(user.whitelist_status)}">${escapeHtml(view.title)}</p>`];

  if (user.steam_id) {
    lines.push(`<p>Steam ID: <code>${escapeHtml(user.steam_id)}</code></p>`);
  }
  if (user.whitelist_status === 'none') {
    lines.push('<p class="muted">Вход на сервер — по списку, пароля нет. Привяжи Steam, и админ откроет доступ.</p>');
  }
  if (user.whitelist_status === 'pending') {
    lines.push('<p class="muted">Админ ещё не решил. Если ошибся в номере — пришли правильный.</p>');
  }
  if (user.whitelist_status === 'approved') {
    lines.push('<p class="muted">Заходи на сервер — адрес в списке ниже. Новый номер придётся одобрять заново, так что меняй только если правда сменил аккаунт.</p>');
  }
  if (user.whitelist_status === 'rejected' && user.whitelist_note) {
    lines.push(`<p class="muted">Причина: ${escapeHtml(user.whitelist_note)}</p>`);
  }

  lines.push(`
    <form id="whitelist-form">
      <label>SteamID64 или ссылка на профиль
        <input type="text" name="steam_id" placeholder="76561198000000000" required>
      </label>
      <button type="submit">${escapeHtml(view.submit)}</button>
      <p class="error" id="whitelist-error"></p>
    </form>
    <p class="muted">Где взять номер: Steam → свой профиль → «Изменить профиль». Номер стоит в адресе страницы.</p>
  `);

  card.innerHTML = lines.join('');

  document.getElementById('whitelist-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const error = document.getElementById('whitelist-error');
    error.textContent = '';
    try {
      const { user: updated } = await apiFetch('/api/whitelist/request', {
        method: 'POST',
        body: JSON.stringify({ steam_id: e.target.steam_id.value }),
      });
      renderWhitelist(updated);
    } catch (err) {
      error.textContent = err.message;
    }
  });
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

  document.getElementById('profile-card').innerHTML = `
    <h2>${escapeHtml(user.nickname)}</h2>
    <p>Email: ${escapeHtml(user.email)}</p>
    <p>Роль: ${escapeHtml(user.role)}</p>
  `;

  renderWhitelist(user);

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
