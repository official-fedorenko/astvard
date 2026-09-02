package main

import (
	"encoding/json"
	"net/http"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// handleAdminStats — короткая сводка для панели в кабинете (карточки с цифрами)
func handleAdminStats(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var users, servers, articles, onlineServers int
		err := pool.QueryRow(r.Context(), `
			SELECT
			  (SELECT COUNT(*) FROM users),
			  (SELECT COUNT(*) FROM servers),
			  (SELECT COUNT(*) FROM articles),
			  (SELECT COUNT(*)
			     FROM servers s
			     JOIN LATERAL (
			       SELECT online FROM server_status st
			       WHERE st.server_id = s.id
			       ORDER BY checked_at DESC LIMIT 1
			     ) latest ON true
			     WHERE latest.online)
		`).Scan(&users, &servers, &articles, &onlineServers)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		writeJSON(w, http.StatusOK, map[string]int{
			"users": users, "servers": servers, "articles": articles, "onlineServers": onlineServers,
		})
	}
}

type adminUser struct {
	ID         int       `json:"id"`
	Nickname   string    `json:"nickname"`
	FirstName  *string   `json:"firstName"`
	LastName   *string   `json:"lastName"`
	Email      *string   `json:"email"`
	AvatarURL  *string   `json:"avatarUrl"`
	Role       string    `json:"role"`
	AuthMethod string    `json:"authMethod"`
	CreatedAt  time.Time `json:"createdAt"`
}

// handleListUsers — список всех пользователей для superadmin, чтобы менять роли
func handleListUsers(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		rows, err := pool.Query(r.Context(), `
			SELECT id, nickname, first_name, last_name, email, avatar_url,
			       role, created_at, (steam_id IS NOT NULL) AS via_steam
			FROM users
			ORDER BY created_at DESC
		`)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer rows.Close()

		users := []adminUser{}
		for rows.Next() {
			var u adminUser
			var viaSteam bool
			if err := rows.Scan(&u.ID, &u.Nickname, &u.FirstName, &u.LastName, &u.Email, &u.AvatarURL,
				&u.Role, &u.CreatedAt, &viaSteam); err != nil {
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
				return
			}
			if viaSteam {
				u.AuthMethod = "steam"
			} else {
				u.AuthMethod = "email"
			}
			users = append(users, u)
		}
		writeJSON(w, http.StatusOK, users)
	}
}

type roleRequest struct {
	Role string `json:"role"`
}

// handleUpdateUserRole — только superadmin (см. requireRole на роуте): обычному admin
// нельзя доверять назначение других admin/superadmin, это была бы дыра для самоповышения
func handleUpdateUserRole(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req roleRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}
		if req.Role != "player" && req.Role != "admin" && req.Role != "superadmin" {
			writeError(w, http.StatusBadRequest, "Роль должна быть player, admin или superadmin")
			return
		}

		tag, err := pool.Exec(r.Context(), "UPDATE users SET role = $1 WHERE id = $2", req.Role, id)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Пользователь не найден")
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}
