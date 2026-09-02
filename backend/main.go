package main

import (
	"log"
	"net/http"
	"os"
	"path/filepath"

	"github.com/joho/godotenv"
)

var jwtSecret []byte
var isProduction bool
var steamAPIKey string

func main() {
	envPath := filepath.Join("..", ".env")
	if err := godotenv.Load(envPath); err != nil {
		log.Println("предупреждение: не смог загрузить .env:", err)
	}

	jwtSecret = []byte(os.Getenv("JWT_SECRET"))
	isProduction = os.Getenv("APP_ENV") == "production"
	steamAPIKey = os.Getenv("STEAM_API_KEY")

	pool := connectDB()
	defer pool.Close()

	limiter := newRateLimiter()

	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/register", handleRegister(pool, limiter))
	mux.HandleFunc("POST /api/login", handleLogin(pool, limiter))
	mux.HandleFunc("POST /api/logout", handleLogout)
	mux.HandleFunc("GET /api/me", handleMe(pool))
	mux.HandleFunc("GET /api/games", handleGetGames(pool))
	mux.HandleFunc("GET /api/servers", handleGetServers(pool))
	mux.HandleFunc("GET /api/articles", handleGetArticles(pool))
	mux.HandleFunc("GET /api/settings", handleGetSettings(pool))
	mux.HandleFunc("PUT /api/admin/settings", requireRole("superadmin", handleUpdateSettings(pool)))

	mux.HandleFunc("POST /api/admin/articles", requireRole("admin", handleCreateArticle(pool)))
	mux.HandleFunc("PUT /api/admin/articles/{id}", requireRole("admin", handleUpdateArticle(pool)))
	mux.HandleFunc("DELETE /api/admin/articles/{id}", requireRole("admin", handleDeleteArticle(pool)))
	mux.HandleFunc("POST /api/admin/servers", requireRole("admin", handleCreateServer(pool)))
	mux.HandleFunc("PUT /api/admin/servers/{id}", requireRole("admin", handleUpdateServer(pool)))
	mux.HandleFunc("DELETE /api/admin/servers/{id}", requireRole("admin", handleDeleteServer(pool)))
	mux.HandleFunc("POST /api/admin/servers/refresh", requireRole("admin", handleRefreshServers))
	mux.HandleFunc("GET /api/admin/stats", requireRole("admin", handleAdminStats(pool)))
	mux.HandleFunc("GET /api/admin/users", requireRole("superadmin", handleListUsers(pool)))
	mux.HandleFunc("PUT /api/admin/users/{id}/role", requireRole("superadmin", handleUpdateUserRole(pool)))

	mux.HandleFunc("PUT /api/admin/servers/{id}/docker-path", requireRole("superadmin", handleSetServerDockerPath(pool)))
	mux.HandleFunc("GET /api/admin/servers/{id}/admins", requireRole("superadmin", handleListServerAdmins(pool)))
	mux.HandleFunc("POST /api/admin/servers/{id}/admins", requireRole("superadmin", handleAddServerAdmin(pool)))
	mux.HandleFunc("DELETE /api/admin/servers/{id}/admins/{userId}", requireRole("superadmin", handleRemoveServerAdmin(pool)))

	mux.HandleFunc("GET /api/admin/servers/{id}/infra", requireRole("superadmin", handleGetServerInfra(pool)))
	mux.HandleFunc("PUT /api/admin/servers/{id}/infra", requireRole("superadmin", handleSetServerInfra(pool)))
	mux.HandleFunc("PUT /api/admin/servers/{id}/password", requireRole("superadmin", handleSetServerPassword(pool)))

	mux.HandleFunc("GET /auth/steam/login", handleSteamLogin)
	mux.HandleFunc("GET /auth/steam/callback", handleSteamCallback(pool))

	// http.FileServer сам отдаёт index.html для "/" и сам защищён от path traversal —
	// то, что в Node мы писали руками (serveStatic), тут даёт стандартная библиотека.
	// cleanURLFileServer сверху добавляет красивые пути (/admin вместо /admin.html)
	frontendDir := filepath.Join("..", "frontend")
	mux.Handle("/", cleanURLFileServer(frontendDir))

	port := os.Getenv("PORT")
	if port == "" {
		port = "3000"
	}
	// слушаем только localhost — снаружи сервер виден через nginx (он проксирует сюда),
	// напрямую с публичного интерфейса достучаться до Go-процесса нельзя
	addr := "127.0.0.1:" + port

	log.Println("Astvard server running:", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Fatal(err)
	}
}
