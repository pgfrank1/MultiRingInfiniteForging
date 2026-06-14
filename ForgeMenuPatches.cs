using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>Harmony entry points and craft-rules helpers for the forge's extra-ring panel.
    /// The panel state and interaction live in <see cref="ForgeRingPanel"/>, one instance per
    /// <see cref="ForgeMenu"/> (kept in <see cref="Panels"/>); these patches route each call to
    /// the panel for the menu in hand.  The craft-rules helpers stay here as <c>internal
    /// static</c> so the panel can reach them via <c>using static</c>, and so Patches.cs and the
    /// Forge Menu Choice shim keep calling <see cref="IsValidCraft"/> / <see cref="GetHeldItem"/>
    /// / <see cref="CanRightItemEnchantTool"/> as before.</summary>
    public static class ForgeMenuPatches
    {
        private static readonly HashSet<string> _testLogOnce = new();

        private static void TestLogOnce(string message)
        {
            // Gate before the set: with verbose logging off, growing the dedup set is pure
            // waste, and some call sites sit in per-frame or per-probe paths.
            if (!ModEntry.Instance.Config.VerboseLogging) return;
            if (_testLogOnce.Add(message))
                ModEntry.DiagVerbose("[Test] " + message);
        }

        private static readonly System.Reflection.FieldInfo? HeldItemField =
            AccessTools.Field(typeof(MenuWithInventory), "_heldItem");

        private static readonly System.Reflection.FieldInfo? HighlightDictionaryField =
            AccessTools.Field(typeof(ForgeMenu), "_highlightDictionary");

        private static readonly System.Reflection.MethodInfo? ValidateCraftMethod =
            AccessTools.Method(typeof(ForgeMenu), "_ValidateCraft");

        // ============================================================
        //  Per-menu panel registry
        // ============================================================

        private static readonly ConditionalWeakTable<ForgeMenu, ForgeRingPanel> Panels = new();

        private static ForgeRingPanel PanelFor(ForgeMenu menu) =>
            Panels.GetValue(menu, m => new ForgeRingPanel(m));

        /// <summary>The panel for the forge currently on screen, or null when no forge is the
        /// active menu.  Used by the keyboard/controller accessors and the global scroll patch.</summary>
        private static ForgeRingPanel? ActivePanel =>
            Game1.activeClickableMenu is ForgeMenu menu
            && Panels.TryGetValue(menu, out var panel)
                ? panel
                : null;

        public static bool IsPanelOpen => ActivePanel?.PanelOpen ?? false;

        public static void TogglePanel(bool playSound) => ActivePanel?.TogglePanel(playSound);

        /// <summary>Rebuild the active forge's panel (e.g. after the slot count changes in
        /// GMCM).  Drops the old components and re-injects a freshly built set.</summary>
        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is ForgeMenu menu)
            {
                menu.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                PanelFor(menu).Build();
                menu.populateClickableComponentList();
            }
        }

        /// <summary>Drop the active forge panel's draw-path dim memo.  Called (via
        /// Patches.InvalidateCraftCaches) when a config edit changes the dimming verdicts;
        /// the memo is per-panel now, so there is nothing to clear when no forge is open.</summary>
        internal static void InvalidateDimCache() => ActivePanel?.ClearDimMemo();

        /// <summary>Session cleanup on return to title: drop the active panel's dim memo and the
        /// save-scoped log-dedup set.</summary>
        internal static void ClearSessionCaches()
        {
            InvalidateDimCache();
            _testLogOnce.Clear();
        }

        // ============================================================
        //  Harmony wiring + delegators
        // ============================================================

        public static void Apply(Harmony harmony, IMonitor monitor)
        {
            harmony.Patch(
                original: AccessTools.Constructor(typeof(ForgeMenu)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Ctor_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.draw),
                    new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Draw_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveLeftClick)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(LeftClick_Prefix)));

            // Postfix to see what vanilla left in _heldItem after handling the click.
            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveLeftClick)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(LeftClick_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveRightClick)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(RightClick_Prefix)));

            // Postfix on update() to detect when vanilla finishes the craft (diagnostics).
            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.update),
                    new[] { typeof(GameTime) }),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Update_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.performHoverAction)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Hover_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.receiveScrollWheelAction)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(ScrollWheel_Prefix)));
        }

        [HarmonyPriority(Priority.Last)]
        public static void Ctor_Postfix(ForgeMenu __instance)
        {
            _testLogOnce.Clear();
            // Fresh menu: drop the PerScreen highlight memo so nothing from the previous menu
            // (or a config edit made in between) leaks in.  The per-panel dim memo starts empty.
            Patches.InvalidateCraftCaches();
            PanelFor(__instance).Build();
        }

        public static void Draw_Postfix(ForgeMenu __instance, SpriteBatch b)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.Draw(b);
        }

        public static bool LeftClick_Prefix(ForgeMenu __instance, int x, int y, bool playSound) =>
            !Panels.TryGetValue(__instance, out var panel) || panel.HandleLeftClick(x, y, playSound);

        public static void LeftClick_Postfix(ForgeMenu __instance, int x, int y)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.HandleLeftClickPostfix(x, y);
        }

        public static bool RightClick_Prefix(ForgeMenu __instance, int x, int y, bool playSound) =>
            !Panels.TryGetValue(__instance, out var panel) || panel.HandleRightClick(x, y, playSound);

        public static void Update_Postfix(ForgeMenu __instance)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.HandleUpdate();
        }

        public static void Hover_Postfix(ForgeMenu __instance, int x, int y)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.HandleHover(x, y);
        }

        public static bool ScrollWheel_Prefix(int direction) => ActivePanel?.HandleScroll(direction) ?? true;

        // ============================================================
        //  Craft-rules helpers (shared with ForgeRingPanel via using static,
        //  and with Patches.cs / the Forge Menu Choice shim)
        // ============================================================

        /// <summary>True if the item is something the forge would accept (ring, weapon, tool,
        /// or any item HighlightItems would mark valid like prismatic shards / gems / dragon tooth).</summary>
        internal static bool IsValidForgeItem(ForgeMenu menu, Item item)
        {
            if (item == null) return false;
            if (item is Ring) return true;
            if (item is StardewValley.Tools.MeleeWeapon weapon) return Patches.IsScytheForgingAllowed(weapon);
            if (item is StardewValley.Tools.Slingshot) return true;
            if (item is Tool) return true;

            // Gems → forge enchantments, and Diamond → random forge enchantment.
            if (StardewValley.Enchantments.BaseEnchantment.GetEnchantmentFromItem(null, item) != null)
                return true;

            // Prismatic Shard → adds a secondary (innate) enchantment.
            if (item.QualifiedItemId == "(O)74") return true;
            // Dragon Tooth → re-rolls the secondary enchantment on galaxy weapons.
            if (item.QualifiedItemId == "(O)852") return true;
            // Galaxy Soul → evolves fully-souled Galaxy weapons into Infinity weapons.
            if (item.QualifiedItemId == "(O)896") return true;

            // (Cinder Shard "(O)848" is the forge currency, NOT a right-slot ingredient.)
            return false;
        }

        /// <summary>True if dropping <paramref name="rightItem"/> onto a forge with
        /// <paramref name="leftTool"/> would actually produce a change.  Used to block
        /// "no-op" forges that would otherwise consume the right item with no result.</summary>
        internal static bool CanRightItemEnchantTool(Tool leftTool, Item rightItem)
        {
            // Same-type weapon: vanilla's "appearance copy" forge.
            if (leftTool is StardewValley.Tools.MeleeWeapon leftWeapon
                && rightItem is StardewValley.Tools.MeleeWeapon rightWeapon
                && rightWeapon.type.Value == leftWeapon.type.Value
                && Patches.IsScytheForgingAllowed(leftWeapon)
                && Patches.IsScytheForgingAllowed(rightWeapon))
            {
                return true;
            }

            // Prismatic Shard: applies a random secondary/innate enchantment.
            if (rightItem.QualifiedItemId == "(O)74")
            {
                if (ModEntry.Instance.Config.MultipleEnchantments)
                {
                    var available = StardewValley.Enchantments.BaseEnchantment
                        .GetAvailableEnchantmentsForItem(leftTool);
                    bool anyAvailable = available != null && available.Count > 0;
                    if (!anyAvailable)
                        TestLogOnce("CanRightItemEnchantTool: Prismatic Shard refused on " + leftTool.Name + " (no unapplied enchantments left)");
                    return anyAvailable;
                }

                var allEnch = StardewValley.Enchantments.BaseEnchantment.GetAvailableEnchantments();
                foreach (var ench in allEnch)
                {
                    if (ench.IsForge() || ench.IsSecondaryEnchantment()) continue;
                    if (ench.CanApplyTo(leftTool)) return true;
                }
                return false;
            }

            // Dragon Tooth: re-rolls (or, with DragonToothStacking, adds to) the innate stat
            // enchantments on a MeleeWeapon.  For non-Galaxy weapons below level 15, vanilla
            // handles this.  For Galaxy/Infinity weapons (and all weapons when stacking/always-max
            // are on) the Harmony prefix (Tool_Forge_DragonToothReroll_Prefix) takes over.
            if (rightItem.QualifiedItemId == "(O)852")
            {
                if (leftTool is not StardewValley.Tools.MeleeWeapon weapon) return false;
                if (!Patches.IsScytheForgingAllowed(weapon)) return false;
                if (ModEntry.Instance.Config.DragonToothStacking
                    && !Patches.HasInnateBelowMax(weapon))
                {
                    TestLogOnce("CanRightItemEnchantTool: Dragon Tooth refused on " + weapon.Name + " (every innate type at cap, stacking mode)");
                    return false;
                }
                return true;
            }

            // Diamond: weapon-only.
            if (rightItem.QualifiedItemId == "(O)72")
            {
                if (leftTool is not StardewValley.Tools.MeleeWeapon scytheCheck)
                    return false;
                if (!Patches.IsScytheForgingAllowed(scytheCheck))
                    return false;

                // Respect the forge-level cap: a Diamond craft at the cap is a no-op.
                if (leftTool.GetTotalForgeLevels() >= leftTool.GetMaxForges())
                {
                    TestLogOnce("CanRightItemEnchantTool: Diamond refused on " + leftTool.Name + " (at forge cap "
                        + leftTool.GetTotalForgeLevels() + "/" + leftTool.GetMaxForges() + ")");
                    return false;
                }

                bool anyMissing =
                    !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.EmeraldEnchantment>()
                    || !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.AquamarineEnchantment>()
                    || !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.RubyEnchantment>()
                    || !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.AmethystEnchantment>()
                    || !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.TopazEnchantment>()
                    || !leftTool.hasEnchantmentOfType<StardewValley.Enchantments.JadeEnchantment>()
                    || ModEntry.Instance.Config.RemoveDiamondForgesCap;
                return anyMissing;
            }

            // Gems (Ruby/Emerald/Topaz/Aquamarine/Jade/Amethyst): weapon-only.
            if (leftTool is not StardewValley.Tools.MeleeWeapon meleeForGem)
                return false;
            if (!Patches.IsScytheForgingAllowed(meleeForGem))
                return false;

            var enchantment = StardewValley.Enchantments.BaseEnchantment
                .GetEnchantmentFromItem(leftTool, rightItem);
            if (enchantment == null) return false;
            bool canAdd = leftTool.CanAddEnchantment(enchantment);
            if (!canAdd)
                TestLogOnce("CanRightItemEnchantTool: " + rightItem.Name + " refused on " + leftTool.Name
                    + " (CanAddEnchantment false — forge cap " + leftTool.GetTotalForgeLevels() + "/" + leftTool.GetMaxForges() + ")");
            return canAdd;
        }

        /// <summary>Returns true if <paramref name="ring"/> is NOT dimmed in the current forge
        /// context (non-ring-in-slot guard, vanilla IsValidCraft when InfiniteCombining is off,
        /// duplicate cap when InfiniteCombining + AddCombinedDuplicateRingCap are on).</summary>
        internal static bool IsRingAllowedInForgeContext(Ring ring, ForgeMenu menu)
        {
            bool nonRingInSlot =
                (menu.leftIngredientSpot.item  != null && menu.leftIngredientSpot.item  is not Ring) ||
                (menu.rightIngredientSpot.item != null && menu.rightIngredientSpot.item is not Ring);
            if (nonRingInSlot) return false;

            var forgeLeftRing  = menu.leftIngredientSpot.item  as Ring;
            var forgeRightRing = menu.rightIngredientSpot.item as Ring;

            if (!ModEntry.Instance.Config.InfiniteCombining)
            {
                if (forgeLeftRing != null && !IsValidCraft(menu, forgeLeftRing, ring))
                    return false;
                if (forgeRightRing != null && forgeLeftRing == null && !IsValidCraft(menu, ring, forgeRightRing))
                    return false;
                return true;
            }

            if (ModEntry.Instance.Config.AddCombinedDuplicateRingCap)
            {
                if (forgeLeftRing != null && Patches.WouldCreateDuplicateRing(forgeLeftRing, ring))
                    return false;
                if (forgeRightRing != null && Patches.WouldCreateDuplicateRing(forgeRightRing, ring))
                    return false;
            }

            return true;
        }

        /// <summary>Vanilla's left-slot rule (ForgeMenu._leftIngredientSpotClicked): any Tool or
        /// Ring, gated by IsValidCraftIngredient.  No upgrade-level requirement; scythes
        /// additionally need a scythe-forging mod.</summary>
        internal static bool IsAcceptedInLeftSlot(ForgeMenu menu, Item item) =>
            menu.IsValidCraftIngredient(item)
            && (item is Ring
                || (item is StardewValley.Tools.MeleeWeapon mw
                    ? Patches.IsScytheForgingAllowed(mw)
                    : item is Tool));

        /// <summary>Whether the item would be accepted in the right ingredient slot given the
        /// current left item: any forge ingredient when the left is empty, otherwise a valid
        /// (and non-no-op) pairing with the left item.</summary>
        internal static bool IsAcceptedInRightSlot(ForgeMenu menu, Item item)
        {
            Item? left = menu.leftIngredientSpot.item;
            if (left == null)
                return IsValidForgeItem(menu, item);
            if (!IsValidCraft(menu, left, item))
                return false;
            if (left is Tool leftTool && !CanRightItemEnchantTool(leftTool, item))
                return false;
            return true;
        }

        /// <summary>The first free forge ingredient slot that would form a valid pairing with
        /// <paramref name="ring"/> — left slot first, then right — or null when both are
        /// occupied or no valid placement exists.</summary>
        internal static ClickableTextureComponent? GetFreeForgeSlotFor(ForgeMenu menu, Ring ring)
        {
            Item? left = menu.leftIngredientSpot.item;
            Item? right = menu.rightIngredientSpot.item;
            if (left == null && (right == null || IsValidCraft(menu, ring, right)))
                return menu.leftIngredientSpot;
            if (right == null && (left == null || IsValidCraft(menu, left, ring)))
                return menu.rightIngredientSpot;
            return null;
        }

        internal static bool IsRingBlockedByDuplicateCap(Ring ring, ForgeMenu menu)
        {
            if (!ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                || !ModEntry.Instance.Config.InfiniteCombining)
                return false;
            if (menu.leftIngredientSpot.item is Ring leftRing && Patches.WouldCreateDuplicateRing(leftRing, ring))
                return true;
            if (menu.rightIngredientSpot.item is Ring rightRing && Patches.WouldCreateDuplicateRing(rightRing, ring))
                return true;
            return false;
        }

        /// <summary>Run vanilla's post-slot-change bookkeeping after we mutate the forge
        /// ingredient slots directly: drop the cached highlight dictionary (its regeneration
        /// probes IsValidCraft, which is what lets Forge Menu Choice notice a broken pair and
        /// close its carousel) and re-validate the craft state.  With BOTH slots empty no probes
        /// fire, so any stale carousel is closed explicitly.</summary>
        internal static void NotifyForgeSlotsChanged(ForgeMenu menu)
        {
            try
            {
                HighlightDictionaryField?.SetValue(menu, null);
                ValidateCraftMethod?.Invoke(menu, null);
                if (menu.leftIngredientSpot.item == null && menu.rightIngredientSpot.item == null)
                    ForgeMenuChoiceCompat.CloseCarousel();
            }
            catch
            {
                // Bookkeeping only — never break the click that triggered it.
            }
        }

        /// <summary>Read the forge's private MenuWithInventory._heldItem via reflection.</summary>
        internal static Item? GetHeldItem(ForgeMenu menu) =>
            HeldItemField?.GetValue(menu) as Item;

        /// <summary>Set the forge's private _heldItem via reflection (or clear it with null).</summary>
        internal static void SetHeldItem(ForgeMenu menu, Item? item) =>
            HeldItemField?.SetValue(menu, item);

        /// <summary>Side-effect-free craft validity: vanilla ForgeMenu.IsValidCraft's logic plus
        /// this mod's overrides, WITHOUT calling the patched method (so our per-frame highlight
        /// probing never fires other mods' stateful IsValidCraft patches, e.g. Forge Menu
        /// Choice's carousel open/close).  CanForge / CanCombine are still invoked virtually, so
        /// mods patching those deeper hooks keep working.</summary>
        internal static bool IsValidCraft(ForgeMenu menu, Item? left, Item? right)
        {
            bool vanilla = false;
            if (left != null && right != null)
            {
                if (left is Tool tool && tool.CanForge(right))
                    vanilla = true;
                else if (left is Ring leftRing && right is Ring rightRing && leftRing.CanCombine(rightRing))
                    vanilla = true;
            }
            return Patches.ApplyCraftOverrides(left, right, vanilla);
        }
    }
}
