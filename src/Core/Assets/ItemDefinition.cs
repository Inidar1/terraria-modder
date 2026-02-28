using System;
using System.Collections.Generic;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Delegate for ModifyWeaponDamage hook — allows modifying damage by ref.
    /// </summary>
    public delegate void ModifyDamageDelegate(object player, ref int damage);

    /// <summary>
    /// Defines a custom item's properties. All vanilla Item fields can be set here.
    /// At runtime, items with these definitions get real type IDs (6145+) and behave
    /// identically to vanilla items — SetDefaults fills in all properties from this definition.
    /// </summary>
    public class ItemDefinition
    {
        // Identity
        public string DisplayName { get; set; }
        public string[] Tooltip { get; set; }
        public string Texture { get; set; } // relative path within mod folder, e.g. "assets/textures/sword.png"

        // Combat
        public int Damage { get; set; }
        public float KnockBack { get; set; }
        public int UseTime { get; set; } = 20;
        public int UseAnimation { get; set; } = 20;
        public int UseStyle { get; set; }
        public int Crit { get; set; } = 4;
        public int Mana { get; set; }
        public bool AutoReuse { get; set; }

        // Damage type flags
        public bool Melee { get; set; }
        public bool Ranged { get; set; }
        public bool Magic { get; set; }
        public bool Summon { get; set; }

        // Projectile
        public int Shoot { get; set; }
        public float ShootSpeed { get; set; }

        // Defense / Equipment
        public int Defense { get; set; }
        public bool Accessory { get; set; }
        public bool Vanity { get; set; }
        public int HeadSlot { get; set; } = -1;
        public int BodySlot { get; set; } = -1;
        public int LegSlot { get; set; } = -1;

        // Consumable / Potion
        public int MaxStack { get; set; } = 1;
        public bool Consumable { get; set; }
        public bool Potion { get; set; }
        public int HealLife { get; set; }
        public int HealMana { get; set; }
        public int BuffType { get; set; }
        public int BuffTime { get; set; }

        // Ammo
        public int Ammo { get; set; }
        public int UseAmmo { get; set; }

        // Placement
        public int CreateTile { get; set; } = -1;
        public int CreateWall { get; set; } = -1;
        public int PlaceStyle { get; set; }

        // Visual
        public int Width { get; set; } = 20;
        public int Height { get; set; } = 20;
        public float Scale { get; set; } = 1f;
        public int HoldStyle { get; set; }
        public bool NoUseGraphic { get; set; }
        public bool NoMelee { get; set; }
        public bool Channel { get; set; }

        // Economy
        public int Rarity { get; set; }
        public int Value { get; set; }

        // Material flag (can be used in recipes)
        public bool Material { get; set; }

        // Journey Mode
        public int JourneyResearchCount { get; set; } = 1;

        // ── Runtime Behavior Hooks ──
        // All hooks receive Terraria types as object to avoid compile-time XNA dependencies.

        /// <summary>Return false to prevent item use. (player) → bool</summary>
        public Func<object, bool> CanUseItem { get; set; }

        /// <summary>Called when item use starts. Return false to cancel the use effect. (player) → bool</summary>
        public Func<object, bool> OnUse { get; set; }

        /// <summary>Called after hitting an NPC. (player, npc, damage, knockback, crit)</summary>
        public Action<object, object, int, float, bool> OnHitNPC { get; set; }

        /// <summary>Modify weapon damage by ref. (player, ref damage)</summary>
        public ModifyDamageDelegate ModifyWeaponDamage { get; set; }

        /// <summary>Override projectile type on shoot. (player, originalProjType, shootSpeed) → overrideType or null</summary>
        public Func<object, int, float, int?> OnShoot { get; set; }

        /// <summary>Return false to prevent consuming the item (eternal potion). (player) → bool</summary>
        public Func<object, bool> OnConsume { get; set; }

        /// <summary>Called per frame while item is equipped as accessory. (player)</summary>
        public Action<object> UpdateEquip { get; set; }

        /// <summary>Called per frame while item is held (selected). (player)</summary>
        public Action<object> OnHoldItem { get; set; }

        /// <summary>Modify tooltip lines dynamically. (lines list)</summary>
        public Action<List<string>> ModifyTooltips { get; set; }

        /// <summary>
        /// Validate the definition. Returns null if valid, error message if not.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(DisplayName))
                return "DisplayName is required";
            if (Width <= 0 || Height <= 0)
                return "Width and Height must be positive";
            if (MaxStack < 1)
                return "MaxStack must be >= 1";
            return null;
        }
    }

    /// <summary>
    /// Recipe for a custom item.
    /// </summary>
    public class RecipeDefinition
    {
        /// <summary>Result item: "modid:itemname" for custom, or vanilla item name/ID.</summary>
        public string Result { get; set; }
        public int ResultStack { get; set; } = 1;

        /// <summary>
        /// Ingredients: key = "modid:itemname" or vanilla item name, value = count.
        /// </summary>
        public Dictionary<string, int> Ingredients { get; set; } = new Dictionary<string, int>();

        /// <summary>Crafting station tile name (e.g., "WorkBench", "Anvil", "Furnace").</summary>
        public string Station { get; set; }
    }

    /// <summary>
    /// NPC shop item definition.
    /// </summary>
    public class ShopDefinition
    {
        /// <summary>NPC type ID that sells this item.</summary>
        public int NpcType { get; set; }
        /// <summary>Item: "modid:itemname" for custom, or vanilla item name/ID.</summary>
        public string ItemId { get; set; }
        /// <summary>Custom price in copper (0 = use item value).</summary>
        public int Price { get; set; }
    }

    /// <summary>
    /// NPC drop definition.
    /// </summary>
    public class DropDefinition
    {
        /// <summary>NPC type ID that drops this item.</summary>
        public int NpcType { get; set; }
        /// <summary>Item: "modid:itemname" for custom, or vanilla item name/ID.</summary>
        public string ItemId { get; set; }
        /// <summary>Drop chance (0.0 to 1.0).</summary>
        public float Chance { get; set; } = 1.0f;
        /// <summary>Min stack on drop.</summary>
        public int MinStack { get; set; } = 1;
        /// <summary>Max stack on drop.</summary>
        public int MaxStack { get; set; } = 1;
    }
}
