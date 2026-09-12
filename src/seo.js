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

async function renderHome() {
  const [settings, template] = await Promise.all([
    loadSettings(),
    fs.readFile(path.join(CLIENT_DIR, 'index.html'), 'utf8')
  ]);
  let articles = [];
  try {
    articles = await publishedArticles();
  } catch (err) {
    logger.error('[seo] статьи для главной не прочитались:', err.message);
  }

  const url = siteUrl();
  const title = text(settings, 'seo_title');
  const description = text(settings, 'seo_description');
  const head = headTags(settings, {
    title,
    description,
    pagePath: '/',
    jsonLd: {
      '@context': 'https://schema.org',
      '@graph': [
        ...siteGraph(settings),
        {
          '@type': 'WebPage',
          '@id': `${url}/#webpage`,
          url: `${url}/`,
          name: title,
          description,
          inLanguage: 'ru-RU',
          isPartOf: { '@id': `${url}/#website` }
        }
      ]
    }
  });

  const values = { head, articles: articleCards(articles), year: new Date().getFullYear() };
  for (const key of [
    'site_name', 'hero_title', 'site_description', 'about_title', 'about_subtitle',
    'about_card1_title', 'about_card1_text', 'about_card2_title', 'about_card2_text',
    'contact_title', 'contact_subtitle', 'contact_email', 'contact_address'
  ]) {
    values[key] = text(settings, key);
  }
  // The second step says what really happens to a request today, not in general.
  values.join_access = settings.whitelist_auto_approve === 'true'
    ? 'Сейчас заявки принимаются сразу: через десяток секунд после нажатия можно заходить.'
    : 'Заявку смотрит админ, ответ появится там же, в кабинете.';

  return { html: fill(template, values), settings };
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
    { loc: `${url}/`, lastmod: newest ? iso(newest) : null, changefreq: 'daily', priority: '1.0' },
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
  renderHome,
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
