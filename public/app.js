// Configuration & State
let currentUser = null;
let articles = [];
let quill = null;
let usersList = [];
let fullLogsList = [];
let mediaFilesCache = [];

// DOM Elements
const sections = document.querySelectorAll('.app-section');
const navItems = document.querySelectorAll('.nav-list .nav-item');
const toastContainer = document.getElementById('toastContainer');

// Initialization
document.addEventListener('DOMContentLoaded', async () => {
  await checkSession();
  setupNavigation();
  initApp();

  // Create icons
  try {
    if (typeof lucide !== 'undefined') lucide.createIcons();
  } catch (e) {
    console.warn('Lucide icons failed to load:', e);
  }
});

// Toast notification helper
function showToast(message, type = 'success') {
  const toast = document.createElement('div');
  toast.className = `toast ${type}`;
  toast.innerHTML = `
    <i data-lucide="${type === 'success' ? 'check-circle' : 'alert-circle'}"></i>
    <span>${message}</span>
  `;
  toastContainer.appendChild(toast);
  lucide.createIcons({attrs: {'stroke-width': 2}});
  
  setTimeout(() => toast.classList.add('show'), 10);
  
  setTimeout(() => {
    toast.classList.remove('show');
    setTimeout(() => toast.remove(), 400);
  }, 3000);
}

// === Модальные диалоги вместо нативных alert/confirm/prompt ===
// Возвращают Promise: confirm → boolean, prompt → string|null, alert → true.
function uiDialog(opts) {
  const { type = 'confirm', title = '', message = '', defaultValue = '',
          okText, cancelText = 'Отмена', danger = false } = opts || {};
  const isPrompt = type === 'prompt';
  const isAlert = type === 'alert';
  const ok = okText || (isAlert ? 'OK' : 'Подтвердить');
  const esc = (s) => String(s == null ? '' : s)
    .replace(/[&<>"']/g, m => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[m]));
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'ui-dialog-overlay';
    overlay.innerHTML = `
      <div class="ui-dialog" role="dialog" aria-modal="true">
        ${title ? `<div class="ui-dialog__title">${esc(title)}</div>` : ''}
        <div class="ui-dialog__msg">${esc(message)}</div>
        ${isPrompt ? `<input class="ui-dialog__input form-control" type="text">` : ''}
        <div class="ui-dialog__actions">
          ${isAlert ? '' : `<button type="button" class="ui-dialog__btn ui-dialog__btn--cancel">${esc(cancelText)}</button>`}
          <button type="button" class="ui-dialog__btn ui-dialog__btn--ok${danger ? ' ui-dialog__btn--danger' : ''}">${esc(ok)}</button>
        </div>
      </div>`;
    document.body.appendChild(overlay);
    const input = overlay.querySelector('.ui-dialog__input');
    if (input) { input.value = defaultValue || ''; setTimeout(() => { input.focus(); input.select(); }, 30); }
    const done = (val) => { document.removeEventListener('keydown', onKey); overlay.classList.remove('open'); setTimeout(() => overlay.remove(), 150); resolve(val); };
    const onOk = () => done(isPrompt ? (input ? input.value : '') : true);
    const onCancel = () => done(isPrompt ? null : false);
    overlay.querySelector('.ui-dialog__btn--ok').addEventListener('click', onOk);
    const cancelBtn = overlay.querySelector('.ui-dialog__btn--cancel');
    if (cancelBtn) cancelBtn.addEventListener('click', onCancel);
    overlay.addEventListener('click', (e) => { if (e.target === overlay) onCancel(); });
    const onKey = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); onCancel(); }
      else if (e.key === 'Enter') { e.preventDefault(); onOk(); }
    };
    document.addEventListener('keydown', onKey);
    requestAnimationFrame(() => overlay.classList.add('open'));
  });
}
const confirmDialog = (message, opts = {}) => uiDialog({ type: 'confirm', message, ...opts });
const promptDialog = (message, defaultValue = '', opts = {}) => uiDialog({ type: 'prompt', message, defaultValue, ...opts });
const alertDialog = (message, opts = {}) => uiDialog({ type: 'alert', message, ...opts });

// Session Check
async function checkSession() {
  try {
    const res = await fetch('/api/auth/me');
    if (!res.ok) {
      window.location.href = '/admin/login.html';
      return;
    }
    const data = await res.json();
    currentUser = data.user;
    
    // Update sidebar profile
    document.getElementById('userDisplay').textContent = currentUser.username;
    document.getElementById('roleDisplay').textContent = currentUser.role === 'Superadmin' ? 'Суперадмин' : (currentUser.role === 'Admin' ? 'Администратор' : 'Редактор');
    document.getElementById('avatarLetter').textContent = currentUser.username.charAt(0).toUpperCase();

    // Show Superadmin-only sections
    if (currentUser.role === 'Superadmin') {
      document.querySelectorAll('.superadmin-only').forEach(el => {
        el.style.display = 'block';
      });
      const resetBox = document.getElementById('resetDemoBox');
      if (resetBox) resetBox.style.display = 'block';
      const backupBox = document.getElementById('backupBox');
      if (backupBox) backupBox.style.display = 'block';
    }
  } catch (err) {
    console.error('Session check error:', err);
    window.location.href = '/admin/login.html';
  }
}

