package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os/exec"
	"regexp"
	"strconv"
	"strings"

	"github.com/jackc/pgx/v5/pgxpool"
)

var dockerNameRe = regexp.MustCompile(`^[a-zA-Z0-9_.-]{1,64}$`)

type serverInfra struct {
	GameSlug            string  `json:"gameSlug"`
	DockerContainerName *string `json:"dockerContainerName"`
	DockerWorldName     *string `json:"dockerWorldName"`
	DockerVolumePath    *string `json:"dockerVolumePath"`
	ConnectPassword     *string `json:"connectPassword"`
	IsPublic            bool    `json:"isPublic"`
	WorldSeed           *string `json:"worldSeed"`
}

// handleGetServerInfra — только superadmin: реальное состояние сервера — вся информация,
// нужная панели "Изменить сервер", одним запросом (gameSlug — решить, поддерживается ли
// управление контейнером вообще; остальное — что уже настроено)
func handleGetServerInfra(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var infra serverInfra
		err = pool.QueryRow(r.Context(), `
			SELECT games.slug, servers.docker_container_name, servers.docker_world_name,
			       servers.docker_volume_path, servers.connect_password, servers.is_public, servers.world_seed
			FROM servers JOIN games ON games.id = servers.game_id
			WHERE servers.id = $1
		`, id).Scan(&infra.GameSlug, &infra.DockerContainerName, &infra.DockerWorldName,
			&infra.DockerVolumePath, &infra.ConnectPassword, &infra.IsPublic, &infra.WorldSeed)
		if err != nil {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		writeJSON(w, http.StatusOK, infra)
	}
}

type infraConfigRequest struct {
	DockerContainerName string `json:"dockerContainerName"`
	DockerWorldName     string `json:"dockerWorldName"`
	DockerVolumePath    string `json:"dockerVolumePath"`
	WorldSeed           string `json:"worldSeed"`
}

