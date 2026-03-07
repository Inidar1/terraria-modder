---
title: Asset Experience Mod - Custom Items Tutorial for Terraria 1.4.5
description: Walkthrough of the Asset Experience Mod for Terraria 1.4.5. Learn how to create custom items with textures, hooks, recipes, shop entries, and NPC drops using the Custom Assets system.
parent: Walkthroughs
nav_order: 11
---

# Asset Experience Mod Walkthrough

**Difficulty:** Intermediate
**Concepts:** Custom items, textures, behavior hooks, recipes, shops, NPC drops

The Asset Experience Mod is a hands-on tutorial mod demonstrating the Custom Assets system. It registers 13 custom items covering every major item category: weapons, armor, accessories, consumables, and materials.

> **Early Access:** This mod is available in beta on [Discord](https://discord.gg/VvVD5EeYsK). It serves as both a playable example and a reference implementation for modders building custom items.

## What It Does

Install the mod and press NumPad keys to spawn groups of test items:

- **NumPad1** - Melee weapons (fire sword with +100 bonus damage, ice sword that's intentionally broken)
- **NumPad2** - Ranged weapons (shadow bow that fires Jester Arrows, plasma gun with dynamic tooltips)
- **NumPad3** - Consumables (eternal potion that never runs out, buff potions)
- **NumPad4** - Armor set (160 total defense) + emerald accessory
- **NumPad5** - All 13 items at once
- **NumPad0** - Log all modded items in your inventory

Every item has a custom texture, and most demonstrate one or more behavior hooks (damage bonuses, projectile overrides, use prevention, tooltip injection, etc.).

## Key Concepts

### 1. Registering Custom Items

Custom items are registered through `context.RegisterItem()` with an `ItemDefinition` that sets all properties:

```csharp
context.RegisterItem("power-sword", new ItemDefinition
{
    DisplayName = "Power Test Sword",
    Tooltip = new[] { "Extreme damage test sword", "+100 bonus damage" },
    Texture = "assets/textures/tx_sword_fire.png",
    Damage = 500,
    KnockBack = 15f,
    UseTime = 20,
    UseAnimation = 20,
    UseStyle = 1,  // Swing
    Melee = true,
    AutoReuse = true,
    Width = 40,
    Height = 40,
    Rarity = 11,
    Value = 1000000
});
```

Each item gets a real runtime type ID (starting at 6145). This means custom items work exactly like vanilla items: they stack, sort, save/load, and display in the UI normally.

### 2. Custom Textures

Place PNG files in your mod's `assets/textures/` folder and reference them in the ItemDefinition:

```
my-mod/
├── assets/
│   └── textures/
│       ├── tx_sword_fire.png
│       ├── tx_potion_red.png
│       └── tx_helm_dragon.png
├── manifest.json
└── MyMod.dll
```

```csharp
context.RegisterItem("fire-sword", new ItemDefinition
{
    Texture = "assets/textures/tx_sword_fire.png",
    // ... other properties
});
```

Textures are loaded once and cached. The path is relative to your mod's folder.

### 3. Behavior Hooks

Hooks let custom items run code at specific points. Here are the hooks demonstrated by this mod:

**ModifyWeaponDamage** - Add bonus damage at calculation time:

```csharp
context.RegisterItem("power-sword", new ItemDefinition
{
    Damage = 500,
    ModifyWeaponDamage = (object player, ref int damage) =>
    {
        damage += 100;  // +100 bonus on top of base damage
    }
});
```

**CanUseItem** - Prevent an item from being used:

```csharp
context.RegisterItem("ice-sword", new ItemDefinition
{
    Damage = 300,
    CanUseItem = (player) => false  // Can never swing this sword
});
```

**OnShoot** - Override the projectile a ranged weapon fires:

```csharp
context.RegisterItem("shadow-bow", new ItemDefinition
{
    UseAmmo = 40,     // Arrow ammo type
    Shoot = 1,        // Default arrow projectile
    OnShoot = (player, origProj, speed) => 5  // Always fire Jester Arrows
});
```

**OnConsume** - Control whether a consumable is used up:

```csharp
context.RegisterItem("red-potion", new ItemDefinition
{
    Consumable = true,
    BuffType = 5,      // Ironskin
    BuffTime = 3600,   // 60 seconds
    OnConsume = (player) => false  // Never consumed - infinite uses
});
```

**ModifyTooltips** - Add dynamic lines to an item's tooltip:

```csharp
context.RegisterItem("plasma-gun", new ItemDefinition
{
    ModifyTooltips = (lines) =>
    {
        double time = Main.time;
        bool day = Main.dayTime;
        lines.Add($"Game time: {(day ? "Day" : "Night")} {time:F0}");
    }
});
```

**OnHitNPC** - React when a weapon hits an enemy:

```csharp
OnHitNPC = (player, npc, damage, kb, crit) =>
{
    _log.Info($"Hit for {damage} damage! Crit: {crit}");
}
```

**UpdateEquip** - Run logic each frame while an accessory is equipped:

```csharp
context.RegisterItem("emerald-amulet", new ItemDefinition
{
    Accessory = true,
    UpdateEquip = (player) =>
    {
        // Runs every frame while equipped
        // Add stat bonuses, trigger effects, etc.
    }
});
```

> **Tip:** Per-frame hooks like `UpdateEquip` and `OnHoldItem` fire 60 times per second. Use a counter to throttle logging or expensive operations.

### 4. Armor Slots

Armor items use slot IDs to borrow vanilla armor visuals:

```csharp
context.RegisterItem("dragon-helmet", new ItemDefinition
{
    DisplayName = "Dragon Helmet",
    Defense = 50,
    HeadSlot = 1,  // Borrows Iron Helmet visuals
    Width = 20,
    Height = 20,
    Rarity = 5
});

context.RegisterItem("dragon-chestplate", new ItemDefinition
{
    Defense = 60,
    BodySlot = 1,  // Borrows Iron Chestplate visuals
});

context.RegisterItem("dragon-leggings", new ItemDefinition
{
    Defense = 50,
    LegSlot = 1,   // Borrows Iron Greaves visuals
});
```

Set `HeadSlot`, `BodySlot`, or `LegSlot` to make items equippable in armor slots. The slot ID determines which vanilla armor sprites are used for the player's appearance.

### 5. Recipes

Register crafting recipes that use your custom items:

```csharp
context.RegisterRecipe(new RecipeDefinition
{
    Result = "asset-experience-mod:power-sword",  // modId:itemName
    ResultStack = 1,
    Ingredients = { { "IronBar", 10 }, { "Gel", 5 } },
    Station = "Anvils"
});
```

Ingredients can reference vanilla items by name or ID. The result uses `modId:itemName` format for custom items. Station names match Terraria's crafting station groups (Anvils, Furnaces, Bottles, WorkBenches, etc.).

### 6. Shop Entries and NPC Drops

Add custom items to NPC shops:

```csharp
context.AddShopItem(new ShopDefinition
{
    NpcType = 17,  // Merchant
    ItemId = "asset-experience-mod:emerald-amulet",
    Price = 100000  // 10 gold (in copper coins)
});
```

Register items as NPC drops:

```csharp
context.RegisterDrop(new DropDefinition
{
    NpcType = 1,   // Blue Slime
    ItemId = "asset-experience-mod:mythril-ore",
    Chance = 0.25f,
    MinStack = 1,
    MaxStack = 3
});
```

### 7. Spawning Items via Code

Items have real type IDs, so you can spawn them with `SetDefaults`:

```csharp
private void SpawnItem(string itemName)
{
    var player = Main.LocalPlayer;
    string fullId = $"{Id}:{itemName}";

    int runtimeType = ItemRegistry.GetRuntimeType(fullId);
    if (runtimeType < 0) return;

    for (int i = 0; i < 50; i++)
    {
        if (player.inventory[i] == null || player.inventory[i].IsAir)
        {
            player.inventory[i] = new Item();
            player.inventory[i].SetDefaults(runtimeType);
            return;
        }
    }
}
```

`ItemRegistry.GetRuntimeType()` resolves the `modId:itemName` string to the integer type ID that Terraria's item system uses internally.

## Complete Item Reference

| Item | Category | Key Hook | Effect |
|------|----------|----------|--------|
| Power Test Sword | Melee | ModifyWeaponDamage, OnHitNPC | +100 bonus damage, hit logging |
| Ice Test Sword | Melee | CanUseItem | Intentionally broken (can't swing) |
| Shadow Bow | Ranged | OnShoot | Converts any arrow to Jester Arrow |
| Plasma Gun | Ranged | ModifyTooltips, OnHoldItem | Dynamic tooltip with game time |
| Red Test Potion | Consumable | OnConsume | Gives Ironskin, never consumed |
| Blue Test Potion | Consumable | OnUse | Gives Wrath, logs on use |
| Green Test Potion | Consumable | (none) | Gives Regeneration (vanilla behavior) |
| Dragon Helmet | Armor | (none) | 50 defense, head slot |
| Dragon Chestplate | Armor | (none) | 60 defense, body slot |
| Dragon Leggings | Armor | (none) | 50 defense, legs slot |
| Emerald Amulet | Accessory | UpdateEquip | Periodic tick logging |
| Custom Mythril Ore | Material | (none) | Stackable, used in recipes, drops from Blue Slimes |
| Shadow Ore | Material | (none) | Stackable material |

## Configuration

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `enabled` | bool | true | Load custom items and enable keybinds |

## Lessons Learned

1. **Items get real type IDs** - Custom items are indistinguishable from vanilla at runtime. Sorting, stacking, saving all work automatically.
2. **One registration call does everything** - `RegisterItem` handles type allocation, texture loading, tooltip building, and hook wiring in a single `ItemDefinition`.
3. **Hooks are optional** - Items without hooks (Green Test Potion, armor pieces, ores) work with pure vanilla behavior. Only add hooks when you need custom logic.
4. **Throttle per-frame hooks** - `UpdateEquip` and `OnHoldItem` fire every frame. Use a counter (`if (counter++ % 300 == 0)`) to avoid log spam or expensive work.
5. **CanUseItem returning false is powerful** - It completely prevents item use while keeping the item visible and equippable. Useful for conditional items.
6. **OnConsume returning false creates infinite consumables** - The buff still applies but the item is never consumed.
7. **Textures are simple** - Drop PNGs in `assets/textures/`, reference the path in the definition, done.
8. **Recipes, shops, and drops are separate registrations** - Keep item definitions focused on the item itself, register integration points separately.

## Getting the Mod

Asset Experience Mod is currently in early access. Join the [Discord](https://discord.gg/VvVD5EeYsK) to download it and share feedback.

For the full Custom Assets API reference, see [Core API Reference - Custom Assets System](../core-api-reference.md#custom-assets-system).
