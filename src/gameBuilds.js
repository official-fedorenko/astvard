const { db } = require('../db');
const logger = require('./logger');
const { parseSteamId64 } = require('./steamId');

/**
 * What players may build on the game server: the mod's rules for players, and who
 * may build each shared template - everyone, or players named one by one.
 *
 * Until this, all of it lived only in the mod: the rules in its config, the access in
 * a header line of each template file, and the only way to change either was the
 * admin panel inside the game. Now the site keeps the same state in the database and
 * both ends change it:
 *
 * - the mod pulls the whole state by token (GET /api/game/builds) and writes it into
 *   its config and template files;
 * - what an admin changes in the game the mod pushes here (POST), and that becomes
 *   the state everyone pulls next.
 *
 * "The last change wins" is an order, not a clock. Every change takes the next
 * revision; the mod reports which revision it has applied; a row whose revision is
 * above that is still on its way to the server. The VPS, the container and a browser
 * never have to agree on the time.
 *
 * The first time the mod comes the site knows nothing, so it asks for everything the
 * server has (a seed) and from then on follows the database. A change made on the site
 * before that seed arrives is kept: the seed only fills in what is missing.
 */

const get = (sql, params = []) => new Promise((resolve, reject) => {
  db.get(sql, params, (err, row) => (err ? reject(err) : resolve(row)));
});
const all = (sql, params = []) => new Promise((resolve, reject) => {
  db.all(sql, params, (err, rows) => (err ? reject(err) : resolve(rows)));
});
const run = (sql, params = []) => new Promise((resolve, reject) => {
  db.run(sql, params, function callback(err) { return err ? reject(err) : resolve(this); });
});

const RULE_KEY_RE = /^[a-z]{1,24}$/;
const INT_RE = /^-?\d{1,7}$/;
const STEAM_ID_RE = /^\d{17}$/;
const KINDS = new Set(['toggle', 'limit', 'choice', 'choicelimit', 'number']);
const GROUPS = new Set(['terrain', 'build', 'features']);
const MAX_TEMPLATE_NAME = 120;
const MAX_PLAYERS = 500;
const MAX_PUSH_LINES = 2000;
const MIN_VALUE = -1;
const MAX_VALUE = 100000;

const CHOICE_WORDS = ['нельзя', 'даром', 'платно'];

const toInt = (value) => {
  const text = String(value ?? '').trim();
  return INT_RE.test(text) ? Number(text) : null;
};

const cleanText = (value, max) => String(value ?? '').replace(/[\t\r\n]+/g, ' ').trim().slice(0, max);

const splitPlayers = (text) => String(text || '').split(',').map((id) => id.trim()).filter((id) => STEAM_ID_RE.test(id));

function templateNameError(name) {
  if (typeof name !== 'string' || !name.trim()) return 'Нет названия постройки';
  if (name.length > MAX_TEMPLATE_NAME) return 'Слишком длинное название постройки';
  // A tab or a line break would split the record the mod reads.
  if (/[\u0000-\u001f\u007f]/.test(name)) return 'В названии постройки служебные символы';
  return null;
}

function normalizePlayers(list) {
  if (!Array.isArray(list)) return { error: 'Игроки — список SteamID64' };
  const ids = new Set();
  for (const raw of list) {
    const parsed = parseSteamId64(String(raw));
    if (parsed.error) return { error: `${String(raw).slice(0, 40)}: ${parsed.error}` };
    ids.add(parsed.id);
  }
  if (ids.size > MAX_PLAYERS) {
    return { error: `Больше ${MAX_PLAYERS} игроков на одну постройку — проще открыть её всем` };
  }
  return { ids: [...ids].sort() };
}

async function syncRow() {
  // The row is created by the schema; this covers a database the tests emptied.
  await run('INSERT INTO game_build_sync (id) VALUES (1) ON CONFLICT (id) DO NOTHING');
  return get('SELECT revision, seeded, mod_seen_at, mod_applied_revision FROM game_build_sync WHERE id = 1');
}

async function nextRevision() {
  await syncRow();
  const row = await get('UPDATE game_build_sync SET revision = revision + 1 WHERE id = 1 RETURNING revision');
  return row.revision;
}

function upsertRule(key, value, limit, revision, by, { keepExisting = false } = {}) {
  const onConflict = keepExisting
    ? 'DO NOTHING'
    : `DO UPDATE SET value = EXCLUDED.value, limit_value = EXCLUDED.limit_value,
         revision = EXCLUDED.revision, updated_at = now(), updated_by = EXCLUDED.updated_by`;
  return run(
    `INSERT INTO game_build_rules (key, value, limit_value, revision, updated_at, updated_by)
     VALUES (?, ?, ?, ?, now(), ?)
     ON CONFLICT (key) ${onConflict}`,
    [key, value, limit, revision, by]
  );
}