// handleSetServerInfra — только метаданные (имя контейнера/мира/путь к тому/сид), без
// пересоздания контейнера — нужно настроить один раз перед первым применением пароля.
// Путь к тому — тот же, что использует раздел "Игровые админы" для adminlist.txt
// (одно и то же поле servers.docker_volume_path, настраивается тут в одном месте).
func handleSetServerInfra(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req infraConfigRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}

		containerName := strings.TrimSpace(req.DockerContainerName)
		worldName := strings.TrimSpace(req.DockerWorldName)
		if !dockerNameRe.MatchString(containerName) || !dockerNameRe.MatchString(worldName) {
			writeError(w, http.StatusBadRequest, "Имя контейнера/мира: латиница, цифры, - _ ., без пробелов")
			return
		}

		volumePath := strings.TrimSpace(req.DockerVolumePath)
		if volumePath != "" && !strings.HasPrefix(volumePath, "/var/lib/docker/volumes/") {
			writeError(w, http.StatusBadRequest, "Путь к тому должен начинаться с /var/lib/docker/volumes/")
			return
		}
		var volumePathValue any
		if volumePath != "" {
			volumePathValue = volumePath
		}

		worldSeed := strings.TrimSpace(req.WorldSeed)
		if len(worldSeed) > 100 {
			writeError(w, http.StatusBadRequest, "Сид слишком длинный (макс. 100 символов)")
			return
		}
		var worldSeedValue any
		if worldSeed != "" {
			worldSeedValue = worldSeed
		}

		tag, err := pool.Exec(r.Context(),
			"UPDATE servers SET docker_container_name = $1, docker_world_name = $2, docker_volume_path = $3, world_seed = $4 WHERE id = $5",
			containerName, worldName, volumePathValue, worldSeedValue, id,
		)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Внутренняя ошибка сервера")
			return
		}
		if tag.RowsAffected() == 0 {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

type setPasswordRequest struct {
	Password string `json:"password"`
	IsPublic bool   `json:"isPublic"`
}

// handleSetServerPassword — реально пересоздаёт Docker-контейнер Valheim с новым паролем
// и видимостью (SERVER_PASS/PUBLIC — переменные окружения, их нельзя поменять на уже
// запущенном контейнере). Сервер на несколько секунд уходит в оффлайн, игроки отвалятся.
func handleSetServerPassword(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var req setPasswordRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный JSON")
			return
		}
		// пароль обязателен только для публичных серверов (Steam так требует),
		// приватный сервер (не виден в браузере серверов) может быть вообще без пароля
		if req.IsPublic && len(req.Password) < 5 {
			writeError(w, http.StatusBadRequest, "Для публичного сервера Steam требует пароль не короче 5 символов")
			return
		}

		var gameSlug, serverName string
		var port int
		var volumePath, containerName, worldName *string
		err = pool.QueryRow(r.Context(), `
			SELECT games.slug, servers.name, servers.port,
			       servers.docker_volume_path, servers.docker_container_name, servers.docker_world_name
			FROM servers JOIN games ON games.id = servers.game_id
			WHERE servers.id = $1
		`, id).Scan(&gameSlug, &serverName, &port, &volumePath, &containerName, &worldName)
		if err != nil {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}

		if gameSlug != "valheim" {
			writeError(w, http.StatusBadRequest, "Смена пароля с пересозданием контейнера пока поддержана только для Valheim")
			return
		}
		if volumePath == nil || containerName == nil || worldName == nil {
			writeError(w, http.StatusBadRequest, "Сначала заполни и сохрани имя контейнера, мира и путь к тому выше")
			return
		}

		volumeName, err := dockerVolumeNameFromPath(*volumePath)
		if err != nil {
			writeError(w, http.StatusInternalServerError, err.Error())
			return
		}

		if err := recreateValheimContainer(*containerName, volumeName, port, serverName, *worldName, req.Password, req.IsPublic); err != nil {
			writeError(w, http.StatusInternalServerError, "Не удалось пересоздать контейнер: "+err.Error())
			return
		}

		var passwordValue any
		if req.Password != "" {
			passwordValue = req.Password
		}
		if _, err := pool.Exec(r.Context(),
			"UPDATE servers SET connect_password = $1, is_public = $2 WHERE id = $3", passwordValue, req.IsPublic, id,
		); err != nil {
			writeError(w, http.StatusInternalServerError, "Контейнер пересоздан, но не удалось сохранить настройки в базе: "+err.Error())
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

// handleRestartServer — superadmin: обычный "docker restart", без смены каких-либо
// настроек (в отличие от handleSetServerPassword, который полностью пересоздаёт
// контейнер). Нужен, когда сервер завис/подвисает или просто хочется применить
// обновление игры/перечитать сохранение, не трогая пароль/публичность/имя.
func handleRestartServer(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := strconv.Atoi(r.PathValue("id"))
		if err != nil {
			writeError(w, http.StatusBadRequest, "Некорректный id")
			return
		}

		var containerName *string
		err = pool.QueryRow(r.Context(),
			"SELECT docker_container_name FROM servers WHERE id = $1", id,
		).Scan(&containerName)
		if err != nil {
			writeError(w, http.StatusNotFound, "Сервер не найден")
			return
		}
		if containerName == nil {
			writeError(w, http.StatusBadRequest, "Сначала настрой имя контейнера в панели «Изменить сервер»")
			return
		}

		out, err := exec.Command("docker", "restart", *containerName).CombinedOutput()
		if err != nil {
			writeError(w, http.StatusInternalServerError, "Не удалось перезапустить контейнер: "+err.Error()+": "+string(out))
			return
		}

		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	}
}

// dockerVolumeNameFromPath достаёт имя volume из его пути на диске
// ("/var/lib/docker/volumes/имя/_data" -> "имя") — путь мы уже храним для adminlist.txt,
// а для `docker run -v` нужно именно имя, а не путь монтирования
func dockerVolumeNameFromPath(path string) (string, error) {
	const prefix = "/var/lib/docker/volumes/"
	const suffix = "/_data"
	if !strings.HasPrefix(path, prefix) || !strings.HasSuffix(path, suffix) {
		return "", fmt.Errorf("не похоже на путь тома Docker: %s", path)
	}
	return strings.TrimSuffix(strings.TrimPrefix(path, prefix), suffix), nil
}

func recreateValheimContainer(containerName, volumeName string, hostPort int, serverName, worldName, password string, isPublic bool) error {
	// игнорируем ошибку stop/rm — контейнера могло не быть или он уже остановлен
	exec.Command("docker", "stop", containerName).Run()
	exec.Command("docker", "rm", containerName).Run()

	publicValue := "0"
	if isPublic {
		publicValue = "1"
	}

	// отдельный volume под /opt/valheim (сам сервер + кэш SteamCMD, ~3.3 ГБ) —
	// без него каждое пересоздание контейнера качает игру заново с нуля
	binVolumeName := strings.TrimSuffix(volumeName, "_data") + "_bin"

	args := []string{
		"run", "-d",
		"--name", containerName,
		"--restart", "unless-stopped",
		"-p", fmt.Sprintf("%d:2456/udp", hostPort),
		"-p", fmt.Sprintf("%d:2457/udp", hostPort+1),
		"-p", fmt.Sprintf("%d:2458/udp", hostPort+2),
		"-v", volumeName + ":/config",
		"-v", binVolumeName + ":/opt/valheim",
		"-e", "SERVER_NAME=" + serverName,
		"-e", "WORLD_NAME=" + worldName,
		"-e", "PUBLIC=" + publicValue,
		"-e", "TZ=Europe/Vilnius",
	}
	// SERVER_PASS не передаём вовсе, если пароля нет — приватному серверу
	// (не публикуется в браузере Steam) он не обязателен
	if password != "" {
		args = append(args, "-e", "SERVER_PASS="+password)
	}
	args = append(args, "ghcr.io/lloesche/valheim-server")

	// os/exec передаёт аргументы напрямую процессу, минуя шелл — значения (даже с
	// спецсимволами) не могут повлиять на саму команду, инъекция тут невозможна
	out, err := exec.Command("docker", args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %s", err, string(out))
	}
	return nil
}
