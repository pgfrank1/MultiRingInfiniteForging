using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>Adds extra ring slots into the vanilla InventoryPage equipment column.</summary>
    public static class InventoryPagePatches
    {
        private const int SlotSize = 64;
        private const int SlotSpacing = 4;
        private const int FirstSlotId = 110_000;

        private static IMonitor Log = null!;

        /// <summary>Our extra-slot components.  Also added to InventoryPage.equipmentIcons
        /// so vanilla gamepad navigation can reach them.</summary>
        private static readonly List<ClickableComponent> Slots = new();

        public static void Apply(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            harmony.Patch(
                original: AccessTools.Constructor(typeof(InventoryPage),
                    new[] { typeof(int), typeof(int), typeof(int), typeof(int) }),
                postfix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(Ctor_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.draw),
                    new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(Draw_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.receiveLeftClick)),
                prefix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(LeftClick_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.receiveRightClick)),
                prefix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(RightClick_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.performHoverAction)),
                postfix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(Hover_Postfix))
            );
        }

        /// <summary>
        /// Rebuild the slots when the config changes (e.g. from Generic Mod Config Menu).
        /// </summary>
        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                // Drop any previously-injected slots, then rebuild and re-inject.
                page.equipmentIcons.RemoveAll(c => c.name.StartsWith("ExtraRing"));
                Ctor_Postfix(page);
            }
        }

        // ============================================================
        //  Layout
        //
        //  The vanilla equipment column ends at row "Boots" / "Pants",
        //  about 384 pixels below the menu's top border.  We place our
        //  extra slots as one or two rows immediately under that, spanning
        //  from the Left-Ring column rightward.
        // ============================================================

        private static void RebuildSlots(InventoryPage page)
        {
            Slots.Clear();
            RingSlotManager.EnsureSize();

            ClickableComponent? boots = page.equipmentIcons.Find(c => c.name == "Boots");
            ClickableComponent? pants = page.equipmentIcons.Find(c => c.name == "Pants");
            ClickableComponent? leftRing = page.equipmentIcons.Find(c => c.name == "Left Ring");
            if (boots == null || leftRing == null) return;

            // Anchor row right under the Boots/Pants row, with a small gap.
            int startY = boots.bounds.Y + SlotSize + SlotSpacing * 2;
            int startX = leftRing.bounds.X;

            // Max slots per row = how many fit horizontally before we'd run into the
            // trinket column (vanilla "Trinket" is at xPositionOnScreen + 48 + 280).
            // The Left-Ring column starts at xPositionOnScreen + 48, so we have 280px
            // = 4 slots (4 * (64 + 4) = 272) before colliding.
            const int maxPerRow = 4;

            for (int i = 0; i < RingSlotManager.SlotCount; i++)
            {
                int col = i % maxPerRow;
                int row = i / maxPerRow;

                var slot = new ClickableComponent(
                    new Rectangle(
                        startX + col * (SlotSize + SlotSpacing),
                        startY + row * (SlotSize + SlotSpacing),
                        SlotSize, SlotSize),
                    name: "ExtraRing" + i)
                {
                    myID = FirstSlotId + i,
                    upNeighborID = row == 0
                        ? (col == 0 ? boots.myID
                            : col == 1 ? (pants?.myID ?? boots.myID)
                            : -99998)
                        : FirstSlotId + i - maxPerRow,
                    downNeighborID = -99998,
                    leftNeighborID = col == 0 ? -99998 : FirstSlotId + i - 1,
                    rightNeighborID = (col == maxPerRow - 1 || i == RingSlotManager.SlotCount - 1)
                        ? -99998
                        : FirstSlotId + i + 1,
                    fullyImmutable = true
                };

                Slots.Add(slot);
            }
        }

        // ============================================================
        //  Inject our components into equipmentIcons so gamepad nav reaches them.
        // ============================================================

        public static void Ctor_Postfix(InventoryPage __instance)
        {
            RebuildSlots(__instance);

            foreach (var slot in Slots)
                __instance.equipmentIcons.Add(slot);

            // Wire the bottom of the existing equipment column to point down into our slots.
            var boots = __instance.equipmentIcons.Find(c => c.name == "Boots");
            var pants = __instance.equipmentIcons.Find(c => c.name == "Pants");
            if (boots != null && Slots.Count >= 1)
                boots.downNeighborID = Slots[0].myID;
            if (pants != null && Slots.Count >= 2)
                pants.downNeighborID = Slots[1].myID;
        }

        // ============================================================
        //  Draw
        // ============================================================

        public static void Draw_Postfix(InventoryPage __instance, SpriteBatch b)
        {
            foreach (var slot in Slots)
            {
                int idx = SlotIndex(slot);
                var ring = idx >= 0 && idx < RingSlotManager.Slots.Count
                    ? RingSlotManager.Slots[idx]
                    : null;

                int sourceIdx = ring != null ? 10 : 41;
                b.Draw(Game1.menuTexture, slot.bounds,
                    Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, sourceIdx),
                    Color.White);

                ring?.drawInMenu(b, new Vector2(slot.bounds.X, slot.bounds.Y), slot.scale);
            }

            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);
        }

        // ============================================================
        //  Click handling
        // ============================================================

        public static bool LeftClick_Prefix(InventoryPage __instance, int x, int y, bool playSound)
        {
            foreach (var slot in Slots)
            {
                if (!slot.containsPoint(x, y)) continue;

                int idx = SlotIndex(slot);
                if (idx < 0) return true;

                Item? held = Game1.player.CursorSlotItem;
                if (held != null && held is not Ring) return false;

                Ring? heldRing = held as Ring;
                Ring? current = RingSlotManager.Slots[idx];

                RingSlotManager.Equip(idx, heldRing);
                Game1.player.CursorSlotItem = current;

                if (playSound) Game1.playSound("crit");
                return false;
            }
            return true;
        }

        public static bool RightClick_Prefix(InventoryPage __instance, int x, int y, bool playSound)
        {
            foreach (var slot in Slots)
            {
                if (!slot.containsPoint(x, y)) continue;

                int idx = SlotIndex(slot);
                if (idx < 0) return true;

                Ring? ring = RingSlotManager.Slots[idx];
                if (ring == null) return false;

                Ring? heldRing = Game1.player.CursorSlotItem as Ring;
                if (heldRing != null)
                {
                    RingSlotManager.Equip(idx, heldRing);
                    Game1.player.CursorSlotItem = ring;
                }
                else
                {
                    RingSlotManager.Equip(idx, null);
                    var leftover = Game1.player.addItemToInventory(ring);
                    if (leftover != null) Game1.player.CursorSlotItem = leftover;
                }

                if (playSound) Game1.playSound("coin");
                return false;
            }
            return true;
        }

        // ============================================================
        //  Hover
        // ============================================================

        private static string _hoverText = "";

        public static void Hover_Postfix(InventoryPage __instance, int x, int y)
        {
            _hoverText = "";
            foreach (var slot in Slots)
            {
                bool inside = slot.containsPoint(x, y);
                if (inside)
                    slot.scale = System.Math.Min(slot.scale + 0.05f, 1.1f);
                else
                    slot.scale = System.Math.Max(1f, slot.scale - 0.025f);

                if (!inside) continue;

                int idx = SlotIndex(slot);
                var ring = idx >= 0 && idx < RingSlotManager.Slots.Count
                    ? RingSlotManager.Slots[idx]
                    : null;
                _hoverText = ring != null
                    ? ring.DisplayName + "\n" + ring.getDescription()
                    : "Empty extra ring slot";
            }
        }

        // ============================================================
        //  Helpers
        // ============================================================

        private static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;
    }
}