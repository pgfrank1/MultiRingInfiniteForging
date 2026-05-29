# Multi Ring Infinite Forging

A Stardew Valley 1.6 SMAPI mod that lifts every artificial limit the forge
imposes — extra ring slots, unlimited ring combining, unlimited gem forging
on weapons, and stackable enchantments — all configurable.

[![Stardew Valley 1.6](https://img.shields.io/badge/Stardew%20Valley-1.6.15%2B-green.svg)](https://www.stardewvalley.net/)
[![SMAPI 4.0+](https://img.shields.io/badge/SMAPI-4.0%2B-blue.svg)](https://smapi.io/)
[![License: MIT](https://img.shields.io/badge/License-MPL-yellow.svg)](LICENSE)
[![Nexus Mods](https://img.shields.io/badge/Nexus-46798-orange.svg)](https://www.nexusmods.com/stardewvalley/mods/46798)

## What it does

- **Extra ring slots** — up to 16 additional ring slots in a collapsible panel
  next to your equipment column.  Available in both the inventory page and the
  forge menu.  Every vanilla ring effect works from extra slots: stat bonuses,
  light sources, on-kill procs, and combined rings.
- **Infinite ring combining** — merge rings without the vanilla cap.  Stack
  duplicates, combine already-combined rings, build whatever ring tower you
  like.
- **Configurable weapon forging cap** — set a custom gem forge limit per weapon
  (or -1 for unlimited).  Keep applying gems past vanilla limits.
- **Multiple enchantments** — stack named secondary enchantments instead of
  replacing them on each Prismatic Shard forge.
- **Dragon Tooth on endgame weapons** — re-roll the innate stat on Galaxy
  and Infinity weapons, which vanilla refuses.

Every feature is independently toggleable via `config.json` or the in-game
[Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098).

## Installation

1. Install [SMAPI](https://smapi.io) 4.0 or later.
2. Download the latest release from [Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/46798)
   (or build from source — see below).
3. Unzip into your `Stardew Valley/Mods/` folder.
4. Launch the game.  A `config.json` will be created on first run.
5. (Optional) Install [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098)
   to configure the mod in-game.

## Configuration

| Setting | Default | Description |
|---|---|---|
| `ExtraRingSlots` | `4` | How many extra ring slots to add (0–16). |
| `InfiniteCombining` | `true` | Allow combining rings beyond the vanilla cap. |
| `WeaponForgingCap` | `-1` | Maximum gem forges per weapon (`-1` = unlimited, `0` = none, `3` = vanilla, any `N` = cap). |
| `RemoveDiamondForgesCap` | `false` | When true, Diamond can keep adding gem enchantments even after all six types are present.  Off by default to preserve vanilla feel. |
| `MultipleEnchantments` | `true` | Stack secondary enchantments instead of replacing them. |
| `VerboseLogging` | `false` | Emit per-second diagnostic snapshots to `smapi-latest.log`.  Off by default; enable when reporting bugs. |

Any setting can be changed at any time — no save corruption risk.

## Uninstalling

Rings stored in extra slots live in this mod's save-data section, so you
should return them to your inventory before removing the mod.  Pick one:

- In-game: open Generic Mod Config Menu → Multi Ring Infinite Forging →
  toggle "Eject all extra rings now" and apply.
- SMAPI console: type `mrif_drain` and press Enter.
- Manually: drag every extra ring back into your bag.

Then save, quit, and delete the mod folder.

If you forgot and uninstalled with rings still in extra slots: reinstall, load
the save, run `mrif_drain`, save, and uninstall again.  The save file
preserves the mod's data while the mod is absent.

## Console commands

| Command | What it does |
|---|---|
| `mrif_stats` | Dumps the player's ring-derived stats (defense, attack, magnetic radius, crit, luck, immunity, etc.) to the console.  Useful for verifying that a ring's effect is actually applying. |
| `mrif_drain` | Returns every ring stored in extra slots and the overflow bucket to the player's inventory, dropping overflow at the player's feet.  Use before uninstalling the mod. |

## Building from source

Requirements:

- [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)
- A local Stardew Valley installation (the project uses the
  [Pathoschild.Stardew.ModBuildConfig](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md)
  NuGet package to find the game DLLs automatically).
