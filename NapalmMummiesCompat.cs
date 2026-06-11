using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>
    /// Compatibility with Napalm Mummies (aedenthorn.NapalmMummies) — implemented entirely
    /// from our side; that mod's code is untouched.
    ///
    /// Napalm Mummies postfixes Mummy.takeDamage: when a mummy crumples it scans the
    /// attacker's two vanilla ring fields (flattening ONE level of CombinedRing) for a
    /// Napalm Ring (811) and explodes the tile, perma-killing the mummy.  Reading the
    /// fields directly bypasses every detection method this mod extends
    /// (Farmer.isWearingRing / GetEquippedItems / GetEffectsOfRingMultiplier), so a napalm
    /// ring in an extra slot — or nested deeper than one level by infinite combining — is
    /// invisible to it and the mummy gets back up.  (The ring itself still works on normal
    /// monsters from extra slots via our onMonsterSlay forwarding; only the mummy crumple
    /// path reads the fields.)
    ///
    /// Fix: our own postfix on the same Mummy.takeDamage that fires ONLY when their scan
    /// would have missed (mutually exclusive with their explosion — no double blast) but
    /// our patched Farmer.isWearingRing finds the ring (extra slots + recursive combined
    /// lookup).  Their GMCM "Mod Enabled" toggle is honored via reflection.  Their own
    /// Ring.onMonsterSlay suppression already prevents a chain explosion when our blast
    /// kills the crumpled mummy, including for forwarded extra-slot rings.
    /// </summary>
    internal static class NapalmMummiesCompat
    {
        private const string ModId = "aedenthorn.NapalmMummies";
        private const string NapalmRingId = "811";

        private static FieldInfo? configField;       // NapalmMummies.ModEntry.Config (static)
        private static PropertyInfo? modEnabledProp; // their ModConfig.ModEnabled

        private static bool active;

        /// <summary>Patch alongside Napalm Mummies when it's installed.  No-op otherwise.</summary>
        public static void TryApply(Harmony harmony, IModHelper helper, IMonitor monitor)
        {
            if (!helper.ModRegistry.IsLoaded(ModId))
                return;
            try
            {
                Type? theirModEntry = AccessTools.TypeByName("NapalmMummies.ModEntry");
                configField = theirModEntry is null ? null : AccessTools.Field(theirModEntry, "Config");
                modEnabledProp = configField is null
                    ? null
                    : AccessTools.Property(configField.FieldType, "ModEnabled");

                harmony.Patch(
                    original: AccessTools.Method(
                        typeof(Mummy), nameof(Mummy.takeDamage),
                        new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(double), typeof(Farmer) }),
                    postfix: new HarmonyMethod(
                        typeof(NapalmMummiesCompat), nameof(Mummy_takeDamage_Postfix)));

                active = true;
                monitor.Log(
                    "Napalm Mummies detected: crumpled mummies will also explode when the Napalm Ring "
                        + "is in an extra ring slot or nested inside a combined ring.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                monitor.Log(
                    "Napalm Mummies is installed but wiring compatibility failed: " + ex.Message,
                    LogLevel.Warn);
            }
        }

        private static bool TheirModEnabled()
        {
            // Mirror their gate; default to enabled if reflection came up short.
            if (configField?.GetValue(null) is not object config)
                return true;
            return modEnabledProp?.GetValue(config) is not false;
        }

        /// <summary>The exact one-level scan Napalm Mummies performs — when THIS finds the
        /// ring, their postfix already exploded and we must stay out of the way.</summary>
        private static bool VanillaScanFindsNapalm(Farmer who)
        {
            foreach (var slot in new[] { who.leftRing.Value, who.rightRing.Value })
            {
                if (slot is CombinedRing combined)
                {
                    foreach (var r in combined.combinedRings)
                    {
                        if (r?.ItemId == NapalmRingId)
                            return true;
                    }
                }
                else if (slot?.ItemId == NapalmRingId)
                {
                    return true;
                }
            }
            return false;
        }

        public static void Mummy_takeDamage_Postfix(Mummy __instance, Farmer who)
        {
            try
            {
                if (!active || who is null || __instance.reviveTimer.Value != 10000)
                    return; // not the crumple moment (same gate Napalm Mummies uses)
                if (!TheirModEnabled())
                    return;
                if (VanillaScanFindsNapalm(who))
                    return; // their explosion already covers this crumple
                if (!who.isWearingRing(NapalmRingId))
                    return; // our patched lookup: extra slots + recursive combined rings
                ModEntry.DiagVerbose(
                    "[Test] Napalm Mummies compat: exploding crumpled mummy (napalm ring in extra slot / nested combined)");
                __instance.currentLocation.explode(__instance.Tile, 2, who, false);
            }
            catch
            {
                // Best-effort — compat must never break combat.
            }
        }
    }
}
