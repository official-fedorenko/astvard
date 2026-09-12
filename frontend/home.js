(async function init() {
  renderNav(document.getElementById('nav'), await getCurrentUser());

  // The list is public on purpose: /api/servers asks for no session, and a player
  // deciding whether to come back should not have to sign in to see who is online.
  try {
    const { servers } = await apiFetch('/api/servers');
    renderServers(document.getElementById('servers-list'), servers);
  } catch {
    document.getElementById('servers-list').textContent = 'Статус серверов недоступен.';
  }
})();
