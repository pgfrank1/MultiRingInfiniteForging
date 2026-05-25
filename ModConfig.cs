namespace MultiRingInfiniteForging
{
    public class ModConfig
    {
        /// <summary>How many extra ring slots to add (in addition to the 2 vanilla slots).</summary>
        public int ExtraRingSlots { get; set; } = 4;

        /// <summary>Remove the cap on how many rings can be combined together.</summary>
        public bool InfiniteCombining { get; set; } = true;
        
        /// <summary>Remove the 3-gem cap on weapon forging (lets you keep applying gems
        /// to a weapon beyond level 3).</summary>
        public bool InfiniteWeaponForging { get; set; } = true;

        /// <summary>Allow stacking multiple enchantments on the same weapon or tool
        /// instead of replacing the existing enchantment.</summary>
        public bool MultipleEnchantments { get; set; } = true;
    }
}