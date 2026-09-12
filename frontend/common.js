// Anything that came back from the server is text, not markup. Escaping at the
// point of interpolation is the only place this can be got right once: the email
// regex, for instance, happily accepts <img/src=x/onerror=...>@e.co.
function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));
}

async function apiFetch(url, options = {}) {
  const res = await fetch(url, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    credentials: 'same-origin',
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(data.error || `Ошибка ${res.status}`);
  }
  return data;
}

async function getCurrentUser() {
  try {
    const { user } = await apiFetch('/api/me');
    return user;
  } catch {
    return null;
  }
}

// Whether anyone is signed in is something only the server knows, so the header has
// to ask instead of being written into the page: a hard-coded "Вход / Регистрация" on
// the home page told a signed-in player they had been logged out.
function renderNav(el, user) {
  if (!el) return;
  const links = [];
  if (user) {
    if (window.location.pathname !== '/cabinet.html') {
      links.push('<a href="/cabinet.html">Кабинет</a>');
    }
    if (user.role === 'admin' || user.role === 'superadmin') {
      links.push('<a href="/admin.html">Админка</a>');
    }
    links.push('<button type="button" class="link-button" id="logout">Выйти</button>');
  } else {
    links.push(
      '<a href="/login.html">Вход</a>',
      '<a class="btn btn-sm btn-primary" href="/register.html">Регистрация</a>'
    );
  }
  el.innerHTML = links.join(' · ');

  const logout = document.getElementById('logout');
  if (logout) {
    logout.addEventListener('click', async () => {
      await apiFetch('/api/logout', { method: 'POST' });
      window.location.href = '/';
    });
  }
}

// The address is what a player types into "Подключиться по IP", so it is the game
// port that is shown, whatever the status is read from.
function renderServers(el, servers) {
  if (!el) return;
  if (!servers || servers.length === 0) {
    el.textContent = 'Пока нет серверов.';
    return;
  }
  el.innerHTML = servers.map((s) => `
    <div class="card server-row">
      <strong>${escapeHtml(s.name)}</strong> — ${escapeHtml(s.host)}:${escapeHtml(s.port)}
      <span class="status ${s.is_online ? 'online' : 'offline'}">
        ${s.is_online ? `● online (${s.players}/${s.max_players})` : '○ offline'}
      </span>
    </div>
  `).join('');
}

// The Steam return lands as a browser redirect, so its failures arrive in the URL
// rather than in a response body. Printed with textContent: the wording is ours,
// but it has been through the address bar and anyone can retype it.
function showSteamError(elementId) {
  const message = new URLSearchParams(window.location.search).get('steam_error');
  if (!message) return;
  const el = document.getElementById(elementId);
  if (el) el.textContent = message;
}
