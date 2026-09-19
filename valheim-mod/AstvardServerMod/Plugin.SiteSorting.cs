using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// По какой полке раскладывать предмет — то, что решили на сайте.
        ///
        /// The mod sorts by the game's own item type, and for most things that is right.
        /// But the type is the game's word, not the word of whoever keeps the base: a deer
        /// hide is a «material» to the game, the same word it uses for stone, and tar lives
        /// with the potions at one base and with the materials at the next. There is nothing
        /// the mod can say to that, so the answer is given elsewhere and fetched.
        ///
        /// The catalogue goes up, the choices come down. Up, because the site has no idea
        /// what the game contains and should not have to: a new item in a new version turns
        /// up on the page without a line of site code changing. Down, only what a person
        /// actually chose - a list of four hundred «this one stays where it was» would be a
        /// kilobyte a minute carrying no decision at all.
        ///
        /// And then on to the clients, because the sorter runs there: the server is only the
        /// one place that has the token.
        /// </summary>
        private static ConfigEntry<string> _siteSortingUrl;

        private static ConfigEntry<int> _siteSortingSeconds;

        // A choice made on the site is not urgent - nothing is waiting on it but a chest.
        private const int DefaultSiteSortingSeconds = 60;

        private const int MinSiteSortingSeconds = 10;

        private const int MaxSiteSortingSeconds = 3600;

        private static float _siteSortingNextAt;

        private static bool _siteSortingBusy;

        private static bool _siteSortingSent;

        private static int _siteSortingApplied;

        private static string _siteSortingLastProblem;

        internal static void BindSiteSorting(ConfigFile config)
        {
            _siteSortingUrl = config.Bind("Сайт", "SortingUrl", "",
                "Откуда сервер берёт, куда сортировщик кладёт какой предмет, и куда отправляет список "
                + "предметов игры. Пусто — рядом со списками: ListsUrl, где «lists» заменено на "
                + "«sorting». Токен тот же — ListsToken.");
            _siteSortingSeconds = config.Bind("Сайт", "SortingSeconds", DefaultSiteSortingSeconds,
                "Как часто спрашивать сайт о раскладке, в секундах, от 10 до 3600.");
        }

        private static string SiteSortingUrl()
        {
            return SiteSync.SiblingUrlFrom(_siteSortingUrl != null ? _siteSortingUrl.Value : "",
                                           _siteListsUrl != null ? _siteListsUrl.Value : "", "sorting");
        }

        /// <summary>From TickSiteLists, on the server: starts a round when one is due.</summary>
        private void TickSiteSorting()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || _siteSortingBusy) return;

            var url = SiteSortingUrl();
            var token = SiteToken();
            if (url.Length == 0 || token.Length == 0) return;

            var now = Time.realtimeSinceStartup;
            if (now < _siteSortingNextAt) return;

            var seconds = Mathf.Clamp(
                _siteSortingSeconds != null ? _siteSortingSeconds.Value : DefaultSiteSortingSeconds,
                MinSiteSortingSeconds, MaxSiteSortingSeconds);

            _siteSortingNextAt = now + seconds;
            StartCoroutine(SyncSiteSorting(url, token));
        }

        private IEnumerator SyncSiteSorting(string url, string token)
        {
            _siteSortingBusy = true;
            try
            {
                if (!_siteSortingSent)
                {
                    var sent = false;
                    yield return PostCatalogue(url, token, (ok) => sent = ok);
                    if (!sent) yield break;

                    _siteSortingSent = true;
                }

                string answer = null;
                using (var request = UnityWebRequest.Get(
                    url + (url.Contains("?") ? "&" : "?") + "rev=" + _siteSortingApplied))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                    request.timeout = 15;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        SaySortingProblem(request.error);
                        yield break;
                    }

                    answer = request.downloadHandler != null ? request.downloadHandler.text : "";
                }

                _siteSortingLastProblem = null;

                // The site has nothing to choose from - it was wiped, or this is a new one.
                // Sending the catalogue again is the whole of the repair.
                if (answer != null && answer.StartsWith("seed needed"))
                {
                    _siteSortingSent = false;
                    yield break;
                }

                ApplySortingAnswer(answer);
            }
            finally
            {
                _siteSortingBusy = false;
            }
        }

        /// <summary>
        /// «rev N», а дальше строки «ключ=номер». Ревизия — просто порядок: по ней сайт
        /// видит, доехало ли до сервера, а сервер — надо ли что-то делать вообще.
        /// </summary>
        private static void ApplySortingAnswer(string answer)
        {
            if (answer == null) return;

            var lines = answer.Split('\n');
            var revision = _siteSortingApplied;
            var chosen = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith("rev "))
                {
                    int read;
                    if (int.TryParse(trimmed.Substring(4).Trim(), out read)) revision = read;
                    continue;
                }

                if (chosen.Length > 0) chosen.Append(';');
                chosen.Append(trimmed);
            }

            var packed = chosen.ToString();
            if (revision == _siteSortingApplied && packed == Sorting.PackChosen()) return;

            _siteSortingApplied = revision;
            Sorting.ReadChosen(packed);
            _sortKindsPacked = packed;

            Log.LogInfo($"[AstvardServerMod] Sorting: the site chose for {lines.Length - 1} kinds (rev {revision}).");
        }

        /// <summary>
        /// На каком языке спрашивать у игры имена предметов для каталога.
        ///
        /// Сервер локализует своим языком, а он английский: на сайт уезжали «Deer Hide» и
        /// «Barley Flour», и найти оленью шкуру по слову «шкура» было нечем. Все языки
        /// лежат в одном CSV игры, так что довольно попросить у неё другой на время сборки
        /// каталога. Настройкой это не сделано намеренно: сайт русский целиком.
        /// </summary>
        private const string CatalogueLanguage = "Russian";

        // Localization.Clear() закрыт, но без него остаётся кэш на сотню строк: имя,
        // прочитанное до переключения, вернулось бы на прежнем языке, и в каталоге легла бы
        // горсть английских строк среди русских. SetupLanguage кладёт слова поверх и кэша
        // не трогает — чистит только он.
        private static readonly System.Reflection.MethodInfo MLocalizationClear =
            AccessTools.Method(typeof(Localization), "Clear");

        private static bool _saidNoLocalizationClear;

        /// <summary>
        /// Просит игру говорить на этом языке. То же самое, что её собственный SetLanguage
        /// (вычистить словарь и загрузить заново), но без записи в настройки и без события
        /// о смене языка: на выделенном сервере ни то, ни другое никому не нужно.
        /// </summary>
        private static bool SpeakLanguage(Localization words, string language)
        {
            if (MLocalizationClear != null)
            {
                MLocalizationClear.Invoke(words, null);
            }
            else if (!_saidNoLocalizationClear)
            {
                _saidNoLocalizationClear = true;
                Log.LogWarning("[AstvardServerMod] Localization.Clear is gone: some item names may keep the old language.");
            }

            if (!string.IsNullOrEmpty(language) && words.SetupLanguage(language)) return true;

            Log.LogWarning($"[AstvardServerMod] Sorting: the game has no '{language}' words.");
            return false;
        }

        /// <summary>
        /// Вернуть язык, на котором сервер говорил. Обязано получиться: словарь к этому
        /// моменту вычищен, и сервер, которому не нашлось ни одного языка, до конца
        /// сессии отвечает ключами вида `$item_wood`. Поэтому запасной — английский: с
        /// него игра и начинает, и его колонка в CSV та, из которой берутся непереведённые
        /// слова, то есть она есть всегда.
        /// </summary>
        private static void RestoreLanguage(Localization words, string previous)
        {
            if (SpeakLanguage(words, previous)) return;
            if (previous != "English") SpeakLanguage(words, "English");
        }

        /// <summary>
        /// Каталог: что в игре вообще есть. Шлётся раз за запуск, и правильно, что при
        /// каждом: предметы могли появиться с обновлением игры, а имена — с переводом.
        /// </summary>
        private IEnumerator PostCatalogue(string url, string token, System.Action<bool> done)
        {
            var body = new System.Text.StringBuilder();
            body.Append("#categories ");
            for (var i = 0; i < Sorting.Count; i++)
            {
                if (i > 0) body.Append('|');
                body.Append(Sorting.Title(i));
            }

            body.Append('\n');

            // Пока игра не разложила свой ObjectDB, рассказывать нечего - и переключать
            // язык тоже: этот заход повторяется раз в минуту, пока не выйдет, а каждое
            // переключение перечитывает весь CSV игры целиком.
            var db = ObjectDB.instance;
            if (db == null || db.m_items == null)
            {
                done(false);
                yield break;
            }

            // Язык меняется только на время сборки строк, и между переключением и
            // возвратом нет ни одного yield: иначе чужой кадр застал бы сервер говорящим
            // не на том языке.
            var words = Localization.instance;

            // Язык берётся из настроек игры, а там он бывает и пустым: сама игра проверяет
            // это перед тем, как применить. Пустым его возвращать нельзя — колонки с таким
            // именем в CSV нет, и возврат оставил бы сервер без слов.
            var previous = words != null ? words.GetSelectedLanguage() : null;
            if (string.IsNullOrEmpty(previous)) previous = "English";

            var switched = words != null && previous != CatalogueLanguage
                           && SpeakLanguage(words, CatalogueLanguage);

            // Не вышло — вернуть прежний сразу: язык грузится поверх вычищенного словаря.
            if (words != null && !switched && previous != CatalogueLanguage) RestoreLanguage(words, previous);

            var counted = 0;
            var skipped = 0;
            try
            {
                foreach (var prefab in db.m_items)
                {
                    var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                    if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;

                    var shared = drop.m_itemData.m_shared;
                    var kind = CleanForCatalogue(shared.m_name);
                    if (kind.Length == 0) continue;

                    // Имя без `$` — не ключ перевода, а внутреннее имя: в ObjectDB рядом с
                    // предметами лежат атаки существ (`WolfAttack1`, `claw`, `Unarmed`) —
                    // у них тоже есть ItemDrop. Сортировать их некуда, и в списке админа
                    // это два десятка строк, которые он будет читать и не понимать.
                    if (kind[0] != '$')
                    {
                        skipped++;
                        continue;
                    }

                    body.Append(kind).Append('|')
                        .Append(CleanForCatalogue(ItemTitle(drop.m_itemData))).Append('|')
                        .Append(CleanForCatalogue(shared.m_itemType.ToString())).Append('|')
                        .Append(DefaultCategoryOf(drop.m_itemData))
                        .Append('\n');

                    counted++;
                }
            }
            finally
            {
                if (switched) RestoreLanguage(words, previous);
            }

            if (counted == 0)
            {
                // ObjectDB на месте, а предметов в нём нет - такого быть не должно, но
                // пустой каталог сайт всё равно отвергнет.
                done(false);
                yield break;
            }

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(
                    System.Text.Encoding.UTF8.GetBytes(body.ToString()));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", "Bearer " + token);
                request.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                request.timeout = 30;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SaySortingProblem(request.error);
                    done(false);
                    yield break;
                }

                // Язык в строке не для красоты: имена, уехавшие не на том языке, иначе
                // видны только на самом сайте и через сутки. Отброшенное — тем же: фильтр,
                // съевший лишнего, иначе не отличить от игры, потерявшей предметы.
                Log.LogInfo($"[AstvardServerMod] Sorting: told the site about {counted} items"
                            + $" ({(switched ? CatalogueLanguage : previous)} names,"
                            + $" {skipped} skipped as not items).");
                done(true);
            }
        }

        /// <summary>Ни переносов, ни разделителей: строка каталога иначе разорвётся.</summary>
        private static string CleanForCatalogue(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var kept = new System.Text.StringBuilder();
            foreach (var symbol in text)
            {
                if (symbol == '|' || symbol == '\n' || symbol == '\r') continue;
                kept.Append(symbol);
            }

            return kept.ToString().Trim();
        }

        // The site being down is one line in the log, not one a minute.
        private static void SaySortingProblem(string problem)
        {
            if (_siteSortingLastProblem == problem) return;

            _siteSortingLastProblem = problem;
            Log.LogWarning($"[AstvardServerMod] Sorting: the site would not answer — {problem}");
        }

        // ---------------- to the clients ----------------

        private const string RpcSortKinds = "AstvardSortKinds";

        private static string _sortKindsPacked = "";

        private static readonly Dictionary<long, string> SortKindsSent = new Dictionary<long, string>();

        internal static void RegisterSiteSortingRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcSortKinds, OnSortKinds);
        }

        /// <summary>
        /// From the zones tick, which already walks the peers: each one gets the choices when
        /// they differ from what it was last sent - which on an ordinary second is never.
        /// </summary>
        private static void SendSortKinds(ZNetPeer peer)
        {
            if (peer == null || peer.m_socket == null) return;

            string last;
            if (SortKindsSent.TryGetValue(peer.m_uid, out last) && last == _sortKindsPacked) return;

            SortKindsSent[peer.m_uid] = _sortKindsPacked;
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, RpcSortKinds, _sortKindsPacked);
        }

        private static void OnSortKinds(long sender, string text)
        {
            var net = ZNet.instance;
            if (net == null || net.IsServer()) return;

            Sorting.ReadChosen(text);
        }
    }
}
