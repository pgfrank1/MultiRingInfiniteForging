using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>Manages the player's extra ring slots beyond the two vanilla ones.</summary>
    public static class RingSlotManager
    {
        public static List<Ring?> Slots { get; private set; } = new();

        private static readonly XmlSerializer RingSerializer = new(typeof(Ring));

        public static int SlotCount => ModEntry.Instance.Config.ExtraRingSlots;

        public static void EnsureSize()
        {
            while (Slots.Count < SlotCount) Slots.Add(null);
            while (Slots.Count > SlotCount)
            {
                // Drop extras into the player's inventory if the user reduced slot count.
                var ring = Slots[^1];
                Slots.RemoveAt(Slots.Count - 1);
                if (ring != null && Game1.player != null)
                    Game1.player.addItemToInventory(ring);
            }
        }

        // ---------- effects ----------

        public static void ApplyAllEffects()
        {
            EnsureSize();
            int applied = 0;
            foreach (var ring in Slots)
            {
                if (ring != null)
                {
                    ring.onEquip(Game1.player);
                    applied++;
                }
            }
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
            ModEntry.Instance.Monitor.Log(
                $"[Diag] RingSlotManager.ApplyAllEffects: applied {applied} ring(s). MagneticRadius={Game1.player?.MagneticRadius}",
                LogLevel.Info);
        }

        public static void RemoveAllEffects()
        {
            int removed = 0;
            foreach (var ring in Slots)
            {
                if (ring != null)
                {
                    ring.onUnequip(Game1.player);
                    removed++;
                }
            }
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
            ModEntry.Instance.Monitor.Log(
                $"[Diag] RingSlotManager.RemoveAllEffects: removed {removed} ring(s).",
                LogLevel.Info);
        }

        public static void Equip(int slot, Ring? ring)
        {
            EnsureSize();
            var previous = Slots[slot];
            ModEntry.Instance.Monitor.Log(
                $"[Diag] RingSlotManager.Equip slot={slot} previous={previous?.DisplayName ?? "null"} new={ring?.DisplayName ?? "null"}",
                LogLevel.Info);

            // Unequip current
            previous?.onUnequip(Game1.player);
            Slots[slot] = ring;
            ring?.onEquip(Game1.player);

            // Force BuffManager to recompute on the next access so AddEquipmentEffects
            // sees the new/removed ring.  Without this, magnetic radius, defense,
            // crit chance etc. wouldn't update until something else dirties the cache.
            if (Game1.player != null)
            {
                Game1.player.buffs.Dirty = true;
                ModEntry.Instance.Monitor.Log(
                    $"[Diag] RingSlotManager.Equip set buffs.Dirty=true. MagneticRadius now={Game1.player.MagneticRadius}",
                    LogLevel.Info);
            }
        }

        // ---------- persistence ----------

        public static void Load(IModHelper helper)
        {
            Slots = new List<Ring?>();
            var data = helper.Data.ReadSaveData<ExtraRingsData>(ModEntry.SaveKey) ?? new ExtraRingsData();

            foreach (var xml in data.Slots)
            {
                if (string.IsNullOrEmpty(xml)) { Slots.Add(null); continue; }
                try
                {
                    using var sr = new StringReader(xml);
                    var ring = (Ring?)RingSerializer.Deserialize(sr);
                    Slots.Add(ring);
                }
                catch
                {
                    Slots.Add(null);
                }
            }

            EnsureSize();
        }

        public static void Save(IModHelper helper)
        {
            var data = new ExtraRingsData();
            foreach (var ring in Slots)
            {
                if (ring == null) { data.Slots.Add(null); continue; }
                using var sw = new StringWriter();
                RingSerializer.Serialize(sw, ring);
                data.Slots.Add(sw.ToString());
            }
            helper.Data.WriteSaveData(ModEntry.SaveKey, data);
        }
    }
}