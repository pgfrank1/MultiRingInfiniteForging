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
            
            // 8) Clean up the leftover DiamondEnchantment placeholder after a Diamond
            //    forge so the tooltip doesn't show "+<huge number> Random Forges".
            try
            {
                var forgeMethod = AccessTools.Method(typeof(Tool),
                    nameof(Tool.Forge));
                if (forgeMethod != null)
                {
                    harmony.Patch(
                        original: forgeMethod,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_Postfix))
                    );
                    Log.Log("Patched Tool.Forge successfully.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge patching failed: " + ex.Message, LogLevel.Warn);
            }
            
            // 9) Cap Diamond forges at 3 per craft.  Vanilla computes the per-craft cap
            //    as GetMaxForges() - GetTotalForgeLevels(); with InfiniteWeaponForging
            //    that becomes int.MaxValue, so a single Diamond fills all 6 gem slots.
            //    Intercept the Diamond branch and emulate the vanilla 3-per-craft cap.
            try
            {
                var forgeMethod = AccessTools.Method(typeof(Tool),
                    nameof(Tool.Forge));
                if (forgeMethod != null)
                {
                    harmony.Patch(
                        original: forgeMethod,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_Diamond_Prefix))
                    );
                    Log.Log("Patched Tool.Forge (Diamond cap) successfully.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge (Diamond cap) patching failed: " + ex.Message, LogLevel.Warn);
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
            // Override 1: Infinite ring combining.  Allow ring+ring even when vanilla
            // refuses (which it does once one of the rings is already combined or both
            // are already at the cap).  This must run regardless of the incoming
            // __result, since vanilla's answer for these pairs is "false".
            if (ModEntry.Instance.Config.InfiniteCombining
                && left_item is Ring && right_item is Ring)
            {
                __result = true;
                return;
            }

            // Override 2: Block no-op tool crafts (e.g. Diamond on a tool that already
            // has all 6 gem enchantments, Prismatic Shard on a fully-enchanted tool).
            // Only relevant when vanilla originally said "yes" — if it already said "no",
            // there's nothing for us to suppress.
            if (__result
                && left_item is Tool leftTool
                && right_item != null
                && !ForgeMenuPatches.CanRightItemEnchantTool(leftTool, right_item))
            {
                __result = false;
            }
        }

                /// <summary>Diagnostic + functional postfix on ForgeMenu.HighlightItems.
                /// Logs each call so we can verify the patch is active, then (when the left
                /// ingredient is a Ring) tightens the result so only Rings and Prismatic
                /// Shards are highlighted.</summary>
            public static void Forge_HighlightItems_Postfix(ForgeMenu __instance, Item i, ref bool __result)
            {
                if (i == null) return;

                // Scythes can't be forged or enchanted — always dim them.
                if (i is MeleeWeapon scytheCheck && scytheCheck.isScythe())
                {
                    __result = false;
                    return;
                }

                Item? leftItem  = __instance.leftIngredientSpot.item;
                Item? rightItem = __instance.rightIngredientSpot.item;

                // Case 1: Ring in left slot.  Only highlight rings — prismatic shards
                // and dragon teeth don't combine with rings.
                if (leftItem is Ring)
                {
                    __result = i is Ring;
                    return;
                }

                // Case 2: Weapon/Slingshot/Tool in left slot.
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

                    if (valid && leftItem is Tool leftTool
                              && !ForgeMenuPatches.CanRightItemEnchantTool(leftTool, i))
                    {
                        valid = false;
                    }

                    __result = valid;
                    return;
                }

                // Case 3: Left slot empty, but RIGHT slot has an item.  Vanilla doesn't
                // dim the inventory in this state — but we want to.  Highlight items that
                // would form a valid craft when placed in the LEFT slot against the
                // existing right ingredient.
                if (leftItem == null && rightItem != null)
                {
                    bool valid = false;
                    try
                    {
                        var isValidCraft = AccessTools.Method(typeof(ForgeMenu), "IsValidCraft");
                        if (isValidCraft != null)
                        {
                            var ret = isValidCraft.Invoke(__instance, new object?[] { i, rightItem });
                            if (ret is bool b) valid = b;
                        }
                    }
                    catch { /* leave valid = false */ }

                    // Also dim if the (i, rightItem) pair would be a no-op craft.
                    if (valid && i is Tool prospectiveLeftTool
                              && !ForgeMenuPatches.CanRightItemEnchantTool(prospectiveLeftTool, rightItem))
                    {
                        valid = false;
                    }

                    __result = valid;
                    return;
                }

                // Case 4: Both forge slots empty.  Vanilla refuses to highlight a Tool whose
                // GetAvailableEnchantmentsForItem list is empty (i.e. all secondary
                // enchantments are applied) — that means the user can't even PICK UP
                // a fully-enchanted tool from inventory to forge more gems onto it.
                // Override: a tool/weapon/ring is always pickable when the forge is empty;
                // the right-slot drop logic handles refusing no-op crafts later.
                if (leftItem == null && rightItem == null && !__result)
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
        /// <summary>After a successful Diamond forge, strip the leftover
        /// DiamondEnchantment from the tool.  Vanilla leaves it on so the tooltip
        /// displays "+N Random Forges" (where N = GetMaxForges() - GetTotalForgeLevels()).
        /// With InfiniteWeaponForging that displays as "+2147483641 Random Forges"
        /// because GetMaxForges() is int.MaxValue.  Remove it so the tooltip is clean
        /// and players can later forge another Diamond without the misleading line.</summary>
        public static void Tool_Forge_Postfix(Tool __instance, Item item, bool __result)
        {
            if (!__result) return;
            if (item?.QualifiedItemId != "(O)72") return;
            if (!ModEntry.Instance.Config.InfiniteWeaponForging) return;

            // Remove the DiamondEnchantment marker.
            for (int i = __instance.enchantments.Count - 1; i >= 0; i--)
            {
                if (__instance.enchantments[i] is StardewValley.Enchantments.DiamondEnchantment)
                {
                    __instance.enchantments[i].UnapplyTo(__instance);
                    __instance.enchantments.RemoveAt(i);
                }
            }
        }
        /// <summary>Replace Tool.Forge's Diamond branch with a vanilla-faithful 3-per-craft
        /// version.  Vanilla's code uses GetMaxForges() - GetTotalForgeLevels() as the
        /// per-craft cap; our InfiniteWeaponForging patch makes GetMaxForges() return
        /// int.MaxValue, so a single Diamond fills all 6 gem enchantment slots in one
        /// shot.  We emulate vanilla's "up to 3 random gems per Diamond" behaviour here
        /// and then short-circuit vanilla.
        ///
        /// <paramref name="count_towards_stats"/> is part of Tool.Forge's signature and
        /// required by Harmony for argument binding.  Vanilla's Diamond branch doesn't
        /// use it (the GemsForged / PrismaticShardsForged stat counters are bumped
        /// from the gem and prismatic branches, not the Diamond branch), so we don't
        /// need to use it either.</summary>
        public static bool Tool_Forge_Diamond_Prefix(
            Tool __instance, Item item, bool count_towards_stats, ref bool __result)
        {
            if (!ModEntry.Instance.Config.InfiniteWeaponForging)
                return true; // let vanilla handle it as normal

            if (item?.QualifiedItemId != "(O)72")
                return true; // not a Diamond — let vanilla handle the rest

            // Build the list of missing gem enchantment types.  These mirror the order
            // and types vanilla uses in Tool.Forge's Diamond branch.
            List<int> validForges = new List<int>();
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.EmeraldEnchantment>())    validForges.Add(0);
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.AquamarineEnchantment>()) validForges.Add(1);
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.RubyEnchantment>())       validForges.Add(2);
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.AmethystEnchantment>())   validForges.Add(3);
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.TopazEnchantment>())      validForges.Add(4);
            if (!__instance.hasEnchantmentOfType<StardewValley.Enchantments.JadeEnchantment>())       validForges.Add(5);

            // Vanilla applies up to 3 gem enchantments per Diamond (the original
            // "MAX_FORGES" constant before our patch).
            const int diamondCapPerCraft = 3;
            int forgesLeft = System.Math.Min(diamondCapPerCraft, validForges.Count);

            for (int i = 0; i < forgesLeft; i++)
            {
                if (validForges.Count == 0) break;
                int choice = Game1.random.Next(validForges.Count);
                StardewValley.Enchantments.BaseEnchantment ench = validForges[choice] switch
                {
                    0 => new StardewValley.Enchantments.EmeraldEnchantment(),
                    1 => new StardewValley.Enchantments.AquamarineEnchantment(),
                    2 => new StardewValley.Enchantments.RubyEnchantment(),
                    3 => new StardewValley.Enchantments.AmethystEnchantment(),
                    4 => new StardewValley.Enchantments.TopazEnchantment(),
                    _ => new StardewValley.Enchantments.JadeEnchantment(),
                };
                __instance.AddEnchantment(ench);
                validForges.RemoveAt(choice);
            }

            // Vanilla also adds a DiamondEnchantment marker which makes the tooltip show
            // "+N Random Forges".  We strip that in Tool_Forge_Postfix anyway, so we can
            // skip it entirely here.

            __result = forgesLeft > 0;   // success only if we actually applied something
            return false;                // skip the vanilla method
        }
    }
}