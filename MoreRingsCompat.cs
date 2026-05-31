using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace MultiRingInfiniteForging
{
    /// <summary>
    /// Compatibility shim for spacechase0's "More Rings" mod.
    ///
    /// More Rings detects its own rings (e.g. Ring of Wide Nets, which enlarges the
    /// fishing-minigame bar) via its private <c>Mod.CountRingsEquipped(string)</c>, which reads
    /// only <c>Game1.player.leftRing</c> / <c>rightRing</c> — the two vanilla slots. It never
    /// calls <see cref="Farmer.isWearingRing"/> or <c>GetEquippedItems</c> (the methods we patch),
    /// so it cannot see rings worn in our extra slots, and its effects don't apply there.
    ///
    /// We postfix <c>CountRingsEquipped</c> to also count matching rings in our extra slots,
    /// using the same <see cref="Ring.GetEffectsOfRingMultiplier"/> the original uses (so combined
    /// rings are handled identically). This is best-effort and entirely optional:
    ///   - does nothing if More Rings isn't installed;
    ///   - resolves the target method by reflection and skips (with a log) if the method can't be
    ///     found, so a future More Rings update can't break our mod.
    /// </summary>
    internal static class MoreRingsCompat
    {
        private const string MoreRingsModId = "spacechase0.MoreRings";

        /// <summary>Attempt to apply the More Rings compatibility patch. Safe to call always.</summary>
        public static void TryApply(Harmony harmony, IModHelper helper, IMonitor monitor)
        {
            if (!helper.ModRegistry.IsLoaded(MoreRingsModId))
                return;

            try
            {
                // The detection method (CountRingsEquipped) lives on different types depending on
                // the More Rings build:
                //   - chiccenDev's 1.2.4+ rewrite (the only version that loads on SV 1.6): the
                //     partial class "MoreRings.ModEntry".
                //   - spacechase0's original 1.2.3: "MoreRings.Mod" (cannot load on 1.6, but we
                //     still try it so this shim works on older game versions too).
                // Try each known type name and use the first that exists.
                Type? modType = null;
                foreach (var typeName in new[] { "MoreRings.ModEntry", "MoreRings.Mod" })
                {
                    modType = AccessTools.TypeByName(typeName);
                    if (modType is not null)
                        break;
                }
                if (modType is null)
                {
                    monitor.Log(
                        "More Rings is installed but its mod type wasn't found; "
                            + "extra-slot rings won't grant More Rings effects.",
                        LogLevel.Warn
                    );
                    return;
                }

                MethodInfo target = AccessTools.Method(
                    modType,
                    "CountRingsEquipped",
                    new[] { typeof(string) }
                );
                if (target is null)
                {
                    monitor.Log(
                        "More Rings is installed but CountRingsEquipped(string) wasn't found "
                            + "(the mod may have changed); extra-slot rings won't grant More Rings "
                            + "effects.",
                        LogLevel.Warn
                    );
                    return;
                }

                harmony.Patch(
                    original: target,
                    postfix: new HarmonyMethod(
                        typeof(MoreRingsCompat),
                        nameof(CountRingsEquipped_Postfix)
                    )
                );
                monitor.Log(
                    "Patched More Rings' CountRingsEquipped: its rings now work in extra slots.",
                    LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                monitor.Log(
                    "Failed to apply More Rings compatibility patch; extra-slot rings won't grant "
                        + "More Rings effects.\n"
                        + ex,
                    LogLevel.Warn
                );
            }
        }

        /// <summary>
        /// Adds the count of matching rings worn in our extra slots to More Rings' own count.
        /// The parameter name <c>id</c> matches the original method's argument so Harmony binds it.
        /// </summary>
        public static void CountRingsEquipped_Postfix(string id, ref int __result)
        {
            int extra = 0;
            foreach (Ring? ring in RingSlotManager.Slots)
            {
                if (ring is not null)
                    extra += ring.GetEffectsOfRingMultiplier(id);
            }

            if (extra > 0)
                __result += extra;
        }
    }
}
