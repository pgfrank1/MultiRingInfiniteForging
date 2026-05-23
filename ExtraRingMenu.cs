using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>A tiny menu listing the extra ring slots and letting the player equip/unequip.</summary>
    public class ExtraRingMenu : IClickableMenu
    {
        private readonly List<ClickableComponent> _slots = new();
        private const int SlotSize = 64 + 16;

        public ExtraRingMenu()
            : base(
                Game1.uiViewport.Width / 2 - 200,
                Game1.uiViewport.Height / 2 - 150,
                400, 300, showUpperRightCloseButton: true)
        {
            RingSlotManager.EnsureSize();
            for (int i = 0; i < RingSlotManager.SlotCount; i++)
            {
                _slots.Add(new ClickableComponent(
                    new Rectangle(xPositionOnScreen + 32 + (i % 5) * SlotSize,
                                  yPositionOnScreen + 80 + (i / 5) * SlotSize,
                                  64, 64),
                    name: i.ToString()));
            }
        }

        public static void Toggle()
        {
            if (Game1.activeClickableMenu is ExtraRingMenu)
                Game1.activeClickableMenu = null;
            else if (Game1.activeClickableMenu == null)
                Game1.activeClickableMenu = new ExtraRingMenu();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].containsPoint(x, y)) continue;

                var held = Game1.player.CursorSlotItem as Ring;
                var current = RingSlotManager.Slots[i];

                RingSlotManager.Equip(i, held);
                Game1.player.CursorSlotItem = current;

                if (playSound) Game1.playSound("smallSelect");
                return;
            }
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].containsPoint(x, y)) continue;
                var ring = RingSlotManager.Slots[i];
                if (ring == null) return;

                RingSlotManager.Equip(i, null);
                Game1.player.addItemToInventory(ring);
                if (playSound) Game1.playSound("coin");
                return;
            }
        }

        public override void draw(SpriteBatch b)
        {
            // Dim background
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * 0.4f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height,
                speaker: false, drawOnlyBox: true);

            Utility.drawTextWithShadow(b, "Extra Ring Slots",
                Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 32),
                Game1.textColor);

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                b.Draw(Game1.menuTexture, slot.bounds,
                    Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                RingSlotManager.Slots[i]?.drawInMenu(b,
                    new Vector2(slot.bounds.X, slot.bounds.Y),
                    1f, 1f, 0.86f, StackDrawType.Hide);
            }

            base.draw(b);
            drawMouse(b);
        }
    }
}