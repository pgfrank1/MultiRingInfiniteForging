using System;
using StardewModdingAPI;

namespace MultiRingInfiniteForging
{
    /// <summary>
    /// Subset of Generic Mod Config Menu's API we actually use, copied from the
    /// upstream IGenericModConfigMenuApi.cs. We declare it locally so we don't take
    /// a hard dependency on the GMCM assembly.
    /// </summary>
    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

        void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue,
            Func<string> name, Func<string>? tooltip = null, string? fieldId = null);

        void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue,
            Func<string> name, Func<string>? tooltip = null,
            int? min = null, int? max = null, int? interval = null,
            Func<int, string>? formatValue = null, string? fieldId = null);

        void Unregister(IManifest mod);
    }

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
                reset: () =>
                {
                    config.ExtraRingSlots = 4;
                    config.InfiniteCombining = true;
                    config.InfiniteWeaponForging = true;
                    config.MultipleEnchantments = true;
                },
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
                max: 16,
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
                getValue: () => config.InfiniteWeaponForging,
                setValue: v => config.InfiniteWeaponForging = v,
                name: () => ModEntry.T("config.infiniteWeaponForging.name"),
                tooltip: () => ModEntry.T("config.infiniteWeaponForging.description")
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.MultipleEnchantments,
                setValue: v => config.MultipleEnchantments = v,
                name: () => ModEntry.T("config.multipleEnchantments.name"),
                tooltip: () => ModEntry.T("config.multipleEnchantments.description")
            );
        }
    }
}