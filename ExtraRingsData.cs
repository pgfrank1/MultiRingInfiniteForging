using System.Collections.Generic;

namespace MultiRingInfiniteForging
{
    /// <summary>Per-player save data: the extra equipped rings (item IDs + forged-onto data).</summary>
    public class ExtraRingsData
    {
        /// <summary>Index -> serialized ring (qualified item id). Null/empty means slot is empty.</summary>
        public List<string?> Slots { get; set; } = new();

        /// <summary>Rings that were in slots beyond the current ExtraRingSlots
        /// count.  Kept here so reducing the slot count and then increasing it
        /// restores them rather than losing them.  Cleared only by an explicit
        /// drain (mrif_drain / GMCM eject button) or by saving while the slot
        /// count is high enough to hold them all again.</summary>
        public List<string?> Overflow { get; set; } = new();
    }
}