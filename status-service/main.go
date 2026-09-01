package main

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/joho/godotenv"
)

type server struct {
	ID                  int
	Host                string
	Port                int
	Name                string
	GameSlug            string
	DockerContainerName *string
}

func main() {
	envPath := filepath.Join("..", ".env")
	if err := godotenv.Load(envPath); err != nil {
		fmt.Println("предупреждение: не смог загрузить .env:", err)
	}

	dsn := fmt.Sprintf("postgres://%s:%s@localhost:5432/%s",
		os.Getenv("POSTGRES_USER"), os.Getenv("POSTGRES_PASSWORD"), os.Getenv("POSTGRES_DB"))

	pool, err := pgxpool.New(context.Background(), dsn)
	if err != nil {
		panic(err)
	}
	defer pool.Close()

	fmt.Println("status-service запущен, опрос каждые 30 секунд")

	go func() {
		for {
			checkAllServers(pool)
			time.Sleep(30 * time.Second)
		}
	}()

	// внутренний HTTP — только на localhost, чтобы бэкенд мог попросить
	// перепроверить сервера прямо сейчас, не дожидаясь следующего цикла
	statusPort := os.Getenv("STATUS_PORT")
	if statusPort == "" {
		statusPort = "3002"
	}
	mux := http.NewServeMux()
	mux.HandleFunc("POST /check-all", func(w http.ResponseWriter, r *http.Request) {
		checkAllServers(pool)
		w.WriteHeader(http.StatusOK)
	})
	addr := "127.0.0.1:" + statusPort
	fmt.Println("status-service внутренний HTTP:", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		panic(err)
	}
}

func checkAllServers(pool *pgxpool.Pool) {
	ctx := context.Background()

	rows, err := pool.Query(ctx, `
		SELECT servers.id, servers.host, servers.port, servers.name, games.slug, servers.docker_container_name
		FROM servers
		JOIN games ON games.id = servers.game_id
	`)
	if err != nil {
		fmt.Println("ошибка чтения servers:", err)
		return
	}

	var servers []server
	for rows.Next() {
		var s server
		if err := rows.Scan(&s.ID, &s.Host, &s.Port, &s.Name, &s.GameSlug, &s.DockerContainerName); err != nil {
			fmt.Println("ошибка чтения строки:", err)
			continue
		}
		servers = append(servers, s)
	}
	rows.Close()

	for _, s := range servers {
		online, playersOnline, playersMax, reportedName, playerNames := checkServer(s)

		_, err := pool.Exec(ctx,
			`INSERT INTO server_status (server_id, online, players_online, players_max, reported_name, player_names)
			 VALUES ($1, $2, $3, $4, $5, $6)`,
			s.ID, online, playersOnline, playersMax, reportedName, playerNames,
		)
		if err != nil {
			fmt.Println("ошибка записи статуса:", err)
			continue
		}

		msg := fmt.Sprintf("%s (%s:%d) -> online=%v", s.Name, s.Host, s.Port, online)
		if playersOnline != nil {
			msg += fmt.Sprintf(", players=%d/%d", *playersOnline, *playersMax)
		}
		if reportedName != nil {
			msg += fmt.Sprintf(", реальное имя=%q", *reportedName)
		}
		if len(playerNames) > 0 {
			msg += fmt.Sprintf(", игроки=%v", playerNames)
		}
		fmt.Println(msg)
	}
}

func checkServer(s server) (online bool, playersOnline, playersMax *int, reportedName *string, playerNames []string) {
	switch s.GameSlug {
	case "valheim":
		// у Valheim (образ lloesche/valheim-server) query-порт A2S — это игровой порт + 1
		info, err := queryA2SInfo(s.Host, s.Port+1, 3*time.Second)
		if err != nil {
			return false, nil, nil, nil, nil
		}
		p, m := info.Players, info.MaxPlayers
		// A2S_PLAYER у Valheim всегда отдаёт пустое имя (ограничение самой игры,
		// проверено вживую) — реальные ники есть только в логах контейнера
		// ("Got character ZDOID from <ник>"), поэтому достаём их оттуда
		var names []string
		if s.DockerContainerName != nil {
			names = queryValheimPlayerNamesFromLogs(*s.DockerContainerName, info.Players)
		}
		return true, &p, &m, &info.Name, names
	default:
		// для остальных игр пока держим грубую TCP-проверку — см. isReachable
		return isReachable(s.Host, s.Port), nil, nil, nil, nil
	}
}

var characterJoinRe = regexp.MustCompile(`Got character ZDOID from (\S+) :`)

// queryValheimPlayerNamesFromLogs достаёт ники игроков из лога контейнера —
// A2S_PLAYER для Valheim имена не отдаёт, а сервер сам их логирует при заходе
// персонажа в мир. Берём count последних уникальных имён (count — реальное
// число игроков из A2S_INFO), чтобы не показывать тех, кто уже отключился.
func queryValheimPlayerNamesFromLogs(containerName string, count int) []string {
	if containerName == "" || count <= 0 {
		return nil
	}
	out, err := exec.Command("docker", "logs", "--tail", "500", containerName).CombinedOutput()
	if err != nil {
		return nil
	}

	matches := characterJoinRe.FindAllSubmatch(out, -1)
	names := make([]string, 0, count)
	seen := make(map[string]bool)
	for i := len(matches) - 1; i >= 0 && len(names) < count; i-- {
		name := string(matches[i][1])
		if seen[name] {
			continue
		}
		seen[name] = true
		names = append(names, name)
	}
	// разворачиваем обратно в порядок захода (от старых к новым)
	for i, j := 0, len(names)-1; i < j; i, j = i+1, j-1 {
		names[i], names[j] = names[j], names[i]
	}
	return names
}

// Это грубая TCP-проверка доступности, ещё не настоящий игровой протокол —
// не умеет считать игроков и не подходит для UDP-серверов (например Valheim).
// Следующий шаг — заменить на протокол-специфичные запросы (A2S для Valheim/CS2,
// свой протокол пинга у Minecraft).
func isReachable(host string, port int) bool {
	address := fmt.Sprintf("%s:%d", host, port)
	conn, err := net.DialTimeout("tcp", address, 3*time.Second)
	if err != nil {
		return false
	}
	conn.Close()
	return true
}
