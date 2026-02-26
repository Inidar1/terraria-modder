---
title: StorageHub Disk Storage Format (Technical)
description: Technical reference for StorageHub disk identity and persistent drive storage format.
parent: Walkthroughs
nav_order: 5.6
---

# StorageHub Disk Storage Format

This document describes the current StorageHub disk persistence model at code level.

## Scope

- Applies to StorageHub drive-backed storage (`DriveStorageState`).
- File is world-scoped (not character-scoped).
- Disk identity currently uses the Terraria item `prefix` byte on the disk item itself.

## Storage File Location

Drive storage is persisted at:

```text
{modFolder}/worlds/{sanitizedWorldName}/drive-storage.dat
```

Notes:

- `sanitizedWorldName` replaces invalid filename characters with `_`.
- Save uses atomic-ish rotation:
  - write `{file}.tmp`
  - move existing file to `{file}.bak` (if present)
  - move `.tmp` into place

## File Format

Header:

```text
# StorageHub disk storage v2
```

Data records are pipe-delimited:

- Disk header:

```text
D|{disk_item_type}|{disk_uid}
```

- Disk item entry:

```text
I|{disk_item_type}|{disk_uid}|{item_id}|{item_prefix}|{stack}
```

Example:

```text
# StorageHub disk storage v2
D|6151|7
I|6151|7|74|0|999
I|6151|7|757|81|1
D|6152|3
I|6152|3|3380|0|245
```

## Validation Rules on Load

Lines are skipped when invalid. Important checks:

- Empty lines and `#` comments are ignored.
- `disk_uid` must be `1..255`.
- `disk_item_type` must resolve to a known disk tier.
- For item rows, `item_id > 0` and `stack > 0`.
- If `item_prefix` fails parse, it defaults to `0`.

Implementation details:

- Duplicate `D` records for the same identity are ignored.
- `I` records can create the disk implicitly (`EnsureDisk`) even if a `D` line was missing.

## Disk Identity Model

Disk identity key in memory:

```text
{diskItemType}:{diskUid}
```

`diskUid` source:

- Stored on the disk item's Terraria `prefix` field.
- Read when the disk is discovered in a drive slot.
- If missing and assignment is allowed, a new UID is allocated and written back to the item prefix.

UID allocation:

- Sequential scan `1..255`.
- Per disk item type (Basic/Improved/Advanced/Quantum each have their own UID space).

## Upgrade Migration Semantics

When upgrading disk tier:

- If target tier+UID key is free, keep same UID.
- If occupied, allocate a new UID in target tier.
- Copy all disk item records (`itemId`, `prefix`, `stack`) to new identity.
- Remove old identity.

## Prefix Semantics

Two different prefix concepts exist:

- Disk item `prefix`: currently used as disk UID (identity metadata).
- Stored item `item_prefix`: normal Terraria item modifier for items inside disk contents.

StorageHub now suppresses affix naming/rolling behavior for disk items so UID bytes are not shown to players as item modifiers.

## Current Constraints

Because UID is stored in a byte-sized prefix field:

- Maximum `255` unique disk identities per disk item type per world.
- External systems that rewrite disk item prefix can orphan or remap disk identities.

This is a known technical debt and is tracked in StorageHub TODOs for eventual UUID-based metadata replacement.

