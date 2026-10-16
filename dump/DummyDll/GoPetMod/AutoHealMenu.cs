using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppGoPet.Ui;
using MelonLoader;

namespace GoPetMod
{
    // Adds "Tự hồi phục" to the game's main menu, right after "Tự động đánh".
    // dw.cfk() builds that menu: for each label in dw.irm it adds new br(dw.cfl(label), label, null, dw) via
    // Menu.boq, then shows it with cx.buw(menu). Picking an item calls the listener's bfq(br, da) with br.hpi = id.
    internal static class AutoHealMenu
    {
        // Ids outside the range dw.cfl hands out for its own menu entries.
        private const int MainItemId = 0x5A1000;
        private const int OffItemId = 0x5A1001;
        private const int PercentItemBase = 0x5A1100;
        private static readonly int[] Percents = { 30, 40, 50, 60, 70, 80, 90, 100 };

        private static bool _buildingMainMenu;
        private static int _itemsAdded;
        private static bool _adding;

        private static string MainLabel() =>
            AutoHeal.Enabled ? $"Tự hồi phục: {AutoHeal.Percent}%" : "Tự hồi phục: Tắt";

        [HarmonyPatch(typeof(dw), "cfk")]
        private static class MainMenuPatch
        {
            private static void Prefix()
            {
                _buildingMainMenu = true;
                _itemsAdded = 0;
            }

            private static void Postfix() => _buildingMainMenu = false;
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.boq))]
        private static class AddItemPatch
        {
            private static void Postfix(Menu __instance, br a)
            {
                if (!_buildingMainMenu || _adding)
                    return;
                if (++_itemsAdded != 1)
                    return;
                try
                {
                    _adding = true;
                    // Same listener as the game's own entries, so the click comes back to dw.bfq.
                    __instance.boq(new br(MainItemId, MainLabel(), null, a.hpl));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[AutoHealMenu] add item failed: {e.Message}");
                }
                finally
                {
                    _adding = false;
                }
            }
        }

        [HarmonyPatch(typeof(dw), nameof(dw.bfq))]
        private static class CommandPatch
        {
            private static bool Prefix(dw __instance, br a)
            {
                if (a == null)
                    return true;
                var id = a.hpi;
                try
                {
                    if (id == MainItemId)
                    {
                        ShowPercentMenu(__instance);
                        return false;
                    }
                    if (id == OffItemId)
                    {
                        AutoHeal.Disable();
                        return false;
                    }
                    if (id > PercentItemBase && id <= PercentItemBase + 100)
                    {
                        AutoHeal.SetPercent(id - PercentItemBase);
                        return false;
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[AutoHealMenu] command failed: {e.Message}");
                    return false;
                }
                return true;
            }
        }

        private static void ShowPercentMenu(dw game)
        {
            var menu = new Menu();
            menu.boq(new br(OffItemId, AutoHeal.Enabled ? "Tắt" : "• Tắt", null, game.Cast<bp>()));
            foreach (var p in Percents)
            {
                var mark = AutoHeal.Enabled && AutoHeal.Percent == p ? "• " : "";
                menu.boq(new br(PercentItemBase + p, $"{mark}Dưới {p}% máu", null, game.Cast<bp>()));
            }
            game.buw(menu);
        }
    }
}
