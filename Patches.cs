using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Enchantments;
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
            // Gate before the set: with verbose logging off, growing the dedup set is
            // pure waste, and some call sites sit in per-frame or per-probe paths.
            if (!ModEntry.Instance.Config.VerboseLogging) return;
            if (_testLogOnce.Add(message))
                ModEntry.DiagVerbose("[Test] " + message);
        }

        /// <summary>Per-screen memo for <see cref="Forge_HighlightItems_Postfix"/>.
        /// Vanilla caches HighlightItems verdicts in ForgeMenu._highlightDictionary and
        /// only regenerates them on slot changes, but a Harmony postfix runs on every
        /// call — InventoryMenu.draw asks 2-3 times per inventory slot per frame.
        /// Recomputing our adjustments at that rate (CanForge/CanCombine probes,
        /// enchantment-pool lookups, duplicate-cap walks) burns CPU and allocates, so
        /// mirror vanilla: remember the final verdict per item and drop everything when
        /// the ingredient pair or vanilla's held item changes (reference identity —
        /// crafts and clicks always swap the instances).</summary>
        private sealed class HighlightMemo
        {
            public readonly Dictionary<Item, bool> Verdicts = new();
            public Item? Left;
            public Item? Right;
            public Item? Held;
        }

        private static readonly PerScreen<HighlightMemo> HighlightMemoPerScreen = new(() => new HighlightMemo());

        /// <summary>Drop the highlight memos on every screen.  Call when something outside
        /// the (left, right, held) key can change the verdicts — config edits, menu
        /// construction, returning to title.</summary>
        internal static void InvalidateHighlightCache()
        {
            foreach (var pair in HighlightMemoPerScreen.GetActiveValues())
            {
                pair.Value.Verdicts.Clear();
                pair.Value.Left = null;
                pair.Value.Right = null;
                pair.Value.Held = null;
            }
        }

        /// <summary>Drop both craft-rules memos in one call: the inventory-highlight verdicts
        /// here and the forge panel's ring-dim verdicts in ForgeMenuPatches.  Config edits and
        /// menu construction change both, so this single entry point keeps callers from
        /// refreshing one while leaving the other stale.</summary>
        internal static void InvalidateCraftCaches()
        {
            InvalidateHighlightCache();
            ForgeMenuPatches.InvalidateDimCache();
        }

        /// <summary>Session cleanup on return to title: the memo holds Item references
        /// from the closed save, and the log-dedup set is save-scoped noise.</summary>
        internal static void ClearSessionCaches()
        {
            InvalidateHighlightCache();
            _testLogOnce.Clear();
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
            
            // 8) Tool.Forge overrides, all dispatched by gem item ID from a single prefix
            //     (Tool_Forge_Prefix): the Diamond 3-per-craft cap, the Prismatic Shard
            //     re-roll when MultipleEnchantments is off, and the Dragon Tooth re-roll /
            //     stacking (including on Galaxy/Infinity weapons vanilla refuses).  A postfix
            //     strips the leftover DiamondEnchantment marker so the tooltip doesn't show
            //     "+<huge number> Random Forges".  These used to be four separate patch
            //     registrations; since each prefix only ever acted on its own gem, one
            //     dispatching prefix is equivalent and makes the routing explicit instead of
            //     relying on Harmony prefix ordering.
            try
            {
                var forgeMethod = AccessTools.Method(typeof(Tool), nameof(Tool.Forge));
                if (forgeMethod != null)
                {
                    harmony.Patch(
                        original: forgeMethod,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_Prefix)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Tool_Forge_Postfix))
                    );
                    Log.Log("Patched Tool.Forge (prefix dispatch + postfix) successfully.", LogLevel.Trace);
                }
                else
                {
                    Log.Log("Could not find Tool.Forge method to patch.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log.Log("Tool.Forge patching failed: " + ex.Message, LogLevel.Warn);
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
            __result = ApplyCraftOverrides(left_item, right_item, __result);
        }

        /// <summary>This mod's IsValidCraft overrides as a pure function: takes the vanilla
        /// (or earlier-patch) verdict and returns the adjusted one.  Shared between the real
        /// Harmony postfix above and <see cref="ForgeMenuPatches.IsValidCraft"/>, our
        /// side-effect-free evaluator used for per-frame highlight/drop probing.</summary>
        internal static bool ApplyCraftOverrides(Item? left_item, Item? right_item, bool result)
        {
            // Override 1: Infinite ring combining.  Allow ring+ring even when vanilla
            // refuses (which it does once one of the rings is already combined or both
            // are already at the cap).  This must run regardless of the incoming
            // result, since vanilla's answer for these pairs is "false".
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
                    return false;
                }
                TestLogOnce("IsValidCraft: infinite combining ALLOWED: " + leftRing.Name + " + " + rightRing.Name);
                return true;
            }

            // Override 2: Block no-op tool crafts (e.g. Diamond on a tool that already
            // has all 6 gem enchantments, Prismatic Shard on a fully-enchanted tool).
            // Only relevant when vanilla originally said "yes" — if it already said "no",
            // there's nothing for us to suppress.
            if (result
                && left_item is Tool leftToolDemote
                && right_item != null
                && !ForgeMenuPatches.CanRightItemEnchantTool(leftToolDemote, right_item))
            {
                TestLogOnce("IsValidCraft: demoted to false (no-op craft) for " + leftToolDemote.Name + " + " + right_item.Name);
                return false;
            }

            // Override 3: Allow tool crafts vanilla refused but our mod can handle.
            // With MultipleEnchantments=false, a Prismatic Shard on a fully-enchanted
            // tool is valid in our world because vanilla's AddEnchantment clears the
            // existing tool enchantment before re-applying.  Vanilla refuses because
            // GetEnchantmentFromItem returns null, but our CanRightItemEnchantTool
            // knows the craft will produce a result after the clear+reapply.
            if (!result
                && left_item is Tool leftToolPromote
                && right_item != null
                && ForgeMenuPatches.CanRightItemEnchantTool(leftToolPromote, right_item))
            {
                TestLogOnce("IsValidCraft: promoted to true (re-roll) for " + leftToolPromote.Name + " + " + right_item.Name);
                return true;
            }

            return result;
        }

        /// <summary>Functional postfix on ForgeMenu.HighlightItems: when the left
        /// ingredient is a Ring/Tool (or the slots are empty), adjusts which inventory
        /// items light up to match this mod's craft rules.  The verdicts are memoized —
        /// see <see cref="HighlightMemo"/> — because this postfix runs on every
        /// HighlightItems call, several times per inventory slot per frame, while the
        /// inputs only change when a forge slot or the held item does.</summary>
        public static void Forge_HighlightItems_Postfix(ForgeMenu __instance, Item i, ref bool __result)
        {
            if (i == null) return;

            var memo = HighlightMemoPerScreen.Value;
            Item? left  = __instance.leftIngredientSpot.item;
            Item? right = __instance.rightIngredientSpot.item;
            Item? held  = ForgeMenuPatches.GetHeldItem(__instance);
            if (!ReferenceEquals(left, memo.Left)
                || !ReferenceEquals(right, memo.Right)
                || !ReferenceEquals(held, memo.Held))
            {
                memo.Verdicts.Clear();
                memo.Left = left;
                memo.Right = right;
                memo.Held = held;
            }

            if (memo.Verdicts.TryGetValue(i, out bool cached))
            {
                __result = cached;
                return;
            }

            __result = ComputeHighlight(__instance, i, left, right, __result);
            memo.Verdicts[i] = __result;
        }

        /// <summary>The actual highlight adjustments, as a pure function of the queried
        /// item, the ingredient slots, and vanilla's verdict.</summary>
        private static bool ComputeHighlight(ForgeMenu menu, Item i, Item? leftItem, Item? rightItem, bool result)
        {
            // Scythes can't be forged or enchanted — always dim them.
            if (i is MeleeWeapon scytheCheck && !IsScytheForgingAllowed(scytheCheck))
            {
                TestLogOnce("HighlightItems: scythe blocked (" + scytheCheck.Name + ")");
                return false;
            }

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
                    return false;
                }

                bool valid = ForgeMenuPatches.IsValidCraft(menu, leftItem, i);

                if (valid
                    && ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                    && ModEntry.Instance.Config.InfiniteCombining
                    && WouldCreateDuplicateRing((Ring)leftItem, (Ring)i))
                {
                    TestLogOnce("HighlightItems: ring " + i.Name + " dimmed by duplicate cap");
                    valid = false;
                }

                TestLogOnce("HighlightItems: ring in left slot, " + i.Name + " → " + (valid ? "highlighted" : "dimmed"));
                return valid;
            }

            // Case 2: any Tool in the left slot (weapons, slingshots, and regular tools —
            // vanilla has no upgrade-level requirement; base tools are enchantable).
            if (leftItem is Tool)
            {
                bool valid = ForgeMenuPatches.IsValidCraft(menu, leftItem, i);

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
                return valid;
            }

            // Case 3: Left slot empty, but RIGHT slot has an item.  Vanilla doesn't
            // dim the inventory in this state — but we want to.  Highlight items that
            // would form a valid craft when placed in the LEFT slot against the
            // existing right ingredient.
            if (leftItem == null && rightItem != null)
            {
                bool valid = ForgeMenuPatches.IsValidCraft(menu, i, rightItem);

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
                return valid;
            }

            // Case 4: Both forge slots empty.  Vanilla refuses to highlight a Tool whose
            // GetAvailableEnchantmentsForItem list is empty (i.e. all secondary
            // enchantments are applied) — that means the user can't even PICK UP
            // a fully-enchanted tool from inventory to forge more gems onto it.
            // Override: a tool/weapon/ring is always pickable when the forge is empty;
            // the right-slot drop logic handles refusing no-op crafts later.
            if (leftItem == null && rightItem == null && !result)
            {
                // Any Tool or Ring (scythes were already dimmed by the check at the top;
                // vanilla has no upgrade-level requirement for tools).
                if (i is Ring || i is Tool)
                {
                    TestLogOnce("HighlightItems: both empty, re-enabled " + i.Name);
                    return true;
                }
            }

            return result;
        }
        
        // ============================================================
        //  Weapon / tool forge patches (GetMaxForges, AddEnchantment,
        //  Diamond/Prismatic/Dragon Tooth forge intercepts)
        // ============================================================

        /// <summary>Single Tool.Forge prefix that dispatches to the right override by gem
        /// item ID.  Replaces three separate prefixes on Tool.Forge; each only ever acted on
        /// its own gem, so one switch is behavior-equivalent and makes the routing explicit
        /// rather than dependent on Harmony prefix ordering.  The target helpers keep their
        /// own id guards as defense-in-depth.  Galaxy Souls, gems, and every other item fall
        /// through to vanilla (return true).</summary>
        public static bool Tool_Forge_Prefix(
            Tool __instance, Item item, bool count_towards_stats, ref bool __result)
        {
            return item?.QualifiedItemId switch
            {
                "(O)72"  => Tool_Forge_Diamond_Prefix(__instance, item, count_towards_stats, ref __result),
                "(O)74"  => Tool_Forge_PrismaticReroll_Prefix(__instance, item),
                "(O)852" => Tool_Forge_DragonToothReroll_Prefix(__instance, item, count_towards_stats, ref __result),
                _        => true,
            };
        }

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
            Tool __instance, Item item, bool count_towards_stats, ref bool __result)
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

            // count_towards_stats is vanilla's forReal: the forge's result-preview box
            // crafts CLONES with forReal=false on every _ValidateCraft (so this prefix
            // runs once per slot change, on a copy).  Previews must never consume the
            // Forge Menu Choice selection, and their traces would drown the log.
            bool isRealCraft = count_towards_stats;

            bool stacking = ModEntry.Instance.Config.DragonToothStacking;
            bool alwaysMax = ModEntry.Instance.Config.AlwaysMaxDragonToothStat;

            // Plain vanilla-eligible re-rolls (below level 15, non-Galaxy, no option that
            // changes the semantics) stay with vanilla's path — including Forge Menu
            // Choice's own selection transpiler inside it.
            bool vanillaEligible = !weapon.Name.Contains("Galaxy") && weapon.getItemLevel() < 15;
            // Don't hand a fully-stacked weapon (all five core innates) to vanilla's own forge
            // path either: its reroll would hang on the same exclude-list problem.  We take
            // those over and roll them safely below.
            if (vanillaEligible && !stacking && !alwaysMax && !ContainsAllCoreInnates(weapon.enchantments))
            {
                ModEntry.DiagVerbose("[Test] Dragon Tooth: non-Galaxy below level 15, passing to vanilla");
                return true;
            }

            // We own the craft: Galaxy/Infinity weapons (vanilla's CanForge refuses them),
            // and all weapons once stacking/always-max change the rules.
            if (isRealCraft)
            {
                ModEntry.DiagVerbose(
                    "[Test] Dragon Tooth craft on " + weapon.Name
                    + ": stacking=" + stacking + ", alwaysMax=" + alwaysMax
                    + ", vanillaEligible=" + vanillaEligible);
            }
            List<BaseEnchantment> oldInnate = new();
            if (!stacking)
            {
                // Vanilla re-roll semantics: strip the existing innate stat enchantments.
                // Named secondaries (Galaxy Souls) are always preserved.
                for (int i = weapon.enchantments.Count - 1; i >= 0; i--)
                {
                    var ench = weapon.enchantments[i];
                    if (ench.IsSecondaryEnchantment()
                        && ench is not GalaxySoulEnchantment)
                    {
                        oldInnate.Add(ench);
                        ench.UnapplyTo(weapon);
                        weapon.enchantments.RemoveAt(i);
                    }
                }
            }

            // Forge Menu Choice integration: when its innate carousel is open for this
            // weapon (opened by FMC itself for vanilla-eligible weapons, or by our
            // ForgeMenuChoiceCompat prefix for the crafts FMC's gate excludes), honor the
            // player's selection.  Otherwise roll randomly, like vanilla.  Previews never
            // touch the selection — the clone is crafted with a silent random roll.
            var selected = isRealCraft ? ForgeMenuChoiceCompat.TakeCurrentSelection(weapon) : null;
            if (selected != null)
            {
                ModEntry.DiagVerbose("[Test] Dragon Tooth: applying Forge Menu Choice selection " + selected.GetType().Name);
                ApplyInnateEnchantment(weapon, selected, alwaysMax);
            }
            else if (!stacking)
            {
                // Random re-roll via vanilla's roller (it re-rolls away from the
                // just-stripped types).  But vanilla's loop only terminates when it rolls a
                // core type (Attack/Crit/WeaponSpeed/SlimeSlayer/CritPower) not in the exclude
                // list, and its switch always picks one of those five.  A fully-stacked weapon
                // we just stripped puts all five in oldInnate, so the loop could never satisfy
                // that and would spin forever (the freeze that wrote a multi-GB log).  Drop the
                // exclude list in that case so vanilla does a single unconstrained roll.
                if (ContainsAllCoreInnates(oldInnate))
                    MeleeWeapon.attemptAddRandomInnateEnchantment(weapon, Game1.random, force: true);
                else
                    MeleeWeapon.attemptAddRandomInnateEnchantment(weapon, Game1.random, force: true, oldInnate);
                if (isRealCraft)
                    ModEntry.DiagVerbose("[Test] Dragon Tooth: random re-roll applied" + (alwaysMax ? " (maxing levels)" : ""));
            }
            else
            {
                // Stacking + random: roll among innate types still below their cap so the
                // tooth is never wasted on an at-max type.
                var rolled = RollStackableInnate(weapon);
                if (rolled != null)
                {
                    if (isRealCraft)
                        ModEntry.DiagVerbose("[Test] Dragon Tooth: stacking random roll " + rolled.GetType().Name);
                    ApplyInnateEnchantment(weapon, rolled, alwaysMax, quiet: !isRealCraft);
                }
            }

            if (alwaysMax)
            {
                // "Always max" promises max innate STATS, not just a maxed pick: raise
                // every innate stat enchantment on the weapon to its per-type cap.
                // Without this, innates that were already on the weapon (spawn rolls, or
                // crafts made before the option was enabled) stay sub-max forever —
                // stacking mode never strips them.  Galaxy Souls are named secondaries,
                // not stats; leveling those would hand out free Infinity progress.
                foreach (var ench in weapon.enchantments)
                {
                    if (ench.IsSecondaryEnchantment()
                        && ench is not GalaxySoulEnchantment
                        && ench.GetMaximumLevel() >= 0
                        && ench.GetLevel() < ench.GetMaximumLevel())
                    {
                        ench.SetLevel(weapon, ench.GetMaximumLevel());
                        if (isRealCraft)
                            ModEntry.DiagVerbose(
                                "[Test] Dragon Tooth: always-max raised existing "
                                + ench.GetType().Name + " → level " + ench.GetLevel() + " (max)");
                    }
                }
            }

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

        // ---------- Dragon Tooth innate-enchantment helpers ----------

        /// <summary>The innate enchantment types vanilla's Dragon Tooth roller can produce
        /// for this weapon (mirrors attemptAddRandomInnateEnchantment's option set; Defense
        /// only rolls at weapon level &lt;= 10).</summary>
        internal static IEnumerable<Type> InnateCandidateTypes(MeleeWeapon weapon)
        {
            if (weapon.getItemLevel() <= 10)
                yield return typeof(DefenseEnchantment);
            yield return typeof(LightweightEnchantment);
            yield return typeof(SlimeGathererEnchantment);
            yield return typeof(AttackEnchantment);
            yield return typeof(CritEnchantment);
            yield return typeof(WeaponSpeedEnchantment);
            yield return typeof(SlimeSlayerEnchantment);
            yield return typeof(CritPowerEnchantment);
        }

        /// <summary>The five innate types vanilla's reroll switch (<c>switch (r.Next(5))</c> in
        /// <see cref="MeleeWeapon.attemptAddRandomInnateEnchantment"/>) always picks exactly one
        /// of.  Loop termination depends only on these: vanilla rerolls until it adds a type not
        /// in the exclude list, and the switch always adds one of the five, so an exclude list
        /// that covers all five can never be satisfied and the loop spins forever.  This is fixed
        /// vanilla internals, NOT "every innate a weapon can hold": a modded enchantment (e.g.
        /// from a scythe-enchant mod) never enters vanilla's roller, so it can neither cause nor
        /// prevent this loop and is correctly ignored here.  The three probabilistic types
        /// (Defense/Lightweight/SlimeGatherer) don't matter either, since they aren't guaranteed
        /// each iteration.  Keep in sync with vanilla's switch.</summary>
        private static readonly Type[] CoreInnateTypes =
        {
            typeof(AttackEnchantment),
            typeof(CritEnchantment),
            typeof(WeaponSpeedEnchantment),
            typeof(SlimeSlayerEnchantment),
            typeof(CritPowerEnchantment),
        };

        /// <summary>True if <paramref name="enchantments"/> includes every one of the five core
        /// innate types, the set that makes vanilla's reroll loop hang.  See
        /// <see cref="CoreInnateTypes"/>.</summary>
        private static bool ContainsAllCoreInnates(IEnumerable<BaseEnchantment> enchantments)
        {
            foreach (var core in CoreInnateTypes)
            {
                bool present = false;
                foreach (var e in enchantments)
                {
                    if (e.GetType() == core) { present = true; break; }
                }
                if (!present) return false;
            }
            return true;
        }

        /// <summary>True if the weapon already carries this innate type at its per-type cap
        /// (BaseEnchantment.GetMaximumLevel — every vanilla innate overrides it).</summary>
        internal static bool InnateTypeAtMax(MeleeWeapon weapon, Type enchantmentType)
        {
            foreach (var ench in weapon.enchantments)
            {
                if (ench.GetType() == enchantmentType)
                    return ench.GetMaximumLevel() >= 0 && ench.GetLevel() >= ench.GetMaximumLevel();
            }
            return false;
        }

        /// <summary>True if at least one innate candidate type still has room to grow —
        /// used to dim Dragon Tooth crafts that would be no-ops in stacking mode.</summary>
        internal static bool HasInnateBelowMax(MeleeWeapon weapon)
        {
            foreach (var type in InnateCandidateTypes(weapon))
            {
                if (!InnateTypeAtMax(weapon, type))
                    return true;
            }
            return false;
        }

        /// <summary>Apply an innate enchantment with stacking-aware semantics.  If the same
        /// type is already on the weapon (stacking), level it up (+1) — or jump it to its
        /// cap with alwaysMax — via SetLevel, which clamps and handles Unapply/Reapply.
        /// Otherwise add it as a new enchantment (at its cap when alwaysMax).</summary>
        private static void ApplyInnateEnchantment(MeleeWeapon weapon, BaseEnchantment enchantment, bool alwaysMax, bool quiet = false)
        {
            foreach (var existing in weapon.enchantments)
            {
                if (existing.GetType() == enchantment.GetType())
                {
                    int target = alwaysMax && existing.GetMaximumLevel() >= 0
                        ? existing.GetMaximumLevel()
                        : existing.GetLevel() + 1;
                    existing.SetLevel(weapon, target);
                    if (!quiet)
                        ModEntry.DiagVerbose(
                            "[Test] Dragon Tooth: merged " + enchantment.GetType().Name
                            + " on " + weapon.Name + " → level " + existing.GetLevel()
                            + (alwaysMax ? " (max)" : " (+1)"));
                    return;
                }
            }

            if (alwaysMax && enchantment.GetMaximumLevel() >= 0)
                enchantment.Level = enchantment.GetMaximumLevel();
            weapon.AddEnchantment(enchantment);
            if (!quiet)
                ModEntry.DiagVerbose(
                    "[Test] Dragon Tooth: added " + enchantment.GetType().Name
                    + " on " + weapon.Name + " at level " + enchantment.GetLevel()
                    + (alwaysMax ? " (max)" : ""));
        }

        /// <summary>Roll a random innate enchantment for stacking mode: vanilla's five main
        /// roll types with vanilla's level formulas, filtered to types still below their cap
        /// so the Dragon Tooth is never wasted on an at-max type.  Null when everything is
        /// capped (the craft should already have been dimmed by then).</summary>
        private static BaseEnchantment? RollStackableInnate(MeleeWeapon weapon)
        {
            int weaponLevel = weapon.getItemLevel();
            var options = new List<BaseEnchantment>();
            if (!InnateTypeAtMax(weapon, typeof(AttackEnchantment)))
                options.Add(new AttackEnchantment
                {
                    Level = Math.Max(1, Math.Min(5, Game1.random.Next(weaponLevel + 1) / 2 + 1)),
                });
            if (!InnateTypeAtMax(weapon, typeof(CritEnchantment)))
                options.Add(new CritEnchantment
                {
                    Level = Math.Max(1, Math.Min(3, Game1.random.Next(weaponLevel) / 3)),
                });
            if (!InnateTypeAtMax(weapon, typeof(WeaponSpeedEnchantment)))
                options.Add(new WeaponSpeedEnchantment
                {
                    Level = Math.Max(1, Math.Min(Math.Max(1, 4 - weapon.speed.Value), Game1.random.Next(weaponLevel))),
                });
            if (!InnateTypeAtMax(weapon, typeof(SlimeSlayerEnchantment)))
                options.Add(new SlimeSlayerEnchantment());
            if (!InnateTypeAtMax(weapon, typeof(CritPowerEnchantment)))
                options.Add(new CritPowerEnchantment
                {
                    Level = Math.Max(1, Math.Min(3, Game1.random.Next(weaponLevel) / 3)),
                });

            return options.Count > 0 ? options[Game1.random.Next(options.Count)] : null;
        }

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