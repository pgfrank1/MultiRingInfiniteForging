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
                PanelFor(page).Build();
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

        public static void Ctor_Postfix(InventoryPage __instance) => PanelFor(__instance).Build();

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
    internal sealed class InventoryRingPanel
    {
        private const int SlotSize = 64;
        private const int SlotSpacing = 4;
        private const int FirstSlotId = 110_000;
        private const int ToggleButtonId = 109_999;
        private const int ScrollUpBtnId = 109_998;
        private const int ScrollDownBtnId = 109_997;
        private const int ScrollBtnWidth = 40;
        private const int ScrollBtnHeight = 44;
        private const int ScrollBarWidth = 6;
        private const int ScrollBarGap = 4;

        private readonly InventoryPage _page;

        private readonly List<ClickableComponent> _slots = new();
        private ClickableTextureComponent? _toggleButton;
        private ClickableComponent? _scrollUpBtn;
        private ClickableComponent? _scrollDownBtn;
        public bool PanelOpen { get; private set; }
        private int _scrollOffset;
        private int _maxScrollOffset;
        private int _visibleRows;
        private int _lastVpW = -1;
        private int _lastVpH = -1;
        private string _hoverText = "";

        public InventoryRingPanel(InventoryPage page) => _page = page;

        // ============================================================
        //  Build / inject into equipmentIcons
        // ============================================================

        public void Build()
        {
            PanelOpen = false;
            _scrollOffset = 0;
            RebuildSlots();

            if (_toggleButton != null)
                _page.equipmentIcons.Add(_toggleButton);
            foreach (var slot in _slots)
                _page.equipmentIcons.Add(slot);
            if (_scrollUpBtn != null)
                _page.equipmentIcons.Add(_scrollUpBtn);
            if (_scrollDownBtn != null)
                _page.equipmentIcons.Add(_scrollDownBtn);

            var boots = _page.equipmentIcons.Find(c => c.name == "Boots");
            if (boots != null && _toggleButton != null)
                boots.downNeighborID = _toggleButton.myID;

            ModEntry.DiagVerbose("[Test] Inventory panel initialized with " + _slots.Count + " slots");
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
            _slots.Clear();
            _toggleButton = null;
            _scrollUpBtn = null;
            _scrollDownBtn = null;
            RingSlotManager.EnsureSize();

            var toggleBounds = GetToggleBounds();
            if (toggleBounds == Rectangle.Empty) return;

            _toggleButton = new ClickableTextureComponent(
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
            _toggleButton.baseScale = 4f;

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
                _slots.Add(slot);
            }

            ApplyPanelVisibility();
        }

        private void ApplyPanelVisibility()
        {
            if (_toggleButton == null) return;
            const int maxPerRow = 6;

            int firstVisibleInv = _scrollOffset * maxPerRow;
            _toggleButton.rightNeighborID = PanelOpen && firstVisibleInv < _slots.Count
                ? _slots[firstVisibleInv].myID
                : -99998;

            int gridStartX = _toggleButton.bounds.X + SlotSize + SlotSpacing * 2;
            int panelTopY = _toggleButton.bounds.Y;

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

            for (int i = 0; i < _slots.Count; i++)
            {
                int row = i / maxPerRow;
                bool visible = PanelOpen && row >= _scrollOffset && row < _scrollOffset + maxVisibleRows;

                if (visible)
                {
                    int displayRow = row - _scrollOffset;
                    int col = i % maxPerRow;
                    _slots[i].bounds = new Rectangle(
                        gridStartX + col * (SlotSize + SlotSpacing),
                        panelTopY + displayRow * (SlotSize + SlotSpacing),
                        SlotSize, SlotSize);

                    if (firstVisibleSlot < 0) firstVisibleSlot = i;
                    lastVisibleSlot = i;

                    _slots[i].leftNeighborID = col == 0
                        ? ToggleButtonId
                        : FirstSlotId + i - 1;
                    _slots[i].rightNeighborID = (col == maxPerRow - 1 || i == RingSlotManager.SlotCount - 1)
                        ? -99998
                        : FirstSlotId + i + 1;

                    if (displayRow == 0)
                        _slots[i].upNeighborID = _scrollOffset > 0 ? ScrollUpBtnId : -99998;
                    else
                        _slots[i].upNeighborID = FirstSlotId + i - maxPerRow;

                    if (displayRow == maxVisibleRows - 1 || row == totalRows - 1)
                        _slots[i].downNeighborID = _scrollOffset < _maxScrollOffset ? ScrollDownBtnId : -99998;
                    else
                        _slots[i].downNeighborID = FirstSlotId + i + maxPerRow;
                }
                else
                {
                    _slots[i].bounds = new Rectangle(-9999, -9999, 0, 0);
                    _slots[i].leftNeighborID = -99998;
                    _slots[i].rightNeighborID = -99998;
                    _slots[i].upNeighborID = -99998;
                    _slots[i].downNeighborID = -99998;
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
                    if (panelIdx < _slots.Count && invIdx < _page.inventory.inventory.Count)
                    {
                        _slots[panelIdx].upNeighborID = _page.inventory.inventory[invIdx].myID;
                        _page.inventory.inventory[invIdx].downNeighborID = _slots[panelIdx].myID;
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
            if (_toggleButton != null)
            {
                b.Draw(Game1.menuTexture, _toggleButton.bounds,
                    Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                    Color.White);

                b.Draw(
                    _toggleButton.texture,
                    new Vector2(_toggleButton.bounds.X + 16, _toggleButton.bounds.Y + 16),
                    _toggleButton.sourceRect,
                    Color.White,
                    rotation: 0f,
                    origin: Vector2.Zero,
                    scale: 2f,
                    effects: SpriteEffects.None,
                    layerDepth: 0.86f);

                Utility.drawTextWithShadow(b,
                    PanelOpen ? "<" : ">",
                    Game1.smallFont,
                    new Vector2(_toggleButton.bounds.Right - 16, _toggleButton.bounds.Bottom - 24),
                    Game1.textColor);
            }

            if (PanelOpen && _slots.Count > 0)
            {
                Rectangle panelBg = GetPanelBackground();
                IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    panelBg.X, panelBg.Y, panelBg.Width, panelBg.Height,
                    Color.White, 1f, drawShadow: true);

                foreach (var slot in _slots)
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

                // Scroll bar.
                if (_scrollUpBtn != null && _scrollDownBtn != null && _maxScrollOffset > 0)
                {
                    int trackX = _scrollUpBtn.bounds.X + (ScrollBtnWidth - ScrollBarWidth) / 2;
                    int trackY = _scrollUpBtn.bounds.Bottom;
                    int trackH = _scrollDownBtn.bounds.Y - trackY;
                    b.Draw(Game1.staminaRect,
                        new Rectangle(trackX, trackY, ScrollBarWidth, trackH),
                        new Color(60, 60, 60, 180));

                    int totalRows = _maxScrollOffset + _visibleRows;
                    int thumbH = System.Math.Max(ScrollBarWidth * 2,
                        trackH * _visibleRows / totalRows);
                    int thumbY = trackY +
                        (trackH - thumbH) * _scrollOffset / _maxScrollOffset;
                    b.Draw(Game1.staminaRect,
                        new Rectangle(trackX, thumbY, ScrollBarWidth, thumbH),
                        Color.White * 0.9f);
                }

                // Scroll up arrow
                if (_scrollUpBtn != null && _scrollOffset > 0)
                {
                    Color arrowCol = _scrollUpBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : Color.White;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollUpBtn.bounds.X, _scrollUpBtn.bounds.Y),
                        new Rectangle(76, 72, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
                }

                // Scroll down arrow
                if (_scrollDownBtn != null && _scrollOffset < _maxScrollOffset)
                {
                    Color arrowCol = _scrollDownBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : Color.White;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollDownBtn.bounds.X, _scrollDownBtn.bounds.Y),
                        new Rectangle(12, 76, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
                }
            }

            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);

            // Redraw the cursor-held item ONLY if the mouse is currently over our panel, so
            // the held ring isn't hidden behind the panel/slot frames.  Outside the panel,
            // vanilla already drew the held item; drawing again would double it.
            if (Game1.player.CursorSlotItem != null && PanelOpen && _slots.Count > 0)
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
            foreach (var s in _slots)
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
            if (_toggleButton != null && _toggleButton.containsPoint(x, y))
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

            foreach (var slot in _slots)
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

            foreach (var slot in _slots)
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

        public bool HandleScroll(int direction)
        {
            if (!PanelOpen || _toggleButton == null || _maxScrollOffset <= 0) return true;

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

            AfterScrollChange();
            return false;
        }

        /// <summary>Re-layout + refresh the host menu's clickable component list + keep the
        /// controller snap on a visible slot.  Shared by every scroll trigger.</summary>
        private void AfterScrollChange()
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

        private void SnapToVisibleSlot()
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
                var firstVisible = _slots.FirstOrDefault(s => s.bounds.X > -5000);
                if (firstVisible != null)
                {
                    page.setCurrentlySnappedComponentTo(firstVisible.myID);
                    return;
                }
            }

            page.snapCursorToCurrentSnappedComponent();
        }

        public void TogglePanel(bool playSound)
        {
            PanelOpen = !PanelOpen;
            ModEntry.DiagVerbose("[Test] Inventory panel toggled: open=" + PanelOpen);
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(PanelOpen ? "bigSelect" : "bigDeSelect");

            if (Game1.activeClickableMenu is GameMenu gm
                && gm.pages[GameMenu.inventoryTab] is InventoryPage page)
            {
                page.populateClickableComponentList();

                if (Game1.options.SnappyMenus && _toggleButton != null)
                {
                    if (PanelOpen && _slots.Count > 0)
                        page.setCurrentlySnappedComponentTo(_slots[0].myID);
                    else
                        page.setCurrentlySnappedComponentTo(_toggleButton.myID);
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
                if (_toggleButton != null)
                    _page.equipmentIcons.Add(_toggleButton);
                foreach (var slot in _slots)
                    _page.equipmentIcons.Add(slot);
                if (_scrollUpBtn != null)
                    _page.equipmentIcons.Add(_scrollUpBtn);
                if (_scrollDownBtn != null)
                    _page.equipmentIcons.Add(_scrollDownBtn);
                var boots = _page.equipmentIcons.Find(c => c.name == "Boots");
                if (boots != null && _toggleButton != null)
                    boots.downNeighborID = _toggleButton.myID;
                PanelOpen = wasOpen;
                ApplyPanelVisibility();
                _page.populateClickableComponentList();
                (Game1.activeClickableMenu as GameMenu)?.populateClickableComponentList();
            }

            _hoverText = "";

            if (_toggleButton != null && _toggleButton.containsPoint(x, y))
            {
                _hoverText = PanelOpen
                    ? ModEntry.T("panel.hover.hide")
                    : ModEntry.T("panel.hover.show");
                return;
            }

            if (!PanelOpen) return;

            foreach (var slot in _slots)
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

        private static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;
    }
}
