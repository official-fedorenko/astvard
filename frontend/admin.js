const status = document.getElementById('status');
const content = document.getElementById('content');

let games = [];
let isSuperadmin = false;

async function checkAccess() {
  // renderNav() уже дёргает /api/me для шапки — переиспользуем результат
  const data = await renderNav();
  if (!data) {
    window.location.href = 'login';
    return null;
  }
  if (data.role !== 'admin' && data.role !== 'superadmin') {
    status.textContent = 'Недостаточно прав для этой страницы.';
    return null;
  }
  status.textContent = `Ты вошёл как ${data.nickname} (${data.role})`;
  content.style.display = 'block';
  return data;
}

// ---------- Сервера ----------

const serverForm = document.getElementById('server-form');
const serverIdInput = document.getElementById('server-id');
const serverGameSelect = document.getElementById('server-gameId');
const serverNameInput = document.getElementById('server-name');
const serverHostInput = document.getElementById('server-host');
const serverPortInput = document.getElementById('server-port');
const serverDescInput = document.getElementById('server-description');
const serverSubmitBtn = document.getElementById('server-submit');
const serverCancelBtn = document.getElementById('server-cancel');
const serverRefreshBtn = document.getElementById('server-refresh');
const serverMessage = document.getElementById('server-message');
const serversList = document.getElementById('servers-list');

async function loadGames() {
  const response = await fetch('/api/games');
  games = await response.json();
  serverGameSelect.innerHTML = games
    .map((g) => `<option value="${g.id}">${escapeHtml(g.name)}</option>`)
    .join('');
}

async function loadServersAdmin() {
  const response = await fetch('/api/servers');
  const servers = await response.json();

  serversList.innerHTML = servers
    .map((s) => {
      const displayName = s.reported_name || s.name;
      const passwordToggle = isSuperadmin
        ? `<button class="btn-secondary" data-toggle-infra="${s.id}">🔑 Пароль сервера</button>
           <div data-infra-panel="${s.id}" style="display:none; margin-top:var(--spacing-sm);"></div>`
        : '';
      return `
        <div class="card">
          <div class="card-header">
            <span class="card-title">${escapeHtml(displayName)}</span>
            ${statusBadge(s.online)}
          </div>
          <div class="card-meta">${escapeHtml(s.game_name)}</div>
          <code>${escapeHtml(s.host)}:${escapeHtml(s.port)}</code><br>
          <span>${escapeHtml(s.description)}</span><br>
          <button class="btn-secondary" data-edit-server="${s.id}">Изменить</button>
          <button class="btn-danger" data-delete-server="${s.id}">Удалить</button>
          ${passwordToggle}
        </div>
      `;
    })
    .join('');

  serversList.querySelectorAll('[data-edit-server]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const server = servers.find((s) => s.id === Number(btn.dataset.editServer));
      startEditServer(server);
    });
  });

  serversList.querySelectorAll('[data-delete-server]').forEach((btn) => {
    btn.addEventListener('click', () => deleteServer(Number(btn.dataset.deleteServer)));
  });

  serversList.querySelectorAll('[data-toggle-infra]').forEach((btn) => {
    btn.addEventListener('click', () => toggleInfraPanel(Number(btn.dataset.toggleInfra)));
  });
}

