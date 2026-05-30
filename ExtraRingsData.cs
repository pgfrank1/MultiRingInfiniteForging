using System.Collections.Generic;

namespace MultiRingInfiniteForging
{
    /// <summary>Represents the serialized save data structure for storing a player's extra equipped ring slots and overflow rings, containing item IDs and forging enchantment data.</summary>
    public class ExtraRingsData
    {
        /// <summary>The list of extra-equipped ring slots. Each element is a ring item or null if the slot is empty.</summary>
        public List<string?> Slots { get; set; } = new();

        /// <summary>Index -> serialized ring (qualified item id). Rings stored in slots beyond the current SlotCount.</summary>
        public List<string?> Overflow { get; set; } = new();
    }
}