// Everything a new Valheim dedicated server needs, worked out from the game's own
// FejdStartup.ParseServerArguments (assembly_valheim.dll 1.0.15) rather than from a
// wiki. The list below is EXHAUSTIVE: the parser reads these arguments and nothing
// else, so a field this module does not offer is a field the game would ignore.
//
// This module only decides and renders. It touches neither the database nor the
// disk, so the whole thing is testable without a VPS, and the site never gains a
// way to start anything by itself: what it produces is text for a human to run.

// The two enums the game parses -preset and -modifier against (WorldPresets,
// WorldModifiers, WorldModifierOption). Anything outside them it rejects with
// "Could not parse ... as a world modifier preset" and carries on with the world as
// it was — a typo here is silent, hence the check.
const PRESETS = ['Default', 'Normal', 'Casual', 'Easy', 'Hard', 'Hardcore', 'Immersive', 'Hammer'];
const MODIFIERS = ['Combat', 'DeathPenalty', 'Resources', 'Raids', 'Portals'];
const MODIFIER_OPTIONS = [
  'Default', 'None', 'Less', 'MuchLess', 'More', 'MuchMore',
  'Casual', 'VeryEasy', 'Easy', 'Hard', 'VeryHard', 'Hardcore', 'Most'
];

// The game takes port and port+1: the first is the game itself, the second answers
// Steam's queries. Measured on the Windows server with Get-NetUDPEndpoint, which is
// also how we learned that 2458 — the port every guide mentions — is not bound.
const PORTS_PER_SERVER = 2;

// Taken by the server that is already running. A second instance landing on them
// would bind nothing and look, from the site, exactly like a server that is down.
const BUSY_PORTS = [2456, 2457];

// A folder name that is also a container name, a compose project and a path. The
// narrow shape is the point: this string ends up inside the commands we render, and
// the site must not be able to write a path or a shell fragment into them.
const SLOT_RE = /^[a-z][a-z0-9-]{0,23}$/;
// The world's folder name IS its identity — SaveSystem.GetChunkedSaveName is
// literally Path.GetFileName(Path.GetDirectoryName(path)) — so it has to survive a
// trip through a filename unchanged.
const WORLD_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/;

// /srv/valheim belongs to the server installed by deploy/valheim/install.sh.
const RESERVED_SLOTS = ['server', 'saves', 'logs'];

// GetPublicPasswordError compares against m_minimumPasswordLength, and that is a
// serialized field of the prefab, not a constant in the code — so this number is the
// one everybody observes, not one we could read. It matters only for -public 1,
// which we do not run anyway.
const MIN_PASSWORD = 5;

const DEFAULTS = {
  port: 2458,
  saveInterval: 900,
  backups: 4,
  backupShort: 3600,
  backupLong: 43200
};

function root(slot) {
  return `/srv/valheim-${slot}`;
}

function intField(value, fallback) {
  if (value === undefined || value === null || value === '') return fallback;
  const n = Number(value);
  return Number.isInteger(n) ? n : NaN;
}

