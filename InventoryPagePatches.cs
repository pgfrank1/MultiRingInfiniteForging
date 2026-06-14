using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Harmony entry points for the inventory equipment page's extra-ring panel.
    /// The panel state and behavior live in <see cref="InventoryRingPanel"/>, one instance
    /// per <see cref="InventoryPage"/> (so split-screen players each get their own, keyed by
    /// the page instance instead of PerScreen bookkeeping).  These patches just route each
    /// call to the panel for the page being drawn/clicked.</summary>
    public static class InventoryPagePatches
    {
        private static readonly ConditionalWeakTable<InventoryPage, InventoryRingPanel> Panels = new();

        private static InventoryRingPanel PanelFor(InventoryPage page) =>
            Panels.GetValue(page, p => new InventoryRingPanel(p));

        /// <summary>The panel for the inventory page currently on screen, or null when the
        /// inventory tab isn't the active menu.  Used by the keyboard/controller accessors
        /// that don't have a page argument.</summary>
        private static InventoryRingPanel? ActivePanel =>
            Game1.activeClickableMenu is GameMenu gm
            && gm.currentTab == GameMenu.inventoryTab
            && gm.pages[GameMenu.inventoryTab] is InventoryPage page
            && Panels.TryGetValue(page, out var panel)
                ? panel
                : null;

        public static bool IsPanelOpen => ActivePanel?.PanelOpen ?? false;

        public static void TogglePanel(bool playSound) => ActivePanel?.TogglePanel(playSound);

        /// <summary>Rebuild the active page's panel (e.g. after the slot count changes in
        /// GMCM).  Drops the old components and re-injects a freshly built set.</summary>
        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                var panel = PanelFor(page);
                panel.Build(panel.PanelOpen);
                page.populateClickableComponentList();
            }
        }

        public static void Apply(Harmony harmony, IMonitor monitor)
        {
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
        }

        // ---- Harmony delegators: route to the panel for the page in hand ----

        public static void Ctor_Postfix(InventoryPage __instance)
        {
            // A window resize recreates the whole GameMenu (Game1.gameWindowSizeChanged builds
            // a new GameMenu + pages), so a fresh InventoryPage and panel are constructed
            // mid-session.  The old GameMenu is still the active menu while the new one is being
            // built, so inherit the outgoing page's open state -- resizing then doesn't collapse
            // the panel.  A genuine fresh menu open (no GameMenu active yet) starts closed.
            bool open = false;
            if (Game1.activeClickableMenu is GameMenu oldGm
                && oldGm.pages.Count > GameMenu.inventoryTab
                && oldGm.pages[GameMenu.inventoryTab] is InventoryPage oldPage
                && Panels.TryGetValue(oldPage, out var oldPanel))
            {
                open = oldPanel.PanelOpen;
            }
            PanelFor(__instance).Build(open);
        }

        public static void Draw_Postfix(InventoryPage __instance, SpriteBatch b)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.Draw(b);
        }

        public static bool LeftClick_Prefix(InventoryPage __instance, int x, int y, bool playSound) =>
            !Panels.TryGetValue(__instance, out var panel) || panel.HandleLeftClick(x, y, playSound);

        public static bool RightClick_Prefix(InventoryPage __instance, int x, int y, bool playSound) =>
            !Panels.TryGetValue(__instance, out var panel) || panel.HandleRightClick(x, y, playSound);

        public static void Hover_Postfix(InventoryPage __instance, int x, int y)
        {
            if (Panels.TryGetValue(__instance, out var panel)) panel.HandleHover(x, y);
        }

        public static bool ScrollWheel_Prefix(int direction) => ActivePanel?.HandleScroll(direction) ?? true;
    }

    /// <summary>The extra-ring slot panel for a single inventory equipment page: a small
    /// ring-book toggle below the vanilla equipment column that expands a scrollable grid of
    /// extra ring slots.  One instance per <see cref="InventoryPage"/>.</summary>
    internal sealed class InventoryRingPanel : RingSlotPanel
    {
        protected override int FirstSlotId => 110_000;
        protected override int ToggleButtonId => 109_999;
        protected override int ScrollUpBtnId => 109_998;
        protected override int ScrollDownBtnId => 109_997;

        private readonly InventoryPage _page;

        public InventoryRingPanel(InventoryPage page) => _page = page;

        protected override IClickableMenu SnapMenu => _page;
        protected override Rectangle PanelBounds() => GetPanelBackground();

        // ============================================================
        //  Build / inject into equipmentIcons
        // ============================================================

        public void Build(bool open = false)
        {
            _panelOpen = open;
            _scrollOffset = 0;
            RebuildSlots();

            if (ToggleButton != null)
                _page.equipmentIcons.Add(ToggleButton);
            foreach (var slot in Slots)
                _page.equipmentIcons.Add(slot);
            if (_scrollUpBtn != null)
                _page.equipmentIcons.Add(_scrollUpBtn);
            if (_scrollDownBtn != null)
                _page.equipmentIcons.Add(_scrollDownBtn);

            var boots = _page.equipmentIcons.Find(c => c.name == "Boots");
            if (boots != null && ToggleButton != null)
                boots.downNeighborID = ToggleButton.myID;

            ModEntry.DiagVerbose("[Test] Inventory panel initialized with " + Slots.Count + " slots");
        }

        // ============================================================
        //  Layout
        // ============================================================

        private Rectangle GetToggleBounds()
        {
            ClickableComponent? boots = _page.equipmentIcons.Find(c => c.name == "Boots");
            ClickableComponent? leftRing = _page.equipmentIcons.Find(c => c.name == "Left Ring");
            if (boots == null || leftRing == null) return Rectangle.Empty;
            return new Rectangle(
                leftRing.bounds.X,
                boots.bounds.Y + SlotSize + SlotSpacing * 2,
                SlotSize, SlotSize);
        }

        private void RebuildSlots()
        {
            Slots.Clear();
            ToggleButton = null;
            _scrollUpBtn = null;
            _scrollDownBtn = null;
            RingSlotManager.EnsureSize();

            var toggleBounds = GetToggleBounds();
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
                upNeighborID = _page.equipmentIcons.Find(c => c.name == "Boots")?.myID ?? -99998,
                downNeighborID = -99998,
                leftNeighborID = -99998,
                rightNeighborID = -99998
            };
            ToggleButton.baseScale = 4f;

            int gridStartX = toggleBounds.X + SlotSize + SlotSpacing * 2;
            const int maxPerRow = 6;

            int panelTopY = toggleBounds.Y;
            int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) + ScrollBarGap;

            _scrollUpBtn = new ClickableComponent(
                new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollUp")
            {
                myID = ScrollUpBtnId,
                upNeighborID = -99998, downNeighborID = -99998,
                leftNeighborID = -99998, rightNeighborID = -99998
            };
            _scrollDownBtn = new ClickableComponent(
                new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollDown")
            {
                myID = ScrollDownBtnId,
                upNeighborID = -99998, downNeighborID = -99998,
                leftNeighborID = -99998, rightNeighborID = -99998
            };

            for (int i = 0; i < RingSlotManager.SlotCount; i++)
            {
                var slot = new ClickableComponent(
                    new Rectangle(-9999, -9999, 0, 0),
                    name: "ExtraRing" + i)
                {
                    myID = FirstSlotId + i,
                    // Neighbor IDs are assigned by ApplyPanelVisibility (called below and on
                    // every toggle/scroll/resize); no need to wire them here.
                    fullyImmutable = true
                };
                Slots.Add(slot);
            }

            ApplyPanelVisibility();
        }

        private void ApplyPanelVisibility()
        {
            if (ToggleButton == null) return;
            const int maxPerRow = 6;

            int firstVisibleInv = _scrollOffset * maxPerRow;
            ToggleButton.rightNeighborID = PanelOpen && firstVisibleInv < Slots.Count
                ? Slots[firstVisibleInv].myID
                : -99998;

            int gridStartX = ToggleButton.bounds.X + SlotSize + SlotSpacing * 2;
            int panelTopY = ToggleButton.bounds.Y;

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
            _visibleRows = maxVisibleRows;

            int totalRows = (RingSlotManager.SlotCount + maxPerRow - 1) / maxPerRow;
            _maxScrollOffset = totalRows > maxVisibleRows ? totalRows - maxVisibleRows : 0;
            if (_scrollOffset > _maxScrollOffset) _scrollOffset = _maxScrollOffset;

            var firstVisibleSlot = -1;
            var lastVisibleSlot = -1;

            for (int i = 0; i < Slots.Count; i++)
            {
                int row = i / maxPerRow;
                bool visible = PanelOpen && row >= _scrollOffset && row < _scrollOffset + maxVisibleRows;

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

                    Slots[i].leftNeighborID = col == 0
                        ? ToggleButtonId
                        : FirstSlotId + i - 1;
                    Slots[i].rightNeighborID = (col == maxPerRow - 1 || i == RingSlotManager.SlotCount - 1)
                        ? -99998
                        : FirstSlotId + i + 1;

                    if (displayRow == 0)
                        Slots[i].upNeighborID = _scrollOffset > 0 ? ScrollUpBtnId : -99998;
                    else
                        Slots[i].upNeighborID = FirstSlotId + i - maxPerRow;

                    if (displayRow == maxVisibleRows - 1 || row == totalRows - 1)
                        Slots[i].downNeighborID = _scrollOffset < _maxScrollOffset ? ScrollDownBtnId : -99998;
                    else
                        Slots[i].downNeighborID = FirstSlotId + i + maxPerRow;
                }
                else
                {
                    Slots[i].bounds = new Rectangle(-9999, -9999, 0, 0);
                    Slots[i].leftNeighborID = -99998;
                    Slots[i].rightNeighborID = -99998;
                    Slots[i].upNeighborID = -99998;
                    Slots[i].downNeighborID = -99998;
                }
            }

            if (_scrollUpBtn != null)
            {
                int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) + ScrollBarGap;
                _scrollUpBtn.bounds = new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight);
                _scrollUpBtn.leftNeighborID = -99998;
                _scrollUpBtn.rightNeighborID = -99998;
                _scrollUpBtn.downNeighborID = firstVisibleSlot >= 0 ? FirstSlotId + firstVisibleSlot : -99998;
                _scrollUpBtn.visible = PanelOpen && _scrollOffset > 0;
            }

            if (_scrollDownBtn != null)
            {
                int scrollBtnX = gridStartX + maxPerRow * (SlotSize + SlotSpacing) + ScrollBarGap;
                _scrollDownBtn.bounds = new Rectangle(scrollBtnX,
                        System.Math.Min(panelTopY + maxVisibleRows * (SlotSize + SlotSpacing),
                            Game1.uiViewport.Height - ScrollBtnHeight - 16),
                        ScrollBtnWidth, ScrollBtnHeight);
                _scrollDownBtn.leftNeighborID = -99998;
                _scrollDownBtn.rightNeighborID = -99998;
                _scrollDownBtn.upNeighborID = lastVisibleSlot >= 0 ? FirstSlotId + lastVisibleSlot : -99998;
                _scrollDownBtn.visible = PanelOpen && _scrollOffset < _maxScrollOffset;
            }

            // Wire panel's first visible row UP to inventory's bottom row, and back down.
            int invCols = _page.inventory.capacity / _page.inventory.rows;
            if (invCols <= 0) invCols = 12;
            int invBottomRowStart = (_page.inventory.rows - 1) * invCols;

            if (PanelOpen)
            {
                int firstPanelIdx = _scrollOffset * maxPerRow;
                for (int col = 0; col < maxPerRow && col < invCols; col++)
                {
                    int panelIdx = firstPanelIdx + col;
                    int invIdx = invBottomRowStart + col;
                    if (panelIdx < Slots.Count && invIdx < _page.inventory.inventory.Count)
                    {
                        Slots[panelIdx].upNeighborID = _page.inventory.inventory[invIdx].myID;
                        _page.inventory.inventory[invIdx].downNeighborID = Slots[panelIdx].myID;
                    }
                }
            }
            else
            {
                for (int col = 0; col < invCols; col++)
                {
                    int invIdx = invBottomRowStart + col;
                    if (invIdx < _page.inventory.inventory.Count)
                        _page.inventory.inventory[invIdx].downNeighborID = -99998;
                }
            }
        }

        // ============================================================
        //  Draw
        // ============================================================

        public void Draw(SpriteBatch b)
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
                    PanelOpen ? "<" : ">",
                    Game1.smallFont,
                    new Vector2(ToggleButton.bounds.Right - 16, ToggleButton.bounds.Bottom - 24),
                    Game1.textColor);
            }

            if (PanelOpen && Slots.Count > 0)
            {
                Rectangle panelBg = GetPanelBackground();
                IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    panelBg.X, panelBg.Y, panelBg.Width, panelBg.Height,
                    Color.White, 1f, drawShadow: true);

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

                DrawScrollbarAndArrows(b);
            }

            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);

            // Redraw the cursor-held item ONLY if the mouse is currently over our panel, so
            // the held ring isn't hidden behind the panel/slot frames.  Outside the panel,
            // vanilla already drew the held item; drawing again would double it.
            if (Game1.player.CursorSlotItem != null && PanelOpen && Slots.Count > 0)
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

        private Rectangle GetPanelBackground()
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

        public bool HandleLeftClick(int x, int y, bool playSound)
        {
            if (ToggleButton != null && ToggleButton.containsPoint(x, y))
            {
                ModEntry.DiagVerbose("[Test] Inventory toggle clicked, panel=" + !PanelOpen);
                TogglePanel(playSound);
                return false;
            }

            if (!PanelOpen)
                return true;

            // Scroll buttons
            if (_scrollUpBtn != null && _scrollUpBtn.containsPoint(x, y))
            {
                if (_scrollOffset > 0) _scrollOffset--;
                AfterScrollChange();
                if (playSound) Game1.playSound("bigDeSelect");
                return false;
            }
            if (_scrollDownBtn != null && _scrollDownBtn.containsPoint(x, y))
            {
                if (_scrollOffset < _maxScrollOffset) _scrollOffset++;
                AfterScrollChange();
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
                ModEntry.DiagVerbose("[Test] Inventory panel left-click: slot " + idx + " " + (heldRing?.Name ?? "empty") + " <-> " + (current?.Name ?? "empty"));

                RingSlotManager.Equip(idx, heldRing);
                Game1.player.CursorSlotItem = current;

                if (playSound) Game1.playSound("crit");
                return false;
            }
            return true;
        }

        public bool HandleRightClick(int x, int y, bool playSound)
        {
            // === RIGHT-CLICK EQUIPPED-RING -> PANEL TRANSFER ===
            // When the panel is open and the player right-clicks the vanilla Left Ring or
            // Right Ring equipment slot with an empty cursor, move that ring straight into
            // the first empty panel slot.  Left-click is left to vanilla so the standard
            // "pick up to cursor" behaviour is preserved.
            if (PanelOpen && Game1.player.CursorSlotItem == null)
            {
                ClickableComponent? leftRing  = _page.equipmentIcons.Find(c => c.name == "Left Ring");
                ClickableComponent? rightRing = _page.equipmentIcons.Find(c => c.name == "Right Ring");

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
                        ModEntry.DiagVerbose("[Test] Inventory: right-click transfer " + clickedRing.Name + " -> panel slot " + firstEmpty);
                        clickedRing.onUnequip(Game1.player);
                        if (clickedLeft)  Game1.player.leftRing.Value  = null;
                        else              Game1.player.rightRing.Value = null;

                        RingSlotManager.Equip(firstEmpty, clickedRing);

                        if (playSound) Game1.playSound("crit");
                        return false;
                    }
                }
            }

            if (!PanelOpen)
                return true;

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
                    ModEntry.DiagVerbose("[Test] Inventory: right-click swap " + heldRing.Name + " <-> " + ring.Name + " in slot " + idx);
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
        //  Scroll / toggle / hover
        // ============================================================

        /// <summary>Re-layout + refresh the host menu's clickable component list + keep the
        /// controller snap on a visible slot.  Shared by every scroll trigger.</summary>
        protected override void AfterScrollChange()
        {
            ApplyPanelVisibility();
            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();
                gm.populateClickableComponentList();
            }
            SnapToVisibleSlot();
        }

        public void TogglePanel(bool playSound)
        {
            _panelOpen = !_panelOpen;
            ModEntry.DiagVerbose("[Test] Inventory panel toggled: open=" + PanelOpen);
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(PanelOpen ? "bigSelect" : "bigDeSelect");

            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();

                if (Game1.options.SnappyMenus && ToggleButton != null)
                {
                    if (PanelOpen && Slots.Count > 0)
                        page.setCurrentlySnappedComponentTo(Slots[0].myID);
                    else
                        page.setCurrentlySnappedComponentTo(ToggleButton.myID);
                    page.snapCursorToCurrentSnappedComponent();
                }
            }
        }

        public void HandleHover(int x, int y)
        {
            var vp = Game1.uiViewport;
            if (vp.Width != _lastVpW || vp.Height != _lastVpH)
            {
                _lastVpW = vp.Width;
                _lastVpH = vp.Height;
                // Preserve the panel state the user is actually looking at (a window resize
                // shouldn't pop the panel open or closed).
                bool wasOpen = PanelOpen;
                _page.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                RebuildSlots();
                if (ToggleButton != null)
                    _page.equipmentIcons.Add(ToggleButton);
                foreach (var slot in Slots)
                    _page.equipmentIcons.Add(slot);
                if (_scrollUpBtn != null)
                    _page.equipmentIcons.Add(_scrollUpBtn);
                if (_scrollDownBtn != null)
                    _page.equipmentIcons.Add(_scrollDownBtn);
                var boots = _page.equipmentIcons.Find(c => c.name == "Boots");
                if (boots != null && ToggleButton != null)
                    boots.downNeighborID = ToggleButton.myID;
                _panelOpen = wasOpen;
                ApplyPanelVisibility();
                _page.populateClickableComponentList();
                (Game1.activeClickableMenu as GameMenu)?.populateClickableComponentList();
            }

            UpdateHoverAndScale(x, y);
        }
    }
}