// SPA Routing / Navigation
function setupNavigation() {
  // Бургер-меню (мобильные): выпадающий список пунктов по кнопке.
  const sidebar = document.querySelector('aside.sidebar');
  const burger = document.getElementById('navBurger');
  const closeMenu = () => {
    if (!sidebar) return;
    sidebar.classList.remove('menu-open');
    if (burger) burger.setAttribute('aria-expanded', 'false');
  };
  if (burger && sidebar) {
    burger.addEventListener('click', (e) => {
      e.stopPropagation();
      const open = sidebar.classList.toggle('menu-open');
      burger.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
    // Клик вне меню — закрыть.
    document.addEventListener('click', (e) => {
      if (sidebar.classList.contains('menu-open') && !sidebar.contains(e.target)) closeMenu();
    });
  }

  const handleHashChange = () => {
    const hash = window.location.hash.replace('#', '') || 'whitelist';
    let targetSection = document.getElementById(`section-${hash}`);

    if (!targetSection) {
      targetSection = document.getElementById('section-dashboard');
    }

    // Toggle active section
    sections.forEach(sec => sec.classList.remove('active'));
    targetSection.classList.add('active');

    // Toggle active sidebar item
    navItems.forEach(item => {
      item.classList.remove('active');
      if (item.getAttribute('data-section') === hash) {
        item.classList.add('active');
      }
    });

    // Load section data
    loadSectionData(hash);

    if (hash === 'articles') {
      loadDashboardStats();
    }

    // Выбор пункта закрывает бургер-меню.
    closeMenu();
  };

  window.addEventListener('hashchange', handleHashChange);
  
  // Set initial page state if hash is present
  if (window.location.hash) {
    handleHashChange();
  } else {
    window.location.hash = '#whitelist';
  }
}

// Global App Event Listeners & Startup
function initApp() {
  // Initialize Quill Editor
  // Quill грузится с внешнего CDN — если он недоступен (блокировка, сбой
  // сети, медленное соединение), новый Quill() бросает исключение. Без
  // try/catch это необработанное исключение остановило бы выполнение всей
  // initApp() и оставило бы без обработчиков всё, что настраивается ниже
  // (модалку пользователей, поиск, drag&drop загрузку и т.д.) — поэтому
  // ошибка отсюда не должна "ронять" остальную инициализацию.
  if (document.getElementById('quillEditor')) {
    try {
      if (typeof Quill === 'undefined') throw new Error('Quill script failed to load');
      quill = new Quill('#quillEditor', {
        theme: 'snow',
        modules: {
          toolbar: {
            container: [
              [{ 'header': [1, 2, 3, false] }],
              ['bold', 'italic', 'underline', 'strike'],
              ['blockquote', 'code-block'],
              [{ 'list': 'ordered'}, { 'list': 'bullet' }],
              ['link', 'image'],
              ['clean']
            ],
            handlers: {
              image: async function() {
                if (window.openMediaPicker) {
                  window.openMediaPicker((url) => {
                    const range = this.quill.getSelection() || { index: this.quill.getLength() };
                    this.quill.insertEmbed(range.index, 'image', url);
                  }, 'articles');
                } else {
                  const url = await promptDialog('Введите URL изображения:');
                  if (url) {
                    const range = this.quill.getSelection() || { index: this.quill.getLength() };
                    this.quill.insertEmbed(range.index, 'image', url);
                  }
                }
              }
            }
          }
        }
      });
    } catch (e) {
      console.warn('Не удалось инициализировать редактор Quill, используется обычное текстовое поле:', e);
      quill = null;

      // Делаем скрытое поле контента видимым textarea, чтобы статьи
      // всё равно можно было редактировать без rich-текстового редактора.
      const editorDiv = document.getElementById('quillEditor');
      const hiddenInput = document.getElementById('articleContent');
      if (editorDiv && hiddenInput) {
        const textarea = document.createElement('textarea');
        textarea.id = 'articleContent';
        textarea.className = 'form-control';
        textarea.rows = 8;
        textarea.value = hiddenInput.value || '';
        editorDiv.replaceWith(textarea);
        hiddenInput.remove();
      }
    }
  }

  // Logout Button
  document.getElementById('logoutBtn').addEventListener('click', async () => {
    try {
      const res = await fetch('/api/auth/logout', { method: 'POST' });
      if (res.ok) {
        showToast('Вы вышли из системы', 'success');
        setTimeout(() => window.location.href = '/admin/login.html', 500);
      }
    } catch (err) {
      showToast('Ошибка при выходе', 'error');
    }
  });

  // Articles Search
  const crudSearch = document.getElementById('crudSearch');
  if (crudSearch) {
    crudSearch.addEventListener('input', (e) => {
      const query = e.target.value.toLowerCase();
      renderArticles(query);
    });
  }

  // Загружаем статистику для "дашборда" на главной странице статей
  loadDashboardStats();

  // Счётчик непрочитанных сообщений поддержки (бейдж в меню) + лёгкий опрос,
  // чтобы цифра появлялась даже когда админ не в разделе «Обратная связь».
  updateSupportBadge();
  setInterval(updateSupportBadge, 15000);

  // Modal Setup
  const modal = document.getElementById('articleModalOverlay');
  const addBtn = document.getElementById('addArticleBtn');
  const cancelBtn = document.getElementById('cancelModalBtn');
  const closeBtn = document.getElementById('closeModalBtn');
  const articleForm = document.getElementById('articleForm');

  const openModal = (title = 'Добавить статью', id = '', artTitle = '', artContent = '', artStatus = 'draft') => {
    document.getElementById('modalTitle').textContent = title;
    document.getElementById('articleId').value = id;
    document.getElementById('articleTitle').value = artTitle;
    if (quill) {
      quill.root.innerHTML = artContent || '';
    } else {
      document.getElementById('articleContent').value = artContent;
    }
    document.getElementById('articleStatus').value = artStatus;
    modal.classList.add('active');
  };

  const closeModal = () => {
    modal.classList.remove('active');
    articleForm.reset();
  };

  addBtn.addEventListener('click', () => openModal());
  cancelBtn.addEventListener('click', closeModal);
  closeBtn.addEventListener('click', closeModal);
  modal.addEventListener('click', (e) => { if (e.target === modal) closeModal(); });

  // CRUD Save handler
  articleForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = document.getElementById('articleId').value;
    const title = document.getElementById('articleTitle').value;
    const content = quill ? quill.root.innerHTML : document.getElementById('articleContent').value;
    const status = document.getElementById('articleStatus').value;

    const payload = { title, content, status };
    const method = id ? 'PUT' : 'POST';
    const url = id ? `/api/crud/articles?id=${id}` : '/api/crud/articles';

    try {
      const res = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (res.ok) {
        showToast(id ? 'Статья успешно обновлена' : 'Статья создана', 'success');
        closeModal();
        await loadArticles();
      } else {
        const errData = await res.json();
        showToast(errData.message || 'Ошибка сохранения', 'error');
      }
    } catch (err) {
      showToast('Ошибка при сохранении статьи', 'error');
    }
  });

  // Preview button
  const previewBtn = document.getElementById('previewArticleBtn');
  if (previewBtn) {
    previewBtn.addEventListener('click', () => {
      const title = document.getElementById('articleTitle').value || 'Без названия';
      const content = quill ? quill.root.innerHTML : (document.getElementById('articleContent')?.value || '');
      const status = document.getElementById('articleStatus').value;

      const previewWindow = window.open('', '_blank', 'width=900,height=700');
      previewWindow.document.write(`
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <title>Предпросмотр: ${escapeHtml(title)}</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 800px; margin: 40px auto; padding: 20px; line-height: 1.7; }
            h1 { border-bottom: 1px solid #eee; padding-bottom: 10px; }
            .meta { color: #666; font-size: 14px; margin-bottom: 30px; }
            .content { font-size: 16px; }
            .content img { max-width: 100%; height: auto; }
          </style>
        </head>
        <body>
          <div class="meta">Статус: <strong>${status}</strong> • Предпросмотр</div>
          <h1>${escapeHtml(title)}</h1>
          <div class="content">${content}</div>
        </body>
        </html>
      `);
      previewWindow.document.close();
    });
  }

  // Media File Manager Upload Setup
  const dropZone = document.getElementById('dropZone');
  const fileInput = document.getElementById('fileInput');

  dropZone.addEventListener('click', (e) => {
    // Не открываем диалог выбора файла, если кликнули по выпадающему списку
    if (e.target.tagName === 'SELECT' || e.target.tagName === 'OPTION') return;
    fileInput.click();
  });

  dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('dragover');
  });

  dropZone.addEventListener('dragleave', () => {
    dropZone.classList.remove('dragover');
  });

  dropZone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropZone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) {
      const category = document.getElementById('uploadCategorySelect').value;
      uploadFiles(e.dataTransfer.files, category);
    }
  });

  fileInput.addEventListener('change', (e) => {
    if (e.target.files.length > 0) {
      const category = document.getElementById('uploadCategorySelect').value;
      uploadFiles(e.target.files, category);
    }
  });

  // Settings Save handler (поддерживает input, textarea, checkbox)
  document.getElementById('settingsForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const container = document.getElementById('settingsForm');
    const settings = {};

    // Собираем обычные input и textarea
    container.querySelectorAll('input[type="text"], input[type="email"], textarea').forEach(el => {
      settings[el.name] = el.value;
    });

    // Чекбоксы (boolean настройки)
    container.querySelectorAll('input[type="checkbox"]').forEach(el => {
      settings[el.name] = el.checked ? 'true' : 'false';
    });

    try {
      const res = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settings)
      });

      if (res.ok) {
        showToast('Настройки сайта сохранены!', 'success');
      } else {
        showToast('Не удалось сохранить настройки', 'error');
      }
    } catch (err) {
      showToast('Ошибка при сохранении настроек', 'error');
    }
  });

  // Demo reset button (superadmin only)
  const resetBox = document.getElementById('resetDemoBox');
  const resetBtn = document.getElementById('resetDemoBtn');
  if (resetBox && resetBtn) {
    // Will be shown by checkSession for Superadmin (we toggle here too for safety)
    resetBtn.addEventListener('click', async () => {
      if (!await confirmDialog('Сбросить все демо-данные? Это действие необратимо.', { okText: 'Сбросить', danger: true })) return;
      try {
        const res = await fetch('/api/admin/reset-demo', { method: 'POST' });
        const data = await res.json();
        if (res.ok && data.success) {
          showToast('Демо-данные сброшены. Обновляю...', 'success');
          setTimeout(() => {
            window.location.reload();
          }, 800);
        } else {
          showToast(data.message || 'Не удалось сбросить', 'error');
        }
      } catch (e) {
        showToast('Ошибка сети при сбросе', 'error');
      }
    });
  }

  // Users Search
  const usersSearch = document.getElementById('usersSearch');
  if (usersSearch) {
    usersSearch.addEventListener('input', (e) => {
      const query = e.target.value.toLowerCase();
      renderUsers(query);
    });
  }

  // Logs Search
  const logsSearch = document.getElementById('logsSearch');
  if (logsSearch) {
    logsSearch.addEventListener('input', (e) => {
      const query = e.target.value.toLowerCase();
      renderFullLogs(query);
    });
  }

  // User Modal Setup
  const userModal = document.getElementById('userModalOverlay');
  const addUserBtn = document.getElementById('addUserBtn');
  const cancelUserBtn = document.getElementById('cancelUserModalBtn');
  const closeUserBtn = document.getElementById('closeUserModalBtn');
  const userForm = document.getElementById('userForm');

  const openUserModal = (title = 'Добавить пользователя', id = '', username = '', email = '', role = 'User') => {
    document.getElementById('userModalTitle').textContent = title;
    document.getElementById('userId').value = id;
    document.getElementById('userUsername').value = username;
    document.getElementById('userEmail').value = email;
    document.getElementById('userPassword').value = '';
    document.getElementById('userPassword').required = !id; // required only for new user
    document.getElementById('passwordHelp').textContent = id
      ? 'Оставьте пустым, чтобы не менять пароль.'
      : 'Пароль обязателен для создания нового пользователя.';
    document.getElementById('userRole').value = role;
    userModal.classList.add('active');
  };

  const closeUserModal = () => {
    userModal.classList.remove('active');
    userForm.reset();
  };

  if (addUserBtn) addUserBtn.addEventListener('click', () => openUserModal());
  if (cancelUserBtn) cancelUserBtn.addEventListener('click', closeUserModal);
  if (closeUserBtn) closeUserBtn.addEventListener('click', closeUserModal);
  if (userModal) userModal.addEventListener('click', (e) => { if (e.target === userModal) closeUserModal(); });

  // User Save handler
  if (userForm) {
    userForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const id = document.getElementById('userId').value;
      const username = document.getElementById('userUsername').value;
      const email = document.getElementById('userEmail').value;
      const password = document.getElementById('userPassword').value;
      const role = document.getElementById('userRole').value;

      const payload = { username, email, role };
      if (password) payload.password = password;

      const method = id ? 'PUT' : 'POST';
      const url = id ? `/api/users?id=${id}` : '/api/users';

      try {
        const res = await fetch(url, {
          method,
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });

        if (res.ok) {
          showToast(id ? 'Данные пользователя обновлены' : 'Пользователь успешно создан', 'success');
          closeUserModal();
          await loadUsers();
        } else {
          const errData = await res.json();
          showToast(errData.message || 'Ошибка сохранения', 'error');
        }
      } catch (err) {
        showToast('Ошибка сохранения данных пользователя', 'error');
      }
    });
  }

  // Clear Logs Handler
  const clearLogsBtn = document.getElementById('clearLogsBtn');
  if (clearLogsBtn) {
    clearLogsBtn.addEventListener('click', async () => {
      if (await confirmDialog('Вы действительно хотите удалить все логи действий? Это действие необратимо.', { okText: 'Удалить', danger: true })) {
        try {
          const res = await fetch('/api/logs', { method: 'DELETE' });
          if (res.ok) {
            showToast('Логи действий успешно очищены', 'success');
            await loadFullLogs();
          } else {
            showToast('Не удалось очистить логи', 'error');
          }
        } catch (err) {
          showToast('Ошибка при отправке запроса', 'error');
        }
      }
    });
  }

}

// Load data specifically for selected route
function loadSectionData(hash) {
  if (hash === 'whitelist') {
    loadWhitelist();
  } else if (hash === 'servers') {
    loadServersSection();
  } else if (hash === 'dashboard') {
    loadDashboardStats();
  } else if (hash === 'articles') {
    loadArticles();
  } else if (hash === 'media') {
    loadMedia();
  } else if (hash === 'settings') {
    loadSettings();
  } else if (hash === 'users') {
    loadUsers();
  } else if (hash === 'logs') {
    loadFullLogs();
  } else if (hash === 'support') {
    loadSupportTickets();
  }
}