async function toggleInfraPanel(serverId) {
  const panel = serversList.querySelector(`[data-infra-panel="${serverId}"]`);
  if (!panel) return;

  if (panel.style.display !== 'none') {
    panel.style.display = 'none';
    return;
  }

  panel.style.display = 'block';
  panel.innerHTML = 'Загрузка...';

  const response = await fetch(`/api/admin/servers/${serverId}/infra`);
  if (!response.ok) {
    panel.innerHTML = '<span style="color:var(--color-danger);">Не удалось загрузить</span>';
    return;
  }
  const infra = await response.json();

  panel.innerHTML = `
    <div class="row">
      <label style="flex:1 1 140px;">
        Имя контейнера
        <input type="text" data-infra-container="${serverId}" value="${escapeHtml(infra.dockerContainerName ?? '')}">
      </label>
      <label style="flex:1 1 140px;">
        Имя мира
        <input type="text" data-infra-world="${serverId}" value="${escapeHtml(infra.dockerWorldName ?? '')}">
      </label>
    </div>
    <button type="button" class="btn-secondary" data-save-infra="${serverId}">Сохранить настройки</button>

    <div class="row" style="margin-top:var(--spacing-md);">
      <label style="flex:1 1 200px;">
        Текущий пароль
        <input type="text" value="${escapeHtml(infra.connectPassword ?? '(не задан)')}" readonly>
      </label>
    </div>

    <div class="row" style="margin-top:var(--spacing-sm);">
      <label class="row" style="flex:none; gap:var(--spacing-xs);">
        <input type="checkbox" data-infra-public="${serverId}" ${infra.isPublic ? 'checked' : ''}>
        Публичный (виден в браузере серверов Steam)
      </label>
    </div>
    <p style="color:var(--color-text-muted); font-size:0.85rem;" data-infra-public-hint="${serverId}"></p>

    <div class="row" style="margin-top:var(--spacing-sm);">
      <label style="flex:1 1 200px;">
        <span data-infra-password-label="${serverId}"></span>
        <input type="text" data-infra-password="${serverId}">
      </label>
      <button type="button" class="btn-danger" data-change-password="${serverId}">Применить на сервере</button>
    </div>
    <p style="color:var(--color-text-muted); font-size:0.85rem;">
      Применение пересоздаёт контейнер — сервер на пару секунд уйдёт в оффлайн, текущие игроки отключатся.
    </p>
    <p data-infra-message="${serverId}"></p>
  `;

  const publicCheckbox = panel.querySelector(`[data-infra-public="${serverId}"]`);
  const publicHint = panel.querySelector(`[data-infra-public-hint="${serverId}"]`);
  const passwordLabel = panel.querySelector(`[data-infra-password-label="${serverId}"]`);

  function updatePublicHint() {
    if (publicCheckbox.checked) {
      publicHint.textContent = 'Публичному серверу Steam требует пароль (мин. 5 символов).';
      passwordLabel.textContent = 'Новый пароль (мин. 5 символов)';
    } else {
      publicHint.textContent = 'Приватный — не виден в поиске, подключаются по прямому адресу. Пароль можно оставить пустым.';
      passwordLabel.textContent = 'Новый пароль (необязательно)';
    }
  }
  updatePublicHint();
  publicCheckbox.addEventListener('change', updatePublicHint);

  panel.querySelector(`[data-save-infra="${serverId}"]`).addEventListener('click', async () => {
    const containerName = panel.querySelector(`[data-infra-container="${serverId}"]`).value;
    const worldName = panel.querySelector(`[data-infra-world="${serverId}"]`).value;
    const msg = panel.querySelector(`[data-infra-message="${serverId}"]`);
    const res = await fetch(`/api/admin/servers/${serverId}/infra`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dockerContainerName: containerName, dockerWorldName: worldName }),
    });
    const data = await res.json();
    msg.textContent = res.ok ? 'Настройки сохранены' : data.error;
    msg.style.color = res.ok ? 'var(--color-success)' : 'var(--color-danger)';
  });

  panel.querySelector(`[data-change-password="${serverId}"]`).addEventListener('click', async () => {
    const password = panel.querySelector(`[data-infra-password="${serverId}"]`).value;
    const isPublic = publicCheckbox.checked;
    const msg = panel.querySelector(`[data-infra-message="${serverId}"]`);
    if (isPublic && password.length < 5) {
      msg.textContent = 'Для публичного сервера нужен пароль от 5 символов';
      msg.style.color = 'var(--color-danger)';
      return;
    }
    if (!confirm('Сервер перезапустится, текущие игроки отключатся. Продолжить?')) return;

    msg.textContent = 'Пересобираю контейнер...';
    msg.style.color = 'var(--color-text-muted)';
    const res = await fetch(`/api/admin/servers/${serverId}/password`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password, isPublic }),
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = 'Готово, применено';
      msg.style.color = 'var(--color-success)';
      toggleInfraPanel(serverId);
      toggleInfraPanel(serverId); // перезагрузить панель со свежими данными
    } else {
      msg.textContent = data.error;
      msg.style.color = 'var(--color-danger)';
    }
  });
}

function startEditServer(server) {
  serverIdInput.value = server.id;
  serverGameSelect.value = String(games.find((g) => g.slug === server.game_slug)?.id ?? '');
  serverNameInput.value = server.name ?? '';
  serverHostInput.value = server.host;
  serverPortInput.value = server.port;
  serverDescInput.value = server.description ?? '';
  serverSubmitBtn.textContent = 'Сохранить';
  serverCancelBtn.style.display = 'inline-block';
}

function resetServerForm() {
  serverForm.reset();
  serverIdInput.value = '';
  serverSubmitBtn.textContent = 'Добавить сервер';
  serverCancelBtn.style.display = 'none';
}

serverCancelBtn.addEventListener('click', resetServerForm);

