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

// The Steam return lands as a browser redirect, so its failures arrive in the URL
// rather than in a response body. Printed with textContent: the wording is ours,
// but it has been through the address bar and anyone can retype it.
function showSteamError(elementId) {
  const message = new URLSearchParams(window.location.search).get('steam_error');
  if (!message) return;
  const el = document.getElementById(elementId);
  if (el) el.textContent = message;
}
