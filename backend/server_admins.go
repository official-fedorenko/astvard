package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/jackc/pgx/v5/pgxpool"
)

// writeAdminlist переписывает adminlist.txt в томе Valheim-контейнера — сам дедикейт-сервер
// читает этот файл и даёт перечисленным SteamID доступ к консольным командам в игре
func writeAdminlist(volumePath string, steamIDs []string) error {
	content := strings.Join(steamIDs, "\n")
	if len(steamIDs) > 0 {
		content += "\n"
	}
	return os.WriteFile(filepath.Join(volumePath, "adminlist.txt"), []byte(content), 0644)
}

type dockerPathRequest struct {
	DockerVolumePath string `json:"dockerVolumePath"`
}

// handleSetServerDockerPath — только superadmin: привязывает сервер к реальному тому
// Docker-контейнера на VPS, чтобы можно было управлять его adminlist.txt
func handleSetServerDockerPath(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req dockerPathRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		path := strings.TrimSpace(req.DockerVolumePath)
		if path != "" && !strings.HasPrefix(path, "/var/lib/docker/volumes/") {
			writeError(w, http.StatusBadRequest, "Путь должен начинаться с /var/lib/docker/volumes/")
			return
		}

		var pathValue any
		if path != "" {
			pathValue = path
		}

		tag, err := pool.Exec(r.Context(), "UPDATE servers SET docker_volume_path = $1 WHERE id = $2", pathValue, id)
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

type serverAdmin struct {
	UserID   int    `json:"userId"`
	Nickname string `json:"nickname"`
	SteamID  string `json:"steamId"`
}

func handleListServerAdmins(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		serverID, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var volumePath *string
		if err := pool.QueryRow(r.Context(),
			"SELECT docker_volume_path FROM servers WHERE id = $1", serverID,
		).Scan(&volumePath); err != nil {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		rows, err := pool.Query(r.Context(), `
			SELECT users.id, users.nickname, users.steam_id
			FROM server_admins
			JOIN users ON users.id = server_admins.user_id
			WHERE server_admins.server_id = $1
			ORDER BY users.nickname
		`, serverID)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer rows.Close()

		admins := []serverAdmin{}
		for rows.Next() {
			var a serverAdmin
			if err := rows.Scan(&a.UserID, &a.Nickname, &a.SteamID); err != nil {
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
				return
			}
			admins = append(admins, a)
		}

		writeJSON(w, http.StatusOK, map[string]any{
			"dockerVolumePath": volumePath,
			"admins":           admins,
		})
	}
}

type addServerAdminRequest struct {
	UserID int `json:"userId"`
}

// resyncAdminlistFile перечитывает всех игровых админов сервера из базы и переписывает
// adminlist.txt в его томе — вызывается после каждого добавления/удаления
func resyncAdminlistFile(pool *pgxpool.Pool, r *http.Request, serverID int) error {
	var volumePath *string
	err := pool.QueryRow(r.Context(), "SELECT docker_volume_path FROM servers WHERE id = $1", serverID).Scan(&volumePath)
	if err != nil {
		return err
	}
	if volumePath == nil {
		// для этого сервера путь ещё не настроен — просто нечего синхронизировать
		return nil
	}

	rows, err := pool.Query(r.Context(), `
		SELECT users.steam_id FROM server_admins
		JOIN users ON users.id = server_admins.user_id
		WHERE server_admins.server_id = $1 AND users.steam_id IS NOT NULL
	`, serverID)
	if err != nil {
		return err
	}
	defer rows.Close()

	var steamIDs []string
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return err
		}
		steamIDs = append(steamIDs, id)
	}

	return writeAdminlist(*volumePath, steamIDs)
}

func handleAddServerAdmin(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		serverID, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req addServerAdminRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		var steamID *string
		err = pool.QueryRow(r.Context(), "SELECT steam_id FROM users WHERE id = $1", req.UserID).Scan(&steamID)
		if err != nil {
			writeError(w, http.StatusNotFound, "Пользователь не найден")
			return
		}
		if steamID == nil {
			writeError(w, http.StatusBadRequest, "У этого пользователя нет привязанного Steam-аккаунта")
			return
		}

		_, err = pool.Exec(r.Context(),
			"INSERT INTO server_admins (user_id, server_id) VALUES ($1, $2) ON CONFLICT DO NOTHING",
			req.UserID, serverID,
		)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		if err := resyncAdminlistFile(pool, r, serverID); err != nil {
			writeError(w, http.StatusInternalServerError, "Записано в базу, но не удалось обновить adminlist.txt: "+err.Error())
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

func handleRemoveServerAdmin(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		serverID, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id сервера")
			return
		}
		userID, err := strconv.Atoi(r.PathValue("userId"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id пользователя")
			return
		}

		_, err = pool.Exec(r.Context(),
			"DELETE FROM server_admins WHERE server_id = $1 AND user_id = $2", serverID, userID,
		)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		if err := resyncAdminlistFile(pool, r, serverID); err != nil {
			writeError(w, http.StatusInternalServerError, "Удалено из базы, но не удалось обновить adminlist.txt: "+err.Error())
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}
