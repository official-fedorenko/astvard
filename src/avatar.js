/**
 * Какие адреса аватара сайт соглашается хранить.
 *
 * `avatar_url` попадает и в `<img src>`, и в CSS `background: url("…")`, а в списке
 * обращений поддержки он подставляется в атрибут без экранирования — то есть строка,
 * которую игрок вписал себе в профиль, доезжает до браузера админа как разметка. До
 * сих пор туда принималось всё подряд: `/api/cabinet/me` клал `avatar_url` в базу без
 * единой проверки. Чинить это в каждом месте, где аватар рисуют, — значит чинить
 * вечно; дешевле не пускать на запись то, что не похоже на адрес картинки.
 *
 * Своих ровно два вида: стандартный из `client/avatars` и загруженный в медиатеку.
 * Третий — Steam: его адрес мы не придумываем, а читаем из профиля, и он проверяется
 * тем же правилом, потому что XML с чужого сервера — это тоже ввод.
 */

// Имена файлов в медиатеке уже приведены к этому набору (src/utils.js), а
// стандартные аватары лежат в репозитории.
const OWN_RE = /^\/(avatars|uploads)\/[A-Za-z0-9._-]+$/;

// Steam раздаёт аватары с разных площадок — avatars.fastly, avatars.akamai,
// avatars.cloudflare, — поэтому проверяется не хост целиком, а его конец.
const STEAM_RE = /^https:\/\/(?:[a-z0-9-]+\.)+steamstatic\.com\/[A-Za-z0-9._/-]+\.(?:jpg|jpeg|png)$/;

const MAX_LENGTH = 300;

function isSteamAvatar(value) {
  return typeof value === 'string' && STEAM_RE.test(value);
}

/**
 * Возвращает адрес, который можно класть в базу, или null. Пустая строка — тоже
 * null: «стереть аватар» и «поставить вот этот» приходят разными полями.
 */
function safeAvatarUrl(value) {
  if (typeof value !== 'string') return null;
  const clean = value.trim();
  if (!clean || clean.length > MAX_LENGTH) return null;
  return OWN_RE.test(clean) || STEAM_RE.test(clean) ? clean : null;
}

module.exports = { safeAvatarUrl, isSteamAvatar };
