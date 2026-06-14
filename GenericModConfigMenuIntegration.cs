using System;
using StardewModdingAPI;

namespace MultiRingInfiniteForging
{
    public static class GenericModConfigMenuIntegration
    {
        public static void Register(IMod mod, ModConfig config, Action save)
        {
            var api = mod.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(
                "spacechase0.GenericModConfigMenu");
            if (api == null) return;

            // Unregister first so re-loads (e.g. via SMAPI reload commands) don't error.
            try { api.Unregister(mod.ModManifest); } catch { /* ignore */ }

            api.Register(
                mod: mod.ModManifest,
                reset: () => ResetToDefaults(config),
                save: save
            );

            api.AddSectionTitle(mod.ModManifest, () => ModEntry.T("config.section.title.ring"));

            api.AddNumberOption(
                mod: mod.ModManifest,
                getValue: () => config.ExtraRingSlots,
                setValue: v =>
                {
                    config.ExtraRingSlots = v;
                    RingSlotManager.EnsureSize();
                    InventoryPagePatches.RebuildForActiveMenu();
                },
                name: () => ModEntry.T("config.extraRingSlots.name"),
                tooltip: () => ModEntry.T("config.extraRingSlots.description"),
                min: 0,
                max: 36,
                interval: 1
            );

            api.AddSectionTitle(mod.ModManifest, () => ModEntry.T("config.section.title.forge"));

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.InfiniteCombining,
                setValue: v => config.InfiniteCombining = v,
                name: () => ModEntry.T("config.infiniteCombining.name"),
                tooltip: () => ModEntry.T("config.infiniteCombining.description")
            );
            
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.AddCombinedDuplicateRingCap,
                setValue: v => config.AddCombinedDuplicateRingCap = v,
                name: () => ModEntry.T("config.AddCombinedDuplicateRingCap.name"),
                tooltip: () => ModEntry.T("config.AddCombinedDuplicateRingCap.description")
            );

            string[] forgingPresets = { "-1", "0", "3", "6", "10", "15", "30", "60", "100", "250", "500", "999" };
            api.AddTextOption(
                mod: mod.ModManifest,
                getValue: () =>
                {
                    int v = config.WeaponForgingCap;
                    foreach (var p in forgingPresets)
                        if (int.TryParse(p, out int n) && n == v)
                            return p;
                    return v.ToString();
                },
                setValue: v =>
                {
                    if (int.TryParse(v, out int n))
                        config.WeaponForgingCap = n;
                },
                name: () => ModEntry.T("config.WeaponForgingCap.name"),
                tooltip: () => ModEntry.T("config.WeaponForgingCap.description"),
                allowedValues: forgingPresets,
                formatAllowedValue: v => v switch
                {
                    "-1" => ModEntry.T("config.WeaponForgingCap.valueUnlimited"),
                    "0" => ModEntry.T("config.WeaponForgingCap.valueNone"),
                    "3" => ModEntry.T("config.WeaponForgingCap.valueDefault"),
                    _ => v
                }
            );
            
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.RemoveDiamondForgesCap,
                setValue: v => config.RemoveDiamondForgesCap = v,
                name: () => ModEntry.T("config.RemoveDiamondForgesCap.name"),
                tooltip: () => ModEntry.T("config.RemoveDiamondForgesCap.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.MultipleEnchantments,
                setValue: v => config.MultipleEnchantments = v,
                name: () => ModEntry.T("config.multipleEnchantments.name"),
                tooltip: () => ModEntry.T("config.multipleEnchantments.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.DragonToothStacking,
                setValue: v => config.DragonToothStacking = v,
                name: () => ModEntry.T("config.DragonToothStacking.name"),
                tooltip: () => ModEntry.T("config.DragonToothStacking.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.AlwaysMaxDragonToothStat,
                setValue: v => config.AlwaysMaxDragonToothStat = v,
                name: () => ModEntry.T("config.AlwaysMaxDragonToothStat.name"),
                tooltip: () => ModEntry.T("config.AlwaysMaxDragonToothStat.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.ForgeMenuChoiceIntegration,
                setValue: v => config.ForgeMenuChoiceIntegration = v,
                name: () => ModEntry.T("config.ForgeMenuChoiceIntegration.name"),
                tooltip: () => ModEntry.T("config.ForgeMenuChoiceIntegration.description")
            );
            api.AddSectionTitle(mod.ModManifest, () => ModEntry.T("config.section.title.verbose.logging"));
            
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.VerboseLogging,
                setValue: v => config.VerboseLogging = v,
                name: () => ModEntry.T("config.verbose.logging.name"),
                tooltip: () => ModEntry.T("config.verbose.logging.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.VerboseTickSnapshot,
                setValue: v => config.VerboseTickSnapshot = v,
                name: () => ModEntry.T("config.VerboseTickSnapshot.name"),
                tooltip: () => ModEntry.T("config.VerboseTickSnapshot.description")
            );
            // After your existing AddNumberOption / AddBoolOption calls:
            api.AddSectionTitle(mod.ModManifest, () => ModEntry.T("config.section.title.uninstall"));
            api.AddParagraph(mod.ModManifest, () => ModEntry.T("config.uninstall.safety.paragraph"));
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => false,
                setValue: v =>
                {
                    if (v) RingSlotManager.DrainAllToPlayer();
                },
                name: () => ModEntry.T("config.uninstall.safety.button"),
                tooltip: () => ModEntry.T("config.uninstall.safety.button.tooltip"));
        }

        /// <summary>Reset every config field to its <see cref="ModConfig"/> default by
        /// copying from a fresh instance.  Defaults then live in exactly one place (the
        /// ModConfig property initializers), so GMCM's "Reset to Default" can't silently
        /// drift from them.  Previously each default was re-hardcoded in the reset lambda,
        /// and a missed update there reverted DragonToothStacking on reset.</summary>
        private static void ResetToDefaults(ModConfig config)
        {
            var defaults = new ModConfig();
            foreach (var prop in typeof(ModConfig).GetProperties())
            {
                if (prop.CanRead && prop.CanWrite)
                    prop.SetValue(config, prop.GetValue(defaults));
            }
        }
    }
}