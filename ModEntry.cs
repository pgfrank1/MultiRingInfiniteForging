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
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Player.Warped += OnWarped;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            var harmony = new Harmony(ModManifest.UniqueID);
            Patches.ApplyAll(harmony, Monitor);

            helper.ConsoleCommands.Add(
                name: "mrif_stats",
                documentation:
                "Multi Ring Infinite Forging: dump the local player's current ring-derived stats (defense, attack, magnetic radius, crit, luck, immunity, etc.).",
                callback: DumpStatsCommand);
        }
        
        /// <summary>Console command: prints the current values of all buff-aggregated
        /// player stats.  Use to verify ring effects from extra slots are flowing
        /// through to Farmer.buffs.  Defense in particular doesn't get a dedicated row
        /// on the character sheet when it's 0, so this is the only easy way to confirm
        /// e.g. a Topaz Ring in an extra slot is contributing.</summary>
        private void DumpStatsCommand(string name, string[] args)
        {
            if (!Context.IsWorldReady || Game1.player == null)
            {
                Monitor.Log("No player loaded.", LogLevel.Warn);
                return;
            }

            var p = Game1.player;
            var b = p.buffs;

            int extra = 0;
            foreach (var r in RingSlotManager.Slots)
                if (r != null) extra++;

            Monitor.Log("== Ring-derived stats (from Farmer.buffs) ==", LogLevel.Info);
            Monitor.Log($"  Extra rings equipped: {extra}/{RingSlotManager.Slots.Count}", LogLevel.Info);
            Monitor.Log($"  leftRing:  {p.leftRing.Value?.DisplayName ?? "(empty)"}", LogLevel.Info);
            Monitor.Log($"  rightRing: {p.rightRing.Value?.DisplayName ?? "(empty)"}", LogLevel.Info);
            for (int i = 0; i < RingSlotManager.Slots.Count; i++)
                Monitor.Log($"  extra[{i}]: {RingSlotManager.Slots[i]?.DisplayName ?? "(empty)"}", LogLevel.Info);

            Monitor.Log("-- Combat stats --", LogLevel.Info);
            Monitor.Log($"  Defense              = {b.Defense}", LogLevel.Info);
            Monitor.Log($"  Attack               = {b.Attack}", LogLevel.Info);
            Monitor.Log($"  AttackMultiplier     = {b.AttackMultiplier:F2}", LogLevel.Info);
            Monitor.Log($"  KnockbackMultiplier  = {b.KnockbackMultiplier:F2}", LogLevel.Info);
            Monitor.Log($"  WeaponSpeedMult      = {b.WeaponSpeedMultiplier:F2}", LogLevel.Info);
            Monitor.Log($"  CriticalChanceMult   = {b.CriticalChanceMultiplier:F2}", LogLevel.Info);
            Monitor.Log($"  CriticalPowerMult    = {b.CriticalPowerMultiplier:F2}", LogLevel.Info);
            Monitor.Log($"  Immunity             = {b.Immunity}", LogLevel.Info);

            Monitor.Log("-- World stats --", LogLevel.Info);
            Monitor.Log($"  MagneticRadius       = {p.MagneticRadius} (base 128, buff {b.MagneticRadius})", LogLevel.Info);
            Monitor.Log($"  LuckLevel buff       = {b.LuckLevel}", LogLevel.Info);
            Monitor.Log($"  Speed buff           = {b.Speed:F2}", LogLevel.Info);
            Monitor.Log($"  MaxStamina buff      = {b.MaxStamina}", LogLevel.Info);

            // Force a recompute so values reflect any recent equipment changes.
            b.Dirty = true;
            Monitor.Log("(buffs marked dirty for next-access recompute)", LogLevel.Info);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // Wire GMCM if it's installed; otherwise this is a no-op.
            GenericModConfigMenuIntegration.Register(
                mod: this,
                config: Config,
                save: () => Helper.WriteConfig(Config));
        }

        /// <summary>Per-tick forwarder so light-source rings (Glow, Iridium Band,
        /// Glowstone Ring) in extra slots reposition their light to follow the player.</summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;
            RingSlotManager.Update(Game1.currentGameTime, Game1.player.currentLocation, Game1.player);
        }

        /// <summary>When the local player warps, forward onLeaveLocation/onNewLocation
        /// to extra-slot rings so light sources migrate to the new map.</summary>
        private void OnWarped(object? sender, WarpedEventArgs e)
        {
            if (!e.IsLocalPlayer) return;
            RingSlotManager.HandleWarp(e.Player, e.OldLocation, e.NewLocation);
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