const fs = require('node:fs/promises');
const path = require('node:path');
const logger = require('../logger');

/**
 * Что мод уже записал на диск — руны игроков, общие постройки и правила сервера.
 *
 * Мод и сайт живут на одной машине, и в compose том `/srv/valheim/server`
 * смонтирован один в один: файл внутри контейнера — это файл на VPS. Поэтому
 * никакого сетевого обмена не нужно, а мод и не умеет: сетевого кода в нём нет ни
 * строки. Сайт просто читает то, что мод пишет для себя.
 *
 * Всё здесь — чтение и только чтение. Файлы принадлежат игровому серверу, и
 * единственный, кто вправе их менять, — он сам. И раз файлы чужие, разбор обязан
 * повторять правила мода буквально: где он зажимает число или сверяет значение
 * заголовка, там же это делаем и мы, иначе сайт уверенно рассказывает то, чего в
 * игре нет.
 */

// Игра пишет руны раз в минуту и на выходе, шаблоны — когда админ их отправляет.
// Читать это на каждый запрос незачем: страница открыта у нескольких человек, а
// файлы меняются раз в минуты. Кэш живёт полминуты — столько же, сколько опрос
// состояния серверов.
const CACHE_MS = 30 * 1000;

// Сколько файлов и байт готовы прочитать за раз. Папка шаблонов растёт сама: мод
// разрешает каждому игроку прислать пять построек, а удалённые админом только
// переезжают в подпапку. Без потолка один большой файл или сотня присланных
// превращают открытый всем маршрут в способ занять память.
const MAX_TEMPLATE_FILES = 200;
const MAX_TEMPLATE_BYTES = 2 * 1024 * 1024;
const MAX_RUNES_BYTES = 4 * 1024 * 1024;

// Заголовки пишет игрок, когда делится своей постройкой: длину мод не режет, а на
// странице это заголовок карточки.
const MAX_NAME = 120;
const MAX_AUTHOR = 60;

const cache = new Map();

/**
 * Кэш держит сам промис, а не готовое значение: пока первое чтение идёт, соседние
 * запросы должны ждать его, а не запускать своё. Иначе десяток одновременных
 * запросов к открытому маршруту читает папку десять раз.
 */
function cached(key, load) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < CACHE_MS) return hit.value;

  const value = load();
  cache.set(key, { at: Date.now(), value });
  // Неудачное чтение не запирается в кэше на полминуты.
  value.catch(() => cache.delete(key));
  return value;
}

// «Файла нет» — это «мод ещё не писал», и молчать тут правильно. Всё остальное —
// права, битый том, папка вместо файла — выглядит на сайте так же, как пустота, и
// без записи в журнал такое не диагностируется вовсе.
function complain(what, err) {
  if (err && err.code !== 'ENOENT') logger.warn(`[valheim] ${what}: ${err.message}`);
}

function trimTo(value, limit) {
  const text = String(value || '').trim();
  return text.length > limit ? `${text.slice(0, limit)}…` : text;
}

/**
 * Руны: `<SteamID64>\t<баланс>\t<секунды до следующей>\t<имя персонажа>`, первая
 * строка — комментарий. Формат задан в Plugin.Currency.cs (SavePurses), файл
 * пишется через .tmp + Replace, так что оборванной записи тут не бывает.
 *
 * Имя в файле — имя персонажа в игре, а не ник на сайте: сопоставлять их —
 * работа вызывающего, у него есть база.
 */
async function readRunes(filePath) {
  return cached(`runes:${filePath}`, async () => {
    let text;
    try {
      const stat = await fs.stat(filePath);
      if (stat.size > MAX_RUNES_BYTES) {
        logger.warn(`[valheim] файл рун больше ${MAX_RUNES_BYTES} байт — пропускаю`);
        return [];
      }
      text = await fs.readFile(filePath, 'utf8');
    } catch (err) {
      complain('не прочитать файл рун', err);
      return [];
    }

    return text.split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line && !line.startsWith('#'))
      .map((line) => {
        const parts = line.split('\t');
        if (parts.length < 3) return null;
        const balance = Number(parts[1]);
        const seconds = Number(parts[2]);
        if (!/^\d+$/.test(parts[0]) || !Number.isFinite(balance) || !Number.isFinite(seconds)) return null;
        return {
          steamId: parts[0],
          runes: Math.max(0, Math.trunc(balance)),
          // Остаток часа до следующей руны; на страницу он идёт только как часть
          // общего наигранного времени.
          seconds: Math.max(0, Math.trunc(seconds)),
          character: trimTo(parts.slice(3).join('\t'), MAX_AUTHOR)
        };
      })
      .filter(Boolean);
  });
}

// Заголовки шаблона пишет Plugin.SharedTemplates.cs.
const HEADERS = {
  '#name': ['name', MAX_NAME],
  '#category': ['category', MAX_NAME],
  '#author': ['author', MAX_AUTHOR]
};