function upsertTemplate(name, forAll, players, revision, by, { keepExisting = false, onlyForAll = false } = {}) {
  let onConflict = `DO UPDATE SET for_all = EXCLUDED.for_all, players = EXCLUDED.players,
                      revision = EXCLUDED.revision, updated_at = now(), updated_by = EXCLUDED.updated_by`;
  if (keepExisting) onConflict = 'DO NOTHING';
  // The switch in game says nothing about the players named on the site; they stay.
  if (onlyForAll) {
    onConflict = `DO UPDATE SET for_all = EXCLUDED.for_all,
                    revision = EXCLUDED.revision, updated_at = now(), updated_by = EXCLUDED.updated_by`;
  }
  return run(
    `INSERT INTO game_template_access (name, for_all, players, revision, updated_at, updated_by)
     VALUES (?, ?, ?, ?, now(), ?)
     ON CONFLICT (name) ${onConflict}`,
    [name, forAll, players, revision, by]
  );
}

// ---------------------------------------------------------------- the mod's side

/**
 * The state for the mod. `rev` is the revision the mod has already applied: the same
 * revision gets a one-word answer, so a pull every few seconds costs a line, and the
 * number is kept as what the server has - the admin page reads "applied" from it.
 */
async function pullText(rev) {
  const sync = await syncRow();
  const applied = /^\d{1,9}$/.test(String(rev ?? '')) ? Number(rev) : null;

  // A mod reporting more than the site has is a database restored from a copy: that
  // number means nothing here, only the visit does.
  if (applied !== null && applied <= sync.revision) {
    await run('UPDATE game_build_sync SET mod_seen_at = now(), mod_applied_revision = ? WHERE id = 1', [applied]);
  } else {
    await run('UPDATE game_build_sync SET mod_seen_at = now() WHERE id = 1');
  }

  const lines = [`revision\t${sync.revision}`];
  if (!sync.seeded) {
    lines.push('seed\tneeded');
  } else if (applied === sync.revision) {
    lines.push('unchanged');
  } else {
    for (const r of await all('SELECT key, value, limit_value FROM game_build_rules ORDER BY key')) {
      lines.push(['rule', r.key, r.value, r.limit_value === null ? '' : r.limit_value].join('\t'));
    }
    for (const t of await all('SELECT name, for_all, players FROM game_template_access ORDER BY name')) {
      lines.push(['tpl', t.name, t.for_all ? 1 : 0, t.players].join('\t'));
    }
  }
  return `${lines.join('\n')}\n`;
}

function parseMeta(f) {
  if (f.length < 8 || !RULE_KEY_RE.test(f[1]) || !GROUPS.has(f[3]) || !KINDS.has(f[4])) return null;
  const title = cleanText(f[2], 80);
  if (!title) return null;
  return {
    key: f[1],
    title,
    group: f[3],
    kind: f[4],
    min: toInt(f[5]),
    max: toInt(f[6]),
    word: cleanText(f[7], 20) || null,
    note: cleanText(f[8], 300) || null
  };
}

function parseRule(f) {
  if (f.length < 3 || !RULE_KEY_RE.test(f[1])) return null;
  const value = toInt(f[2]);
  if (value === null || value < MIN_VALUE || value > MAX_VALUE) return null;
  let limit = null;
  if (f.length > 3 && f[3] !== '') {
    limit = toInt(f[3]);
    if (limit === null || limit < MIN_VALUE || limit > MAX_VALUE) return null;
  }
  return { key: f[1], value, limit };
}

function parseTemplate(f, withPlayers) {
  if (f.length < 3 || templateNameError(f[1]) || (f[2] !== '0' && f[2] !== '1')) return null;
  return {
    name: f[1],
    forAll: f[2] === '1',
    players: withPlayers ? [...new Set(splitPlayers(f[3]))].sort().slice(0, MAX_PLAYERS) : []
  };
}

const reply = (status, text) => ({ status, text: `${text}\n` });

async function replaceMeta(meta) {
  await run('DELETE FROM game_build_rule_meta');
  let order = 0;
  for (const m of meta) {
    order += 1;
    await run(
      `INSERT INTO game_build_rule_meta (key, sort_order, title, grp, kind, limit_min, limit_max, limit_word, note)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (key) DO NOTHING`,
      [m.key, order, m.title, m.group, m.kind, m.min, m.max, m.word, m.note]
    );
  }
}

/**
 * What the mod sends: «hello» with the rules it knows, «seed» with everything the
 * server holds, «change» with what an admin changed in game. Lines that do not read
 * are skipped with a word in the log rather than failing the rest: one rule a newer
 * mod adds must not stop the others arriving.
 */
