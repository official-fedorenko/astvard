package main

import (
	"encoding/json"
	"net/http"
	"strings"

	"github.com/jackc/pgx/v5/pgxpool"
)

// Какие ключи вообще существуют и что подставлять, пока в базе пусто.
// Новое поле = одна строка тут + поле в форме админки, без миграций.
var settingDefaults = map[string]string{
	"site_name":    "Astvard",
	"site_tagline": "Портал наших игровых серверов.",
	"footer_text":  "",
}

const settingMaxLen = 500

func loadSettings(pool *pgxpool.Pool, r *http.Request) (map[string]string, error) {
	result := make(map[string]string, len(settingDefaults))
	for k, v := range settingDefaults {
		result[k] = v
	}

	rows, err := pool.Query(r.Context(), "SELECT key, value FROM settings")
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	for rows.Next() {
		var k, v string
		if err := rows.Scan(&k, &v); err != nil {
			return nil, err
		}
		if _, known := settingDefaults[k]; known {
			result[k] = v
		}
	}
	return result, nil
}

// handleGetSettings — публичный: шапка/футер/главная читают отсюда название и тексты
func handleGetSettings(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		settings, err := loadSettings(pool, r)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		writeJSON(w, http.StatusOK, settings)
	}
}

// handleUpdateSettings — superadmin: принимает объект {ключ: значение}, неизвестные
// ключи игнорирует, известные — записывает (INSERT ... ON CONFLICT = upsert)
func handleUpdateSettings(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var req map[string]string
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		for key, value := range req {
			if _, known := settingDefaults[key]; !known {
				continue
			}
			value = strings.TrimSpace(value)
			if len([]rune(value)) > settingMaxLen {
				writeError(w, http.StatusBadRequest, "Значение слишком длинное (макс. 500 символов)")
				return
			}
			if _, err := pool.Exec(r.Context(), `
				INSERT INTO settings (key, value, updated_at) VALUES ($1, $2, now())
				ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
			`, key, value); err != nil {
				writeError(w, http.StatusInternalServerError, "Не удалось сохранить настройки")
				return
			}
		}

		settings, err := loadSettings(pool, r)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		writeJSON(w, http.StatusOK, settings)
	}
}
