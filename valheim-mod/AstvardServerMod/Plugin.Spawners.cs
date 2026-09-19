using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SpawnerButton;

        internal static GameObject NestButton;

        internal static GameObject SpawnerHint;

        internal static GameObject SpawnerRemoveButton;

        internal const int MaxSpawnerButtons = 8;

        private const string LivestockHint = "Выбери биом, потом тварь.\nЛКМ — поставить, Esc — отмена,\nP — закрепить, стрелки — сдвиг.\n\n«Мирные» — это живой зверь,\nодин и сразу. Остальное —\nгнездо: оно невидимо, в\nпроекции показан сам зверь,\nи оно подсылает их, пока\nигрок ближе 60 м.\nВ базе игрока (верстак, костёр)\nгнездо молчит.\n«Убрать рядом» сносит все\nгнёзда в 8 м, и родные тоже.\nБуфер копирования будет занят.";

        private const string NestHint = "Выбери биом, потом гнездо.\nЛКМ — поставить, Esc — отмена,\nP — закрепить, стрелки — сдвиг.\n\nЗдесь только гнёзда, и только\nте, что есть в этой игре.\nГнездо невидимо: в проекции\nпоказан сам зверь, а подсылает\nих оно, пока игрок ближе 60 м.\nВ базе игрока (верстак, костёр)\nгнездо молчит.\n«Убрать рядом» сносит все\nгнёзда в 8 м, и родные тоже.\nБуфер копирования будет занят.";

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
        // appears, the same as the weather list - the names were read out of the game's
        // own data files, and a guess that is wrong disappears instead of doing nothing.
        //
        // Подпись кнопки мод спрашивает у самой игры (TitleOf), а написанное здесь - только
        // запас на случай, если она промолчит. Поэтому в списке спокойно лежат твари, чьего
        // русского имени мы не знаем: назовёт их игра, и назовёт правильно.
        private static readonly SpawnerGroup[] SpawnerGroups =
        {
            // Мирные ставятся **живьём**, а не гнездом: у оленя, зайца и утконоса своего
            // спавнера в игре нет вовсе. У кабана и курицы есть - они ниже, в «Лугах».
            new SpawnerGroup("Мирные",
                new SpawnerKind("Олень", "Deer"),
                new SpawnerKind("Кабан", "Boar"),
                new SpawnerKind("Поросёнок", "Boar_piggy"),
                new SpawnerKind("Утконос", "Neck"),
                new SpawnerKind("Заяц", "Hare"),
                new SpawnerKind("Быкоящер", "Lox"),
                new SpawnerKind("Телёнок быкоящера", "Lox_Calf"),
                new SpawnerKind("Курица", "Hen"),
                new SpawnerKind("Цыплёнок", "Chicken"),
                new SpawnerKind("Асксвин", "Asksvin"),
                new SpawnerKind("Птенец асксвина", "Asksvin_hatchling"),
                new SpawnerKind("Ворона", "Crow")),

            new SpawnerGroup("Луга",
                new SpawnerKind("Гнездо кабанов", "Spawner_Boar"),
                new SpawnerKind("Гнездо кур", "Spawner_Hen"),
                new SpawnerKind("Гнездо цыплят", "Spawner_Chicken"),
                new SpawnerKind("Скелет лугов", "Spawner_Skeleton_Meadows")),

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
                new SpawnerKind("Скелет болот", "Spawner_Skeleton_Swamp"),
                new SpawnerKind("Слизь", "Spawner_Blob"),
                new SpawnerKind("Слизь-элита", "Spawner_BlobElite"),
                new SpawnerKind("Пиявка", "Spawner_Leech_cave"),
                new SpawnerKind("Дух", "Spawner_Wraith")),

            new SpawnerGroup("Горы",
                new SpawnerKind("Ульв", "Spawner_Ulv"),
                new SpawnerKind("Фенринг", "Spawner_Fenring"),
                new SpawnerKind("Каменный голем", "Spawner_StoneGolem"),
                new SpawnerKind("Детёныш дракона", "Spawner_Hatchling"),
                new SpawnerKind("Летучая мышь", "Spawner_Bat"),
                new SpawnerKind("Скелет гор", "Spawner_Skeleton_Mountains")),

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
                new SpawnerKind("Двергр (любой)", "Spawner_DvergerRandom"),
                new SpawnerKind("Звёздный клещ", "Spawner_Tick_stared")),

            new SpawnerGroup("Пепельные земли",
                new SpawnerKind("Обугленный", "Spawner_Charred"),
                new SpawnerKind("Обугленный лучник", "Spawner_Charred_Archer"),
                new SpawnerKind("Обугленный маг", "Spawner_Charred_Mage"),
                new SpawnerKind("Обугленный с клинком", "Spawner_Charred_Dyrnwyn"),
                new SpawnerKind("Обугленный камень", "Spawner_CharredStone"),
                new SpawnerKind("Обугленный камень (элита)", "Spawner_CharredStone_Elite"),
                new SpawnerKind("Крест обугленных", "Spawner_CharredCross"),
                new SpawnerKind("Баллиста обугленных", "Spawner_Charred_balista"),
                new SpawnerKind("Двергр пепла", "Spawner_DvergerAshlands"),
                new SpawnerKind("Морген", "Spawner_Morgen"),
                new SpawnerKind("Стервятник", "Spawner_Volture"),
                new SpawnerKind("Бес", "Spawner_imp"),
                new SpawnerKind("Spawner_Twitcher", "Spawner_Twitcher")),

            // Дальний север 1.0: где русского имени не знаем - оставлено имя префаба, и
            // игра подставит своё. Выдумывать за неё имя твари незачем.
            new SpawnerGroup("Дальний север",
                new SpawnerKind("Ётун-воин", "Spawner_JotunWarrior"),
                new SpawnerKind("Ётун с двумя клинками", "Spawner_JotunDualWield"),
                new SpawnerKind("Ётун-ведьма", "Spawner_JotunWitch"),
                new SpawnerKind("Фулинг севера", "Spawner_GoblinDeepNorth"),
                new SpawnerKind("Двергр севера", "Spawner_DvergerDeepNorth"),
                new SpawnerKind("Морозный тролль", "Spawner_TrollFrost"),
                new SpawnerKind("Spawner_Frysling", "Spawner_Frysling"),
                new SpawnerKind("Spawner_Writhan", "Spawner_Writhan"),
                new SpawnerKind("Spawner_ShadowPerson", "Spawner_ShadowPerson"),
                new SpawnerKind("Спящий медведь", "Spawner_Bjorn_sleeping")),

            new SpawnerGroup("Прочее",
                new SpawnerKind("Культист", "Spawner_Cultist"),
                new SpawnerKind("Культист Хильдир", "Spawner_Cultist_Hildir"),
                new SpawnerKind("Громила Хильдир", "Spawner_GoblinBrute_Hildir"),
                new SpawnerKind("Скелет Хильдир", "Spawner_Skeleton_hildir"),
                new SpawnerKind("Павшая валькирия", "Spawner_FallenValkyrie"),
                new SpawnerKind("Ядовитый скелет", "Spawner_Skeleton_poison"),
                new SpawnerKind("Spawner_Kvastur", "Spawner_Kvastur"),
                new SpawnerKind("Призрак пустоты", "Spawner_Ghost_Void"),
                new SpawnerKind("Рыба (пещерная)", "Spawner_Fish4"))
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

        // Подпись, которую игра дала этой твари. Спрошено один раз на вид: ответ не
        // меняется, а страница перерисовывается на каждое нажатие.
        private static readonly Dictionary<string, string> SpawnerTitles = new Dictionary<string, string>();

        /// <summary>
        /// Как эту тварь зовёт сама игра.
        ///
        /// The label written in the list is only a fallback. Asking the game means the
        /// button says what the player reads everywhere else - in the creature's hover
        /// text, in the trophy, in his own language - and it means a creature whose
        /// Russian name we do not know can still go in the list honestly: the game will
        /// name it. For a nest the name comes from what it sends out, not from the nest.
        /// </summary>
        private static string TitleOf(SpawnerKind kind)
        {
            string said;
            if (SpawnerTitles.TryGetValue(kind.Prefab, out said)) return said;

            said = kind.Label;

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kind.Prefab) : null;
            if (prefab != null)
            {
                var nest = prefab.GetComponent<CreatureSpawner>();
                var beast = nest != null && nest.m_creaturePrefab != null ? nest.m_creaturePrefab : prefab;

                var character = beast.GetComponent<Character>();
                var name = character != null ? character.m_name : null;

                if (!string.IsNullOrEmpty(name) && Localization.instance != null)
                {
                    var localised = Localization.instance.Localize(name);
                    // Ключ, для которого перевода нет, возвращается как есть - со знаком
                    // доллара. Такую «подпись» показывать хуже, чем нашу.
                    if (!string.IsNullOrEmpty(localised) && !localised.StartsWith("$")) said = localised;
                }
            }

            SpawnerTitles[kind.Prefab] = said;
            return said;
        }

        private static readonly Dictionary<string, bool> Nests = new Dictionary<string, bool>();

        /// <summary>Ставим мы живого зверя или гнездо, которое их подсылает.</summary>
        private static bool IsNest(SpawnerKind kind)
        {
            bool nest;
            if (Nests.TryGetValue(kind.Prefab, out nest)) return nest;

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kind.Prefab) : null;
            // Пока сцена не собрана, ответить нечем - и запоминать «нет» нельзя: оно
            // осталось бы навсегда, а страница спрашивает снова через секунду.
            if (prefab == null) return false;

            nest = prefab.GetComponent<CreatureSpawner>() != null;
            Nests[kind.Prefab] = nest;
            return nest;
        }

        /// <summary>
        /// Страниц у этого списка две, и различает их одно: «Спавнеры» показывают только
        /// гнёзда, «Живность» - всё, что можно поставить, живого зверя в том числе.
        /// </summary>
        private static bool _nestsOnly;

        /// <summary>Годится ли эта тварь для той страницы, что открыта сейчас.</summary>
        private static bool Fits(SpawnerKind kind)
        {
            return KnowsPrefab(kind.Prefab) && (!_nestsOnly || IsNest(kind));
        }

        private static readonly List<int> ShownGroups = new List<int>();

        /// <summary>
        /// Биомы, в которых для этой страницы хоть что-то есть.
        ///
        /// Пустая строка в списке хуже отсутствующей: на «Спавнерах» у «Мирных» нет ни
        /// одного гнезда, а в чужой сборке игры может не оказаться и целого биома -
        /// нажатие на такой биом открыло бы страницу без единой кнопки, и сказать об
        /// этом было бы некому.
        /// </summary>
        private static void GroupsShown()
        {
            ShownGroups.Clear();

            for (var i = 0; i < SpawnerGroups.Length; i++)
                foreach (var kind in SpawnerGroups[i].Kinds)
                    if (Fits(kind))
                    {
                        ShownGroups.Add(i);
                        break;
                    }
        }

        /// <summary>«Читы» → «Спавнеры» или «Живность»: одна страница, разный отбор.</summary>
        internal static void OpenSpawners(bool nestsOnly)
        {
            _nestsOnly = nestsOnly;
            _categoryOffset = 0;
            _itemOffset = 0;
            MenuState = StateSpawners;
            RefreshMenu();
        }

        /// <summary>Сколько всего биомов и сколько тварей в открытом — для листания.</summary>
        internal static int SpawnerGroupCount
        {
            get
            {
                GroupsShown();
                return ShownGroups.Count;
            }
        }

        internal static int SpawnerKindCount
        {
            get { return ShownKinds.Count; }
        }

        private static void KindsIn(int group, List<SpawnerKind> into)
        {
            into.Clear();
            if (group < 0 || group >= SpawnerGroups.Length) return;

            foreach (var kind in SpawnerGroups[group].Kinds)
                if (Fits(kind)) into.Add(kind);
        }

        /// <summary>
        /// Relabels both pools. Widgets are made once and only their captions change, the
        /// way every other list in the panel works.
        /// </summary>
        internal static void RebuildSpawnerViews()
        {
            // Оба списка листаются: биомов стало десять, а тварей в пепельных землях -
            // тринадцать, и «показаны первые восемь» здесь значило бы, что половины списка
            // нет вовсе и сказать об этом некому. Так уже было с шаблонами.
            GroupsShown();
            SetLabel(SpawnerHint, _nestsOnly ? NestHint : LivestockHint);

            var fromGroup = MenuPaging.Clamp(_categoryOffset, ShownGroups.Count, MaxSpawnerButtons);
            _shownSpawnerGroups = Mathf.Min(ShownGroups.Count - fromGroup, MaxSpawnerButtons);

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var label = SpawnerGroupButtons[i] != null
                    ? SpawnerGroupButtons[i].GetComponentInChildren<Text>(true)
                    : null;

                var at = fromGroup + i;
                if (label != null)
                    label.text = at < ShownGroups.Count ? SpawnerGroups[ShownGroups[at]].Label : "";
            }

            KindsIn(_spawnerGroup, ShownKinds);

            var fromKind = MenuPaging.Clamp(_itemOffset, ShownKinds.Count, MaxSpawnerButtons);
            _shownSpawnerKinds = Mathf.Min(ShownKinds.Count - fromKind, MaxSpawnerButtons);

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var label = SpawnerKindButtons[i] != null
                    ? SpawnerKindButtons[i].GetComponentInChildren<Text>(true)
                    : null;

                var at = fromKind + i;
                if (label != null) label.text = at < ShownKinds.Count ? TitleOf(ShownKinds[at]) : "";
            }
        }

        internal static void OpenSpawnerGroup(int slot)
        {
            GroupsShown();

            var at = MenuPaging.Clamp(_categoryOffset, ShownGroups.Count, MaxSpawnerButtons) + slot;
            if (at >= ShownGroups.Count) return;

            _spawnerGroup = ShownGroups[at];
            _itemOffset = 0;
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
            var at = MenuPaging.Clamp(_itemOffset, ShownKinds.Count, MaxSpawnerButtons) + slot;
            if (slot < 0 || at >= ShownKinds.Count) return;
            // The builder walks the clipboard across a yield, re-reading its count each
            // time round, so emptying it here would stop a build already in progress
            // partway and leave the placement state pointing at nothing. RunCopy and
            // RunCopyToFile already refuse for the same reason.
            if (BuildInProgress) return;

            var kind = ShownKinds[at];
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kind.Prefab) : null;
            if (prefab == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Такого у этой игры нет");
                return;
            }

            Clipboard.Clear();
            Clipboard.Add(new CopiedPiece
            {
                Prefab = kind.Prefab,
                LocalPos = Vector3.zero,
                LocalRot = Quaternion.identity
            });

            var title = TitleOf(kind);
            StartPlacement(IsNest(kind) ? $"гнездо «{title}»" : $"зверь «{title}»");
            InventoryGui.instance?.Hide();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"{title}: ЛКМ — поставить, P — закрепить");
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
