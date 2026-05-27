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

        /// <summary>Rings stored in slots beyond the current SlotCount.  Kept so
        /// shrinking the slot count and then increasing it restores them rather
        /// than discarding them.  Persisted alongside Slots in save data.</summary>
        private static readonly List<Ring?> Overflow = new();

        private static readonly XmlSerializer RingSerializer = new(typeof(Ring));

        public static int SlotCount => ModEntry.Instance.Config.ExtraRingSlots;

        public static void EnsureSize()
        {
            // GROW: pull rings back from overflow first, then pad with nulls.
            while (Slots.Count < SlotCount)
            {
                if (Overflow.Count > 0)
                {
                    var ring = Overflow[0];
                    Overflow.RemoveAt(0);
                    Slots.Add(ring);
                    // Re-apply effects since this ring is once again "equipped".
                    ring?.onEquip(Game1.player);
                }
                else
                {
                    Slots.Add(null);
                }
            }

            // SHRINK: move trailing slots into overflow instead of discarding.
            //   - Their effects are removed (they're no longer equipped).
            //   - They stay in save data so we can restore them on grow / drain.
            while (Slots.Count > SlotCount)
            {
                var ring = Slots[^1];
                Slots.RemoveAt(Slots.Count - 1);
                if (ring != null)
                {
                    ring.onUnequip(Game1.player);
                    Overflow.Add(ring);
                }
            }

            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
        }
        
        /// <summary>Drains every extra ring slot AND the overflow bucket back to
        /// the player, with overflow dropped at the player's feet.  Use this
        /// before uninstalling the mod or when the user explicitly asks for all
        /// rings back.</summary>
        public static int DrainAllToPlayer()
        {
            int drained = 0;

            for (int i = 0; i < Slots.Count; i++)
            {
                var ring = Slots[i];
                if (ring == null) continue;
                ring.onUnequip(Game1.player);
                Slots[i] = null;
                ReturnRingToPlayer(ring);
                drained++;
            }

            // Drain the overflow bucket too — these are rings the user can't
            // currently see in the UI but are still preserved in save data.
            foreach (var ring in Overflow)
            {
                if (ring == null) continue;
                ReturnRingToPlayer(ring);
                drained++;
            }
            Overflow.Clear();

            if (drained > 0 && Game1.player != null)
                Game1.player.buffs.Dirty = true;

            return drained;
        }
        
        /// <summary>Adds a ring to the player's inventory if there's room, otherwise
        /// drops it as a Debris item at the player's feet in their current location.
        /// Falls back to dropping at the farmhouse's bed tile if the player has no
        /// current location (e.g. mid-save-transition).</summary>
        private static void ReturnRingToPlayer(Ring ring)
        {
            if (ring == null) return;
            var player = Game1.player;
            if (player == null)
            {
                ModEntry.DiagVerbose($"ReturnRingToPlayer: no player; ring {ring.DisplayName} dropped (data orphaned)");
                return;
            }

            // Try inventory first.
            var leftover = player.addItemToInventory(ring);
            if (leftover == null)
            {
                ModEntry.DiagVerbose($"ReturnRingToPlayer: {ring.DisplayName} -> inventory");
                return;
            }

            // Inventory full: drop as Debris at the player's feet.
            var location = player.currentLocation;
            if (location != null)
            {
                Game1.createItemDebris(leftover, player.Position, -1, location);
                Game1.addHUDMessage(new HUDMessage(
                    $"{leftover.DisplayName} dropped on the ground (inventory full).",
                    HUDMessage.error_type));
                ModEntry.DiagVerbose($"ReturnRingToPlayer: {ring.DisplayName} -> ground at {player.Position}");
                return;
            }

            // Final fallback: place in farmhouse bedside chest-equivalent spot.
            // Use the farmhouse if accessible; otherwise log and orphan (very rare).
            if (Game1.getLocationFromName("FarmHouse") is GameLocation farmhouse)
            {
                Game1.createItemDebris(leftover, new Microsoft.Xna.Framework.Vector2(8, 9) * Game1.tileSize, -1, farmhouse);
                ModEntry.DiagVerbose($"ReturnRingToPlayer: {ring.DisplayName} -> dropped in farmhouse (no current location)");
            }
            else
            {
                ModEntry.DiagVerbose($"ReturnRingToPlayer: {ring.DisplayName} could not be placed (no location available)");
            }
        }

        // ---------- effects ----------

        public static void ApplyAllEffects()
        {
            EnsureSize();
            int applied = 0;
            foreach (var ring in Slots)
                ring?.onEquip(Game1.player);
            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
            ModEntry.Diag(
                $"RingSlotManager.ApplyAllEffects: applied {applied} ring(s). " +
                $"MagneticRadius={Game1.player?.MagneticRadius}");
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
            ModEntry.Diag($"RingSlotManager.RemoveAllEffects: removed {removed} ring(s).");
        }

        public static void Equip(int slot, Ring? ring)
        {
            EnsureSize();
            var previous = Slots[slot];
            ModEntry.Diag(
                $"RingSlotManager.Equip slot={slot} " +
                $"previous={previous?.DisplayName ?? "null"} " +
                $"new={ring?.DisplayName ?? "null"}");

            // Unequip current
            previous?.onUnequip(Game1.player);
            Slots[slot] = ring;
            ring?.onEquip(Game1.player);

            // Force BuffManager to recompute on the next access so AddEquipmentEffects
            // sees the new/removed ring.  Without this, magnetic radius, defense,
            // crit chance, etc. wouldn't update until something else dirties the cache.
            if (Game1.player != null)
            {
                Game1.player.buffs.Dirty = true;
                ModEntry.Diag(
                    $"RingSlotManager.Equip set buffs.Dirty=true. " +
                    $"MagneticRadius now={Game1.player.MagneticRadius}");
            }
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
            Overflow.Clear();
            var data = helper.Data.ReadSaveData<ExtraRingsData>(ModEntry.SaveKey) ?? new ExtraRingsData();

            foreach (var xml in data.Slots)
                Slots.Add(DeserializeRing(xml));

            foreach (var xml in data.Overflow)
            {
                var ring = DeserializeRing(xml);
                if (ring != null) Overflow.Add(ring);
            }

            EnsureSize();
        }

        public static void Save(IModHelper helper)
        {
            var data = new ExtraRingsData();
            foreach (var ring in Slots)
                data.Slots.Add(SerializeRing(ring));

            foreach (var ring in Overflow)
                data.Overflow.Add(SerializeRing(ring));

            helper.Data.WriteSaveData(ModEntry.SaveKey, data);
        }

        private static Ring? DeserializeRing(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                using var sr = new StringReader(xml);
                return (Ring?)RingSerializer.Deserialize(sr);
            }
            catch
            {
                return null;
            }
        }

        private static string? SerializeRing(Ring? ring)
        {
            if (ring == null) return null;
            using var sw = new StringWriter();
            RingSerializer.Serialize(sw, ring);
            return sw.ToString();
        }
    }
}