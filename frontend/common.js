function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str ?? '';
  return div.innerHTML;
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
