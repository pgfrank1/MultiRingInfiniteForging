# Changelog

Release notes for **Multi Ring Infinite Forging** ([Nexus 46798](https://www.nexusmods.com/stardewvalley/mods/46798)). Newest first; mirrors the Nexus changelog tab.

## 1.1.1
- Fixed a freeze that could occur when forging a Dragon Tooth onto a fully-stacked weapon with Dragon Tooth Stacking turned off. The game's innate-enchantment reroll could loop forever, spiking CPU and writing a multi-gigabyte log; these crafts now resolve in a single roll.
- Internal modernization pass: ring effects now apply through one idempotent reconcile step, the forge patches are consolidated into a single dispatch, and the extra-ring panels are rebuilt as per-menu instances. No change to in-game behavior or settings.

## 1.1.0
- Multiplayer & split-screen support - each player has their own extra ring slots and panel
- Dragon Tooth Stacking (new option, default ON): innate enchantments add and level up instead of replacing; the tooth dims when every type is capped
- Always Max Dragon Tooth Stat (new option, default ON): innates land at their maximum level, and each craft raises the weapon's existing innates to their caps
- Pick Your Enchantment (Forge Menu Choice) integration: its carousel opens for the Dragon Tooth crafts this mod handles (Galaxy/Infinity weapons; all weapons with stacking), with at-cap options filtered and true levels shown
- Forge right-click quality-of-life: right-click sends rings/tools/weapons/ingredients to the correct forge slot and takes them back out; works on controller (X)
- Built-in Napalm Mummies compatibility: napalm rings in extra slots or nested combined rings detonate crumpled mummies

## 1.0.6
- More Rings compatibility. Rings from the More Rings mod now grant their effects when worn in the extra ring slots this mod adds - not just the two vanilla slots. Works with combined rings too, and stacking rings (Regeneration, Refreshing, Quality+) scale correctly with how many you wear.

## 1.0.5
- The extra ring panel now scrolls when you have more rings than fit on screen - supports mouse wheel, click on the up/down arrows, and full D-pad/controller navigation
- The forge menu panel now dynamically adjusts its column count based on viewport width, so it stays on-screen at narrow resolutions instead of being clipped
- Both ring panels now correctly reposition when the game window is resized, preserving scroll position and open/closed state

## 1.0.4
- Fixed ring panel in the forge menu not dimming rings that are incompatible when Infinite Ring Combining is disabled
- Fixed Combined Duplicate Ring Cap not blocking ring pickups and swaps in the forge menu (inventory slots, panel slots, Ring1/Ring2 equipment icons, and the cursor swap path)
- Fixed Combined Duplicate Ring Cap not dimming incompatible rings in the regular inventory (via HighlightItems patch)
- Added translations for AddCombinedDuplicateRingCap to all 11 supported languages
- Replaced Infinite Weapon Forging toggle with Weapon Forging Cap - a configurable numeric limit with an Unlimited option
- Updated max ring slots from 32 to 36. Just looked better for the inventory ring menu

## 1.0.3-1
- Fixed zu.json file. Had incorrect json formatting.

## 1.0.3
- Fixed Dragon Tooth forge being blocked on non-scythe weapons. All MeleeWeapons can now re-roll their innate enchantment as intended.
- Added Combined Duplicate Ring Cap - when enabled with Infinite Ring Combining, prevents stacking duplicate rings inside a combined ring. Also prevents the same ring from being combined with another ring of the same type (Ruby ring + Ruby ring) like vanilla.

## 1.0.2
- Fixed: reducing the Extra Ring Slots count (via config edit, deleted config.json, or GMCM) no longer permanently loses rings in the trimmed slots. Orphaned rings are now preserved in save data and restored when you raise the slot count again, or returned to your inventory with mrif_drain / the GMCM eject button.

## 1.0.1
- New console command mrif_drain - returns every ring stored in extra slots back to your inventory, with overflow dropped at your feet. Run this before uninstalling the mod to avoid orphaning rings in the save data.
- Added an "Eject all extra rings now" button in the Generic Mod Config Menu page for players who'd rather not use the console.
- Setting Extra Ring Slots to a lower number via GMCM now correctly drains the excess rings to your inventory or to the ground if your bag is full.