async function pushFromMod(text) {
  const lines = String(text || '').split(/\r?\n/).filter((line) => line.length > 0);
  if (lines.length === 0 || lines.length > MAX_PUSH_LINES) return reply(400, 'bad body');

  const head = lines[0].split('\t');
  const kind = head[0] === 'kind' ? head[1] : null;
  if (!['hello', 'seed', 'change'].includes(kind)) return reply(400, 'no kind');

  const meta = [];
  const rules = [];
  const templates = [];
  const templateAll = [];
  let skipped = 0;

  for (const line of lines.slice(1)) {
    const f = line.split('\t');
    let parsed = null;
    if (f[0] === 'meta') {
      parsed = parseMeta(f);
      if (parsed) meta.push(parsed);
    } else if (f[0] === 'rule') {
      parsed = parseRule(f);
      if (parsed) rules.push(parsed);
    } else if (f[0] === 'tpl') {
      parsed = parseTemplate(f, true);
      if (parsed) templates.push(parsed);
    } else if (f[0] === 'tplall') {
      parsed = parseTemplate(f, false);
      if (parsed) templateAll.push(parsed);
    }
    if (!parsed) skipped += 1;
  }
  if (skipped) logger.warn(`[builds] мод прислал строк, которые не читаются: ${skipped} — пропущены`);

  // The mod describes its rules every time it starts: a newer build may add one or
  // drop one, and the admin page draws exactly what the running server knows.
  if (meta.length) await replaceMeta(meta);

  const sync = await syncRow();
  if (kind === 'hello') return reply(200, `revision\t${sync.revision}`);

  if (kind === 'seed') {
    if (sync.seeded) return reply(200, `revision\t${sync.revision}\nignored`);
    const revision = await nextRevision();
    for (const r of rules) await upsertRule(r.key, r.value, r.limit, revision, 'game', { keepExisting: true });
    for (const t of templates) {
      await upsertTemplate(t.name, t.forAll, t.players.join(','), revision, 'game', { keepExisting: true });
    }
    await run('UPDATE game_build_sync SET seeded = true WHERE id = 1');
    logger.info(`[builds] сервер прислал своё состояние: правил ${rules.length}, построек ${templates.length}`);
    return reply(200, `revision\t${revision}`);
  }

  // A change before any seed has nothing to change: the seed that follows carries the
  // same values anyway, because the mod has already applied them.
  if (!sync.seeded) return reply(409, `revision\t${sync.revision}\nseed\tneeded`);
  if (!rules.length && !templateAll.length) return reply(200, `revision\t${sync.revision}`);

  const revision = await nextRevision();
  for (const r of rules) await upsertRule(r.key, r.value, r.limit, revision, 'game');
  for (const t of templateAll) await upsertTemplate(t.name, t.forAll, '', revision, 'game', { onlyForAll: true });
  logger.info(`[builds] изменено в игре (ревизия ${revision}): правил ${rules.length}, построек ${templateAll.length}`);
  return reply(200, `revision\t${revision}`);
}

// ---------------------------------------------------------------- the admin's side

async function overview(templateFiles) {
  const sync = await syncRow();
  const meta = await all('SELECT * FROM game_build_rule_meta ORDER BY sort_order, key');
  const rules = await all('SELECT key, value, limit_value, revision, updated_at, updated_by FROM game_build_rules');
  const access = await all('SELECT name, for_all, players, revision, updated_at, updated_by FROM game_template_access');
  const people = await all(
    'SELECT username, steam_id, whitelist_status FROM users WHERE steam_id IS NOT NULL ORDER BY lower(username)'
  );

  const applied = (revision) => revision <= sync.mod_applied_revision;
  const ruleByKey = new Map(rules.map((r) => [r.key, r]));
  const accessByName = new Map(access.map((a) => [a.name, a]));
  const nameOf = new Map(people.map((p) => [p.steam_id, p.username]));

  const describeTemplate = (name, file) => {
    const row = accessByName.get(name);
    // Before the first seed the file is all there is to say.
    const ids = row ? splitPlayers(row.players) : ((file && file.allowed) || []);
    return {
      name,
      on_server: !!file,
      submitted: file ? !!file.submitted : false,
      category: file ? (file.category || 'Разное') : null,
      author: file ? (file.author || null) : null,
      pieces: file ? file.pieces : null,
      for_all: row ? row.for_all : !!(file && file.forPlayers),
      players: ids.map((id) => ({ steam_id: id, username: nameOf.get(id) || null })),
      applied: row ? applied(row.revision) : true,
      updated_at: row ? row.updated_at : null,
      updated_by: row ? row.updated_by : null
    };
  };

  const templates = [];
  const listed = new Set();
  for (const file of templateFiles) {
    listed.add(file.name);
    templates.push(describeTemplate(file.name, file));
  }
  // A row for a template no longer in the folder: renamed or thrown out in game.
  for (const row of access) {
    if (!listed.has(row.name)) templates.push(describeTemplate(row.name, null));
  }

  return {
    sync: {
      revision: sync.revision,
      seeded: sync.seeded,
      mod_seen_at: sync.mod_seen_at,
      mod_applied_revision: sync.mod_applied_revision
    },
    rules: meta.map((m) => {
      const row = ruleByKey.get(m.key);
      return {
        key: m.key,
        title: m.title,
        group: m.grp,
        kind: m.kind,
        min: m.limit_min,
        max: m.limit_max,
        word: m.limit_word,
        note: m.note,
        value: row ? row.value : null,
        limit: row ? row.limit_value : null,
        applied: row ? applied(row.revision) : true,
        updated_at: row ? row.updated_at : null,
        updated_by: row ? row.updated_by : null
      };
    }),
    templates,
    players: people
      .filter((p) => p.whitelist_status === 'approved')
      .map((p) => ({ username: p.username, steam_id: p.steam_id }))
  };
}

