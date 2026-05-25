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
                    config.InfiniteReforging = true;
                    config.InfiniteWeaponForging = true;
                    config.MultipleEnchantments = true;
                },
                save: save
            );

            api.AddSectionTitle(mod.ModManifest, () => "Ring Slots");

            api.AddNumberOption(
                mod: mod.ModManifest,
                getValue: () => config.ExtraRingSlots,
                setValue: v =>
                {
                    config.ExtraRingSlots = v;
                    RingSlotManager.EnsureSize();
                    InventoryPagePatches.RebuildForActiveMenu();
                },
                name: () => "Extra ring slots",
                tooltip: () => "How many additional ring slots to add beyond the vanilla 2.",
                min: 0,
                max: 16,
                interval: 1
            );

            api.AddSectionTitle(mod.ModManifest, () => "Forge");

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.InfiniteCombining,
                setValue: v => config.InfiniteCombining = v,
                name: () => "Infinite ring combining",
                tooltip: () => "Allow combining more than two rings at the forge."
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.InfiniteReforging,
                setValue: v => config.InfiniteReforging = v,
                name: () => "Infinite reforging",
                tooltip: () => "Allow reforging rings with a Prismatic Shard without the iteration cap."
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.InfiniteWeaponForging,
                setValue: v => config.InfiniteWeaponForging = v,
                name: () => "Infinite weapon forging",
                tooltip: () => "Remove the 3-gem cap on weapon forging.  You can keep applying gems beyond level 3."
            );

            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.MultipleEnchantments,
                setValue: v => config.MultipleEnchantments = v,
                name: () => "Multiple enchantments",
                tooltip: () => "Allow stacking multiple enchantments on the same weapon or tool instead of replacing the existing one."
            );
        }
    }
}