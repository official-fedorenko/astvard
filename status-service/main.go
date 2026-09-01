package main

import (
	"context"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/joho/godotenv"
)

type server struct {
	ID       int
	Host     string
	Port     int
	Name     string
	GameSlug string
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

	for {
		checkAllServers(pool)
		time.Sleep(30 * time.Second)
	}
}

func checkAllServers(pool *pgxpool.Pool) {
	ctx := context.Background()

	rows, err := pool.Query(ctx, `
		SELECT servers.id, servers.host, servers.port, servers.name, games.slug
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
		if err := rows.Scan(&s.ID, &s.Host, &s.Port, &s.Name, &s.GameSlug); err != nil {
			fmt.Println("ошибка чтения строки:", err)
			continue
		}
		servers = append(servers, s)
	}
	rows.Close()

	for _, s := range servers {
		online, playersOnline, playersMax := checkServer(s)

		_, err := pool.Exec(ctx,
			"INSERT INTO server_status (server_id, online, players_online, players_max) VALUES ($1, $2, $3, $4)",
			s.ID, online, playersOnline, playersMax,
		)
		if err != nil {
			fmt.Println("ошибка записи статуса:", err)
			continue
		}

		msg := fmt.Sprintf("%s (%s:%d) -> online=%v", s.Name, s.Host, s.Port, online)
		if playersOnline != nil {
			msg += fmt.Sprintf(", players=%d/%d", *playersOnline, *playersMax)
		}
		fmt.Println(msg)
	}
}

func checkServer(s server) (online bool, playersOnline, playersMax *int) {
	switch s.GameSlug {
	case "valheim":
		// у Valheim (образ lloesche/valheim-server) query-порт A2S — это игровой порт + 1
		info, err := queryA2SInfo(s.Host, s.Port+1, 3*time.Second)
		if err != nil {
			return false, nil, nil
		}
		p, m := info.Players, info.MaxPlayers
		return true, &p, &m
	default:
		// для остальных игр пока держим грубую TCP-проверку — см. isReachable
		return isReachable(s.Host, s.Port), nil, nil
	}
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
