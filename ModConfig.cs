namespace MultiRingInfiniteForging
{
    public class ModConfig
    {
        /// <summary>How many extra ring slots to add (in addition to the 2 vanilla slots).</summary>
        public int ExtraRingSlots { get; set; } = 4;

        /// <summary>Remove the cap on how many rings can be combined together.</summary>
        public bool InfiniteCombining { get; set; } = true;

        /// <summary>Remove the cap on how many enchantment rerolls a forged ring can have.</summary>
        public bool InfiniteReforging { get; set; } = true;
    }
}