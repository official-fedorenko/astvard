document.getElementById('login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target;
  const errorEl = document.getElementById('error');
  errorEl.textContent = '';

  const body = {
    email: form.email.value,
    password: form.password.value,
  };

  try {
    await apiFetch('/api/login', { method: 'POST', body: JSON.stringify(body) });
    window.location.href = '/cabinet.html';
  } catch (err) {
    errorEl.textContent = err.message;
  }
});
