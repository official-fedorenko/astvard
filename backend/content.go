package main

import (
	"net/http"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

type game struct {
	ID          int     `json:"id"`
	Name        string  `json:"name"`
	Slug        string  `json:"slug"`
	Description *string `json:"description"`
}

func handleGetGames(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		rows, err := pool.Query(r.Context(), "SELECT id, name, slug, description FROM games ORDER BY name")
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer rows.Close()

		games := []game{}
		for rows.Next() {
			var g game
			if err := rows.Scan(&g.ID, &g.Name, &g.Slug, &g.Description); err != nil {
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
				return
			}
			games = append(games, g)
		}
		writeJSON(w, http.StatusOK, games)
	}
}

type serverWithStatus struct {
	ID            int        `json:"id"`
	Name          string     `json:"name"`
	Host          string     `json:"host"`
	Port          int        `json:"port"`
	Description   *string    `json:"description"`
	GameName      string     `json:"game_name"`
	GameSlug      string     `json:"game_slug"`
	Online        *bool      `json:"online"`
	PlayersOnline *int       `json:"players_online"`
	PlayersMax    *int       `json:"players_max"`
	ReportedName    *string    `json:"reported_name"`
	PlayerNames     []string   `json:"player_names"`
	CheckedAt       *time.Time `json:"checked_at"`
	UptimePercent   *float64   `json:"uptime_percent"`
	PeakPlayers     *int       `json:"peak_players"`
	ConnectPassword *string    `json:"connect_password"`
}

func handleGetServers(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		rows, err := pool.Query(r.Context(), `
			SELECT servers.id, servers.name, servers.host, servers.port, servers.description,
			       servers.connect_password,
			       games.name AS game_name, games.slug AS game_slug,
			       latest.online, latest.players_online, latest.players_max,
			       latest.reported_name, latest.player_names, latest.checked_at,
			       stats.uptime_percent, stats.peak_players
			FROM servers
			JOIN games ON games.id = servers.game_id
			LEFT JOIN LATERAL (
			  SELECT online, players_online, players_max, reported_name, player_names, checked_at
			  FROM server_status
			  WHERE server_status.server_id = servers.id
			  ORDER BY checked_at DESC
			  LIMIT 1
			) latest ON true
			LEFT JOIN LATERAL (
			  -- аптайм и пик игроков за последние 24 часа (по накопленной истории опроса)
			  SELECT
			    ROUND(100.0 * COUNT(*) FILTER (WHERE online) / NULLIF(COUNT(*), 0), 1) AS uptime_percent,
			    MAX(players_online) AS peak_players
			  FROM server_status
			  WHERE server_status.server_id = servers.id
			    AND checked_at > now() - interval '24 hours'
			) stats ON true
			ORDER BY servers.name
		`)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer rows.Close()

		servers := []serverWithStatus{}
		for rows.Next() {
			var s serverWithStatus
			if err := rows.Scan(&s.ID, &s.Name, &s.Host, &s.Port, &s.Description, &s.ConnectPassword,
				&s.GameName, &s.GameSlug, &s.Online, &s.PlayersOnline, &s.PlayersMax,
				&s.ReportedName, &s.PlayerNames, &s.CheckedAt, &s.UptimePercent, &s.PeakPlayers); err != nil {
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
				return
			}
			servers = append(servers, s)
		}
		writeJSON(w, http.StatusOK, servers)
	}
}

type article struct {
	ID        int       `json:"id"`
	Title     string    `json:"title"`
	Slug      string    `json:"slug"`
	Content   string    `json:"content"`
	CreatedAt time.Time `json:"created_at"`
}

func handleGetArticles(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		rows, err := pool.Query(r.Context(),
			"SELECT id, title, slug, content, created_at FROM articles ORDER BY created_at DESC")
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		defer rows.Close()

		articles := []article{}
		for rows.Next() {
			var a article
			if err := rows.Scan(&a.ID, &a.Title, &a.Slug, &a.Content, &a.CreatedAt); err != nil {
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
				return
			}
			articles = append(articles, a)
		}
		writeJSON(w, http.StatusOK, articles)
	}
}
