using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace MultiRingInfiniteForging
{
    /// <summary>
    /// Subset of Generic Mod Config Menu's API we actually use, copied from the
    /// upstream IGenericModConfigMenuApi.cs. We declare it locally so we don't take
    /// a hard dependency on the GMCM assembly.
    /// </summary>
    /// <summary>The API which lets other mods add a config UI through Generic Mod Config Menu.</summary>
    public interface IGenericModConfigMenuApi // Obsolete methods can be found in Framework/IGenericModConfigMenuApiWithObsoleteMethods
    {
        /*********
        ** Methods
        *********/
        /****
        ** Must be called first
        ****/
        /// <summary>Register a mod whose config can be edited through the UI.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="reset">Reset the mod's config to its default values.</param>
        /// <param name="save">Save the mod's current config to the <c>config.json</c> file.</param>
        /// <param name="titleScreenOnly">Whether the options can only be edited from the title screen.</param>
        /// <remarks>Each mod can only be registered once, unless it's deleted via <see cref="Unregister"/> before calling this again.</remarks>
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        /****
        ** Basic options
        ****/
        /// <summary>Add a section title at the current position in the form.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="text">The title text shown in the form.</param>
        /// <param name="tooltip">The tooltip text shown when the cursor hovers on the title, or <c>null</c> to disable the tooltip.</param>
        void AddSectionTitle(IManifest mod, Func<string> text, Func<string> tooltip = null);

        /// <summary>Add a paragraph of text at the current position in the form.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="text">The paragraph text to display.</param>
        void AddParagraph(IManifest mod, Func<string> text);

        /// <summary>Add a boolean option at the current position in the form.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="getValue">Get the current value from the mod config.</param>
        /// <param name="setValue">Set a new value in the mod config.</param>
        /// <param name="name">The label text to show in the form.</param>
        /// <param name="tooltip">The tooltip text shown when the cursor hovers on the field, or <c>null</c> to disable the tooltip.</param>
        /// <param name="fieldId">The unique field ID for use with <see cref="OnFieldChanged"/>, or <c>null</c> to auto-generate a randomized ID.</param>
        void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string> tooltip = null, string fieldId = null);

        /// <summary>Add an integer option at the current position in the form.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="getValue">Get the current value from the mod config.</param>
        /// <param name="setValue">Set a new value in the mod config.</param>
        /// <param name="name">The label text to show in the form.</param>
        /// <param name="tooltip">The tooltip text shown when the cursor hovers on the field, or <c>null</c> to disable the tooltip.</param>
        /// <param name="min">The minimum allowed value, or <c>null</c> to allow any.</param>
        /// <param name="max">The maximum allowed value, or <c>null</c> to allow any.</param>
        /// <param name="interval">The interval of values that can be selected.</param>
        /// <param name="formatValue">Get the display text to show for a value, or <c>null</c> to show the number as-is.</param>
        /// <param name="fieldId">The unique field ID for use with <see cref="OnFieldChanged"/>, or <c>null</c> to auto-generate a randomized ID.</param>
        void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string> tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string> formatValue = null, string fieldId = null);
        
        /****
        ** Multi-page management
        ****/
        /// <summary>Add a link to a page added via <see cref="AddPage"/> at the current position in the form.</summary>
        /// <param name="mod">The mod's manifest.</param>
        /// <param name="pageId">The unique ID of the page to open when the link is clicked.</param>
        /// <param name="text">The link text shown in the form.</param>
        /// <param name="tooltip">The tooltip text shown when the cursor hovers on the link, or <c>null</c> to disable the tooltip.</param>
        void AddPageLink(IManifest mod, string pageId, Func<string> text, Func<string> tooltip = null);

        /// <summary>Remove a mod from the config UI and delete all its options and pages.</summary>
        /// <param name="mod">The mod's manifest.</param>
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
                    config.VerboseLogging = false;
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
                max: 32,
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
            
            api.AddSectionTitle(mod.ModManifest, () => ModEntry.T("config.section.title.verbose.logging"));
            
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => config.VerboseLogging,
                setValue: v => config.VerboseLogging = v,
                name: () => ModEntry.T("config.verbose.logging.name"),
                tooltip: () => ModEntry.T("config.verbose.logging.description")
            );
            
            // After your existing AddNumberOption / AddBoolOption calls:
            api.AddSectionTitle(mod.ModManifest, () => "Uninstall safety");
            
            api.AddParagraph(mod.ModManifest, () =>
                "Rings stored in extra slots live in this mod's save data.  Before you " +
                "uninstall the mod, click the button below to return them all to your " +
                "inventory (or drop them at your feet if it's full).");
            
            api.AddPageLink(mod.ModManifest, "", () => "", () => ""); // optional separator
            
            api.AddBoolOption(
                mod: mod.ModManifest,
                getValue: () => false,
                setValue: v =>
                {
                    if (v) RingSlotManager.DrainAllToPlayer();
                },
                name: () => "Eject all extra rings now",
                tooltip: () => "Toggling this returns every ring in your extra slots to your inventory.  Toggle and apply.");
        }
    }
}