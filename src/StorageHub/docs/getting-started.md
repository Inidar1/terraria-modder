# Getting Started

This guide gets a minimal Storage Hub network running quickly.

## 1. Enable the Mod

- Open TerrariaModder menu (`F6`).
- Ensure `Storage Hub` is enabled.

Default keybind:

- `F5` = toggle UI (only relevant when `dedicatedBlocksOnly=false`).

## 2. Know Your Mode

`storage-hub` supports two workflows:

- `dedicatedBlocksOnly=true` (default):
  - network is built from Storage Hub custom blocks (Core, Drive, Access, etc.).
- `dedicatedBlocksOnly=false`:
  - legacy chest-registration flow is also allowed.

This doc focuses on dedicated-block mode.

## 3. Acquire Starter Parts

Minimum functional setup:

- `1x Storage Core`
- `1x Storage Drive`
- `1x Basic Storage Disk`

Helpful additions:

- `Storage Access` (convenient terminal)
- `Storage Component` + `Storage Connector` (for routing)
- `Disk Upgrader`

## 4. Place and Connect

1. Place a `Storage Core`.
2. Place at least one `Storage Drive`.
3. Ensure the drive is connected to the core through valid adjacency/pathing:
   - components are 2x2 network nodes
   - connectors are 1x1 link pieces
4. Insert a storage disk into the drive (drive has 8 disk slots).

If the core/access says there is no connected core or no drive, your path is broken or missing a drive.

## 5. Open the Network

- Right-click `Storage Core` or `Storage Access` to open the main UI.
- Right-click `Storage Crafting Interface` to open directly on crafting.

## 6. Upgrade a Disk

1. Place `Disk Upgrader`.
2. Right-click it to open the upgrader UI.
3. Put a disk in the slot.
4. If materials are available, press `Upgrade Disk`.

Disk contents are preserved across upgrades.

## 7. Early Troubleshooting

- "Not connected to a Storage Core":
  - check component/connector path between terminal and core.
- "A Storage Core needs at least one connected Storage Drive":
  - add or reconnect a drive.
- Missing recipes for some blocks:
  - currently expected for certain items in this fork.

