using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>Adds a collapsible panel of extra ring slots to the forge menu.</summary>
    public static class ForgeMenuPatches
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

        private static readonly System.Reflection.FieldInfo? HeldItemField =
            AccessTools.Field(typeof(MenuWithInventory), "_heldItem");

        private static readonly System.Reflection.FieldInfo? HighlightDictionaryField =
            AccessTools.Field(typeof(ForgeMenu), "_highlightDictionary");

        private static readonly System.Reflection.MethodInfo? ValidateCraftMethod =
            AccessTools.Method(typeof(ForgeMenu), "_ValidateCraft");

        private static readonly System.Reflection.MethodInfo? HighlightItemsMethod =
            AccessTools.Method(typeof(ForgeMenu), "HighlightItems");


        /// <summary>All mutable panel/UI state, one instance per screen.  Split-screen
        /// players each have their own menus, so sharing this across screens made one
        /// screen's panel clobber the other's.  The properties below keep the original
        /// member names so the rest of the file reads/writes them unchanged.</summary>
        private sealed class PanelState
        {
            public readonly List<ClickableComponent> Slots = new();
            public ClickableTextureComponent? ToggleButton;
            public bool PanelOpen;
            public int ScrollOffset;
            public int MaxScrollOffset;
            public int VisibleRows;
            public ClickableComponent? ScrollUpBtn;
            public ClickableComponent? ScrollDownBtn;
            public int LastVpW = -1;
            public int LastVpH = -1;
            public string HoverText = "";

            // Forge-completion trackers used by Update_Postfix diagnostics.
            public string? LastHeldName;
            public string? LastUpdateLeftName;
            public string? LastUpdateRightName;
            public List<string>? PreForgeCombinedRingNames;
            public bool LastLeftWasRing;

            // Draw-path dim memo: IsRingAllowedInForgeContext verdict per panel ring,
            // plus the pair-level duplicate check DrawCombinedRingGlow needs.  Keyed by
            // the forge ingredient pair (reference identity); revalidated on every
            // lookup, dropped on menu open and config change.
            public readonly Dictionary<Ring, bool> DimAllowed = new();
            public Item? DimLeft;
            public Item? DimRight;
            public bool PairDuplicate;
        }

        private static readonly PerScreen<PanelState> StatePerScreen = new(() => new PanelState());

        private static List<ClickableComponent> Slots => StatePerScreen.Value.Slots;
        private static ClickableTextureComponent? ToggleButton
        {
            get => StatePerScreen.Value.ToggleButton;
            set => StatePerScreen.Value.ToggleButton = value;
        }
        private static bool _panelOpen
        {
            get => StatePerScreen.Value.PanelOpen;
            set => StatePerScreen.Value.PanelOpen = value;
        }
        private static int _scrollOffset
        {
            get => StatePerScreen.Value.ScrollOffset;
            set => StatePerScreen.Value.ScrollOffset = value;
        }
        private static int _maxScrollOffset
        {
            get => StatePerScreen.Value.MaxScrollOffset;
            set => StatePerScreen.Value.MaxScrollOffset = value;
        }
        private static int _visibleRows
        {
            get => StatePerScreen.Value.VisibleRows;
            set => StatePerScreen.Value.VisibleRows = value;
        }
        private static ClickableComponent? _scrollUpBtn
        {
            get => StatePerScreen.Value.ScrollUpBtn;
            set => StatePerScreen.Value.ScrollUpBtn = value;
        }
        private static ClickableComponent? _scrollDownBtn
        {
            get => StatePerScreen.Value.ScrollDownBtn;
            set => StatePerScreen.Value.ScrollDownBtn = value;
        }
        private static int _lastVpW
        {
            get => StatePerScreen.Value.LastVpW;
            set => StatePerScreen.Value.LastVpW = value;
        }
        private static int _lastVpH
        {
            get => StatePerScreen.Value.LastVpH;
            set => StatePerScreen.Value.LastVpH = value;
        }
        private static string _hoverText
        {
            get => StatePerScreen.Value.HoverText;
            set => StatePerScreen.Value.HoverText = value;
        }
        private static string? _lastHeldName
        {
            get => StatePerScreen.Value.LastHeldName;
            set => StatePerScreen.Value.LastHeldName = value;
        }
        private static string? _lastUpdateLeftName
        {
            get => StatePerScreen.Value.LastUpdateLeftName;
            set => StatePerScreen.Value.LastUpdateLeftName = value;
        }
        private static string? _lastUpdateRightName
        {
            get => StatePerScreen.Value.LastUpdateRightName;
            set => StatePerScreen.Value.LastUpdateRightName = value;
        }
        private static List<string>? _preForgeCombinedRingNames
        {
            get => StatePerScreen.Value.PreForgeCombinedRingNames;
            set => StatePerScreen.Value.PreForgeCombinedRingNames = value;
        }
        private static bool _lastLeftWasRing
        {
            get => StatePerScreen.Value.LastLeftWasRing;
            set => StatePerScreen.Value.LastLeftWasRing = value;
        }

        public static bool IsPanelOpen => _panelOpen;
        
        // Forge spritesheet (cached on first access).  Vanilla loads this into
        // ForgeMenu.forgeTextures; we mirror its content path so our visuals
        // match the rest of the forge UI exactly.
        private static Texture2D? _forgeTextures;
        private static Texture2D ForgeTextures
        {
            get
            {
                if (_forgeTextures == null)
                    _forgeTextures = Game1.content.Load<Texture2D>("LooseSprites\\ForgeMenu");
                return _forgeTextures;
            }
        }

        /// <summary>Calibrated tint that renders to the forge's #BC635B panel BG.  The raw
        /// hex tint is shifted to compensate for menuTexture's cream-coloured source pixels
        /// (so tint × source ≈ #BC635B).</summary>
        private static readonly Color ForgePanelTint = new Color(0xBC, 0x81, 0xC5);

        /// <summary>Color sampled from the vanilla forge Ring1/Ring2 slot border (#6D0A03).</summary>
        private static readonly Color ForgeSlotTint  = new Color(0x6D, 0x0A, 0x03);

        /// <summary>Solid fill behind transparent slot frames so the toggle/slot has a
        /// recessed look instead of an empty transparent center.  Slightly darker than the
        /// panel BG so the slot reads as a sunken cell.</summary>
        private static readonly Color ForgeSlotFill = new Color(0x9B, 0x40, 0x35);

        /// <summary>Source rectangle on ForgeTextures for the dusky-red equipment-slot frame
        /// (the one vanilla uses for Ring1/Ring2/leftIngredient/rightIngredient).</summary>
        private static readonly Rectangle ForgeSlotFrameSource = new Rectangle(140, 250, 28, 28);

        public static void Apply(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            harmony.Patch(
                original: AccessTools.Constructor(typeof(ForgeMenu)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Ctor_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.draw),
                    new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Draw_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveLeftClick)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(LeftClick_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveRightClick)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(RightClick_Prefix))
            );

            // Postfix to see what vanilla left in _heldItem after handling the click.
            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.receiveLeftClick)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(LeftClick_Postfix))
            );

            // Postfix on update() to detect when vanilla finishes the craft and assigns _heldItem.
            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.update),
                    new[] { typeof(Microsoft.Xna.Framework.GameTime) }),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Update_Postfix))
            );
            
            harmony.Patch(
                original: AccessTools.Method(typeof(ForgeMenu), nameof(ForgeMenu.performHoverAction)),
                postfix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(Hover_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.receiveScrollWheelAction)),
                prefix: new HarmonyMethod(typeof(ForgeMenuPatches), nameof(ScrollWheel_Prefix))
            );
        }

        public static bool ScrollWheel_Prefix(int direction)
        {
            if (!_panelOpen || ToggleButton == null || _maxScrollOffset <= 0) return true;
            // The panel state statics outlive the menu (nothing resets them on close) —
            // never intercept scrolling while some other menu is on screen.
            if (Game1.activeClickableMenu is not ForgeMenu) return true;

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
            (Game1.activeClickableMenu as ForgeMenu)?.populateClickableComponentList();
            SnapToVisibleSlot();
            return false;
        }

        private static void SnapToVisibleSlot()
        {
            if (!Game1.options.SnappyMenus) return;
            var menu = Game1.activeClickableMenu;
            if (menu == null) return;
            var snapped = menu.currentlySnappedComponent;
            if (snapped == null) return;
            if (!snapped.name.StartsWith("ExtraRing") || snapped.name.StartsWith("ExtraRingScroll"))
                return;

            if (snapped.bounds.X <= -5000)
            {
                var firstVisible = Slots.FirstOrDefault(s => s.bounds.X > -5000);
                if (firstVisible != null)
                {
                    menu.setCurrentlySnappedComponentTo(firstVisible.myID);
                    return;
                }
            }

            menu.snapCursorToCurrentSnappedComponent();
        }

        public static void RebuildForActiveMenu()
        {
            if (Game1.activeClickableMenu is ForgeMenu menu)
            {
                menu.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                ForgeMenuPatches.Ctor_Postfix(menu);
                menu.populateClickableComponentList();
            }
        }

        // ============================================================
        //  Layout
        // ============================================================

        private static Rectangle GetToggleBounds(ForgeMenu menu)
        {
            ClickableComponent? anchor = null;
            foreach (var c in menu.equipmentIcons)
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

        private static void RebuildSlots(ForgeMenu menu)
        {
            Slots.Clear();
            ToggleButton = null;
            _scrollUpBtn = null;
            _scrollDownBtn = null;
            RingSlotManager.EnsureSize();

            var toggleBounds = GetToggleBounds(menu);
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
                upNeighborID = (menu.equipmentIcons.Find(c => c.name == "Ring2")
                                ?? menu.equipmentIcons.Find(c => c.name == "Ring1"))?.myID
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

            ClickableComponent? leftRing = menu.equipmentIcons.Find(c => c.name == "Ring1");
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
                    // Neighbor IDs are assigned by ApplyPanelVisibility (called below and
                    // on every toggle/scroll/resize); no need to wire them here.
                    fullyImmutable = true
                };
                Slots.Add(slot);
            }

            ApplyPanelVisibility();
        }

        private static void ApplyPanelVisibility()
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

            ForgeMenu? menu = Game1.activeClickableMenu as ForgeMenu;

            int gridEndX = ToggleButton.bounds.X - SlotSpacing * 4;
            int gridStartX = gridEndX - maxPerRow * (SlotSize + SlotSpacing);
            int panelTopY = (menu?.equipmentIcons.Find(c => c.name == "Ring1") is { } lr)
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

                // Wire inventory's leftmost column ↔ panel's rightmost visible column.
                if (menu != null)
                {
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

                if (menu != null)
                {
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
        }

        // ============================================================
        //  Inject into equipmentIcons
        // ============================================================

        [HarmonyPriority(Priority.Last)]
        public static void Ctor_Postfix(ForgeMenu __instance)
        {
            _testLogOnce.Clear();
            _panelOpen = false;
            _scrollOffset = 0;
            // Fresh menu: drop the highlight/dim memos so nothing from the previous
            // menu (or a config edit made in between) leaks into this one.
            InvalidateDimCache();
            Patches.InvalidateHighlightCache();
            RebuildSlots(__instance);

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
            ModEntry.DiagVerbose("[Test] Forge panel initialized with " + Slots.Count + " slots");

            // One-shot diagnostic: dump equipmentIcons.
            ModEntry.DiagVerbose("[Forge] equipmentIcons dump:");
            foreach (var c in __instance.equipmentIcons)
                ModEntry.DiagVerbose($"  name='{c.name}'  bounds={c.bounds}");
        }

        // ============================================================
        //  Draw
        // ============================================================

            public static void Draw_Postfix(ForgeMenu __instance, SpriteBatch b)
            {
                if (ToggleButton == null) return;

                // 1) Toggle button: solid fill underneath (gives the recessed cell a
                //    background), then the slot frame on top.
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

                    // Panel BG — calibrated tint to render as forge's #BC635B.
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

                        // Solid fill behind the slot frame so the cell isn't transparent.
                        b.Draw(Game1.staminaRect, slot.bounds, ForgeSlotFill);

                        // Slot border in the deep brick-red.
                        b.Draw(Game1.menuTexture, slot.bounds,
                            Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10),
                            ForgeSlotTint);

                        if (ring != null)
                        {
                            // Dim rings the forge context refuses (non-ring ingredient in a
                            // slot, invalid craft, duplicate cap).  Verdicts are memoized per
                            // ingredient pair — this runs for every panel slot every frame.
                            bool blocked = !IsRingAllowedCached(ring, __instance);

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
            
                DrawCombinedRingGlow(__instance, b);
            
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
            __instance.drawMouse(b);
        }

        private static Rectangle GetSlotsBounds()
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

        /// <summary>Vanilla's ForgeMenu.draw explicitly excludes CombinedRing from the
        /// "this item could go in a slot" highlight (vanilla can't combine already-
        /// combined rings).  With InfiniteCombining we DO support that — so draw the
        /// missing glow ourselves.
        /// Also re-draws the hovered-item tooltip on top since vanilla's tooltip was
        /// drawn before the glow.</summary>
        private static void DrawCombinedRingGlow(ForgeMenu menu, SpriteBatch b)
        {
            if (!ModEntry.Instance.Config.InfiniteCombining) return;

            Item? highlightItem = Game1.player.CursorSlotItem
                                  ?? GetHeldItem(menu)
                                  ?? menu.hoveredItem;

            var leftRing  = menu.leftIngredientSpot.item as Ring;
            var rightRing = menu.rightIngredientSpot.item as Ring;

            if (highlightItem is CombinedRing
                && menu.leftIngredientSpot.item is null or Ring
                && menu.rightIngredientSpot.item is null or Ring
                && !menu.IsBusy()
                && ModEntry.Instance.Config.AddCombinedDuplicateRingCap
                && leftRing != null && rightRing != null
                && RevalidateDimMemo(menu).PairDuplicate)
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

        public static bool LeftClick_Prefix(ForgeMenu __instance, int x, int y, bool playSound)
        {
            Item? heldItem = GetHeldItem(__instance);
            Item? invItemAtClick = __instance.inventory.getItemAt(x, y);

            // Toggle button click — handle FIRST, always permitted.
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
                    (Game1.activeClickableMenu as ForgeMenu)?.populateClickableComponentList();
                    SnapToVisibleSlot();
                    return false;
                }
                if (_scrollDownBtn != null && _scrollDownBtn.containsPoint(x, y) && _scrollOffset < _maxScrollOffset)
                {
                    _scrollOffset++;
                    ApplyPanelVisibility();
                    (Game1.activeClickableMenu as ForgeMenu)?.populateClickableComponentList();
                    SnapToVisibleSlot();
                    return false;
                }
            }

            // === UNIFY THE TWO CARRY MECHANISMS ===
            // If the forge's private _heldItem has something and CursorSlotItem is empty,
            // promote the held item to CursorSlotItem.  All our drop logic uses
            // CursorSlotItem; the forge ingredient slot drops are handled by our own
            // code (below), not vanilla's, so promotion is always safe.
            if (Game1.player.CursorSlotItem == null && heldItem != null)
            {
                ModEntry.DiagVerbose($"[Forge] Promoting forge.held ({heldItem.Name}) to CursorSlotItem");
                Game1.player.CursorSlotItem = heldItem;
                SetHeldItem(__instance, null);
                heldItem = null;
            }
            
            ModEntry.DiagVerbose($"[Forge] *Prefix* click at ({x},{y})  cursor={Game1.player.CursorSlotItem?.Name ?? "null"}  forge.held={heldItem?.Name ?? "null"}  inv@click={invItemAtClick?.Name ?? "null"}  panelOpen={_panelOpen}");
            
            // DIAGNOSTIC: inspect inventory.highlightMethod
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
                // If cursor carries *anything*, and click is on an inventory slot, do a simple
                // swap to bypass other blocking logic.
                //
                // GUARDS:
                //   1. Refuse to swap with a non-forge inventory item (Frozen Tear, etc.).
                //   2. Refuse to swap with an inventory item that would form a no-op craft
                //      against the current forge.left (e.g. picking up a Diamond stack from
                //      inventory while a fully-gem-enchanted tool is in forge.left).
                Item? cursorAny = Game1.player.CursorSlotItem;
                if (cursorAny != null)
                {
                    int invSwapIdx = __instance.inventory.getInventoryPositionOfClick(x, y);
                    ModEntry.DiagVerbose($"[Forge] Cursor-any swap path: cursor={cursorAny.Name}, invSwapIdx={invSwapIdx}");
                    if (invSwapIdx >= 0 && invSwapIdx < __instance.inventory.actualInventory.Count)
                    {
                        Item? existing = __instance.inventory.actualInventory[invSwapIdx];

                        // Guard 1: must be empty or a valid forge ingredient.
                        bool swapOk = existing == null || IsValidForgeItem(__instance, existing);
                        if (!swapOk)
                        {
                            ModEntry.DiagVerbose($"[Forge] Cursor swap refused: inventory slot {invSwapIdx} holds non-forge item ({existing!.Name})");
                            return false;
                        }

                        // Guard 2: refuse if picking up `existing` would be a no-op craft
                        // against the current forge.left tool.  Prevents lifting dimmed
                        // Diamonds / Prismatic Shards via a swap.
                        if (existing != null
                            && __instance.leftIngredientSpot.item is Tool leftToolForSwap
                            && !CanRightItemEnchantTool(leftToolForSwap, existing))
                        {
                            ModEntry.DiagVerbose($"[Forge] Cursor swap refused: would lift no-op-ingredient ({existing.Name}) against {leftToolForSwap.Name}");
                            return false;
                        }
                        
                        // Guard 3: ring/non-ring mix prevention.
                        // The swap would leave `existing` on the cursor.  If `existing` is a
                        // non-Ring and a Ring is sitting in a forge slot, that's the broken
                        // mix-and-match state.  Likewise if `existing` is a Ring while a
                        // non-Ring is in a forge slot.  Refuse either case.
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
                                ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: non-ring " + existing.Name + " → ring in forge slot");
                                return false;
                            }
                            if (nonRingInSlot && existing is Ring)
                            {
                                ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: ring " + existing.Name + " → non-ring in forge slot");
                                return false;
                            }

                            // Guard 4: forge context — refuse if the ring being lifted would
                            // be dimmed by the current forge state.
                            if (existing is Ring existingCtxRing && !IsRingAllowedInForgeContext(existingCtxRing, __instance))
                            {
                                ModEntry.DiagVerbose("[Test] Forge cursor swap blocked: " + existingCtxRing.Name + " dimmed");
                                return false;
                            }

                            // Guard 5: duplicate cap — refuse if the ring being lifted would
                            // be blocked by AddCombinedDuplicateRingCap.
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
            // Two cursor mechanisms exist:
            //   - Game1.player.CursorSlotItem (used by our code).
            //   - MenuWithInventory._heldItem (used internally by the forge).
            // If EITHER is occupied with a non-Ring, refuse any click that would put a Ring
            // onto either carry — that's how the "ring + sword on cursor" bug arises.
            // Likewise, refuse clicks that would put a non-Ring onto either carry while a
            // Ring is already there.
            //
            // Additionally: if a non-Ring is in EITHER forge ingredient slot, block ring
            // pickups entirely — vanilla "greys out" rings in this state.
            Item? cursorItem = Game1.player.CursorSlotItem;
            
            bool nonRingInForgeSlot =
                (__instance.leftIngredientSpot.item  != null && __instance.leftIngredientSpot.item  is not Ring) ||
                (__instance.rightIngredientSpot.item != null && __instance.rightIngredientSpot.item is not Ring);

            bool ringInForgeSlot =
                __instance.leftIngredientSpot.item  is Ring ||
                __instance.rightIngredientSpot.item is Ring;

            // Helper: does the click target something whose pickup would put a Ring onto a carry?
            bool wouldGrabRing = false;
            // Helper: does the click target something whose pickup would put a non-Ring onto a carry?
            bool wouldGrabNonRing = false;

            // Forge ingredient slots (cursor empty → vanilla returns to inventory, but if our
            // panel-open pickup is on, we'd pick it up to CursorSlotItem).
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

            // Vanilla equipment ring icons.
            var gateLeftRingIcon  = __instance.equipmentIcons.Find(c => c.name == "Ring1");
            var gateRightRingIcon = __instance.equipmentIcons.Find(c => c.name == "Ring2");
            if ((gateLeftRingIcon  != null && gateLeftRingIcon.containsPoint(x, y)  && Game1.player.leftRing.Value  is Ring) ||
                (gateRightRingIcon != null && gateRightRingIcon.containsPoint(x, y) && Game1.player.rightRing.Value is Ring))
            {
                wouldGrabRing = true;
            }

            // Inventory slot.
            //if (invItemAtClick is Ring) wouldGrabRing = true;
            // else if (invItemAtClick != null) wouldGrabNonRing = true;

            // Panel slot.
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

            // Refuse mix-and-match: if either carry already has something, don't let a
            // different-type item land on the OTHER carry.
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

            // New rule: when a non-Ring is in either forge ingredient slot, rings are
            // "greyed out" — block all ring pickups.
            if (nonRingInForgeSlot && wouldGrabRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: non-ring in forge slot, rings greyed out");
                return false;
            }
            
            // Symmetric rule: when a Ring is in either forge ingredient slot, non-rings
            // (swords, tools, etc.) are greyed out — block their pickup so the user can't
            // create an invalid ring+sword combination in the forge.
            if (ringInForgeSlot && wouldGrabNonRing)
            {
                ModEntry.DiagVerbose("[Test] Forge blocked: ring in forge slot, non-rings greyed out");
                return false;
            }
            
            // === CURSOR-HELD ITEM: forge ingredient slot drops ===
            // Vanilla checks _heldItem (not CursorSlotItem) for forge slot drops, so we
            // need to handle these ourselves when our promotion has moved a non-Ring item
            // onto the cursor.  This works for both Ring and non-Ring cursor items.
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

                    // If the slot is empty, just drop.
                    if (leftExisting == null)
                    {
                        // Honor the Ring + right-slot validity rule when carrying a Ring.
                        if (carriedAny is Ring r)
                        {
                            Item? rightItem = __instance.rightIngredientSpot.item;
                            if (rightItem != null && !IsValidCraft(__instance, r, rightItem))
                            {
                                ModEntry.DiagVerbose("[Test] Forge drop blocked: ring " + r.Name + " invalid with right=" + rightItem.Name);
                                return false;
                            }
                            // Prismatic Shard / Dragon Tooth never combine with rings — refuse
                            // dropping a ring onto forge.left if those are sitting in forge.right.
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
                        ModEntry.DiagVerbose("[Test] Forge drop accepted: " + carriedAny.Name + " → left slot");
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }

                    // Slot is occupied: swap with the cursor.
                    __instance.leftIngredientSpot.item = carriedAny;
                    Game1.player.CursorSlotItem = leftExisting;
                    NotifyForgeSlotsChanged(__instance);
                    ModEntry.DiagVerbose("[Test] Forge swap: " + carriedAny.Name + " ↔ " + leftExisting.Name + " in left slot");
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }

                // Forge RIGHT ingredient slot.
                if (__instance.rightIngredientSpot.containsPoint(x, y))
                {
                    Item? rightExisting = __instance.rightIngredientSpot.item;
                    Item? leftItem = __instance.leftIngredientSpot.item;

                    // Prismatic Shard and Dragon Tooth are tool/weapon-only — refuse the
                    // drop if forge.left holds a Ring.
                    if (leftItem is Ring
                        && (carriedAny.QualifiedItemId == "(O)74"
                            || carriedAny.QualifiedItemId == "(O)852"))
                    {
                        ModEntry.DiagVerbose("[Test] Forge drop blocked: " + carriedAny.Name + " doesn't combine with ring");
                        return false;
                    }
                    
                    // Vanilla allows ANY valid forge ingredient in forge.right when forge.left
                    // is empty.  Only enforce the IsValidCraft pairing check when forge.left
                    // is already occupied.
                    bool acceptsInRight;
                    if (leftItem == null)
                    {
                        acceptsInRight = IsValidForgeItem(__instance, carriedAny);
                    }
                    else
                    {
                        acceptsInRight = IsValidCraft(__instance, leftItem, carriedAny);

                        // Extra rule: if left is a Tool and the right item would produce
                        // no enchantment / no change (e.g. Prismatic Shard on a tool with
                        // no available enchantments), refuse — otherwise the craft would
                        // consume the items with no result.
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
                        ModEntry.DiagVerbose("[Test] Forge drop accepted: " + carriedAny.Name + " → right slot");
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }

                    // Swap.
                    __instance.rightIngredientSpot.item = carriedAny;
                    Game1.player.CursorSlotItem = rightExisting;
                    NotifyForgeSlotsChanged(__instance);
                    ModEntry.DiagVerbose("[Test] Forge swap: " + carriedAny.Name + " ↔ " + rightExisting.Name + " in right slot");
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }
            }

            if (Game1.player.CursorSlotItem is Ring)
                return HandleCursorRingDrop(__instance, x, y, playSound);

            if (Game1.player.CursorSlotItem == null)
                return HandleEmptyCursorPickup(__instance, x, y, playSound);

            // Let vanilla handle anything else.
            return true;
        }

        /// <summary>Right-click handling.  Vanilla's path (MenuWithInventory.receiveRightClick)
        /// only knows about its private _heldItem: with a ring on our CursorSlotItem carry, it
        /// would happily lift a second item onto _heldItem — the dual-carry mix-and-match state
        /// every guard in <see cref="LeftClick_Prefix"/> exists to prevent.  Instead,
        /// right-click is the forge's "smart send":
        ///   - rings (panel slots, equipped Ring1/Ring2, or inventory) go to the first free,
        ///     valid forge ingredient slot;
        ///   - inventory tools/weapons go to the left slot — or the right slot for a
        ///     same-type weapon when the left holds a matching one (appearance copy);
        ///   - forge ingredients (gems, Diamond, Prismatic Shard, Dragon Tooth, Galaxy
        ///     Soul) go to the right slot only;
        ///   - a filled forge slot sends its content back: rings to the panel, then an
        ///     empty Ring1/Ring2, then inventory; everything else to the inventory.
        /// Works on controller via the X button (vanilla maps gamepad X to
        /// receiveRightClick in menus, Game1.updateActiveMenu).</summary>
        public static bool RightClick_Prefix(ForgeMenu __instance, int x, int y, bool playSound)
        {
            // While our cursor carry is occupied, block vanilla's right-click path entirely.
            if (Game1.player.CursorSlotItem != null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click blocked: cursor carry occupied");
                return false;
            }

            // Right-click an equipped vanilla ring (Ring1/Ring2) → send it straight into
            // the first free, valid forge ingredient slot.  Works with the panel open or
            // closed: one consistent rule at the forge — right-click = send to forge.
            var leftRingIcon  = __instance.equipmentIcons.Find(c => c.name == "Ring1");
            var rightRingIcon = __instance.equipmentIcons.Find(c => c.name == "Ring2");
            bool clickedLeft  = leftRingIcon  != null && leftRingIcon.containsPoint(x, y);
            bool clickedRight = rightRingIcon != null && rightRingIcon.containsPoint(x, y);
            if (clickedLeft || clickedRight)
            {
                Ring? equipped = clickedLeft ? Game1.player.leftRing.Value : Game1.player.rightRing.Value;
                if (equipped == null)
                    return false; // empty slot: nothing to send (vanilla does nothing here either)

                // Same gating as left-click pickups: dimmed rings stay put.
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
                    + " → forge " + (ReferenceEquals(equippedDest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
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
                // Right-click a panel slot → send the ring straight into the first free,
                // valid forge ingredient slot (left first, then right): one-click
                // combining, which is what the panel is for at the forge.  (Unequipping
                // to inventory stays an inventory-page-only action.)
                foreach (var slot in Slots)
                {
                    if (!slot.containsPoint(x, y)) continue;
                    int idx = SlotIndex(slot);
                    if (idx < 0 || idx >= RingSlotManager.Slots.Count) return true;

                    Ring? ring = RingSlotManager.Slots[idx];
                    if (ring == null) return false;

                    // Same gating as left-click pickups: dimmed rings stay put.
                    if (!IsRingAllowedInForgeContext(ring, __instance)
                        || IsRingBlockedByDuplicateCap(ring, __instance))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send blocked: " + ring.Name + " dimmed");
                        return false;
                    }

                    var dest = GetFreeForgeSlotFor(__instance, ring);
                    if (dest == null)
                    {
                        // Both forge slots occupied (or the pairing would be invalid) — no-op.
                        ModEntry.DiagVerbose("[Test] Forge right-click send: no free/valid forge slot for " + ring.Name);
                        return false;
                    }

                    ModEntry.DiagVerbose(
                        "[Test] Forge right-click send " + ring.Name + " from panel slot " + idx
                        + " → forge " + (ReferenceEquals(dest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
                    RingSlotManager.Equip(idx, null);
                    dest.item = ring;
                    NotifyForgeSlotsChanged(__instance);
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }
            }

            // Right-click a filled forge ingredient slot → send its content back where it
            // belongs: rings via ReturnRingFromForge (panel → Ring1/Ring2 → inventory);
            // tools, weapons, and forge ingredients straight back to the inventory.
            foreach (var spot in new[] { __instance.leftIngredientSpot, __instance.rightIngredientSpot })
            {
                if (!spot.containsPoint(x, y) || spot.item == null)
                    continue;

                if (spot.item is Ring forgeRing)
                {
                    if (ReturnRingFromForge(__instance, forgeRing, playSound))
                    {
                        spot.item = null;
                        NotifyForgeSlotsChanged(__instance);
                    }
                    return false;
                }

                // addItemToInventory can place part of a stack; whatever didn't fit stays
                // in the forge slot.
                int stackBefore = spot.item.Stack;
                var slotLeftover = Game1.player.addItemToInventory(spot.item);
                if (slotLeftover == null)
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click return " + spot.item.Name + " → inventory");
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

            // Inventory right-click routing: forge-relevant items are sent to their slot
            // instead of vanilla's pick-up-one-onto-_heldItem (which our carry guards
            // would fight anyway).  Anything else falls through to vanilla.
            int invIdx = __instance.inventory.getInventoryPositionOfClick(x, y);
            if (invIdx >= 0 && invIdx < __instance.inventory.actualInventory.Count
                && __instance.inventory.actualInventory[invIdx] is { } invItem)
            {
                // Rings → first free, valid forge slot (same rule as panel rings).
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
                        "[Test] Forge right-click send " + invRing.Name + " from inventory → forge "
                        + (ReferenceEquals(invRingDest, __instance.leftIngredientSpot) ? "left" : "right") + " slot");
                    __instance.inventory.actualInventory[invIdx] = null;
                    invRingDest.item = invRing;
                    NotifyForgeSlotsChanged(__instance);
                    if (playSound) Game1.playSound("stoneStep");
                    return false;
                }

                // Weapons/tools → the left slot — except a same-type weapon, which goes to
                // the RIGHT slot when the left already holds a matching weapon (vanilla's
                // appearance-copy forge).
                if (invItem is Tool invTool)
                {
                    if (invTool is StardewValley.Tools.MeleeWeapon invWeapon
                        && __instance.rightIngredientSpot.item == null
                        && __instance.leftIngredientSpot.item is StardewValley.Tools.MeleeWeapon leftWeapon
                        && CanRightItemEnchantTool(leftWeapon, invWeapon))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invWeapon.Name + " → forge right slot (appearance copy)");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.rightIngredientSpot.item = invWeapon;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    if (__instance.leftIngredientSpot.item == null
                        && IsAcceptedInLeftSlot(__instance, invTool))
                    {
                        // Honor the verdict the highlighting shows: with an ingredient in
                        // the right slot, only send a tool that forms a valid, non-no-op
                        // craft with it.  A weapon at its forge cap is dimmed against a
                        // gem — right-click must refuse it too, like it does currencies.
                        if (__instance.rightIngredientSpot.item is { } rightOccupant
                            && !IsValidCraft(__instance, invTool, rightOccupant))
                        {
                            ModEntry.DiagVerbose(
                                "[Test] Forge right-click send blocked: " + invTool.Name
                                + " dimmed (no valid craft with " + rightOccupant.Name + ")");
                            return false;
                        }
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invTool.Name + " → forge left slot");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.leftIngredientSpot.item = invTool;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose("[Test] Forge right-click send: no destination for " + invTool.Name);
                    return false;
                }

                // Forge ingredients (gems, Diamond, Prismatic Shard, Dragon Tooth, Galaxy
                // Soul) → the RIGHT slot only.  The whole stack moves; crafting consumes
                // one per craft as usual.
                if (IsValidForgeItem(__instance, invItem))
                {
                    if (__instance.rightIngredientSpot.item == null
                        && IsAcceptedInRightSlot(__instance, invItem))
                    {
                        ModEntry.DiagVerbose("[Test] Forge right-click send " + invItem.Name + " → forge right slot");
                        __instance.inventory.actualInventory[invIdx] = null;
                        __instance.rightIngredientSpot.item = invItem;
                        NotifyForgeSlotsChanged(__instance);
                        if (playSound) Game1.playSound("stoneStep");
                        return false;
                    }
                    ModEntry.DiagVerbose("[Test] Forge right-click send: right slot unavailable/invalid for " + invItem.Name);
                    return false;
                }

                // Not forge-relevant: vanilla right-click (stack pickup etc.).
            }

            return true;
        }

        /// <summary>True if the item is something the forge would accept (ring, weapon, tool,
        /// or any item HighlightItems would mark valid like prismatic shards / gems / dragon tooth).</summary>
        private static bool IsValidForgeItem(ForgeMenu menu, Item item)
        {
            if (item == null) return false;
            if (item is Ring) return true;
            if (item is StardewValley.Tools.MeleeWeapon weapon) return Patches.IsScytheForgingAllowed(weapon);
            if (item is StardewValley.Tools.Slingshot) return true;
            if (item is Tool) return true;

            // Gems → forge enchantments (Ruby/Emerald/Topaz/Aquamarine/Jade/Amethyst), and
            // Diamond → random forge enchantment.
            if (StardewValley.Enchantments.BaseEnchantment.GetEnchantmentFromItem(null, item) != null)
                return true;

            // Prismatic Shard → adds a secondary (innate) enchantment chosen from the
            // tool's available list.
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

            // Dragon Tooth: re-rolls (or, with DragonToothStacking, adds to) the innate
            // stat enchantments on a MeleeWeapon.  For non-Galaxy weapons below level 15,
            // vanilla handles this.  For Galaxy/Infinity weapons (and all weapons when
            // stacking/always-max are on) the Harmony prefix
            // (Tool_Forge_DragonToothReroll_Prefix) takes over.
            if (rightItem.QualifiedItemId == "(O)852")
            {
                if (leftTool is not StardewValley.Tools.MeleeWeapon weapon) return false;
                if (!Patches.IsScytheForgingAllowed(weapon)) return false;
                // Stacking mode: once every innate type is at its per-type cap the craft
                // is a no-op — dim/refuse it.  Re-roll mode is always meaningful.
                if (ModEntry.Instance.Config.DragonToothStacking
                    && !Patches.HasInnateBelowMax(weapon))
                {
                    TestLogOnce("CanRightItemEnchantTool: Dragon Tooth refused on " + weapon.Name + " (every innate type at cap, stacking mode)");
                    return false;
                }
                return true;
            }

            // Diamond: weapon-only.  Gems boost weapon stats (damage/defense/crit/speed)
            // and have no effect on Pickaxe/Hoe/Axe/Pan/Rod/Watering Can.
            if (rightItem.QualifiedItemId == "(O)72")
            {
                if (leftTool is not StardewValley.Tools.MeleeWeapon scytheCheck)
                    return false;
                if (!Patches.IsScytheForgingAllowed(scytheCheck))
                    return false;
                
                // Respect the forge-level cap: WeaponForgingCap controls GetMaxForges()
                // (default -1 = unlimited, otherwise the configured cap).  A Diamond
                // craft at the cap is a no-op (consumes the diamond + shards, adds
                // nothing).  Refuse regardless of RemoveDiamondForgesCap.
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

            // Gems (Ruby/Emerald/Topaz/Aquamarine/Jade/Amethyst): weapon-only.  Same
            // reasoning as Diamond — these are weapon-stat enchantments.
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

        // ============================================================
        //  Toggle
        // ============================================================

        public static void TogglePanel(bool playSound)
        {
            _panelOpen = !_panelOpen;
            ModEntry.DiagVerbose("[Test] Forge panel toggled: open=" + _panelOpen);
            ApplyPanelVisibility();
            if (playSound) Game1.playSound(_panelOpen ? "bigSelect" : "bigDeSelect");

            if (Game1.activeClickableMenu is ForgeMenu menu)
            {
                menu.populateClickableComponentList();

                if (Game1.options.SnappyMenus && ToggleButton != null)
                {
                    if (_panelOpen && Slots.Count > 0)
                    {
                        int target = System.Math.Min(3, Slots.Count - 1);
                        menu.setCurrentlySnappedComponentTo(Slots[target].myID);
                    }
                    else
                    {
                        menu.setCurrentlySnappedComponentTo(ToggleButton.myID);
                    }
                    menu.snapCursorToCurrentSnappedComponent();
                }
            }
        }

        // ============================================================
        //  Hover
        // ============================================================

        /// <summary>Harmony postfix for ForgeMenu.performHoverAction that handles viewport changes
        /// and updates hover text for custom UI elements.
        /// Rebuilds slot positions when the viewport dimensions change and manages hover states
        /// for the toggle button and extra ring slots.</summary>
        /// <param name="__instance">The ForgeMenu instance being hovered over.</param>
        /// <param name="x">The x-coordinate of the mouse cursor.</param>
        /// <param name="y">The y-coordinate of the mouse cursor.</param>
        public static void Hover_Postfix(ForgeMenu __instance, int x, int y)
        {
            // Rebuild the panel on viewport change. Unlike the inventory page's handler,
            // we don't need to save/restore _panelOpen (no _userPanelOpen): this handler
            // never mutates _panelOpen, so RebuildSlots -> ApplyPanelVisibility reads the
            // user's real open state directly.
            var vp = Game1.uiViewport;
            if (vp.Width != _lastVpW || vp.Height != _lastVpH)
            {
                _lastVpW = vp.Width;
                _lastVpH = vp.Height;
                __instance.equipmentIcons.RemoveAll(c =>
                    c.name.StartsWith("ExtraRing") || c.name == "ExtraRingToggle");
                RebuildSlots(__instance);
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

        // Track _heldItem after each click to see what vanilla did with it.
        public static void LeftClick_Postfix(ForgeMenu __instance, int x, int y)
        {
            var held = GetHeldItem(__instance);
            ModEntry.DiagVerbose($"[Forge] *Postfix* click at ({x},{y})  cursor={Game1.player.CursorSlotItem?.Name ?? "null"}  forge.held={held?.Name ?? "null"}  forge.left={__instance.leftIngredientSpot.item?.Name ?? "null"}  forge.right={__instance.rightIngredientSpot.item?.Name ?? "null"}");
        }

        // Track forge ingredient slot transitions to detect forge completion.
        // (Trackers live in PanelState so each split-screen player detects their own.)
        public static void Update_Postfix(ForgeMenu __instance)
        {
            // Everything below is diagnostics (forge-completion traces).  Skip the whole
            // body — including the per-tick held-item reflection and the CombinedRing
            // name materialization — unless verbose logging is on.
            if (!ModEntry.Instance.Config.VerboseLogging) return;

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

            // Detect forge completion: both ingredient slots transition from
            // non-null to null in the same frame (the forge consumed them).
            // Manual pickups change only one slot at a time.
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

            // Save slot state for next frame's forge detection.
            _lastLeftWasRing = leftItem is Ring;
            if (leftItem is CombinedRing leftCr)
                _preForgeCombinedRingNames = leftCr.combinedRings.Select(r => r.Name).ToList();
            else
                _preForgeCombinedRingNames = null;

            _lastUpdateLeftName = leftName;
            _lastUpdateRightName = rightName;
        }
        // ============================================================
        //  Helpers
        // ============================================================

        /// <summary>Returns true if <paramref name="ring"/> is NOT dimmed — i.e. it
        /// passes the current forge-context checks (non-ring-in-slot guard, vanilla
        /// IsValidCraft when InfiniteCombining is off, duplicate cap when
        /// InfiniteCombining + AddCombinedDuplicateRingCap are on).
        /// Mirrors the dimming logic in <see cref="Draw_Postfix"/>.</summary>
        private static bool IsRingAllowedInForgeContext(Ring ring, ForgeMenu menu)
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

        /// <summary>Revalidate this screen's dim memo against the current ingredient pair
        /// (reference identity), clearing it when the pair changed, and refresh the
        /// pair-level duplicate flag.  Returns the state for further lookups.</summary>
        private static PanelState RevalidateDimMemo(ForgeMenu menu)
        {
            var st = StatePerScreen.Value;
            Item? left = menu.leftIngredientSpot.item;
            Item? right = menu.rightIngredientSpot.item;
            if (!ReferenceEquals(left, st.DimLeft) || !ReferenceEquals(right, st.DimRight))
            {
                st.DimAllowed.Clear();
                st.DimLeft = left;
                st.DimRight = right;
                st.PairDuplicate = left is Ring leftRing && right is Ring rightRing
                    && Patches.WouldCreateDuplicateRing(leftRing, rightRing);
            }
            return st;
        }

        /// <summary>Memoized <see cref="IsRingAllowedInForgeContext"/> for the draw path,
        /// which asks once per panel ring per frame.  The underlying checks probe
        /// CanForge/CanCombine and walk CombinedRing trees — recomputing those 60×/s per
        /// slot is the waste this avoids.  Click handlers keep calling the uncached
        /// helper directly.</summary>
        private static bool IsRingAllowedCached(Ring ring, ForgeMenu menu)
        {
            var st = RevalidateDimMemo(menu);
            if (!st.DimAllowed.TryGetValue(ring, out bool allowed))
                st.DimAllowed[ring] = allowed = IsRingAllowedInForgeContext(ring, menu);
            return allowed;
        }

        /// <summary>Drop the dim memos on every screen.  Call when something outside the
        /// ingredient-pair key can change the verdicts — config edits, menu construction,
        /// returning to title.</summary>
        internal static void InvalidateDimCache()
        {
            foreach (var pair in StatePerScreen.GetActiveValues())
            {
                pair.Value.DimAllowed.Clear();
                pair.Value.DimLeft = null;
                pair.Value.DimRight = null;
                pair.Value.PairDuplicate = false;
            }
        }

        /// <summary>Session cleanup on return to title: the dim memo holds Ring references
        /// from the closed save, and the log-dedup set is save-scoped noise.</summary>
        internal static void ClearSessionCaches()
        {
            InvalidateDimCache();
            _testLogOnce.Clear();
        }

        /// <summary>Vanilla's left-slot rule (ForgeMenu._leftIngredientSpotClicked): any
        /// Tool or Ring, gated by IsValidCraftIngredient (only restrictive for untrashable
        /// items without available enchantments).  There is NO upgrade-level requirement —
        /// base tools are enchantable.  Scythes additionally need a scythe-forging mod.</summary>
        private static bool IsAcceptedInLeftSlot(ForgeMenu menu, Item item) =>
            menu.IsValidCraftIngredient(item)
            && (item is Ring
                || (item is StardewValley.Tools.MeleeWeapon mw
                    ? Patches.IsScytheForgingAllowed(mw)
                    : item is Tool));

        /// <summary>Whether the item would be accepted in the right ingredient slot given
        /// the current left item: any forge ingredient when the left is empty, otherwise a
        /// valid (and non-no-op) pairing with the left item.</summary>
        private static bool IsAcceptedInRightSlot(ForgeMenu menu, Item item)
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

        /// <summary>Route a ring taken out of a forge slot back where it belongs: the
        /// first empty panel slot (when the panel is open), else an empty vanilla ring
        /// slot (Ring1 then Ring2), else the inventory.  Returns false only when
        /// everything is full — the ring should then stay in the forge slot.</summary>
        private static bool ReturnRingFromForge(ForgeMenu menu, Ring ring, bool playSound)
        {
            if (_panelOpen)
            {
                int firstEmpty = RingSlotManager.Slots.FindIndex(r => r == null);
                if (firstEmpty >= 0)
                {
                    ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " → panel slot " + firstEmpty);
                    RingSlotManager.Equip(firstEmpty, ring);
                    if (playSound) Game1.playSound("crit");
                    return true;
                }
            }
            if (Game1.player.leftRing.Value == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " → Ring1");
                Game1.player.leftRing.Value = ring;
                ring.onEquip(Game1.player);
                Game1.player.buffs.Dirty = true;
                if (playSound) Game1.playSound("crit");
                return true;
            }
            if (Game1.player.rightRing.Value == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " → Ring2");
                Game1.player.rightRing.Value = ring;
                ring.onEquip(Game1.player);
                Game1.player.buffs.Dirty = true;
                if (playSound) Game1.playSound("crit");
                return true;
            }
            if (Game1.player.addItemToInventory(ring) == null)
            {
                ModEntry.DiagVerbose("[Test] Forge right-click return " + ring.Name + " → inventory");
                if (playSound) Game1.playSound("coin");
                return true;
            }
            ModEntry.DiagVerbose("[Test] Forge right-click return: everywhere full, keeping " + ring.Name + " in forge slot");
            return false;
        }

        /// <summary>The first free forge ingredient slot that would form a valid pairing
        /// with <paramref name="ring"/> — left slot first, then right — or null when both
        /// are occupied or no valid placement exists.  Used by the right-click
        /// "send to forge" actions for panel, equipped, and inventory rings.</summary>
        private static ClickableTextureComponent? GetFreeForgeSlotFor(ForgeMenu menu, Ring ring)
        {
            Item? left = menu.leftIngredientSpot.item;
            Item? right = menu.rightIngredientSpot.item;
            if (left == null && (right == null || IsValidCraft(menu, ring, right)))
                return menu.leftIngredientSpot;
            if (right == null && (left == null || IsValidCraft(menu, left, ring)))
                return menu.rightIngredientSpot;
            return null;
        }

        private static bool IsRingBlockedByDuplicateCap(Ring ring, ForgeMenu menu)
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

        /// <summary>Handles left-click while the cursor is carrying a Ring.  Attempts
        /// to drop/swap at equipment icons, forge ingredient slots, inventory, or
        /// panel slots.  Returns false (skip vanilla) in all cases — the cursor
        /// ring should never reach vanilla's click handler.</summary>
        private static bool HandleCursorRingDrop(ForgeMenu menu, int x, int y, bool playSound)
        {
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
                ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " → left ring equipment slot");
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
                ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " → right ring equipment slot");
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
                    // If the swap would lift a dimmed ring onto the cursor, block it.
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
                    ModEntry.DiagVerbose("[Test] Forge cursor drop: " + carriedRing.Name + " → panel slot " + idx);
                    RingSlotManager.Equip(idx, carriedRing);
                    Game1.player.CursorSlotItem = slotRing;
                    if (playSound) Game1.playSound("crit");
                    return false;
                }
            }

            ModEntry.DiagVerbose($"[Forge]   -> no target found; blocking click while carrying ring");
            return false;
        }

        /// <summary>Handles left-click with an empty cursor.  Attempts to pick up
        /// rings from equipment icons, forge ingredient slots, inventory, and panel
        /// slots.</summary>
        /// <param name="menu">The ForgeMenu instance being clicked.</param>
        /// <param name="x">The x-coordinate of the click position.</param>
        /// <param name="y">The y-coordinate of the click position.</param>
        /// <param name="playSound">Whether to play a sound when picking up an item.</param>
        /// <return>False when the method handles the pickup (skip vanilla processing), or true
        /// to let vanilla process the click.</return>
        private static bool HandleEmptyCursorPickup(ForgeMenu menu, int x, int y, bool playSound)
        {
            // Vanilla Ring1/Ring2 equipped-ring icons → pick up to cursor.
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

            // Panel-open-dependent fallbacks: forge slots, inventory, panel.
            if (!_panelOpen)
            {
                // When the duplicate cap is active and a ring is in a forge slot,
                // intercept inventory clicks to block picking up blocked rings even
                // with panel closed — vanilla has no knowledge of the duplicate cap.
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

            // Forge LEFT ingredient slot → pick up.
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

            // Forge RIGHT ingredient slot → pick up.
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

            // Inventory ring → pick up.
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

            // Panel slot → pick up.
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

        /// <summary>
        /// Converts a ClickableComponent's name to its corresponding slot index by parsing the numeric portion of the name.
        /// </summary>
        /// <param name="c">The ClickableComponent whose name should be parsed to extract the slot index.</param>
        /// <return>The zero-based slot index if the name contains a valid integer after removing the "ExtraRing" prefix, otherwise -1.</return>
        private static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;

        /// <summary>
        /// Retrieves the currently held item from the ForgeMenu using reflection to access the private _heldItem field.
        /// </summary>
        /// <param name="menu">The ForgeMenu instance to get the held item from.</param>
        /// <return>The held Item if accessible, otherwise null.</return>
        internal static Item? GetHeldItem(ForgeMenu menu) =>
            HeldItemField?.GetValue(menu) as Item;

        /// <summary>Sets the held item in the forge menu by updating the private _heldItem field via reflection.</summary>
        /// <param name="menu">The forge menu instance to modify.</param>
        /// <param name="item">The item to set as held, or null to clear the held item.</param>
        private static void SetHeldItem(ForgeMenu menu, Item? item) =>
            HeldItemField?.SetValue(menu, item);

        /// <summary>Run vanilla's post-slot-change bookkeeping after we mutate the forge
        /// ingredient slots directly.  Vanilla's own _left/_rightIngredientSpotClicked do
        /// this for their changes: drop the cached highlight dictionary (its regeneration
        /// probes IsValidCraft, which is what lets Forge Menu Choice notice a broken pair
        /// and close its carousel) and re-validate the craft state.  With BOTH slots empty
        /// no probes fire at all, so any stale carousel is closed explicitly.</summary>
        private static void NotifyForgeSlotsChanged(ForgeMenu menu)
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

        /// <summary>
        /// Side-effect-free craft validity: vanilla ForgeMenu.IsValidCraft's logic (mirrored
        /// below) plus this mod's overrides — WITHOUT calling the patched method.  Our panel
        /// re-evaluates highlighting for every inventory item every frame; routing those
        /// probes through the real IsValidCraft would fire other mods' stateful patches on it.
        /// Forge Menu Choice in particular opens/closes its enchantment-selection carousel in
        /// an IsValidCraft prefix and trashes it on any call that isn't tool+prismatic, so our
        /// per-frame probing destroyed the player's selection continuously.  CanForge and
        /// CanCombine are still invoked virtually, so mods patching those deeper hooks keep
        /// working.  The real craft path (the game's own IsValidCraft calls) is unaffected and
        /// still runs every mod's patches, including our postfix.
        /// </summary>
        /// <param name="menu">The forge menu (kept for call-site compatibility; the
        /// evaluation itself only depends on the two items).</param>
        /// <param name="left">The prospective left (tool/ring) item.</param>
        /// <param name="right">The prospective right (ingredient) item.</param>
        /// <return>True if the two items would form a valid forge combination.</return>
        internal static bool IsValidCraft(ForgeMenu menu, Item? left, Item? right)
        {
            // Mirror of vanilla ForgeMenu.IsValidCraft (null check, Tool.CanForge,
            // Ring.CanCombine) — small and stable, and the heavy lifting lives in the
            // virtual CanForge/CanCombine calls.
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