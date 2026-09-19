// Раздел «Что умеет мод»: плитка открывает окно с пояснением.
//
// Текст окна лежит в самой странице, в скрытом блоке .feature-texts, а не
// приезжает запросом: так его видит поисковик и человек без скриптов, а окно
// открывается мгновенно и работает при упавшем сервере.
(function () {
  const modal = document.getElementById('featureModal');
  const body = document.getElementById('featureModalBody');
  if (!modal || !body) return;

  const box = modal.querySelector('.site-modal__box');
  let opener = null;

  function close() {
    if (!modal.classList.contains('open')) return;
    modal.classList.remove('open');
    body.innerHTML = '';
    if (opener) {
      opener.focus();
      opener = null;
    }
  }

  function open(card) {
    const source = document.getElementById('feature-' + card.dataset.feature);
    if (!source) return;
    body.innerHTML = source.innerHTML;
    opener = card;
    modal.classList.add('open');
    if (box) box.scrollTop = 0;
    // Иконки внутри окна — это <i data-lucide>, их надо перерисовать после вставки.
    if (window.lucide) window.lucide.createIcons();
    const closeBtn = modal.querySelector('.site-modal__close');
    if (closeBtn) closeBtn.focus();
  }

  document.addEventListener('click', (event) => {
    const card = event.target.closest('.feature-card');
    if (card) {
      open(card);
      return;
    }
    if (event.target === modal || event.target.closest('.site-modal__close')) close();
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') close();
  });
})();
