using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace MultiRingInfiniteForging
{
    /// <summary>Shared scaffolding for the two collapsible extra-ring panels
    /// (<see cref="InventoryRingPanel"/>, <see cref="ForgeRingPanel"/>): the slot/toggle/scroll
    /// state and the genuinely menu-agnostic behavior (scroll handling, the scrollbar + arrow
    /// draw, controller snap-to-visible-slot, and the hover scale/tooltip pass).
    ///
    /// The menu-specific parts stay in the subclasses: where the panel anchors, the grid
    /// geometry and controller-neighbor wiring (RebuildSlots/ApplyPanelVisibility), the slot/
    /// toggle styling, and the click handling.  Field names here match the forge panel so that
    /// side needs no renames.</summary>
    internal abstract class RingSlotPanel
    {
        protected const int SlotSize = 64;
        protected const int SlotSpacing = 4;
        protected const int ScrollBtnWidth = 40;
        protected const int ScrollBtnHeight = 44;
        protected const int ScrollBarWidth = 6;
        protected const int ScrollBarGap = 4;

        /// <summary>Component-ID bases, kept distinct per panel so the two never collide.</summary>
        protected abstract int FirstSlotId { get; }
        protected abstract int ToggleButtonId { get; }
        protected abstract int ScrollUpBtnId { get; }
        protected abstract int ScrollDownBtnId { get; }

        protected readonly List<ClickableComponent> Slots = new();
        protected ClickableTextureComponent? ToggleButton;
        protected ClickableComponent? _scrollUpBtn;
        protected ClickableComponent? _scrollDownBtn;
        protected bool _panelOpen;
        public bool PanelOpen => _panelOpen;
        protected int _scrollOffset;
        protected int _maxScrollOffset;
        protected int _visibleRows;
        protected int _lastVpW = -1;
        protected int _lastVpH = -1;
        protected string _hoverText = "";

        /// <summary>The menu that owns controller snapping for this panel (the InventoryPage or
        /// the ForgeMenu).</summary>
        protected abstract IClickableMenu SnapMenu { get; }

        /// <summary>The on-screen bounding box of the visible slot grid (each panel computes it
        /// its own way), used to gate mouse-wheel scrolling.</summary>
        protected abstract Rectangle PanelBounds();

        /// <summary>Re-layout after a scroll/offset change: re-run the panel's
        /// ApplyPanelVisibility, refresh the host menu's clickable-component list, and keep the
        /// controller snap on a visible slot.</summary>
        protected abstract void AfterScrollChange();

        // Scrollbar/arrow tints (forge overrides; inventory uses the menu defaults).
        protected virtual Color ScrollTrackColor => new Color(60, 60, 60, 180);
        protected virtual Color ScrollThumbColor => Color.White * 0.9f;
        protected virtual Color ScrollArrowColor => Color.White;

        protected static int SlotIndex(ClickableComponent c) =>
            int.TryParse(c.name.Replace("ExtraRing", ""), out var i) ? i : -1;

        /// <summary>Mouse-wheel handler: scroll the grid when the cursor is over the panel.
        /// Returns true to let the wheel fall through to vanilla, false when handled.</summary>
        public bool HandleScroll(int direction)
        {
            if (!_panelOpen || ToggleButton == null || _maxScrollOffset <= 0) return true;

            var mousePos = Game1.getMousePosition();
            var panelBounds = PanelBounds();
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

        /// <summary>Keep the controller snap on a visible extra-ring slot after a scroll: if the
        /// snapped slot scrolled off (parked at -9999), move snap to the first visible one.</summary>
        protected void SnapToVisibleSlot()
        {
            if (!Game1.options.SnappyMenus) return;
            var menu = SnapMenu;
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

        /// <summary>Draw the scroll track, thumb, and up/down arrows for the open panel.  Colors
        /// come from the (overridable) scrollbar tint properties.</summary>
        protected void DrawScrollbarAndArrows(SpriteBatch b)
        {
            if (_scrollUpBtn != null && _scrollDownBtn != null && _maxScrollOffset > 0)
            {
                int trackX = _scrollUpBtn.bounds.X + (ScrollBtnWidth - ScrollBarWidth) / 2;
                int trackY = _scrollUpBtn.bounds.Bottom;
                int trackH = _scrollDownBtn.bounds.Y - trackY;
                b.Draw(Game1.staminaRect,
                    new Rectangle(trackX, trackY, ScrollBarWidth, trackH),
                    ScrollTrackColor);

                int totalRows = _maxScrollOffset + _visibleRows;
                int thumbH = System.Math.Max(ScrollBarWidth * 2,
                    trackH * _visibleRows / totalRows);
                int thumbY = trackY +
                    (trackH - thumbH) * _scrollOffset / _maxScrollOffset;
                b.Draw(Game1.staminaRect,
                    new Rectangle(trackX, thumbY, ScrollBarWidth, thumbH),
                    ScrollThumbColor);
            }

            if (_scrollUpBtn != null && _scrollOffset > 0)
            {
                Color arrowCol = _scrollUpBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                    ? Color.Gold : ScrollArrowColor;
                Utility.drawWithShadow(b, Game1.mouseCursors,
                    new Vector2(_scrollUpBtn.bounds.X, _scrollUpBtn.bounds.Y),
                    new Rectangle(76, 72, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
            }
            if (_scrollDownBtn != null && _scrollOffset < _maxScrollOffset)
            {
                Color arrowCol = _scrollDownBtn.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY())
                    ? Color.Gold : ScrollArrowColor;
                Utility.drawWithShadow(b, Game1.mouseCursors,
                    new Vector2(_scrollDownBtn.bounds.X, _scrollDownBtn.bounds.Y),
                    new Rectangle(12, 76, 40, 44), arrowCol, 0f, Vector2.Zero, 1f);
            }
        }

        /// <summary>Per-frame hover pass: set the toggle/slot tooltip text and ease the hovered
        /// slot's scale up (others back down).  Called by each panel's hover handler after its
        /// own viewport-change rebuild.</summary>
        protected void UpdateHoverAndScale(int x, int y)
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
    }
}
