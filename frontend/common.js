function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str ?? '';
  return div.innerHTML;
}

// Общая шапка с навигацией — один раз тут, подключается на каждой странице через
// <header id="site-header"></header> + <script src="common.js">, дальше просто
// вызвать renderNav(). Сама разбирается, залогинен ли человек и какая у него роль.
async function renderNav() {
  const header = document.getElementById('site-header');
  if (!header) return;

  let me = null;
  try {
    const response = await fetch('/api/me');
    if (response.ok) me = await response.json();
  } catch {
    // не залогинен или сервер недоступен — просто покажем гостевые ссылки
  }

  const authLinks = me
    ? `
      <a href="/cabinet" class="btn btn-secondary">Кабинет</a>
      ${me.role === 'admin' || me.role === 'superadmin' ? '<a href="/admin" class="btn btn-secondary">Админка</a>' : ''}
      <button id="nav-logout-btn" class="btn-secondary">Выйти</button>
    `
    : `
      <a href="/login" class="btn btn-secondary">Войти</a>
      <a href="/register" class="btn">Регистрация</a>
    `;

  header.innerHTML = `
    <nav class="row" style="justify-content:space-between;">
      <div class="row">
        <a href="/" class="site-logo">Astvard</a>
        <a href="/#servers">Сервера</a>
        <a href="/#articles">Статьи</a>
      </div>
      <div class="row">${authLinks}</div>
    </nav>
  `;

  const logoutBtn = document.getElementById('nav-logout-btn');
  if (logoutBtn) {
    logoutBtn.addEventListener('click', async () => {
      await fetch('/api/logout', { method: 'POST' });
      window.location.href = '/login';
    });
  }

  return me;
}

// Общие для главной и кабинета — обе страницы показывают один и тот же список
// серверов/статей, просто в разном контексте (публично / после входа)

async function loadServers() {
  const container = document.getElementById('servers-list');
  if (!container) return;

  const response = await fetch('/api/servers');
  const servers = await response.json();

  container.innerHTML = servers
    .map((s) => {
      const statusText =
        s.online === true ? '🟢 онлайн' : s.online === false ? '🔴 офлайн' : '⚪ ещё не проверялся';
      // реальное имя, которое сообщил сам игровой сервер, приоритетнее ручного названия
      const displayName = s.reported_name || s.name;
      const statsParts = [];
      if (s.uptime_percent != null) statsParts.push(`аптайм 24ч: ${s.uptime_percent}%`);
      if (s.peak_players != null) statsParts.push(`пик игроков за 24ч: ${s.peak_players}`);
      const statsLine = statsParts.length ? `<br><span style="color:var(--color-text-muted); font-size:0.85rem;">${statsParts.join(' · ')}</span>` : '';
      return `
        <div class="card">
          <strong>${escapeHtml(displayName)}</strong> (${escapeHtml(s.game_name)}) — ${statusText}<br>
          <code>${escapeHtml(s.host)}:${escapeHtml(s.port)}</code><br>
          <span>${escapeHtml(s.description)}</span>${statsLine}
        </div>
      `;
    })
    .join('');
}

async function loadArticles() {
  const container = document.getElementById('articles-list');
  if (!container) return;

  const response = await fetch('/api/articles');
  const articles = await response.json();

  container.innerHTML = articles
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
