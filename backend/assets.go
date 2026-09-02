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
// делает "git pull" поверх frontend/, и мы не хотим, чтобы загруженные файлы
// затёрлись или попали в репозиторий (см. .gitignore)
const uploadsDir = "../uploads"
const maxAssetSize = 3 << 20 // 3 МБ

var allowedAssetExts = map[string]bool{
	".svg": true, ".png": true, ".jpg": true, ".jpeg": true,
	".gif": true, ".webp": true, ".ico": true,
}

// removeUploadedAsset удаляет файл прошлой загруженной картинки, если она была
// (а не дефолтный /logo.svg из фронтенда) — иначе на диске копится мусор
func removeUploadedAsset(pool *pgxpool.Pool, r *http.Request, settingsKey string) {
	settings, err := loadSettings(pool, r)
	if err != nil {
		return
	}
	if old, ok := settings[settingsKey]; ok && strings.HasPrefix(old, "/uploads/") {
		os.Remove(filepath.Join(uploadsDir, strings.TrimPrefix(old, "/uploads/")))
	}
}

// handleUploadAsset — superadmin: загружает картинку сайта (логотип, фавиконка —
// kind определяет и ключ настройки settings.<kind>_url, и префикс имени файла на
// диске). Сохраняет под уникальным именем (метка времени — чтобы браузеры не
// кэшировали старую картинку под тем же URL после замены). Рендерится на сайте
// всегда через <img>/<link>, поэтому даже вредоносный SVG со скриптом внутри не
// опасен — браузер не исполняет script внутри SVG, когда он подключен так, а не
// открыт как отдельный документ.
func handleUploadAsset(pool *pgxpool.Pool, kind string) http.HandlerFunc {
	settingsKey := kind + "_url"
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxAssetSize)
		if err := r.ParseMultipartForm(maxAssetSize); err != nil {
			writeError(w, http.StatusBadRequest, "Файл слишком большой (макс. 3 МБ) или некорректный запрос")
			return
		}

		file, header, err := r.FormFile("file")
		if err != nil {
			writeError(w, http.StatusBadRequest, "Файл не передан")
			return
		}
		defer file.Close()

		ext := strings.ToLower(filepath.Ext(header.Filename))
		if !allowedAssetExts[ext] {
			writeError(w, http.StatusBadRequest, "Разрешены только SVG, PNG, JPG, GIF, WEBP, ICO")
			return
		}

		if err := os.MkdirAll(uploadsDir, 0755); err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		filename := fmt.Sprintf("%s-%d%s", kind, time.Now().UnixNano(), ext)
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

		removeUploadedAsset(pool, r, settingsKey)

		newURL := "/uploads/" + filename
		if _, err := pool.Exec(r.Context(), `
			INSERT INTO settings (key, value, updated_at) VALUES ($1, $2, now())
			ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
		`, settingsKey, newURL); err != nil {
			writeError(w, http.StatusInternalServerError, "Не удалось сохранить настройки")
			return
		}

		writeJSON(w, http.StatusOK, map[string]string{"url": newURL})
	}
}

// handleResetAsset — superadmin: возвращает дефолтную картинку, удаляет загруженный файл
func handleResetAsset(pool *pgxpool.Pool, kind string) http.HandlerFunc {
	settingsKey := kind + "_url"
	return func(w http.ResponseWriter, r *http.Request) {
		removeUploadedAsset(pool, r, settingsKey)

		if _, err := pool.Exec(r.Context(), "DELETE FROM settings WHERE key = $1", settingsKey); err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"url": settingDefaults[settingsKey]})
	}
}
