function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str ?? '';
  return div.innerHTML;
}

const status = document.getElementById('status');
const logoutBtn = document.getElementById('logout-btn');
const content = document.getElementById('content');
const serversList = document.getElementById('servers-list');
const articlesList = document.getElementById('articles-list');

async function loadServers() {
  const response = await fetch('/api/servers');
  const servers = await response.json();

  serversList.innerHTML = servers
    .map(
      (s) => {
        const statusText =
          s.online === true ? '🟢 онлайн' : s.online === false ? '🔴 офлайн' : '⚪ ещё не проверялся';
        // реальное имя, которое сообщил сам игровой сервер, приоритетнее ручного названия
        const displayName = s.reported_name || s.name;
        return `
        <div class="card">
          <strong>${escapeHtml(displayName)}</strong> (${escapeHtml(s.game_name)}) — ${statusText}<br>
          <code>${escapeHtml(s.host)}:${escapeHtml(s.port)}</code><br>
          <span>${escapeHtml(s.description)}</span>
        </div>
      `;
      }
    )
    .join('');
}

async function loadArticles() {
  const response = await fetch('/api/articles');
  const articles = await response.json();

  articlesList.innerHTML = articles
    .map(
      (a) => `
        <div class="card">
          <strong>${escapeHtml(a.title)}</strong>
          <p>${escapeHtml(a.content)}</p>
        </div>
      `
    )
    .join('');
}

async function loadMe() {
  const response = await fetch('/api/me');

  if (!response.ok) {
    // не залогинен — отправляем на страницу входа
    window.location.href = 'login.html';
    return;
  }

  const data = await response.json();
  const roleLabels = { player: 'игрок', admin: 'админ', superadmin: 'супер-админ' };
  status.textContent = `Привет, ${data.nickname}! (${data.email}) — ${roleLabels[data.role] ?? data.role}`;
  logoutBtn.style.display = 'inline-block';
  content.style.display = 'block';

  if (data.role === 'admin' || data.role === 'superadmin') {
    document.getElementById('admin-link').style.display = 'inline';
  }

  loadServers();
  loadArticles();
}

logoutBtn.addEventListener('click', async () => {
  await fetch('/api/logout', { method: 'POST' });
  window.location.href = 'login.html';
});

loadMe();
