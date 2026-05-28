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
        
        /// <summary>Remove the 3-gem cap on weapon forging (lets you keep applying gems
        /// to a weapon beyond level 3).</summary>
        public bool InfiniteWeaponForging { get; set; } = true;
        
        public bool RemoveDiamondForgesCap { get; set; } = false;

        /// <summary>Allow stacking multiple enchantments on the same weapon or tool
        /// instead of replacing the existing enchantment.</summary>
        public bool MultipleEnchantments { get; set; } = true;
        
        /// <summary>If true, emit per-tick / per-recompute diagnostic lines to
        /// smapi-latest.log.  These are normally suppressed even from the log file
        /// because they're high-volume; only enable when debugging a reproducible
        /// issue.  Low-frequency diagnostics (ring equip events, patch results) are
        /// always written at Trace level regardless of this setting.</summary>
        public bool VerboseLogging { get; set; } = false;
    }
}