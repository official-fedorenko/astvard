const status = document.getElementById('status');
const logoutBtn = document.getElementById('logout-btn');
const content = document.getElementById('content');
const profileCard = document.getElementById('profile-card');
const adminStats = document.getElementById('admin-stats');
const adminStatsList = document.getElementById('admin-stats-list');

const roleLabels = { player: 'игрок', admin: 'админ', superadmin: 'супер-админ' };
const authMethodLabels = { email: 'Email/пароль', steam: 'Steam' };

function renderProfileCard(data) {
  const avatar = data.avatarUrl
    ? `<img src="${escapeHtml(data.avatarUrl)}" alt="" width="64" height="64" style="border-radius:8px; vertical-align:middle; margin-right:12px;">`
    : '';
  const emailLine = data.email ? `<br>Email: ${escapeHtml(data.email)}` : '';
  const joined = new Date(data.createdAt).toLocaleDateString('ru-RU');

  profileCard.innerHTML = `
    ${avatar}
    <strong style="font-size:1.1rem;">${escapeHtml(data.nickname)}</strong><br>
    Роль: ${roleLabels[data.role] ?? data.role}<br>
    Вход через: ${authMethodLabels[data.authMethod] ?? data.authMethod}${emailLine}<br>
    На сайте с: ${joined}
  `;
}

async function loadAdminStats() {
  const response = await fetch('/api/admin/stats');
  if (!response.ok) return;
  const data = await response.json();

  adminStats.style.display = 'block';
  adminStatsList.innerHTML = `
    <div class="card" style="max-width:420px;">
      Пользователей: ${data.users}<br>
      Серверов: ${data.servers} (онлайн: ${data.onlineServers})<br>
      Статей: ${data.articles}
    </div>
  `;
}

async function loadMe() {
  const response = await fetch('/api/me');

  if (!response.ok) {
    // не залогинен — отправляем на страницу входа
    window.location.href = 'login';
    return;
  }

  const data = await response.json();
  status.textContent = '';
  renderProfileCard(data);
  content.style.display = 'block';

  if (data.role === 'admin' || data.role === 'superadmin') {
    document.getElementById('admin-link').style.display = 'inline-flex';
    loadAdminStats();
  }

  loadServers();
  loadArticles();
}

logoutBtn.addEventListener('click', async () => {
  await fetch('/api/logout', { method: 'POST' });
  window.location.href = 'login';
});

loadMe();
