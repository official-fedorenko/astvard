const form = document.getElementById('login-form');
const message = document.getElementById('message');

if (new URLSearchParams(window.location.search).get('error') === 'steam') {
  message.textContent = 'Не удалось войти через Steam, попробуй ещё раз';
  message.style.color = '#e66';
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();

  const identifier = document.getElementById('identifier').value;
  const password = document.getElementById('password').value;

  message.textContent = 'Отправка...';

  try {
    const response = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ identifier, password }),
    });

    const data = await response.json();

    if (!response.ok) {
      message.textContent = data.error;
      message.style.color = '#e66';
      return;
    }

    window.location.href = 'cabinet';
  } catch (err) {
    message.textContent = 'Не удалось связаться с сервером';
    message.style.color = '#e66';
  }
});