// === Отправка уведомлений пользователям (в личный кабинет) ===
window.openSendNotificationModal = async (userId = null) => {
  const overlay = document.getElementById('sendNotificationModalOverlay');
  if (!overlay) return;
  if (!usersList.length) { try { await loadUsers(); } catch (e) {} }
  const sel = document.getElementById('notifTargetUser');
  const names = usersList.map(u => `<option value="${u.id}">${escapeHtml(userDisplayName(u))} (${escapeHtml(u.email)})</option>`).join('');
  sel.innerHTML = '<option value="">Всем пользователям</option>' + names;
  sel.value = userId != null ? String(userId) : '';
  document.getElementById('notifMessage').value = '';
  document.getElementById('notifScheduledAt').value = '';
  overlay.classList.add('active');
};
window.closeSendNotificationModal = () => {
  const overlay = document.getElementById('sendNotificationModalOverlay');
  if (overlay) overlay.classList.remove('active');
};
window.submitNotification = async () => {
  const targetVal = document.getElementById('notifTargetUser').value;
  const message = document.getElementById('notifMessage').value.trim();
  const scheduledAt = document.getElementById('notifScheduledAt').value || null;
  if (!message) { showToast('Введите текст уведомления', 'error'); return; }
  try {
    const res = await fetch('/api/admin/notifications', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ user_id: targetVal ? parseInt(targetVal, 10) : null, message, scheduled_at: scheduledAt })
    });
    const d = await res.json().catch(() => ({}));
    if (res.ok && d.success) {
      showToast(scheduledAt ? 'Уведомление запланировано' : 'Уведомление отправлено', 'success');
      closeSendNotificationModal();
      if (typeof loadNotificationHistory === 'function') loadNotificationHistory();
    } else {
      showToast(d.message || 'Не удалось отправить', 'error');
    }
  } catch (e) { showToast('Ошибка сети', 'error'); }
};

// === Настройки уведомлений: история отправленных, удаление, статусы ===
window.openNotificationSettingsModal = () => {
  const overlay = document.getElementById('notificationSettingsModalOverlay');
  if (!overlay) return;
  overlay.classList.add('active');
  loadNotificationHistory();
};
window.closeNotificationSettingsModal = () => {
  const overlay = document.getElementById('notificationSettingsModalOverlay');
  if (overlay) overlay.classList.remove('active');
};

// === Настройки чата поддержки: имя администрации + показ ФИО сотрудника ===
window.openChatSettingsModal = async () => {
  const overlay = document.getElementById('chatSettingsModalOverlay');
  if (!overlay) return;
  overlay.classList.add('active');
  try {
    const res = await fetch('/api/settings');
    const rows = res.ok ? await res.json() : [];
    const map = {};
    (rows || []).forEach(r => { map[r.key] = r.value; });
    document.getElementById('chatSettingsAdminName').value = map.support_admin_display_name || 'Администрация';
  } catch (e) {
    showToast('Не удалось загрузить настройки чата', 'error');
  }
};
window.closeChatSettingsModal = () => {
  const overlay = document.getElementById('chatSettingsModalOverlay');
  if (overlay) overlay.classList.remove('active');
};
window.saveChatSettings = async () => {
  const name = document.getElementById('chatSettingsAdminName').value.trim() || 'Администрация';
  try {
    const res = await fetch('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        support_admin_display_name: name
      })
    });
    if (res.ok) {
      showToast('Настройки чата сохранены', 'success');
      closeChatSettingsModal();
    } else {
      showToast('Не удалось сохранить настройки', 'error');
    }
  } catch (e) {
    showToast('Ошибка сети', 'error');
  }
};

function shortWhenNotif(iso) {
  if (!iso) return '';
  const d = new Date(String(iso).replace(' ', 'T'));
  if (isNaN(d)) return '';
  return d.toLocaleString('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}

async function loadNotificationHistory() {
  const box = document.getElementById('notificationHistoryList');
  if (!box) return;
  box.innerHTML = '<div style="padding:20px; color:hsl(var(--text-muted));">Загрузка...</div>';
  try {
    const res = await fetch('/api/admin/notifications');
    const d = await res.json();
    const list = (d && d.notifications) || [];
    if (!list.length) {
      box.innerHTML = '<div style="padding:30px; text-align:center; color:hsl(var(--text-muted));">Уведомлений пока не отправляли</div>';
      return;
    }
    box.innerHTML = list.map(n => {
      const target = n.target_name ? escapeHtml(n.target_name) : 'Всем пользователям';
      const statusBadge = n.is_pending
        ? `<span class="badge" style="background:hsl(var(--accent-amber) / 0.15);color:hsl(var(--accent-amber))">Запланировано на ${shortWhenNotif(n.scheduled_at)}</span>`
        : `<span class="badge" style="background:hsl(var(--accent-green) / 0.15);color:hsl(var(--accent-green))">Отправлено</span>`;
      const readInfo = n.total_recipients > 1
        ? `Прочитали: ${n.read_count} из ${n.total_recipients}`
        : (n.read_count > 0 ? 'Прочитано' : 'Не прочитано');
      const canShowReaders = !n.is_pending && n.read_count > 0;
      const readInfoHtml = canShowReaders
        ? `<span style="font-size:12px; color:hsl(var(--accent-cyan)); cursor:pointer; text-decoration:underline;" onclick="toggleNotificationReaders(${n.id})">${readInfo} — кто?</span>`
        : `<span style="font-size:12px; color:hsl(var(--text-muted));">${readInfo}</span>`;
      return `
        <div style="border:1px solid hsl(var(--border-color)); border-radius:10px; padding:14px 16px;">
          <div style="display:flex; justify-content:space-between; align-items:flex-start; gap:10px; margin-bottom:8px;">
            <div style="font-size:13px; color:hsl(var(--text-muted));">${target} · ${shortWhenNotif(n.created_at)}</div>
            <button class="action-btn delete" title="Удалить" onclick="deleteNotificationHistoryItem(${n.id})"><i data-lucide="trash-2"></i></button>
          </div>
          <div style="font-size:14px; margin-bottom:8px;">${escapeHtml(n.message)}</div>
          <div style="display:flex; gap:10px; flex-wrap:wrap; align-items:center;">
            ${statusBadge}
            ${!n.is_pending ? readInfoHtml : ''}
          </div>
          <div id="notifReaders-${n.id}" style="display:none; margin-top:10px; padding-top:10px; border-top:1px solid hsl(var(--border-color));"></div>
        </div>`;
    }).join('');
    if (window.lucide) lucide.createIcons();
  } catch (e) {
    box.innerHTML = '<div style="padding:20px; color:#ff6b6b;">Ошибка загрузки</div>';
  }
}

window.toggleNotificationReaders = async (id) => {
  const box = document.getElementById(`notifReaders-${id}`);
  if (!box) return;
  if (box.style.display !== 'none') { box.style.display = 'none'; return; }
  box.style.display = 'block';
  box.innerHTML = '<div style="font-size:12px; color:hsl(var(--text-muted));">Загрузка...</div>';
  try {
    const res = await fetch(`/api/admin/notifications/reads?id=${id}`);
    const d = await res.json();
    const readers = (d && d.readers) || [];
    if (!readers.length) {
      box.innerHTML = '<div style="font-size:12px; color:hsl(var(--text-muted));">Пока никто не прочитал</div>';
      return;
    }
    box.innerHTML = readers.map(r => `
      <div style="display:flex; justify-content:space-between; gap:10px; font-size:13px; padding:4px 0;">
        <span>${escapeHtml(r.name)}</span>
        <span style="color:hsl(var(--text-muted));">${shortWhenNotif(r.read_at)}</span>
      </div>`).join('');
  } catch (e) {
    box.innerHTML = '<div style="font-size:12px; color:#ff6b6b;">Ошибка загрузки</div>';
  }
};

window.deleteNotificationHistoryItem = async (id) => {
  if (!await confirmDialog('Удалить это уведомление? Если оно ещё не отправлено (запланировано) — отправки не будет.', { okText: 'Удалить', danger: true })) return;
  try {
    const res = await fetch(`/api/admin/notifications?id=${id}`, { method: 'DELETE' });
    if (res.ok) { showToast('Удалено', 'success'); loadNotificationHistory(); }
    else showToast('Не удалось удалить', 'error');
  } catch (e) { showToast('Ошибка сети', 'error'); }
};

async function loadMedia(filter = '') {
  const grid = document.getElementById('mediaGrid');
  const categorySelect = document.getElementById('mediaCategorySelect');
  
  // If we are in folder view, don't load grid yet
  const foldersGrid = document.getElementById('mediaFoldersGrid');
  if (foldersGrid && foldersGrid.style.display !== 'none') {
    return;
  }
  
  if (!grid) return;
  const category = categorySelect ? categorySelect.value : 'all';
  grid.innerHTML = '<div style="color: hsl(var(--text-muted));">Загрузка файлов...</div>';

  try {
    const res = await fetch(`/api/media?category=${category}`);
    if (res.ok) {
      mediaFilesCache = await res.json();
      renderMediaGrid(filter);
    }
  } catch (err) {
    showToast('Ошибка загрузки медиатеки', 'error');
  }
}

function renderMediaGrid(filter = '') {
  const grid = document.getElementById('mediaGrid');
  grid.innerHTML = '';

  const filtered = mediaFilesCache.filter(f => 
    f.filename.toLowerCase().includes(filter.toLowerCase())
  );

  if (filtered.length === 0) {
    grid.innerHTML = '<div style="grid-column: 1/-1; text-align: center; color: hsl(var(--text-muted)); padding: 40px;">Ничего не найдено.</div>';
    return;
  }

  filtered.forEach(file => {
    const item = document.createElement('div');
    item.className = 'media-item';
    
    const isImage = file.mime_type.startsWith('image/');
    const previewEl = isImage 
      ? `<img src="${file.file_url}" alt="${escapeHtml(file.filename)}">`
      : `<i data-lucide="file" style="width: 48px; height: 48px; color: hsl(var(--text-secondary));"></i>`;

    const sizeMB = (file.file_size / (1024 * 1024)).toFixed(2);

    // Строка с привязкой: к чему/к кому относится файл.
    let attachHtml = '';
    if (file.attached_to) {
      const a = file.attached_to;
      if (a.type === 'user') {
        attachHtml = `<div class="media-attach" style="font-size:11px;color:hsl(var(--accent-amber));margin-top:2px;">👤 Аватар: ${escapeHtml(a.label || '')}</div>`;
      }
    }
    const uploaderHtml = file.uploaded_by_name
      ? `<div class="media-uploader" style="font-size:11px;color:hsl(var(--text-muted));margin-top:2px;">Загрузил: ${escapeHtml(file.uploaded_by_name)}</div>`
      : '';

    item.innerHTML = `
      <div class="media-preview">${previewEl}</div>
      <div class="media-info">
        <div class="media-title" title="${escapeHtml(file.filename)}">${escapeHtml(file.filename)}</div>
        ${attachHtml}
        ${uploaderHtml}
        <div class="media-meta">
          <span>${sizeMB} MB</span>
          <a href="${file.file_url}" target="_blank" style="color: hsl(var(--accent-cyan)); text-decoration: none;">Открыть</a>
          <button onclick="copyMediaUrl('${file.file_url}', event)" style="background:none;border:1px solid hsl(var(--border-color));color:hsl(var(--text-secondary));padding:2px 6px;font-size:11px;border-radius:4px;cursor:pointer;">URL</button>
          ${(typeof currentUser !== 'undefined' && currentUser && currentUser.role === 'Superadmin') ? `<button onclick="moveMedia(${file.id}, '${file.category}', event)" style="background:none;border:1px solid hsl(var(--border-color));color:hsl(var(--accent-amber));padding:2px 6px;font-size:11px;border-radius:4px;cursor:pointer;margin-left:4px;">В папку</button>` : ''}
        </div>
      </div>
      <button class="media-delete" onclick="deleteMedia(${file.id}, event)"><i data-lucide="trash-2" style="width: 14px; height: 14px;"></i></button>
    `;
    grid.appendChild(item);
  });
  
  lucide.createIcons();
}

window.moveMedia = async (id, currentCategory, e) => {
  if (e) e.stopImmediatePropagation();
  const targetCategory = await promptDialog('Введите новую категорию (general, avatars, articles, support):', currentCategory, { okText: 'Переместить' });
  if (!targetCategory || targetCategory === currentCategory) return;
  try {
    const res = await fetch('/api/media', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, category: targetCategory })
    });
    if (res.ok) {
      showToast('Медиафайл перемещен', 'success');
      loadMedia(document.getElementById('mediaSearch')?.value || '');
    } else {
      const data = await res.json().catch(()=>({}));
      showToast(data.message || 'Ошибка перемещения', 'error');
    }
  } catch(err) {
    showToast('Ошибка сети', 'error');
  }
};

