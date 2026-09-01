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

func main() {
	envPath := filepath.Join("..", ".env")
	if err := godotenv.Load(envPath); err != nil {
		log.Println("предупреждение: не смог загрузить .env:", err)
	}

	jwtSecret = []byte(os.Getenv("JWT_SECRET"))
	isProduction = os.Getenv("APP_ENV") == "production"

	pool := connectDB()
	defer pool.Close()

	limiter := newRateLimiter()

	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/register", handleRegister(pool, limiter))
	mux.HandleFunc("POST /api/login", handleLogin(pool, limiter))
	mux.HandleFunc("POST /api/logout", handleLogout)
	mux.HandleFunc("GET /api/me", handleMe)
	mux.HandleFunc("GET /api/games", handleGetGames(pool))
	mux.HandleFunc("GET /api/servers", handleGetServers(pool))
	mux.HandleFunc("GET /api/articles", handleGetArticles(pool))

	// http.FileServer сам отдаёт index.html для "/" и сам защищён от path traversal —
	// то, что в Node мы писали руками (serveStatic), тут даёт стандартная библиотека
	frontendDir := filepath.Join("..", "frontend")
	mux.Handle("/", http.FileServer(http.Dir(frontendDir)))

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
