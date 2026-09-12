const fs = require('node:fs/promises');
const path = require('node:path');

/**
 * Что мод уже записал на диск — руны игроков, общие постройки и правила сервера.
 *
 * Мод и сайт живут на одной машине, и в compose том `/srv/valheim/server`
 * смонтирован один в один: файл внутри контейнера — это файл на VPS. Поэтому
 * никакого сетевого обмена не нужно, а мод и не умеет: сетевого кода в нём нет ни
 * строки. Сайт просто читает то, что мод пишет для себя.
 *
 * Всё здесь — чтение и только чтение. Файлы принадлежат игровому серверу, и
 * единственный, кто вправе их менять, — он сам.
 */

// Игра пишет руны раз в минуту и на выходе, шаблоны — когда админ их отправляет.
// Читать это на каждый запрос незачем: страница открыта у нескольких человек, а
// файлы меняются раз в минуты. Кэш живёт полминуты — столько же, сколько опрос
// состояния серверов.
const CACHE_MS = 30 * 1000;

const cache = new Map();

async function cached(key, load) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < CACHE_MS) return hit.value;
  const value = await load();
  cache.set(key, { at: Date.now(), value });
  return value;
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
      text = await fs.readFile(filePath, 'utf8');
    } catch {
      // Мод не запускался, файла ещё нет — это не ошибка, а «пока пусто».
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
          character: parts.slice(3).join('\t').trim()
        };
      })
      .filter(Boolean);
  });
}

// Заголовки шаблона пишет Plugin.SharedTemplates.cs: имя, категория, автор, а у
// присланного игроком ещё и `#from <SteamID>` — его наружу не отдаём.
const HEADERS = {
  '#name': 'name',
  '#category': 'category',
  '#author': 'author'
};

/**
 * Общие постройки: по файлу на шаблон, заголовки сверху, дальше строки деталей
 * вида `prefab;x;y;z;qx;qy;qz;qw`. Число деталей считается так же, как считает мод
 * — по строкам тела.
 *
 * Подпапка `deleted` — корзина самого мода, и её содержимое на сайте показывать
 * нечего: админ эти постройки убрал.
 */
async function readSharedTemplates(dirPath) {
  return cached(`templates:${dirPath}`, async () => {
    let entries;
    try {
      entries = await fs.readdir(dirPath, { withFileTypes: true });
    } catch {
      return [];
    }

    const files = entries
      .filter((e) => e.isFile() && e.name.toLowerCase().endsWith('.txt'))
      .map((e) => e.name)
      .sort((a, b) => a.localeCompare(b, 'ru'));

    const templates = [];
    for (const file of files) {
      let text;
      try {
        text = await fs.readFile(path.join(dirPath, file), 'utf8');
      } catch {
        continue;
      }

      const template = {
        name: file.replace(/\.txt$/i, ''),
        category: '',
        author: '',
        pieces: 0,
        forPlayers: false
      };

      for (const raw of text.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line) continue;
        if (line.startsWith('#')) {
          const key = line.split(/\s+/)[0].toLowerCase();
          if (key === '#players') template.forPlayers = true;
          const field = HEADERS[key];
          if (field) template[field] = line.slice(key.length).trim();
          continue;
        }
        // Строка детали: имя префаба и семь чисел через «;». Считаем только их,
        // чтобы случайная строка в файле не выдавалась за деталь.
        if (line.split(';').length === 8) template.pieces += 1;
      }

      if (template.pieces > 0) templates.push(template);
    }
    return templates;
  });
}

// Конфиг BepInEx — обычный ini с описаниями в комментариях. Нужен ровно один
// ключ: по нему считается, сколько наиграно за уже выданные руны.
const MINUTES_PER_RUNE_RE = /^\s*MinutesPerRune\s*=\s*(\d+)\s*$/m;

const DEFAULT_MINUTES_PER_RUNE = 60;

async function readMinutesPerRune(cfgPath) {
  return cached(`cfg:${cfgPath}`, async () => {
    try {
      const text = await fs.readFile(cfgPath, 'utf8');
      const m = text.match(MINUTES_PER_RUNE_RE);
      const value = m ? Number(m[1]) : DEFAULT_MINUTES_PER_RUNE;
      return Number.isFinite(value) && value > 0 ? value : DEFAULT_MINUTES_PER_RUNE;
    } catch {
      // Значение по умолчанию у мода то же самое (Plugin.Currency.cs).
      return DEFAULT_MINUTES_PER_RUNE;
    }
  });
}

module.exports = { readRunes, readSharedTemplates, readMinutesPerRune, DEFAULT_MINUTES_PER_RUNE };
