using System.Collections.Generic;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using HarmonyLib;

namespace AstvardServerMod
{
    /// <summary>
    /// ОПЫТ, а не часть мода. Ставит человекоподобное существо, делает его своим,
    /// неуязвимым и отправляет ходить за игроком — чтобы увидеть своими глазами,
    /// годится ли оно на роль помощника: ходит ли, как выглядит, не дерётся ли.
    ///
    /// Проверяем ровно то, чего нельзя узнать чтением кода: походку, вид и то, как
    /// существо ведёт себя на базе среди построек. Всё остальное - сундуки, станции,
    /// зоны - у мода уже есть.
    ///
    /// Команда всё делает публичными вызовами: SetTamed и SetFollowTarget объявлены
    /// открытыми, так что опыт не добавляет ни одной цели, которую обновление игры
    /// сломает молча. Снимать урон приходится патчем, но тоже на публичный метод -
    /// его проверит компилятор.
    /// </summary>
    public partial class Plugin
    {
        // Кого мы поставили сами: по ним и решает патч, кому не больно. Список живёт
        // до конца сессии - опыт одноразовый, переживать перезапуск ему незачем.
        internal static readonly HashSet<ZDOID> TestHelpers = new HashSet<ZDOID>();

        /// <summary>Имена, под которыми двергов стоит поискать в сцене.</summary>
        private static readonly string[] HelperCandidates =
        {
            "Dverger", "DvergerArbalest", "DvergerMage", "DvergerMageFire",
            "DvergerMageIce", "DvergerMageNature", "DvergerMageSupport"
        };

        internal static void SpawnTestHelper(string wanted)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                Log.LogWarning("[Опыт] сцены нет, ставить некуда.");
                return;
            }

            // Имя префаба мы знаем только по спавнерам, а они называются иначе.
            // Поэтому перебираем кандидатов и говорим, какой нашёлся.
            GameObject prefab = null;
            var tried = new List<string>();

            foreach (var name in string.IsNullOrEmpty(wanted) ? HelperCandidates : new[] { wanted })
            {
                tried.Add(name);
                prefab = scene.GetPrefab(name);
                if (prefab != null)
                {
                    Log.LogInfo($"[Опыт] префаб найден: {name}");
                    break;
                }
            }

            if (prefab == null)
            {
                var message = "не нашёл: " + string.Join(", ", tried);
                Log.LogWarning($"[Опыт] {message}");
                player.Message(MessageHud.MessageType.Center, $"Помощник: {message}");
                return;
            }

            var where = player.transform.position + player.transform.forward * 3f;
            var spawned = Object.Instantiate(prefab, where, Quaternion.identity);

            var view = spawned.GetComponent<ZNetView>();
            var character = spawned.GetComponent<Character>();
            var ai = spawned.GetComponent<MonsterAI>();

            Log.LogInfo($"[Опыт] поставлен {prefab.name}: ZNetView {(view != null)}, "
                        + $"Character {(character != null)}, MonsterAI {(ai != null)}, "
                        + $"Tameable {(spawned.GetComponent<Tameable>() != null)}");

            if (view != null && view.IsValid()) TestHelpers.Add(view.GetZDO().m_uid);

            // Свой - чтобы не дрался и чтобы игра считала его ручным.
            if (character != null) character.SetTamed(true);

            // Идти за мной. Это и есть проверка ходьбы: путь ищет сама игра.
            if (ai != null)
            {
                ai.SetFollowTarget(player.gameObject);
                Log.LogInfo("[Опыт] отправлен следовать за игроком.");
            }
            else
            {
                Log.LogWarning("[Опыт] у существа нет MonsterAI — командовать нечем.");
            }

            player.Message(MessageHud.MessageType.Center, $"Помощник поставлен: {prefab.name}");
        }

        // ---------------- страница опыта ----------------

        internal static GameObject HelperButton;

        internal static GameObject HelperHint;

        internal static GameObject HelperSpawnButton;

        internal static GameObject HelperClearButton;

        /// <summary>
        /// «Функции» → «Помощник (опыт)». Через консоль это тоже работает, но набирать
        /// команду посреди базы неудобно, а опыт весь в том, чтобы поставить его там, где
        /// стоишь, и посмотреть, как он ходит между постройками.
        ///
        /// Только админу: ставит живое существо в мир.
        /// </summary>
        private void CreateHelperWidgets(Jotunn.Managers.GUIManager gui)
        {
            HelperButton = MakeButton(gui, "Помощник (опыт)", () =>
            {
                MenuState = StateHelper;
                RefreshMenu();
            });

            HelperHint = MakeText(gui, "");

            HelperSpawnButton = MakeButton(gui, "Поставить помощника", () =>
            {
                SpawnTestHelper(null);
                InventoryGui.instance?.Hide();
                RefreshMenu();
            });

            HelperClearButton = MakeButton(gui, "", () =>
            {
                RemoveTestHelpers();
                RefreshMenu();
            });
        }

        private static void RefreshHelperVisibility(bool admin)
        {
            SetLabel(HelperClearButton, TestHelpers.Count > 0
                ? $"Убрать помощников ({TestHelpers.Count})"
                : "Убрать помощников");

            var hint = HelperHint != null ? HelperHint.GetComponentInChildren<UnityEngine.UI.Text>(true) : null;
            if (hint != null)
                hint.text = $"Опыт, а не готовая вещь.{NEWLINE}Ставит дверга в трёх метрах{NEWLINE}"
                            + $"перед тобой, своим и{NEWLINE}неуязвимым, и отправляет{NEWLINE}"
                            + $"ходить за тобой.{NEWLINE}Смотрим: как ходит, как{NEWLINE}"
                            + $"выглядит, не дерётся ли.";

            SetActive(HelperButton, admin && MenuState == StateFeatures);

            var page = admin && MenuState == StateHelper;
            SetActive(HelperHint, page);
            SetActive(HelperSpawnButton, page);
            SetActive(HelperClearButton, page);
        }

        /// <summary>Убирает поставленных опытом — чтобы не оставлять их в мире.</summary>
        internal static void RemoveTestHelpers()
        {
            var removed = 0;

            foreach (var id in new List<ZDOID>(TestHelpers))
            {
                var go = ZNetScene.instance?.FindInstance(id);
                var view = go != null ? go.GetComponent<ZNetView>() : null;
                if (view == null || !view.IsValid()) continue;

                if (!view.IsOwner()) view.ClaimOwnership();
                view.Destroy();
                removed++;
            }

            TestHelpers.Clear();
            Log.LogInfo($"[Опыт] убрано помощников: {removed}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Убрано: {removed}");
        }
    }

    /// <summary>
    /// Помощнику не больно. Патч на публичный Damage: компилятор присмотрит за его
    /// сигнатурой, в отличие от приватного RPC_Damage, который менялся бы молча.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    public static class TestHelperTakesNoDamage
    {
        private static bool Prefix(Character __instance)
        {
            var view = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return true;

            return !Plugin.TestHelpers.Contains(view.GetZDO().m_uid);
        }
    }

    public class HelperTestCommand : ConsoleCommand
    {
        public override string Name => "astvardhelper";

        public override string Help => "Опыт: поставить помощника (необязательно имя префаба), "
                                       + "astvardhelper clear — убрать";

        public override void Run(string[] args)
        {
            if (args.Length > 0 && args[0] == "clear")
            {
                Plugin.RemoveTestHelpers();
                return;
            }

            Plugin.SpawnTestHelper(args.Length > 0 ? args[0] : null);
        }
    }
}
