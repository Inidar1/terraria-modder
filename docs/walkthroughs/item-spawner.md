---
title: Item Spawner - Terraria 1.4.5.8 Mod Walkthrough
description: Build a searchable item catalog that includes vanilla and registered TerrariaModder items.
---

# Item Spawner Walkthrough

Item Spawner is an example of a searchable grid UI backed by both Terraria's content samples and Core's registered custom items.

## Catalog

The catalog starts with every valid Terraria item type and then adds runtime item types registered through TerrariaModder. A set prevents duplicate entries. Display names are read from initialized item samples and sorted alphabetically.

The catalog is rebuilt when the panel opens so custom items registered during content setup are available.

## Spawning

Press Insert to open the panel. Left-click requests one item, right-click requests the item's maximum stack, and Shift sends the result to inventory rather than the cursor.

In singleplayer the item is created locally through Terraria's inventory rules. In multiplayer the request is sent through Core's server command path, where administrator or per-mod permission is checked before the item is delivered.

## UI pattern

The mod uses <code>DraggablePanel</code>, <code>TextInput</code>, <code>ScrollView</code>, and a virtual grid. Only visible rows are drawn, so the full item database does not create a widget per item.

## Multiplayer

Works in singleplayer, Host & Play, and dedicated servers. Multiplayer use requires administrator permission or an Item Spawner grant.

## Source

See <code>src/ItemSpawner/</code> in the TerrariaModder repository.