renderNav();

const form = document.getElementById('register-form');
const message = document.getElementById('message');

form.addEventListener('submit', async (event) => {
  event.preventDefault(); // не даём браузеру перезагрузить страницу при отправке формы

  const nickname = document.getElementById('nickname').value;
  const firstName = document.getElementById('firstName').value;
  const lastName = document.getElementById('lastName').value;
  const email = document.getElementById('email').value;
  const password = document.getElementById('password').value;
  const passwordConfirm = document.getElementById('passwordConfirm').value;
  const website = document.getElementById('website').value; // honeypot

  if (password !== passwordConfirm) {
    message.textContent = 'Пароли не совпадают';
    message.style.color = '#e66';
    return;
  }

  message.textContent = 'Отправка...';

  try {
    const response = await fetch('/api/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        nickname,
        firstName,
        lastName,
        email,
        password,
        passwordConfirm,
        website,
      }),
    });

    const data = await response.json();

    if (!response.ok) {
      message.textContent = data.error;
      message.style.color = '#e66';
      return;
    }

    // сразу логиним новым аккаунтом, чтобы не заставлять вводить пароль второй раз
    const loginResponse = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ identifier: email, password }),
    });

    if (loginResponse.ok) {
      window.location.href = 'cabinet';
      return;
    }

    message.textContent = 'Аккаунт создан, но автоматический вход не удался — попробуй войти вручную.';
    message.style.color = '#e66';
  } catch (err) {
    message.textContent = 'Не удалось связаться с сервером';
    message.style.color = '#e66';
  }
});
