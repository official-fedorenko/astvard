package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

const steamOpenIDEndpoint = "https://steamcommunity.com/openid/login"

var steamIDRe = regexp.MustCompile(`^https://steamcommunity\.com/openid/id/(\d+)$`)
var nicknameSanitizeRe = regexp.MustCompile(`[^a-zA-Z0-9_-]`)

// requestOrigin восстанавливает "https://astvard.online" (или "http://localhost:3000"
// на локалке) по заголовкам запроса — nginx на проде передаёт исходный протокол
// через X-Forwarded-Proto, сам Go-процесс за ним TLS не видит
func requestOrigin(r *http.Request) string {
	scheme := "http"
	if r.Header.Get("X-Forwarded-Proto") == "https" || r.TLS != nil {
		scheme = "https"
	}
	return scheme + "://" + r.Host
}

func handleSteamLogin(w http.ResponseWriter, r *http.Request) {
	origin := requestOrigin(r)

	params := url.Values{
		"openid.ns":         {"http://specs.openid.net/auth/2.0"},
		"openid.mode":       {"checkid_setup"},
		"openid.return_to":  {origin + "/auth/steam/callback"},
		"openid.realm":      {origin + "/"},
		"openid.identity":   {"http://specs.openid.net/auth/2.0/identifier_select"},
		"openid.claimed_id": {"http://specs.openid.net/auth/2.0/identifier_select"},
	}

	http.Redirect(w, r, steamOpenIDEndpoint+"?"+params.Encode(), http.StatusFound)
}

func handleSteamCallback(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		steamID, err := verifySteamCallback(r)
		if err != nil {
			http.Redirect(w, r, "/login?error=steam", http.StatusFound)
			return
		}

		var userID int
		var nickname, email, role string
		err = pool.QueryRow(r.Context(),
			"SELECT id, nickname, COALESCE(email, ''), role FROM users WHERE steam_id = $1", steamID,
		).Scan(&userID, &nickname, &email, &role)

		if errors.Is(err, pgx.ErrNoRows) {
			userID, nickname, role, err = createSteamUser(r, pool, steamID)
		}
		if err != nil {
			http.Redirect(w, r, "/login?error=steam", http.StatusFound)
			return
		}

		token, err := signToken(userID, email, nickname, role)
		if err != nil {
			http.Redirect(w, r, "/login?error=steam", http.StatusFound)
			return
		}

		setAuthCookie(w, token, 7*24*time.Hour)
		http.Redirect(w, r, "/cabinet", http.StatusFound)
	}
}

// verifySteamCallback перепроверяет ответ Steam у самого Steam (openid.mode=check_authentication) —
// без этого шага кто угодно мог бы прислать нам поддельные параметры и залогиниться чужим SteamID
func verifySteamCallback(r *http.Request) (steamID string, err error) {
	claimedID := r.URL.Query().Get("openid.claimed_id")
	match := steamIDRe.FindStringSubmatch(claimedID)
	if match == nil {
		return "", errors.New("некорректный claimed_id")
	}

	verifyParams := url.Values{}
	for key, values := range r.URL.Query() {
		verifyParams[key] = values
	}
	verifyParams.Set("openid.mode", "check_authentication")

	resp, err := http.PostForm(steamOpenIDEndpoint, verifyParams)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", err
	}
	if !strings.Contains(string(body), "is_valid:true") {
		return "", errors.New("steam не подтвердил ответ")
	}

	return match[1], nil
}

func createSteamUser(r *http.Request, pool *pgxpool.Pool, steamID string) (id int, nickname, role string, err error) {
	nickname = steamPersonaName(steamID)
	if nickname == "" {
		nickname = "steam_" + steamID[len(steamID)-6:]
	}

	err = pool.QueryRow(r.Context(),
		"INSERT INTO users (nickname, steam_id) VALUES ($1, $2) RETURNING id, role",
		nickname, steamID,
	).Scan(&id, &role)

	if uniqueViolationField(err) == "users_nickname_key" {
		// ник уже занят кем-то другим — добавляем кусок SteamID для уникальности
		nickname = nickname + "_" + steamID[len(steamID)-4:]
		err = pool.QueryRow(r.Context(),
			"INSERT INTO users (nickname, steam_id) VALUES ($1, $2) RETURNING id, role",
			nickname, steamID,
		).Scan(&id, &role)
	}

	return id, nickname, role, err
}

// steamPersonaName запрашивает имя профиля через Steam Web API — работает только
// если задан STEAM_API_KEY; без ключа просто возвращает "" и мы берём ник по умолчанию
func steamPersonaName(steamID string) string {
	if steamAPIKey == "" {
		return ""
	}

	apiURL := fmt.Sprintf(
		"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key=%s&steamids=%s",
		steamAPIKey, steamID,
	)
	resp, err := http.Get(apiURL)
	if err != nil {
		return ""
	}
	defer resp.Body.Close()

	var result struct {
		Response struct {
			Players []struct {
				PersonaName string `json:"personaname"`
			} `json:"players"`
		} `json:"response"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return ""
	}
	if len(result.Response.Players) == 0 {
		return ""
	}

	sanitized := nicknameSanitizeRe.ReplaceAllString(result.Response.Players[0].PersonaName, "")
	if len(sanitized) > 20 {
		sanitized = sanitized[:20]
	}
	if len(sanitized) < 3 {
		return ""
	}
	return sanitized
}