// The whole decision in one place: what is wrong, what is merely worth knowing, and
// what the flags come out as. Callers render; they do not judge.
function planServer(input = {}) {
  const errors = [];
  const warnings = [];

  const slot = String(input.slot || '').trim();
  const worldName = String(input.worldName || '').trim();
  const serverName = String(input.serverName || '').trim();
  const password = String(input.password || '');
  const isPublic = input.public === true || input.public === 1 || input.public === '1';
  const crossplay = input.crossplay === true;
  const bepinex = input.bepinex === true;
  const instanceId = String(input.instanceId || '').trim();
  const newWorld = input.newWorld === true;
  const preset = String(input.preset || '').trim();
  const modifiers = Array.isArray(input.modifiers) ? input.modifiers : [];
  const setKeys = Array.isArray(input.setKeys) ? input.setKeys : [];

  const port = intField(input.port, DEFAULTS.port);
  const saveInterval = intField(input.saveInterval, DEFAULTS.saveInterval);
  const backups = intField(input.backups, DEFAULTS.backups);
  const backupShort = intField(input.backupShort, DEFAULTS.backupShort);
  const backupLong = intField(input.backupLong, DEFAULTS.backupLong);

  if (!SLOT_RE.test(slot)) {
    errors.push('Ключ сервера: латиница в нижнем регистре, цифры и дефис, до 24 символов, начинается с буквы.');
  } else if (RESERVED_SLOTS.includes(slot)) {
    errors.push(`Ключ «${slot}» занят папками уже стоящего сервера.`);
  }
  if (!WORLD_RE.test(worldName)) {
    errors.push('Имя мира: латиница, цифры, дефис и подчёркивание, до 32 символов. Это имя ПАПКИ мира, и оно же его личность.');
  }
  if (!serverName) errors.push('Название сервера пустое — игрок увидит его в списке.');
  if (serverName.length > 64) errors.push('Название сервера длиннее 64 символов.');

  if (!Number.isInteger(port) || port < 1024 || port > 65534) {
    errors.push('Порт: целое от 1024 до 65534. Игра занимает порт и следующий за ним.');
  } else {
    const pair = [port, port + PORTS_PER_SERVER - 1];
    if (pair.some((p) => BUSY_PORTS.includes(p))) {
      errors.push(`Порты ${pair[0]}–${pair[1]} пересекаются с уже работающим сервером (${BUSY_PORTS.join('–')}).`);
    }
  }

  // Mathf.Max(5, ...) in the parser: a smaller number is not refused, it is quietly
  // raised. Saying so here is cheaper than wondering later why 1 became 5.
  if (!Number.isInteger(saveInterval) || saveInterval < 5) {
    errors.push('Интервал сохранения — целое, не меньше 5 секунд: меньшее игра всё равно поднимет до 5.');
  }
  if (!Number.isInteger(backups) || backups < 0) errors.push('Число резервных копий — целое, не меньше нуля.');
  if (!Number.isInteger(backupShort) || backupShort < 5) errors.push('Частая копия — целое, не меньше 5 секунд.');
  if (!Number.isInteger(backupLong) || backupLong < 5) errors.push('Редкая копия — целое, не меньше 5 секунд.');

  if (isPublic) {
    // IsPublicPasswordValid, and it does not complain — it calls Application.Quit().
    // A public server that fails this check does not start at all.
    if (password.length < MIN_PASSWORD) {
      errors.push(`Публичный сервер обязан иметь пароль не короче ${MIN_PASSWORD} символов, иначе игра не запустится вовсе.`);
    }
    if (password && (worldName.includes(password) || serverName.includes(password))) {
      errors.push('Пароль не должен быть частью имени мира или названия сервера — такой игра отвергает.');
    }
    warnings.push('Публичный сервер виден в списке игры всему интернету, и вайтлист — единственное, что удержит чужих.');
  } else if (password) {
    warnings.push('Пароль у закрытого сервера ничего не решает: игра требует его только у публичного, а вход сюда решает permittedlist.txt.');
  }

  if (crossplay) {
    warnings.push('-crossplay меняет весь транспорт на PlayFab Party: слушающего сокета нет, вход по IP превращается в поиск по лобби, а личность игрока приходит полем, которое написал сам клиент, вместо тикета Steam.');
  }
  if (instanceId && !crossplay) {
    warnings.push('-instanceid без -crossplay не делает ничего: он читается ровно в одном месте, суффиксом к PlayFab-аккаунту.');
  }
  if (!isPublic) {
    warnings.push('При -public 0 игра не отвечает на запрос статуса (A2S) вовсе: SetAdvertiseServerActive включается только у публичного. Статус сайт будет читать из лога.');
  }

  if (preset && !PRESETS.includes(preset)) {
    errors.push(`Пресет «${preset}» игра не знает. Есть: ${PRESETS.join(', ')}.`);
  }
  for (const m of modifiers) {
    const what = String((m && m.name) || '');
    const how = String((m && m.option) || '');
    if (!MODIFIERS.includes(what)) errors.push(`Правило мира «${what}» игра не знает. Есть: ${MODIFIERS.join(', ')}.`);
    if (!MODIFIER_OPTIONS.includes(how)) errors.push(`Значение «${how}» игра не знает. Есть: ${MODIFIER_OPTIONS.join(', ')}.`);
  }
  for (const key of setKeys) {
    if (!/^[a-z0-9_]{1,32}$/.test(String(key))) {
      errors.push(`Глобальный ключ «${key}»: только латиница в нижнем регистре, цифры и подчёркивание.`);
    }
  }
  if (preset || modifiers.length || setKeys.length) {
    warnings.push('Пресет, правила мира и ключи впечатываются в САМ МИР при каждом запуске и остаются в нём до -resetmodifiers: это правка мира, а не настройка запуска.');
    if (modifiers.some((m) => m && m.name === 'Resources')) {
      warnings.push('Правило Resources подерётся с модом: ставку ресурсов он держит своим ключом мира и возвращает её раз в полминуты.');
    }
  }
  if (bepinex) {
    warnings.push('BEPINEX=yes: DLL мода у сервера и у игроков должна совпадать байт в байт, а у этого сервера будет своя папка BepInEx и свой LogOutput.log.');
  }
  if (newWorld) {
    warnings.push('Мир создаст сам сервер, и сид у него будет СЛУЧАЙНЫЙ: флага для сида у игры нет вовсе. Свой сид — только миром, созданным в клиенте, и его папкой в worlds_local.');
  }

  const flags = [
    ['-nographics', ''],
    ['-batchmode', ''],
    ['-name', serverName],
    ['-port', String(port)],
    ['-world', worldName],
    ['-public', isPublic ? '1' : '0'],
    ['-savedir', `${root(slot)}/saves`],
    ['-logFile', `${root(slot)}/logs/server.log`],
    ['-saveinterval', String(saveInterval)],
    ['-backups', String(backups)],
    ['-backupshort', String(backupShort)],
    ['-backuplong', String(backupLong)]
  ];
  if (isPublic && password) flags.push(['-password', password]);
  if (crossplay) flags.push(['-crossplay', '']);
  if (instanceId) flags.push(['-instanceid', instanceId]);
  if (preset) flags.push(['-preset', preset]);
  for (const m of modifiers) flags.push(['-modifier', `${m.name} ${m.option}`]);
  for (const key of setKeys) flags.push(['-setkey', String(key)]);

  const opts = {
    slot, worldName, serverName, port, isPublic, password, saveInterval,
    backups, backupShort, backupLong, bepinex, crossplay, instanceId,
    preset, modifiers, setKeys, newWorld
  };

  return {
    ok: errors.length === 0,
    errors,
    warnings,
    slot,
    root: root(slot),
    ports: [port, port + PORTS_PER_SERVER - 1],
    flags,
    newWorld,
    files: errors.length ? null : {
      'server.env': renderEnv(opts),
      'compose.yml': renderCompose(opts)
    },
    commands: errors.length ? [] : renderCommands(opts)
  };
}

