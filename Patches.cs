using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.Tools;

namespace MultiRingInfiniteForging
{
    public static class Patches
    {
        private static IMonitor Log = null!;
        private static readonly HashSet<string> _testLogOnce = new();

        private static void TestLogOnce(string message)
        {
            if (_testLogOnce.Add(message))
                ModEntry.DiagVerbose("[Test] " + message);
        }

        public static void ApplyAll(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            // 1) Make Farmer.isWearingRing aware of our extra slots.
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.isWearingRing)),
                postfix: new HarmonyMethod(typeof(Patches), nameof(IsWearingRing_Postfix))
            );

            // 1b) Make Farmer.GetEquippedItems include our extra rings.  This is the
            //     master pipeline: BuffManager.GetValues() iterates GetEquippedItems()
            //     and calls AddEquipmentEffects on each, which is how rings contribute
            //     magnetic radius, defense, attack/crit/knockback/speed multipliers,
            //     luck, immunity, etc. Without this, extra-slot rings appear equipped
            //     but provide no passive stat bonuses.  CombinedRing.AddEquipmentEffects
            //     recurses internally, so nested rings inside CombinedRings also work.
            try
            {
                var getEquipped = AccessTools.Method(typeof(Farmer), nameof(Farmer.GetEquippedItems));
                if (getEquipped != null)
                {
                    harmony.Patch(
                        original: getEquipped,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(GetEquippedItems_Postfix))
                    );
                    Log.Log("Patched Farmer.GetEquippedItems successfully.", LogLevel.Trace);
                }
                else
                {
                    Log.Log("Could not find Farmer.GetEquippedItems to patch.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Farmer.GetEquippedItems patching failed: " + ex.Message, LogLevel.Warn);
            }
            // 1c) Forward onMonsterSlay to extra-slot rings.  Vanilla calls
            //     leftRing.Value?.onMonsterSlay(...) / rightRing.Value?.onMonsterSlay(...)
            //     inside GameLocation when a monster dies; without this patch Vampire,
            //     Warrior, Savage, Soul Sapper, Napalm, Hot Java effects don't fire
            //     from extra slots.  We hook the simpler choke point: Monster.deathAnimation
            //     is called once per kill on the dying monster.  Using a postfix on
            //     GameLocation.monsterDrop would be equivalent.
            try
            {
                var monsterDrop = AccessTools.Method(typeof(GameLocation), "monsterDrop");
                if (monsterDrop != null)
                {
                    harmony.Patch(
                        original: monsterDrop,
                        postfix: new HarmonyMethod(typeof(Patches), nameof(MonsterDrop_Postfix))
                    );
                    Log.Log("Patched GameLocation.monsterDrop successfully.", LogLevel.Trace);
                }
                else
                {
                    Log.Log("Could not find GameLocation.monsterDrop to patch.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log.Log("GameLocation.monsterDrop patching failed: " + ex.Message, LogLevel.Warn);
            }

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
                    Log.Log("Patched ForgeMenu.IsValidCraft successfully.", LogLevel.Trace);
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
                    Log.Log("Patched ForgeMenu.HighlightItems successfully.", LogLevel.Trace);
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
                    Log.Log("Patched MeleeWeapon.GetMaxForges successfully.", LogLevel.Trace);
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
                    Log.Log("Patched Tool.AddEnchantment successfully.", LogLevel.Trace);
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
                    Log.Log("Patched Tool.Forge successfully.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge patching failed: " + ex.Message, LogLevel.Warn);
            }
            
            // 9) Cap Diamond forges at 3 per craft.  Vanilla computes the per-craft cap
            //    as GetMaxForges() - GetTotalForgeLevels(); with an unlimited cap
            //    (WeaponForgingCap == -1) that becomes int.MaxValue, so a single
            //    Diamond fills all 6 gem slots.  Intercept the Diamond branch and
            //    emulate the vanilla 3-per-craft cap.
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
                    Log.Log("Patched Tool.Forge (Diamond cap) successfully.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge (Diamond cap) patching failed: " + ex.Message, LogLevel.Warn);
            }
            // 10) Prismatic Shard re-roll on fully-enchanted tools when
            //     MultipleEnchantments is disabled.  Vanilla's Tool.Forge returns
            //     null when the enchantment pool is empty, which would delete the
            //     tool via the null-craft-result path.  Pre-empt that by manually
            //     clearing existing tool enchantments first.
            try
            {
                var forgeMethod = AccessTools.Method(typeof(Tool),
                    nameof(Tool.Forge));
                if (forgeMethod != null)
                {
                    harmony.Patch(
                        original: forgeMethod,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_PrismaticReroll_Prefix))
                    );
                    Log.Log("Patched Tool.Forge (Prismatic re-roll) successfully.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge (Prismatic re-roll) patching failed: " + ex.Message, LogLevel.Warn);
            }
            // 11) Dragon Tooth re-roll on Infinity / fully-evolved Galaxy weapons.
            //     Vanilla refuses this combination (Tool.CanForge restricts to
            //     name-doesn't-contain-Galaxy AND level < 15) — but the mechanic is
            //     useful as an endgame mod feature.  We allow the drop via
            //     CanRightItemEnchantTool; this prefix executes the re-roll mechanic
            //     in cases vanilla would otherwise skip.
            try
            {
                var forgeMethod = AccessTools.Method(typeof(Tool),
                    nameof(Tool.Forge));
                if (forgeMethod != null)
                {
                    harmony.Patch(
                        original: forgeMethod,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_DragonToothReroll_Prefix))
                    );
                    Log.Log("Patched Tool.Forge (Dragon Tooth Infinity re-roll) successfully.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge (Dragon Tooth Infinity re-roll) patching failed: " + ex.Message, LogLevel.Warn);
            }
        }

        // ============================================================
        //  Ring effect patches
        // ============================================================

        public static void IsWearingRing_Postfix(Farmer __instance, string itemId, ref bool __result)
        {
            if (__result) return;
            // GetRingsFor resolves any farmer: the live Slots for the local player, or the
            // synced modData for remote players — so host-simulated checks (e.g. slime
            // aggro vs a farmhand wearing Slime Charmer in a panel slot) work too.
            foreach (var ring in RingSlotManager.GetRingsFor(__instance))
            {
                if (ring == null) continue;
                if (ring.GetsEffectOfRing(itemId))
                {
                    TestLogOnce("IsWearingRing: extra slot match for " + itemId + " via " + ring.DisplayName);
                    __result = true;
                    return;
                }
            }
        }
        /// <summary>Forward monster-killed events to extra-slot rings.  Vanilla calls
        /// leftRing/rightRing.onMonsterSlay from the same code block as monsterDrop;
        /// hooking monsterDrop's postfix lets us mirror that call for our extra rings.</summary>
        public static void MonsterDrop_Postfix(GameLocation __instance, Monster monster, int x, int y, Farmer who)
        {
            if (who == null || monster == null) return;
            // monsterDrop runs on the client simulating the location (usually the host);
            // OnMonsterSlay resolves the killer's rings via GetRingsFor, so farmhand kills
            // fire their extra-slot Vampire/Warrior/etc. effects just like vanilla rings.
            RingSlotManager.OnMonsterSlay(monster, __instance, who);
        }
        
        /// <summary>Append extra-slot rings to Farmer.GetEquippedItems so vanilla's
        /// BuffManager / enchantment loops pick up their AddEquipmentEffects
        /// contribution.  This is the canonical wiring for every passive ring stat
        /// (Topaz +Defense, Aquamarine +Crit, Ruby +Attack, Magnet/Iridium Band radius,
        /// Crabshell +5 Def, Lucky Ring, Immunity Band, etc.).</summary>
        public static IEnumerable<Item> GetEquippedItems_Postfix(IEnumerable<Item> __result, Farmer __instance)
        {
            foreach (var item in __result)
                yield return item;

            // Local player: the live Slots list.  Remote farmers: reconstructed from their
            // synced modData, so equipment-derived checks evaluated on this client (buffs,
            // enchantment conditions, tooltips) see their extra rings too.
            foreach (var ring in RingSlotManager.GetRingsFor(__instance))
            {
                if (ring != null)
                    yield return ring;
            }
        }

        // ============================================================
        //  Forge menu patches (IsValidCraft, HighlightItems)
        // ============================================================

        public static void Forge_IsValidCraft_Postfix(Item left_item, Item right_item, ref bool __result)
        {
            // Override 1: Infinite ring combining.  Allow ring+ring even when vanilla
            // refuses (which it does once one of the rings is already combined or both
            // are already at the cap).  This must run regardless of the incoming
            // __result, since vanilla's answer for these pairs is "false".
            if (ModEntry.Instance.Config.InfiniteCombining
                && left_item is Ring leftRing && right_item is Ring rightRing)
            {
                // Optional cap: refuse the combine if either side already contains a
                // ring ID that the other side would re-introduce.  Prevents the user
                // from stacking the same ring twice (or more) into a CombinedRing.
                if (ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                    && WouldCreateDuplicateRing(leftRing, rightRing))
                {
                    TestLogOnce("IsValidCraft: infinite combining BLOCKED (duplicate cap)");
                    __result = false;
                    return;
                }
                TestLogOnce("IsValidCraft: infinite combining ALLOWED: " + leftRing.Name + " + " + rightRing.Name);
                __result = true;
                return;
            }

            // Override 2: Block no-op tool crafts (e.g. Diamond on a tool that already
            // has all 6 gem enchantments, Prismatic Shard on a fully-enchanted tool).
            // Only relevant when vanilla originally said "yes" — if it already said "no",
            // there's nothing for us to suppress.
            if (__result
                && left_item is Tool leftToolDemote
                && right_item != null
                && !ForgeMenuPatches.CanRightItemEnchantTool(leftToolDemote, right_item))
            {
                TestLogOnce("IsValidCraft: demoted to false (no-op craft) for " + leftToolDemote.Name + " + " + right_item.Name);
                __result = false;
                return;
            }
            
            // Override 3: Allow tool crafts vanilla refused but our mod can handle.
            // With MultipleEnchantments=false, a Prismatic Shard on a fully-enchanted
            // tool is valid in our world because vanilla's AddEnchantment clears the
            // existing tool enchantment before re-applying.  Vanilla refuses because
            // GetEnchantmentFromItem returns null, but our CanRightItemEnchantTool
            // knows the craft will produce a result after the clear+reapply.
            if (!__result
                && left_item is Tool leftToolPromote
                && right_item != null
                && ForgeMenuPatches.CanRightItemEnchantTool(leftToolPromote, right_item))
            {
                TestLogOnce("IsValidCraft: promoted to true (re-roll) for " + leftToolPromote.Name + " + " + right_item.Name);
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

            // Scythes can't be forged or enchanted — always dim them.
            if (i is MeleeWeapon scytheCheck && !IsScytheForgingAllowed(scytheCheck))
            {
                TestLogOnce("HighlightItems: scythe blocked (" + scytheCheck.Name + ")");
                __result = false;
                return;
            }

            Item? leftItem  = __instance.leftIngredientSpot.item;
            Item? rightItem = __instance.rightIngredientSpot.item;

            // Case 1: Ring in left slot.  Highlight only rings that would form
            // a valid ring+ring combine with the left ring.  Without InfiniteCombining,
            // vanilla restricts to "uncombined + uncombined of different IDs"; with
            // InfiniteCombining our Forge_IsValidCraft_Postfix override allows any
            // ring+ring.  Either way, defer to IsValidCraft for the decision.
            if (leftItem is Ring)
            {
                if (i is not Ring)
                {
                    TestLogOnce("HighlightItems: ring in left slot, non-ring " + i.Name + " dimmed");
                    __result = false;
                    return;
                }

                bool valid = ForgeMenuPatches.IsValidCraft(__instance, leftItem, i);

                if (valid
                    && ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                    && ModEntry.Instance.Config.InfiniteCombining
                    && WouldCreateDuplicateRing((Ring)leftItem, (Ring)i))
                {
                    TestLogOnce("HighlightItems: ring " + i.Name + " dimmed by duplicate cap");
                    valid = false;
                }

                TestLogOnce("HighlightItems: ring in left slot, " + i.Name + " → " + (valid ? "highlighted" : "dimmed"));
                __result = valid;
                return;
            }

            // Case 2: Weapon/Slingshot/Tool in left slot.
            if (leftItem is MeleeWeapon || leftItem is Slingshot
                                        || (leftItem is Tool t && t.UpgradeLevel > 0))
            {
                bool valid = ForgeMenuPatches.IsValidCraft(__instance, leftItem, i);

                // Demote: dim items that would produce a no-op craft (e.g. Diamond
                // on a tool that already has all 6 gem enchantments).
                if (valid && leftItem is Tool leftToolDemote
                          && !ForgeMenuPatches.CanRightItemEnchantTool(leftToolDemote, i))
                {
                    TestLogOnce("HighlightItems: tool in left slot, " + i.Name + " dimmed (no-op demote)");
                    valid = false;
                }

                // Promote: highlight items that vanilla rejects but our mod accepts.
                // Specifically: with MultipleEnchantments=false, vanilla refuses a
                // Prismatic Shard on a fully-enchanted tool because
                // GetEnchantmentFromItem returns null (the available pool is empty
                // after filtering applied types).  But vanilla's AddEnchantment will
                // CLEAR the existing tool enchantment before applying the new one,
                // so the craft does produce a result — CanRightItemEnchantTool
                // simulates that.  If our gate says yes, override vanilla's "no".
                if (!valid && leftItem is Tool leftToolPromote
                           && ForgeMenuPatches.CanRightItemEnchantTool(leftToolPromote, i))
                {
                    TestLogOnce("HighlightItems: tool in left slot, " + i.Name + " highlighted (re-roll promote)");
                    valid = true;
                }

                TestLogOnce("HighlightItems: tool in left slot, " + i.Name + " → " + (valid ? "highlighted" : "dimmed"));
                __result = valid;
                return;
            }

            // Case 3: Left slot empty, but RIGHT slot has an item.  Vanilla doesn't
            // dim the inventory in this state — but we want to.  Highlight items that
            // would form a valid craft when placed in the LEFT slot against the
            // existing right ingredient.
            if (leftItem == null && rightItem != null)
            {
                bool valid = ForgeMenuPatches.IsValidCraft(__instance, i, rightItem);

                // Demote: no-op craft.
                if (valid && i is Tool prospectiveLeftToolDemote
                          && !ForgeMenuPatches.CanRightItemEnchantTool(prospectiveLeftToolDemote, rightItem))
                {
                    TestLogOnce("HighlightItems: right slot occupied, " + i.Name + " dimmed (no-op demote)");
                    valid = false;
                }

                // Promote: re-roll case (see Case 2 comment).
                if (!valid && i is Tool prospectiveLeftToolPromote
                           && ForgeMenuPatches.CanRightItemEnchantTool(prospectiveLeftToolPromote, rightItem))
                {
                    TestLogOnce("HighlightItems: right slot occupied, " + i.Name + " highlighted (re-roll promote)");
                    valid = true;
                }

                TestLogOnce("HighlightItems: right slot occupied, " + i.Name + " → " + (valid ? "highlighted" : "dimmed"));
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
                    TestLogOnce("HighlightItems: both empty, re-enabled " + i.Name);
                    __result = true;
                }
            }
        }
        
        // ============================================================
        //  Weapon / tool forge patches (GetMaxForges, AddEnchantment,
        //  Diamond/Prismatic/Dragon Tooth forge intercepts)
        // ============================================================

        public static void MeleeWeapon_GetMaxForges_Postfix(ref int __result)
        {
            if (ModEntry.Instance.Config.WeaponForgingCap == -1)
            {
                TestLogOnce("GetMaxForges: unlimited (int.MaxValue)");
                __result = int.MaxValue;
            }
            else if (ModEntry.Instance.Config.WeaponForgingCap >= 0)
            {
                TestLogOnce("GetMaxForges: capped at " + ModEntry.Instance.Config.WeaponForgingCap);
                __result = ModEntry.Instance.Config.WeaponForgingCap;
            }
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
            {
                ModEntry.DiagVerbose("[Test] AddEnchantment: MultipleEnchantments=false, running vanilla");
                return true; // run vanilla as normal
            }

            if (enchantment == null)
            {
                ModEntry.DiagVerbose("[Test] AddEnchantment: null enchantment, returning false");
                __result = false;
                return false;
            }

            // Defer melee forge/secondary enchantments (gems, Galaxy Souls, innate stats)
            // to vanilla: its melee branch already stacks them, with same-type level-up
            // (Ruby I -> Ruby II rather than two "Ruby I" instances), GetMaximumLevel caps,
            // and the single leveled GalaxySoulEnchantment instance the Infinity transform
            // removes cleanly.  MultipleEnchantments only needs to bypass the RemoveAll in
            // vanilla's *tool/weapon enchantment* branch, replicated below.
            if (__instance is MeleeWeapon && (enchantment.IsForge() || enchantment.IsSecondaryEnchantment()))
            {
                ModEntry.DiagVerbose("[Test] AddEnchantment: forge/secondary on melee weapon, running vanilla");
                return true;
            }

            ModEntry.DiagVerbose("[Test] AddEnchantment: adding " + enchantment.GetType().Name + " to " + __instance.Name + " (MultipleEnchantments mode)");
            __instance.enchantments.Add(enchantment);
            enchantment.ApplyTo(__instance, Game1.player);

            __result = true;   // tell Tool.Forge that the enchantment was added
            return false;      // skip the vanilla method
        }
        /// <summary>After a successful Diamond forge, strip the leftover
        /// DiamondEnchantment from the tool.  Vanilla leaves it on so the tooltip
        /// displays "+N Random Forges" (where N = GetMaxForges() - GetTotalForgeLevels()).
        /// With an unlimited cap (WeaponForgingCap == -1) that displays as
        /// "+2147483641 Random Forges" because GetMaxForges() is int.MaxValue.
        /// With a finite cap the tooltip ends up at "+0 Random Forges" once the
        /// tool hits the cap.  Either way the line is misleading — strip the marker
        /// so the tooltip is clean.</summary>
        public static void Tool_Forge_Postfix(Tool __instance, Item item, bool __result)
        {
            if (!__result) return;
            if (item?.QualifiedItemId != "(O)72") return;

            // Remove the DiamondEnchantment marker (works regardless of config).
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
        /// per-craft cap; an unlimited cap (WeaponForgingCap == -1) makes GetMaxForges()
        /// return int.MaxValue, so a single Diamond fills all 6 gem enchantment slots in
        /// one shot.  We emulate vanilla's "up to 3 random gems per Diamond" behaviour
        /// here and then short-circuit vanilla.
        ///
        /// <paramref name="count_towards_stats"/> is part of Tool.Forge's signature and
        /// required by Harmony for argument binding.  Vanilla's Diamond branch doesn't
        /// use it (the GemsForged / PrismaticShardsForged stat counters are bumped
        /// from the gem and prismatic branches, not the Diamond branch), so we don't
        /// need to use it either.</summary>
        public static bool Tool_Forge_Diamond_Prefix(
            Tool __instance, Item item, bool count_towards_stats, ref bool __result)
        {
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

            if (validForges.Count == 0 && ModEntry.Instance.Config.RemoveDiamondForgesCap)
            {
                ModEntry.DiagVerbose("[Test] Diamond forge: RemoveDiamondForgesCap=true, re-enabling all 6 types");
                validForges = new List<int> { 0, 1, 2, 3, 4, 5 };
            }
            
            // Vanilla applies up to 3 gem enchantments per Diamond (the original
            // "MAX_FORGES" constant before our patch).
            const int diamondCapPerCraft = 3;

            int forgesLeft = System.Math.Min(diamondCapPerCraft, validForges.Count);

            // Honor the configured total-forge cap too: vanilla bounds its Diamond branch
            // by GetMaxForges() - GetTotalForgeLevels(), so a finite WeaponForgingCap must
            // not be exceedable via Diamonds.  With the default -1 (unlimited) GetMaxForges()
            // is int.MaxValue and this never binds.
            int remainingCapacity = __instance.GetMaxForges() - __instance.GetTotalForgeLevels();
            if (forgesLeft > remainingCapacity)
                forgesLeft = System.Math.Max(0, remainingCapacity);
            ModEntry.DiagVerbose("[Test] Diamond forge: " + forgesLeft + " forges available on " + __instance.Name);

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
                ModEntry.DiagVerbose("[Test] Diamond forge: applying " + ench.GetType().Name + " (choice=" + choice + ")");
                __instance.AddEnchantment(ench);
                validForges.RemoveAt(choice);
            }

            // Vanilla also adds a DiamondEnchantment marker which makes the tooltip show
            // "+N Random Forges".  We strip that in Tool_Forge_Postfix anyway, so we can
            // skip it entirely here.

            __result = forgesLeft > 0;   // success only if we actually applied something
            return false;                // skip the vanilla method
        }
        /// <summary>When MultipleEnchantments is disabled and a Prismatic Shard is
        /// forged onto a tool with every applicable enchantment already applied,
        /// vanilla's Tool.Forge would return null (empty pool) and the craft would
        /// delete the tool.  Pre-clear the tool's existing tool-style enchantments so
        /// vanilla's GetEnchantmentFromItem finds a candidate.</summary>
        public static bool Tool_Forge_PrismaticReroll_Prefix(
            Tool __instance, Item item)
        {
            if (ModEntry.Instance.Config.MultipleEnchantments)
            {
                ModEntry.DiagVerbose("[Test] Prismatic reroll: MultipleEnchantments=true, passing through");
                return true; // stacking mode — pass through
            }
            if (item?.QualifiedItemId != "(O)74")
                return true; // not a Prismatic Shard

            // Check whether vanilla's pool would be empty.
            var available = StardewValley.Enchantments.BaseEnchantment
                .GetAvailableEnchantmentsForItem(__instance);
            if (available != null && available.Count > 0)
                return true; // pool non-empty — vanilla handles it normally

            // Pool is empty — manually clear existing tool-style enchantments so
            // vanilla's next call to GetAvailableEnchantmentsForItem finds candidates.
            for (int i = __instance.enchantments.Count - 1; i >= 0; i--)
            {
                var ench = __instance.enchantments[i];
                if (!ench.IsForge() && !ench.IsSecondaryEnchantment())
                {
                    ench.UnapplyTo(__instance);
                    __instance.enchantments.RemoveAt(i);
                }
            }

            // Let vanilla proceed; it'll now find a fresh enchantment from the pool.
            return true;
        }
        /// <summary>Vanilla refuses Dragon Tooth on Galaxy/Infinity weapons via
        /// Tool.CanForge's name check.  Our gate accepts the drop; this prefix
        /// performs the re-roll using the same logic vanilla's non-Galaxy path uses
        /// (strip existing innate stat enchantments via IsSecondaryEnchantment(),
        /// then attemptAddRandomInnateEnchantment to add a fresh one).  Named
        /// secondary enchantments like Vampiric return IsSecondaryEnchantment()==false
        /// and are preserved automatically.</summary>
        public static bool Tool_Forge_DragonToothReroll_Prefix(
            Tool __instance, Item item, ref bool __result)
        {
            if (item?.QualifiedItemId != "(O)852")
                return true; // not a Dragon Tooth — let vanilla handle
            if (__instance is not MeleeWeapon weapon)
                return true;
            if (weapon.isScythe())
            {
                ModEntry.DiagVerbose("[Test] Dragon Tooth: scythe blocked");
                return true;
            }

            // For non-Galaxy weapons below level 15, vanilla's existing path works
            // correctly — leave it alone.
            if (!weapon.Name.Contains("Galaxy") && weapon.getItemLevel() < 15)
            {
                ModEntry.DiagVerbose("[Test] Dragon Tooth: non-Galaxy below level 15, passing to vanilla");
                return true;
            }

            // Mod extension: Galaxy/Infinity weapons.  Replicate vanilla's behaviour
            // here since vanilla's CanForge refuses.
            List<StardewValley.Enchantments.BaseEnchantment> oldInnate = new();
            for (int i = weapon.enchantments.Count - 1; i >= 0; i--)
            {
                var ench = weapon.enchantments[i];
                if (ench.IsSecondaryEnchantment()
                    && ench is not StardewValley.Enchantments.GalaxySoulEnchantment)
                {
                    oldInnate.Add(ench);
                    ench.UnapplyTo(weapon);
                    weapon.enchantments.RemoveAt(i);
                }
            }

            MeleeWeapon.attemptAddRandomInnateEnchantment(
                weapon, Game1.random, force: true, oldInnate);

            __result = true;
            return false; // skip vanilla
        }
        
        // ============================================================
        //  Helpers
        // ============================================================

        /// <summary>True if the given weapon can be placed in a forge slot.  Non-scythes
        /// are always allowed; scythes require an optional scythe-forging mod.</summary>
        public static bool IsScytheForgingAllowed(MeleeWeapon weapon) =>
            !weapon.isScythe() || ModEntry.HasEnchantableScythes || ModEntry.HasScytheToolEnchantments;

        /// <summary>True if combining <paramref name="a"/> and <paramref name="b"/>
        /// would result in a CombinedRing containing two or more copies of the same
        /// ring ID.  Walks each side recursively into nested CombinedRings.</summary>
        internal static bool WouldCreateDuplicateRing(Ring a, Ring b)
        {
            var seen = new HashSet<string>();
            CollectRingIds(a, seen);
            foreach (var id in EnumerateRingIds(b))
            {
                if (!seen.Add(id))
                {
                    TestLogOnce("WouldCreateDuplicateRing: duplicate found " + id);
                    return true;  // already present on the left side OR duplicated within the right side itself
                }
            }
            return false;
        }
        
        /// <summary>Add every constituent ring ID of <paramref name="ring"/> into
        /// <paramref name="ids"/>.  Recurses through CombinedRings.</summary>
        private static void CollectRingIds(Ring ring, HashSet<string> ids)
        {
            if (ring is CombinedRing combined)
            {
                foreach (var inner in combined.combinedRings)
                    CollectRingIds(inner, ids);
            }
            else
            {
                ids.Add(ring.QualifiedItemId);
            }
        }

        /// <summary>Yield every constituent ring ID of <paramref name="ring"/>.
        /// Used to iterate the right-hand side while checking against the left's
        /// set without merging the two halves' sets prematurely.</summary>
        private static IEnumerable<string> EnumerateRingIds(Ring ring)
        {
            if (ring is CombinedRing combined)
            {
                foreach (var inner in combined.combinedRings)
                foreach (var id in EnumerateRingIds(inner))
                    yield return id;
            }
            else
            {
                yield return ring.QualifiedItemId;
            }
        }
    }
}