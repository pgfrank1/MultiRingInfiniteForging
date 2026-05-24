using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace MultiRingInfiniteForging
{
    public class ModEntry : Mod
    {
        public static ModEntry Instance = null!;
        public ModConfig Config = null!;

        public const string SaveKey = "ExtraRings";

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            var harmony = new Harmony(ModManifest.UniqueID);
            Patches.ApplyAll(harmony, Monitor);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            GenericModConfigMenuIntegration.Register(
                mod: this,
                config: Config,
                save: () => Helper.WriteConfig(Config));
        }

        /// <summary>If the extra-ring panel is open and the player presses B (cancel) on the
        /// controller, close just the panel instead of exiting the inventory menu.</summary>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.ControllerB) return;
            if (!InventoryPagePatches.IsPanelOpen) return;

            // Only intercept while the inventory tab of the GameMenu is active.
            if (Game1.activeClickableMenu is not GameMenu gm) return;
            if (gm.currentTab != GameMenu.inventoryTab) return;

            // Don't intercept if the player is dragging an item with the cursor —
            // they probably want to dump it back in inventory by closing the menu.
            if (Game1.player.CursorSlotItem != null) return;

            InventoryPagePatches.TogglePanel(playSound: true);
            Helper.Input.Suppress(e.Button);
        }

        // ---------- save/load ----------

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            RingSlotManager.Load(Helper);
            RingSlotManager.ApplyAllEffects();
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            RingSlotManager.RemoveAllEffects();
            RingSlotManager.Save(Helper);
        }

        private void OnDayEnding(object? sender, DayEndingEventArgs e)
        {
            RingSlotManager.Save(Helper);
        }
    }
}