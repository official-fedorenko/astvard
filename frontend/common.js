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
