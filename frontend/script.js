renderNav();
loadServers();
loadArticles();

// заголовок и подзаголовок главной — из настроек проекта (админка)
loadSiteSettings().then((settings) => {
  if (settings.site_name) {
    document.getElementById('site-title').textContent = settings.site_name;
    document.title = settings.site_name;
  }
  if (settings.site_tagline != null) {
    document.getElementById('site-tagline').textContent = settings.site_tagline;
  }
});
