package main

import (
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/bcrypt"
)

type authRequest struct {
	Email    string `json:"email"`
	Password string `json:"password"`
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(data)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}

// r.RemoteAddr приходит в виде "1.2.3.4:54321" — порт нам не нужен, только сам IP
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

func isUniqueViolation(err error) bool {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code == "23505"
	}
	return false
}

func setAuthCookie(w http.ResponseWriter, token string, maxAge time.Duration) {
	http.SetCookie(w, &http.Cookie{
		Name:     "token",
		Value:    token,
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		MaxAge:   int(maxAge.Seconds()),
		Secure:   isProduction,
	})
}

func clearAuthCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:     "token",
		Value:    "",
		Path:     "/",
		HttpOnly: true,
		MaxAge:   -1,
		Secure:   isProduction,
	})
}

func handleRegister(pool *pgxpool.Pool, limiter *rateLimiter) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if limiter.isLimited("register:"+clientIP(r), 5, time.Minute) {
			writeError(w, http.StatusTooManyRequests, "Слишком много попыток, попробуй через минуту")
			return
		}

		var req authRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		email := strings.ToLower(strings.TrimSpace(req.Email))
		if email == "" || req.Password == "" {
			writeError(w, http.StatusBadRequest, "Нужны email и password")
			return
		}
		if len(req.Password) < 8 {
			writeError(w, http.StatusBadRequest, "Пароль должен быть не короче 8 символов")
			return
		}

		hash, err := bcrypt.GenerateFromPassword([]byte(req.Password), bcrypt.DefaultCost)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		var id int
		var createdAt time.Time
		err = pool.QueryRow(r.Context(),
			"INSERT INTO users (email, password_hash) VALUES ($1, $2) RETURNING id, created_at",
			email, string(hash),
		).Scan(&id, &createdAt)

		if err != nil {
			if isUniqueViolation(err) {
				writeError(w, http.StatusConflict, "Этот email уже зарегистрирован")
				return
			}
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		writeJSON(w, http.StatusCreated, map[string]any{"id": id, "email": email, "created_at": createdAt})
	}
}

func handleLogin(pool *pgxpool.Pool, limiter *rateLimiter) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if limiter.isLimited("login:"+clientIP(r), 5, time.Minute) {
			writeError(w, http.StatusTooManyRequests, "Слишком много попыток, попробуй через минуту")
			return
		}

		var req authRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		email := strings.ToLower(strings.TrimSpace(req.Email))
		if email == "" || req.Password == "" {
			writeError(w, http.StatusBadRequest, "Нужны email и password")
			return
		}

		invalid := func() { writeError(w, http.StatusUnauthorized, "Неверный email или пароль") }

		var userID int
		var passwordHash string
		err := pool.QueryRow(r.Context(),
			"SELECT id, password_hash FROM users WHERE email = $1", email,
		).Scan(&userID, &passwordHash)

		if err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				invalid()
				return
			}
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		if bcrypt.CompareHashAndPassword([]byte(passwordHash), []byte(req.Password)) != nil {
			invalid()
			return
		}

		token, err := signToken(userID, email)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		setAuthCookie(w, token, 7*24*time.Hour)
		writeJSON(w, http.StatusOK, map[string]string{"email": email})
	}
}

func handleLogout(w http.ResponseWriter, r *http.Request) {
	clearAuthCookie(w)
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func handleMe(w http.ResponseWriter, r *http.Request) {
	cookie, err := r.Cookie("token")
	if err != nil {
		writeError(w, http.StatusUnauthorized, "Не авторизован")
		return
	}

	claims, err := parseToken(cookie.Value)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "Не авторизован")
		return
	}

	writeJSON(w, http.StatusOK, map[string]string{"email": claims.Email})
}