function renderEnv(o) {
  const dir = root(o.slot);
  const lines = [
    `# ${o.serverName} — ещё один игровой сервер на этой машине.`,
    `# Собрано на сайте, лежит в ${dir}/server.env и в git не попадает.`,
    '',
    '# Имя ПАПКИ мира в worlds_local, символ в символ: это личность мира.',
    `WORLD_NAME=${o.worldName}`,
    `SERVER_NAME=${o.serverName}`,
    `SERVER_PORT=${o.port}`,
    `SERVER_PUBLIC=${o.isPublic ? 1 : 0}`,
    `SAVE_INTERVAL=${o.saveInterval}`,
    `BACKUPS=${o.backups}`,
    `BACKUP_SHORT=${o.backupShort}`,
    `BACKUP_LONG=${o.backupLong}`,
    `SERVER_DIR=${dir}/server`,
    `SAVE_DIR=${dir}/saves`,
    `LOG_DIR=${dir}/logs`,
    `BEPINEX=${o.bepinex ? 'yes' : 'no'}`
  ];
  if (o.isPublic && o.password) lines.push(`SERVER_PASSWORD=${o.password}`);
  if (o.crossplay) lines.push('CROSSPLAY=yes');
  if (o.instanceId) lines.push(`INSTANCE_ID=${o.instanceId}`);
  if (o.preset) lines.push(`WORLD_PRESET=${o.preset}`);
  if (o.modifiers.length) lines.push(`WORLD_MODIFIERS=${o.modifiers.map((m) => `${m.name}:${m.option}`).join(',')}`);
  if (o.setKeys.length) lines.push(`WORLD_KEYS=${o.setKeys.join(',')}`);
  if (o.newWorld) {
    lines.push('');
    lines.push('# Мира ещё нет, и сервер создаст его сам — со СЛУЧАЙНЫМ сидом: флага для');
    lines.push('# сида у игры нет вовсе. Убрать сразу после первого запуска: этот ключ');
    lines.push('# выключает единственную защиту от опечатки в имени мира.');
    lines.push('ALLOW_NEW_WORLD=yes');
  }
  return lines.join('\n') + '\n';
}

