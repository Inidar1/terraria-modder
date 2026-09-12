# Quick Keys

Adds rebindable shortcuts for torch placement, recall, quick stack, a ruler overlay, and optional inventory slots 11-20.

## Features

- **Auto Torch** places a suitable torch near the cursor and synchronizes the placed tile and inventory change in multiplayer.
- **Auto Recall** uses Recall Potions, Magic/Ice Mirrors, Cell Phones, and the Shellphone destination modes supported by Terraria.
- **Quick Stack** sends eligible inventory items to nearby chests.
- **Ruler** shows tile distances from the player.
- **Extended Hotbar** activates an item in inventory slots 11-20 once, then returns to the original hotbar slot.

## Keybinds

| Default | Action |
| --- | --- |
| Tilde | Auto Torch |
| Home | Auto Recall |
| End | Quick Stack |
| K | Toggle ruler |
| NumPad 1-9, 0 | Activate inventory slots 11-20 when Extended Hotbar is enabled |

All keybinds can be changed in the F6 Mod Menu.

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Enables Quick Keys |
| Show Messages | On | Shows action messages in chat |
| Extended Hotbar | Off | Enables the NumPad inventory shortcuts |
| Debug Logging | Off | Adds action details to the session log |

## Multiplayer

Quick Keys is client-only. Auto Torch sends Terraria tile and inventory synchronization packets; the other shortcuts use the local player's normal game actions.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/quick-keys/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.