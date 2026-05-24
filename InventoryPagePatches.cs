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
    /// <summary>Adds extra ring slots into the vanilla InventoryPage equipment column.
    /// The slots live behind a collapsible panel toggled via a small ring-book button.</summary>
    public static class InventoryPagePatches
    {
        private const int SlotSize = 64;
        private const int SlotSpacing = 4;
        private const int FirstSlotId = 110_000;
        private const int ToggleButtonId = 109_999;

        private static IMonitor Log = null!;

        private static readonly List<ClickableComponent> Slots = new();
        private static ClickableTextureComponent? ToggleButton;
        private static bool _panelOpen;

        /// <summary>True if the extra-ring panel is currently expanded.</summary>
        public static bool IsPanelOpen => _panelOpen;

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

        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                Ctor_Postfix(page);

                // Rebuild the snap component cache so navigation reaches the new IDs.
                page.populateClickableComponentList();
            }
        }

        // ============================================================
        //  Layout
        // ============================================================

        private static Rectangle GetToggleBounds(InventoryPage page)
        {
            ClickableComponent? boots = page.equipmentIcons.Find(c => c.name == "Boots");
            ClickableComponent? leftRing = page.equipmentIcons.Find(c => c.name == "Left Ring");
            if (boots == null || leftRing == null) return Rectangle.Empty;
            return new Rectangle(
                leftRing.bounds.X,
                boots.bounds.Y + SlotSize + SlotSpacing * 2,
                SlotSize, SlotSize);
        }

        private static void RebuildSlots(InventoryPage page)
        {
            Slots.Clear();
            ToggleButton = null;
            RingSlotManager.EnsureSize();

            var toggleBounds = GetToggleBounds(page);
            if (toggleBounds == Rectangle.Empty) return;

            // Toggle button.  baseScale must match scale so the icon doesn't drift smaller.
            ToggleButton = new ClickableTextureComponent(
                name: "ExtraRingToggle",
                bounds: toggleBounds,
                label: null,
                hoverText: "Extra Ring Slots",
                texture: Game1.objectSpriteSheet,
                sourceRect: Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 534, 16, 16),
                scale: 4f)
            {
                myID = ToggleButtonId,
                upNeighborID = page.equipmentIcons.Find(c => c.name == "Boots")?.myID ?? -99998,
                downNeighborID = -99998,
                leftNeighborID = -99998,
                rightNeighborID = -99998
            };
            ToggleButton.baseScale = 4f;  // freeze base scale so draw() doesn't shrink it

            int gridStartX = toggleBounds.X + SlotSize + SlotSpacing * 2;
            int gridStartY = toggleBounds.Y;
            const int maxPerRow = 6;

            for (int i = 0; i < RingSlotManager.SlotCount; i++)
            {
                int col = i % maxPerRow;
                int row = i / maxPerRow;

                var slot = new ClickableComponent(
                    new Rectangle(
                        gridStartX + col * (SlotSize + SlotSpacing),
                        gridStartY + row * (SlotSize + SlotSpacing),
                        SlotSize, SlotSize),
                    name: "ExtraRing" + i)
                {
                    myID = FirstSlotId + i,
                    leftNeighborID = col == 0
                        ? ToggleButtonId
                        : FirstSlotId + i - 1,
                    rightNeighborID = (col == maxPerRow - 1 || i == RingSlotManager.SlotCount - 1)
                        ? -99998
                        : FirstSlotId + i + 1,
                    upNeighborID = row == 0 ? -99998 : FirstSlotId + i - maxPerRow,
                    downNeighborID = -99998,
                    fullyImmutable = true
                };
                Slots.Add(slot);
            }

            ApplyPanelVisibility();
        }

        /// <summary>Show/hide the slot grid for the controller by changing only the toggle
        /// button's right-neighbor pointer. Slot IDs themselves stay stable. When the panel
        /// is closed we set the slots' bounds to a zero-size off-screen rectangle so the
        /// snap-cursor logic never picks them as a target either.</summary>
        private static void ApplyPanelVisibility()
        {
            if (ToggleButton == null) return;

            ToggleButton.rightNeighborID = _panelOpen && Slots.Count > 0
                ? Slots[0].myID
                : -99998;

            for (int i = 0; i < Slots.Count; i++)
            {
                if (_panelOpen)
                {
                    // Restore visible bounds.
                    int col = i % 6;
                    int row = i / 6;
                    Slots[i].bounds = new Rectangle(
                        ToggleButton.bounds.X + SlotSize + SlotSpacing * 2 + col * (SlotSize + SlotSpacing),
                        ToggleButton.bounds.Y + row * (SlotSize + SlotSpacing),
                        SlotSize, SlotSize);
                }
                else
                {
                    // Move off-screen so containsPoint() and snap-region search ignore them.
                    Slots[i].bounds = new Rectangle(-9999, -9999, 0, 0);
                }
            }
        }

        // ============================================================
        //  Inject into equipmentIcons
        // ============================================================

        public static void Ctor_Postfix(InventoryPage __instance)
        {
            _panelOpen = false;
            RebuildSlots(__instance);

            if (ToggleButton != null)
                __instance.equipmentIcons.Add(ToggleButton);
            foreach (var slot in Slots)
                __instance.equipmentIcons.Add(slot);

            // D-pad down from Boots → toggle button.
            var boots = __instance.equipmentIcons.Find(c => c.name == "Boots");
            if (boots != null && ToggleButton != null)
                boots.downNeighborID = ToggleButton.myID;
        }

        // ============================================================
        //  Draw
        // ============================================================

        public static void Draw_Postfix(InventoryPage __instance, SpriteBatch b)
        {
            // 1) Toggle button.
            if (ToggleButton != null)
            {
                // Background plate that matches an equipment slot.
                b.Draw(Game1.menuTexture, ToggleButton.bounds,
                    Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                    Color.White);

                // Draw the icon manually so we don't lean on ClickableTextureComponent.draw
                // (which animates scale toward baseScale and can fight our static size).
                b.Draw(
                    ToggleButton.texture,
                    new Vector2(ToggleButton.bounds.X + 16, ToggleButton.bounds.Y + 16),
                    ToggleButton.sourceRect,
                    Color.White,
                    rotation: 0f,
                    origin: Vector2.Zero,
                    scale: 2f,           // 16px sprite drawn at 2x = 32px, centered in 64px slot
                    effects: SpriteEffects.None,
                    layerDepth: 0.86f);

                Utility.drawTextWithShadow(b,
                    _panelOpen ? "<" : ">",
                    Game1.smallFont,
                    new Vector2(ToggleButton.bounds.Right - 16, ToggleButton.bounds.Bottom - 24),
                    Game1.textColor);
            }

            // 2) Expanded slot panel.
            if (_panelOpen && Slots.Count > 0)
            {
                Rectangle panelBg = GetPanelBackground();
                IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    panelBg.X, panelBg.Y, panelBg.Width, panelBg.Height,
                    Color.White, 1f, drawShadow: true);

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
            }

            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);
        }

        private static Rectangle GetPanelBackground()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var s in Slots)
            {
                if (s.bounds.X < minX) minX = s.bounds.X;
                if (s.bounds.Y < minY) minY = s.bounds.Y;
                if (s.bounds.Right > maxX) maxX = s.bounds.Right;
                if (s.bounds.Bottom > maxY) maxY = s.bounds.Bottom;
            }
            const int pad = 16;
            return new Rectangle(minX - pad, minY - pad,
                                 (maxX - minX) + pad * 2,
                                 (maxY - minY) + pad * 2);
        }

        // ============================================================
        //  Click handling
        // ============================================================

        public static bool LeftClick_Prefix(InventoryPage __instance, int x, int y, bool playSound)
        {
            if (ToggleButton != null && ToggleButton.containsPoint(x, y))
            {
                TogglePanel(playSound);
                return false;
            }

            if (!_panelOpen) return true;

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
            if (!_panelOpen) return true;

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
        //  Toggle
        // ============================================================

        public static void TogglePanel(bool playSound)
        {
            _panelOpen = !_panelOpen;
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(_panelOpen ? "bigSelect" : "bigDeSelect");

            // Rebuild the snap-region cache so the controller can reach our newly-visible
            // (or newly-hidden) slot rectangles.
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();

                if (Game1.options.SnappyMenus && ToggleButton != null)
                {
                    if (_panelOpen && Slots.Count > 0)
                    {
                        // Move focus into the panel.
                        page.setCurrentlySnappedComponentTo(Slots[0].myID);
                    }
                    else
                    {
                        // Keep focus on the toggle button after closing.
                        page.setCurrentlySnappedComponentTo(ToggleButton.myID);
                    }
                    page.snapCursorToCurrentSnappedComponent();
                }
            }
        }

        // ============================================================
        //  Hover
        // ============================================================

        private static string _hoverText = "";

        public static void Hover_Postfix(InventoryPage __instance, int x, int y)
        {
            _hoverText = "";

            if (ToggleButton != null && ToggleButton.containsPoint(x, y))
            {
                _hoverText = _panelOpen
                    ? "Hide extra ring slots"
                    : "Show extra ring slots";
                return;
            }

            if (!_panelOpen) return;

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