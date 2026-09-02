package main

import (
	"net/http"
	"os"
	"path/filepath"
	"strings"
)

// cleanURLFileServer оборачивает http.FileServer: если запросили путь без
// расширения (например "/admin") и такого файла нет, но есть "admin.html" —
// подставляет расширение сам. Так ссылки выглядят чище (/admin вместо
// /admin.html), а реальные файлы (style.css, script.js) продолжают отдаваться
// как обычно, их это не касается.
//
// Заодно выставляет Cache-Control. "no-cache" — не «не кэшировать», а
// «кэшировать, но перед использованием спросить сервер, не изменился ли файл»:
// браузер шлёт If-Modified-Since, http.FileServer сравнивает с mtime файла и
// отвечает пустым 304, если тот не менялся. Итог: после деплоя все сразу видят
// свежую версию, а повторные заходы не качают файлы заново. Долгий max-age
// имеет смысл только с версионированными именами (style.abc123.css) — пока нет.
func cleanURLFileServer(dir string) http.Handler {
	fileServer := http.FileServer(http.Dir(dir))

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" && filepath.Ext(r.URL.Path) == "" {
			htmlPath := filepath.Join(dir, r.URL.Path+".html")
			if info, err := os.Stat(htmlPath); err == nil && !info.IsDir() {
				r.URL.Path += ".html"
			}
		}

		switch strings.ToLower(filepath.Ext(r.URL.Path)) {
		case "", ".html", ".css", ".js":
			w.Header().Set("Cache-Control", "no-cache")
		case ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".woff", ".woff2":
			// картинки и шрифты меняются редко — сутки без перепроверки
			w.Header().Set("Cache-Control", "public, max-age=86400")
		}

		fileServer.ServeHTTP(w, r)
	})
}