// Поиск в медиатеке
const mediaSearchInput = document.getElementById('mediaSearch');
if (mediaSearchInput) {
  mediaSearchInput.addEventListener('input', (e) => {
    renderMediaGrid(e.target.value);
  });
}

window.copyMediaUrl = async (url, e) => {
  e.stopImmediatePropagation();
  try {
    await navigator.clipboard.writeText(window.location.origin + url);
    showToast('URL скопирован в буфер обмена', 'success');
  } catch {
    // fallback — показываем URL в поле, откуда его можно скопировать вручную
    await promptDialog('Скопируйте URL:', window.location.origin + url, { okText: 'Готово' });
  }
};

// --- Media Picker Modal ---
let mediaPickerCallback = null;

window.openMediaPicker = async (onSelect, defaultCategory = 'all') => {
  mediaPickerCallback = onSelect;
  const modal = document.getElementById('mediaPickerModalOverlay');
  const catSelect = document.getElementById('mediaPickerCategorySelect');
  if (catSelect) catSelect.value = defaultCategory;
  
  try {
    const res = await fetch(`/api/media?category=${defaultCategory}`);
    if (res.ok) mediaFilesCache = await res.json();
  } catch(e) {}
  
  renderMediaPickerGrid();
  modal.classList.add('active');
};

function renderMediaPickerGrid(filter = '') {
  const grid = document.getElementById('mediaPickerGrid');
  if (!grid) return;
  grid.innerHTML = '';

  const filtered = mediaFilesCache.filter(f => 
    f.mime_type.startsWith('image/') && 
    f.filename.toLowerCase().includes(filter.toLowerCase())
  );

  if (filtered.length === 0) {
    grid.innerHTML = '<div style="grid-column: 1/-1; text-align: center; color: hsl(var(--text-muted)); padding: 40px;">Изображения не найдены.</div>';
    return;
  }

  filtered.forEach(file => {
    const item = document.createElement('div');
    item.className = 'media-item';
    item.style.cursor = 'pointer';
    
    item.innerHTML = `
      <div class="media-preview">
        <img src="${file.file_url}" alt="${escapeHtml(file.filename)}">
      </div>
      <div class="media-info" style="border-top: 1px solid hsl(var(--border-color)); padding: 6px;">
        <div class="media-title" title="${escapeHtml(file.filename)}" style="font-size: 11px;">${escapeHtml(file.filename)}</div>
      </div>
    `;
    
    item.addEventListener('click', () => {
      if (mediaPickerCallback) mediaPickerCallback(file.file_url);
      document.getElementById('mediaPickerModalOverlay').classList.remove('active');
    });
    
    grid.appendChild(item);
  });
}

document.getElementById('closeMediaPickerBtn')?.addEventListener('click', () => {
  document.getElementById('mediaPickerModalOverlay').classList.remove('active');
});
document.getElementById('cancelMediaPickerBtn')?.addEventListener('click', () => {
  document.getElementById('mediaPickerModalOverlay').classList.remove('active');
});
const mediaPickerModal = document.getElementById('mediaPickerModalOverlay');
mediaPickerModal?.addEventListener('click', (e) => {
  if (e.target === mediaPickerModal) mediaPickerModal.classList.remove('active');
});
document.getElementById('mediaPickerSearch')?.addEventListener('input', (e) => {
  renderMediaPickerGrid(e.target.value);
});
document.getElementById('mediaCategorySelect')?.addEventListener('change', () => {
  loadMedia(document.getElementById('mediaSearch').value);
});
document.getElementById('mediaPickerCategorySelect')?.addEventListener('change', async (e) => {
  const category = e.target.value;
  try {
    const res = await fetch(`/api/media?category=${category}`);
    if (res.ok) {
      mediaFilesCache = await res.json();
      renderMediaPickerGrid(document.getElementById('mediaPickerSearch').value);
    }
  } catch(e) {}
});


window.deleteMedia = async (id, e) => {
  if (e) e.stopImmediatePropagation();
  if (!await confirmDialog('Вы уверены, что хотите удалить этот файл?', { okText: 'Удалить', danger: true })) return;

  try {
    const res = await fetch(`/api/media?id=${id}`, { method: 'DELETE' });
    if (res.ok) {
      showToast('Файл удален', 'success');
      await loadMedia(document.getElementById('mediaSearch')?.value || '');
    } else {
      showToast('Не удалось удалить файл', 'error');
    }
  } catch (err) {
    showToast('Ошибка при удалении', 'error');
  }
};

// Лимит одного файла на сервере (держим в синхроне с MAX_SIZE_BYTES в utils.js)
const MEDIA_MAX_FILE_BYTES = 25 * 1024 * 1024; // 25 МБ
// Большие изображения ужимаем в браузере до этого размера по длинной стороне
const IMAGE_MAX_DIMENSION = 1920;

function humanSize(bytes) {
  if (bytes >= 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' МБ';
  return Math.max(1, Math.round(bytes / 1024)) + ' КБ';
}

async function uploadFiles(filesList, category = 'general') {
  showToast('Подготовка файлов...', 'info');

  try {
    const filesPayload = [];
    const skipped = [];

    for (const file of filesList) {
      let prepared = { filename: file.name, blobOrFile: file, mimeType: file.type };

      // Изображения при необходимости уменьшаем (кроме SVG/GIF — их не трогаем).
      if (/^image\/(jpeg|png|webp)$/.test(file.type)) {
        try {
          const compressed = await compressImage(file);
          if (compressed && compressed.blob.size < file.size) {
            const base = file.name.replace(/\.[^.]+$/, '');
            prepared = { filename: base + '.jpg', blobOrFile: compressed.blob, mimeType: 'image/jpeg' };
          }
        } catch (_) { /* если сжатие не удалось — пробуем отправить оригинал */ }
      }

      // Понятная ошибка ДО отправки: если всё ещё больше лимита — пропускаем файл.
      if (prepared.blobOrFile.size > MEDIA_MAX_FILE_BYTES) {
        skipped.push(`${file.name} (${humanSize(prepared.blobOrFile.size)})`);
        continue;
      }

      const data = await blobToBase64(prepared.blobOrFile);
      filesPayload.push({ filename: prepared.filename, data, mimeType: prepared.mimeType });
    }

    if (skipped.length) {
      showToast(`Слишком большие, пропущены (макс ${humanSize(MEDIA_MAX_FILE_BYTES)}): ${skipped.join(', ')}`, 'error');
    }
    if (filesPayload.length === 0) return;

    showToast('Загрузка файлов...', 'info');
    const res = await fetch('/api/media', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ files: filesPayload, category })
    });

    if (res.ok) {
      showToast(`Загружено файлов: ${filesPayload.length}`, 'success');
      await loadMedia();
    } else {
      const err = await res.json().catch(() => ({}));
      showToast(err.message || 'Ошибка загрузки файлов', 'error');
    }
  } catch (err) {
    showToast('Ошибка сети при загрузке файлов', 'error');
  }
}