/**
 * Общие постройки: по файлу на шаблон, заголовки сверху, дальше строки деталей
 * вида `prefab;x;y;z;qx;qy;qz;qw`. Число деталей считается так же, как считает мод
 * — по строкам тела.
 *
 * Чего здесь нет и не должно быть:
 *
 * - подпапки `deleted` — это корзина самого мода: админ эти постройки убрал;
 * - файлов с заголовком `#from` — так мод помечает **присланное игроком**, что
 *   админ ещё не разобрал («is open to nobody until an admin opens it», его же
 *   комментарий). Имя и категорию там набирает игрок, а страница у нас открыта
 *   всему интернету — публиковать такое до решения админа значит отдать главную
 *   страницу любому, у кого есть вайтлист;
 * - шаблонов без `#name`: имя файла — это имя на диске, и на сайте ему не место.
 */
async function readSharedTemplates(dirPath) {
  return cached(`templates:${dirPath}`, async () => {
    let entries;
    try {
      entries = await fs.readdir(dirPath, { withFileTypes: true });
    } catch (err) {
      complain('не прочитать папку шаблонов', err);
      return [];
    }

    const files = entries
      .filter((e) => e.isFile() && e.name.toLowerCase().endsWith('.txt'))
      .map((e) => e.name)
      .sort((a, b) => a.localeCompare(b, 'ru'));

    if (files.length > MAX_TEMPLATE_FILES) {
      logger.warn(`[valheim] шаблонов в папке ${files.length}, показываю первые ${MAX_TEMPLATE_FILES}`);
      files.length = MAX_TEMPLATE_FILES;
    }

    const templates = [];
    for (const file of files) {
      const full = path.join(dirPath, file);
      let text;
      try {
        const stat = await fs.stat(full);
        if (stat.size > MAX_TEMPLATE_BYTES) {
          logger.warn(`[valheim] шаблон ${file} больше ${MAX_TEMPLATE_BYTES} байт — пропускаю`);
          continue;
        }
        text = await fs.readFile(full, 'utf8');
      } catch (err) {
        complain(`не прочитать шаблон ${file}`, err);
        continue;
      }

      const template = { name: '', category: '', author: '', pieces: 0, forPlayers: false };
      let submitted = false;

      for (const raw of text.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line) continue;
        if (line.startsWith('#')) {
          const key = line.split(/\s+/)[0].toLowerCase();
          const value = line.slice(key.length).trim();
          // Мод открывает постройку игрокам только строкой ровно «#players yes»
          // (константа PlayersHeader), а закрывая — удаляет её целиком. Значит и
          // судить надо по значению, иначе плашка «открыта игрокам» появится там,
          // где никто ничего не открывал.
          if (key === '#players') template.forPlayers = value === 'yes';
          if (key === '#from') submitted = true;
          const header = HEADERS[key];
          // Пустое значение мод пропускает и поле не трогает — так же и здесь,
          // иначе строка «#name» без значения стирает имя.
          if (header && value) template[header[0]] = trimTo(value, header[1]);
          continue;
        }
        // Строка детали: имя префаба и семь чисел через «;», а у сундука следом
        // девятым полем его содержимое. Считаем и то и другое, но только их: случайная
        // строка в файле деталью быть не должна.
        if (line.split(';').length >= 8) template.pieces += 1;
      }

      if (submitted || !template.name || template.pieces === 0) continue;
      templates.push(template);
    }
    return templates;
  });
}

// Конфиг BepInEx — обычный ini с описаниями в комментариях. Нужен ровно один
// ключ: по нему считается, сколько наиграно за уже выданные руны.
const MINUTES_PER_RUNE_RE = /^\s*MinutesPerRune\s*=\s*(-?\d+)\s*$/m;

const DEFAULT_MINUTES_PER_RUNE = 60;

// Границы мода: Mathf.Clamp(значение, 1, MaxMinutesPerRune) в Plugin.Currency.cs.
// Число вне их сервер зажимает у себя, а в файле оставляет как есть — значит
// сайт, поверивший файлу, посчитает часы не по той ставке, по которой они на
// самом деле начислялись.
const MIN_MINUTES_PER_RUNE = 1;
const MAX_MINUTES_PER_RUNE = 1440;

async function readMinutesPerRune(cfgPath) {
  return cached(`cfg:${cfgPath}`, async () => {
    let text;
    try {
      text = await fs.readFile(cfgPath, 'utf8');
    } catch (err) {
      complain('не прочитать конфиг мода', err);
      return DEFAULT_MINUTES_PER_RUNE;
    }

    const m = text.match(MINUTES_PER_RUNE_RE);
    // Ключа нет — у мода тоже сработает его собственное умолчание.
    if (!m) return DEFAULT_MINUTES_PER_RUNE;

    const value = Number(m[1]);
    if (!Number.isFinite(value)) return DEFAULT_MINUTES_PER_RUNE;
    return Math.min(MAX_MINUTES_PER_RUNE, Math.max(MIN_MINUTES_PER_RUNE, Math.trunc(value)));
  });
}

module.exports = { readRunes, readSharedTemplates, readMinutesPerRune, DEFAULT_MINUTES_PER_RUNE };
