using System.Collections.Generic;

namespace MultiRingInfiniteForging
{
    /// <summary>Per-player save data: the extra equipped rings (item IDs + forged-onto data).</summary>
    public class ExtraRingsData
    {
        /// <summary>Index -> serialized ring (qualified item id). Null/empty means slot is empty.</summary>
        public List<string?> Slots { get; set; } = new();
    }
}