// Уменьшает изображение до IMAGE_MAX_DIMENSION по длинной стороне и
// перекодирует в JPEG. Возвращает { blob } или null, если сжатие невозможно.
function compressImage(file) {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      let { width, height } = img;
      const scale = Math.min(1, IMAGE_MAX_DIMENSION / Math.max(width, height));
      width = Math.round(width * scale);
      height = Math.round(height * scale);

      const canvas = document.createElement('canvas');
      canvas.width = width; canvas.height = height;
      const ctx = canvas.getContext('2d');
      if (!ctx) return resolve(null);
      ctx.drawImage(img, 0, 0, width, height);
      canvas.toBlob(
        (blob) => resolve(blob ? { blob } : null),
        'image/jpeg',
        0.85
      );
    };
    img.onerror = () => { URL.revokeObjectURL(url); resolve(null); };
    img.src = url;
  });
}

function blobToBase64(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result.split(',')[1]); // убираем data:...;base64,
    reader.onerror = reject;
    reader.readAsDataURL(blob);
  });
}

// API: Settings loader с типами полей и группировкой
async function loadSettings() {
  const siteContainer = document.getElementById('settingsContainerSite');
  siteContainer.innerHTML = '<div style="color: hsl(var(--text-muted));">Загрузка настроек...</div>';

  const GROUPS = {
    'Общие': ['site_name', 'maintenance_mode', 'allow_registration'],
    'Главная страница': ['hero_title', 'site_description'],
    'О блоге': ['about_title', 'about_subtitle', 'about_card1_title', 'about_card1_text', 'about_card2_title', 'about_card2_text'],
    'Контакты': ['contact_title', 'contact_subtitle', 'contact_email', 'contact_address']
  };

  // Понятные подписи (не зависят от description в БД, который может теряться
  // при сохранении из-за INSERT OR REPLACE).
  const LABELS = {};

  // Определяем тип контрола по ключу
  function getFieldType(key) {
    if (['maintenance_mode', 'allow_registration'].includes(key)) return 'boolean';
    if (['site_description', 'about_subtitle', 'about_card1_text', 'about_card2_text', 'contact_subtitle'].includes(key)) return 'textarea';
    if (key === 'contact_email') return 'email';
    return 'text';
  }

  try {
    const res = await fetch('/api/settings');
    if (!res.ok) throw new Error('Failed to load');
    const allSettings = await res.json();

    // Преобразуем в map для быстрого доступа
    const settingsMap = {};
    allSettings.forEach(s => { settingsMap[s.key] = s; });

    siteContainer.innerHTML = '';

    Object.entries(GROUPS).forEach(([groupTitle, keys]) => {
      const container = siteContainer;
      // Заголовок группы
      const groupHeader = document.createElement('div');
      groupHeader.style.cssText = 'margin: 18px 0 8px; font-size: 13px; font-weight: 600; color: var(--accent-cyan); text-transform: uppercase; letter-spacing: 0.5px;';
      groupHeader.textContent = groupTitle;
      container.appendChild(groupHeader);

      keys.forEach(key => {
        const set = settingsMap[key];
        if (!set) return;

        const fieldType = getFieldType(key);
        const div = document.createElement('div');
        div.className = 'form-group';

        const labelText = escapeHtml(LABELS[key] || set.description || set.key);

        if (fieldType === 'boolean') {
          const checked = set.value === 'true' ? 'checked' : '';
          div.innerHTML = `
            <label style="display:flex; align-items:center; gap:10px; cursor:pointer;">
              <input type="checkbox" name="${escapeHtml(key)}" ${checked} style="width:18px; height:18px; accent-color: var(--accent-purple);">
              <span>${labelText}</span>
            </label>
          `;
        } else if (fieldType === 'textarea') {
          div.innerHTML = `
            <label>${labelText}</label>
            <textarea name="${escapeHtml(key)}" class="form-control" rows="3" style="resize: vertical; min-height: 70px;">${escapeHtml(set.value || '')}</textarea>
          `;
        } else {
          const inputType = fieldType === 'email' ? 'email' : 'text';
          div.innerHTML = `
            <label>${labelText}</label>
            <input type="${inputType}" name="${escapeHtml(key)}" value="${escapeHtml(set.value || '')}" class="form-control">
          `;
        }

        container.appendChild(div);
      });
    });
  } catch (err) {
    siteContainer.innerHTML = '<div style="color: #ff6b6b;">Не удалось загрузить настройки</div>';
    showToast('Ошибка загрузки настроек', 'error');
  }
}

// Переключение вкладок раздела «Настройки»: сайт / система.
function switchSettingsTab(tab) {
  document.querySelectorAll('.settings-tab-btn').forEach(b => {
    b.classList.toggle('active', b.dataset.settingsTab === tab);
  });
  document.getElementById('settingsForm').hidden = tab === 'system';
  document.getElementById('settingsPanelSite').hidden = tab !== 'site';
  document.getElementById('settingsPanelSystem').hidden = tab !== 'system';
}
document.querySelectorAll('.settings-tab-btn').forEach(btn => {
  btn.addEventListener('click', () => switchSettingsTab(btn.dataset.settingsTab));
});

// Helpers
window.exportJSON = async (type) => {
  try {
    const res = await fetch(type === 'media' ? '/api/media' : `/api/crud/${type}`);
    const data = await res.json();
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${type}_export.json`;
    a.click();
    URL.revokeObjectURL(url);
    showToast('JSON успешно экспортирован', 'success');
  } catch (err) {
    showToast('Ошибка экспорта JSON', 'error');
  }
};

window.exportCSV = async (type) => {
  try {
    const res = await fetch(type === 'media' ? '/api/media' : `/api/crud/${type}`);
    const data = await res.json();
    if (!data.length) return showToast('Нет данных для экспорта', 'error');
    
    const keys = Object.keys(data[0]);
    const csvContent = [
      keys.join(','),
      ...data.map(row => keys.map(k => `"${String(row[k] || '').replace(/"/g, '""')}"`).join(','))
    ].join('\n');
    
    const blob = new Blob(['\uFEFF' + csvContent], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${type}_export.csv`;
    a.click();
    URL.revokeObjectURL(url);
    showToast('CSV успешно экспортирован', 'success');
  } catch (err) {
    showToast('Ошибка экспорта CSV', 'error');
  }
};

function escapeHtml(text) {
  if (!text) return '';
  const map = {
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#039;'
  };
  return text.replace(/[&<>"']/g, function(m) { return map[m]; });
}

// Имя+фамилия для сотрудников (карточка привязана к аккаунту), иначе —
// логин (у клиентов карточки сотрудника нет).
function userDisplayName(u) {
  const full = [u.first_name, u.last_name].filter(Boolean).join(' ').trim();
  return full || u.username;
}

// API: Users loader
async function loadUsers() {
  if (currentUser.role !== 'Superadmin') return;
  try {
    const res = await fetch('/api/users');
    if (res.ok) {
      usersList = await res.json();
      renderUsers();
    }
  } catch (err) {
    showToast('Ошибка загрузки пользователей', 'error');
  }
}

function renderUsers(filterQuery = '') {
  const tbody = document.getElementById('usersTableBody');
  if (!tbody) return;
  tbody.innerHTML = '';
  
  const filtered = usersList.filter(u =>
    u.username.toLowerCase().includes(filterQuery) ||
    u.email.toLowerCase().includes(filterQuery) ||
    userDisplayName(u).toLowerCase().includes(filterQuery)
  );

  if (filtered.length === 0) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="6" class="empty-state" style="text-align: center; color: hsl(var(--text-muted)); padding: 30px;">Пользователи не найдены</td></tr>`;
    return;
  }

  filtered.forEach(u => {
    const tr = document.createElement('tr');
    tr.onclick = mobileRowTap(() => openUserDetail(u.id));
    const roleBadge = userRoleBadge(u.role);
    const dateFormatted = new Date(u.created_at).toLocaleDateString('ru-RU', {
      day: 'numeric', month: 'long', year: 'numeric'
    });
    const name = userDisplayName(u);
    const primaryCell = name !== u.username
      ? `<strong>${escapeHtml(name)}</strong><div style="font-size:12px;color:hsl(var(--text-muted));">${escapeHtml(u.username)}</div>`
      : `<strong>${escapeHtml(name)}</strong>`;

    tr.innerHTML = `
      <td class="hide-mobile">${u.id}</td>
      <td class="mobile-primary">${primaryCell}</td>
      <td class="mobile-hidden">${escapeHtml(u.email)}</td>
      <td><div style="display:flex;gap:6px;flex-wrap:wrap;align-items:center">${roleBadge}</div></td>
      <td class="mobile-hidden">${dateFormatted}</td>
      <td class="no-label" style="text-align: right;">
        <div class="action-btns" style="justify-content: flex-end;">
          <button class="action-btn edit" onclick="editUser(${u.id})"><i data-lucide="edit-3"></i></button>
          <button class="action-btn delete" onclick="deleteUser(${u.id})"><i data-lucide="trash-2"></i></button>
          <button class="action-btn chat" onclick="adminStartChat(${u.id}, '${escapeHtml(name)}', '${escapeHtml(u.email)}')"><i data-lucide="message-circle"></i></button>
          <button class="action-btn" title="Отправить уведомление" onclick="openSendNotificationModal(${u.id})"><i data-lucide="bell"></i></button>
        </div>
      </td>
    `;
    tbody.appendChild(tr);
  });

  lucide.createIcons();
}

function userRoleBadge(role) {
  if (role === 'Admin') return `<span class="badge badge-success">Admin</span>`;
  if (role === 'Superadmin') return `<span class="badge badge-success" style="background:var(--accent-purple)">Superadmin</span>`;
  return `<span class="badge badge-warning">User</span>`;
}

window.openUserDetail = (id) => {
  const u = usersList.find(x => x.id === id);
  if (!u) return;
  const dateFormatted = new Date(u.created_at).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
  const name = userDisplayName(u);
  const rows = [
    ['ID', u.id],
    ...(name !== u.username ? [['Логин', u.username]] : []),
    ['Email', u.email],
    ['Роль', userRoleBadge(u.role), true],
    ['Создан', dateFormatted]
  ];
  const actions = `
    <button class="btn btn-secondary" onclick="closeRowDetail(); adminStartChat(${u.id}, '${escapeHtml(name)}', '${escapeHtml(u.email)}')">Чат</button>
    <button class="btn btn-secondary" onclick="closeRowDetail(); deleteUser(${u.id})" style="color:hsl(var(--accent-red));">Удалить</button>
    <button class="btn" onclick="closeRowDetail(); editUser(${u.id})">Редактировать</button>`;
  showRowDetail(name, rows, actions);
};