serverForm.addEventListener('submit', async (event) => {
  event.preventDefault();

  const id = serverIdInput.value;
  const body = {
    gameId: Number(serverGameSelect.value),
    name: serverNameInput.value,
    host: serverHostInput.value,
    port: Number(serverPortInput.value),
    description: serverDescInput.value,
  };

  const response = await fetch(id ? `/api/admin/servers/${id}` : '/api/admin/servers', {
    method: id ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  const data = await response.json();

  if (!response.ok) {
    serverMessage.textContent = data.error;
    serverMessage.style.color = '#e66';
    return;
  }

  serverMessage.textContent = '';
  resetServerForm();
  loadServersAdmin();
});

async function deleteServer(id) {
  if (!confirm('Удалить сервер?')) return;
  await fetch(`/api/admin/servers/${id}`, { method: 'DELETE' });
  loadServersAdmin();
}

serverRefreshBtn.addEventListener('click', async () => {
  serverRefreshBtn.disabled = true;
  serverRefreshBtn.textContent = 'Проверяю...';
  try {
    await fetch('/api/admin/servers/refresh', { method: 'POST' });
    await loadServersAdmin();
  } finally {
    serverRefreshBtn.disabled = false;
    serverRefreshBtn.textContent = 'Обновить статусы';
  }
});

// ---------- Статьи ----------

const articleForm = document.getElementById('article-form');
const articleIdInput = document.getElementById('article-id');
const articleTitleInput = document.getElementById('article-title');
const articleSlugInput = document.getElementById('article-slug');
const articleContentInput = document.getElementById('article-content');
const articleSubmitBtn = document.getElementById('article-submit');
const articleCancelBtn = document.getElementById('article-cancel');
const articleMessage = document.getElementById('article-message');
const articlesList = document.getElementById('articles-list');

async function loadArticlesAdmin() {
  const response = await fetch('/api/articles');
  const articles = await response.json();

  articlesList.innerHTML = articles
    .map(
      (a) => `
        <div class="card">
          <strong>${escapeHtml(a.title)}</strong> (${escapeHtml(a.slug)})
          <p>${escapeHtml(a.content)}</p>
          <button class="btn-secondary" data-edit-article="${a.id}">Изменить</button>
          <button class="btn-danger" data-delete-article="${a.id}">Удалить</button>
        </div>
      `
    )
    .join('');

  articlesList.querySelectorAll('[data-edit-article]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const article = articles.find((a) => a.id === Number(btn.dataset.editArticle));
      startEditArticle(article);
    });
  });

  articlesList.querySelectorAll('[data-delete-article]').forEach((btn) => {
    btn.addEventListener('click', () => deleteArticle(Number(btn.dataset.deleteArticle)));
  });
}

function startEditArticle(article) {
  articleIdInput.value = article.id;
  articleTitleInput.value = article.title;
  articleSlugInput.value = article.slug;
  articleContentInput.value = article.content;
  articleSubmitBtn.textContent = 'Сохранить';
  articleCancelBtn.style.display = 'inline-block';
}

function resetArticleForm() {
  articleForm.reset();
  articleIdInput.value = '';
  articleSubmitBtn.textContent = 'Добавить статью';
  articleCancelBtn.style.display = 'none';
}

articleCancelBtn.addEventListener('click', resetArticleForm);

articleForm.addEventListener('submit', async (event) => {
  event.preventDefault();

  const id = articleIdInput.value;
  const body = {
    title: articleTitleInput.value,
    slug: articleSlugInput.value,
    content: articleContentInput.value,
  };

  const response = await fetch(id ? `/api/admin/articles/${id}` : '/api/admin/articles', {
    method: id ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  const data = await response.json();

  if (!response.ok) {
    articleMessage.textContent = data.error;
    articleMessage.style.color = '#e66';
    return;
  }

  articleMessage.textContent = '';
  resetArticleForm();
  loadArticlesAdmin();
});

async function deleteArticle(id) {
  if (!confirm('Удалить статью?')) return;
  await fetch(`/api/admin/articles/${id}`, { method: 'DELETE' });
  loadArticlesAdmin();
}

// ---------- Пользователи (только superadmin) ----------

const usersSection = document.getElementById('users-section');
const usersList = document.getElementById('users-list');
const roleLabels = { player: 'игрок', admin: 'админ', superadmin: 'супер-админ' };
const authMethodLabels = { email: 'Email/пароль', steam: 'Steam' };

async function loadUsers() {
  const response = await fetch('/api/admin/users');
  if (!response.ok) return;
  const users = await response.json();

  usersList.innerHTML = users
    .map(
      (u) => `
        <div class="card">
          <strong>${escapeHtml(u.nickname)}</strong> — ${authMethodLabels[u.authMethod] ?? u.authMethod}<br>
          С ${new Date(u.createdAt).toLocaleDateString('ru-RU')}<br>
          <select data-role-for="${u.id}">
            ${['player', 'admin', 'superadmin']
              .map((r) => `<option value="${r}" ${r === u.role ? 'selected' : ''}>${roleLabels[r]}</option>`)
              .join('')}
          </select>
          <button class="btn-secondary" data-save-role="${u.id}">Сохранить</button>
        </div>
      `
    )
    .join('');

  usersList.querySelectorAll('[data-save-role]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.saveRole;
      const select = usersList.querySelector(`[data-role-for="${id}"]`);
      btn.disabled = true;
      await fetch(`/api/admin/users/${id}/role`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ role: select.value }),
      });
      btn.disabled = false;
      btn.textContent = 'Сохранено';
      setTimeout(() => (btn.textContent = 'Сохранить'), 1500);
    });
  });
}

