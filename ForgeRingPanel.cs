using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using static MultiRingInfiniteForging.ForgeMenuPatches;

namespace MultiRingInfiniteForging
{
    /// <summary>The extra-ring slot panel for a single <see cref="ForgeMenu"/>: a collapsible
    /// grid of extra ring slots plus the forge's "smart" ring/ingredient handling.  One
    /// instance per ForgeMenu (kept in <see cref="ForgeMenuPatches"/>'s table), so split-screen
    /// players each have their own.  The craft-rules helpers it leans on stay static on
    /// <see cref="ForgeMenuPatches"/> and are pulled in via <c>using static</c>.</summary>
    internal sealed class ForgeRingPanel
    {
        private const int SlotSize = 64;
        private const int SlotSpacing = 4;
        private const int FirstSlotId = 120_000;
        private const int ToggleButtonId = 119_999;
        private const int ScrollUpBtnId = 119_998;
        private const int ScrollDownBtnId = 119_997;
        private const int ScrollBtnWidth = 40;
        private const int ScrollBtnHeight = 44;
        private const int ScrollBarWidth = 6;
        private const int ScrollBarGap = 4;

        // Forge spritesheet (cached on first access).  Vanilla loads this into
        // ForgeMenu.forgeTextures; we mirror its content path so our visuals match.
        private static Texture2D? _forgeTextures;
        private static Texture2D ForgeTextures =>
            _forgeTextures ??= Game1.content.Load<Texture2D>("LooseSprites\\ForgeMenu");

        /// <summary>Calibrated tint that renders to the forge's #BC635B panel BG.</summary>
        private static readonly Color ForgePanelTint = new Color(0xBC, 0x81, 0xC5);
        /// <summary>Color sampled from the vanilla forge Ring1/Ring2 slot border (#6D0A03).</summary>
        private static readonly Color ForgeSlotTint = new Color(0x6D, 0x0A, 0x03);
        /// <summary>Solid fill behind transparent slot frames so the cell reads as recessed.</summary>
        private static readonly Color ForgeSlotFill = new Color(0x9B, 0x40, 0x35);
        private static readonly Rectangle ForgeSlotFrameSource = new Rectangle(140, 250, 28, 28);

        private readonly ForgeMenu _menu;

        private readonly List<ClickableComponent> Slots = new();
        private ClickableTextureComponent? ToggleButton;
        private ClickableComponent? _scrollUpBtn;
        private ClickableComponent? _scrollDownBtn;
        private bool _panelOpen;
        public bool PanelOpen => _panelOpen;
        private int _scrollOffset;
        private int _maxScrollOffset;
        private int _visibleRows;
        private int _lastVpW = -1;
        private int _lastVpH = -1;
        private string _hoverText = "";

        // Forge-completion trackers used by Update diagnostics.
        private string? _lastHeldName;
        private string? _lastUpdateLeftName;
        private string? _lastUpdateRightName;
        private List<string>? _preForgeCombinedRingNames;
        private bool _lastLeftWasRing;

        // Draw-path dim memo: IsRingAllowedInForgeContext verdict per panel ring, plus the
        // pair-level duplicate check DrawCombinedRingGlow needs.  Keyed by the forge ingredient
        // pair (reference identity); revalidated on every lookup, dropped on config change.
        private readonly Dictionary<Ring, bool> DimAllowed = new();
        private Item? DimLeft;
        private Item? DimRight;
        private bool PairDuplicate;

        public ForgeRingPanel(ForgeMenu menu) => _menu = menu;

        // ============================================================
        //  Build / inject into equipmentIcons
        // ============================================================

        public void Build()
        {
            _panelOpen = false;
            _scrollOffset = 0;
            RebuildSlots();

            if (ToggleButton != null)
                _menu.equipmentIcons.Add(ToggleButton);
            foreach (var slot in Slots)
                _menu.equipmentIcons.Add(slot);
            if (_scrollUpBtn != null)
                _menu.equipmentIcons.Add(_scrollUpBtn);
            if (_scrollDownBtn != null)
                _menu.equipmentIcons.Add(_scrollDownBtn);

            var rightRing = _menu.equipmentIcons.Find(c => c.name == "Ring2");
            if (rightRing != null && ToggleButton != null)
                rightRing.downNeighborID = ToggleButton.myID;

            _menu.populateClickableComponentList();
            ModEntry.DiagVerbose("[Test] Forge panel initialized with " + Slots.Count + " slots");
        }

        // ============================================================
        //  Layout
        // ============================================================

