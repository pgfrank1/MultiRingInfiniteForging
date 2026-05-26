using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Objects;
using Microsoft.Xna.Framework;

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
            foreach (var ring in Slots)
                ring?.onEquip(Game1.player);
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
        }

        public static void RemoveAllEffects()
        {
            foreach (var ring in Slots)
                ring?.onUnequip(Game1.player);
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
        }

        public static void Equip(int slot, Ring? ring)
        {
            EnsureSize();
            // Unequip current
            Slots[slot]?.onUnequip(Game1.player);
            Slots[slot] = ring;
            ring?.onEquip(Game1.player);

            // Force BuffManager to recompute on the next access so AddEquipmentEffects
            // sees the new/removed ring.  Without this, magnetic radius, defense,
            // crit chance, etc. wouldn't update until something else dirties the cache.
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
        }

        // ---------- per-frame / world-event forwarders ----------

        /// <summary>Forward Ring.update to all extra-slot rings every tick.  Vanilla only
        /// calls update() on leftRing/rightRing, but Glow Ring, Iridium Band, Glowstone
        /// Ring etc. need this each tick to reposition their light source to follow the
        /// player.</summary>
        public static void Update(GameTime time, GameLocation location, Farmer who)
        {
            if (location == null || who == null) return;
            foreach (var ring in Slots)
                ring?.update(time, location, who);
        }

        /// <summary>Forward Ring.onLeaveLocation / onNewLocation on map warps so light-
        /// source rings de-register their light from the old location and re-register
        /// it in the new one.</summary>
        public static void HandleWarp(Farmer who, GameLocation? oldLocation, GameLocation? newLocation)
        {
            if (who == null) return;
            foreach (var ring in Slots)
            {
                if (ring == null) continue;
                if (oldLocation != null) ring.onLeaveLocation(who, oldLocation);
                if (newLocation != null) ring.onNewLocation(who, newLocation);
            }
        }

        /// <summary>Forward Ring.onMonsterSlay to all extra-slot rings (and recurse into
        /// CombinedRings, which Ring.onMonsterSlay handles itself).  Drives Vampire,
        /// Warrior, Savage, Soul Sapper, Napalm, and Hot Java effects from extra slots.</summary>
        public static void OnMonsterSlay(Monster monster, GameLocation location, Farmer who)
        {
            if (monster == null || who == null) return;
            foreach (var ring in Slots)
                ring?.onMonsterSlay(monster, location, who);
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