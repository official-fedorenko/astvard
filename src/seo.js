const fs = require('node:fs/promises');
const path = require('node:path');
const { db, DEFAULT_SETTINGS } = require('../db');
const logger = require('./logger');

/**
 * Everything a search engine or a link preview reads, built on the server.
 *
 * The public pages used to arrive as a shell: the title, the texts of every section
 * and the list of articles were filled in by client/app.js after load. Google runs
 * scripts on a second pass, when it gets round to it; Yandex runs them far less
 * reliably; Telegram, VK and Discord never do. So what they indexed and showed was
 * the placeholder in the markup — "О нашем блоге", a street in Vilnius, and the
 * title "Astvard" that app.js put over the real one. Now the HTML leaves the server
 * finished, and the scripts only add what depends on the visitor or on the game.
 */

const CLIENT_DIR = path.join(__dirname, '..', 'client');
const TEMPLATES_DIR = path.join(__dirname, 'templates');

// The address search engines are given, from APP_URL and never from Host: the header
// is written by whoever is asking, and a canonical link pointing wherever they like
// would hand our pages to them.
function siteUrl() {
  return (process.env.APP_URL || 'http://localhost:3001').replace(/\/+$/, '');
}

const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[ch]));
}

// One pass over the template: {{name}} is escaped, {{{name}}} goes in as it is.
// Inserted values are not scanned again, so braces inside an article stay text. A
// name the caller did not supply is left in the page on purpose — a visible
// {{name}} is found at once, an empty spot is not.
function fill(template, values) {
  return template.replace(/\{\{\{(\w+)\}\}\}|\{\{(\w+)\}\}/g, (match, rawName, name) => {
    if (rawName) return rawName in values ? String(values[rawName]) : match;
    return name in values ? escapeHtml(values[name]) : match;
  });
}

// JSON inside <script>: "</script>" in any string would end the element early.
function jsonForScript(data) {
  return JSON.stringify(data).replace(/</g, '\\u003c');
}

async function loadSettings() {
  const values = Object.fromEntries(DEFAULT_SETTINGS.map(([key, value]) => [key, value]));
  try {
    for (const row of await all('SELECT key, value FROM settings')) values[row.key] = row.value ?? '';
  } catch (err) {
    // Defaults still make a whole page; a search engine that meets a 500 here drops
    // the address from the index sooner than one that meets last week's texts.
    logger.error('[seo] настройки не прочитались, страница собрана из значений по умолчанию:', err.message);
  }
  return values;
}

// A text setting cleared in the admin panel falls back to its default: an empty
// <title> or <h1> is worse than the stock one.
function text(settings, key) {
  const value = String(settings[key] ?? '').trim();
  if (value) return value;
  const fallback = DEFAULT_SETTINGS.find(([k]) => k === key);
  return fallback ? fallback[1] : '';
}

// Plain text of an article, for the places where markup cannot go: the description
// and the excerpt on a card.
function plainText(html) {
  return String(html || '')
    .replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, ' ')
    .replace(/<br\s*\/?>|<\/(p|h[1-6]|li|blockquote|pre)>/gi, ' ')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#0?39;/g, "'")
    .replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim();
}

function clip(value, max) {
  if (value.length <= max) return value;
  const cut = value.slice(0, max - 1);
  const space = cut.lastIndexOf(' ');
  return `${(space > max * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,.;:—-]+$/, '')}…`;
}

const TRANSLIT = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y',
  к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f',
  х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya'
};

function slugify(title) {
  return Array.from(String(title || '').toLowerCase(), (ch) => TRANSLIT[ch] ?? ch).join('')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)
    .replace(/-+$/, '');
}

// The id decides which article it is, the words after it are only for people and
// search engines. A renamed article keeps its old links working: they answer with a
// redirect to the new address instead of a 404.
const NEWS_PATH_RE = /^\/news\/(\d{1,9})(?:-[a-z0-9-]*)?\/?$/;

function articlePath(article) {
  const slug = slugify(article.title);
  return `/news/${article.id}${slug ? `-${slug}` : ''}`;
}

async function publishedArticles() {
  return all(
    `SELECT id, title, content, created_at, updated_at FROM articles
     WHERE status = 'published' ORDER BY id DESC LIMIT 1000`
  );
}

async function publishedArticle(id) {
  const rows = await all(
    `SELECT id, title, content, created_at, updated_at FROM articles
     WHERE id = ? AND status = 'published'`,
    [id]
  );
  return rows[0] || null;
}

