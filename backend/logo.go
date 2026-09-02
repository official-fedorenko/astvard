package main

import (
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// uploads/ лежит рядом с backend/frontend/db, а не внутри frontend/ — деплой
// делает "git pull" поверх frontend/, и мы не хотим, чтобы загруженный логотип
// затёрся или попал в репозиторий (см. .gitignore)
const uploadsDir = "../uploads"
const maxLogoSize = 3 << 20 // 3 МБ

var allowedLogoExts = map[string]bool{
	".svg": true, ".png": true, ".jpg": true, ".jpeg": true,
	".gif": true, ".webp": true, ".ico": true,
}

// removeUploadedLogo удаляет файл прошлого загруженного логотипа, если он был
// (а не дефолтный /logo.svg из фронтенда) — иначе на диске копится мусор
func removeUploadedLogo(pool *pgxpool.Pool, r *http.Request) {
	settings, err := loadSettings(pool, r)
	if err != nil {
		return
	}
	if old, ok := settings["logo_url"]; ok && strings.HasPrefix(old, "/uploads/") {
		os.Remove(filepath.Join(uploadsDir, strings.TrimPrefix(old, "/uploads/")))
	}
}

// handleUploadLogo — superadmin: сохраняет файл под уникальным именем (метка
// времени в имени — чтобы браузеры не кэшировали старую картинку под тем же
// URL после замены) и кладёт путь в settings.logo_url. Рендерится на сайте
// всегда через <img src="...">, поэтому даже вредоносный SVG со скриптом
// внутри не опасен — браузер не исполняет script внутри SVG, загруженного как <img>.
func handleUploadLogo(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLogoSize)
		if err := r.ParseMultipartForm(maxLogoSize); err != nil {
			writeError(w, http.StatusBadRequest, "Файл слишком большой (макс. 3 МБ) или некорректный запрос")
			return
		}

		file, header, err := r.FormFile("logo")
		if err != nil {
			writeError(w, http.StatusBadRequest, "Файл не передан")
			return
		}
		defer file.Close()

		ext := strings.ToLower(filepath.Ext(header.Filename))
		if !allowedLogoExts[ext] {
			writeError(w, http.StatusBadRequest, "Разрешены только SVG, PNG, JPG, GIF, WEBP, ICO")
			return
		}

		if err := os.MkdirAll(uploadsDir, 0755); err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		filename := fmt.Sprintf("logo-%d%s", time.Now().UnixNano(), ext)
		dst, err := os.Create(filepath.Join(uploadsDir, filename))
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer dst.Close()

		if _, err := io.Copy(dst, file); err != nil {
			writeError(w, http.StatusInternalServerError, "Не удалось сохранить файл")
			return
		}

		removeUploadedLogo(pool, r)

		newURL := "/uploads/" + filename
		if _, err := pool.Exec(r.Context(), `
			INSERT INTO settings (key, value, updated_at) VALUES ('logo_url', $1, now())
			ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
		`, newURL); err != nil {
			writeError(w, http.StatusInternalServerError, "Не удалось сохранить настройки")
			return
		}

		writeJSON(w, http.StatusOK, map[string]string{"logoUrl": newURL})
	}
}

// handleResetLogo — superadmin: возвращает дефолтный логотип, удаляет загруженный файл
func handleResetLogo(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		removeUploadedLogo(pool, r)

		if _, err := pool.Exec(r.Context(), "DELETE FROM settings WHERE key = 'logo_url'"); err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"logoUrl": settingDefaults["logo_url"]})
	}
}
