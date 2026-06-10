using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Objects;
using Microsoft.Xna.Framework;

namespace MultiRingInfiniteForging
{
    /// <summary>Manages the local player's extra ring slots beyond the two vanilla ones,
    /// and resolves other players' extra rings from their net-synced modData.</summary>
    public static class RingSlotManager
    {
        // Per-screen so split-screen players each manage their own slots; in network
        // multiplayer each client only ever holds its own player's live list here.
        private static readonly PerScreen<List<Ring?>> SlotsByScreen = new(() => new List<Ring?>());
        private static readonly PerScreen<List<Ring?>> OverflowByScreen = new(() => new List<Ring?>());

        /// <summary>The local player's extra ring slots.</summary>
        public static List<Ring?> Slots => SlotsByScreen.Value;

        /// <summary>Rings stored in slots beyond the current SlotCount.  Kept so
        /// shrinking the slot count and then increasing it restores them rather
        /// than discarding them.  Persisted alongside Slots.</summary>
        private static List<Ring?> Overflow => OverflowByScreen.Value;

        /// <summary>Rings reconstructed from modData for farmers other than the local
        /// player, keyed by UniqueMultiplayerID.  Invalidated by comparing the raw
        /// modData string, which only changes when that player re-persists.</summary>
        private static readonly Dictionary<long, (string Raw, List<Ring?> Rings)> RemoteRingsCache = new();

        private static readonly List<Ring?> NoRings = new();

        private static readonly XmlSerializer RingSerializer = new(typeof(Ring));

        /// <summary>modData key holding each player's serialized extra rings.  Lives on the
        /// Farmer itself, so it's per-player, synced to every client, and serialized into
        /// the save by the host even if the host doesn't run this mod.</summary>
        private static string DataKey => _dataKey ??= ModEntry.Instance.ModManifest.UniqueID + "/ExtraRings";
        private static string? _dataKey;

        public static int SlotCount => ModEntry.Instance.Config.ExtraRingSlots;

        /// <param name="applyEffects">Whether rings moved in/out of slots have their
        /// onEquip/onUnequip side effects applied.  Pass false during Load, where the
        /// freshly loaded rings haven't been equipped yet and ApplyAllEffects runs right
        /// after — otherwise overflow-restored rings would be onEquip'd twice.</param>
        public static void EnsureSize(bool applyEffects = true)
        {
            bool changed = false;

            // GROW: pull rings back from overflow first, then pad with nulls.
            while (Slots.Count < SlotCount)
            {
                changed = true;
                if (Overflow.Count > 0)
                {
                    var ring = Overflow[0];
                    Overflow.RemoveAt(0);
                    Slots.Add(ring);
                    ModEntry.DiagVerbose("[Test] SlotManager: restored " + (ring?.DisplayName ?? "null") + " from overflow");
                    // Re-apply effects since this ring is once again "equipped".
                    if (applyEffects)
                        ring?.onEquip(Game1.player);
                }
                else
                {
                    Slots.Add(null);
                }
            }

            // SHRINK: move trailing slots into overflow instead of discarding.
            //   - Their effects are removed (they're no longer equipped).
            //   - They stay in saved data so we can restore them on grow / drain.
            while (Slots.Count > SlotCount)
            {
                changed = true;
                var ring = Slots[^1];
                Slots.RemoveAt(Slots.Count - 1);
                if (ring != null)
                {
                    if (applyEffects)
                        ring.onUnequip(Game1.player);
                    Overflow.Add(ring);
                    ModEntry.DiagVerbose("[Test] SlotManager: moved " + ring.DisplayName + " to overflow (slot count shrank)");
                }
            }

            if (changed)
                Persist();

            if (Game1.player != null)
                Game1.player.buffs.Dirty = true;
        }

        /// <summary>Reset all in-memory slot state.  Called on return to title so a stale
        /// session's rings can't leak into title-screen GMCM callbacks or a different save
        /// loaded next.  Persisted modData is untouched.</summary>
        public static void Clear()
        {
            Slots.Clear();
            Overflow.Clear();
            RemoteRingsCache.Clear();
        }

