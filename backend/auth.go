package main

import (
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/mail"
	"regexp"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/bcrypt"
)

type authRequest struct {
	Identifier string `json:"identifier"` // email или ник
	Password   string `json:"password"`
}

type registerRequest struct {
	Nickname        string `json:"nickname"`
	FirstName       string `json:"firstName"`
	LastName        string `json:"lastName"`
	Email           string `json:"email"`
	Password        string `json:"password"`
	PasswordConfirm string `json:"passwordConfirm"`
	// скрытое поле-ловушка для ботов: реальный человек его не видит и не заполняет,
	// а простые боты, автоматически заполняющие все поля формы, попадаются на нём
	Website string `json:"website"`
}

var nicknameRe = regexp.MustCompile(`^[a-zA-Z0-9_-]{3,20}$`)

func writeJSON(w http.ResponseWriter, status int, data any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(data)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}

// За nginx r.RemoteAddr — это всегда сам nginx (127.0.0.1), а не реальный посетитель,
// поэтому сначала смотрим на X-Real-IP, который nginx проставляет из настоящего адреса
// (см. proxy_set_header в конфиге). Если заголовка нет — значит идём напрямую (локальная
// разработка), тогда берём r.RemoteAddr как раньше.
func clientIP(r *http.Request) string {
	if ip := r.Header.Get("X-Real-IP"); ip != "" {
		return ip
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// возвращает имя нарушенного UNIQUE-ограничения (например "users_email_key"),
// или "" если это вообще не ошибка нарушения уникальности
func uniqueViolationField(err error) string {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == "23505" {
		return pgErr.ConstraintName
	}
	return ""
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

		var req registerRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		// honeypot: поле должно быть пустым у настоящего человека
		if req.Website != "" {
			writeError(w, http.StatusBadRequest, "Регистрация не удалась")
			return
		}

		nickname := strings.TrimSpace(req.Nickname)
		firstName := strings.TrimSpace(req.FirstName)
		lastName := strings.TrimSpace(req.LastName)
		email := strings.ToLower(strings.TrimSpace(req.Email))

		if !nicknameRe.MatchString(nickname) {
			writeError(w, http.StatusBadRequest, "Ник должен быть 3-20 символов: латинские буквы, цифры, _ или -")
			return
		}
		if firstName == "" || lastName == "" {
			writeError(w, http.StatusBadRequest, "Нужны имя и фамилия")
			return
		}
		if _, err := mail.ParseAddress(email); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный email")
			return
		}
		if len(req.Password) < 8 {
			writeError(w, http.StatusBadRequest, "Пароль должен быть не короче 8 символов")
			return
		}
		if req.Password != req.PasswordConfirm {
			writeError(w, http.StatusBadRequest, "Пароли не совпадают")
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
			`INSERT INTO users (nickname, first_name, last_name, email, password_hash)
			 VALUES ($1, $2, $3, $4, $5) RETURNING id, created_at`,
			nickname, firstName, lastName, email, string(hash),
		).Scan(&id, &createdAt)

		if err != nil {
			switch uniqueViolationField(err) {
			case "users_nickname_key":
				writeError(w, http.StatusConflict, "Этот ник уже занят")
			case "users_email_key":
				writeError(w, http.StatusConflict, "Этот email уже зарегистрирован")
			default:
				writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			}
			return
		}

		writeJSON(w, http.StatusCreated, map[string]any{
			"id": id, "nickname": nickname, "email": email, "created_at": createdAt,
		})
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

		identifier := strings.TrimSpace(req.Identifier)
		if identifier == "" || req.Password == "" {
			writeError(w, http.StatusBadRequest, "Нужны email/ник и password")
			return
		}

		invalid := func() { writeError(w, http.StatusUnauthorized, "Неверный email/ник или пароль") }

		var userID int
		var nickname, email string
		var passwordHash string
		// identifier сравниваем и как email (без учёта регистра), и как ник (тоже без учёта
		// регистра) — не знаем заранее, что именно ввёл пользователь
		err := pool.QueryRow(r.Context(),
			`SELECT id, nickname, email, password_hash FROM users
			 WHERE email = LOWER($1) OR LOWER(nickname) = LOWER($1)`,
			identifier,
		).Scan(&userID, &nickname, &email, &passwordHash)

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

		token, err := signToken(userID, email, nickname)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}

		setAuthCookie(w, token, 7*24*time.Hour)
		writeJSON(w, http.StatusOK, map[string]string{"email": email, "nickname": nickname})
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

	writeJSON(w, http.StatusOK, map[string]string{"email": claims.Email, "nickname": claims.Nickname})
}
