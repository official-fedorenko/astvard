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
  content.style.display = ''; // снимаем инлайновый display:none — дальше рулит CSS (.admin-layout: flex)
  return data;
}

// ---------- Меню разделов (слева) ----------
// Одновременно виден один раздел; выбранный запоминаем в localStorage,
// чтобы после перезагрузки открывался тот же

const ADMIN_SECTION_KEY = 'astvard.admin.section';

function showAdminSection(sectionId) {
  document.querySelectorAll('.admin-section').forEach((s) => {
    s.classList.toggle('active', s.id === sectionId);
  });
  document.querySelectorAll('.admin-menu button').forEach((b) => {
    b.classList.toggle('active', b.dataset.section === sectionId);
  });
  // при переходе в "Сервера" из другого раздела меню всегда открываем список,
  // а не ту подвкладку, что случайно осталась активной с прошлого раза
  if (sectionId === 'servers-admin') {
    showAdminSubtab(serversAdminSection, 'server-list-tab');
  }
  try {
    localStorage.setItem(ADMIN_SECTION_KEY, sectionId);
  } catch {
    // приватный режим / отключённое хранилище — просто не запоминаем
  }
}

function initAdminMenu() {
  const buttons = [...document.querySelectorAll('.admin-menu button')];
  buttons.forEach((b) => b.addEventListener('click', () => showAdminSection(b.dataset.section)));

  // superadmin-разделы показываем только superadmin'у
  if (isSuperadmin) {
    buttons.filter((b) => b.hasAttribute('data-superadmin')).forEach((b) => (b.style.display = ''));
  }

  let saved = null;
  try {
    saved = localStorage.getItem(ADMIN_SECTION_KEY);
  } catch {}
  const visible = buttons.filter((b) => b.style.display !== 'none').map((b) => b.dataset.section);
  showAdminSection(visible.includes(saved) ? saved : visible[0]);
}

// ---------- Подвкладки внутри раздела (например "Существующие" / "Добавить") ----------
// root — контейнер, где рядом лежат .admin-subtabs (кнопки) и .admin-subtab (панели)

function showAdminSubtab(root, subtabId) {
  root.querySelectorAll('.admin-subtab').forEach((el) => el.classList.toggle('active', el.id === subtabId));
  root.querySelectorAll('.admin-subtabs button').forEach((b) => b.classList.toggle('active', b.dataset.subtab === subtabId));
}

function initAdminSubtabs(root) {
  root.querySelectorAll('.admin-subtabs button').forEach((b) => {
    b.addEventListener('click', () => showAdminSubtab(root, b.dataset.subtab));
  });
}

// ---------- Сервера ----------

