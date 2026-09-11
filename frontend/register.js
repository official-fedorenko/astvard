showSteamError('error');

document.getElementById('register-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target;
  const errorEl = document.getElementById('error');
  errorEl.textContent = '';

  const body = {
    nickname: form.nickname.value,
    email: form.email.value,
    password: form.password.value,
  };

  try {
    await apiFetch('/api/register', { method: 'POST', body: JSON.stringify(body) });
    window.location.href = '/cabinet.html';
  } catch (err) {
    errorEl.textContent = err.message;
  }
});
