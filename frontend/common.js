function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str ?? '';
  return div.innerHTML;
}

// ---------- Модалка (общая) ----------
// Простое окно поверх страницы: заголовок + произвольный HTML внутри + кнопка закрытия.
// Закрывается по клику на подложку, на "Закрыть" или по Escape.

function showModal(title, bodyHtml) {
  document.getElementById('site-modal-overlay')?.remove();

  const overlay = document.createElement('div');
  overlay.id = 'site-modal-overlay';
  overlay.className = 'modal-overlay';
  overlay.innerHTML = `
    <div class="modal-box" role="dialog" aria-modal="true" aria-label="${escapeHtml(title)}">
      <h3>${escapeHtml(title)}</h3>
      <div class="modal-body">${bodyHtml}</div>
      <button type="button" class="btn-secondary" data-modal-close style="margin-top:var(--spacing-md);">Закрыть</button>
    </div>
  `;
  document.body.appendChild(overlay);

  const close = () => overlay.remove();
  overlay.addEventListener('click', (event) => {
    if (event.target === overlay) close();
  });
  overlay.querySelector('[data-modal-close]').addEventListener('click', close);
  document.addEventListener('keydown', function onKey(event) {
    if (event.key === 'Escape') {
      close();
      document.removeEventListener('keydown', onKey);
    }
  });

  return overlay;
}

// ---------- Копирование в буфер ----------

async function copyToClipboard(text, btn) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    return; // буфер обмена недоступен (например, страница открыта не по https) — просто молчим
  }
  const original = btn.textContent;
  btn.textContent = '✓';
  setTimeout(() => {
    btn.textContent = original;
  }, 1200);
}

// Общая шапка с навигацией — один раз тут, подключается на каждой странице через
// <header id="site-header"></header> + <script src="common.js">, дальше просто
// вызвать renderNav(). Сама разбирается, залогинен ли человек и какая у него роль.
// Настройки сайта (название, подзаголовок, футер) из админки. Промис кэшируется,
// чтобы шапка, футер и главная не ходили за ними по три раза за загрузку.
let siteSettingsPromise = null;
function loadSiteSettings() {
  if (!siteSettingsPromise) {
    siteSettingsPromise = fetch('/api/settings')
      .then((r) => (r.ok ? r.json() : {}))
      .catch(() => ({}));
  }
  return siteSettingsPromise;
}

async function renderNav() {
  const header = document.getElementById('site-header');
  if (!header) return;

  let me = null;
  const [meResult, settings] = await Promise.all([
    fetch('/api/me').catch(() => null),
    loadSiteSettings(),
  ]);
  try {
    if (meResult?.ok) me = await meResult.json();
  } catch {
    // не залогинен или сервер недоступен — просто покажем гостевые ссылки
  }
  const siteName = settings.site_name || 'Astvard';
  const logoUrl = settings.logo_url || '/logo.svg';

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
        <a href="/" class="site-logo"><img src="${escapeHtml(logoUrl)}" class="site-logo-img" alt="">${escapeHtml(siteName)}</a>
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

  const footer = document.getElementById('site-footer');
  if (footer) {
    footer.textContent = settings.footer_text || `© ${new Date().getFullYear()} ${siteName}`;
  }

  return me;
}

// Общие для главной и кабинета — обе страницы показывают один и тот же список
// серверов/статей, просто в разном контексте (публично / после входа)

// иконка + акцентный цвет левой полоски карточки — под конкретную игру;
// неизвестная игра просто получает нейтральный вид
const GAME_VISUALS = {
  valheim: { icon: '⚔️', accent: '#7cb87c' },
  cs2: { icon: '🎯', accent: '#e6a23c' },
  minecraft: { icon: '⛏️', accent: '#8bc34a' },
};

function gameVisual(slug) {
  return GAME_VISUALS[slug] ?? { icon: '🎮', accent: 'var(--color-primary)' };
}

function statusBadge(online) {
  if (online === true) return '<span class="badge badge-online">🟢 онлайн</span>';
  if (online === false) return '<span class="badge badge-offline">🔴 офлайн</span>';
  return '<span class="badge badge-pending">⚪ не проверялся</span>';
}

