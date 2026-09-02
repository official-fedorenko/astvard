const status = document.getElementById('status');
const content = document.getElementById('content');
const profileCard = document.getElementById('profile-card');
const adminStats = document.getElementById('admin-stats');
const adminStatsList = document.getElementById('admin-stats-list');

const roleLabels = { player: 'игрок', admin: 'админ', superadmin: 'супер-админ' };
const authMethodLabels = { email: 'Email/пароль', steam: 'Steam' };

function renderProfileCard(data) {
  const avatar = data.avatarUrl
    ? `<img class="profile-avatar" src="${escapeHtml(data.avatarUrl)}" alt="">`
    : '';
  const joined = new Date(data.createdAt).toLocaleDateString('ru-RU');
  const metaParts = [`Вход через ${authMethodLabels[data.authMethod] ?? data.authMethod}`];
  if (data.email) metaParts.push(escapeHtml(data.email));
  metaParts.push(`на сайте с ${joined}`);

  profileCard.innerHTML = `
    <div class="card">
      <div class="card-header">
        <span class="card-title">${avatar}${escapeHtml(data.nickname)}</span>
        <span class="badge badge-role-${data.role}">${roleLabels[data.role] ?? data.role}</span>
      </div>
      <div class="card-meta">${metaParts.join(' · ')}</div>
    </div>
  `;
}

async function loadAdminStats() {
  const response = await fetch('/api/admin/stats');
  if (!response.ok) return;
  const data = await response.json();

  adminStats.style.display = 'block';
  adminStatsList.innerHTML = `
    <div class="stat-grid">
      <div class="stat-tile">
        <div class="stat-value">${data.users}</div>
        <div class="stat-label">Пользователей</div>
      </div>
      <div class="stat-tile">
        <div class="stat-value">${data.servers}</div>
        <div class="stat-label">Серверов (${data.onlineServers} онлайн)</div>
      </div>
      <div class="stat-tile">
        <div class="stat-value">${data.articles}</div>
        <div class="stat-label">Статей</div>
      </div>
    </div>
  `;
}

async function init() {
  // renderNav() уже делает единственный запрос к /api/me — переиспользуем результат,
  // а не дёргаем эндпоинт второй раз
  const me = await renderNav();

  if (!me) {
    window.location.href = 'login';
    return;
  }

  status.textContent = '';
  renderProfileCard(me);
  content.style.display = 'block';

  if (me.role === 'admin' || me.role === 'superadmin') {
    loadAdminStats();
  }

  loadServers();
  loadArticles();
}

init();
