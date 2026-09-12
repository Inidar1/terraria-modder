# Whip Stacking

Enables Terraria 1.4.5.8's native maximum of five simultaneous whip tag effects.

## Behavior

Terraria's native <code>TagEffectStack</code> can retain up to five active effects, but players normally begin with capacity for one. While enabled, this mod sets the player's capacity to five after equipment effects are applied.

Terraria continues to own hit processing, tag duration, procs, replacement order, networking, and cleanup. Applying another distinct effect at capacity replaces the oldest effect. Accessories that normally raise tag capacity keep their other bonuses.

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Enables the five-effect capacity |

Open the F6 Mod Menu to change the setting; it applies immediately.

## Multiplayer

Whip Stacking is an optional client mod. Each player who wants the five-effect capacity installs it on their client.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/whip-stacking/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.