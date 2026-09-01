function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str ?? '';
  return div.innerHTML;
}

const status = document.getElementById('status');
const content = document.getElementById('content');

let games = [];

async function checkAccess() {
  const response = await fetch('/api/me');
  if (!response.ok) {
    window.location.href = 'login.html';
    return false;
  }
  const data = await response.json();
  if (data.role !== 'admin' && data.role !== 'superadmin') {
    status.textContent = 'Недостаточно прав для этой страницы.';
    return false;
  }
  status.textContent = `Ты вошёл как ${data.nickname} (${data.role})`;
  content.style.display = 'block';
  return true;
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
    .map(
      (s) => `
        <div class="card">
          <strong>${escapeHtml(s.name)}</strong> (${escapeHtml(s.game_name)})<br>
          <code>${escapeHtml(s.host)}:${escapeHtml(s.port)}</code><br>
          <span>${escapeHtml(s.description)}</span><br>
          <button class="btn-secondary" data-edit-server="${s.id}">Изменить</button>
          <button class="btn-danger" data-delete-server="${s.id}">Удалить</button>
        </div>
      `
    )
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
}

function startEditServer(server) {
  serverIdInput.value = server.id;
  serverGameSelect.value = String(games.find((g) => g.slug === server.game_slug)?.id ?? '');
  serverNameInput.value = server.name;
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

// ---------- Инициализация ----------

(async () => {
  const allowed = await checkAccess();
  if (!allowed) return;
  await loadGames();
  await loadServersAdmin();
  await loadArticlesAdmin();
})();