        /// <summary>The extra rings "worn" by any farmer.  For the local player this is the
        /// live <see cref="Slots"/> list; for any other farmer it's reconstructed (and
        /// cached) from their net-synced modData.  This is what lets host-simulated checks
        /// — slime aggro via isWearingRing, onMonsterSlay for a farmhand's kill — see rings
        /// that only exist in another player's panel.</summary>
        public static IReadOnlyList<Ring?> GetRingsFor(Farmer? who)
        {
            if (who == null) return NoRings;
            if (who == Game1.player) return Slots;
            if (who.modData == null
                || !who.modData.TryGetValue(DataKey, out var raw)
                || string.IsNullOrEmpty(raw))
                return NoRings;

            long id = who.UniqueMultiplayerID;
            if (RemoteRingsCache.TryGetValue(id, out var cached) && cached.Raw == raw)
                return cached.Rings;

            var rings = new List<Ring?>();
            try
            {
                var data = JsonSerializer.Deserialize<ExtraRingsData>(raw);
                if (data != null)
                {
                    // Only the active slots count as "worn"; overflow rings don't.
                    foreach (var xml in data.Slots)
                        rings.Add(DeserializeRing(xml));
                }
            }
            catch (System.Exception ex)
            {
                ModEntry.Instance.Monitor.Log(
                    $"Failed to parse extra-ring data for '{who.Name}'; treating them as having none.\n" + ex,
                    LogLevel.Trace);
            }
            RemoteRingsCache[id] = (raw, rings);
            return rings;
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
            if (drained > 0)
                Persist();

            if (drained > 0 && Game1.player != null)
                Game1.player.buffs.Dirty = true;

            ModEntry.DiagVerbose("[Test] SlotManager: drained " + drained + " rings to player");
            return drained;
        }
        
