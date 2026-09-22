using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// Сколько игроков пускать на сервер.
    ///
    /// У игры это не настройка и не флаг: `ZNet.ServerPlayerLimit` объявлен как
    /// `public const int = 10`, то есть в место проверки он вкомпилирован числом, и
    /// подменять сам член бесполезно - менять надо проверку. Проверка одна и она
    /// **серверная**, в `ZNet.RPC_PeerInfo` (сверено по декомпиляту 1.0.15):
    ///
    ///     if (GetNrOfPlayers() >= 10) { rpc.Invoke("Error", 9); "server is full" }
    ///
    /// Оттуда следует всё остальное: правка нужна только на сервере, игрокам
    /// обновляться не надо, и одиннадцатого пускает или не пускает сервер.
    /// </summary>
    public partial class Plugin
    {
        // Число самой игры. От него же считается «ничего не менять»: при десяти
        // транспайлер всё равно подставляет наш вызов, но отвечает он то же самое,
        // и сервер ведёт себя ровно как ванильный.
        internal const int VanillaPlayerLimit = 10;

        // Сверху предел нужен не игре, а нам: каждый игрок тянет свои загруженные
        // зоны, и число, набранное в поле сгоряча, положит машину, а не сервер.
        // Снизу единица - сервер, на который пускают одного.
        private const int MinPlayerLimit = 1;

        private const int MaxPlayerLimit = 64;

        private static ConfigEntry<int> _playerLimit;

        internal static void BindPlayerLimit(ConfigFile config)
        {
            _playerLimit = config.Bind("Серверы", "PlayerLimit", VanillaPlayerLimit,
                "Сколько игроков пускать одновременно. У самой игры это не настройка, а "
                + "зашитое число 10, и меняет его мод. От 1 до 64; больше десяти — вопрос "
                + "не игры, а машины: каждый игрок держит вокруг себя свои зоны. "
                + "Действует на сервере, игрокам обновлять мод не нужно.");
        }

        /// <summary>
        /// Предел, который увидит проверка вместо зашитой десятки. Зовётся из
        /// подменённого кода игры, поэтому public: иначе Harmony не соберёт вызов.
        /// </summary>
        public static int ServerPlayerLimit()
        {
            if (_playerLimit == null) return VanillaPlayerLimit;
            return Mathf.Clamp(_playerLimit.Value, MinPlayerLimit, MaxPlayerLimit);
        }
    }

    /// <summary>
    /// Подменяет число в единственной проверке «сервер полон».
    ///
    /// Транспайлер, а не префикс: префикс пришлось бы вешать на весь `RPC_PeerInfo` -
    /// рукопожатие целиком, вместе с вайтлистом, паролем и версией, - и повторять его
    /// у себя. Здесь же меняется одна инструкция.
    ///
    /// **Десятка в этом методе не одна**: рядом стоит `rpc.Invoke("Error", 10)` -
    /// отказ по кроссплею. Поэтому ищется не число, а пара «вызов GetNrOfPlayers, и
    /// сразу за ним константа»: это и есть та самая проверка.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    public static class ServerPlayerLimitPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var nrOfPlayers = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers));
            var ourLimit = AccessTools.Method(typeof(Plugin), nameof(Plugin.ServerPlayerLimit));
            var code = new List<CodeInstruction>(instructions);
            var replaced = 0;

            if (nrOfPlayers == null || ourLimit == null)
            {
                // Молчащий патч хуже отсутствующего: сервер будет держать десятку, а
                // админ — считать, что поставил двадцать.
                Plugin.Log.LogError("[AstvardServerMod] Player limit: ZNet.GetNrOfPlayers not found, limit stays at "
                                    + Plugin.VanillaPlayerLimit + ".");
                return code;
            }

            for (var i = 0; i + 1 < code.Count; i++)
            {
                if (!code[i].Calls(nrOfPlayers)) continue;

                var next = code[i + 1];
                if (!IsConstant(next, out var value) || value != Plugin.VanillaPlayerLimit) continue;

                // Метки и блоки исключений принадлежат месту в коде, а не инструкции:
                // потерять их значит увести чужой переход в никуда.
                code[i + 1] = new CodeInstruction(OpCodes.Call, ourLimit)
                {
                    labels = next.labels,
                    blocks = next.blocks
                };
                replaced++;
            }

            if (replaced != 1)
            {
                Plugin.Log.LogError($"[AstvardServerMod] Player limit: expected one «players >= {Plugin.VanillaPlayerLimit}» "
                                    + $"check in ZNet.RPC_PeerInfo, patched {replaced}. The game has changed — the limit "
                                    + "may be the game's own.");
            }
            else
            {
                Plugin.Log.LogInfo($"[AstvardServerMod] Player limit: {Plugin.ServerPlayerLimit()} players.");
            }

            return code;
        }

        private static bool IsConstant(CodeInstruction instruction, out int value)
        {
            value = 0;
            if (instruction.opcode == OpCodes.Ldc_I4_S || instruction.opcode == OpCodes.Ldc_I4)
            {
                try
                {
                    value = Convert.ToInt32(instruction.operand);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            // Компилятор вправе положить маленькое число короткой инструкцией без
            // операнда, и десятка как раз из таких.
            if (instruction.opcode == OpCodes.Ldc_I4_0) { value = 0; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_1) { value = 1; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_2) { value = 2; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_3) { value = 3; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_4) { value = 4; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_5) { value = 5; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_6) { value = 6; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_7) { value = 7; return true; }
            if (instruction.opcode == OpCodes.Ldc_I4_8) { value = 8; return true; }

            return false;
        }
    }
}
