using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Enchantments;
using StardewValley.Menus;
using StardewValley.Tools;

namespace MultiRingInfiniteForging
{
    /// <summary>
    /// Integration with Forge Menu Choice ("Pick Your Enchantment",
    /// <c>focustense.ForgeMenuChoice</c>) — implemented entirely from our side via
    /// reflection; FMC itself is untouched.
    ///
    /// FMC lets the player pick which enchantment a Prismatic Shard or Dragon Tooth applies,
    /// via a selection carousel above the forge.  Stock FMC only opens that carousel for
    /// Dragon Tooth on vanilla-eligible weapons (below level 15, non-Galaxy), and its
    /// IsValidCraft prefix trashes any menu it didn't expect on the next call.  This shim:
    ///
    ///   1. Registers a prefix on ForgeMenu.IsValidCraft at Priority.VeryHigh — above FMC's
    ///      Priority.High prefix.  Harmony skips lower-priority bool prefixes once an
    ///      earlier one returns false, so when we claim a Dragon Tooth craft we handle
    ///      (Galaxy/Infinity weapons, or any weapon when DragonToothStacking is on), FMC's
    ///      prefix never runs and can't trash the carousel.
    ///   2. Opens FMC's own ForgeSelectionMenu (reflection ctor) with FMC's own innate
    ///      option list, installed through its static Menu property — all of FMC's
    ///      draw/click/hover/gamepad handling operates on that property, so the entire UI
    ///      works unchanged (and it's PerScreen-backed, so split-screen works too).
    ///   3. Exposes <see cref="TakeCurrentSelection"/> for our Tool.Forge prefix to consume
    ///      the player's pick.
    ///
    /// Best-effort: if FMC is absent or any member fails to resolve, everything degrades to
    /// random rolls with a single warning.
    /// </summary>
    internal static class ForgeMenuChoiceCompat
    {
        private const string ModId = "focustense.ForgeMenuChoice";

        private static IMonitor monitor = null!;

        private static PropertyInfo? menuProp;             // ForgeMenuPatches.Menu (private static, PerScreen-backed)
        private static PropertyInfo? currentSelectionProp; // ForgeMenuPatches.CurrentSelection
        private static MethodInfo? trashMenuMethod;        // ForgeMenuPatches.TrashMenu()
        private static MethodInfo? getInnateMethod;        // ForgeMenuPatches.GetInnateEnchantments(MeleeWeapon)
        private static ConstructorInfo? menuCtor;          // ForgeSelectionMenu(List<BaseEnchantment>, Tool, bool)
        private static PropertyInfo? menuToolProp;         // ForgeSelectionMenu.Tool
        private static PropertyInfo? menuIsInnateProp;     // ForgeSelectionMenu.IsInnate
        private static PropertyInfo? fmcConfigProp;        // ForgeMenuChoice.ModEntry.Config
        private static PropertyInfo? overrideInnateProp;   // FMC ModConfig.OverrideInnateEnchantments
        private static FieldInfo? menuOptionsField;        // ForgeSelectionMenu.options (List<BaseEnchantment>)

        /// <summary>Last carousel instance whose options were logged — log-dedup only.</summary>
        private static object? lastLoggedMenu;

        private static bool loggedPrefixError;

