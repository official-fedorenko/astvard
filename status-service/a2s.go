package main

import (
	"bytes"
	"errors"
	"fmt"
	"net"
	"time"
)

// Протокол A2S (Source Engine Query) — по нему опрашиваются Valheim (через Steam-обвязку)
// и любые Source-игры типа CS2. Работает по UDP, в отличие от нашей старой TCP-проверки.
type a2sInfo struct {
	Name       string
	Players    int
	MaxPlayers int
}

func queryA2SInfo(host string, port int, timeout time.Duration) (*a2sInfo, error) {
	addr := fmt.Sprintf("%s:%d", host, port)
	conn, err := net.DialTimeout("udp", addr, timeout)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(timeout))

	request := a2sInfoRequest(nil)
	if _, err := conn.Write(request); err != nil {
		return nil, err
	}

	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		return nil, err
	}
	data := buf[:n]

	// сервер часто сначала отвечает "challenge" (заголовок 'A') — просим повторить
	// запрос с этим challenge-номером, прежде чем отдать реальные данные
	if len(data) > 4 && data[4] == 0x41 {
		challenge := data[5:9]
		if _, err := conn.Write(a2sInfoRequest(challenge)); err != nil {
			return nil, err
		}
		n, err = conn.Read(buf)
		if err != nil {
			return nil, err
		}
		data = buf[:n]
	}

	return parseA2SInfo(data)
}

func a2sInfoRequest(challenge []byte) []byte {
	request := append([]byte{0xFF, 0xFF, 0xFF, 0xFF, 0x54}, []byte("Source Engine Query\x00")...)
	return append(request, challenge...)
}

// queryA2SPlayers — отдельный запрос A2S_PLAYER, возвращает ники игроков онлайн.
// В отличие от A2S_INFO (общие цифры), тут сервер почти всегда сначала шлёт
// challenge — запрашиваем его и повторяем запрос уже с ним.
func queryA2SPlayers(host string, port int, timeout time.Duration) ([]string, error) {
	addr := fmt.Sprintf("%s:%d", host, port)
	conn, err := net.DialTimeout("udp", addr, timeout)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(timeout))

	buf := make([]byte, 4096)

	if _, err := conn.Write(a2sPlayerRequest(nil)); err != nil {
		return nil, err
	}
	n, err := conn.Read(buf)
	if err != nil {
		return nil, err
	}
	data := buf[:n]

	if len(data) > 4 && data[4] == 0x41 { // 'A' challenge — повторяем с ним
		challenge := data[5:9]
		if _, err := conn.Write(a2sPlayerRequest(challenge)); err != nil {
			return nil, err
		}
		n, err = conn.Read(buf)
		if err != nil {
			return nil, err
		}
		data = buf[:n]
	}

	return parseA2SPlayers(data)
}

func a2sPlayerRequest(challenge []byte) []byte {
	if challenge == nil {
		challenge = []byte{0xFF, 0xFF, 0xFF, 0xFF}
	}
	return append([]byte{0xFF, 0xFF, 0xFF, 0xFF, 0x55}, challenge...)
}

func parseA2SPlayers(data []byte) ([]string, error) {
	if len(data) < 6 || data[4] != 0x44 { // 'D' = A2S_PLAYER ответ
		return nil, errors.New("неожиданный ответ A2S_PLAYER")
	}

	count := int(data[5])
	names := make([]string, 0, count)
	i := 6
	for p := 0; p < count; p++ {
		if i >= len(data) {
			break
		}
		i++ // index игрока — не используем
		idx := bytes.IndexByte(data[i:], 0)
		if idx < 0 {
			return nil, errors.New("оборванное имя в ответе A2S_PLAYER")
		}
		names = append(names, string(data[i:i+idx]))
		i += idx + 1 + 4 + 4 // имя + score (int32) + duration (float32)
	}
	return names, nil
}

func parseA2SInfo(data []byte) (*a2sInfo, error) {
	if len(data) < 6 || data[4] != 0x49 { // 'I' = A2S_INFO ответ
		return nil, errors.New("неожиданный ответ A2S")
	}

	i := 6 // пропускаем заголовок (4 байта) + 'I' + байт версии протокола

	readString := func() (string, error) {
		idx := bytes.IndexByte(data[i:], 0)
		if idx < 0 {
			return "", errors.New("оборванная строка в ответе A2S")
		}
		s := string(data[i : i+idx])
		i += idx + 1
		return s, nil
	}

	name, err := readString()
	if err != nil {
		return nil, err
	}
	if _, err := readString(); err != nil { // map
		return nil, err
	}
	if _, err := readString(); err != nil { // folder
		return nil, err
	}
	if _, err := readString(); err != nil { // game
		return nil, err
	}

	if i+4 > len(data) {
		return nil, errors.New("обрезанный ответ A2S")
	}
	i += 2 // app id (не используем)
	players := int(data[i])
	maxPlayers := int(data[i+1])

	return &a2sInfo{Name: name, Players: players, MaxPlayers: maxPlayers}, nil
}
