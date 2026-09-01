const status = document.getElementById('status');
const logoutBtn = document.getElementById('logout-btn');
const content = document.getElementById('content');

async function loadMe() {
  const response = await fetch('/api/me');

  if (!response.ok) {
    // не залогинен — отправляем на страницу входа
    window.location.href = 'login';
    return;
  }

  const data = await response.json();
  const roleLabels = { player: 'игрок', admin: 'админ', superadmin: 'супер-админ' };
  const emailPart = data.email ? ` (${data.email})` : '';
  status.textContent = `Привет, ${data.nickname}!${emailPart} — ${roleLabels[data.role] ?? data.role}`;
  logoutBtn.style.display = 'inline-block';
  content.style.display = 'block';

  if (data.role === 'admin' || data.role === 'superadmin') {
    document.getElementById('admin-link').style.display = 'inline-flex';
  }

  loadServers();
  loadArticles();
}

logoutBtn.addEventListener('click', async () => {
  await fetch('/api/logout', { method: 'POST' });
  window.location.href = 'login';
});

loadMe();