// API: Full logs loader
async function loadFullLogs() {
  if (currentUser.role !== 'Superadmin') return;
  try {
    const res = await fetch('/api/logs?limit=1000');
    if (res.ok) {
      fullLogsList = await res.json();
      renderFullLogs();
    }
  } catch (err) {
    showToast('Ошибка загрузки логов', 'error');
  }
}

function renderFullLogs(filterQuery = '') {
  const tbody = document.getElementById('fullLogsTableBody');
  if (!tbody) return;
  tbody.innerHTML = '';
  
  const filtered = fullLogsList.filter(l => 
    l.user.toLowerCase().includes(filterQuery) || 
    l.action.toLowerCase().includes(filterQuery)
  );

  if (filtered.length === 0) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="3" class="empty-state" style="text-align: center; color: hsl(var(--text-muted)); padding: 30px;">Логи не найдены</td></tr>`;
    return;
  }

  filtered.forEach(l => {
    const tr = document.createElement('tr');
    const dateStr = new Date(l.created_at).toLocaleString('ru-RU');
    tr.onclick = mobileRowTap(() => showRowDetail('Запись лога', [
      ['Дата', dateStr],
      ['Пользователь', `<span class="badge badge-warning">${escapeHtml(l.user)}</span>`, true],
      ['Действие', l.action]
    ]));
    tr.innerHTML = `
      <td class="mobile-hidden" style="color: hsl(var(--text-muted)); font-size: 13px;">${dateStr}</td>
      <td class="mobile-hidden"><span class="badge badge-warning">${escapeHtml(l.user)}</span></td>
      <td class="mobile-primary" style="overflow:hidden;text-overflow:ellipsis;">${escapeHtml(l.action)}</td>
    `;
    tbody.appendChild(tr);
  });
}

window.editUser = (id) => {
  const u = usersList.find(user => user.id === id);
  if (u) {
    const modal = document.getElementById('userModalOverlay');
    document.getElementById('userModalTitle').textContent = 'Редактировать пользователя';
    document.getElementById('userId').value = u.id;
    document.getElementById('userUsername').value = u.username;
    document.getElementById('userEmail').value = u.email;
    document.getElementById('userPassword').value = '';
    document.getElementById('userPassword').required = false;
    document.getElementById('passwordHelp').textContent = 'Оставьте пустым, чтобы не менять пароль.';
    document.getElementById('userRole').value = u.role;
    modal.classList.add('active');
  }
};

window.deleteUser = async (id) => {
  if (parseInt(id, 10) === parseInt(currentUser.id, 10)) {
    showToast('Вы не можете удалить свою собственную учетную запись', 'error');
    return;
  }
  if (await confirmDialog('Вы действительно хотите удалить этого пользователя?', { okText: 'Удалить', danger: true })) {
    try {
      const res = await fetch(`/api/users?id=${id}`, { method: 'DELETE' });
      if (res.ok) {
        showToast('Пользователь успешно удален', 'success');
        await loadUsers();
      } else {
        const data = await res.json();
        showToast(data.message || 'Не удалось удалить пользователя', 'error');
      }
    } catch (err) {
      showToast('Ошибка запроса на удаление', 'error');
    }
  }
};

// --- SUPPORT CHAT LOGIC ---
let activeTicketId = null;
let supportPollingInterval = null;
let supportUsersCache = [];   // все пользователи + агрегаты поддержки
let supportSearchQuery = '';
let supportListStream = null;  // EventSource: любое новое сообщение в любом тикете
let ticketChatStream = null;   // EventSource: сообщения открытого тикета
let activeTicketStatus = 'open';
let currentChatMessages = [];

// Открывает/переоткрывает SSE-канал списка тикетов (раздел «Обратная связь»).
// При ошибке — молча отключается, старый setInterval (5с, ниже) остаётся
// подстраховкой и продолжает работать сам по себе.
function startSupportListStream() {
  if (supportListStream) { supportListStream.close(); supportListStream = null; }
  try {
    supportListStream = new EventSource('/api/support/stream/admin');
    supportListStream.onmessage = async (e) => {
      let data;
      try { data = JSON.parse(e.data); } catch (err) { return; }
      if (data && data.type === 'ticket_updated') {
        const u = await fetchSupportUsers();
        if (u) { supportUsersCache = u; renderSupportUsers(); }
        if (activeTicketId && data.ticketId === activeTicketId) loadSupportMessages(activeTicketId, true);
      }
    };
    supportListStream.onerror = () => {
      if (supportListStream) { supportListStream.close(); supportListStream = null; }
    };
  } catch (e) { supportListStream = null; }
}
function stopSupportListStream() {
  if (supportListStream) { supportListStream.close(); supportListStream = null; }
}

async function fetchSupportUsers() {
  const res = await fetch('/api/support/users');
  if (!res.ok) return null;
  const data = await res.json();
  return data.users || [];
}

// Фильтр по имени/нику и сортировка: сначала непрочитанные, затем по
// последней активности, затем по имени.
function renderSupportUsers() {
  const q = supportSearchQuery.trim().toLowerCase();
  let list = supportUsersCache.slice();
  if (q) list = list.filter(u => (u.name || '').toLowerCase().includes(q) || (u.email || '').toLowerCase().includes(q));
  list.sort((a, b) => {
    if ((b.unread_count || 0) !== (a.unread_count || 0)) return (b.unread_count || 0) - (a.unread_count || 0);
    const ta = a.last_activity ? Date.parse(String(a.last_activity).replace(' ', 'T')) : 0;
    const tb = b.last_activity ? Date.parse(String(b.last_activity).replace(' ', 'T')) : 0;
    if (tb !== ta) return tb - ta;
    return (a.name || '').localeCompare(b.name || '', 'ru');
  });
  renderTickets(list, !!q);
}

async function loadSupportTickets() {
  try {
    const users = await fetchSupportUsers();
    if (users) { supportUsersCache = users; renderSupportUsers(); }

    startSupportListStream();

    // Подстраховка на случай обрыва/недоступности SSE (прокси и т.п.) —
    // редкий опрос, основная доставка идёт через SSE выше.
    if (supportPollingInterval) clearInterval(supportPollingInterval);
    supportPollingInterval = setInterval(async () => {
      const sec = document.getElementById('section-support');
      if (sec && sec.classList.contains('active')) {
        if (!supportListStream) {
          const u = await fetchSupportUsers();
          if (u) { supportUsersCache = u; renderSupportUsers(); }
          if (activeTicketId && !ticketChatStream) loadSupportMessages(activeTicketId, true);
        }
      } else {
        clearInterval(supportPollingInterval);
        stopSupportListStream();
      }
    }, 30000);
  } catch (err) {
    showToast('Ошибка загрузки списка пользователей', 'error');
  }
}

document.getElementById('supportUserSearch')?.addEventListener('input', (e) => {
  supportSearchQuery = e.target.value || '';
  renderSupportUsers();
});

// Короткая относительная дата: «5 мин», «3 ч», «2 дн», иначе дата.
function shortWhen(raw) {
  if (!raw) return '';
  const d = new Date(String(raw).replace(' ', 'T'));
  if (isNaN(d)) return '';
  const diff = Date.now() - d.getTime();
  const min = Math.floor(diff / 60000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч`;
  const days = Math.floor(h / 24);
  if (days < 7) return `${days} дн`;
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

function renderTickets(tickets, isFiltered = false) {
  const list = document.getElementById('ticketsList');
  if (!list) return;

  // Бейдж меню считаем из ПОЛНОГО кэша (не из отфильтрованного списка).
  setSupportBadge((supportUsersCache || []).reduce((s, t) => s + (t.unread_count || 0), 0));

  if (!tickets.length) {
    const msg = isFiltered ? 'Никого не найдено' : 'Пока нет пользователей';
    list.innerHTML = `
      <div style="padding: 40px 20px; color: hsl(var(--text-muted)); text-align: center; display:flex; flex-direction:column; align-items:center; gap:10px;">
        <i data-lucide="${isFiltered ? 'search-x' : 'users'}" style="width:36px; height:36px; opacity:0.5;"></i>
        <div>${msg}</div>
      </div>`;
    if (window.lucide) lucide.createIcons();
    return;
  }

  let html = '';
  tickets.forEach(t => {
    const unread = t.unread_count || 0;
    const isActive = t.ticket_id === activeTicketId;
    const rowBg = isActive ? 'background: hsl(var(--accent-purple) / 0.12);'
                : unread > 0 ? 'background: hsl(var(--accent-purple) / 0.06);' : '';
    const accent = isActive ? 'box-shadow: inset 3px 0 0 hsl(var(--accent-purple));'
                 : unread > 0 ? 'box-shadow: inset 3px 0 0 hsl(var(--accent-cyan));' : '';
    const badge = unread > 0
      ? `<span title="Непрочитанных: ${unread}" style="background: hsl(var(--accent-purple)); color:#fff; border-radius: 10px; min-width:18px; text-align:center; padding: 1px 6px; font-size: 11px; font-weight: 700;">${unread > 99 ? '99+' : unread}</span>`
      : '';
    const nameWeight = unread > 0 ? '700' : '600';
    const avatarLetter = (t.name || '?').charAt(0).toUpperCase();
    const closedBadge = t.status === 'closed'
      ? `<span style="font-size:10px; color:hsl(var(--text-muted)); border:1px solid hsl(var(--border-color)); border-radius:6px; padding:1px 6px; flex-shrink:0;">закрыт</span>`
      : '';

    // Превью последнего сообщения (кто написал последним + текст).
    const fromAdmin = t.last_sender_role === 'Admin' || t.last_sender_role === 'Superadmin';
    const preview = t.last_message
      ? (fromAdmin ? 'Вы: ' : '') + String(t.last_message).replace(/\s+/g, ' ').trim()
      : (t.email ? t.email + ' · нет переписки' : 'Нет переписки — напишите первым');
    const when = shortWhen(t.last_activity);

    html += `
      <div style="padding: 12px 16px; border-bottom: 1px solid hsl(var(--border-color)); cursor: pointer; display: flex; align-items: center; gap: 12px; transition: background 0.2s; ${rowBg} ${accent}"
           onmouseover="this.style.background='hsl(var(--accent-purple) / 0.1)'"
           onmouseout="this.style.background='${isActive ? 'hsl(var(--accent-purple) / 0.12)' : unread > 0 ? 'hsl(var(--accent-purple) / 0.06)' : 'transparent'}'"
           onclick="openSupportChat('${t.ticket_id}', '${escapeHtml(t.name || '')}', '${escapeHtml(t.email || '')}')">
        <div style="width: 40px; height: 40px; flex-shrink:0; border-radius: 50%; background: linear-gradient(135deg, hsl(var(--accent-purple)), hsl(var(--accent-cyan))); display: flex; align-items: center; justify-content: center; font-weight: bold; color:#fff; overflow:hidden;">${t.avatar_url ? `<img src="${escapeHtml(t.avatar_url)}" style="width:100%;height:100%;object-fit:cover;" onerror="this.replaceWith(document.createTextNode('${avatarLetter}'))">` : avatarLetter}</div>
        <div style="flex: 1; min-width:0;">
          <div style="display: flex; justify-content: space-between; align-items: center; gap:8px; margin-bottom: 3px;">
            <strong style="font-size: 14px; font-weight:${nameWeight}; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(t.name || 'Пользователь')}</strong>
            <span style="display:flex; align-items:center; gap:6px; flex-shrink:0;">
              ${closedBadge}
              ${when ? `<span style="font-size:11px; color:hsl(var(--text-muted));">${when}</span>` : ''}
              ${badge}
            </span>
          </div>
          <div style="font-size: 12px; color: hsl(var(--text-muted)); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(preview)}</div>
        </div>
      </div>
    `;
  });
  list.innerHTML = html;
}

// Статус тикета: обновляет текст/цвет кнопки в шапке модалки чата.
function renderTicketStatusButton() {
  const btn = document.getElementById('ticketStatusToggleBtn');
  if (!btn) return;
  if (activeTicketStatus === 'closed') {
    btn.textContent = 'Открыть заново';
    btn.style.color = 'hsl(var(--accent-cyan))';
  } else {
    btn.textContent = 'Закрыть обращение';
    btn.style.color = '';
  }
}

window.toggleTicketStatus = async () => {
  if (!activeTicketId) return;
  const endpoint = activeTicketStatus === 'closed' ? '/api/support/reopen' : '/api/support/close';
  try {
    const res = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ticketId: activeTicketId })
    });
    if (res.ok) {
      activeTicketStatus = activeTicketStatus === 'closed' ? 'open' : 'closed';
      renderTicketStatusButton();
      showToast(activeTicketStatus === 'closed' ? 'Обращение закрыто' : 'Обращение открыто заново', 'success');
      loadSupportTickets();
    } else {
      showToast('Не удалось изменить статус', 'error');
    }
  } catch (e) {
    showToast('Ошибка сети', 'error');
  }
};

