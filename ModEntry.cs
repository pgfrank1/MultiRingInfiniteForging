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
        
        /// <summary>Convenience: shorthand for translating a key.</summary>
        public static string T(string key) => Instance.Helper.Translation.Get(key);

        public const string SaveKey = "ExtraRings";

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTick;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            var harmony = new Harmony(ModManifest.UniqueID);
            Patches.ApplyAll(harmony, Monitor);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // Wire GMCM if it's installed; otherwise this is a no-op.
            GenericModConfigMenuIntegration.Register(
                mod: this,
                config: Config,
                save: () => Helper.WriteConfig(Config));
        }
        
        /// <summary>Periodic diagnostic snapshot of the player's combined buff state.
        /// Used to verify that extra-slot rings are flowing through AddEquipmentEffects.</summary>
        private void OnOneSecondTick(object? sender, StardewModdingAPI.Events.OneSecondUpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            int extra = 0;
            foreach (var r in RingSlotManager.Slots)
                if (r != null) extra++;
            if (extra == 0) return; // nothing to log about

            Monitor.Log(
                $"[Diag] tick snapshot: MagneticRadius={Game1.player.MagneticRadius} " +
                $"Immunity={Game1.player.Immunity} buffs.Dirty already-recomputed " +
                $"(extra rings={extra})",
                LogLevel.Trace);
        }

        /// <summary>If an extra-ring panel is open and the player presses B (cancel) on the
        /// controller, close just the panel instead of exiting the inventory or forge menu.</summary>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.ControllerB) return;

            // Inventory tab on the GameMenu.
            if (InventoryPagePatches.IsPanelOpen
                && Game1.activeClickableMenu is GameMenu gm
                && gm.currentTab == GameMenu.inventoryTab
                && Game1.player.CursorSlotItem == null)
            {
                InventoryPagePatches.TogglePanel(playSound: true);
                Helper.Input.Suppress(e.Button);
                return;
            }

            // Forge menu.
            if (ForgeMenuPatches.IsPanelOpen
                && Game1.activeClickableMenu is ForgeMenu
                && Game1.player.CursorSlotItem == null)
            {
                ForgeMenuPatches.TogglePanel(playSound: true);
                Helper.Input.Suppress(e.Button);
            }
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