// ---------- Игровые админы (только superadmin) ----------

const gameAdminsSection = document.getElementById('game-admins-section');
const gameAdminsServerSelect = document.getElementById('game-admins-server');
const gameAdminsPathWarning = document.getElementById('game-admins-path-warning');
const gameAdminsPathInput = document.getElementById('game-admins-path-input');
const gameAdminsPathSaveBtn = document.getElementById('game-admins-path-save');
const gameAdminsUserSelect = document.getElementById('game-admins-user');
const gameAdminsAddBtn = document.getElementById('game-admins-add');
const gameAdminsList = document.getElementById('game-admins-list');

let steamUsers = [];

async function populateGameAdminsPickers() {
  const serversResponse = await fetch('/api/servers');
  const allServers = await serversResponse.json();
  gameAdminsServerSelect.innerHTML = allServers
    .map((s) => `<option value="${s.id}">${escapeHtml(s.reported_name || s.name)} (${escapeHtml(s.game_name)})</option>`)
    .join('');

  const usersResponse = await fetch('/api/admin/users');
  const allUsers = await usersResponse.json();
  steamUsers = allUsers.filter((u) => u.authMethod === 'steam');
  gameAdminsUserSelect.innerHTML = steamUsers
    .map((u) => `<option value="${u.id}">${escapeHtml(u.nickname)}</option>`)
    .join('');
}

async function loadGameAdmins() {
  const serverId = gameAdminsServerSelect.value;
  if (!serverId) return;

  const response = await fetch(`/api/admin/servers/${serverId}/admins`);
  if (!response.ok) return;
  const data = await response.json();

  if (data.dockerVolumePath) {
    gameAdminsPathWarning.style.display = 'none';
    gameAdminsPathInput.value = data.dockerVolumePath;
  } else {
    gameAdminsPathWarning.style.display = 'block';
    gameAdminsPathWarning.innerHTML = `<p style="color:var(--color-danger);">Для этого сервера ещё не указан путь к тому — назначение админов не применится к реальному серверу, пока не сохранишь путь ниже.</p>`;
    gameAdminsPathInput.value = '';
  }

  gameAdminsList.innerHTML = data.admins
    .map(
      (a) => `
        <li class="card row" style="max-width:420px;">
          <span style="flex:1;">${escapeHtml(a.nickname)}</span>
          <button class="btn-danger" data-remove-game-admin="${a.userId}">Убрать</button>
        </li>
      `
    )
    .join('') || '<li style="color:var(--color-text-muted);">Пока никого нет</li>';

  gameAdminsList.querySelectorAll('[data-remove-game-admin]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      await fetch(`/api/admin/servers/${serverId}/admins/${btn.dataset.removeGameAdmin}`, { method: 'DELETE' });
      loadGameAdmins();
    });
  });
}

gameAdminsServerSelect.addEventListener('change', loadGameAdmins);

gameAdminsPathSaveBtn.addEventListener('click', async () => {
  const serverId = gameAdminsServerSelect.value;
  gameAdminsPathSaveBtn.disabled = true;
  const response = await fetch(`/api/admin/servers/${serverId}/docker-path`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ dockerVolumePath: gameAdminsPathInput.value }),
  });
  gameAdminsPathSaveBtn.disabled = false;
  if (response.ok) loadGameAdmins();
});

gameAdminsAddBtn.addEventListener('click', async () => {
  const serverId = gameAdminsServerSelect.value;
  const userId = Number(gameAdminsUserSelect.value);
  if (!serverId || !userId) return;

  gameAdminsAddBtn.disabled = true;
  await fetch(`/api/admin/servers/${serverId}/admins`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId }),
  });
  gameAdminsAddBtn.disabled = false;
  loadGameAdmins();
});

// ---------- Инициализация ----------

(async () => {
  const me = await checkAccess();
  if (!me) return;
  isSuperadmin = me.role === 'superadmin';

  await loadGames();
  await loadServersAdmin();
  await loadArticlesAdmin();

  if (me.role === 'superadmin') {
    usersSection.style.display = 'block';
    await loadUsers();

    gameAdminsSection.style.display = 'block';
    await populateGameAdminsPickers();
    await loadGameAdmins();
  }
})();
