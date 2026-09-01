package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

var slugRe = regexp.MustCompile(`^[a-z0-9-]{2,60}$`)

type articleRequest struct {
	Title   string `json:"title"`
	Slug    string `json:"slug"`
	Content string `json:"content"`
}

type serverRequest struct {
	GameID      int    `json:"gameId"`
	Host        string `json:"host"`
	Port        int    `json:"port"`
	Description string `json:"description"`
}

func handleCreateArticle(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var req articleRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		title := strings.TrimSpace(req.Title)
		slug := strings.TrimSpace(req.Slug)
		content := strings.TrimSpace(req.Content)

		if title == "" || content == "" {
			writeError(w, http.StatusBadRequest, "Нужны title и content")
			return
		}
		if !slugRe.MatchString(slug) {
			writeError(w, http.StatusBadRequest, "Slug: 2-60 символов, латиница в нижнем регистре, цифры, дефис")
			return
		}

		authorID := claimsFromContext(r).UserID

		var id int
		var createdAt time.Time
		err := pool.QueryRow(r.Context(),
			"INSERT INTO articles (title, slug, content, author_id) VALUES ($1, $2, $3, $4) RETURNING id, created_at",
			title, slug, content, authorID,
		).Scan(&id, &createdAt)

		if err != nil {
			if uniqueViolationField(err) == "articles_slug_key" {
				writeError(w, http.StatusConflict, "Статья с таким slug уже есть")
				return
			}
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		writeJSON(w, http.StatusCreated, map[string]any{
			"id": id, "title": title, "slug": slug, "content": content, "created_at": createdAt,
		})
	}
}

func handleUpdateArticle(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req articleRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		title := strings.TrimSpace(req.Title)
		slug := strings.TrimSpace(req.Slug)
		content := strings.TrimSpace(req.Content)

		if title == "" || content == "" {
			writeError(w, http.StatusBadRequest, "Нужны title и content")
			return
		}
		if !slugRe.MatchString(slug) {
			writeError(w, http.StatusBadRequest, "Slug: 2-60 символов, латиница в нижнем регистре, цифры, дефис")
			return
		}

		tag, err := pool.Exec(r.Context(),
			"UPDATE articles SET title = $1, slug = $2, content = $3 WHERE id = $4",
			title, slug, content, id,
		)
		if err != nil {
			if uniqueViolationField(err) == "articles_slug_key" {
				writeError(w, http.StatusConflict, "Статья с таким slug уже есть")
				return
			}
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Статья не найдена")
			return
		}

		writeJSON(w, http.StatusOK, map[string]any{"id": id, "title": title, "slug": slug, "content": content})
	}
}

func handleDeleteArticle(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		tag, err := pool.Exec(r.Context(), "DELETE FROM articles WHERE id = $1", id)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Статья не найдена")
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

// Имя сервера не спрашиваем у админа — сразу после создания его подтянет
// status-service из реального ответа сервера (reported_name). До первого опроса
// (максимум ~30 сек) показываем как временную заглушку "host:port".
func validateServerRequest(req serverRequest) (host, description string, ok bool, errMsg string) {
	host = strings.TrimSpace(req.Host)
	description = strings.TrimSpace(req.Description)

	if req.GameID == 0 || host == "" {
		return "", "", false, "Нужны gameId и host"
	}
	if req.Port < 1 || req.Port > 65535 {
		return "", "", false, "Port должен быть от 1 до 65535"
	}
	return host, description, true, ""
}

func handleCreateServer(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var req serverRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		host, description, ok, errMsg := validateServerRequest(req)
		if !ok {
			writeError(w, http.StatusBadRequest, errMsg)
			return
		}
		name := fmt.Sprintf("%s:%d", host, req.Port)

		var id int
		var createdAt time.Time
		err := pool.QueryRow(r.Context(),
			`INSERT INTO servers (game_id, name, host, port, description)
			 VALUES ($1, $2, $3, $4, $5) RETURNING id, created_at`,
			req.GameID, name, host, req.Port, description,
		).Scan(&id, &createdAt)

		if err != nil {
			switch uniqueViolationField(err) {
			case "servers_host_port_key":
				writeError(w, http.StatusConflict, "Сервер с таким host:port уже есть")
			default:
				if isForeignKeyViolation(err) {
					writeError(w, http.StatusBadRequest, "Такой игры не существует")
					return
				}
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			}
			return
		}

		writeJSON(w, http.StatusCreated, map[string]any{
			"id": id, "gameId": req.GameID, "name": name, "host": host, "port": req.Port,
			"description": description, "created_at": createdAt,
		})
	}
}

func handleUpdateServer(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req serverRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		host, description, ok, errMsg := validateServerRequest(req)
		if !ok {
			writeError(w, http.StatusBadRequest, errMsg)
			return
		}
		name := fmt.Sprintf("%s:%d", host, req.Port)

		tag, err := pool.Exec(r.Context(),
			`UPDATE servers SET game_id = $1, name = $2, host = $3, port = $4, description = $5
			 WHERE id = $6`,
			req.GameID, name, host, req.Port, description, id,
		)
		if err != nil {
			switch uniqueViolationField(err) {
			case "servers_host_port_key":
				writeError(w, http.StatusConflict, "Сервер с таким host:port уже есть")
			default:
				if isForeignKeyViolation(err) {
					writeError(w, http.StatusBadRequest, "Такой игры не существует")
					return
				}
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			}
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		writeJSON(w, http.StatusOK, map[string]any{
			"id": id, "gameId": req.GameID, "name": name, "host": host, "port": req.Port, "description": description,
		})
	}
}

func handleDeleteServer(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		// server_status для этого сервера удалится сам (ON DELETE CASCADE)
		tag, err := pool.Exec(r.Context(), "DELETE FROM servers WHERE id = $1", id)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

// handleRefreshServers просит status-service (отдельный процесс) перепроверить
// все сервера прямо сейчас, вместо того чтобы ждать следующего цикла (до 30 сек)
func handleRefreshServers(w http.ResponseWriter, r *http.Request) {
	statusPort := os.Getenv("STATUS_PORT")
	if statusPort == "" {
		statusPort = "3002"
	}

	client := http.Client{Timeout: 10 * time.Second}
	resp, err := client.Post(fmt.Sprintf("http://127.0.0.1:%s/check-all", statusPort), "", nil)
	if err != nil {
		writeError(w, http.StatusBadGateway, "status-service недоступен")
		return
	}
	defer resp.Body.Close()

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}