function renderCompose(o) {
  const dir = root(o.slot);
  const pair = `${o.port}-${o.port + PORTS_PER_SERVER - 1}`;
  return `# ${o.slot}: ещё один сервер Valheim на этой машине.
#
#   docker compose -f ${dir}/compose.yml up -d     # запуск
#   docker compose -f ${dir}/compose.yml stop      # остановка С СОХРАНЕНИЕМ МИРА
#
# Образ берётся готовый, собранный deploy/valheim/install.sh для первого сервера:
# пересобирать его тут незачем, а build: увёл бы этот сервер на свою копию.
name: astvard-valheim-${o.slot}

services:
  valheim:
    image: astvard-valheim:local
    container_name: astvard-valheim-${o.slot}
    env_file: ${dir}/server.env

    # Игра пишет мир только по Ctrl+C, то есть по SIGINT. У Docker по умолчанию
    # SIGTERM и SIGKILL через десять секунд — то есть потеря всего с последнего
    # автосохранения при каждом перезапуске и при каждой перезагрузке машины.
    stop_signal: SIGINT
    stop_grace_period: 2m
    init: true
    restart: unless-stopped

    ports:
      - "${pair}:${pair}/udp"

    volumes:
      - ${dir}/server:${dir}/server
      - ${dir}/saves:${dir}/saves
      - ${dir}/logs:${dir}/logs
`;
}

function renderCommands(o) {
  const dir = root(o.slot);
  const list = [
    {
      title: 'Папки и владелец',
      why: 'Игра в контейнере работает от пользователя valheim с той же парой uid/gid, что на хосте.',
      code: `mkdir -p ${dir}/server ${dir}/saves/worlds_local ${dir}/logs\nchown -R valheim:valheim ${dir}`
    },
    {
      title: 'Положить server.env и compose.yml',
      why: 'Оба файла выше. Они живут вне git, так что update-site.sh их не тронет.',
      code: `nano ${dir}/server.env\nnano ${dir}/compose.yml`
    },
    {
      title: 'Поставить игру',
      why: 'Своя установка, а не общая с первым сервером: две игры на одной установке пишут в один BepInEx/LogOutput.log, и разобрать его потом нечем. Цена — около 2 ГБ диска.',
      code: `docker compose -f ${dir}/compose.yml run --rm valheim update`
    }
  ];
  if (!o.newWorld) {
    list.push({
      title: 'Положить мир',
      why: `Папка должна называться ровно ${o.worldName}: имя папки и есть личность мира. Внутри ничего не переименовывать, кэш биомов не тащить.`,
      code: `scp -r "<папка мира>" astvard-vps:${dir}/saves/worlds_local/\nchown -R valheim:valheim ${dir}/saves`
    });
  }
  list.push(
    {
      title: 'Запуск',
      why: 'Ждём в логе строку Game server connected.',
      code: `docker compose -f ${dir}/compose.yml up -d\ntail -f ${dir}/logs/server.log`
    },
    {
      title: 'Дать сайту читать лог',
      why: 'Иначе статус этого сервера на сайте будет вечное «offline»: сайт работает от своего пользователя.',
      code: `chmod o+rx ${dir} ${dir}/logs\nchmod o+r ${dir}/logs/server.log`
    }
  );
  if (o.newWorld) {
    list.push({
      title: 'Убрать ALLOW_NEW_WORLD',
      why: 'Мир создан, и дальше этот ключ означал бы, что опечатка в имени мира молча заведёт ещё один пустой.',
      code: `sed -i '/ALLOW_NEW_WORLD/d' ${dir}/server.env\ndocker compose -f ${dir}/compose.yml up -d`
    });
  }
  return list;
}

module.exports = {
  planServer,
  PRESETS,
  MODIFIERS,
  MODIFIER_OPTIONS,
  SLOT_RE,
  DEFAULTS
};
