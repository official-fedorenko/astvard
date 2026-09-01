package main

import (
	"fmt"
	"time"
)

// временный ручной тест A2S против реального Valheim-сервера, потом удалить
func main() {
	info, err := queryA2SInfoTest("72.61.139.115", 2457, 5*time.Second)
	if err != nil {
		fmt.Println("error:", err)
		return
	}
	fmt.Printf("name=%q players=%d/%d\n", info.Name, info.Players, info.MaxPlayers)
}
