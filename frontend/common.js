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

  const faviconLink = document.getElementById('site-favicon');
  if (faviconLink) faviconLink.href = settings.favicon_url || '/logo.svg';

  // значок Steam в шапке — виден гостю сразу на любой странице (включая главную),
  // чтобы не пришлось сначала открывать /login, чтобы узнать про такой способ входа
  const steamBtn = `
    <a href="/auth/steam/login" class="btn-steam" title="Войти через Steam" aria-label="Войти через Steam">
      <svg viewBox="0 0 24 24" width="20" height="20" fill="currentColor" aria-hidden="true">
        <path d="M11.979 0C5.678 0 .511 4.86.022 11.037l6.432 2.658c.545-.371 1.203-.59 1.912-.59.063 0 .125.004.188.006l2.861-4.142v-.059c0-2.495 2.028-4.524 4.524-4.524 2.494 0 4.524 2.031 4.524 4.527s-2.03 4.525-4.524 4.525h-.105l-4.076 2.911c0 .052.004.105.004.159 0 1.875-1.515 3.396-3.39 3.396-1.635 0-3.016-1.173-3.331-2.727L.436 15.27C1.862 20.307 6.486 24 11.979 24c6.627 0 11.999-5.373 11.999-12S18.605 0 11.979 0zM7.54 18.21l-1.473-.61c.262.543.714.999 1.314 1.25 1.297.539 2.793-.076 3.332-1.375.263-.63.264-1.319.005-1.949s-.75-1.121-1.377-1.383c-.624-.26-1.29-.249-1.878-.03l1.523.63c.956.4 1.409 1.5 1.009 2.455-.398.957-1.497 1.41-2.454 1.012H7.54zm11.415-9.303c0-1.662-1.353-3.015-3.015-3.015-1.665 0-3.015 1.353-3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015-1.35 3.015-3.015zm-5.273-.005c0-1.252 1.013-2.266 2.265-2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251-1.017 2.265-2.266 2.265-1.253 0-2.265-1.014-2.265-2.265z"/>
      </svg>
    </a>
  `;

  const authLinks = me
    ? `
      <a href="/cabinet" class="btn btn-secondary">Кабинет</a>
      ${me.role === 'admin' || me.role === 'superadmin' ? '<a href="/admin" class="btn btn-secondary">Админка</a>' : ''}
      <button id="nav-logout-btn" class="btn-secondary">Выйти</button>
    `
    : `
      ${steamBtn}
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
      // сид отдаётся сервером только админам (см. backend/content.go) — обычный игрок
      // просто не получит поле world_seed, кнопка сама решает, что вообще показывать
      const infoBtn = s.connect_password || s.world_seed
        ? `<button type="button" class="btn-secondary info-btn"
             data-password="${escapeHtml(s.connect_password ?? '')}"
             data-seed="${escapeHtml(s.world_seed ?? '')}"
             data-server-name="${escapeHtml(displayName)}">ℹ️ Информация</button>`
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
          ${infoBtn}
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

  container.querySelectorAll('.info-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      const password = btn.dataset.password;
      const seed = btn.dataset.seed;

      const passwordRow = password
        ? `
          <p class="hint" style="margin-bottom:4px;">Пароль</p>
          <div class="password-reveal">
            <code>${escapeHtml(password)}</code>
            <button type="button" class="copy-btn" data-copy="${escapeHtml(password)}" title="Скопировать пароль">📋</button>
          </div>
        `
        : '<p class="hint">Пароль не установлен</p>';

      // seed сюда попадает только для админов — see backend/content.go (world_seed отдаётся
      // только при роли admin+), так что отдельно проверять роль на фронте не нужно
      const seedRow = seed
        ? `
          <p class="hint" style="margin:var(--spacing-md) 0 4px;">Сид мира (видно только админам)</p>
          <div class="password-reveal">
            <code>${escapeHtml(seed)}</code>
            <button type="button" class="copy-btn" data-copy="${escapeHtml(seed)}" title="Скопировать сид">📋</button>
          </div>
        `
        : '';

      const overlay = showModal(btn.dataset.serverName, passwordRow + seedRow);
      overlay.querySelectorAll('.copy-btn').forEach((copyBtn) => {
        copyBtn.addEventListener('click', (event) => copyToClipboard(copyBtn.dataset.copy, event.currentTarget));
      });
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
