package main

import (
	"net/http"
	"os"
	"path/filepath"
)

// cleanURLFileServer оборачивает http.FileServer: если запросили путь без
// расширения (например "/admin") и такого файла нет, но есть "admin.html" —
// подставляет расширение сам. Так ссылки выглядят чище (/admin вместо
// /admin.html), а реальные файлы (style.css, script.js) продолжают отдаваться
// как обычно, их это не касается.
func cleanURLFileServer(dir string) http.Handler {
	fileServer := http.FileServer(http.Dir(dir))

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" && filepath.Ext(r.URL.Path) == "" {
			htmlPath := filepath.Join(dir, r.URL.Path+".html")
			if info, err := os.Stat(htmlPath); err == nil && !info.IsDir() {
				r.URL.Path += ".html"
			}
		}
		fileServer.ServeHTTP(w, r)
	})
}