// Открывает/переоткрывает SSE-канал открытого тикета — новые сообщения и
// смена статуса приходят сразу, без ожидания следующего опроса.
function startTicketChatStream(ticketId) {
  if (ticketChatStream) { ticketChatStream.close(); ticketChatStream = null; }
  try {
    ticketChatStream = new EventSource(`/api/support/stream?ticketId=${encodeURIComponent(ticketId)}`);
    ticketChatStream.onmessage = (e) => {
      let data;
      try { data = JSON.parse(e.data); } catch (err) { return; }
      if (!data) return;
      if (data.type === 'status') {
        activeTicketStatus = data.status;
        renderTicketStatusButton();
        return;
      }
      // Полноценное сообщение (есть id/message) — добавляем в открытый чат
      // (дедуп на случай, если то же сообщение уже пришло обычной перезагрузкой).
      if (data.id && !currentChatMessages.some(m => m.id === data.id)) {
        currentChatMessages.push(data);
        renderChatMessages(currentChatMessages, true);
        fetch('/api/support/read', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ticketId })
        }).then(() => updateSupportBadge());
      }
    };
    ticketChatStream.onerror = () => {
      if (ticketChatStream) { ticketChatStream.close(); ticketChatStream = null; }
    };
  } catch (e) { ticketChatStream = null; }
}
function stopTicketChatStream() {
  if (ticketChatStream) { ticketChatStream.close(); ticketChatStream = null; }
}

window.openSupportChat = (ticketId, name, email) => {
  activeTicketId = ticketId;
  document.getElementById('chatHeaderName').textContent = name;
  document.getElementById('chatHeaderEmail').textContent = email || 'Гость';
  document.getElementById('chatHeaderAvatar').textContent = (name || '?').charAt(0).toUpperCase();
  document.getElementById('supportChatModalOverlay').classList.add('active');

  loadSupportMessages(ticketId);
  startTicketChatStream(ticketId);
  loadSupportTickets(); // обновить счётчики в списке и бейдж меню
  setTimeout(() => document.getElementById('replyMessageInput')?.focus(), 50);
};

window.closeSupportChat = () => {
  document.getElementById('supportChatModalOverlay').classList.remove('active');
  activeTicketId = null;
  stopTicketChatStream();
  loadSupportTickets(); // непрочитанные обнулились — обновляем список/бейдж
};

// Admin-initiated chat function
function adminStartChat(userId, name, email) {
  const ticketId = 'user_' + userId;
  activeTicketId = ticketId;
  // Ensure the ticket exists by creating it (admin only)
  (async () => {
    try {
      await fetch('/api/support/create', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ targetUserId: userId })
      });
    } catch (e) {
      console.error('Failed to create admin chat ticket', e);
    }
    // Открываем модалку чата
    document.getElementById('chatHeaderName').textContent = name;
    document.getElementById('chatHeaderEmail').textContent = email || 'Гость';
    document.getElementById('chatHeaderAvatar').textContent = (name || '?').charAt(0).toUpperCase();
    document.getElementById('supportChatModalOverlay').classList.add('active');
    loadSupportMessages(ticketId);
    startTicketChatStream(ticketId);
    loadSupportTickets();
    setTimeout(() => document.getElementById('replyMessageInput')?.focus(), 50);
  })();
}

async function loadSupportMessages(ticketId, isPolling = false) {
  try {
    const res = await fetch(`/api/support/messages?ticketId=${ticketId}`);
    if (res.ok) {
      const data = await res.json();
      currentChatMessages = data.messages || [];
      activeTicketStatus = data.status || 'open';
      renderTicketStatusButton();
      renderChatMessages(currentChatMessages, isPolling);

      // Mark as read
      await fetch('/api/support/read', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ticketId })
      });
      // Сразу обновляем бейдж меню (диалог прочитан).
      if (!isPolling) updateSupportBadge();
    }
  } catch (err) {}
}

function renderChatMessages(messages, isPolling) {
  const container = document.getElementById('chatMessages');
  if (!container) return;
  const wasAtBottom = container.scrollHeight - container.scrollTop <= container.clientHeight + 10;
  
  if (!messages.length) {
    container.innerHTML = '<div style="text-align:center; color:hsl(var(--text-muted));">Здесь пока нет сообщений.</div>';
    return;
  }
  
  let html = '';
  messages.forEach(msg => {
    const isAdmin = msg.sender_role === 'Admin' || msg.sender_role === 'Superadmin';
    const align = isAdmin ? 'flex-end' : 'flex-start';
    const bg = isAdmin ? 'linear-gradient(135deg, var(--accent-purple), var(--accent-cyan))' : 'rgba(255,255,255,0.05)';
    const color = isAdmin ? '#fff' : 'inherit';
    const name = isAdmin ? (msg.name || 'Admin') : (msg.name || 'Гость');
    const time = new Date(msg.created_at).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    
    html += `
      <div style="display: flex; flex-direction: column; align-items: ${align};">
        <div style="font-size: 11px; color: hsl(var(--text-muted)); margin-bottom: 4px;">${escapeHtml(name)} • ${time}</div>
        <div style="background: ${bg}; color: ${color}; padding: 10px 14px; border-radius: 14px; max-width: 80%; border: 1px solid ${isAdmin ? 'transparent' : 'hsl(var(--border-color))'}; word-break: break-word;">
          ${escapeHtml(msg.message)}
        </div>
      </div>
    `;
  });
  
  container.innerHTML = html;
  
  if (!isPolling || wasAtBottom) {
    container.scrollTop = container.scrollHeight;
  }
}

const replyForm = document.getElementById('replyForm');
if (replyForm) {
  replyForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!activeTicketId) return;
    
    const input = document.getElementById('replyMessageInput');
    const message = input.value.trim();
    if (!message) return;
    
    input.disabled = true;
    try {
      const res = await fetch('/api/support/reply', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ticketId: activeTicketId, message })
      });
      if (res.ok) {
        input.value = '';
        await loadSupportMessages(activeTicketId);
      } else {
        showToast('Ошибка при отправке', 'error');
      }
    } catch(err) {
      showToast('Ошибка сети', 'error');
    } finally {
      input.disabled = false;
      input.focus();
    }
  });
}

// Закрытие модалки чата поддержки по клику на фон и по Escape
(function () {
  const overlay = document.getElementById('supportChatModalOverlay');
  if (!overlay) return;
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeSupportChat(); });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && overlay.classList.contains('active')) closeSupportChat();
  });
})();

