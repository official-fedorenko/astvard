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
