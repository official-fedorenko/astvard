using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SpawnerButton;

        internal static GameObject SpawnerHint;

        internal static GameObject SpawnerRemoveButton;

        internal const int MaxSpawnerButtons = 8;

        internal static readonly GameObject[] SpawnerGroupButtons = new GameObject[MaxSpawnerButtons];

        internal static readonly GameObject[] SpawnerKindButtons = new GameObject[MaxSpawnerButtons];

        /// <summary>What the button says, and the prefab the game files it under.</summary>
        private sealed class SpawnerKind
        {
            public SpawnerKind(string label, string prefab)
            {
                Label = label;
                Prefab = prefab;
            }

            public string Label { get; }

            public string Prefab { get; }
        }

        private sealed class SpawnerGroup
        {
            public SpawnerGroup(string label, params SpawnerKind[] kinds)
            {
                Label = label;
                Kinds = kinds;
            }

            public string Label { get; }

            public SpawnerKind[] Kinds { get; }
        }

        // Grouped by where the creature belongs rather than alphabetically: an admin
        // dropping spawners is furnishing a place, and what fits a swamp is the question
        // being asked. Every name is checked against the running game before its button
        // appears, the same as the weather list — the names were read out of the game's
        // own data files, and a guess that is wrong disappears instead of doing nothing.
        private static readonly SpawnerGroup[] SpawnerGroups =
        {
            new SpawnerGroup("Чёрный лес",
                new SpawnerKind("Серый карлик", "Spawner_Greydwarf"),
                new SpawnerKind("Карлик-элита", "Spawner_Greydwarf_Elite"),
                new SpawnerKind("Карлик-шаман", "Spawner_Greydwarf_Shaman"),
                new SpawnerKind("Гнездо карликов", "Spawner_GreydwarfNest"),
                new SpawnerKind("Скелет", "Spawner_Skeleton"),
                new SpawnerKind("Призрак", "Spawner_Ghost"),
                new SpawnerKind("Тролль", "Spawner_Troll")),

            new SpawnerGroup("Болото",
                new SpawnerKind("Драугр", "Spawner_Draugr"),
                new SpawnerKind("Драугр-элита", "Spawner_Draugr_Elite"),
                new SpawnerKind("Драугр-лучник", "Spawner_Draugr_Ranged"),
                new SpawnerKind("Куча драугров", "Spawner_DraugrPile"),
                new SpawnerKind("Слизь", "Spawner_Blob"),
                new SpawnerKind("Слизь-элита", "Spawner_BlobElite"),
                new SpawnerKind("Пиявка", "Spawner_Leech_cave"),
                new SpawnerKind("Дух", "Spawner_Wraith")),

            new SpawnerGroup("Горы",
                new SpawnerKind("Ульв", "Spawner_Ulv"),
                new SpawnerKind("Фенринг", "Spawner_Fenring"),
                new SpawnerKind("Каменный голем", "Spawner_StoneGolem"),
                new SpawnerKind("Детёныш дракона", "Spawner_Hatchling"),
                new SpawnerKind("Летучая мышь", "Spawner_Bat")),

            new SpawnerGroup("Равнины",
                new SpawnerKind("Фулинг", "Spawner_Goblin"),
                new SpawnerKind("Фулинг-лучник", "Spawner_GoblinArcher"),
                new SpawnerKind("Фулинг-громила", "Spawner_GoblinBrute"),
                new SpawnerKind("Фулинг-шаман", "Spawner_GoblinShaman"),
                new SpawnerKind("Клещ", "Spawner_Tick"),
                new SpawnerKind("Смоляная слизь", "Spawner_BlobTar")),

            new SpawnerGroup("Мгла",
                new SpawnerKind("Искатель", "Spawner_Seeker"),
                new SpawnerKind("Искатель-солдат", "Spawner_SeekerBrute"),
                new SpawnerKind("Двергр-маг", "Spawner_DvergerMage"),
                new SpawnerKind("Двергр-арбалетчик", "Spawner_DvergerArbalest"),
                new SpawnerKind("Звёздный клещ", "Spawner_Tick_stared")),

            new SpawnerGroup("Пепельные земли",
                new SpawnerKind("Обугленный лучник", "Spawner_Charred_Archer"),
                new SpawnerKind("Обугленный маг", "Spawner_Charred_Mage"),
                new SpawnerKind("Обугленный камень", "Spawner_CharredStone"),
                new SpawnerKind("Морген", "Spawner_Morgen"),
                new SpawnerKind("Стервятник", "Spawner_Volture"),
                new SpawnerKind("Бес", "Spawner_imp")),

            new SpawnerGroup("Прочее",
                new SpawnerKind("Кабан", "Spawner_Boar"),
                new SpawnerKind("Курица", "Spawner_Chicken"),
                new SpawnerKind("Культист", "Spawner_Cultist"),
                new SpawnerKind("Павшая валькирия", "Spawner_FallenValkyrie"),
                new SpawnerKind("Ядовитый скелет", "Spawner_Skeleton_poison"))
        };

        private static int _spawnerGroup;

        private static int _shownSpawnerGroups;

        private static int _shownSpawnerKinds;

        private static readonly List<SpawnerKind> ShownKinds = new List<SpawnerKind>();

        /// <summary>
        /// Whether this build of the game has that prefab. The scene's list is filled at
        /// load, so asking it is the only honest answer — a name read out of the data
        /// files can still be one the game never registers.
        /// </summary>
        private static bool KnowsPrefab(string name)
        {
            return ZNetScene.instance != null && ZNetScene.instance.GetPrefab(name) != null;
        }

        private static void KindsIn(int group, List<SpawnerKind> into)
        {
            into.Clear();
            if (group < 0 || group >= SpawnerGroups.Length) return;

            foreach (var kind in SpawnerGroups[group].Kinds)
                if (KnowsPrefab(kind.Prefab)) into.Add(kind);
        }

        /// <summary>
        /// Relabels both pools. Widgets are made once and only their captions change, the
        /// way every other list in the panel works.
        /// </summary>
        internal static void RebuildSpawnerViews()
        {
            _shownSpawnerGroups = Mathf.Min(SpawnerGroups.Length, MaxSpawnerButtons);

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var label = SpawnerGroupButtons[i] != null
                    ? SpawnerGroupButtons[i].GetComponentInChildren<Text>(true)
                    : null;
                if (label != null)
                    label.text = i < SpawnerGroups.Length ? SpawnerGroups[i].Label : "";
            }

            KindsIn(_spawnerGroup, ShownKinds);
            _shownSpawnerKinds = Mathf.Min(ShownKinds.Count, MaxSpawnerButtons);

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var label = SpawnerKindButtons[i] != null
                    ? SpawnerKindButtons[i].GetComponentInChildren<Text>(true)
                    : null;
                if (label != null) label.text = i < ShownKinds.Count ? ShownKinds[i].Label : "";
            }
        }

        internal static void OpenSpawnerGroup(int group)
        {
            _spawnerGroup = group;
            MenuState = StateSpawnerList;
            RefreshMenu();
        }

        /// <summary>
        /// Hands one spawner to the placement preview, so it is aimed the way a blueprint
        /// is: it follows the player, Q and E turn it, a click puts it down and escape
        /// drops it.
        ///
        /// It goes through the copy buffer, which is what placement reads, so whatever
        /// was copied there is lost. That is the same buffer the copy button overwrites
        /// on every use, and anything worth keeping lives in a template file by now.
        /// </summary>
        internal static void PlaceSpawner(int slot)
        {
            if (slot < 0 || slot >= ShownKinds.Count) return;
            // The builder walks the clipboard across a yield, re-reading its count each
            // time round, so emptying it here would stop a build already in progress
            // partway and leave the placement state pointing at nothing. RunCopy and
            // RunCopyToFile already refuse for the same reason.
            if (BuildInProgress) return;

            var kind = ShownKinds[slot];
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kind.Prefab) : null;
            if (prefab == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Нет такого спавнера");
                return;
            }

            Clipboard.Clear();
            Clipboard.Add(new CopiedPiece
            {
                Prefab = kind.Prefab,
                LocalPos = Vector3.zero,
                LocalRot = Quaternion.identity
            });

            StartPlacement($"спавнер «{kind.Label}»");
            InventoryGui.instance?.Hide();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"{kind.Label}: ЛКМ — поставить, P — закрепить");
            Log.LogInfo($"[AstvardServerMod] Placing spawner {kind.Prefab}.");
        }

        /// <summary>How far the undo button reaches.</summary>
        internal const float SpawnerRemoveRadius = 8f;

        /// <summary>
        /// Takes back the last one. A placed spawner has no model and no Piece, so the
        /// hammer cannot see it and neither can the player — without this, putting one
        /// down is permanent short of a console command.
        ///
        /// It removes whatever spawners are in reach, including the ones the world
        /// generated, because a spawner carries nothing that says who made it.
        /// </summary>
        internal static void RemoveNearbySpawners()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null) return;

            var origin = player.transform.position;
            var sqrRadius = SpawnerRemoveRadius * SpawnerRemoveRadius;
            var removed = 0;

            foreach (var spawner in FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
            {
                if (spawner == null) continue;
                if ((spawner.transform.position - origin).sqrMagnitude > sqrRadius) continue;

                // The preview stands in the same scene as the real thing.
                if (GhostRoot != null && spawner.transform.IsChildOf(GhostRoot.transform)) continue;

                var view = spawner.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;

                // ZNetScene.Destroy only drops the ZDO when we own it; without the claim
                // the object comes straight back the next time the zone is read.
                if (!view.IsOwner()) view.ClaimOwnership();
                ZNetScene.instance.Destroy(spawner.gameObject);
                removed++;
            }

            player.Message(MessageHud.MessageType.Center,
                removed == 0
                    ? $"Спавнеров в {SpawnerRemoveRadius:0} м нет"
                    : $"Убрано спавнеров: {removed}");
            Log.LogInfo($"[AstvardServerMod] Removed {removed} spawners within {SpawnerRemoveRadius} m.");
        }
    }
}
