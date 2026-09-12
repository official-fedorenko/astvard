// Серверы на главной. Список открыт всем: решать, возвращаться ли в игру, человек
// должен без аккаунта. Адрес показывается тот, который вводят в «Подключиться по IP»,
// а не тот, по которому сайт спрашивает состояние.
(async function loadPublicServers() {
  const grid = document.getElementById('serversGrid');
  if (!grid) return;

  const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[ch]));

  try {
    const res = await fetch('/api/public/servers');
    if (!res.ok) throw new Error(String(res.status));
    const { servers = [] } = await res.json();

    if (servers.length === 0) {
      grid.innerHTML = '<p style="color: var(--text-muted, #a0a0ab);">Серверов пока нет.</p>';
      return;
    }

    grid.innerHTML = servers.map((s) => `
      <article class="article-card">
        <div class="article-card__body">
          <h3 class="article-card__title">${escape(s.name)}</h3>
          <p class="article-card__excerpt">
            <code>${escape(s.host)}:${escape(s.port)}</code>
          </p>
          <p class="article-card__meta" style="color: ${s.is_online ? '#4ade80' : 'var(--text-muted, #a0a0ab)'};">
            ${s.is_online
              ? `● online — игроков ${escape(s.players)}/${escape(s.max_players)}`
              : '○ offline'}
          </p>
        </div>
      </article>`).join('');
  } catch (err) {
    // Состояние сервера — не то, ради чего стоит рушить главную страницу.
    grid.innerHTML = '<p style="color: var(--text-muted, #a0a0ab);">Состояние серверов сейчас недоступно.</p>';
  }
})();
