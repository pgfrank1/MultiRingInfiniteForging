using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    public static class Patches
    {
        private static IMonitor Log = null!;

        public static void ApplyAll(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            // 1) Make Farmer.isWearingRing aware of our extra slots.
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.isWearingRing)),
                postfix: new HarmonyMethod(typeof(Patches), nameof(IsWearingRing_Postfix))
            );

            // 2) Add extra ring slots into the vanilla inventory/equipment page.
            InventoryPagePatches.Apply(harmony, monitor);

            // 3) Forge menu: allow combining rings even when they're already combined rings
            //    or already at the cap, and allow unlimited reforges.
            try
            {
                var forgeIsValid = AccessTools.Method(typeof(ForgeMenu), "IsValidCraft");
                if (forgeIsValid != null)
                {
                    harmony.Patch(
                        original: forgeIsValid,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Forge_IsValidCraft_Postfix))
                    );
                }
            }
            catch (Exception ex)
            {
                Log.Log("Forge patching failed: " + ex.Message, LogLevel.Warn);
            }
        }

        // ----- patches -----

        public static void IsWearingRing_Postfix(Farmer __instance, string itemId, ref bool __result)
        {
            if (__result) return;
            foreach (var ring in RingSlotManager.Slots)
            {
                if (ring == null) continue;
                if (ring.GetsEffectOfRing(itemId))
                {
                    __result = true;
                    return;
                }
            }
        }

        public static void Forge_IsValidCraft_Postfix(Item left_item, Item right_item, ref bool __result)
        {
            if (!ModEntry.Instance.Config.InfiniteCombining && !ModEntry.Instance.Config.InfiniteReforging)
                return;

            // Allow combining when both are rings (including CombinedRings) regardless of how
            // many rings are already inside them.
            if (ModEntry.Instance.Config.InfiniteCombining
                && left_item is Ring && right_item is Ring)
            {
                __result = true;
            }

            // Allow reforging (right_item is a Prismatic Shard with a forged ring on the left).
            if (ModEntry.Instance.Config.InfiniteReforging
                && left_item is Ring
                && right_item is StardewValley.Object o
                && o.QualifiedItemId == "(O)74") // Prismatic Shard
            {
                __result = true;
            }
        }
    }
}