# Развёртывание на VPS

Всё здесь рассчитано на Ubuntu 24.04 и запускается под root. Скрипты идемпотентные:
повторный запуск не ломает установленное и не перезаписывает ни `.env`, ни
`server.env`.

**Машина общая.** На VPS живут чужие проекты: PM2-приложения `electro-modus` и
`meliowar-space`, сайты `electro-modus` и `tgassistant` в том же nginx, n8n с traefik
и общий Postgres `postgres-shared`. Поэтому скрипты системный Node не ставят и не
меняют, базу кладут в общий Postgres, а конфиг nginx перед заменой сохраняют и при
ошибке возвращают.

## Сайт

```bash
git clone --branch valheim-mod https://github.com/official-fedorenko/astvard.git /srv/astvard
cd /srv/astvard
POSTGRES_CONTAINER=postgres-shared ./deploy/install-site.sh
```

Скрипт проверяет Node (нужен 20.6+: бэкенд запускается с `--env-file`), заводит
пользователя `astvard` и `.env` со свежими секретами, создаёт роль и базу `astvard`
внутри `postgres-shared`, накатывает схему, ставит systemd-юнит и меняет nginx. В
конце делает живой запрос к `/api/health`; если ответа нет, скрипт падает и печатает
журнал.

**Почему общий Postgres, а не свой.** Так решили 1 сентября: один экземпляр на машину,
у каждого проекта своя роль и база. `postgres-shared` уже занимает `127.0.0.1:5432`, и
свой compose-Postgres на этом порту просто не встал бы. Без `POSTGRES_CONTAINER`
скрипт работает по-старому, через `docker-compose.yml` из репозитория, как на машине
разработчика. Какой способ выбран, записано в `.env`, и `update-site.sh` читает его
оттуда.

**Что меняется в nginx.** Заглушка уступает место проксированию на `:3001`, статика
тоже идёт через бэкенд. Прежние файлы сохраняются в `/root/nginx-astvard-backup-<время>`.
Если новый конфиг не проходит `nginx -t`, скрипт возвращает старые файлы и nginx не
перезагружает: сломанный конфиг на диске положил бы и чужие сайты при следующей
перезагрузке. Сертификат уже есть, строки certbot сохранены дословно.

**Первый суперадмин** назначается руками, после первого входа на сайт:

```bash
docker exec postgres-shared psql -U astvard -d astvard \
  -c "UPDATE users SET role='superadmin' WHERE nickname='НИК';"
```

**Обновление:** `./deploy/update-site.sh`. Он подтягивает код, накатывает схему и
перезапускает бэкенд. Если после этого `/api/health` не ответил, скрипт **сам
откатывается** на прежний коммит.

## Игровой сервер — Docker

```bash
./deploy/valheim/install.sh
```

Собирает образ `astvard-valheim:local` (Ubuntu 24.04 + SteamCMD) и ставит игру через
SteamCMD в `/srv/valheim/server`. **Сервер не запускается**: без мира игра создала
бы новый пустой.

| Где | Что |
|---|---|
| `/srv/valheim/server` | игра, а при `BEPINEX=yes` ещё BepInEx и мод |
| `/srv/valheim/saves` | `worlds_local/<мир>`, `permittedlist.txt`, `adminlist.txt`, `bannedlist.txt` |
| `/srv/valheim/logs/server.log` | лог игры (`-logFile`) |
| `/srv/valheim/server/BepInEx/LogOutput.log` | лог BepInEx и мода — это другой файл |
| `/srv/valheim/server.env` | имя мира, имя сервера, `BEPINEX` |

Игра в образ не зашита намеренно. Обновить её — это решение, а не побочный эффект
перезапуска: версии клиента, сервера и DLL мода должны совпадать, и перезапуск, сам
подтянувший новую сборку, развалил бы это молча.

