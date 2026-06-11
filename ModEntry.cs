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
        
        // Mod Compatability
        public static bool HasEnchantableScythes { get; private set;}
        public static bool HasScytheToolEnchantments { get; private set;}
        
        /// <summary>Convenience: shorthand for translating a key.</summary>
        public static string T(string key) => Instance.Helper.Translation.Get(key);
        
        /// <summary>Low-frequency diagnostic (ring equipped, patch applied, etc.).
        /// Always written to smapi-latest.log at Trace level.  Hidden from the
        /// console by default; visible when the user enables DeveloperMode or
        /// adds the mod to SMAPI's VerboseLogging list.</summary>
        public static void Diag(string message) =>
            Instance.Monitor.Log(message, LogLevel.Trace);

        /// <summary>High-frequency diagnostic (per-tick snapshot, per-recompute
        /// trace).  Suppressed entirely unless ModConfig.VerboseLogging is true
        /// to keep smapi-latest.log readable for unrelated troubleshooting.</summary>
        public static void DiagVerbose(string message)
        {
            if (!Instance.Config.VerboseLogging) return;
            Instance.Monitor.Log(message, LogLevel.Trace);
        }

        /// <summary>Legacy SMAPI save-data key used by versions before 1.1.0; only read by
        /// the one-time migration into Farmer.modData (see RingSlotManager.MigrateLegacySaveData).</summary>
        public const string SaveKey = "ExtraRings";

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();
            
            // Mod Compatibility
            HasEnchantableScythes = helper.ModRegistry.IsLoaded("Goldenrevolver.EnchantableScythes");
            HasScytheToolEnchantments = helper.ModRegistry.IsLoaded("mushymato.ScytheToolEnchantments");
            
            switch (HasEnchantableScythes, HasScytheToolEnchantments)
            {
                case (true, true):
                    Monitor.Log(
                        "Both Enchantable Scythes and Scythe Tool Enchantments are installed — this may cause conflicts!",
                        LogLevel.Error);
                    break;
                case (true, false):
                    Monitor.Log("Enchantable Scythes mod found, enabling scythe enchantment support", LogLevel.Info);
                    break;
                case (false, true):
                    Monitor.Log("Scythe Tool Enchantments mod found, enabling scythe enchantment support", LogLevel.Info);
                    break;
            }

            // Compatiblity with other mods that add extra equipment slots.
            bool hasWearMoreRings = helper.ModRegistry.IsLoaded("bcmpinc.WearMoreRings");
            bool hasWearMoreTrinkets = helper.ModRegistry.IsLoaded("Goldenrevolver.WearMoreTrinkets");
            if (hasWearMoreRings)
                Monitor.Log(
                    "Wear More Rings detected. Both mods add extra ring slots — only one set will display if both are active. " +
                    "Disable one of them to avoid confusion.",
                    LogLevel.Warn);
            if (hasWearMoreTrinkets)
                Monitor.Log(
                    "Wear More Trinkets detected. This mod adds a trinket slot; extra ring slots from MultiRings should coexist.",
                    LogLevel.Info);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.Saved += OnSaved;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTick;
            helper.Events.Player.Warped += OnWarped;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            var harmony = new Harmony(ModManifest.UniqueID);
            Patches.ApplyAll(harmony, Monitor);

            helper.ConsoleCommands.Add(
                name: "mrif_stats",
                documentation:
                "Multi Ring Infinite Forging: dump the local player's current ring-derived stats (defense, attack, magnetic radius, crit, luck, immunity, etc.).",
                callback: DumpStatsCommand);
            helper.ConsoleCommands.Add(
                name: "mrif_drain",
                documentation:
                "Multi Ring Infinite Forging: return every extra-slot ring to your inventory (or drop at your feet if full). " +
                "Run this before uninstalling the mod to avoid losing rings stored in extra slots.",
                callback: DrainCommand);
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

            DiagVerbose("[Test] DumpStatsCommand invoked");
            var p = Game1.player;
            var b = p.buffs;
            var loc = p.currentLocation;
            // --- Light sources in current location (for Glow/Iridium Band/Glowstone) ---
            int sharedLights = 0;
            try
            {
                // sharedLights is a NetIntDictionary<LightSource, NetRef<LightSource>>.
                // Iterate .Values rather than relying on a Count property (netcode
                // collections expose enumeration but property surfaces vary by version).
                if (loc?.sharedLights != null)
                {
                    foreach (var _ in loc.sharedLights.Values)
                        sharedLights++;
                }
            }
            catch { /* ignore — diagnostics must never throw */ }

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
        
        /// <summary>Console command: pulls every extra-slot ring back to the player's
        /// inventory, with overflow dropped at their feet.  Designed as the "before
        /// you uninstall" safety net — rings stored in our SMAPI data dictionary are
        /// unreachable without this mod loaded, so the user must run this first.</summary>
        private void DrainCommand(string name, string[] args)
        {
            if (!Context.IsWorldReady || Game1.player == null)
            {
                Monitor.Log("No player loaded.  Load a save before running mrif_drain.", LogLevel.Warn);
                return;
            }

            int drained = RingSlotManager.DrainAllToPlayer();
            if (drained == 0)
            {
                Monitor.Log("No extra-slot rings to drain.", LogLevel.Info);
            }
            else
            {
                Monitor.Log($"Drained {drained} ring(s) back to player inventory / ground.", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage(
                    $"{drained} extra-slot ring(s) returned to inventory.",
                    HUDMessage.newQuest_type));
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // Wire GMCM if it's installed; otherwise this is a no-op.
            GenericModConfigMenuIntegration.Register(
                mod: this,
                config: Config,
                save: () =>
                {
                    Helper.WriteConfig(Config);
                    RingSlotManager.EnsureSize();
                });

            // Optional cross-mod compatibility patches that must run after all mods have
            // loaded (so their assemblies are available). No-op if the target mod is absent.
            var harmony = new Harmony(ModManifest.UniqueID);
            MoreRingsCompat.TryApply(harmony, Helper, Monitor);
            ForgeMenuChoiceCompat.Init(harmony, Helper, Monitor);
            NapalmMummiesCompat.TryApply(harmony, Helper, Monitor);
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
        /// Mirrors the mrif_stats console command but at a per-second cadence.  Gated by
        /// its own VerboseTickSnapshot option, independent of VerboseLogging — it's the
        /// noisiest diagnostic by far, so it can be silenced while testing other things
        /// (or captured alone).  Designed so a snapshot log captures every value a user
        /// would want to verify when reporting a ring-effect bug.</summary>
        private void OnOneSecondTick(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            if (!Config.VerboseTickSnapshot) return;
            if (!Context.IsWorldReady || Game1.player == null) return;

            int extra = 0;
            foreach (var r in RingSlotManager.Slots)
                if (r != null) extra++;
            if (extra == 0) return; // nothing relevant to snapshot

            var p = Game1.player;
            var b = p.buffs;
            var loc = p.currentLocation;

            // --- Equipped rings: left, right, and every extra slot ---
            string leftRing  = p.leftRing.Value?.DisplayName  ?? "(empty)";
            string rightRing = p.rightRing.Value?.DisplayName ?? "(empty)";
            var extraRingNames = new System.Text.StringBuilder();
            for (int i = 0; i < RingSlotManager.Slots.Count; i++)
            {
                if (i > 0) extraRingNames.Append(", ");
                extraRingNames.Append(RingSlotManager.Slots[i]?.DisplayName ?? "(empty)");
            }

            // --- Light sources in current location (for Glow/Iridium Band/Glowstone) ---
            int sharedLights = 0;
            try
            {
                // sharedLights is a NetIntDictionary<LightSource, NetRef<LightSource>>.
                // Iterate .Values rather than relying on a Count property (netcode
                // collections expose enumeration but property surfaces vary by version).
                if (loc?.sharedLights != null)
                {
                    foreach (var _ in loc.sharedLights.Values)
                        sharedLights++;
                }
            }
            catch { /* ignore — diagnostics must never throw */ }

            // Direct Trace log (not DiagVerbose): this snapshot is gated solely by
            // VerboseTickSnapshot, independent of the VerboseLogging master switch.
            Monitor.Log(
                "[Tick] === snapshot ===" +
                $"\n  location              = {loc?.Name ?? "null"}" +
                $"\n  position              = {p.Position}" +
                $"\n  facing                = {p.FacingDirection}" +
                $"\n  rings: leftRing       = {leftRing}" +
                $"\n         rightRing      = {rightRing}" +
                $"\n         extra ({extra}/{RingSlotManager.Slots.Count}) = [{extraRingNames}]" +
                "\n  -- Combat stats --" +
                $"\n  Defense               = {b.Defense}" +
                $"\n  Attack                = {b.Attack}" +
                $"\n  AttackMultiplier      = {b.AttackMultiplier:F2}" +
                $"\n  KnockbackMultiplier   = {b.KnockbackMultiplier:F2}" +
                $"\n  WeaponSpeedMult       = {b.WeaponSpeedMultiplier:F2}" +
                $"\n  CriticalChanceMult    = {b.CriticalChanceMultiplier:F2}" +
                $"\n  CriticalPowerMult     = {b.CriticalPowerMultiplier:F2}" +
                $"\n  Immunity              = {b.Immunity}" +
                "\n  -- World / movement --" +
                $"\n  MagneticRadius        = {p.MagneticRadius} (base 128, buff {b.MagneticRadius})" +
                $"\n  Speed buff            = {b.Speed:F2}" +
                $"\n  LuckLevel buff        = {b.LuckLevel}" +
                "\n  -- Resources --" +
                $"\n  Health                = {p.health}/{p.maxHealth}" +
                $"\n  Stamina               = {p.Stamina:F1}/{p.MaxStamina}" +
                $"\n  MaxStamina buff       = {b.MaxStamina}" +
                "\n  -- Mechanics --" +
                $"\n  buffs.Dirty           = {b.Dirty}" +
                $"\n  sharedLights in loc   = {sharedLights}" +
                $"\n  in combat?            = {Game1.fadeToBlack || Game1.currentMinigame != null || p.UsingTool}",
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
            // Every player — host or farmhand — loads their own rings from their Farmer's
            // modData (per-player, net-synced, saved by the host).
            RingSlotManager.Load(Helper);
            RingSlotManager.ApplyAllEffects();
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            RingSlotManager.RemoveAllEffects();
            // Belt-and-suspenders: state is already written through on every mutation.
            RingSlotManager.Persist();
        }

        /// <summary>Re-apply ring effects after the overnight save completes.  OnSaving
        /// removes them (onUnequip kills light sources and nulls Ring.lightSourceId), and
        /// Ring.update can't restore a null light — without this, extra-slot Glow/Iridium/
        /// Glowstone rings stayed dark every morning until the first warp re-created the
        /// light via onNewLocation.</summary>
        private void OnSaved(object? sender, SavedEventArgs e)
        {
            RingSlotManager.ApplyAllEffects();
        }

        /// <summary>Clear in-memory slot state when leaving a save, so a stale session's
        /// rings can't leak into title-screen GMCM callbacks (EnsureSize/Drain against a
        /// stale Game1.player) or into a different save loaded next.</summary>
        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            RingSlotManager.Clear();
        }

        /// <summary>Our forge panel moves items via Game1.player.CursorSlotItem, which
        /// vanilla's ForgeMenu close path doesn't know about (readyToClose only checks its
        /// private _heldItem).  If the forge closes while our cursor carry is occupied, the
        /// item would ride along invisibly until the next inventory menu — return it to the
        /// player instead.</summary>
        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is not ForgeMenu || e.NewMenu is ForgeMenu) return;
            var carried = Game1.player?.CursorSlotItem;
            if (carried == null) return;

            Game1.player!.CursorSlotItem = null;
            var leftover = Game1.player.addItemToInventory(carried);
            if (leftover != null && Game1.player.currentLocation != null)
                Game1.createItemDebris(leftover, Game1.player.Position, -1, Game1.player.currentLocation);
            Diag($"Returned '{carried.Name}' from the cursor to the player on forge close.");
        }
    }
}