        /// <summary>Adds a ring to the player's inventory if there's room, otherwise
        /// drops it as a Debris item at the player's feet in their current location.
        /// Falls back to dropping at the farmhouse's bed tile if the player has no
        /// current location (e.g. mid-save-transition).</summary>
        private static void ReturnRingToPlayer(Ring ring)
        {
            if (ring == null)
            {
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: null ring");
                return;
            }
            var player = Game1.player;
            if (player == null)
            {
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: no player; ring " + ring.DisplayName + " orphaned");
                return;
            }

            // Try inventory first.
            var leftover = player.addItemToInventory(ring);
            if (leftover == null)
            {
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: " + ring.DisplayName + " → inventory");
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
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: " + ring.DisplayName + " → ground");
                return;
            }

            // Final fallback: place in farmhouse bedside chest-equivalent spot.
            // Use the farmhouse if accessible; otherwise log and orphan (very rare).
            if (Game1.getLocationFromName("FarmHouse") is GameLocation farmhouse)
            {
                Game1.createItemDebris(leftover, new Microsoft.Xna.Framework.Vector2(8, 9) * Game1.tileSize, -1, farmhouse);
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: " + ring.DisplayName + " → farmhouse");
            }
            else
            {
                ModEntry.DiagVerbose("[Test] ReturnRingToPlayer: " + ring.DisplayName + " orphaned (no location)");
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
            Persist();

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

        /// <summary>Forward Ring.onMonsterSlay to the killer's extra-slot rings (recursing
        /// into CombinedRings, which Ring.onMonsterSlay handles itself).  Drives Vampire,
        /// Warrior, Savage, Soul Sapper, Napalm, and Hot Java effects from extra slots.
        /// monsterDrop runs on the client simulating the location (usually the host), so
        /// for a farmhand's kill this resolves their rings from synced modData — the same
        /// way vanilla reads their net-synced leftRing/rightRing there.</summary>
        public static void OnMonsterSlay(Monster monster, GameLocation location, Farmer who)
        {
            if (monster == null || who == null) return;
            foreach (var ring in GetRingsFor(who))
                ring?.onMonsterSlay(monster, location, who);
        }

        // ---------- persistence ----------

        public static void Load(IModHelper helper)
        {
            Slots.Clear();
            Overflow.Clear();
            RemoteRingsCache.Clear();

            MigrateLegacySaveData(helper);

            var data = new ExtraRingsData();
            if (Game1.player?.modData != null
                && Game1.player.modData.TryGetValue(DataKey, out var raw)
                && !string.IsNullOrEmpty(raw))
            {
                try
                {
                    data = JsonSerializer.Deserialize<ExtraRingsData>(raw) ?? new ExtraRingsData();
                }
                catch (System.Exception ex)
                {
                    ModEntry.Instance.Monitor.Log(
                        "Failed to parse this player's saved extra-ring data; slots will load empty.\n" + ex,
                        LogLevel.Error);
                }
            }

            foreach (var xml in data.Slots)
                Slots.Add(DeserializeRing(xml));

            foreach (var xml in data.Overflow)
            {
                var ring = DeserializeRing(xml);
                if (ring != null) Overflow.Add(ring);
            }

            // Don't apply equip effects here: ApplyAllEffects (called right after Load)
            // equips every slotted ring exactly once.
            EnsureSize(applyEffects: false);
            ModEntry.DiagVerbose("[Test] SlotManager: loaded " + Slots.Count + " slots, " + Overflow.Count + " overflow");
        }

        /// <summary>One-time migration: versions before 1.1.0 stored the main player's rings
        /// in SMAPI save data, which only the main player can read — the reason farmhands
        /// couldn't have slots at all.  Move that data into Farmer.modData and delete the
        /// legacy entry so this runs once.</summary>
        private static void MigrateLegacySaveData(IModHelper helper)
        {
            if (!Context.IsMainPlayer || Game1.player == null) return;
            if (Game1.player.modData.ContainsKey(DataKey)) return;
            try
            {
                var legacy = helper.Data.ReadSaveData<ExtraRingsData>(ModEntry.SaveKey);
                if (legacy == null) return;
                Game1.player.modData[DataKey] = JsonSerializer.Serialize(legacy);
                helper.Data.WriteSaveData<ExtraRingsData>(ModEntry.SaveKey, null);
                ModEntry.Diag("Migrated legacy save-data rings into Farmer.modData (pre-1.1.0 format).");
            }
            catch (System.Exception ex)
            {
                ModEntry.Instance.Monitor.Log(
                    "Legacy extra-ring data migration failed; extra slots may load empty.\n" + ex,
                    LogLevel.Error);
            }
        }

        /// <summary>Serialize the local player's slots + overflow into their modData.
        /// Called on every mutation (write-through): modData syncs continuously, so the
        /// host always holds each player's latest rings for saving — even if a farmhand
        /// disconnects without a clean end-of-day save, and even if the host doesn't run
        /// this mod (modData is serialized with the Farmer regardless).</summary>
        public static void Persist()
        {
            if (!Context.IsWorldReady || Game1.player == null) return;
            var data = new ExtraRingsData();
            foreach (var ring in Slots)
                data.Slots.Add(SerializeRing(ring));
            foreach (var ring in Overflow)
                data.Overflow.Add(SerializeRing(ring));
            Game1.player.modData[DataKey] = JsonSerializer.Serialize(data);
        }

        private static Ring? DeserializeRing(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                using var sr = new StringReader(xml);
                return (Ring?)RingSerializer.Deserialize(sr);
            }
            catch (System.Exception ex)
            {
                ModEntry.Instance.Monitor.Log(
                    "Failed to deserialize a saved extra-slot ring; that slot will load empty. " +
                    "If it was a custom ring from another mod, that mod may be missing or updated.\n" + ex,
                    LogLevel.Error);
                return null;
            }
        }

        private static string? SerializeRing(Ring? ring)
        {
            if (ring == null) return null;
            try
            {
                using var sw = new StringWriter();
                RingSerializer.Serialize(sw, ring);
                return sw.ToString();
            }
            catch (System.Exception ex)
            {
                // Most likely a custom Ring subclass from another C# mod, which the vanilla
                // Ring serializer has no XmlInclude for.  Dropping just this ring keeps the
                // rest of the slots persisting (an unhandled throw here would skip
                // WriteSaveData entirely and roll back ALL slots on the next load).
                ModEntry.Instance.Monitor.Log(
                    $"Failed to serialize extra-slot ring '{ring.DisplayName}' ({ring.GetType().FullName}) — " +
                    "it will NOT persist. Run mrif_drain to move it to your inventory before quitting.\n" + ex,
                    LogLevel.Error);
                return null;
            }
        }
    }
}