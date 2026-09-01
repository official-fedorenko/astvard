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
	ID   int
	Host string
	Port int
	Name string
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

	rows, err := pool.Query(ctx, "SELECT id, host, port, name FROM servers")
	if err != nil {
		fmt.Println("ошибка чтения servers:", err)
		return
	}

	var servers []server
	for rows.Next() {
		var s server
		if err := rows.Scan(&s.ID, &s.Host, &s.Port, &s.Name); err != nil {
			fmt.Println("ошибка чтения строки:", err)
			continue
		}
		servers = append(servers, s)
	}
	rows.Close()

	for _, s := range servers {
		online := isReachable(s.Host, s.Port)

		_, err := pool.Exec(ctx,
			"INSERT INTO server_status (server_id, online) VALUES ($1, $2)",
			s.ID, online,
		)
		if err != nil {
			fmt.Println("ошибка записи статуса:", err)
			continue
		}
		fmt.Printf("%s (%s:%d) -> online=%v\n", s.Name, s.Host, s.Port, online)
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