function describeRule(meta, value, limit) {
  const metres = limit === null ? '' : `, ${limit} м`;
  switch (meta.kind) {
    case 'toggle': return value ? 'да' : 'нет';
    case 'limit': return `${value ? 'можно' : 'нельзя'}${metres}`;
    case 'choice': return CHOICE_WORDS[value];
    case 'choicelimit': return `${CHOICE_WORDS[value]}${metres}`;
    default: return `${value} ${meta.limit_word || ''}`.trim();
  }
}

/** An admin's change to one rule, checked against what the mod says the rule is. */
async function setRule(key, body, by) {
  const meta = await get('SELECT * FROM game_build_rule_meta WHERE key = ?', [key]);
  if (!meta) {
    return { status: 404, error: 'Такого правила сервер не знает: он ещё не присылал список или правило убрали из мода' };
  }

  const value = toInt(body.value);
  let limit = body.limit === undefined || body.limit === null || body.limit === '' ? null : toInt(body.limit);
  const min = meta.limit_min ?? 0;
  const max = meta.limit_max ?? MAX_VALUE;
  const inBounds = (n) => n !== null && n >= min && n <= max;

  const hasLimit = meta.kind === 'limit' || meta.kind === 'choicelimit';
  if (meta.kind === 'toggle' || meta.kind === 'limit') {
    if (value !== 0 && value !== 1) return { status: 400, error: 'Значение — да или нет' };
  } else if (meta.kind === 'choice' || meta.kind === 'choicelimit') {
    if (![0, 1, 2].includes(value)) return { status: 400, error: 'Значение — нельзя, даром или платно' };
  } else if (!inBounds(value)) {
    return { status: 400, error: `${meta.title} — от ${min} до ${max}` };
  }

  if (hasLimit) {
    if (limit === null) {
      const current = await get('SELECT limit_value FROM game_build_rules WHERE key = ?', [key]);
      limit = current ? current.limit_value : null;
    }
    if (!inBounds(limit)) {
      return { status: 400, error: `${meta.limit_word || 'Лимит'} — от ${min} до ${max} м` };
    }
  } else {
    limit = null;
  }

  const revision = await nextRevision();
  await upsertRule(key, value, limit, revision, by);
  return { status: 200, revision, title: meta.title, said: describeRule(meta, value, limit) };
}

/** An admin's change to who may build one template: everyone, the players named, or both. */
async function setTemplate(body, templateFiles, by) {
  const nameError = templateNameError(body.name);
  if (nameError) return { status: 400, error: nameError };
  const name = body.name;

  const current = await get('SELECT for_all, players FROM game_template_access WHERE name = ?', [name]);
  const file = templateFiles.find((t) => t.name === name);
  if (!current && !file) return { status: 404, error: 'Такой постройки на сервере нет' };

  let forAll = current ? current.for_all : !!(file && file.forPlayers);
  let players = current ? current.players : ((file && file.allowed) || []).join(',');

  if (body.for_all !== undefined) {
    if (typeof body.for_all !== 'boolean') return { status: 400, error: 'for_all — да или нет' };
    forAll = body.for_all;
  }
  if (body.players !== undefined) {
    const normalized = normalizePlayers(body.players);
    if (normalized.error) return { status: 400, error: normalized.error };
    players = normalized.ids.join(',');
  }

  const revision = await nextRevision();
  await upsertTemplate(name, forAll, players, revision, by);
  return { status: 200, revision, name, forAll, count: splitPlayers(players).length };
}

module.exports = { pullText, pushFromMod, overview, setRule, setTemplate };
