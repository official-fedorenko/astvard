// The texts, the year and the list of articles come in the HTML from the server
// (src/seo.js). What is left here depends on who is looking.
document.addEventListener('DOMContentLoaded', async () => {
  lucide.createIcons();
  await checkAuth(); // Проверяем сессию и обновляем навбар
});

// Проверка сессии для динамического навбара
async function checkAuth() {
  try {
    const res = await fetch('/api/auth/me');
    if (res.ok) {
      const data = await res.json();
      const user = data.user;
      // Скрываем гостевые ссылки, показываем авторизованные
      document.getElementById('guestNav').style.display = 'none';
      const authNav = document.getElementById('authNav');
      authNav.style.display = 'flex';
      
      // Удаляем старую кнопку админ-панели, если она была добавлена
      const oldAdminLink = document.getElementById('navAdminPanel');
      if (oldAdminLink) oldAdminLink.remove();

      // Ссылка на кабинет для всех
      const cabinetLink = document.getElementById('cabinetLink');
      const navUsername = document.getElementById('navUsername');
      cabinetLink.href = '/cabinet.html';
      navUsername.textContent = user.username;

      // Скрываем блоки регистрации для авторизованного пользователя
      const sidebarRegBlock = document.getElementById('sidebarRegisterBlock');
      if (sidebarRegBlock) sidebarRegBlock.style.display = 'none';

      const heroRegBtn = document.getElementById('heroRegisterBtn');
      if (heroRegBtn) {
        heroRegBtn.textContent = 'Личный кабинет';
        heroRegBtn.href = '/cabinet.html';
      }

      // Если админ или суперадмин — добавляем кнопку админ-панели
      if (user.role === 'Admin' || user.role === 'Superadmin') {
        const adminLink = document.createElement('a');
        adminLink.href = '/admin/';
        adminLink.id = 'navAdminPanel';
        adminLink.className = 'admin-link';
        adminLink.style.background = 'linear-gradient(135deg, var(--accent-purple), var(--accent-cyan))';
        adminLink.style.color = '#fff';
        adminLink.style.display = 'inline-flex';
        adminLink.style.alignItems = 'center';
        adminLink.style.gap = '6px';
        adminLink.style.marginRight = '8px';
        adminLink.innerHTML = '<i data-lucide="settings" style="width:16px;height:16px;"></i> Панель';
        authNav.insertBefore(adminLink, cabinetLink);
      }
      // Если авторизован, обновляем плашку поддержки
      const promptText = document.getElementById('supportPromptText');
      if (promptText) {
        promptText.textContent = 'Вы авторизованы. Перейдите в личный кабинет, чтобы общаться с техподдержкой в реальном времени.';
      }
      const supportActionButtons = document.getElementById('supportActionButtons');
      if (supportActionButtons) {
        supportActionButtons.innerHTML = `
          <a href="/cabinet.html" class="btn btn--primary" style="text-decoration: none; padding: 12px 24px;">В личный кабинет</a>
        `;
      }
      // Мобильное меню профиля (тап по нику в шапке)
      setupProfileMenu(user);

      lucide.createIcons();
    }
    // Если не авторизован — оставляем гостевые ссылки
  } catch(e) { /* сетевая ошибка — оставляем гостевые ссылки */ }
}

// Настройка мобильного меню профиля в шапке: на узких экранах тап по нику
// открывает модалку с действиями (зависят от прав), а не переходит в кабинет.
function setupProfileMenu(user) {
  const overlay = document.getElementById('profileMenuOverlay');
  const trigger = document.getElementById('cabinetLink');
  if (!overlay || !trigger) return;

  const isAdmin = user.role === 'Admin' || user.role === 'Superadmin';
  const letter = (user.username || '?').trim().charAt(0).toUpperCase() || '?';

  const avatar = document.getElementById('pmAvatar');
  if (user.avatar_url) {
    avatar.textContent = '';
    avatar.style.background = `center/cover no-repeat url("${user.avatar_url}")`;
  } else {
    avatar.textContent = letter;
  }
  document.getElementById('pmName').textContent = user.username;
  document.getElementById('pmRole').textContent =
    user.role === 'Superadmin' ? 'Суперадмин' : (user.role === 'Admin' ? 'Администратор' : 'Пользователь');
  document.getElementById('pmAdmin').style.display = isAdmin ? 'flex' : 'none';

  const openMenu = () => overlay.classList.add('open');
  const closeMenu = () => overlay.classList.remove('open');

  trigger.addEventListener('click', (e) => {
    // Только на мобильных перехватываем переход и открываем меню.
    if (window.matchMedia('(max-width: 768px)').matches) {
      e.preventDefault();
      openMenu();
    }
  });
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeMenu(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });

  const logout = document.getElementById('pmLogout');
  if (logout) logout.addEventListener('click', async () => {
    try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (_) {}
    window.location.href = '/';
  });

  if (typeof lucide !== 'undefined') lucide.createIcons();
}