```bash
cd /srv/astvard
docker compose -f deploy/valheim/compose.yml up -d        # запуск
docker compose -f deploy/valheim/compose.yml stop         # остановка с сохранением мира
docker compose -f deploy/valheim/compose.yml logs -f      # вывод контейнера (игра пишет в server.log)
./deploy/valheim/install.sh                               # обновление игры — только остановленной
```

**Останавливать только `stop` (или `down`).** В compose стоит `stop_signal: SIGINT`,
то есть тот же Ctrl+C, и игра пишет мир на выходе. `docker kill` и `docker rm -f`
теряют всё после последнего автосохранения, до 15 минут игры. При перезагрузке VPS
Docker, по его коду, останавливает каждый контейнер его собственным сигналом и ждёт его
`stop_grace_period`. Живьём перезагрузкой это не проверялось: на машине чужие проекты.

Порты — `2456-2457/udp`. Сколько портов на самом деле нужно, проверено на
Windows-сервере 1.0.12: занят 2456 (игра) и 2457 (ответы Steam на запросы). Порт
2458, о котором пишут все инструкции и сам скрипт BepInEx, не занят вовсе. ufw на
VPS выключен, Docker открывает опубликованные порты сам.

### Перенос мира

Порядок важен: на каждом шаге можно потерять либо мир, либо часть игры.

**1. Остановить Windows-сервер правильно**, Ctrl+C: так игра пишет мир на выходе.
Как именно — в корневом `CLAUDE.md`. Копировать можно только после строки
`World save (5/5) done` в логе: до неё свежих данных в файлах ещё нет.

**2. Имя мира — это имя папки**, символ в символ: `AstwardWorld`, через `w`. Ошибка на
один символ не роняет сервер, а даёт новый пустой мир. `server.sh` поэтому
отказывается стартовать, если папки нет или в ней нет законченного сохранения
(`_main.N.ok`).

**3. Скопировать папку целиком**, ничего внутри не переименовывать:

```powershell
scp -r "C:\Games\valheim-servers\astvard\worlds_local\AstwardWorld" astvard-vps:/srv/valheim/saves/worlds_local/
ssh astvard-vps chown -R valheim:valheim /srv/valheim/saves
```

**Кэш биомов не тащить.** `cache\<Мир>_biomedatacache.bin` привязан к имени мира, и
чужой кэш под тем же именем был бы принят как свой. На новом месте он построится
заново.

**4. Списки доступа** кладутся в `/srv/valheim/saves/`. Их можно собрать на сайте
(«Собрать permittedlist.txt» и «Собрать adminlist.txt» в админке) или взять со
старого сервера. Пустой `permittedlist.txt` означает «пускать всех», поэтому сайт
отказывается выгружать его без записей. Игра перечитывает списки на ходу, примерно
раз в десять секунд.

**5. Запуск** — `up -d`, потом `tail -f /srv/valheim/logs/server.log`. Ждём
`Game server connected`.

### Мод

Мод включается строкой `BEPINEX=yes` в `server.env` и перезапуском (`up -d`). Файлы
кладутся в `/srv/valheim/server` рядом с игрой:

- `BepInEx/core/*` и `doorstop_libs/libdoorstop_x64.so` — из
  `C:\Games\steamapps\common\Valheim dedicated server`. BepInExPack_Valheim кладёт
  Linux-запускалку туда же, где Windows-версию, так что с Thunderstore ничего качать
  не нужно.
- `BepInEx/plugins/Jotunn.dll` и `BepInEx/plugins/AstvardServerMod.dll`.
  **`AstvardServerMod.dll` должен совпадать байт в байт с клиентским** — сверять
  md5, как в корневом `CLAUDE.md`. Это уже третье место, куда кладётся DLL.

`server.sh` запускает игру через doorstop теми же четырьмя переменными, что и
`start_server_bepinex.sh` из пакета. Если `BEPINEX=yes`, а файлов нет, сервер не
запускается: ванильный старт там, где ждут мод, выглядел бы как «кнопки не работают».
