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
        private const int ScrollUpBtnId = 109_998;
        private const int ScrollDownBtnId = 109_997;
        private const int ScrollBtnWidth = 40;
        private const int ScrollBtnHeight = 16;

        private static IMonitor Log = null!;

        private static readonly List<ClickableComponent> Slots = new();
        private static ClickableTextureComponent? ToggleButton;
        private static bool _panelOpen;
        private static int _scrollOffset;
        private static int _maxScrollOffset;
        private static ClickableComponent? _scrollUpBtn;
        private static ClickableComponent? _scrollDownBtn;

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

            harmony.Patch(
                original: AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.receiveScrollWheelAction)),
                prefix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(ScrollWheel_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.gameWindowSizeChanged)),
                postfix: new HarmonyMethod(typeof(InventoryPagePatches), nameof(WindowResized_Postfix))
            );
        }

        public static bool ScrollWheel_Prefix(int direction)
        {
            if (!_panelOpen || ToggleButton == null || _maxScrollOffset <= 0) return true;

            var mousePos = Game1.getMousePosition();
            var panelBounds = GetPanelBackground();
            if (panelBounds == Rectangle.Empty) return true;

            Rectangle scrollArea = new Rectangle(
                panelBounds.X - 16, panelBounds.Y - 16 - ScrollBtnHeight,
                panelBounds.Width + 32, panelBounds.Height + 32 + ScrollBtnHeight * 2);

            if (!scrollArea.Contains(mousePos)) return true;

            if (direction > 0 && _scrollOffset > 0)
                _scrollOffset--;
            else if (direction < 0 && _scrollOffset < _maxScrollOffset)
                _scrollOffset++;
            else
                return true;

            ApplyPanelVisibility();
            if (Game1.activeClickableMenu is GameMenu gm1
                && gm1.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();
                gm1.populateClickableComponentList();
            }
            SnapToVisibleSlot();
            return false;
        }

        private static void SnapToVisibleSlot()
        {
            if (!Game1.options.SnappyMenus) return;
            if (Game1.activeClickableMenu is not GameMenu gm) return;
            if (gm.pages[GameMenu.inventoryTab] is not InventoryPage page) return;
            var snapped = page.currentlySnappedComponent;
            if (snapped == null) return;
            if (!snapped.name.StartsWith("ExtraRing") || snapped.name.StartsWith("ExtraRingScroll"))
                return;

            if (snapped.bounds.X <= -5000)
            {
                var firstVisible = Slots.FirstOrDefault(s => s.bounds.X > -5000);
                if (firstVisible != null)
                {
                    page.setCurrentlySnappedComponentTo(firstVisible.myID);
                    return;
                }
            }

            page.snapCursorToCurrentSnappedComponent();
        }

        public static void WindowResized_Postfix(IClickableMenu __instance)
        {
            if (Game1.activeClickableMenu is not GameMenu gm) return;
            if (__instance != gm) return;
            if (gm.pages[GameMenu.inventoryTab] is not InventoryPage page) return;

            page.equipmentIcons.RemoveAll(c =>
                c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");

            RebuildSlots(page);

            if (ToggleButton != null)
                page.equipmentIcons.Add(ToggleButton);
            foreach (var slot in Slots)
                page.equipmentIcons.Add(slot);
            if (_scrollUpBtn != null)
                page.equipmentIcons.Add(_scrollUpBtn);
            if (_scrollDownBtn != null)
                page.equipmentIcons.Add(_scrollDownBtn);

            var boots = page.equipmentIcons.Find(c => c.name == "Boots");
            if (boots != null && ToggleButton != null)
                boots.downNeighborID = ToggleButton.myID;

            page.populateClickableComponentList();
            gm.populateClickableComponentList();
        }

        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                InventoryPagePatches.Ctor_Postfix(page);
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
            _scrollUpBtn = null;
            _scrollDownBtn = null;
            RingSlotManager.EnsureSize();

            var toggleBounds = GetToggleBounds(page);
            if (toggleBounds == Rectangle.Empty) return;

            ToggleButton = new ClickableTextureComponent(
                name: "ExtraRingToggle",
                bounds: toggleBounds,
                label: null,
                hoverText: ModEntry.T("panel.label"),
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
            ToggleButton.baseScale = 4f;

            int gridStartX = toggleBounds.X + SlotSize + SlotSpacing * 2;
            int gridStartY = toggleBounds.Y;
            const int maxPerRow = 6;

            int panelTopY = toggleBounds.Y;
            int availableHeight = (Game1.uiViewport.Height - 16) - panelTopY;
            int maxVisibleRows = availableHeight / (SlotSize + SlotSpacing);
            if (maxVisibleRows < 1) maxVisibleRows = 1;

            int totalRows = (RingSlotManager.SlotCount + maxPerRow - 1) / maxPerRow;
            _maxScrollOffset = totalRows > maxVisibleRows ? totalRows - maxVisibleRows : 0;
            if (_scrollOffset > _maxScrollOffset) _scrollOffset = _maxScrollOffset;

            // Scroll up button (top of panel)
            int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) - ScrollBtnWidth;
            _scrollUpBtn = new ClickableComponent(
                new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollUp")
            {
                myID = ScrollUpBtnId,
                upNeighborID = -99998,
                downNeighborID = -99998,
                leftNeighborID = -99998,
                rightNeighborID = -99998
            };

            // Scroll down button (bottom of panel)
            _scrollDownBtn = new ClickableComponent(
                new Rectangle(scrollBtnX, panelTopY + maxVisibleRows * (SlotSize + SlotSpacing), ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollDown")
            {
                myID = ScrollDownBtnId,
                upNeighborID = -99998,
                downNeighborID = -99998,
                leftNeighborID = -99998,
                rightNeighborID = -99998
            };

            for (int i = 0; i < RingSlotManager.SlotCount; i++)
            {
                int col = i % maxPerRow;
                int row = i / maxPerRow;

                bool visible = row >= _scrollOffset && row < _scrollOffset + maxVisibleRows;

                int displayRow = row - _scrollOffset;

                var slot = new ClickableComponent(
                    visible
                        ? new Rectangle(
                            gridStartX + col * (SlotSize + SlotSpacing),
                            panelTopY + displayRow * (SlotSize + SlotSpacing),
                            SlotSize, SlotSize)
                        : new Rectangle(-9999, -9999, 0, 0),
                    name: "ExtraRing" + i)
                {
                    myID = FirstSlotId + i,
                    leftNeighborID = col == 0
                        ? ToggleButtonId
                        : FirstSlotId + i - 1,
                    rightNeighborID = (col == maxPerRow - 1 || i == RingSlotManager.SlotCount - 1)
                        ? -99998
                        : FirstSlotId + i + 1,
                    upNeighborID = -99998,
                    downNeighborID = -99998,
                    fullyImmutable = true
                };

                // Wire D-pad navigation for visible rows
                if (visible)
                {
                    if (displayRow == 0)
                    {
                        slot.upNeighborID = ScrollUpBtnId;
                    }
                    else
                    {
                        slot.upNeighborID = FirstSlotId + i - maxPerRow;
                    }

                    if (displayRow == maxVisibleRows - 1 || row == totalRows - 1)
                    {
                        slot.downNeighborID = ScrollDownBtnId;
                    }
                    else
                    {
                        slot.downNeighborID = FirstSlotId + i + maxPerRow;
                    }
                }

                Slots.Add(slot);
            }

            ApplyPanelVisibility();
        }

        private static void ApplyPanelVisibility()
        {
            if (ToggleButton == null) return;

            ToggleButton.rightNeighborID = _panelOpen && Slots.Count > 0
                ? Slots[0].myID
                : -99998;

            int gridStartX = ToggleButton.bounds.X + SlotSize + SlotSpacing * 2;
            int panelTopY = ToggleButton.bounds.Y;
            const int maxPerRow = 6;

            int windowBottom = Game1.uiViewport.Height - 16;
            int availableHeight = windowBottom - panelTopY;

            if (availableHeight < SlotSize + SlotSpacing)
            {
                panelTopY = windowBottom - (SlotSize + SlotSpacing);
                if (panelTopY < 0) panelTopY = 0;
                availableHeight = windowBottom - panelTopY;
            }

            int maxVisibleRows = availableHeight / (SlotSize + SlotSpacing);
            if (maxVisibleRows < 1) maxVisibleRows = 1;

            int totalRows = (RingSlotManager.SlotCount + maxPerRow - 1) / maxPerRow;
            _maxScrollOffset = totalRows > maxVisibleRows ? totalRows - maxVisibleRows : 0;
            if (_scrollOffset > _maxScrollOffset) _scrollOffset = _maxScrollOffset;

            var firstVisibleSlot = -1;
            var lastVisibleSlot = -1;

            for (int i = 0; i < Slots.Count; i++)
            {
                int row = i / maxPerRow;
                bool visible = _panelOpen && row >= _scrollOffset && row < _scrollOffset + maxVisibleRows;

                if (visible)
                {
                    int displayRow = row - _scrollOffset;
                    int col = i % maxPerRow;
                    Slots[i].bounds = new Rectangle(
                        gridStartX + col * (SlotSize + SlotSpacing),
                        panelTopY + displayRow * (SlotSize + SlotSpacing),
                        SlotSize, SlotSize);

                    if (firstVisibleSlot < 0) firstVisibleSlot = i;
                    lastVisibleSlot = i;

                    if (displayRow == 0)
                        Slots[i].upNeighborID = ScrollUpBtnId;
                    else
                        Slots[i].upNeighborID = FirstSlotId + i - maxPerRow;

                    if (displayRow == maxVisibleRows - 1 || row == totalRows - 1)
                        Slots[i].downNeighborID = ScrollDownBtnId;
                    else
                        Slots[i].downNeighborID = FirstSlotId + i + maxPerRow;
                }
                else
                {
                    Slots[i].bounds = new Rectangle(-9999, -9999, 0, 0);
                    Slots[i].upNeighborID = -99998;
                    Slots[i].downNeighborID = -99998;
                }
            }

            if (_scrollUpBtn != null)
            {
                int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) - ScrollBtnWidth;
                _scrollUpBtn.bounds = new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight);
                _scrollUpBtn.leftNeighborID = -99998;
                _scrollUpBtn.rightNeighborID = -99998;
                _scrollUpBtn.downNeighborID = firstVisibleSlot >= 0 ? FirstSlotId + firstVisibleSlot : -99998;
                _scrollUpBtn.visible = _panelOpen && _scrollOffset > 0;
            }

            if (_scrollDownBtn != null)
            {
                int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) - ScrollBtnWidth;
                _scrollDownBtn.bounds = new Rectangle(scrollBtnX, panelTopY + maxVisibleRows * (SlotSize + SlotSpacing), ScrollBtnWidth, ScrollBtnHeight);
                _scrollDownBtn.leftNeighborID = -99998;
                _scrollDownBtn.rightNeighborID = -99998;
                _scrollDownBtn.upNeighborID = lastVisibleSlot >= 0 ? FirstSlotId + lastVisibleSlot : -99998;
                _scrollDownBtn.visible = _panelOpen && _scrollOffset < _maxScrollOffset;
            }
        }

        // ============================================================
        //  Inject into equipmentIcons
        // ============================================================

        [HarmonyPriority(Priority.Last)]
        public static void Ctor_Postfix(InventoryPage __instance)
        {
            _panelOpen = false;
            _scrollOffset = 0;
            RebuildSlots(__instance);

            if (ToggleButton != null)
                __instance.equipmentIcons.Add(ToggleButton);
            foreach (var slot in Slots)
                __instance.equipmentIcons.Add(slot);
            if (_scrollUpBtn != null)
                __instance.equipmentIcons.Add(_scrollUpBtn);
            if (_scrollDownBtn != null)
                __instance.equipmentIcons.Add(_scrollDownBtn);

            var boots = __instance.equipmentIcons.Find(c => c.name == "Boots");
            if (boots != null && ToggleButton != null)
                boots.downNeighborID = ToggleButton.myID;

            ModEntry.DiagVerbose("[Test] Inventory panel initialized with " + Slots.Count + " slots");
        }

        // ============================================================
        //  Draw
        // ============================================================

        public static void Draw_Postfix(InventoryPage __instance, SpriteBatch b)
        {
            if (ToggleButton != null)
            {
                b.Draw(Game1.menuTexture, ToggleButton.bounds,
                    Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                    Color.White);

                b.Draw(
                    ToggleButton.texture,
                    new Vector2(ToggleButton.bounds.X + 16, ToggleButton.bounds.Y + 16),
                    ToggleButton.sourceRect,
                    Color.White,
                    rotation: 0f,
                    origin: Vector2.Zero,
                    scale: 2f,
                    effects: SpriteEffects.None,
                    layerDepth: 0.86f);

                Utility.drawTextWithShadow(b,
                    _panelOpen ? "<" : ">",
                    Game1.smallFont,
                    new Vector2(ToggleButton.bounds.Right - 16, ToggleButton.bounds.Bottom - 24),
                    Game1.textColor);
            }

            if (_panelOpen && Slots.Count > 0)
            {
                Rectangle panelBg = GetPanelBackground();
                IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    panelBg.X, panelBg.Y, panelBg.Width, panelBg.Height,
                    Color.White, 1f, drawShadow: true);

                // Scroll up arrow
                if (_scrollUpBtn != null && _scrollOffset > 0)
                {
                    Color arrowCol = _scrollUpBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : Color.White;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollUpBtn.bounds.X, _scrollUpBtn.bounds.Y),
                        new Rectangle(76, 72, 40, 16), arrowCol, 0f, Vector2.Zero, 1f);
                }

                foreach (var slot in Slots)
                {
                    if (slot.bounds.Width <= 0) continue;
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

                // Scroll down arrow
                if (_scrollDownBtn != null && _scrollOffset < _maxScrollOffset)
                {
                    Color arrowCol = _scrollDownBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : Color.White;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollDownBtn.bounds.X, _scrollDownBtn.bounds.Y),
                        new Rectangle(76, 88, 40, 16), arrowCol, 0f, Vector2.Zero, 1f);
                }
            }

            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);

            // Redraw the cursor-held item ONLY if the mouse is currently over our panel,
            // so the held ring isn't hidden behind the panel/slot frames.  Outside the
            // panel, vanilla already drew the held item — drawing again would double it.
            if (Game1.player.CursorSlotItem != null && _panelOpen && Slots.Count > 0)
            {
                int mx = Game1.getOldMouseX();
                int my = Game1.getOldMouseY();
                Rectangle panelBg = GetPanelBackground();
                if (panelBg.Contains(mx, my))
                {
                    Game1.player.CursorSlotItem.drawInMenu(b,
                        new Vector2(mx + 8, my + 8),
                        1f);
                }
            }
        }

        private static Rectangle GetPanelBackground()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var s in Slots)
            {
                if (s.bounds.Width <= 0) continue;
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
                    ModEntry.DiagVerbose("[Test] Inventory toggle clicked, panel=" + !_panelOpen);
                    TogglePanel(playSound);
                    return false;
                }

                if (!_panelOpen)
                {
                    ModEntry.DiagVerbose("[Test] Inventory panel closed, passing click to vanilla");
                    return true;
                }

                // Scroll buttons
                if (_scrollUpBtn != null && _scrollUpBtn.containsPoint(x, y))
                {
                    if (_scrollOffset > 0) _scrollOffset--;
                    ApplyPanelVisibility();
                    if (Game1.activeClickableMenu is GameMenu gm
                        && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
                    {
                        page.populateClickableComponentList();
                        gm.populateClickableComponentList();
                    }
                    SnapToVisibleSlot();
                    if (playSound) Game1.playSound("bigDeSelect");
                    return false;
                }
                if (_scrollDownBtn != null && _scrollDownBtn.containsPoint(x, y))
                {
                    if (_scrollOffset < _maxScrollOffset) _scrollOffset++;
                    ApplyPanelVisibility();
                    if (Game1.activeClickableMenu is GameMenu gm
                        && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
                    {
                        page.populateClickableComponentList();
                        gm.populateClickableComponentList();
                    }
                    SnapToVisibleSlot();
                    if (playSound) Game1.playSound("bigDeSelect");
                    return false;
                }

                foreach (var slot in Slots)
                {
                    if (!slot.containsPoint(x, y)) continue;

                    int idx = SlotIndex(slot);
                    if (idx < 0) return true;

                    Item? held = Game1.player.CursorSlotItem;
                    if (held != null && held is not Ring)
                    {
                        ModEntry.DiagVerbose("[Test] Inventory panel blocked: " + held.Name + " is not a ring");
                        return false;
                    }

                    Ring? heldRing = held as Ring;
                    Ring? current = RingSlotManager.Slots[idx];
                    ModEntry.DiagVerbose("[Test] Inventory panel left-click: slot " + idx + " " + (heldRing?.Name ?? "empty") + " ↔ " + (current?.Name ?? "empty"));

                    RingSlotManager.Equip(idx, heldRing);
                    Game1.player.CursorSlotItem = current;

                    if (playSound) Game1.playSound("crit");
                    return false;
                }
                return true;
            }

            public static bool RightClick_Prefix(InventoryPage __instance, int x, int y, bool playSound)
            {
                // === RIGHT-CLICK EQUIPPED-RING → PANEL TRANSFER ===
                // When the panel is open and the player right-clicks the vanilla Left Ring or
                // Right Ring equipment slot with an empty cursor, move that ring straight
                // into the first empty panel slot.  Left-click is left to vanilla so the
                // standard "pick up to cursor" behaviour is preserved.
                if (_panelOpen && Game1.player.CursorSlotItem == null)
                {
                    ClickableComponent? leftRing  = __instance.equipmentIcons.Find(c => c.name == "Left Ring");
                    ClickableComponent? rightRing = __instance.equipmentIcons.Find(c => c.name == "Right Ring");

                    bool clickedLeft  = leftRing  != null && leftRing.containsPoint(x, y);
                    bool clickedRight = rightRing != null && rightRing.containsPoint(x, y);

                    Ring? clickedRing = null;
                    if (clickedLeft)       clickedRing = Game1.player.leftRing.Value;
                    else if (clickedRight) clickedRing = Game1.player.rightRing.Value;

                    if (clickedRing != null)
                    {
                        int firstEmpty = -1;
                        for (int i = 0; i < RingSlotManager.Slots.Count; i++)
                            if (RingSlotManager.Slots[i] == null) { firstEmpty = i; break; }

                        if (firstEmpty >= 0)
                        {
                            ModEntry.DiagVerbose("[Test] Inventory: right-click transfer " + clickedRing.Name + " → panel slot " + firstEmpty);
                            clickedRing.onUnequip(Game1.player);
                            if (clickedLeft)  Game1.player.leftRing.Value  = null;
                            else              Game1.player.rightRing.Value = null;

                            RingSlotManager.Equip(firstEmpty, clickedRing);

                            if (playSound) Game1.playSound("crit");
                            return false;
                        }
                    }
                }

                if (!_panelOpen)
                {
                    ModEntry.DiagVerbose("[Test] Inventory panel closed, right-click passed to vanilla");
                    return true;
                }

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
                        ModEntry.DiagVerbose("[Test] Inventory: right-click swap " + heldRing.Name + " ↔ " + ring.Name + " in slot " + idx);
                        RingSlotManager.Equip(idx, heldRing);
                        Game1.player.CursorSlotItem = ring;
                    }
                    else
                    {
                        ModEntry.DiagVerbose("[Test] Inventory: right-click unequip " + ring.Name + " from slot " + idx + " to inventory");
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
            ModEntry.DiagVerbose("[Test] Inventory panel toggled: open=" + _panelOpen);
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(_panelOpen ? "bigSelect" : "bigDeSelect");

            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();

                if (Game1.options.SnappyMenus && ToggleButton != null)
                {
                    if (_panelOpen && Slots.Count > 0)
                        page.setCurrentlySnappedComponentTo(Slots[0].myID);
                    else
                        page.setCurrentlySnappedComponentTo(ToggleButton.myID);
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
                    ? ModEntry.T("panel.hover.hide")
                    : ModEntry.T("panel.hover.show");
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
                    : ModEntry.T("panel.slot.empty");
            }
        }

        // ============================================================
        //  Helpers
        // ============================================================

        private static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;
    }
}