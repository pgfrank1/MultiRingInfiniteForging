namespace MultiRingInfiniteForging
{
    public class ModConfig
    {
        /// <summary>How many extra ring slots to add (in addition to the 2 vanilla slots).</summary>
        public int ExtraRingSlots { get; set; } = 4;

        /// <summary>Remove the cap on how many rings can be combined together.</summary>
        public bool InfiniteCombining { get; set; } = true;
        
        /// <summary>When InfiniteCombining is on, optionally prevent a combine from
        /// resulting in a CombinedRing that contains the same ring (by qualified
        /// item ID) more than once.  Vanilla never produces duplicates because it
        /// caps combines at 2; with InfiniteCombining you can stack a Magnet Ring
        /// inside another Magnet Ring inside another — enabling this option blocks
        /// that.</summary>
        public bool AddCombinedDuplicateRingCap { get; set; } = false;
        
        /// <summary>Maximum number of gem forges per weapon (-1 = unlimited).</summary>
        public int WeaponForgingCap { get; set; } = -1;
        
        public bool RemoveDiamondForgesCap { get; set; } = false;

        /// <summary>Allow stacking multiple enchantments on the same weapon or tool
        /// instead of replacing the existing enchantment.</summary>
        public bool MultipleEnchantments { get; set; } = true;

        /// <summary>Dragon Tooth adds the innate enchantment on top of existing ones
        /// instead of vanilla's replace-and-re-roll.  Forging the same type again levels
        /// it up (+1), capped at the game's per-type maximum.</summary>
        public bool DragonToothStacking { get; set; } = true;

        /// <summary>Innate enchantments applied by Dragon Tooth always land at their
        /// maximum level (GetMaximumLevel) instead of a rolled level, and each craft
        /// also raises the innate enchantments already on the weapon to their caps
        /// (stacking never strips, so spawn rolls and pre-option crafts would otherwise
        /// stay sub-max forever).  Defaults to true: with Forge Menu Choice the
        /// carousel's rolled levels can be re-rolled for free by re-slotting the tooth
        /// anyway, and the game's cap even exceeds the rollable range for some types
        /// (e.g. Crit. Power V vs rolled max III).</summary>
        public bool AlwaysMaxDragonToothStat { get; set; } = true;

        /// <summary>Master toggle for the Forge Menu Choice integration: when FMC is
        /// installed, its enchantment-selection carousel also opens for Dragon Tooth
        /// crafts this mod handles (Galaxy/Infinity weapons, and all weapons when
        /// DragonToothStacking is on).</summary>
        public bool ForgeMenuChoiceIntegration { get; set; } = true;
        
        /// <summary>If true, emit per-event diagnostic lines ([Test]/[Forge] traces) to
        /// smapi-latest.log.  These are normally suppressed even from the log file
        /// because they're high-volume; only enable when debugging a reproducible
        /// issue.  Low-frequency diagnostics (ring equip events, patch results) are
        /// always written at Trace level regardless of this setting.</summary>
        public bool VerboseLogging { get; set; } = false;

        /// <summary>If true, emit the per-second "[Tick]" snapshot of equipped rings and
        /// buff stats.  Independent of VerboseLogging: it's by far the noisiest output and
        /// muddles the log when testing anything else, so it can be silenced (or captured
        /// alone) separately.</summary>
        public bool VerboseTickSnapshot { get; set; } = false;
    }
}