async function loadServers() {
  const container = document.getElementById('servers-list');
  if (!container) return;

  const response = await fetch('/api/servers');
  const servers = await response.json();

  const cards = servers
    .map((s) => {
      // реальное имя, которое сообщил сам игровой сервер, приоритетнее ручного названия
      const displayName = s.reported_name || s.name;
      const { icon, accent } = gameVisual(s.game_slug);

      const statsParts = [];
      if (s.uptime_percent != null) statsParts.push(`📶 аптайм 24ч: ${s.uptime_percent}%`);
      if (s.peak_players != null) statsParts.push(`🏆 пик за 24ч: ${s.peak_players}`);
      const statsLine = statsParts.length
        ? `<div class="server-stats">${statsParts.map((p) => `<span>${p}</span>`).join('')}</div>`
        : '';

      let playersLine = '';
      if (s.players_online != null) {
        const chips = s.player_names?.length
          ? `<div class="player-chips">${s.player_names
              .map((name) => `<span class="player-chip">${escapeHtml(name)}</span>`)
              .join('')}</div>`
          : '';
        playersLine = `<div class="player-count">👥 Игроков: ${s.players_online}/${s.players_max}</div>${chips}`;
      }

      const descriptionLine = s.description
        ? `<p class="server-description">${escapeHtml(s.description)}</p>`
        : '';

      const address = `${s.host}:${s.port}`;
      const passwordBtn = s.connect_password
        ? `<button type="button" class="btn-secondary password-btn" data-password="${escapeHtml(s.connect_password)}" data-server-name="${escapeHtml(displayName)}">🔑 Пароль</button>`
        : '';

      return `
        <div class="card card-server" style="--game-accent:${accent};">
          <div class="card-header">
            <span class="card-title">${icon} ${escapeHtml(displayName)}</span>
            ${statusBadge(s.online)}
          </div>
          <div class="card-meta">${escapeHtml(s.game_name)}</div>
          <div class="server-address">
            <span>🔗 ${escapeHtml(address)}</span>
            <button type="button" class="copy-btn" data-copy="${escapeHtml(address)}" title="Скопировать адрес">📋</button>
          </div>
          ${passwordBtn}
          ${descriptionLine}
          ${playersLine}
          ${statsLine}
        </div>
      `;
    })
    .join('');

  container.innerHTML = `<div class="grid">${cards}</div>`;

  container.querySelectorAll('.copy-btn').forEach((btn) => {
    btn.addEventListener('click', () => copyToClipboard(btn.dataset.copy, btn));
  });

  container.querySelectorAll('.password-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      const password = btn.dataset.password;
      const overlay = showModal('Пароль подключения', `
        <p class="hint" style="margin-bottom:var(--spacing-sm);">${escapeHtml(btn.dataset.serverName)}</p>
        <div class="password-reveal">
          <code>${escapeHtml(password)}</code>
          <button type="button" class="copy-btn" data-copy="${escapeHtml(password)}" title="Скопировать пароль">📋</button>
        </div>
      `);
      overlay.querySelector('.copy-btn').addEventListener('click', (event) => copyToClipboard(password, event.currentTarget));
    });
  });
}

const ARTICLE_PREVIEW_LENGTH = 160;

async function loadArticles() {
  const container = document.getElementById('articles-list');
  if (!container) return;

  const response = await fetch('/api/articles');
  const articles = await response.json();

  const cards = articles
    .map((a, i) => {
      const isLong = a.content.length > ARTICLE_PREVIEW_LENGTH;
      const previewRaw = isLong ? a.content.slice(0, ARTICLE_PREVIEW_LENGTH) + '…' : a.content;
      const readMoreBtn = isLong
        ? `<button class="btn-secondary read-more-btn" data-expand-article="${i}">Читать дальше</button>`
        : '';

      return `
        <div class="card card-article">
          <div class="card-header">
            <span class="card-title">📰 ${escapeHtml(a.title)}</span>
          </div>
          <p data-article-text="${i}">${escapeHtml(previewRaw)}</p>
          ${readMoreBtn}
        </div>
      `;
    })
    .join('');

  container.innerHTML = `<div class="grid">${cards}</div>`;

  container.querySelectorAll('[data-expand-article]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const i = btn.dataset.expandArticle;
      container.querySelector(`[data-article-text="${i}"]`).textContent = articles[i].content;
      btn.remove();
    });
  });
}
