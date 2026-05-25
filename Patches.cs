using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

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

            // 3) Add the same collapsible extra-ring panel into the forge menu.
            ForgeMenuPatches.Apply(harmony, monitor);

            // 4) Forge menu: allow combining rings even when they're already combined rings
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
                    Log.Log("Patched ForgeMenu.IsValidCraft successfully.", LogLevel.Info);
                }
                else
                {
                    Log.Log("Could not find ForgeMenu.IsValidCraft method to patch.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Forge patching failed: " + ex.Message, LogLevel.Warn);
            }
            // 5) Forge menu: tighten HighlightItems so non-compatible items are dimmed
            //    when a Ring is in the left ingredient slot.
            try
            {
                var highlightItems = AccessTools.Method(typeof(ForgeMenu), "HighlightItems");
                if (highlightItems != null)
                {
                    harmony.Patch(
                        original: highlightItems,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Forge_HighlightItems_Postfix))
                    );
                    Log.Log("Patched ForgeMenu.HighlightItems successfully.", LogLevel.Info);
                }
                else
                {
                    Log.Log("Could not find ForgeMenu.HighlightItems method to patch.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Forge HighlightItems patching failed: " + ex.Message, LogLevel.Warn);
            }

            // 6) Infinite weapon forging: remove the 3-gem cap on melee weapons by
            //    overriding GetMaxForges to return a very high number.
            try
            {
                var maxForges = AccessTools.Method(typeof(StardewValley.Tools.MeleeWeapon),
                    nameof(StardewValley.Tools.MeleeWeapon.GetMaxForges));
                if (maxForges != null)
                {
                    harmony.Patch(
                        original: maxForges,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(MeleeWeapon_GetMaxForges_Postfix))
                    );
                    Log.Log("Patched MeleeWeapon.GetMaxForges successfully.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Log("MeleeWeapon.GetMaxForges patching failed: " + ex.Message, LogLevel.Warn);
            }

            // 7) Multiple enchantments: stop AddEnchantment from removing the existing
            //    enchantments of the same family (weapon-family or tool-family) before
            //    adding the new one.
            try
            {
                var addEnchTool = AccessTools.Method(typeof(Tool),
                    nameof(Tool.AddEnchantment));
                if (addEnchTool != null)
                {
                    harmony.Patch(
                        original: addEnchTool,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Tool_AddEnchantment_Prefix))
                    );
                    Log.Log("Patched Tool.AddEnchantment successfully.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Log("AddEnchantment patching failed: " + ex.Message, LogLevel.Warn);
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
            if (!ModEntry.Instance.Config.InfiniteCombining)
                return;

            // Infinite ring combining: allow ring+ring even when both already have multiple
            // rings combined inside them (vanilla caps total combined rings at 2).
            if (left_item is Ring && right_item is Ring)
            {
                __result = true;
            }
        }

            /// <summary>Diagnostic + functional postfix on ForgeMenu.HighlightItems.
            /// Logs each call so we can verify the patch is active, then (when the left
            /// ingredient is a Ring) tightens the result so only Rings and Prismatic
            /// Shards are highlighted.</summary>
            public static void Forge_HighlightItems_Postfix(ForgeMenu __instance, Item i, ref bool __result)
            {
                if (i == null) return;

                Item? leftItem = __instance.leftIngredientSpot.item;

                // Case 1: Ring in left slot.  Only highlight rings and prismatic shards.
                if (leftItem is Ring)
                {
                    __result = i is Ring
                               || (i is StardewValley.Object pShard && pShard.QualifiedItemId == "(O)74");
                    return;
                }

                // Case 2: Weapon/Slingshot/Tool in left slot.
                // SDV 1.6.15's HighlightItems doesn't reliably gate on IsValidCraft, so we
                // recompute the highlight ourselves: an item is highlighted only if it forms
                // a valid forge craft with the left ingredient.
                if (leftItem is MeleeWeapon || leftItem is Slingshot
                                            || (leftItem is Tool t && t.UpgradeLevel > 0))
                {
                    bool valid = false;
                    try
                    {
                        var isValidCraft = AccessTools.Method(typeof(ForgeMenu), "IsValidCraft");
                        if (isValidCraft != null)
                        {
                            var ret = isValidCraft.Invoke(__instance, new object?[] { leftItem, i });
                            if (ret is bool b) valid = b;
                        }
                    }
                    catch { /* leave valid = false */ }

                    __result = valid;
                    return;
                }

                // Case 3: Left slot empty.  Vanilla refuses to highlight a Tool whose
                // GetAvailableEnchantmentsForItem list is empty (i.e. all secondary
                // enchantments are applied) — that means the user can't even PICK UP
                // a fully-enchanted tool from inventory to forge more gems onto it.
                // Override: a tool/weapon/ring is always pickable when the forge is empty;
                // the right-slot drop logic handles refusing no-op crafts later.
                if (leftItem == null && !__result)
                {
                    if (i is Ring
                        || i is MeleeWeapon
                        || i is Slingshot
                        || (i is Tool tool && tool.UpgradeLevel > 0))
                    {
                        __result = true;
                    }
                }
            }
        
        /// <summary>Stop MeleeWeapon.AddEnchantment from removing the existing
        /// BaseWeaponEnchantment instances before adding the new one.  We replicate
        /// vanilla's call to base.AddEnchantment ourselves and skip the original.</summary>
        public static void MeleeWeapon_GetMaxForges_Postfix(ref int __result)
        {
            if (ModEntry.Instance.Config.InfiniteWeaponForging)
                __result = int.MaxValue;
        }

        /// <summary>Stop Tool.AddEnchantment from removing the existing enchantments of
        /// the same family before adding the new one.  Replicate the relevant logic
        /// from base Item.AddEnchantment without the RemoveAll call.</summary>
        public static bool Tool_AddEnchantment_Prefix(
            Tool __instance,
            StardewValley.Enchantments.BaseEnchantment enchantment,
            ref bool __result)
        {
            if (!ModEntry.Instance.Config.MultipleEnchantments)
                return true; // run vanilla as normal

            if (enchantment == null)
            {
                __result = false;
                return false;
            }

            __instance.enchantments.Add(enchantment);
            enchantment.ApplyTo(__instance, Game1.player);

            __result = true;   // tell Tool.Forge that the enchantment was added
            return false;      // skip the vanilla method
        }
    }
}