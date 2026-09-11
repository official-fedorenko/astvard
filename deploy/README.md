# Развёртывание на VPS

Всё здесь рассчитано на Ubuntu 24.04 (на VPS сейчас nginx/1.24.0 Ubuntu) и
запускается под root. Скрипты идемпотентные: повторный запуск не ломает
установленное и не перезаписывает ни `.env`, ни `server.env`.

**Проверено локально, не на VPS.** `nginx -t` и поведение прокси прогнаны на том же
nginx 1.24.0, юниты — через `systemd-analyze verify`, скрипты — через `shellcheck`.
Самого VPS у агента нет, так что первый запуск — твой.

## Сначала: ветка

Код портала (вайтлист, вход через Steam, серверная админка) лежит в
`claude/website-status-010dn6`. Скрипты по умолчанию берут `valheim-mod`, потому что
работа в репозитории идёт там. Либо влей ветку, либо укажи её явно:

```bash
BRANCH=claude/website-status-010dn6 ./deploy/install-site.sh
```

## Сайт

```bash
git clone https://github.com/official-fedorenko/astvard.git /srv/astvard
cd /srv/astvard && git checkout valheim-mod
./deploy/install-site.sh
```

Скрипт: Node, пользователь `astvard`, `.env` со свежими секретами, Postgres через
`docker compose`, схема, systemd-юнит, nginx. В конце — живой запрос к
`/api/health`; если он не ответил, скрипт падает и печатает журнал.

Что изменится в nginx: заглушка уступает место проксированию на `:3001`. Заодно
уходит ловушка прежнего конфига — `try_files $uri $uri/ /index.html` отвечал на
**каждый** `/api/...` HTML-страницей с кодом 200, то есть фронтенд получал «успех»
с HTML вместо JSON. Проверено после правки: `/api/health` → JSON, неизвестная
ручка → JSON 404.

Сертификат уже есть и certbot-строки сохранены дословно, обновление не ломается.

Первый суперадмин назначается руками — зарегистрируйся на сайте, потом:

```bash
docker compose -f /srv/astvard/docker-compose.yml exec -T postgres \
  psql -U astvard -d astvard -c "UPDATE users SET role='superadmin' WHERE email='ТВОЙ_EMAIL';"
```

Обновление потом: `./deploy/update-site.sh` — подтянет код, накатит схему,
перезапустит и **сам откатится** на прежний коммит, если `/api/health` не ответил.

## Игровой сервер

```bash
./deploy/install-valheim.sh
```

Ставит ванильный сервер через SteamCMD, создаёт `/srv/valheim/{server,saves,logs}`,
кладёт юнит и открывает `2456:2458/udp`. **Сервер не запускается** — сначала мир.

Скрипт предупредит, если на машине меньше 3 ГБ памяти: Valheim держит мир целиком в
памяти и на живом мире просит 2-4 ГБ только под себя, а рядом уже Postgres и Node.

### Перенос карты

Карта на твоей машине, у агента к ней доступа нет. Порядок важен — на каждом шаге
теряется либо мир, либо часть игры.

**1. Остановить сервер правильно.** Только Ctrl+C: тогда игра пишет мир на выходе.
Убитый процесс теряет всё после последнего автосохранения — до 15 минут. Из корня
репозитория:

```powershell
$server = (Get-Process valheim_server).Id
Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"$PWD\valheim-mod\server\send_ctrl_c.ps1",'-TargetPid',$server -WindowStyle Hidden -Wait
```

Дождись в логе `World save (5/5) done` и исчезновения процесса, потом добей
оставшийся `cmd.exe`. Без этой строки в логе копировать нечего — свежих данных в
файлах ещё нет.

**2. Узнать точное имя мира.** Имя папки — это и есть личность мира:
`SaveSystem.GetChunkedSaveName` буквально возвращает имя директории.

```powershell
ls "C:\Games\valheim-servers\astvard\worlds_local"
```

В CLAUDE.md записано `AstwardWorld` — через `w`, не `v`. Сверь: ошибёшься на символ,
и сервер **не упадёт**, а молча создаст новый пустой мир. Обёртка запуска это
проверяет и отказывается стартовать, но проверить ей нужно верное имя.

**3. Скопировать папку мира** целиком, файлы внутри не переименовывать:

```powershell
scp -r "C:\Games\valheim-servers\astvard\worlds_local\AstwardWorld" `
    root@astvard.online:/srv/valheim/saves/worlds_local/
```

**Кэш биомов не тащить.** `cache\<Мир>_biomedatacache.bin` привязан к имени мира, и
чужой кэш под тем же именем будет принят как свой. На новом месте он построится
заново.

**4. Права и имя:**

```bash
chown -R valheim:valheim /srv/valheim/saves
grep WORLD_NAME /srv/valheim/server.env      # должно совпасть с именем папки
```

**5. Списки доступа не копировать — собрать с сайта.** Этим теперь занимается
админка: кнопки «Собрать permittedlist.txt» и «Собрать adminlist.txt». Файлы
кладутся в `/srv/valheim/saves/`. Помни, что пустой `permittedlist.txt` означает
«пускать всех» — сайт поэтому отказывается выгружать файл без записей.

**6. Запуск:**

```bash
systemctl enable --now astvard-valheim
tail -f /srv/valheim/logs/server.log        # ждём Game server connected
```

Остановка — только `systemctl stop astvard-valheim`: в юните стоит
`KillSignal=SIGINT`, то есть тот же Ctrl+C, и мир пишется. Стоковый юнит послал бы
SIGTERM и съедал бы часть игры при каждой перезагрузке машины.

### Мод на Linux — не проверено

`install-valheim.sh` ставит **ваниль**. Сначала стоит убедиться, что мир поднялся и
игроки заходят, и только потом трогать мод. Что известно:

- Windows-специфики в коде мода нет — проверено поиском по `valheim-mod/`.
- Мод собран под `net472` и ссылается на BepInEx, Jötunn и `assembly_valheim`;
  Linux-сервер Valheim — это тоже Mono, так что managed-сборка в принципе
  загрузится.
- BepInEx под Linux ставится иначе: не `winhttp`-doorstop, а `run_bepinex.sh` с
  `libdoorstop`. Значит и запуск пойдёт через него, а не напрямую через бинарник —
  обёртку придётся править.
- Jötunn 2.29.2 официального релиза под Valheim 1.0 не имеет и уже на Windows даёт
  `MissingFieldException` на каждом входе игрока. На Linux это лучше не станет.
- Собирать мод надо там, где есть Valheim и BepInEx (в `.csproj` стоят `HintPath` на
  них), то есть по-прежнему на твоей машине, а на VPS копировать готовую DLL.

### Устаревший bat в репозитории

`valheim-mod/server/start_astvard.bat` не соответствует тому, что описано в
CLAUDE.md как актуальное: в нём `-world "Astvard"` и `-savedir "%~dp0saves"`, нет
`-logFile`, и в шапке упомянут `ServerDevcommands`, который из обеих установок
убран. Актуальный bat — на машине, в git его нет. Флаги в `start-valheim.sh`
собраны по CLAUDE.md, а не по этому файлу; **сверь их с настоящим bat** перед
запуском, и заодно закоммить его — иначе расхождение будет расти.