const iso = (value) => new Date(value).toISOString();

const dateRu = (value) => new Date(value).toLocaleDateString('ru-RU', {
  day: 'numeric', month: 'long', year: 'numeric', timeZone: 'Europe/Moscow'
});

// People paste whatever the service showed them: the bare code, the whole <meta>
// tag, the whole counter snippet. Take the one thing we need out of any of them and
// refuse anything else — the value ends up in the head of every page.
function verificationCode(raw) {
  const value = String(raw || '').trim();
  const fromTag = value.match(/content\s*=\s*["']([^"']+)["']/i);
  const code = (fromTag ? fromTag[1] : value).trim();
  return /^[A-Za-z0-9_-]{8,128}$/.test(code) ? code : '';
}

function metrikaId(raw) {
  const value = String(raw || '');
  const match = value.match(/ym\(\s*(\d{4,12})\b/)
    || value.match(/mc\.yandex\.ru\/watch\/(\d{4,12})\b/)
    || value.match(/^\s*(\d{4,12})\s*$/);
  return match ? match[1] : '';
}

/**
 * The counter and two goals, for every public page when a counter number is set.
 * Goals are what an ad campaign is tuned by: "steam_login" is a click on any
 * "Войти через Steam", "whitelist_request" is sent by the cabinet when a request
 * went through. In Metrika they are created as "JavaScript event" goals with these
 * names. No Webvisor: recording visitors' screens is not something to switch on
 * for them quietly.
 */
// Марки каналов встроены прямо в разметку. У сайта строгая CSP, и внешний
// источник иконок пришлось бы в неё вписывать ради четырёх картинок — а так
// нет ни лишней загрузки, ни ещё одного чужого домена, которому мы доверяем.
// Контуры взяты из Simple Icons (CC0 1.0), все в системе координат 24x24.
// Сами логотипы — товарные знаки своих владельцев, и стоят они ровно там,
// куда ведут.
const CHANNEL_ICONS = {
  discord_url: 'M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z',
  telegram_url: 'M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z',
  thunderstore_url: 'm.322 13.174 4.706 8.192L7.2 16.855 4.824 12.72a1.416 1.416 0 0 1 0-1.444l2.965-5.16c.265-.46.718-.723 1.245-.723h1.595l-3.086 6.953h3.812L6.171 22.403 16.583 9.914h-3.201l2.184-4.52h6.052L24 1.25H7.175c-.86 0-1.598.428-2.028 1.174l-4.825 8.4a2.306 2.306 0 0 0 0 2.35m7.213 9.576h9.29a2.29 2.29 0 0 0 2.03-1.176l4.825-8.4a2.317 2.317 0 0 0 0-2.35l-1.93-3.36h-4.763l2.19 3.813c.262.46.262.987 0 1.444l-2.964 5.162a1.41 1.41 0 0 1-1.248.723h-2.154l-1.497-.017z',
  mod_download_url: 'M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12'
};

function channelIcon(key) {
  const d = CHANNEL_ICONS[key];
  if (!d) return '';

  return `<svg class="channel-icon" viewBox="0 0 24 24" width="16" height="16"`
    + ` fill="currentColor" aria-hidden="true" focusable="false"><path d="${d}"/></svg>`;
}

// Адрес ссылки пишет человек в админке, а попадает он в href на каждой странице.
// Поэтому схема проверяется, а не подставляется как есть: "javascript:" в поле
// настройки иначе стал бы XSS для всех посетителей сразу.
function safeUrl(value) {
  const raw = String(value ?? '').trim();
  if (!raw) return null;

  try {
    const url = new URL(raw);
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null;
  } catch {
    return null;
  }
}

// Пустая настройка — не пустая ссылка, а её отсутствие: ведущая в никуда ссылка
// в подвале выглядит как заброшенный сайт.
function communityLinks(settings) {
  const html = [
    ['discord_url', 'Discord'],
    ['telegram_url', 'Telegram'],
    ['thunderstore_url', 'Мод на Thunderstore'],
    ['mod_download_url', 'Скачать мод']
  ]
    .map(([key, label]) => [key, safeUrl(settings[key]), label])
    .filter(([, href]) => href)
    .map(([key, href, label]) => `<a class="footer__link" href="${escapeHtml(href)}"`
      + ` target="_blank" rel="noopener">${channelIcon(key)}<span>${escapeHtml(label)}</span></a>`)
    .join('');

  return html ? `<nav class="footer__links" aria-label="Сообщество">${html}</nav>` : '';
}

// Два способа поставить мод, и оба необязательные. Файлом — кто не держит
// менеджер модов; через менеджер — кому проще, там BepInEx и Jotunn приедут
// сами. Кнопки появляются порознь: настроен один адрес — будет одна кнопка.
function modDownload(settings) {
  const direct = safeUrl(settings.mod_download_url);
  const store = safeUrl(settings.thunderstore_url);
  if (!direct && !store) return '';

  const buttons = [];

  if (direct) {
    buttons.push(`<a class="btn btn--primary" href="${escapeHtml(direct)}"`
      + ` target="_blank" rel="noopener">${channelIcon('mod_download_url')}`
      + '<span>Скачать мод</span></a>');
  }

  if (store) {
    buttons.push(`<a class="btn btn--ghost" href="${escapeHtml(store)}"`
      + ` target="_blank" rel="noopener">${channelIcon('thunderstore_url')}`
      + '<span>Через менеджер модов</span></a>');
  }

  return `<p class="join-download">${buttons.join('')}`
    + '<span class="join-download__hint">Необязательно: без мода на сервер тоже пускают</span></p>';
}

// Та же пара адресов, но кнопкой в меню и окном с выбором: мод — главное, за чем
// сюда приходят, а раньше его искали внизу страницы «Что умеет мод» или в подвале.
// Кнопки нет вовсе, пока не задан ни один адрес: кнопка, открывающая пустое окно,
// хуже её отсутствия.
function downloadButton(settings) {
  if (!safeUrl(settings.mod_download_url) && !safeUrl(settings.thunderstore_url)) return '';

  return `        <div class="sidebar__cta">
          <button type="button" class="btn btn--primary" id="downloadBtn" aria-haspopup="dialog">
            <span class="sidebar__link-icon"><i data-lucide="download"></i></span>
            <span>Скачать мод</span>
          </button>
        </div>`;
}

/**
 * Окно выбора. Обе ссылки настоящие и лежат в разметке: кто выключил скрипты,
 * всё равно дойдёт до них с «Что умеет мод» и из подвала, а тут ничего не грузится
 * запросом — окно открывается и при мёртвом API.
 */
function downloadModal(settings) {
  const direct = safeUrl(settings.mod_download_url);
  const store = safeUrl(settings.thunderstore_url);
  if (!direct && !store) return '';

  const way = (href, key, title, text) => `
        <a class="download-way" href="${escapeHtml(href)}" target="_blank" rel="noopener">
          <span class="download-way__icon">${channelIcon(key)}</span>
          <span>
            <span class="download-way__title">${escapeHtml(title)}</span>
            <span class="download-way__text">${escapeHtml(text)}</span>
          </span>
        </a>`;

  const ways = [];

  if (direct) {
    ways.push(way(direct, 'mod_download_url', 'Скачать с GitHub',
      'Архив с модом и Jotunn внутри: распаковать в папку с игрой. Годится, когда менеджера модов нет.'));
  }

  if (store) {
    ways.push(way(store, 'thunderstore_url', 'Через менеджер модов',
      'Thunderstore: BepInEx и Jotunn приедут сами, обновление — одной кнопкой. Игру потом запускать из менеджера.'));
  }

  return `    <div class="site-modal" id="downloadModal" role="dialog" aria-modal="true" aria-labelledby="downloadModalTitle">
      <div class="site-modal__box">
        <button type="button" class="site-modal__close" aria-label="Закрыть"><i data-lucide="x"></i></button>
        <h2 class="site-modal__title" id="downloadModalTitle">Скачать мод</h2>
        <p class="site-modal__lead">Мод необязателен: на сервер пускают и без него, просто не будет панели. Способа два, оба рабочие.</p>
        <div class="download-ways">${ways.join('')}
        </div>
        <p class="download-note"><a class="text-link" href="/mod#install">Как поставить — по шагам</a></p>
      </div>
    </div>`;
}

function analyticsTags(settings) {
  const id = metrikaId(settings.yandex_metrika_id);
  if (!id) return '';
  return `
  <script>
    (function (m, e, t, r, i, k, a) {
      m[i] = m[i] || function () { (m[i].a = m[i].a || []).push(arguments); };
      m[i].l = 1 * new Date();
      k = e.createElement(t); a = e.getElementsByTagName(t)[0];
      k.async = 1; k.src = r; a.parentNode.insertBefore(k, a);
    })(window, document, 'script', 'https://mc.yandex.ru/metrika/tag.js', 'ym');
    ym(${id}, 'init', { clickmap: true, trackLinks: true, accurateTrackBounce: true });
    window.astvardGoal = function (name) { try { ym(${id}, 'reachGoal', name); } catch (e) {} };
    document.addEventListener('click', function (e) {
      var link = e.target && e.target.closest && e.target.closest('a[href^="/api/auth/steam"]');
      if (link) window.astvardGoal('steam_login');
    });
  </script>
  <noscript><div><img src="https://mc.yandex.ru/watch/${id}" style="position:absolute; left:-9999px;" alt=""></div></noscript>
`;
}

function headTags(settings, { title, description, pagePath, type = 'website', image, published, modified, jsonLd }) {
  const url = `${siteUrl()}${pagePath}`;
  const picture = image || `${siteUrl()}/img/astvard-og.png`;
  const tags = [
    `<title>${escapeHtml(title)}</title>`,
    `<meta name="description" content="${escapeHtml(description)}">`,
    `<link rel="canonical" href="${escapeHtml(url)}">`,
    '<meta name="robots" content="index, follow, max-image-preview:large">',
    `<meta property="og:type" content="${type}">`,
    '<meta property="og:locale" content="ru_RU">',
    `<meta property="og:site_name" content="${escapeHtml(text(settings, 'site_name'))}">`,
    `<meta property="og:title" content="${escapeHtml(title)}">`,
    `<meta property="og:description" content="${escapeHtml(description)}">`,
    `<meta property="og:url" content="${escapeHtml(url)}">`,
    // Raster and absolute on purpose: no messenger renders an SVG preview, and not
    // every scraper expands a relative path.
    `<meta property="og:image" content="${escapeHtml(picture)}">`
  ];
  if (!image) {
    tags.push('<meta property="og:image:width" content="1200">', '<meta property="og:image:height" content="630">');
  }
  tags.push(`<meta property="og:image:alt" content="${escapeHtml(title)}">`);
  if (published) tags.push(`<meta property="article:published_time" content="${published}">`);
  if (modified) tags.push(`<meta property="article:modified_time" content="${modified}">`);
  tags.push(
    '<meta name="twitter:card" content="summary_large_image">',
    `<meta name="twitter:title" content="${escapeHtml(title)}">`,
    `<meta name="twitter:description" content="${escapeHtml(description)}">`,
    `<meta name="twitter:image" content="${escapeHtml(picture)}">`
  );

  const yandex = verificationCode(settings.yandex_verification);
  if (yandex) tags.push(`<meta name="yandex-verification" content="${yandex}">`);
  const google = verificationCode(settings.google_verification);
  if (google) tags.push(`<meta name="google-site-verification" content="${google}">`);

  // Yandex shows /favicon.ico in its results, Safari wants a raster home-screen
  // icon; the SVG stays for the browsers that take it.
  tags.push(
    '<link rel="icon" href="/favicon.ico" sizes="16x16 32x32 48x48">',
    '<link rel="icon" href="/img/astvard-mark.svg" type="image/svg+xml">',
    '<link rel="apple-touch-icon" href="/img/astvard-icon-180.png">',
    '<link rel="manifest" href="/site.webmanifest">',
    '<meta name="theme-color" content="#030307">',
    `<script type="application/ld+json">${jsonForScript(jsonLd)}</script>`
  );
  return tags.join('\n  ');
}

function siteGraph(settings) {
  const url = siteUrl();
  return [
    {
      '@type': 'Organization',
      '@id': `${url}/#organization`,
      name: text(settings, 'site_name'),
      url: `${url}/`,
      logo: { '@type': 'ImageObject', url: `${url}/img/astvard-icon-512.png`, width: 512, height: 512 }
    },
    {
      '@type': 'WebSite',
      '@id': `${url}/#website`,
      url: `${url}/`,
      name: text(settings, 'site_name'),
      description: text(settings, 'seo_description'),
      inLanguage: 'ru-RU',
      publisher: { '@id': `${url}/#organization` }
    }
  ];
}

function articleCards(articles) {
  if (!articles.length) {
    return '<p class="server-meta">Новостей пока нет.</p>';
  }
  // The title is the one real link; its ::after stretches over the card, so the
  // whole card still opens the article and a crawler sees one link per article,
  // not two.
  return articles.map((a) => `
          <article class="article-card">
            <time class="article-date" datetime="${iso(a.created_at)}">${escapeHtml(dateRu(a.created_at))}</time>
            <h3 class="article-title"><a class="card-link" href="${escapeHtml(articlePath(a))}">${escapeHtml(a.title)}</a></h3>
            <p class="article-content">${escapeHtml(clip(plainText(a.content), 240) || 'Без текста')}</p>
            <span class="read-more" aria-hidden="true">Читать далее <i data-lucide="arrow-right" style="width: 16px; height: 16px;"></i></span>
          </article>`).join('');
}

/**
 * Страницы сайта: адрес, кусок разметки в templates/pages, место в меню и то, что
 * читает поисковик. Раньше всё это жило одной страницей с якорями, и каждая новая
 * тема делала её длиннее: у раздела не было ни своего адреса, ни своего заголовка в
 * выдаче, а ссылку на него нельзя было дать иначе как «промотай вниз».
 *
 * Заголовок и описание главной задаёт админ в настройках — её читают в выдаче первой;
 * у остальных они написаны здесь, потому что зависят от того, что на странице, а не от
 * вкуса.
 */
const PAGES = [
  {
    key: 'home', path: '/', file: 'home.html', nav: 'Главная', icon: 'home',
    scripts: ['/astvard-home.js'],
    title: (s) => text(s, 'seo_title'),
    description: (s) => text(s, 'seo_description'),
    changefreq: 'daily', priority: '1.0'
  },
  {
    key: 'server', path: '/server', file: 'server.html', nav: 'Наш сервер', icon: 'hammer',
    scripts: ['/astvard-home.js'],
    title: (s) => `Наш сервер Valheim: руны и общие постройки — ${text(s, 'site_name')}`,
    description: () => 'Что сейчас на сервере Valheim: руны за время в игре, часы игроков и постройки, '
      + 'которые выложены общими для всех.',
    changefreq: 'daily', priority: '0.8'
  },
  {
    key: 'mod', path: '/mod', file: 'mod.html', nav: 'Что умеет мод', icon: 'wand-sparkles',
    scripts: ['/mod-features.js'],
    title: (s) => `Мод для Valheim: что он умеет — ${text(s, 'site_name')}`,
    description: () => 'Панель управления прямо в игре: постройки по шаблонам, работа с рельефом, '
      + 'сортировка сундуков, автоматика и руны. Что делает каждая возможность, где её искать и во что обойдётся.',
    changefreq: 'weekly', priority: '0.9'
  },
  {
    key: 'join', path: '/join', file: 'join.html', nav: 'Как попасть', icon: 'log-in',
    title: (s) => `Как попасть на сервер Valheim — ${text(s, 'site_name')}`,
    description: () => 'Три шага: войти через Steam, попросить доступ и добавить сервер в игре. '
      + 'Мод ставить необязательно — на сервер пускают и без него.',
    changefreq: 'monthly', priority: '0.9'
  },
  {
    key: 'news', path: '/news', file: 'news.html', nav: 'Новости', icon: 'newspaper',
    title: (s) => `Новости сервера — ${text(s, 'site_name')}`,
    description: () => 'Что меняется на серверах Valheim и вокруг игры: обновления мода, правила, события.',
    changefreq: 'weekly', priority: '0.7'
  },
  {
    key: 'about', path: '/about', file: 'about.html', nav: 'О проекте', icon: 'info',
    // Название сайта админ часто уже вписал в заголовок раздела: «Об Astvard — Astvard»
    // читается как заикание.
    title: (s) => {
      const own = text(s, 'about_title');
      const site = text(s, 'site_name');
      return own.includes(site) ? `${own} — сервер Valheim` : `${own} — ${site}`;
    },
    description: (s) => clip(plainText(text(s, 'about_subtitle')), 160) || text(s, 'seo_description'),
    changefreq: 'monthly', priority: '0.5'
  }
];

const pageByPath = new Map(PAGES.map((page) => [page.path, page]));

// Меню строит сервер, а не скрипт: открытая страница должна быть подсвечена и у того,
// кто скриптов не исполняет, а ссылка — вести на адрес, а не на якорь.
function navHtml(activeKey) {
  const items = PAGES.map((page) => {
    const here = page.key === activeKey;
    return `          <li>
            <a href="${page.path}" class="sidebar__link${here ? ' active' : ''}"${here ? ' aria-current="page"' : ''}>
              <span class="sidebar__link-icon"><i data-lucide="${page.icon}"></i></span>
              <span>${escapeHtml(page.nav)}</span>
            </a>
          </li>`;
  }).join('\n');

  return `        <ul class="sidebar__menu">\n${items}\n        </ul>`;
}

async function renderPage(key) {
  const page = PAGES.find((p) => p.key === key);
  if (!page) throw new Error(`[seo] нет такой страницы: ${key}`);

  const [settings, layout, body] = await Promise.all([
    loadSettings(),
    fs.readFile(path.join(TEMPLATES_DIR, 'layout.html'), 'utf8'),
    fs.readFile(path.join(TEMPLATES_DIR, 'pages', page.file), 'utf8')
  ]);

  let articles = [];
  if (key === 'home' || key === 'news') {
    try {
      articles = await publishedArticles();
    } catch (err) {
      logger.error('[seo] статьи не прочитались:', err.message);
    }
  }

  const url = siteUrl();
  const title = page.title(settings);
  const description = page.description(settings);
  const head = headTags(settings, {
    title,
    description,
    pagePath: page.path,
    jsonLd: {
      '@context': 'https://schema.org',
      '@graph': [
        ...siteGraph(settings),
        {
          '@type': 'WebPage',
          '@id': `${url}${page.path}#webpage`,
          url: `${url}${page.path}`,
          name: title,
          description,
          inLanguage: 'ru-RU',
          isPartOf: { '@id': `${url}/#website` }
        },
        // У главной хлебных крошек нет: она и есть корень.
        ...(page.key === 'home' ? [] : [{
          '@type': 'BreadcrumbList',
          itemListElement: [
            { '@type': 'ListItem', position: 1, name: 'Главная', item: `${url}/` },
            { '@type': 'ListItem', position: 2, name: page.nav, item: `${url}${page.path}` }
          ]
        }])
      ]
    }
  });

  const values = {
    head,
    nav: navHtml(page.key),
    download_button: downloadButton(settings),
    download_modal: downloadModal(settings),
    scripts: (page.scripts || []).map((src) => `  <script src="${src}"></script>`).join('\n'),
    year: new Date().getFullYear(),
    articles: articleCards(articles),
    // На главной — только три свежих, остальное на своей странице.
    latest_articles: articleCards(articles.slice(0, 3))
  };
  for (const settingKey of [
    'site_name', 'hero_title', 'site_description', 'about_title', 'about_subtitle',
    'about_card1_title', 'about_card1_text', 'about_card2_title', 'about_card2_text',
    'contact_title', 'contact_subtitle', 'contact_email', 'contact_address'
  ]) {
    values[settingKey] = text(settings, settingKey);
  }
  values.community_links = communityLinks(settings);
  values.mod_download = modDownload(settings);

  // The second step says what really happens to a request today, not in general.
  values.join_access = settings.whitelist_auto_approve === 'true'
    ? 'Сейчас заявки принимаются сразу: через десяток секунд после нажатия можно заходить.'
    : 'Заявку смотрит админ, ответ появится там же, в кабинете.';

  // Кусок заполняется первым: подставленное значение второй раз не просматривается,
  // иначе текст статьи с двойными скобками внутри стал бы полем шаблона.
  return { html: fill(layout, { ...values, content: fill(body, values) }), settings };
}

// The first picture of the article becomes its preview, if it is one a scraper can
// fetch; an inline data: image is not.
function firstImage(html) {
  const match = String(html || '').match(/<img[^>]+src="([^"]+)"/i);
  if (!match) return null;
  const src = match[1].replace(/&amp;/g, '&');
  if (/^https?:\/\//i.test(src)) return src;
  if (src.startsWith('/')) return `${siteUrl()}${src}`;
  return null;
}

async function renderArticle(article) {
  const [settings, template] = await Promise.all([
    loadSettings(),
    fs.readFile(path.join(TEMPLATES_DIR, 'article.html'), 'utf8')
  ]);

  const url = siteUrl();
  const pagePath = articlePath(article);
  const body = plainText(article.content);
  const description = clip(body, 160) || text(settings, 'seo_description');
  const published = iso(article.created_at);
  const modified = iso(article.updated_at || article.created_at);
  const image = firstImage(article.content);
  const title = `${article.title} — ${text(settings, 'site_name')}`;

  const head = headTags(settings, {
    title,
    description,
    pagePath,
    type: 'article',
    image,
    published,
    modified,
    jsonLd: {
      '@context': 'https://schema.org',
      '@graph': [
        ...siteGraph(settings),
        {
          '@type': 'BlogPosting',
          '@id': `${url}${pagePath}#article`,
          headline: clip(article.title, 110),
          description,
          datePublished: published,
          dateModified: modified,
          inLanguage: 'ru-RU',
          image: [image || `${url}/img/astvard-og.png`],
          author: { '@id': `${url}/#organization` },
          publisher: { '@id': `${url}/#organization` },
          mainEntityOfPage: `${url}${pagePath}`
        },
        {
          '@type': 'BreadcrumbList',
          itemListElement: [
            { '@type': 'ListItem', position: 1, name: 'Главная', item: `${url}/` },
            { '@type': 'ListItem', position: 2, name: article.title, item: `${url}${pagePath}` }
          ]
        }
      ]
    }
  });

  return {
    html: fill(template, {
      head,
      site_name: text(settings, 'site_name'),
      community_links: communityLinks(settings),
      title: article.title,
      published_iso: published,
      published_ru: dateRu(article.created_at),
      // Article HTML is cleaned by sanitize-html when it is saved (src/routes/articles.js)
      // and is written by admins; it goes in as markup, the way the modal showed it.
      body: article.content || '',
      year: new Date().getFullYear()
    }),
    settings
  };
}

function robotsTxt() {
  return [
    'User-agent: *',
    'Disallow: /admin',
    'Disallow: /api/',
    // The home page fills its game blocks from these; a crawler that renders
    // scripts must be allowed to fetch them.
    'Allow: /api/public/',
    // Yandex: the same page with an ad campaign's tags is still the same page.
    'Clean-param: utm_source&utm_medium&utm_campaign&utm_content&utm_term&yclid&gclid&fbclid&vkclid /',
    '',
    `Sitemap: ${siteUrl()}/sitemap.xml`,
    ''
  ].join('\n');
}

async function sitemapXml() {
  const url = siteUrl();
  const articles = await publishedArticles();
  const stamp = (a) => new Date(a.updated_at || a.created_at).getTime();
  const newest = articles.reduce((max, a) => Math.max(max, stamp(a)), 0);

  const entries = [
    // У главной и «Новостей» дата последней записи: они меняются вместе с лентой.
    ...PAGES.map((page) => ({
      loc: `${url}${page.path}`,
      lastmod: (page.key === 'home' || page.key === 'news') && newest ? iso(newest) : null,
      changefreq: page.changefreq,
      priority: page.priority
    })),
    ...articles.map((a) => ({ loc: `${url}${articlePath(a)}`, lastmod: iso(stamp(a)), changefreq: 'monthly', priority: '0.7' }))
  ];

  const xml = (value) => String(value).replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;'
  }[ch]));

  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">',
    ...entries.map((e) => [
      '  <url>',
      `    <loc>${xml(e.loc)}</loc>`,
      e.lastmod ? `    <lastmod>${e.lastmod}</lastmod>` : null,
      `    <changefreq>${e.changefreq}</changefreq>`,
      `    <priority>${e.priority}</priority>`,
      '  </url>'
    ].filter(Boolean).join('\n')),
    '</urlset>',
    ''
  ].join('\n');
}

// Pages that are someone's account or the admin's desk. They answer with
// "X-Robots-Tag: noindex" rather than being closed in robots.txt: a crawler kept
// out by robots.txt never sees the noindex, and a URL it only knows from a link
// can still end up in the results as a bare address.
const PRIVATE_PAGES = new Set(['/cabinet.html', '/login.html', '/register.html', '/tool.html', '/vehicle.html']);

function isPrivatePath(pathname) {
  return pathname.startsWith('/admin') || pathname.startsWith('/api/') || PRIVATE_PAGES.has(pathname);
}

module.exports = {
  siteUrl,
  loadSettings,
  renderPage,
  PAGES,
  pageByPath,
  renderArticle,
  publishedArticle,
  articlePath,
  NEWS_PATH_RE,
  robotsTxt,
  sitemapXml,
  analyticsTags,
  metrikaId,
  verificationCode,
  isPrivatePath,
  // for tests
  slugify,
  plainText,
  fill
};