        /// <summary>Whether FMC is installed and all reflection members resolved.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Attempt to wire up the integration.  Safe to call always.</summary>
        public static void Init(Harmony harmony, IModHelper helper, IMonitor monitorIn)
        {
            monitor = monitorIn;
            if (!helper.ModRegistry.IsLoaded(ModId))
                return;

            void Warn(string reason) =>
                monitor.Log(
                    $"Forge Menu Choice is installed but {reason}; Dragon Tooth crafts handled by this "
                        + "mod will roll randomly instead of using its selection carousel.",
                    LogLevel.Warn);

            try
            {
                Type? patchesType = AccessTools.TypeByName("ForgeMenuChoice.HarmonyPatches.ForgeMenuPatches");
                Type? menuType = AccessTools.TypeByName("ForgeMenuChoice.ForgeSelectionMenu");
                Type? fmcModEntryType = AccessTools.TypeByName("ForgeMenuChoice.ModEntry");
                if (patchesType is null || menuType is null || fmcModEntryType is null)
                {
                    Warn("its types weren't found (the mod may have changed)");
                    return;
                }

                menuProp = AccessTools.Property(patchesType, "Menu");
                currentSelectionProp = AccessTools.Property(patchesType, "CurrentSelection");
                trashMenuMethod = AccessTools.Method(patchesType, "TrashMenu");
                getInnateMethod = AccessTools.Method(patchesType, "GetInnateEnchantments");
                menuCtor = AccessTools.Constructor(
                    menuType,
                    new[] { typeof(List<BaseEnchantment>), typeof(Tool), typeof(bool) });
                menuToolProp = AccessTools.Property(menuType, "Tool");
                menuIsInnateProp = AccessTools.Property(menuType, "IsInnate");
                menuOptionsField = AccessTools.Field(menuType, "options");
                fmcConfigProp = AccessTools.Property(fmcModEntryType, "Config");
                overrideInnateProp = fmcConfigProp is null
                    ? null
                    : AccessTools.Property(fmcConfigProp.PropertyType, "OverrideInnateEnchantments");

                if (menuProp?.SetMethod is null
                    || currentSelectionProp is null
                    || trashMenuMethod is null
                    || getInnateMethod is null
                    || menuCtor is null
                    || menuToolProp is null
                    || menuIsInnateProp is null)
                {
                    Warn("its internals changed");
                    return;
                }

                harmony.Patch(
                    original: AccessTools.Method(typeof(ForgeMenu), "IsValidCraft"),
                    prefix: new HarmonyMethod(
                        typeof(ForgeMenuChoiceCompat),
                        nameof(GalaxyInnate_IsValidCraft_Prefix))
                    {
                        priority = Priority.VeryHigh,
                    },
                    postfix: new HarmonyMethod(
                        typeof(ForgeMenuChoiceCompat),
                        nameof(NormalizeCarousel_IsValidCraft_Postfix)));

                if (menuOptionsField is null)
                    monitor.Log(
                        "Forge Menu Choice's carousel option list wasn't found; its rolled option "
                            + "levels can't be normalized to Always Max (applied crafts still are).",
                        LogLevel.Warn);

                IsActive = true;
                monitor.Log(
                    "Forge Menu Choice detected: its enchantment carousel will also open for the Dragon "
                        + "Tooth crafts this mod handles (Galaxy/Infinity weapons; all weapons with "
                        + "Dragon Tooth Stacking).",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                Warn("wiring it failed: " + ex.Message);
            }
        }

        /// <summary>Prefix on ForgeMenu.IsValidCraft at Priority.VeryHigh (above FMC's own
        /// prefix).  Claims the Dragon Tooth crafts our Tool.Forge prefix owns: keeps FMC's
        /// carousel alive (FMC's prefix, which would trash an unexpected menu, is skipped
        /// once we return false) and opens it for weapons FMC's own gate excludes.</summary>
        public static bool GalaxyInnate_IsValidCraft_Prefix(
            Item left_item, Item right_item, ref bool __result, ForgeMenu __instance)
        {
            try
            {
                if (!IsActive || !ModEntry.Instance.Config.ForgeMenuChoiceIntegration)
                    return true;

                // Spurious-probe guard.  Vanilla's highlight rebuild probes IsValidCraft
                // with (left-slot tool, <every inventory item>) when only the left slot is
                // filled (and the mirrored shape for right-only).  FMC's prismatic branch
                // lacks the slot-identity check its innate branch has, so a probe carrying
                // an inventory Prismatic Shard conjures its carousel out of thin air, and
                // an inventory Dragon Tooth probe lands in its trash-all fallback and
                // churns a live menu.  Claim every 74/852 call that isn't the real slot
                // pair and answer it with the pure mirror; mismatch probes from all other
                // items still reach FMC's TrashMenu, so its menu lifecycle keeps working.
                string? rightId = right_item?.QualifiedItemId;
                if ((rightId == "(O)74" || rightId == "(O)852")
                    && (!ReferenceEquals(left_item, __instance.leftIngredientSpot.item)
                        || !ReferenceEquals(right_item, __instance.rightIngredientSpot.item)))
                {
                    // Claiming these probes also swallows FMC's trash-on-mismatch signal,
                    // so sync the lifecycle here instead: a carousel whose tool is no
                    // longer the left-slot item is stale — close it before it can serve a
                    // pick for the wrong weapon.  These probes fire right after any slot
                    // change, so the timing matches FMC's old cleanup.  (Removing the
                    // right-slot item instead still cleans up via FMC itself: probes
                    // carrying other inventory items keep reaching its TrashMenu.)
                    object? openMenu = menuProp!.GetValue(null);
                    if (openMenu != null
                        && !ReferenceEquals(menuToolProp!.GetValue(openMenu), __instance.leftIngredientSpot.item))
                    {
                        trashMenuMethod!.Invoke(null, null);
                        ModEntry.DiagVerbose("[Test] FMC carousel closed (left slot changed)");
                    }
                    __result = ForgeMenuPatches.IsValidCraft(__instance, left_item, right_item);
                    return false;
                }

                if (rightId != "(O)852" || left_item is not MeleeWeapon weapon)
                    return true;
                if (weapon.isScythe() || !Patches.IsScytheForgingAllowed(weapon))
                    return true;
                if (!FmcOverrideInnateEnabled())
                    return true;

                bool stacking = ModEntry.Instance.Config.DragonToothStacking;
                bool vanillaEligible = weapon.getItemLevel() < 15 && !weapon.Name.Contains("Galaxy");
                // Plain vanilla-eligible re-rolls stay with FMC's own branch (it opens the
                // carousel itself); we only manage the carousel for crafts FMC won't:
                // Galaxy/Infinity weapons, and everything once stacking changes the rules.
                if (vanillaEligible && !stacking)
                    return true;

                // Only the real slot pair reaches this point: every mismatched 74/852
                // call was already claimed by the spurious-probe guard above.

                // Keepalive: the carousel is already open for this weapon — claim the call.
                object? menu = menuProp!.GetValue(null);
                if (menu != null
                    && menuIsInnateProp!.GetValue(menu) is true
                    && ReferenceEquals(menuToolProp!.GetValue(menu), weapon))
                {
                    __result = true;
                    return false;
                }

                // Open FMC's carousel with FMC's own innate option list for this weapon.
                var options = ((IEnumerable<BaseEnchantment>)getInnateMethod!
                    .Invoke(null, new object[] { weapon })!).ToList();
                if (stacking)
                {
                    // Stacking can't gain anything from a type already at its cap.
                    options.RemoveAll(e => Patches.InnateTypeAtMax(weapon, e.GetType()));
                }
                if (options.Count == 0)
                {
                    // Every innate type is at its cap (stacking filter) — the craft is a
                    // no-op.  Refuse it here so FMC's own (unfiltered) carousel can't open
                    // for vanilla-eligible weapons; our IsValidCraft postfix agrees via the
                    // CanRightItemEnchantTool stacking demote.
                    ModEntry.DiagVerbose("[Test] FMC carousel refused: every innate type at cap for " + weapon.Name);
                    __result = false;
                    return false;
                }

                object newMenu = menuCtor!.Invoke(new object[] { options, weapon, true });
                menuProp.SetValue(null, newMenu);
                ModEntry.DiagVerbose(
                    "[Test] FMC carousel opened for " + weapon.Name + " (" + options.Count + " innate options"
                    + (stacking ? ", at-cap types filtered" : "") + ")");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                if (!loggedPrefixError)
                {
                    loggedPrefixError = true;
                    monitor.Log(
                        "Forge Menu Choice integration failed mid-game; Dragon Tooth crafts will roll "
                            + "randomly.\n" + ex,
                        LogLevel.Warn);
                }
                return true;
            }
        }

        /// <summary>Postfix on ForgeMenu.IsValidCraft — runs on every call, including ones a
        /// prefix claimed (Harmony only skips the original), so it sees every carousel right
        /// after it is built, whether we built it or FMC did.  FMC rolls a random level into
        /// every innate option at menu build (its own roll formulas, re-rolled on each
        /// rebuild) and stock FMC applies that rolled level as-is.  With Always Max on we
        /// override the level at apply time, so normalize the displayed/held options to their
        /// caps too — every surface and FMC's own apply path then agree with the result.
        /// Also traces the option list once per menu instance for visibility.</summary>
        public static void NormalizeCarousel_IsValidCraft_Postfix()
        {
            try
            {
                if (!IsActive || menuOptionsField is null
                    || !ModEntry.Instance.Config.ForgeMenuChoiceIntegration)
                    return;
                object? menu = menuProp!.GetValue(null);
                if (menu is null)
                {
                    lastLoggedMenu = null;
                    return;
                }
                if (menuIsInnateProp!.GetValue(menu) is not true)
                    return;
                if (menuOptionsField.GetValue(menu) is not List<BaseEnchantment> options)
                    return;

                bool alwaysMax = ModEntry.Instance.Config.AlwaysMaxDragonToothStat;
                bool changed = false;
                if (alwaysMax)
                {
                    foreach (var option in options)
                    {
                        int cap = option.GetMaximumLevel();
                        if (cap >= 0 && option.Level != cap)
                        {
                            // Not applied to any item yet — a raw Level set is exactly how
                            // FMC constructs these instances.
                            option.Level = cap;
                            changed = true;
                        }
                    }
                }

                if (changed || !ReferenceEquals(menu, lastLoggedMenu))
                {
                    lastLoggedMenu = menu;
                    ModEntry.DiagVerbose(
                        "[Test] FMC carousel options" + (alwaysMax ? " (levels maxed)" : "") + ": "
                        + string.Join(", ", options.Select(o =>
                            o.GetType().Name.Replace("Enchantment", "") + " " + o.GetLevel())));
                }
            }
            catch
            {
                // Best-effort: a failure here only affects the option levels shown/rolled.
            }
        }

        /// <summary>Take (and consume) FMC's currently selected innate enchantment for this
        /// weapon, if its innate carousel is open for it.  Null means the caller should fall
        /// back to a random roll.  Never returns a prismatic selection.</summary>
        public static BaseEnchantment? TakeCurrentSelection(MeleeWeapon weapon)
        {
            if (!IsActive || !ModEntry.Instance.Config.ForgeMenuChoiceIntegration)
                return null;
            try
            {
                object? menu = menuProp!.GetValue(null);
                if (menu is null
                    || menuIsInnateProp!.GetValue(menu) is not true
                    || !ReferenceEquals(menuToolProp!.GetValue(menu), weapon))
                {
                    if (menu is not null)
                        ModEntry.DiagVerbose("[Test] FMC selection skipped: open carousel doesn't match " + weapon.Name);
                    return null;
                }

                if (currentSelectionProp!.GetValue(null) is not BaseEnchantment selection)
                    return null;

                // Consume: close the carousel so the pick isn't applied twice.
                trashMenuMethod!.Invoke(null, null);
                return selection;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Close FMC's carousel if one is open.  Used when the forge slots are
        /// emptied by our own click handling: with no items in the slots, no IsValidCraft
        /// call ever fires, so FMC has no chance to notice the pair broke and close it
        /// itself.  Safe no-op when nothing is open.</summary>
        public static void CloseCarousel()
        {
            if (!IsActive)
                return;
            try
            {
                if (menuProp!.GetValue(null) is null)
                    return;
                ModEntry.DiagVerbose("[Test] FMC carousel closed (forge slots emptied)");
                trashMenuMethod!.Invoke(null, null);
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>Whether FMC's own OverrideInnateEnchantments option is enabled.
        /// Fail-open: if unreadable, assume its default of true.</summary>
        private static bool FmcOverrideInnateEnabled()
        {
            try
            {
                object? config = fmcConfigProp?.GetValue(null);
                if (config is null || overrideInnateProp is null)
                    return true;
                return overrideInnateProp.GetValue(config) is true;
            }
            catch
            {
                return true;
            }
        }
    }
}