        private Rectangle GetToggleBounds()
        {
            ClickableComponent? anchor = null;
            foreach (var c in _menu.equipmentIcons)
            {
                if (anchor == null || c.bounds.Y > anchor.bounds.Y)
                    anchor = c;
            }
            if (anchor == null) return Rectangle.Empty;

            return new Rectangle(
                anchor.bounds.X,
                anchor.bounds.Y + SlotSize,
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
                upNeighborID = (_menu.equipmentIcons.Find(c => c.name == "Ring2")
                                ?? _menu.equipmentIcons.Find(c => c.name == "Ring1"))?.myID
                               ?? -99998,
                downNeighborID = -99998,
                leftNeighborID = -99998,
                rightNeighborID = -99998
            };
            ToggleButton.baseScale = 4f;

            _scrollUpBtn = new ClickableComponent(
                new Rectangle(-9999, -9999, ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollUp")
            {
                myID = ScrollUpBtnId,
                upNeighborID = -99998, downNeighborID = -99998,
                leftNeighborID = -99998, rightNeighborID = -99998,
                fullyImmutable = true
            };
            _scrollDownBtn = new ClickableComponent(
                new Rectangle(-9999, -9999, ScrollBtnWidth, ScrollBtnHeight),
                name: "ExtraRingScrollDown")
            {
                myID = ScrollDownBtnId,
                upNeighborID = -99998, downNeighborID = -99998,
                leftNeighborID = -99998, rightNeighborID = -99998,
                fullyImmutable = true
            };

            int rightEdge = toggleBounds.X - SlotSpacing * 4;
            int leftMargin = 8;
            int maxPerRow = System.Math.Clamp(
                (rightEdge - leftMargin) / (SlotSize + SlotSpacing), 1, 4);

            ClickableComponent? leftRing = _menu.equipmentIcons.Find(c => c.name == "Ring1");
            int panelTopY = leftRing?.bounds.Y ?? toggleBounds.Y;
            int gridEndX = rightEdge;
            int gridStartX = gridEndX - maxPerRow * (SlotSize + SlotSpacing);
            int gridStartY = panelTopY;

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

            int rightEdge = ToggleButton.bounds.X - SlotSpacing * 4;
            int leftMargin = 8;
            int maxPerRow = System.Math.Clamp(
                (rightEdge - leftMargin) / (SlotSize + SlotSpacing), 1, 4);

            int firstVisibleForge = _scrollOffset * maxPerRow;
            ToggleButton.leftNeighborID = _panelOpen && firstVisibleForge < Slots.Count
                ? Slots[firstVisibleForge].myID
                : -99998;
            ToggleButton.rightNeighborID = -99998;

            ForgeMenu menu = _menu;

            int gridEndX = ToggleButton.bounds.X - SlotSpacing * 4;
            int gridStartX = gridEndX - maxPerRow * (SlotSize + SlotSpacing);
            int panelTopY = (menu.equipmentIcons.Find(c => c.name == "Ring1") is { } lr)
                ? lr.bounds.Y
                : ToggleButton.bounds.Y;

            int totalRows = (Slots.Count + maxPerRow - 1) / maxPerRow;

            if (_panelOpen)
            {
                int bottomBoundary = Game1.uiViewport.Height - 8;
                int availableHeight = bottomBoundary - panelTopY;

                if (availableHeight < SlotSize + SlotSpacing)
                {
                    panelTopY = bottomBoundary - (SlotSize + SlotSpacing);
                    if (panelTopY < 0) panelTopY = 0;
                    availableHeight = bottomBoundary - panelTopY;
                }

                int visibleRows = System.Math.Max(1, availableHeight / (SlotSize + SlotSpacing));
                _visibleRows = visibleRows;
                _maxScrollOffset = System.Math.Max(0, totalRows - visibleRows);
                _scrollOffset = System.Math.Clamp(_scrollOffset, 0, _maxScrollOffset);

                // Position scroll buttons at top and bottom of visible area.
                int scrollBtnX = gridStartX - ScrollBarGap - ScrollBtnWidth;
                if (_scrollUpBtn != null)
                    _scrollUpBtn.bounds = new Rectangle(scrollBtnX, panelTopY, ScrollBtnWidth, ScrollBtnHeight);
                if (_scrollDownBtn != null)
                    _scrollDownBtn.bounds = new Rectangle(scrollBtnX,
                        System.Math.Min(panelTopY + visibleRows * (SlotSize + SlotSpacing),
                            Game1.uiViewport.Height - ScrollBtnHeight - 8),
                        ScrollBtnWidth, ScrollBtnHeight);

                for (int i = 0; i < Slots.Count; i++)
                {
                    int row = i / maxPerRow;
                    int col = i % maxPerRow;

                    if (row >= _scrollOffset && row < _scrollOffset + visibleRows)
                    {
                        int displayRow = row - _scrollOffset;
                        Slots[i].bounds = new Rectangle(
                            gridStartX + col * (SlotSize + SlotSpacing),
                            panelTopY + displayRow * (SlotSize + SlotSpacing),
                            SlotSize, SlotSize);

                        bool isFirstDisplayRow = displayRow == 0;
                        bool isLastDisplayRow = displayRow == visibleRows - 1 || row == totalRows - 1;
                        Slots[i].leftNeighborID = col == 0 ? -99998 : FirstSlotId + i - 1;
                        Slots[i].rightNeighborID = col == maxPerRow - 1 || i == Slots.Count - 1
                            ? ToggleButtonId
                            : FirstSlotId + i + 1;
                        Slots[i].upNeighborID = isFirstDisplayRow
                            ? (_scrollOffset > 0 ? ScrollUpBtnId : -99998)
                            : FirstSlotId + i - maxPerRow;
                        Slots[i].downNeighborID = isLastDisplayRow
                            ? (_scrollOffset < _maxScrollOffset ? ScrollDownBtnId : -99998)
                            : FirstSlotId + i + maxPerRow;
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

                // Wire scroll button neighbors.
                int firstVisible = _scrollOffset * maxPerRow;
                int lastVisible = System.Math.Min(
                    (_scrollOffset + visibleRows - 1) * maxPerRow + maxPerRow - 1, Slots.Count - 1);
                if (_scrollUpBtn != null)
                {
                    _scrollUpBtn.downNeighborID = firstVisible < Slots.Count ? Slots[firstVisible].myID : -99998;
                    _scrollUpBtn.upNeighborID = -99998;
                }
                if (_scrollDownBtn != null)
                {
                    _scrollDownBtn.upNeighborID = lastVisible >= 0 ? Slots[lastVisible].myID : -99998;
                    _scrollDownBtn.downNeighborID = -99998;
                }

                // Wire inventory's leftmost column <-> panel's rightmost visible column.
                int invCols = menu.inventory.capacity / menu.inventory.rows;
                if (invCols <= 0) invCols = 12;

                for (int r = 0; r < menu.inventory.rows; r++)
                {
                    int invIdx = r * invCols;
                    if (invIdx >= menu.inventory.inventory.Count) break;

                    int panelRow = r + _scrollOffset;
                    if (panelRow >= _scrollOffset + visibleRows) break;
                    if (panelRow >= totalRows) break;
                    int panelLastCol = maxPerRow - 1;
                    int panelIdx = panelRow * maxPerRow + panelLastCol;
                    if (panelIdx >= Slots.Count) panelIdx = Slots.Count - 1;
                    menu.inventory.inventory[invIdx].leftNeighborID = Slots[panelIdx].myID;
                    Slots[panelIdx].rightNeighborID = menu.inventory.inventory[invIdx].myID;
                }
            }
            else
            {
                _scrollOffset = 0;
                for (int i = 0; i < Slots.Count; i++)
                {
                    Slots[i].bounds = new Rectangle(-9999, -9999, 0, 0);
                    Slots[i].leftNeighborID = -99998;
                    Slots[i].rightNeighborID = -99998;
                    Slots[i].upNeighborID = -99998;
                    Slots[i].downNeighborID = -99998;
                }
                if (_scrollUpBtn != null)
                    _scrollUpBtn.bounds = new Rectangle(-9999, -9999, 0, 0);
                if (_scrollDownBtn != null)
                    _scrollDownBtn.bounds = new Rectangle(-9999, -9999, 0, 0);

                int invCols = menu.inventory.capacity / menu.inventory.rows;
                if (invCols <= 0) invCols = 12;
                for (int r = 0; r < menu.inventory.rows; r++)
                {
                    int invIdx = r * invCols;
                    if (invIdx >= menu.inventory.inventory.Count) break;
                    menu.inventory.inventory[invIdx].leftNeighborID = -99998;
                }
            }
        }

        // ============================================================
        //  Draw
        // ============================================================

        public void Draw(SpriteBatch b)
        {
            if (ToggleButton == null) return;

            // 1) Toggle button: solid fill underneath, then the slot frame on top.
            b.Draw(Game1.staminaRect, ToggleButton.bounds, ForgeSlotFill);
            b.Draw(Game1.menuTexture, ToggleButton.bounds,
                Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                ForgeSlotTint);

            // 2) Toggle button icon (ring book).
            b.Draw(
                ToggleButton.texture,
                new Vector2(ToggleButton.bounds.X + 16, ToggleButton.bounds.Y + 16),
                ToggleButton.sourceRect,
                Color.White,
                rotation: 0f,
                origin: Vector2.Zero,
                scale: 2f,
                effects: SpriteEffects.None,
                layerDepth: 0.87f);

            // 3) Open/closed arrow indicator.
            Utility.drawTextWithShadow(b,
                _panelOpen ? ">" : "<",
                Game1.smallFont,
                new Vector2(ToggleButton.bounds.Right - 16, ToggleButton.bounds.Bottom - 24),
                Game1.textColor);

            // 4) Expanded slot panel.
            if (_panelOpen && Slots.Count > 0)
            {
                Rectangle slotsBg = GetSlotsBounds();

                IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    slotsBg.X - 16, slotsBg.Y - 16,
                    slotsBg.Width + 32, slotsBg.Height + 32,
                    ForgePanelTint, 1f, drawShadow: true);

                foreach (var slot in Slots)
                {
                    int idx = SlotIndex(slot);
                    var ring = idx >= 0 && idx < RingSlotManager.Slots.Count
                        ? RingSlotManager.Slots[idx]
                        : null;

                    b.Draw(Game1.staminaRect, slot.bounds, ForgeSlotFill);
                    b.Draw(Game1.menuTexture, slot.bounds,
                        Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                        ForgeSlotTint);

                    if (ring != null)
                    {
                        // Dim rings the forge context refuses.  Verdicts are memoized per
                        // ingredient pair; this runs for every panel slot every frame.
                        bool blocked = !IsRingAllowedCached(ring);

                        if (blocked)
                        {
                            ring.drawInMenu(b, new Vector2(slot.bounds.X, slot.bounds.Y),
                                scaleSize: slot.scale,
                                transparency: 0.35f,
                                layerDepth: 0.87f,
                                drawStackNumber: StackDrawType.Hide,
                                color: Color.Gray,
                                drawShadow: true);
                        }
                        else
                        {
                            ring.drawInMenu(b, new Vector2(slot.bounds.X, slot.bounds.Y), slot.scale);
                        }
                    }
                }

                // Scroll bar.
                if (_scrollUpBtn != null && _scrollDownBtn != null && _maxScrollOffset > 0)
                {
                    int trackX = _scrollUpBtn.bounds.X + (ScrollBtnWidth - ScrollBarWidth) / 2;
                    int trackY = _scrollUpBtn.bounds.Bottom;
                    int trackH = _scrollDownBtn.bounds.Y - trackY;
                    b.Draw(Game1.staminaRect,
                        new Rectangle(trackX, trackY, ScrollBarWidth, trackH),
                        new Color(0x40, 0x18, 0x10) * 0.7f);

                    int totalRows = _maxScrollOffset + _visibleRows;
                    int thumbH = System.Math.Max(ScrollBarWidth * 2,
                        trackH * _visibleRows / totalRows);
                    int thumbY = trackY +
                        (trackH - thumbH) * _scrollOffset / _maxScrollOffset;
                    b.Draw(Game1.staminaRect,
                        new Rectangle(trackX, thumbY, ScrollBarWidth, thumbH),
                        ForgeSlotFill);
                }

                // Scroll indicators.
                if (_scrollOffset > 0 && _scrollUpBtn != null)
                {
                    Color arrowCol = _scrollUpBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : ForgeSlotFill;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollUpBtn.bounds.X, _scrollUpBtn.bounds.Y),
                        new Rectangle(76, 72, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
                }
                if (_scrollOffset < _maxScrollOffset && _scrollDownBtn != null)
                {
                    Color arrowCol = _scrollDownBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                        ? Color.Gold : ForgeSlotFill;
                    Utility.drawWithShadow(b, Game1.mouseCursors,
                        new Vector2(_scrollDownBtn.bounds.X, _scrollDownBtn.bounds.Y),
                        new Rectangle(12, 76, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
                }
            }

            DrawCombinedRingGlow(b);

            // 5) Hover tooltip.
            if (!string.IsNullOrEmpty(_hoverText))
                IClickableMenu.drawHoverText(b, _hoverText, Game1.smallFont);

            // 6) Cursor-held item drawn above panel.
            if (Game1.player.CursorSlotItem != null)
            {
                Game1.player.CursorSlotItem.drawInMenu(b,
                    new Vector2(Game1.getOldMouseX() + 8, Game1.getOldMouseY() + 8),
                    1f);
            }

            // 7) Redraw the mouse cursor on top.
            _menu.drawMouse(b);
        }

        private Rectangle GetSlotsBounds()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var s in Slots)
            {
                if (s.bounds.X <= -5000) continue;
                if (s.bounds.X < minX) minX = s.bounds.X;
                if (s.bounds.Y < minY) minY = s.bounds.Y;
                if (s.bounds.Right > maxX) maxX = s.bounds.Right;
                if (s.bounds.Bottom > maxY) maxY = s.bounds.Bottom;
            }
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>Vanilla's ForgeMenu.draw excludes CombinedRing from the "this could go in a
        /// slot" highlight (vanilla can't combine already-combined rings).  With
        /// InfiniteCombining we DO support that, so draw the missing glow ourselves.</summary>
        private void DrawCombinedRingGlow(SpriteBatch b)
        {
            if (!ModEntry.Instance.Config.InfiniteCombining) return;

            ForgeMenu menu = _menu;
            Item? highlightItem = Game1.player.CursorSlotItem
                                  ?? GetHeldItem(menu)
                                  ?? menu.hoveredItem;

            var leftRing  = menu.leftIngredientSpot.item as Ring;
            var rightRing = menu.rightIngredientSpot.item as Ring;

            RevalidateDimMemo();
            if (highlightItem is CombinedRing
                && menu.leftIngredientSpot.item is null or Ring
                && menu.rightIngredientSpot.item is null or Ring
                && !menu.IsBusy()
                && ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                && leftRing != null && rightRing != null
                && PairDuplicate)
            {
                if (menu.leftIngredientSpot.item == null)
                    menu.leftIngredientSpot.draw(b, Color.White, 0.87f);
                if (menu.rightIngredientSpot.item == null)
                    menu.rightIngredientSpot.draw(b, Color.White, 0.87f);

                if (menu.hoveredItem != null)
                {
                    IClickableMenu.drawToolTip(b,
                        menu.hoveredItem.getDescription(),
                        menu.hoveredItem.DisplayName,
                        menu.hoveredItem);
                }
            }
        }

        // ============================================================
        //  Click handling
        // ============================================================

        public bool HandleLeftClick(int x, int y, bool playSound)
        {
            ForgeMenu __instance = _menu;
            Item? heldItem = GetHeldItem(__instance);
            Item? invItemAtClick = __instance.inventory.getItemAt(x, y);

            // Toggle button click - handle FIRST, always permitted.
            if (ToggleButton != null && ToggleButton.containsPoint(x, y))
            {
                TogglePanel(playSound);
                return false;
            }

            // Scroll buttons.
            if (_panelOpen)
            {
                if (_scrollUpBtn != null && _scrollUpBtn.containsPoint(x, y) && _scrollOffset > 0)
                {
                    _scrollOffset--;
                    ApplyPanelVisibility();
                    __instance.populateClickableComponentList();
                    SnapToVisibleSlot();
                    return false;
                }
                if (_scrollDownBtn != null && _scrollDownBtn.containsPoint(x, y) && _scrollOffset < _maxScrollOffset)
                {
                    _scrollOffset++;
                    ApplyPanelVisibility();
                    __instance.populateClickableComponentList();
                    SnapToVisibleSlot();
                    return false;
                }
            }

            // === UNIFY THE TWO CARRY MECHANISMS ===
            // If the forge's private _heldItem has something and CursorSlotItem is empty,
            // promote the held item to CursorSlotItem.  All our drop logic uses
            // CursorSlotItem; the forge ingredient slot drops are handled by our own code
            // (below), not vanilla's, so promotion is always safe.
            if (Game1.player.CursorSlotItem == null && heldItem != null)
            {
                ModEntry.DiagVerbose($"[Forge] Promoting forge.held ({heldItem.Name}) to CursorSlotItem");
                Game1.player.CursorSlotItem = heldItem;
                SetHeldItem(__instance, null);
                heldItem = null;
            }

            ModEntry.DiagVerbose($"[Forge] *Prefix* click at ({x},{y})  cursor={Game1.player.CursorSlotItem?.Name ?? "null"}  forge.held={heldItem?.Name ?? "null"}  inv@click={invItemAtClick?.Name ?? "null"}  panelOpen={_panelOpen}");

            var hlMethod = __instance.inventory.highlightMethod;
            ModEntry.DiagVerbose($"[Forge]   inventory.highlightMethod = {(hlMethod == null ? "null" : hlMethod.Method.Name)}");
            ModEntry.DiagVerbose($"[Forge]   forge.leftIngredient={__instance.leftIngredientSpot.item?.Name ?? "null"}  forge.rightIngredient={__instance.rightIngredientSpot.item?.Name ?? "null"}");

            if (ModEntry.Instance.Config.VerboseLogging)
            {
                for (System.Type? t = __instance.GetType(); t != null; t = t.BaseType)
                {
                    foreach (var field in t.GetFields(
                                 System.Reflection.BindingFlags.Instance
                                 | System.Reflection.BindingFlags.Public
                                 | System.Reflection.BindingFlags.NonPublic
                                 | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        try
                        {
                            var val = field.GetValue(__instance);
                            if (val is Item item)
                                ModEntry.DiagVerbose($"[Forge]   field {t.Name}.{field.Name} = Item({item.Name})");
                        }
                        catch { /* ignore */ }
                    }
                }
            }

            // === CURSOR-ANY: inventory swap path ===
            Item? cursorAny = Game1.player.CursorSlotItem;
            if (cursorAny != null)
            {
                int invSwapIdx = __instance.inventory.getInventoryPositionOfClick(x, y);
                ModEntry.DiagVerbose($"[Forge] Cursor-any swap path: cursor={cursorAny.Name}, invSwapIdx={invSwapIdx}");
                if (invSwapIdx >= 0 && invSwapIdx < __instance.inventory.actualInventory.Count)
                {
                    Item? existing = __instance.inventory.actualInventory[invSwapIdx];

                    bool swapOk = existing == null || IsValidForgeItem(__instance, existing);
                    if (!swapOk)
                    {
                        ModEntry.DiagVerbose($"[Forge] Cursor swap refused: inventory slot {invSwapIdx} holds non-forge item ({existing!.Name})");
                        return false;
                    }

                    if (existing != null
                        && __instance.leftIngredientSpot.item is Tool leftToolForSwap
                        && !CanRightItemEnchantTool(leftToolForSwap, existing))
                    {
                        ModEntry.DiagVerbose($"[Forge] Cursor swap refused: would lift no-op-ingredient ({existing.Name}) against {leftToolForSwap.Name}");
                        return false;
                    }

                    if (existing != null)
                    {
                        bool ringInSlot =
                            __instance.leftIngredientSpot.item  is Ring ||
                            __instance.rightIngredientSpot.item is Ring;
                        bool nonRingInSlot =
                            (__instance.leftIngredientSpot.item  != null && __instance.leftIngredientSpot.item  is not Ring) ||
                            (__instance.rightIngredientSpot.item != null && __instance.rightIngredientSpot.item is not Ring);

                        if (ringInSlot && existing is not Ring)
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: non-ring " + existing.Name + " -> ring in forge slot");
                            return false;
                        }
                        if (nonRingInSlot && existing is Ring)
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: ring " + existing.Name + " -> non-ring in forge slot");
                            return false;
                        }

                        if (existing is Ring existingCtxRing && !IsRingAllowedInForgeContext(existingCtxRing, __instance))
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: " + existingCtxRing.Name + " dimmed");
                            return false;
                        }

                        if (existing is Ring existingSwapRing && IsRingBlockedByDuplicateCap(existingSwapRing, __instance))
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: " + existingSwapRing.Name + " duplicate cap");
                            return false;
                        }
                    }

                    __instance.inventory.actualInventory[invSwapIdx] = cursorAny;
                    Game1.player.CursorSlotItem = existing;
                    if (playSound) Game1.playSound("coin");
                    ModEntry.DiagVerbose($"[Forge] Cursor swap with inventory slot {invSwapIdx}: cursor was {cursorAny.Name}, now {existing?.Name ?? "null"}");
                    return false;
                }
            }

            for (int dbgI = 0; dbgI < __instance.inventory.actualInventory.Count; dbgI++)
            {
                var item = __instance.inventory.actualInventory[dbgI];
                if (item != null)
                    ModEntry.DiagVerbose($"[Forge]   inv[{dbgI}] = {item.Name}");
            }

            // === BLOCK ACCUMULATING A SECOND ITEM ON EITHER CARRY ===
            Item? cursorItem = Game1.player.CursorSlotItem;

            bool nonRingInForgeSlot =
                (__instance.leftIngredientSpot.item  != null && __instance.leftIngredientSpot.item  is not Ring) ||
                (__instance.rightIngredientSpot.item != null && __instance.rightIngredientSpot.item is not Ring);

            bool ringInForgeSlot =
                __instance.leftIngredientSpot.item  is Ring ||
                __instance.rightIngredientSpot.item is Ring;

            bool wouldGrabRing = false;
            bool wouldGrabNonRing = false;

            if (__instance.leftIngredientSpot.containsPoint(x, y))
            {
                if (__instance.leftIngredientSpot.item is Ring) wouldGrabRing = true;
                else if (__instance.leftIngredientSpot.item != null) wouldGrabNonRing = true;
            }
            if (__instance.rightIngredientSpot.containsPoint(x, y))
            {
                if (__instance.rightIngredientSpot.item is Ring) wouldGrabRing = true;
                else if (__instance.rightIngredientSpot.item != null) wouldGrabNonRing = true;
            }

            var gateLeftRingIcon  = __instance.equipmentIcons.Find(c => c.name == "Ring1");
            var gateRightRingIcon = __instance.equipmentIcons.Find(c => c.name == "Ring2");
            if ((gateLeftRingIcon  != null && gateLeftRingIcon.containsPoint(x, y)  && Game1.player.leftRing.Value  is Ring) ||
                (gateRightRingIcon != null && gateRightRingIcon.containsPoint(x, y) && Game1.player.rightRing.Value is Ring))
            {
                wouldGrabRing = true;
            }

            if (_panelOpen)
            {
                foreach (var slot in Slots)
                {
                    if (!slot.containsPoint(x, y)) continue;
                    int idx = SlotIndex(slot);
                    if (idx >= 0 && idx < RingSlotManager.Slots.Count
                        && RingSlotManager.Slots[idx] != null)
                    {
                        wouldGrabRing = true;
                    }
                    break;
                }
            }

            bool carryingRing    = cursorItem is Ring || heldItem is Ring;
            bool carryingNonRing = (cursorItem != null && cursorItem is not Ring)
                                || (heldItem   != null && heldItem   is not Ring);

            if (carryingNonRing && wouldGrabRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: carrying non-ring, refusing ring pick-up");
                return false;
            }
            if (carryingRing && wouldGrabNonRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: carrying ring, refusing non-ring pick-up");
                return false;
            }

            if (nonRingInForgeSlot && wouldGrabRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: non-ring in forge slot, rings greyed out");
                return false;
            }

            if (ringInForgeSlot && wouldGrabNonRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: ring in forge slot, non-rings greyed out");
                return false;
            }

            // === CURSOR-HELD ITEM: forge ingredient slot drops ===
            Item? carriedAny = Game1.player.CursorSlotItem;
            ModEntry.DiagVerbose($"[Forge]   reached carriedAny block; carriedAny={carriedAny?.Name ?? "null"}  leftContains={__instance.leftIngredientSpot.containsPoint(x,y)}  rightContains={__instance.rightIngredientSpot.containsPoint(x,y)}");
            if (carriedAny != null)
            {
                // Forge LEFT ingredient slot.
                if (__instance.leftIngredientSpot.containsPoint(x, y))
                {
                    Item? leftExisting = __instance.leftIngredientSpot.item;

                    bool acceptsInLeft = IsAcceptedInLeftSlot(__instance, carriedAny);

                    if (!acceptsInLeft)
                    {
                        ModEntry.DiagVerbose("[Test] Forge drop blocked: " + carriedAny.Name + " not accepted in left slot");
                        return false;
                    }

                    if (leftExisting == null)
                    {
                        if (carriedAny is Ring r)
                        {
                            Item? rightItem = __instance.rightIngredientSpot.item;
                            if (rightItem != null && !IsValidCraft(__instance, r, rightItem))
                            {
                                ModEntry.DiagVerbose("[Test] Forge drop blocked: ring " + r.Name + " invalid with right=" + rightItem.Name);
                                return false;
                            }
                            if (rightItem != null
                                && (rightItem.QualifiedItemId == "(O)74"
                                    || rightItem.QualifiedItemId == "(O)852"))
                            {
                                ModEntry.DiagVerbose("[Test] Forge drop blocked: ring + " + rightItem.Name + " not a valid craft");
                                return false;
                            }
                        }
                        __instance.leftIngredientSpot.item = carriedAny;
                        Game1.player.CursorSlotItem = null;
                        NotifyForgeSlotsChanged(__instance);
                        ModEntry.DiagVerbose("[Test] Forge drop accepted: " + carriedAny.Name + " -> left slot");
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }

                    __instance.leftIngredientSpot.item = carriedAny;
                    Game1.player.CursorSlotItem = leftExisting;
                    NotifyForgeSlotsChanged(__instance);
                    ModEntry.DiagVerbose("[Test] Forge swap: " + carriedAny.Name + " <-> " + leftExisting.Name + " in left slot");
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }

                // Forge RIGHT ingredient slot.
                if (__instance.rightIngredientSpot.containsPoint(x, y))
                {
                    Item? rightExisting = __instance.rightIngredientSpot.item;
                    Item? leftItem = __instance.leftIngredientSpot.item;

                    if (leftItem is Ring
                        && (carriedAny.QualifiedItemId == "(O)74"
                            || carriedAny.QualifiedItemId == "(O)852"))
                    {
                        ModEntry.DiagVerbose("[Test] Forge drop blocked: " + carriedAny.Name + " doesn't combine with ring");
                        return false;
                    }

                    bool acceptsInRight;
                    if (leftItem == null)
                    {
                        acceptsInRight = IsValidForgeItem(__instance, carriedAny);
                    }
                    else
                    {
                        acceptsInRight = IsValidCraft(__instance, leftItem, carriedAny);

                        if (acceptsInRight
                            && leftItem is Tool leftTool
                            && !CanRightItemEnchantTool(leftTool, carriedAny))
                        {
                            ModEntry.DiagVerbose("[Test] Forge drop blocked: " + carriedAny.Name + " no-op craft with " + leftTool.Name);
                            return false;
                        }
                    }

                    if (!acceptsInRight)
                    {
                        ModEntry.DiagVerbose("[Test] Forge drop blocked: " + carriedAny.Name + " not accepted in right slot (left=" + (leftItem?.Name ?? "null") + ")");
                        return false;
                    }

                    if (rightExisting == null)
                    {
                        __instance.rightIngredientSpot.item = carriedAny;
                        Game1.player.CursorSlotItem = null;
                        NotifyForgeSlotsChanged(__instance);
                        ModEntry.DiagVerbose("[Test] Forge drop accepted: " + carriedAny.Name + " -> right slot");
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }

                    __instance.rightIngredientSpot.item = carriedAny;
                    Game1.player.CursorSlotItem = rightExisting;
                    NotifyForgeSlotsChanged(__instance);
                    ModEntry.DiagVerbose("[Test] Forge swap: " + carriedAny.Name + " <-> " + rightExisting.Name + " in right slot");
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }
            }

            if (Game1.player.CursorSlotItem is Ring)
                return HandleCursorRingDrop(x, y, playSound);

            if (Game1.player.CursorSlotItem == null)
                return HandleEmptyCursorPickup(x, y, playSound);

            // Let vanilla handle anything else.
            return true;
        }

        public bool HandleRightClick(int x, int y, bool playSound)
        {
            ForgeMenu __instance = _menu;

            // While our cursor carry is occupied, block vanilla's right-click path entirely.
            if (Game1.player.CursorSlotItem != null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click blocked: cursor carry occupied");
                return false;
            }

            // Right-click an equipped vanilla ring (Ring1/Ring2) -> first free, valid forge slot.
            var leftRingIcon  = __instance.equipmentIcons.Find(c => c.name == "Ring1");
            var rightRingIcon = __instance.equipmentIcons.Find(c => c.name == "Ring2");
            bool clickedLeft  = leftRingIcon  != null && leftRingIcon.containsPoint(x, y);
            bool clickedRight = rightRingIcon != null && rightRingIcon.containsPoint(x, y);
            if (clickedLeft || clickedRight)
            {
                Ring? equipped = clickedLeft ? Game1.player.leftRing.Value : Game1.player.rightRing.Value;
                if (equipped == null)
                    return false;

                if (!IsRingAllowedInForgeContext(equipped, __instance)
                    || IsRingBlockedByDuplicateCap(equipped, __instance))
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click send blocked: " + equipped.Name + " dimmed");
                    return false;
                }

                var equippedDest = GetFreeForgeSlotFor(__instance, equipped);
                if (equippedDest == null)
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click send: no free/valid forge slot for " + equipped.Name);
                    return false;
                }

                ModEntry.DiagVerbose(
                    "[Test] Forge right-click send " + equipped.Name + " from " + (clickedLeft ? "Ring1" : "Ring2")
                    + " -> forge " + (ReferenceEquals(equippedDest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
                equipped.onUnequip(Game1.player);
                if (clickedLeft) Game1.player.leftRing.Value  = null;
                else             Game1.player.rightRing.Value = null;
                Game1.player.buffs.Dirty = true;
                equippedDest.item = equipped;
                NotifyForgeSlotsChanged(__instance);
                if (playSound) Game1.playSound("stoneStep");
                return false;
            }

            if (_panelOpen)
            {
                foreach (var slot in Slots)
                {
                    if (!slot.containsPoint(x, y)) continue;
                    int idx = SlotIndex(slot);
                    if (idx < 0 || idx >= RingSlotManager.Slots.Count) return true;

                    Ring? ring = RingSlotManager.Slots[idx];
                    if (ring == null) return false;

                    if (!IsRingAllowedInForgeContext(ring, __instance)
                        || IsRingBlockedByDuplicateCap(ring, __instance))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send blocked: " + ring.Name + " dimmed");
                        return false;
                    }

                    var dest = GetFreeForgeSlotFor(__instance, ring);
                    if (dest == null)
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send: no free/valid forge slot for " + ring.Name);
                        return false;
                    }

                    ModEntry.DiagVerbose(
                        "[Test] Forge right-click send " + ring.Name + " from panel slot " + idx
                        + " -> forge " + (ReferenceEquals(dest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
                    RingSlotManager.Equip(idx, null);
                    dest.item = ring;
                    NotifyForgeSlotsChanged(__instance);
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }
            }

            // Right-click a filled forge ingredient slot -> send its content back.
            foreach (var spot in new[] { __instance.leftIngredientSpot, __instance.rightIngredientSpot })
            {
                if (!spot.containsPoint(x, y) || spot.item == null)
                    continue;

                if (spot.item is Ring forgeRing)
                {
                    if (ReturnRingFromForge(forgeRing, playSound))
                    {
                        spot.item = null;
                        NotifyForgeSlotsChanged(__instance);
                    }
                    return false;
                }

                int stackBefore = spot.item.Stack;
                var slotLeftover = Game1.player.addItemToInventory(spot.item);
                if (slotLeftover == null)
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click return " + spot.item.Name + " -> inventory");
                    spot.item = null;
                    NotifyForgeSlotsChanged(__instance);
                    if (playSound) Game1.playSound("coin");
                }
                else
                {
                    bool partial = slotLeftover.Stack < stackBefore;
                    ModEntry.DiagVerbose(
                        "[Test] Forge right-click return: inventory full"
                        + (partial ? " (partial transfer)" : "") + ", keeping " + slotLeftover.Name + " in slot");
                    spot.item = slotLeftover;
                    if (playSound && partial) Game1.playSound("coin");
                }
                return false;
            }

            // Inventory right-click routing.
            int invIdx = __instance.inventory.getInventoryPositionOfClick(x, y);
            if (invIdx >= 0 && invIdx < __instance.inventory.actualInventory.Count
                && __instance.inventory.actualInventory[invIdx] is { } invItem)
            {
                if (invItem is Ring invRing)
                {
                    if (!IsRingAllowedInForgeContext(invRing, __instance)
                        || IsRingBlockedByDuplicateCap(invRing, __instance))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send blocked: " + invRing.Name + " dimmed");
                        return false;
                    }
                    var invRingDest = GetFreeForgeSlotFor(__instance, invRing);
                    if (invRingDest == null)
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send: no free/valid forge slot for " + invRing.Name);
                        return false;
                    }
                    ModEntry.DiagVerbose(
                        "[Test] Forge right-click send " + invRing.Name + " from inventory -> forge "
                        + (ReferenceEquals(invRingDest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
                    __instance.inventory.actualInventory[invIdx] = null;
                    invRingDest.item = invRing;
                    NotifyForgeSlotsChanged(__instance);
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }

                if (invItem is Tool invTool)
                {
                    if (invTool is StardewValley.Tools.MeleeWeapon invWeapon
                        && __instance.rightIngredientSpot.item == null
                        && __instance.leftIngredientSpot.item is StardewValley.Tools.MeleeWeapon leftWeapon
                        && CanRightItemEnchantTool(leftWeapon, invWeapon))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invWeapon.Name + " -> forge right slot (appearance copy)");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.rightIngredientSpot.item = invWeapon;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    if (__instance.leftIngredientSpot.item == null
                        && IsAcceptedInLeftSlot(__instance, invTool))
                    {
                        if (__instance.rightIngredientSpot.item is { } rightOccupant
                            && !IsValidCraft(__instance, invTool, rightOccupant))
                        {
                            ModEntry.DiagVerbose(
                                "[Test] Forge right-click send blocked: " + invTool.Name
                                + " dimmed (no valid craft with " + rightOccupant.Name + ")");
                            return false;
                        }
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invTool.Name + " -> forge left slot");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.leftIngredientSpot.item = invTool;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose("[Test] Forge right-click send: no destination for " + invTool.Name);
                    return false;
                }

                if (IsValidForgeItem(__instance, invItem))
                {
                    if (__instance.rightIngredientSpot.item == null
                        && IsAcceptedInRightSlot(__instance, invItem))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invItem.Name + " -> forge right slot");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.rightIngredientSpot.item = invItem;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose("[Test] Forge right-click send: right slot unavailable/invalid for " + invItem.Name);
                    return false;
                }
            }

            return true;
        }

        // Track _heldItem after each click to see what vanilla did with it.
        public void HandleLeftClickPostfix(int x, int y)
        {
            var held = GetHeldItem(_menu);
            ModEntry.DiagVerbose($"[Forge] *Postfix* click at ({x},{y})  cursor={Game1.player.CursorSlotItem?.Name ?? "null"}  forge.held={held?.Name ?? "null"}  forge.left={_menu.leftIngredientSpot.item?.Name ?? "null"}  forge.right={_menu.rightIngredientSpot.item?.Name ?? "null"}");
        }

        // Track forge ingredient slot transitions to detect forge completion (diagnostics).
        public void HandleUpdate()
        {
            if (!ModEntry.Instance.Config.VerboseLogging) return;

            ForgeMenu __instance = _menu;
            var held = GetHeldItem(__instance);
            var heldName = held?.Name ?? "null";

            var leftItem = __instance.leftIngredientSpot.item;
            var rightItem = __instance.rightIngredientSpot.item;
            var leftName = leftItem?.Name ?? "null";
            var rightName = rightItem?.Name ?? "null";

            if (heldName != _lastHeldName)
            {
                ModEntry.DiagVerbose($"[Forge] update: forge.held transitioned {_lastHeldName ?? "(init)"} -> {heldName}");
                _lastHeldName = heldName;
            }

            bool forgeConsumed = _lastUpdateLeftName != null && _lastUpdateLeftName != "null"
                && _lastUpdateRightName != null && _lastUpdateRightName != "null"
                && leftName == "null" && rightName == "null";

            if (forgeConsumed)
            {
                var result = __instance.craftResultDisplay?.item;
                if (result is CombinedRing cr)
                {
                    var names = string.Join(", ", cr.combinedRings.Select(r => r.Name));
                    ModEntry.DiagVerbose("[Test] Forge result: CombinedRing [" + names + "] (" + cr.combinedRings.Count + " rings)");
                }
                else if (result != null)
                {
                    ModEntry.DiagVerbose("[Test] Forge result: " + result.Name);
                }
                else if (_preForgeCombinedRingNames != null)
                {
                    var allNames = string.Join(", ", _preForgeCombinedRingNames) + ", " + _lastUpdateRightName;
                    ModEntry.DiagVerbose("[Test] Forge result: CombinedRing [" + allNames + "] (" + (_preForgeCombinedRingNames.Count + 1) + " rings)");
                }
                else if (_lastLeftWasRing)
                {
                    ModEntry.DiagVerbose("[Test] Forge result: CombinedRing [" + _lastUpdateLeftName + ", " + _lastUpdateRightName + "] (2 rings)");
                }
                else
                {
                    ModEntry.DiagVerbose("[Test] Forge result: consumed " + _lastUpdateLeftName + " + " + _lastUpdateRightName);
                }
            }

            _lastLeftWasRing = leftItem is Ring;
            if (leftItem is CombinedRing leftCr)
                _preForgeCombinedRingNames = leftCr.combinedRings.Select(r => r.Name).ToList();
            else
                _preForgeCombinedRingNames = null;

            _lastUpdateLeftName = leftName;
            _lastUpdateRightName = rightName;
        }

        // ============================================================
        //  Hover
        // ============================================================

        public void HandleHover(int x, int y)
        {
            ForgeMenu __instance = _menu;

            // Rebuild the panel on viewport change.  This handler never mutates _panelOpen,
            // so RebuildSlots -> ApplyPanelVisibility reads the user's real open state.
            var vp = Game1.uiViewport;
            if (vp.Width != _lastVpW || vp.Height != _lastVpH)
            {
                _lastVpW = vp.Width;
                _lastVpH = vp.Height;
                __instance.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                RebuildSlots();
                if (ToggleButton != null)
                    __instance.equipmentIcons.Add(ToggleButton);
                foreach (var slot in Slots)
                    __instance.equipmentIcons.Add(slot);
                if (_scrollUpBtn != null)
                    __instance.equipmentIcons.Add(_scrollUpBtn);
                if (_scrollDownBtn != null)
                    __instance.equipmentIcons.Add(_scrollDownBtn);
                var rightRing = __instance.equipmentIcons.Find(c => c.name == "Ring2");
                if (rightRing != null && ToggleButton != null)
                    rightRing.downNeighborID = ToggleButton.myID;
                __instance.populateClickableComponentList();
            }

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
        //  Toggle / scroll
        // ============================================================

        public void TogglePanel(bool playSound)
        {
            _panelOpen = !_panelOpen;
            ModEntry.DiagVerbose("[Test] Forge panel toggled: open=" + _panelOpen);
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(_panelOpen ? "bigSelect" : "bigDeSelect");

            _menu.populateClickableComponentList();

            if (Game1.options.SnappyMenus && ToggleButton != null)
            {
                if (_panelOpen && Slots.Count > 0)
                {
                    int target = System.Math.Min(3, Slots.Count - 1);
                    _menu.setCurrentlySnappedComponentTo(Slots[target].myID);
                }
                else
                {
                    _menu.setCurrentlySnappedComponentTo(ToggleButton.myID);
                }
                _menu.snapCursorToCurrentSnappedComponent();
            }
        }

        public bool HandleScroll(int direction)
        {
            if (!_panelOpen || ToggleButton == null || _maxScrollOffset <= 0) return true;

            var mousePos = Game1.getMousePosition();
            var panelBounds = GetSlotsBounds();
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
            _menu.populateClickableComponentList();
            SnapToVisibleSlot();
            return false;
        }

        private void SnapToVisibleSlot()
        {
            if (!Game1.options.SnappyMenus) return;
            var snapped = _menu.currentlySnappedComponent;
            if (snapped == null) return;
            if (!snapped.name.StartsWith("ExtraRing") || snapped.name.StartsWith("ExtraRingScroll"))
                return;

            if (snapped.bounds.X <= -5000)
            {
                var firstVisible = Slots.FirstOrDefault(s => s.bounds.X > -5000);
                if (firstVisible != null)
                {
                    _menu.setCurrentlySnappedComponentTo(firstVisible.myID);
                    return;
                }
            }

            _menu.snapCursorToCurrentSnappedComponent();
        }

        // ============================================================
        //  Cursor-ring drop / empty-cursor pickup
        // ============================================================

        private bool HandleCursorRingDrop(int x, int y, bool playSound)
        {
            ForgeMenu menu = _menu;
            Ring carriedRing = (Ring)Game1.player.CursorSlotItem!;
            ModEntry.DiagVerbose("[Test] Forge cursor drop: carrying " + carriedRing.Name);

            // 1) Left Ring equipment icon (forge names it "Ring1").
            var leftRingIcon = menu.equipmentIcons.Find(c => c.name == "Ring1");
            if (leftRingIcon != null && leftRingIcon.containsPoint(x, y))
            {
                Ring? existing = Game1.player.leftRing.Value;
                if (existing != null && !IsRingAllowedInForgeContext(existing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: " + existing.Name + " dimmed");
                    return false;
                }
                if (existing != null && IsRingBlockedByDuplicateCap(existing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: " + existing.Name + " duplicate cap on left ring");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " -> left ring equipment slot");
                existing?.onUnequip(Game1.player);
                Game1.player.leftRing.Value = carriedRing;
                carriedRing.onEquip(Game1.player);
                Game1.player.CursorSlotItem = existing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            // 2) Right Ring equipment icon (forge names it "Ring2").
            var rightRingIcon = menu.equipmentIcons.Find(c => c.name == "Ring2");
            if (rightRingIcon != null && rightRingIcon.containsPoint(x, y))
            {
                Ring? existing = Game1.player.rightRing.Value;
                if (existing != null && !IsRingAllowedInForgeContext(existing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: " + existing.Name + " dimmed");
                    return false;
                }
                if (existing != null && IsRingBlockedByDuplicateCap(existing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: " + existing.Name + " duplicate cap on right ring");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " -> right ring equipment slot");
                existing?.onUnequip(Game1.player);
                Game1.player.rightRing.Value = carriedRing;
                carriedRing.onEquip(Game1.player);
                Game1.player.CursorSlotItem = existing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            // 3) Forge left ingredient slot.
            if (menu.leftIngredientSpot.containsPoint(x, y))
            {
                Item? leftItem = menu.leftIngredientSpot.item;
                if (leftItem == null)
                {
                    Item? rightItem = menu.rightIngredientSpot.item;
                    if (rightItem == null || IsValidCraft(menu, carriedRing, rightItem))
                    {
                        ModEntry.DiagVerbose($"[Forge]   -> drop onto forge.left");
                        menu.leftIngredientSpot.item = carriedRing;
                        Game1.player.CursorSlotItem = null;
                        NotifyForgeSlotsChanged(menu);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose($"[Forge]   -> forge.left would create invalid combination with right={rightItem?.Name}; refusing");
                    return false;
                }
                if (leftItem is Ring)
                {
                    Item? rightItem = menu.rightIngredientSpot.item;
                    if (rightItem == null || IsValidCraft(menu, carriedRing, rightItem))
                    {
                        ModEntry.DiagVerbose($"[Forge]   -> swap with forge.left (existing ring)");
                        menu.leftIngredientSpot.item = carriedRing;
                        Game1.player.CursorSlotItem = leftItem;
                        NotifyForgeSlotsChanged(menu);
                        if (playSound) Game1.playSound("crit");
                        return false;
                    }
                }
                ModEntry.DiagVerbose($"[Forge]   -> forge.left occupied with non-Ring or invalid pair; blocking click");
                return false;
            }

            // 4) Forge right ingredient slot.
            if (menu.rightIngredientSpot.containsPoint(x, y))
            {
                Item? rightItem = menu.rightIngredientSpot.item;
                if (rightItem == null)
                {
                    Item? leftItem = menu.leftIngredientSpot.item;
                    if (leftItem == null || IsValidCraft(menu, leftItem, carriedRing))
                    {
                        ModEntry.DiagVerbose($"[Forge]   -> drop onto forge.right");
                        menu.rightIngredientSpot.item = carriedRing;
                        Game1.player.CursorSlotItem = null;
                        NotifyForgeSlotsChanged(menu);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose($"[Forge]   -> forge.right would not form a valid craft with left={leftItem?.Name}; refusing");
                    return false;
                }
                if (rightItem is Ring)
                {
                    Item? leftItem = menu.leftIngredientSpot.item;
                    if (leftItem == null || IsValidCraft(menu, leftItem, carriedRing))
                    {
                        ModEntry.DiagVerbose($"[Forge]   -> swap with forge.right (existing ring)");
                        menu.rightIngredientSpot.item = carriedRing;
                        Game1.player.CursorSlotItem = rightItem;
                        NotifyForgeSlotsChanged(menu);
                        if (playSound) Game1.playSound("crit");
                        return false;
                    }
                }
                ModEntry.DiagVerbose($"[Forge]   -> forge.right occupied with non-Ring or invalid pair; blocking click");
                return false;
            }

            // 5) Inventory slot.
            int invIdx = menu.inventory.getInventoryPositionOfClick(x, y);
            if (invIdx >= 0 && invIdx < menu.inventory.actualInventory.Count)
            {
                Item? existing = menu.inventory.actualInventory[invIdx];
                if (existing == null || IsValidForgeItem(menu, existing))
                {
                    if (existing is Ring existingRing && !IsRingAllowedInForgeContext(existingRing, menu))
                    {
                        ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: inventory " + existingRing.Name + " dimmed");
                        return false;
                    }
                    ModEntry.DiagVerbose($"[Forge]   -> drop onto inventory slot {invIdx}");
                    menu.inventory.actualInventory[invIdx] = carriedRing;
                    Game1.player.CursorSlotItem = existing;
                    if (playSound) Game1.playSound("coin");
                    return false;
                }
                ModEntry.DiagVerbose($"[Forge]   -> inventory slot {invIdx} holds non-forge item ({existing.Name}); not swapping");
                return false;
            }

            // 6) Panel slot.
            if (_panelOpen)
            {
                foreach (var slot in Slots)
                {
                    if (!slot.containsPoint(x, y)) continue;
                    int idx = SlotIndex(slot);
                    if (idx < 0) return true;

                    Ring? slotRing = RingSlotManager.Slots[idx];
                    if (slotRing != null)
                    {
                        if (!IsRingAllowedInForgeContext(slotRing, menu))
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: panel slot " + idx + " " + slotRing.Name + " dimmed");
                            return false;
                        }
                        if (IsRingBlockedByDuplicateCap(slotRing, menu))
                        {
                            ModEntry.DiagVerbose("[Test] Forge cursor drop blocked: panel slot " + idx + " " + slotRing.Name + " duplicate cap");
                            return false;
                        }
                    }
                    ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " -> panel slot " + idx);
                    RingSlotManager.Equip(idx, carriedRing);
                    Game1.player.CursorSlotItem = slotRing;
                    if (playSound) Game1.playSound("crit");
                    return false;
                }
            }

            ModEntry.DiagVerbose($"[Forge]   -> no target found; blocking click while carrying ring");
            return false;
        }

        private bool HandleEmptyCursorPickup(int x, int y, bool playSound)
        {
            ForgeMenu menu = _menu;

            var leftRingIcon = menu.equipmentIcons.Find(c => c.name == "Ring1");
            if (leftRingIcon != null && leftRingIcon.containsPoint(x, y)
                && Game1.player.leftRing.Value is Ring leftRing)
            {
                if (!IsRingAllowedInForgeContext(leftRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: " + leftRing.Name + " dimmed");
                    return false;
                }
                if (IsRingBlockedByDuplicateCap(leftRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: " + leftRing.Name + " duplicate cap on left ring");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge pickup: " + leftRing.Name + " from left ring equipment");
                leftRing.onUnequip(Game1.player);
                Game1.player.leftRing.Value = null;
                Game1.player.CursorSlotItem = leftRing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            var rightRingIcon = menu.equipmentIcons.Find(c => c.name == "Ring2");
            if (rightRingIcon != null && rightRingIcon.containsPoint(x, y)
                && Game1.player.rightRing.Value is Ring rightRing)
            {
                if (!IsRingAllowedInForgeContext(rightRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: " + rightRing.Name + " dimmed");
                    return false;
                }
                if (IsRingBlockedByDuplicateCap(rightRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: " + rightRing.Name + " duplicate cap on right ring");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge pickup: " + rightRing.Name + " from right ring equipment");
                rightRing.onUnequip(Game1.player);
                Game1.player.rightRing.Value = null;
                Game1.player.CursorSlotItem = rightRing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            if (!_panelOpen)
            {
                if (ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                    && ModEntry.Instance.Config.InfiniteCombining
                    && (menu.leftIngredientSpot.item is Ring || menu.rightIngredientSpot.item is Ring))
                {
                    int invBlockIdx = menu.inventory.getInventoryPositionOfClick(x, y);
                    if (invBlockIdx >= 0
                        && invBlockIdx < menu.inventory.actualInventory.Count
                        && menu.inventory.actualInventory[invBlockIdx] is Ring invBlockRing
                        && IsRingBlockedByDuplicateCap(invBlockRing, menu))
                    {
                        ModEntry.DiagVerbose($"[Forge] Blocked vanilla inventory pickup of {invBlockRing.Name}: duplicate cap");
                        return false;
                    }
                }
                return true;
            }

            if (menu.leftIngredientSpot.containsPoint(x, y)
                && menu.leftIngredientSpot.item is Ring leftSlotRing)
            {
                ModEntry.DiagVerbose("[Test] Forge pickup: " + leftSlotRing.Name + " from forge left slot");
                menu.leftIngredientSpot.item = null;
                Game1.player.CursorSlotItem = leftSlotRing;
                NotifyForgeSlotsChanged(menu);
                if (playSound) Game1.playSound("crit");
                return false;
            }

            if (menu.rightIngredientSpot.containsPoint(x, y)
                && menu.rightIngredientSpot.item is Ring rightSlotRing)
            {
                ModEntry.DiagVerbose("[Test] Forge pickup: " + rightSlotRing.Name + " from forge right slot");
                menu.rightIngredientSpot.item = null;
                Game1.player.CursorSlotItem = rightSlotRing;
                NotifyForgeSlotsChanged(menu);
                if (playSound) Game1.playSound("crit");
                return false;
            }

            int invPickIdx = menu.inventory.getInventoryPositionOfClick(x, y);
            if (invPickIdx >= 0
                && invPickIdx < menu.inventory.actualInventory.Count
                && menu.inventory.actualInventory[invPickIdx] is Ring invRing)
            {
                if (!IsRingAllowedInForgeContext(invRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: inventory " + invRing.Name + " dimmed");
                    return false;
                }
                if (IsRingBlockedByDuplicateCap(invRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: inventory " + invRing.Name + " duplicate cap");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge pickup: " + invRing.Name + " from inventory slot " + invPickIdx);
                menu.inventory.actualInventory[invPickIdx] = null;
                Game1.player.CursorSlotItem = invRing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            foreach (var slot in Slots)
            {
                if (!slot.containsPoint(x, y)) continue;
                int idx = SlotIndex(slot);
                if (idx < 0) return true;

                Ring? slotRing = RingSlotManager.Slots[idx];
                if (slotRing == null) return false;

                if (!IsRingAllowedInForgeContext(slotRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: panel " + slotRing.Name + " dimmed");
                    return false;
                }
                if (IsRingBlockedByDuplicateCap(slotRing, menu))
                {
                    ModEntry.DiagVerbose("[Test] Forge pickup blocked: panel " + slotRing.Name + " duplicate cap");
                    return false;
                }
                ModEntry.DiagVerbose("[Test] Forge pickup: " + slotRing.Name + " from panel slot " + idx);
                RingSlotManager.Equip(idx, null);
                Game1.player.CursorSlotItem = slotRing;
                if (playSound) Game1.playSound("crit");
                return false;
            }

            return true;
        }

        /// <summary>Route a ring taken out of a forge slot back where it belongs: the first
        /// empty panel slot (when the panel is open), else an empty vanilla ring slot (Ring1
        /// then Ring2), else the inventory.  Returns false only when everything is full.</summary>
        private bool ReturnRingFromForge(Ring ring, bool playSound)
        {
            if (_panelOpen)
            {
                int firstEmpty = RingSlotManager.Slots.FindIndex(r => r == null);
                if (firstEmpty >= 0)
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " -> panel slot " + firstEmpty);
                    RingSlotManager.Equip(firstEmpty, ring);
                    if (playSound) Game1.playSound("crit");
                    return true;
                }
            }
            if (Game1.player.leftRing.Value == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " -> Ring1");
                Game1.player.leftRing.Value = ring;
                ring.onEquip(Game1.player);
                Game1.player.buffs.Dirty = true;
                if (playSound) Game1.playSound("crit");
                return true;
            }
            if (Game1.player.rightRing.Value == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " -> Ring2");
                Game1.player.rightRing.Value = ring;
                ring.onEquip(Game1.player);
                Game1.player.buffs.Dirty = true;
                if (playSound) Game1.playSound("crit");
                return true;
            }
            if (Game1.player.addItemToInventory(ring) == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " -> inventory");
                if (playSound) Game1.playSound("coin");
                return true;
            }
            ModEntry.DiagVerbose("[Test] Forge right-click return: everywhere full, keeping " + ring.Name + " in forge slot");
            return false;
        }

        // ============================================================
        //  Dim memo (draw-path)
        // ============================================================

        /// <summary>Revalidate the dim memo against the current ingredient pair (reference
        /// identity), clearing it when the pair changed, and refresh the pair-level duplicate
        /// flag.</summary>
        private void RevalidateDimMemo()
        {
            Item? left = _menu.leftIngredientSpot.item;
            Item? right = _menu.rightIngredientSpot.item;
            if (!ReferenceEquals(left, DimLeft) || !ReferenceEquals(right, DimRight))
            {
                DimAllowed.Clear();
                DimLeft = left;
                DimRight = right;
                PairDuplicate = left is Ring leftRing && right is Ring rightRing
                    && Patches.WouldCreateDuplicateRing(leftRing, rightRing);
            }
        }

        /// <summary>Memoized <see cref="ForgeMenuPatches.IsRingAllowedInForgeContext"/> for the
        /// draw path, which asks once per panel ring per frame.</summary>
        private bool IsRingAllowedCached(Ring ring)
        {
            RevalidateDimMemo();
            if (!DimAllowed.TryGetValue(ring, out bool allowed))
                DimAllowed[ring] = allowed = IsRingAllowedInForgeContext(ring, _menu);
            return allowed;
        }

        /// <summary>Drop the dim memo (config edits change the verdicts).</summary>
        public void ClearDimMemo()
        {
            DimAllowed.Clear();
            DimLeft = null;
            DimRight = null;
            PairDuplicate = false;
        }

        private static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;
    }
}