// --- Общая модалка просмотра строки (для мобильных) ---
// rows: массив [метка, значение, mode?]. Пустые значения пропускаются
// (кроме mode === 'section', для которого значение не нужно).
// mode: true — значение вставляется как HTML; 'section' — заголовок группы
// (без значения); 'block' — метка сверху + значение снизу на всю ширину
// (для длинного текста типа обоснования/комментария).
function rowDetailHtml(rows) {
  return rows
    .filter(r => r[2] === 'section' || (r[1] !== undefined && r[1] !== null && r[1] !== ''))
    .map(([k, v, mode]) => {
      if (mode === 'section') {
        return `<div style="font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:.03em;color:hsl(var(--text-muted));margin:18px 0 6px;">${escapeHtml(k)}</div>`;
      }
      if (mode === 'block') {
        return `<div style="padding:2px 0 14px;">
          <div style="font-size:13px;color:hsl(var(--text-muted));margin-bottom:4px;">${escapeHtml(k)}</div>
          <div style="font-size:14px;white-space:pre-wrap;word-break:break-word;">${escapeHtml(String(v))}</div>
        </div>`;
      }
      return `
      <div style="display:flex;justify-content:space-between;gap:12px;padding:10px 0;border-bottom:1px solid hsl(var(--border-color));">
        <span style="color:hsl(var(--text-muted));font-size:13px;flex-shrink:0;">${escapeHtml(k)}</span>
        <span style="font-size:14px;text-align:right;word-break:break-word;">${mode ? v : escapeHtml(String(v))}</span>
      </div>`;
    }).join('');
}
window.showRowDetail = (title, rows, actionsHtml = '') => {
  document.getElementById('rowDetailTitle').textContent = title;
  document.getElementById('rowDetailBody').innerHTML = rowDetailHtml(rows);
  const act = document.getElementById('rowDetailActions');
  act.innerHTML = actionsHtml;
  act.style.display = actionsHtml ? '' : 'none';
  document.getElementById('rowDetailModalOverlay').classList.add('active');
};
window.closeRowDetail = () => document.getElementById('rowDetailModalOverlay').classList.remove('active');

// Открывает модалку только на мобильных (для tap по строке-карточке).
function mobileRowTap(handler) {
  return (ev) => {
    if (ev.target.closest('.action-btn, button, a')) return;
    if (window.matchMedia('(max-width: 640px)').matches) handler();
  };
}

// Бейдж непрочитанных сообщений поддержки в меню.
// count — суммарное число непрочитанных сообщений по всем диалогам.
function setSupportBadge(count) {
  const badge = document.getElementById('supportBadge');
  if (!badge) return;
  if (count > 0) { badge.textContent = count > 99 ? '99+' : count; badge.style.display = ''; }
  else badge.style.display = 'none';
}

async function updateSupportBadge() {
  try {
    const res = await fetch('/api/support/tickets');
    if (!res.ok) return; // не админ или ошибка — молча выходим
    const data = await res.json();
    const total = (data.tickets || []).reduce((s, t) => s + (t.unread_count || 0), 0);
    setSupportBadge(total);
  } catch (e) { /* тихо */ }
}

// API: Dashboard stats loader (для главной страницы «Статьи»)
async function loadDashboardStats() {
  try {
    const res = await fetch('/api/dashboard/stats');
    if (res.ok) {
      const data = await res.json();
      const u = document.getElementById('stat-users');
      const a = document.getElementById('stat-articles');
      const m = document.getElementById('stat-media');
      if (u) u.textContent = data.users || 0;
      if (a) a.textContent = data.articles || 0;
      if (m) m.textContent = data.mediaFiles || 0;
    }
  } catch (err) {
    console.error('Failed to load dashboard stats:', err);
  }
}

// API: Articles loader
async function loadArticles() {
  try {
    const res = await fetch('/api/crud/articles');
    if (res.ok) {
      articles = await res.json();
      renderArticles();
    }
  } catch (err) {
    showToast('Ошибка загрузки статей', 'error');
  }
}

function renderArticles(filterQuery = '') {
  const tbody = document.getElementById('articlesTableBody');
  tbody.innerHTML = '';

  const filtered = articles.filter(art =>
    art.title.toLowerCase().includes(filterQuery) ||
    (art.content && art.content.toLowerCase().includes(filterQuery))
  );

  if (filtered.length === 0) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="5" class="empty-state" style="text-align: center; color: hsl(var(--text-muted)); padding: 30px;">Записи не найдены</td></tr>`;
    return;
  }

  filtered.forEach(art => {
    const tr = document.createElement('tr');
    tr.onclick = mobileRowTap(() => editArticle(art.id));

    const statusBadge = art.status === 'published'
      ? `<span class="badge badge-success">Опубликовано</span>`
      : `<span class="badge badge-warning">Черновик</span>`;

    const dateFormatted = new Date(art.created_at).toLocaleDateString('ru-RU', {
      day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit'
    });

    tr.innerHTML = `
      <td class="hide-mobile">${art.id}</td>
      <td class="mobile-primary"><strong>${escapeHtml(art.title)}</strong></td>
      <td>${statusBadge}</td>
      <td class="mobile-hidden">${dateFormatted}</td>
      <td class="no-label" style="text-align: right;">
        <div class="action-btns" style="justify-content: flex-end;">
          <button class="action-btn edit" onclick="editArticle(${art.id})"><i data-lucide="edit-3"></i></button>
          <button class="action-btn delete" onclick="deleteArticle(${art.id})"><i data-lucide="trash-2"></i></button>
        </div>
      </td>
    `;
    tbody.appendChild(tr);
  });

  lucide.createIcons();
}

window.editArticle = (id) => {
  const art = articles.find(a => a.id === id);
  if (art) {
    const modal = document.getElementById('articleModalOverlay');
    document.getElementById('modalTitle').textContent = 'Редактировать статью';
    document.getElementById('articleId').value = art.id;
    document.getElementById('articleTitle').value = art.title;
    document.getElementById('articleStatus').value = art.status;

    // Properly populate Quill (or fallback hidden input)
    if (quill) {
      quill.root.innerHTML = art.content || '';
    } else {
      document.getElementById('articleContent').value = art.content || '';
    }

    modal.classList.add('active');
  }
};

window.deleteArticle = async (id) => {
  if (await confirmDialog('Вы действительно хотите удалить эту статью?', { okText: 'Удалить', danger: true })) {
    try {
      const res = await fetch(`/api/crud/articles?id=${id}`, { method: 'DELETE' });
      if (res.ok) {
        showToast('Статья успешно удалена', 'success');
        await loadArticles();
      } else {
        showToast('Не удалось удалить статью', 'error');
      }
    } catch (err) {
      showToast('Ошибка запроса на удаление', 'error');
    }
  }
};

window.showMediaFolders = () => {
  document.getElementById('mediaFoldersGrid').style.display = 'grid';
  document.getElementById('mediaFilesView').style.display = 'none';
  document.getElementById('mediaSearch').value = '';
  const avPanel = document.getElementById('standardAvatarsPanel');
  if (avPanel) avPanel.style.display = 'none';
};

window.openMediaCategory = (category, title) => {
  document.getElementById('mediaCategorySelect').value = category;
  document.getElementById('uploadCategorySelect').value = category;
  document.getElementById('currentMediaCategoryTitle').textContent = title;

  document.getElementById('mediaFoldersGrid').style.display = 'none';
  document.getElementById('mediaFilesView').style.display = 'block';

  // Раздел «Стандартные аватары» показываем только внутри папки «Аватары».
  const avPanel = document.getElementById('standardAvatarsPanel');
  if (avPanel) {
    // Показываем только кнопку; аватары грузятся при открытии модалки.
    avPanel.style.display = category === 'avatars' ? 'block' : 'none';
  }

  loadMedia();
};

// Модалка со стандартными аватарами: открытие грузит набор, закрытие прячет.
window.openStandardAvatarsModal = () => {
  const overlay = document.getElementById('standardAvatarsModalOverlay');
  if (!overlay) return;
  overlay.classList.add('active');
  loadStandardAvatars();
};

window.closeStandardAvatarsModal = () => {
  const overlay = document.getElementById('standardAvatarsModalOverlay');
  if (overlay) overlay.classList.remove('active');
};

// Показывает предустановленные аватары в медиатеке (папка «Аватары»).
async function loadStandardAvatars() {
  const grid = document.getElementById('standardAvatarsGrid');
  if (!grid) return;
  grid.innerHTML = '<div style="grid-column:1/-1; color:hsl(var(--text-muted)); font-size:13px;">Загрузка...</div>';
  try {
    const res = await fetch('/api/standard-avatars');
    const data = await res.json();
    const avatars = (data && data.avatars) || [];
    grid.innerHTML = '';
    if (!avatars.length) {
      grid.innerHTML = '<div style="grid-column:1/-1; color:hsl(var(--text-muted)); font-size:13px;">Пусто</div>';
      return;
    }
    avatars.forEach(a => {
      const card = document.createElement('div');
      card.style.cssText = 'display:flex; flex-direction:column; align-items:center; gap:6px;';
      card.innerHTML = `
        <img src="${a.url}" title="${escapeHtml(a.name)}" style="width:64px; height:64px; border-radius:50%; border:1px solid hsl(var(--border-color));">
        <button onclick="copyMediaUrl('${a.url}', event)" style="background:none;border:1px solid hsl(var(--border-color));color:hsl(var(--text-secondary));padding:2px 6px;font-size:11px;border-radius:4px;cursor:pointer;">URL</button>
      `;
      grid.appendChild(card);
    });
  } catch (e) {
    grid.innerHTML = '<div style="grid-column:1/-1; color:hsl(var(--text-muted));">Ошибка загрузки</div>';
  }
}

(function wireClickOutsideForOverlays() {
  const map = {
    sendNotificationModalOverlay: () => window.closeSendNotificationModal && window.closeSendNotificationModal(),
    notificationSettingsModalOverlay: () => window.closeNotificationSettingsModal && window.closeNotificationSettingsModal(),
    chatSettingsModalOverlay: () => window.closeChatSettingsModal && window.closeChatSettingsModal(),
    standardAvatarsModalOverlay: () => window.closeStandardAvatarsModal && window.closeStandardAvatarsModal(),
    rowDetailModalOverlay: () => window.closeRowDetail && window.closeRowDetail()
  };
  Object.entries(map).forEach(([id, close]) => {
    const overlay = document.getElementById(id);
    if (!overlay) return;
    overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
  });
})();
