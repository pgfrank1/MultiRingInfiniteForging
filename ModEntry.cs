using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

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