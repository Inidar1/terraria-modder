# FPS Unlocked

Unlocks Terraria rendering beyond 60 FPS while its game simulation continues at 60 updates per second.

## Modes

| Mode | Behavior |
| --- | --- |
| VSync (Vanilla) | Restores Terraria's normal fixed 60 FPS behavior |
| Capped | Renders up to the configured limit from 30 to 1000 FPS |
| Uncapped | Renders without a mod-imposed frame limit |

Capped and Uncapped modes can interpolate players, NPCs, projectiles, world items, camera movement, rotations, held items, and related visual state between completed game updates. Turning interpolation off keeps simulation timing at 60 Hz and displays discrete positions at the selected render rate.

## Stability

The mod resets interpolation across teleports, large camera transitions, display-mode changes, world entry/exit, and shutdown. Draw cleanup restores temporary entity positions and respects Terraria's SpriteBatch and render-target lifecycle.

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Enables FPS Unlocked |
| Frame Rate Mode | VSync (Vanilla) | Selects vanilla, capped, or uncapped rendering |
| Max FPS | 144 | Limit used by Capped mode |
| Frame Interpolation | On | Smooths visual state between 60 Hz updates |
| Responsive Mouse | On | Updates the mouse on render frames while the game is focused |

Changes apply from the F6 Mod Menu without restarting.

## Multiplayer

FPS Unlocked is client-only and changes local rendering only.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/fps-unlocked/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.