const serversAdminSection = document.getElementById('servers-admin');
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
      // "Изменить информацию" — карточка каталога (это правят все admin);
      // "Изменить сервер" — реальная инфраструктура на VPS (только superadmin)
      const serverToggle = isSuperadmin
        ? `<button class="btn-secondary" data-toggle-infra="${s.id}">🖥️ Изменить сервер</button>
           <div data-infra-panel="${s.id}" style="display:none;"></div>`
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
          <button class="btn-secondary" data-edit-server="${s.id}">Изменить информацию</button>
          <button class="btn-danger" data-delete-server="${s.id}">Удалить</button>
          ${serverToggle}
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
  panel.innerHTML = '<div class="settings-panel">Загрузка...</div>';

  const response = await fetch(`/api/admin/servers/${serverId}/infra`);
  if (!response.ok) {
    panel.innerHTML = '<div class="settings-panel"><span style="color:var(--color-danger);">Не удалось загрузить</span></div>';
    return;
  }
  const infra = await response.json();

  // управление реальным контейнером сделано только для Valheim — не даём заполнить
  // поля впустую и наткнуться на ошибку при сохранении, а сразу объясняем, почему нельзя
  if (infra.gameSlug !== 'valheim') {
    panel.innerHTML = `
      <div class="settings-panel">
        <p class="settings-panel-disabled">
          Управление контейнером (пароль, публичность, сид мира) пока поддержано только для Valheim.
          Для этой игры доступно только редактирование карточки — кнопка "Изменить информацию".
        </p>
      </div>
    `;
    return;
  }

  // применить пароль/видимость можно только когда сервер привязан к реальному контейнеру —
  // без этого кнопка ниже неактивна, а не просто падает с ошибкой после нажатия
  const ready = Boolean(infra.dockerContainerName && infra.dockerWorldName && infra.dockerVolumePath);

  panel.innerHTML = `
    <div class="settings-panel">
      <div class="row">
        <label style="flex:1 1 160px;">
          Имя контейнера
          <input type="text" data-infra-container="${serverId}" value="${escapeHtml(infra.dockerContainerName ?? '')}" placeholder="astvard-valheim-N">
        </label>
        <label style="flex:1 1 160px;">
          Имя мира
          <input type="text" data-infra-world="${serverId}" value="${escapeHtml(infra.dockerWorldName ?? '')}" placeholder="Randheim">
        </label>
      </div>
      <div class="row" style="margin-top:var(--spacing-sm);">
        <label style="flex:2 1 260px;">
          Путь к тому Docker (для adminlist.txt и пересборки контейнера)
          <input type="text" data-infra-path="${serverId}" value="${escapeHtml(infra.dockerVolumePath ?? '')}" placeholder="/var/lib/docker/volumes/.../_data">
        </label>
        <label style="flex:1 1 160px;">
          Сид мира (видно только админам)
          <input type="text" data-infra-seed="${serverId}" value="${escapeHtml(infra.worldSeed ?? '')}">
        </label>
      </div>
      <button type="button" class="btn-secondary" data-save-infra="${serverId}" style="margin-top:var(--spacing-sm);">Сохранить настройки</button>
      <p data-infra-message="${serverId}"></p>
    </div>

    <div class="settings-panel">
      <button type="button" class="btn-secondary" data-restart="${serverId}" ${infra.dockerContainerName ? '' : 'disabled'}>🔄 Перезапустить сервер</button>
      <p class="hint" style="margin-top:var(--spacing-xs);">
        ${infra.dockerContainerName
          ? 'Обычный docker restart — настройки не меняются (пароль, публичность, имя те же). Сервер на несколько секунд уйдёт в оффлайн, игроки отключатся.'
          : 'Сначала укажи и сохрани имя контейнера выше.'}
      </p>
      <p data-restart-message="${serverId}"></p>
    </div>

    <div class="settings-panel">
      <div class="row">
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
      <p class="hint" data-infra-public-hint="${serverId}"></p>

      <div class="row row-align-end" style="margin-top:var(--spacing-sm);">
        <label style="flex:1 1 200px;">
          <span data-infra-password-label="${serverId}"></span>
          <input type="text" data-infra-password="${serverId}" ${ready ? '' : 'disabled'}>
        </label>
        <button type="button" class="btn-danger" data-change-password="${serverId}" ${ready ? '' : 'disabled'}>Применить на сервере</button>
      </div>
      <p class="hint">
        ${ready
          ? 'Применение пересоздаёт контейнер — сервер на пару секунд уйдёт в оффлайн, текущие игроки отключатся.'
          : 'Сначала заполни и сохрани имя контейнера, мира и путь к тому выше — до этого применить нельзя.'}
      </p>
      <p data-infra-apply-message="${serverId}"></p>
    </div>
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
    const volumePath = panel.querySelector(`[data-infra-path="${serverId}"]`).value;
    const worldSeed = panel.querySelector(`[data-infra-seed="${serverId}"]`).value;
    const msg = panel.querySelector(`[data-infra-message="${serverId}"]`);
    const res = await fetch(`/api/admin/servers/${serverId}/infra`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        dockerContainerName: containerName,
        dockerWorldName: worldName,
        dockerVolumePath: volumePath,
        worldSeed,
      }),
    });
    const data = await res.json();
    if (res.ok) {
      toggleInfraPanel(serverId);
      toggleInfraPanel(serverId); // перезагрузить панель — пересчитать, разблокировалась ли кнопка "Применить"
    } else {
      msg.textContent = data.error;
      msg.style.color = 'var(--color-danger)';
    }
  });

  panel.querySelector(`[data-change-password="${serverId}"]`).addEventListener('click', async () => {
    const password = panel.querySelector(`[data-infra-password="${serverId}"]`).value;
    const isPublic = publicCheckbox.checked;
    const msg = panel.querySelector(`[data-infra-apply-message="${serverId}"]`);
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

  const restartBtn = panel.querySelector(`[data-restart="${serverId}"]`);
  if (restartBtn) {
    restartBtn.addEventListener('click', async () => {
      const msg = panel.querySelector(`[data-restart-message="${serverId}"]`);
      if (!confirm('Сервер перезапустится, текущие игроки отключатся. Продолжить?')) return;

      restartBtn.disabled = true;
      msg.textContent = 'Перезапускаю...';
      msg.style.color = 'var(--color-text-muted)';
      const res = await fetch(`/api/admin/servers/${serverId}/restart`, { method: 'POST' });
      restartBtn.disabled = false;

      const data = await res.json();
      if (res.ok) {
        msg.textContent = 'Готово, перезапущен';
        msg.style.color = 'var(--color-success)';
      } else {
        msg.textContent = data.error;
        msg.style.color = 'var(--color-danger)';
      }
    });
  }
}

function startEditServer(server) {
  showAdminSubtab(serversAdminSection, 'server-add-tab');
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

serverCancelBtn.addEventListener('click', () => {
  resetServerForm();
  showAdminSubtab(serversAdminSection, 'server-list-tab');
});

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
  showAdminSubtab(serversAdminSection, 'server-list-tab');
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

const usersSearch = document.getElementById('users-search');
let allUsers = [];

function renderUsers(users) {
  usersList.innerHTML = users.length
    ? users
        .map((u) => {
          const fullName = [u.firstName, u.lastName].filter(Boolean).join(' ');
          const avatar = u.avatarUrl
            ? `<img class="profile-avatar" src="${escapeHtml(u.avatarUrl)}" alt="">`
            : '';
          const metaParts = [fullName, authMethodLabels[u.authMethod] ?? u.authMethod, u.email].filter(Boolean);
          const joined = new Date(u.createdAt).toLocaleDateString('ru-RU');

          return `
            <div class="card">
              <div class="card-header">
                <span class="card-title">${avatar}${escapeHtml(u.nickname)}</span>
                <span class="badge badge-role-${u.role}">${roleLabels[u.role] ?? u.role}</span>
              </div>
              <div class="card-meta">${metaParts.map(escapeHtml).join(' · ')}</div>
              <div class="card-meta">На сайте с ${joined}</div>
              <div class="row" style="margin-top:var(--spacing-sm);">
                <select data-role-for="${u.id}">
                  ${['player', 'admin', 'superadmin']
                    .map((r) => `<option value="${r}" ${r === u.role ? 'selected' : ''}>${roleLabels[r]}</option>`)
                    .join('')}
                </select>
                <button class="btn-secondary" data-save-role="${u.id}">Сохранить</button>
              </div>
            </div>
          `;
        })
        .join('')
    : '<p class="hint">Никого не нашлось</p>';

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
      const cached = allUsers.find((u) => u.id === Number(id));
      if (cached) cached.role = select.value;
    });
  });
}

usersSearch.addEventListener('input', () => {
  const q = usersSearch.value.trim().toLowerCase();
  const filtered = q
    ? allUsers.filter(
        (u) => u.nickname.toLowerCase().includes(q) || (u.email && u.email.toLowerCase().includes(q))
      )
    : allUsers;
  renderUsers(filtered);
});

async function loadUsers() {
  const response = await fetch('/api/admin/users');
  if (!response.ok) return;
  allUsers = await response.json();
  renderUsers(allUsers);
}

// ---------- Игровые админы (только superadmin) ----------

const gameAdminsSection = document.getElementById('game-admins-section');
const gameAdminsServerSelect = document.getElementById('game-admins-server');
const gameAdminsPathWarning = document.getElementById('game-admins-path-warning');
const gameAdminsPathDisplay = document.getElementById('game-admins-path-display');
const gameAdminsUserSearch = document.getElementById('game-admins-user-search');
const gameAdminsUserResults = document.getElementById('game-admins-user-results');
const gameAdminsAddBtn = document.getElementById('game-admins-add');
const gameAdminsList = document.getElementById('game-admins-list');

let steamUsers = [];
let gameAdminsSelectedUserId = null;
const GAME_ADMINS_SEARCH_LIMIT = 8;

async function populateGameAdminsPickers() {
  const serversResponse = await fetch('/api/servers');
  const allServers = await serversResponse.json();
  gameAdminsServerSelect.innerHTML = allServers
    .map((s) => `<option value="${s.id}">${escapeHtml(s.reported_name || s.name)} (${escapeHtml(s.game_name)})</option>`)
    .join('');

  const usersResponse = await fetch('/api/admin/users');
  const allUsers = await usersResponse.json();
  steamUsers = allUsers.filter((u) => u.authMethod === 'steam');
}

// ---------- Поиск пользователя по нику в реальном времени ----------

function renderGameAdminsUserResults(query) {
  const matches = steamUsers
    .filter((u) => u.nickname.toLowerCase().includes(query.toLowerCase()))
    .slice(0, GAME_ADMINS_SEARCH_LIMIT);

  if (matches.length === 0) {
    gameAdminsUserResults.innerHTML = '<li class="autocomplete-item empty">Никого не нашлось</li>';
  } else {
    gameAdminsUserResults.innerHTML = matches
      .map((u) => `<li class="autocomplete-item" data-user-id="${u.id}">${escapeHtml(u.nickname)}</li>`)
      .join('');
  }
  gameAdminsUserResults.hidden = false;
}

gameAdminsUserSearch.addEventListener('input', () => {
  gameAdminsSelectedUserId = null; // поменял текст руками — выбор из списка больше не действует
  const query = gameAdminsUserSearch.value.trim();
  if (!query) {
    gameAdminsUserResults.hidden = true;
    return;
  }
  renderGameAdminsUserResults(query);
});

gameAdminsUserSearch.addEventListener('focus', () => {
  if (gameAdminsUserSearch.value.trim()) renderGameAdminsUserResults(gameAdminsUserSearch.value.trim());
});

gameAdminsUserResults.addEventListener('click', (event) => {
  const item = event.target.closest('[data-user-id]');
  if (!item) return;
  gameAdminsSelectedUserId = Number(item.dataset.userId);
  gameAdminsUserSearch.value = item.textContent;
  gameAdminsUserResults.hidden = true;
});

// закрываем список при клике вне поля поиска — иначе так и висит открытым
document.addEventListener('click', (event) => {
  if (!event.target.closest('#game-admins-user-autocomplete')) {
    gameAdminsUserResults.hidden = true;
  }
});

async function loadGameAdmins() {
  const serverId = gameAdminsServerSelect.value;
  if (!serverId) return;

  const response = await fetch(`/api/admin/servers/${serverId}/admins`);
  if (!response.ok) return;
  const data = await response.json();

  // путь к тому теперь настраивается в одном месте — на карточке сервера,
  // кнопка "Изменить сервер" (там же контейнер/мир/сид). Здесь только читаем.
  if (data.dockerVolumePath) {
    gameAdminsPathWarning.style.display = 'none';
    gameAdminsPathDisplay.textContent = `Том Docker: ${data.dockerVolumePath}`;
  } else {
    gameAdminsPathWarning.style.display = 'block';
    gameAdminsPathWarning.innerHTML = `<p style="color:var(--color-danger);">Для этого сервера ещё не указан путь к тому — назначение админов не применится к реальному серверу, пока не настроишь его в карточке сервера ("Сервера" → "Изменить сервер").</p>`;
    gameAdminsPathDisplay.textContent = 'Том Docker не настроен';
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

gameAdminsAddBtn.addEventListener('click', async () => {
  const serverId = gameAdminsServerSelect.value;
  if (!serverId || !gameAdminsSelectedUserId) return;

  gameAdminsAddBtn.disabled = true;
  await fetch(`/api/admin/servers/${serverId}/admins`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId: gameAdminsSelectedUserId }),
  });
  gameAdminsAddBtn.disabled = false;
  gameAdminsSelectedUserId = null;
  gameAdminsUserSearch.value = '';
  loadGameAdmins();
});

// ---------- Настройка проекта (только superadmin) ----------

const settingsForm = document.getElementById('settings-form');
const settingsMessage = document.getElementById('settings-message');
const SETTING_KEYS = ['site_name', 'site_tagline', 'footer_text'];

async function loadSettingsForm() {
  const settings = await loadSiteSettings();
  SETTING_KEYS.forEach((key) => {
    document.getElementById(`setting-${key}`).value = settings[key] ?? '';
  });
  document.getElementById('setting-logo-preview').src = settings.logo_url || '/logo.svg';
  document.getElementById('setting-favicon-preview').src = settings.favicon_url || '/logo.svg';
}

settingsForm.addEventListener('submit', async (event) => {
  event.preventDefault();

  const body = {};
  SETTING_KEYS.forEach((key) => {
    body[key] = document.getElementById(`setting-${key}`).value;
  });

  const submitBtn = document.getElementById('settings-submit');
  submitBtn.disabled = true;
  const response = await fetch('/api/admin/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  submitBtn.disabled = false;

  const data = await response.json();
  if (!response.ok) {
    settingsMessage.textContent = data.error;
    settingsMessage.style.color = 'var(--color-danger)';
    return;
  }
  settingsMessage.textContent = 'Сохранено';
  settingsMessage.style.color = 'var(--color-success)';
  // шапка/футер читают из кэшированного промиса — сбрасываем и перерисовываем
  siteSettingsPromise = Promise.resolve(data);
  renderNav();
});

// ---------- Загрузчик картинок (логотип, фавиконка — только superadmin) ----------
// Общая логика на оба блока: превью выбранного файла ещё до отправки (локально,
// через object URL), отправка на /api/admin/settings/<kind>, сброс на дефолт.

function initAssetUploader(kind) {
  const fileInput = document.getElementById(`setting-${kind}-file`);
  const uploadBtn = document.getElementById(`setting-${kind}-upload`);
  const resetBtn = document.getElementById(`setting-${kind}-reset`);
  const preview = document.getElementById(`setting-${kind}-preview`);
  const message = document.getElementById(`setting-${kind}-message`);
  const filenameEl = document.getElementById(`setting-${kind}-filename`);

  let objectUrl = null;

  fileInput.addEventListener('change', () => {
    const file = fileInput.files[0];
    filenameEl.textContent = file ? `Выбран: ${file.name}` : '';
    filenameEl.hidden = !file;

    if (objectUrl) URL.revokeObjectURL(objectUrl);
    if (file) {
      objectUrl = URL.createObjectURL(file);
      preview.src = objectUrl; // локальное превью — до реальной загрузки на сервер
    }
  });

  uploadBtn.addEventListener('click', async () => {
    const file = fileInput.files[0];
    if (!file) {
      message.textContent = 'Сначала выбери файл';
      message.style.color = 'var(--color-danger)';
      return;
    }

    const formData = new FormData();
    formData.append('file', file);

    uploadBtn.disabled = true;
    const response = await fetch(`/api/admin/settings/${kind}`, { method: 'POST', body: formData });
    uploadBtn.disabled = false;

    const data = await response.json();
    if (!response.ok) {
      message.textContent = data.error;
      message.style.color = 'var(--color-danger)';
      return;
    }
    message.textContent = 'Обновлено';
    message.style.color = 'var(--color-success)';
    preview.src = data.url;
    fileInput.value = '';
    filenameEl.hidden = true;
    siteSettingsPromise = null; // сбрасываем кэш — шапка/футер/фавиконка подтянут новое
    renderNav();
  });

  resetBtn.addEventListener('click', async () => {
    resetBtn.disabled = true;
    const response = await fetch(`/api/admin/settings/${kind}`, { method: 'DELETE' });
    resetBtn.disabled = false;

    const data = await response.json();
    if (!response.ok) return;
    message.textContent = 'Сброшено на стандартное';
    message.style.color = 'var(--color-success)';
    preview.src = data.url;
    siteSettingsPromise = null;
    renderNav();
  });
}

// ---------- Инициализация ----------

(async () => {
  const me = await checkAccess();
  if (!me) return;
  isSuperadmin = me.role === 'superadmin';
  initAdminMenu();
  initAdminSubtabs(serversAdminSection);

  await loadGames();
  await loadServersAdmin();
  await loadArticlesAdmin();

  if (isSuperadmin) {
    await loadUsers();
    await populateGameAdminsPickers();
    await loadGameAdmins();
    initAssetUploader('logo');
    initAssetUploader('favicon');
    await loadSettingsForm();
  }
})();
