// Серверы на главной. Список открыт всем: решать, возвращаться ли в игру, человек
// должен без аккаунта. Адрес показывается тот, который вводят в «Подключиться по IP»,
// а не тот, по которому сайт спрашивает состояние.
//
// Карточка собрана классами страницы (.article-card, .article-date, .article-title,
// .article-content) — теми же, что у статей в этой же сетке; своё здесь только то,
// чего у статей нет: состояние сервера и список тех, кто сейчас в игре.
(async function loadPublicServers() {
  const grid = document.getElementById('serversGrid');
  if (!grid) return;

  const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[ch]));

  // Имена приходят от сервера уже отобранными: только те, кто есть у нас на сайте.
  // Игрок без аккаунта остаётся числом в счётчике — показывать чужой ник, которого
  // человек нам не давал, мы не вправе.
  const playersLine = (s) => {
    if (!s.is_online) return '';
    if (s.players_online && s.players_online.length) {
      const named = s.players_online.map(escape).join(', ');
      const rest = (s.players ?? s.players_online.length) - s.players_online.length;
      return `<p class="server-players">В игре: ${named}${rest > 0 ? ` и ещё ${rest}` : ''}</p>`;
    }
    // Ноль игроков — это тоже ответ, и лучше сказать его словами.
    return s.players === 0 ? '<p class="server-players">Сейчас никого — заходи первым</p>' : '';
  };

  const stateLine = (s) => {
    if (!s.is_online) return '<span class="article-date server-state server-state--off">○ офлайн</span>';
    const count = s.players === null
      ? 'считает игроков'
      : `${s.players} из ${s.max_players} в игре`;
    return `<span class="article-date server-state server-state--on">● онлайн — ${escape(count)}</span>`;
  };

  // Номер сохранения и версия игры приходят из лога сервера: по первому видно, что
  // мир живёт и пишется, по второй — какую версию клиента ставить.
  const metaLine = (s) => {
    const parts = [];
    if (s.save_number) parts.push(`сохранение № ${escape(s.save_number)}`);
    if (s.game_version) parts.push(`Valheim ${escape(s.game_version)}`);
    return parts.length ? `<span class="server-meta">${parts.join(' · ')}</span>` : '';
  };

  try {
    const res = await fetch('/api/public/servers');
    if (!res.ok) throw new Error(String(res.status));
    const { servers = [] } = await res.json();

    if (servers.length === 0) {
      grid.innerHTML = '<p class="server-meta">Серверов пока нет.</p>';
      return;
    }

    grid.innerHTML = servers.map((s) => `
      <article class="article-card">
        ${stateLine(s)}
        <h3 class="article-title">${escape(s.name)}</h3>
        <div class="article-content">
          <code class="wrap-any">${escape(s.host)}:${escape(s.port)}</code>
          ${playersLine(s)}
        </div>
        ${metaLine(s)}
      </article>`).join('');
  } catch (err) {
    // Состояние сервера — не то, ради чего стоит рушить главную страницу.
    grid.innerHTML = '<p class="server-meta">Состояние серверов сейчас недоступно.</p>';
  }
})();

// === Наш сервер: руны и общие постройки ===
//
// Всё это мод уже записал на диск рядом с игрой, сайт только читает: руны — в
// своём файле, постройки — по файлу на шаблон. Ни номеров Steam, ни путей сюда
// не приходит: сервер отдаёт ники и числа.
(async function loadServerLife() {
  const runesBox = document.getElementById('runesBoard');
  const buildsBox = document.getElementById('buildsGrid');
  if (!runesBox || !buildsBox) return;

  const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[ch]));

  // «1 руна, 2 руны, 5 рун» — иначе подпись читается как машинная.
  const plural = (n, one, few, many) => {
    const mod10 = n % 10;
    const mod100 = n % 100;
    if (mod10 === 1 && mod100 !== 11) return one;
    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
    return many;
  };

  const hoursLabel = (hours) => {
    if (hours < 1) return `${Math.round(hours * 60)} мин в мире`;
    const rounded = Math.round(hours * 10) / 10;
    // У дробного числа в русском всегда родительный единственного: «14,3 часа»,
    // а не «14,3 часов». Целое склоняется как обычно.
    const word = Number.isInteger(rounded) ? plural(rounded, 'час', 'часа', 'часов') : 'часа';
    return `${rounded.toLocaleString('ru-RU')} ${word} в мире`;
  };

  try {
    const res = await fetch('/api/public/game');
    if (!res.ok) throw new Error(String(res.status));
    const { runes = {}, builds = [] } = await res.json();

    const players = runes.players || [];
    runesBox.innerHTML = players.length
      ? `<ol class="rune-board">${players.map((p, i) => `
          <li class="rune-row">
            <span class="rune-place">${i + 1}</span>
            <span class="rune-name">${escape(p.name)}</span>
            <span class="rune-count">${escape(p.runes)} ${plural(p.runes, 'руна', 'руны', 'рун')}</span>
            <span class="rune-hours">${escape(hoursLabel(p.hours))}</span>
          </li>`).join('')}</ol>`
      : '<p class="server-meta">Пока ни одной руны: их начисляют за время в мире.</p>';

    const rate = runes.minutes_per_rune;
    const hint = document.getElementById('runesHint');
    if (hint && rate) {
      hint.textContent = `Руна начисляется за каждые ${rate} ${plural(rate, 'минуту', 'минуты', 'минут')} `
        + 'в мире. Тратить их пока не на что — это счёт часов.';
    }

    buildsBox.innerHTML = builds.length
      ? builds.map((b) => `
        <article class="article-card build-card">
          <span class="article-date">${escape(b.category)}</span>
          <h3 class="article-title">${escape(b.name)}</h3>
          <div class="article-content">
            ${b.author ? `Построил ${escape(b.author)}` : 'Автор неизвестен'}
          </div>
          <span class="server-meta">
            ${escape(b.pieces)} ${plural(b.pieces, 'деталь', 'детали', 'деталей')}
            ${b.for_players
              ? '<span class="build-mark build-open">открыта игрокам</span>'
              : '<span class="build-mark build-admin">ставит админ</span>'}
          </span>
        </article>`).join('')
      : '<p class="server-meta">Общих построек пока нет.</p>';
  } catch (err) {
    // Страница живёт и без этого блока: сервер мог быть остановлен, файлов может
    // ещё не быть.
    runesBox.innerHTML = '<p class="server-meta">Руны сейчас недоступны.</p>';
    buildsBox.innerHTML = '<p class="server-meta">Список построек сейчас недоступен.</p>';
  }
})();
