using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Harmony prefix on Item.SetDefaults(int Type, ItemVariant variant) that handles custom types.
    ///
    /// When Type >= VanillaItemCount and has a registered definition:
    ///   - Calls ResetStats(Type) to clear all properties
    ///   - Sets type field to our custom type (bypassing the "type >= Count → 0" gate)
    ///   - Fills in all properties from the ItemDefinition
    ///   - Returns false to skip vanilla SetDefaults (which would set type to 0)
    ///
    /// For vanilla types, returns true to let vanilla handle normally.
    /// </summary>
    internal static class SetDefaultsPatch
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static bool _firstCallLogged;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.setdefaults");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                PatchOriginalProperties();

                // Item.SetDefaults(int Type, ItemVariant variant = null)
                var itemType = typeof(Item);
                var itemVariantType = itemType.Assembly.GetType("Terraria.ID.ItemVariant");

                MethodInfo setDefaultsMethod = null;
                // Find the overload with (int, ItemVariant)
                foreach (var m in itemType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "SetDefaults") continue;
                    var parms = m.GetParameters();
                    if (parms.Length == 2 && parms[0].ParameterType == typeof(int))
                    {
                        setDefaultsMethod = m;
                        break;
                    }
                }

                if (setDefaultsMethod == null)
                {
                    // Try single-param overload
                    setDefaultsMethod = itemType.GetMethod("SetDefaults",
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(int) }, null);
                }

                if (setDefaultsMethod == null)
                {
                    _log?.Error("[SetDefaultsPatch] Item.SetDefaults method not found");
                    return;
                }

                var prefix = typeof(SetDefaultsPatch).GetMethod(nameof(Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                _harmony.Patch(setDefaultsMethod, prefix: new HarmonyMethod(prefix));
                _applied = true;
                _log?.Info("[SetDefaultsPatch] Patched Item.SetDefaults");
            }
            catch (Exception ex)
            {
                _log?.Error($"[SetDefaultsPatch] Failed to apply: {ex.Message}");
            }
        }

        /// <summary>
        /// Harmony prefix for Item.SetDefaults(int Type, ...).
        /// Returns false (skip original) for custom types, true for vanilla.
        /// </summary>
        private static bool Prefix(Item __instance, int Type)
        {
            if (!_firstCallLogged)
            {
                _firstCallLogged = true;
                _log?.Info($"[SetDefaultsPatch] First call: Type={Type}, VanillaItemCount={ItemRegistry.VanillaItemCount}");
            }

            // Only intercept custom types
            if (Type < ItemRegistry.VanillaItemCount) return true;

            var definition = ItemRegistry.GetDefinition(Type);
            if (definition == null) return true; // Unknown type, let vanilla handle (will become air)

            try
            {
                // Call ResetStats to clear all properties (same as vanilla does first)
                __instance.ResetStats(Type);

                // Set the type directly — bypasses the "type >= Count → 0" gate
                __instance.type = Type;

                // Apply all properties from definition
                ApplyDefinition(__instance, definition);

                // Mark as material if flagged
                var materialSets = GetMaterialSet();
                if (materialSets != null && Type < materialSets.Length)
                    materialSets[Type] = definition.Material;

                __instance.material = definition.Material;

                // RebuildTooltip — vanilla calls this at end of SetDefaults (line 48522).
                // Since we skip vanilla, we must call it ourselves.
                // It reads from Lang._itemTooltipCache[type] which we populated via SetItemTooltip.
                __instance.RebuildTooltip();

                return false; // Skip vanilla SetDefaults
            }
            catch (Exception ex)
            {
                _log?.Error($"[SetDefaultsPatch] Error for type {Type}: {ex.Message}");
                return true; // Fall through to vanilla (will become air)
            }
        }

        private static void ApplyDefinition(Item item, ItemDefinition def)
        {
            // Combat
            item.damage = def.Damage;
            item.knockBack = def.KnockBack;
            item.useTime = def.UseTime;
            item.useAnimation = def.UseAnimation;
            item.useStyle = def.UseStyle;
            item.crit = def.Crit;
            item.mana = def.Mana;
            item.autoReuse = def.AutoReuse;

            // Damage types
            item.melee = def.Melee;
            item.ranged = def.Ranged;
            item.magic = def.Magic;
            item.summon = def.Summon;

            // Projectile
            item.shoot = def.Shoot;
            item.shootSpeed = def.ShootSpeed;

            // Defense / Equipment
            item.defense = def.Defense;
            item.accessory = def.Accessory;
            item.vanity = def.Vanity;
            if (def.HeadSlot >= 0) item.headSlot = def.HeadSlot;
            if (def.BodySlot >= 0) item.bodySlot = def.BodySlot;
            if (def.LegSlot >= 0) item.legSlot = def.LegSlot;

            // Consumable / Potion
            item.maxStack = def.MaxStack;
            item.consumable = def.Consumable;
            item.potion = def.Potion;
            item.healLife = def.HealLife;
            item.healMana = def.HealMana;
            item.buffType = def.BuffType;
            item.buffTime = def.BuffTime;

            // Ammo
            item.ammo = def.Ammo;
            item.useAmmo = def.UseAmmo;

            // Placement
            item.createTile = def.CreateTile;
            item.createWall = def.CreateWall;
            item.placeStyle = def.PlaceStyle;

            // Visual
            item.width = def.Width;
            item.height = def.Height;
            item.scale = def.Scale;
            item.holdStyle = def.HoldStyle;
            item.noUseGraphic = def.NoUseGraphic;
            item.noMelee = def.NoMelee;
            item.channel = def.Channel;

            // Economy
            item.rare = def.Rarity;
            item.value = def.Value;

            // Set name and tooltip via reflection (Lang caches)
            SetItemName(item.type, def.DisplayName);
            SetItemTooltip(item.type, def.Tooltip);
            _log?.Info($"[SetDefaultsPatch] Applied def for type {item.type}: name='{def.DisplayName}', tooltip={def.Tooltip?.Length ?? 0} lines");

            // Ensure stack is at least 1 for non-air items
            if (item.stack <= 0) item.stack = 1;
        }

        private static bool[] _materialSet;
        private static bool[] GetMaterialSet()
        {
            if (_materialSet != null) return _materialSet;
            try
            {
                var setsType = typeof(Terraria.ID.ItemID).GetNestedType("Sets", BindingFlags.Public);
                var field = setsType?.GetField("IsAMaterial", BindingFlags.Public | BindingFlags.Static);
                _materialSet = field?.GetValue(null) as bool[];
            }
            catch { }
            return _materialSet;
        }

        private static void SetItemName(int type, string name)
        {
            try
            {
                var langType = typeof(Terraria.Lang);
                var cacheField = langType.GetField("_itemNameCache", BindingFlags.NonPublic | BindingFlags.Static);
                if (cacheField == null) return;

                var cache = cacheField.GetValue(null) as Array;
                if (cache == null || type >= cache.Length) return;

                // Create a LocalizedText instance
                var localizedTextType = typeof(Terraria.Localization.LocalizedText);
                var ctor = localizedTextType.GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(string) }, null);

                if (ctor != null)
                {
                    var text = ctor.Invoke(new object[] { $"ItemName.Custom_{type}", name ?? "" });
                    cache.SetValue(text, type);
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[SetDefaultsPatch] Failed to set item name for type {type}: {ex.Message}");
            }
        }

        private static void SetItemTooltip(int type, string[] tooltip)
        {
            try
            {
                var langType = typeof(Terraria.Lang);
                var cacheField = langType.GetField("_itemTooltipCache", BindingFlags.NonPublic | BindingFlags.Static);
                if (cacheField == null) return;

                var cache = cacheField.GetValue(null) as Array;
                if (cache == null || type >= cache.Length) return;

                var tooltipType = cache.GetType().GetElementType();
                if (tooltipType == null) return;

                string[] lines = tooltip != null && tooltip.Length > 0 ? tooltip : new[] { "" };

                // Use ItemTooltip.FromHardcodedText(params string[]) — public static method
                // This creates tooltips that bypass localization validation
                var fromHardcoded = tooltipType.GetMethod("FromHardcodedText",
                    BindingFlags.Public | BindingFlags.Static);

                if (fromHardcoded != null)
                {
                    var instance = fromHardcoded.Invoke(null, new object[] { lines });
                    if (instance != null)
                    {
                        cache.SetValue(instance, type);
                        return;
                    }
                }

                // Fallback: create via hardcoded lines constructor (private)
                var ctor = tooltipType.GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string[]) }, null);
                if (ctor != null)
                {
                    var instance = ctor.Invoke(new object[] { lines });
                    cache.SetValue(instance, type);
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[SetDefaultsPatch] Failed to set tooltip for type {type}: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch Item.OriginalRarity, OriginalDamage, OriginalDefense property getters.
        /// These access ContentSamples.ItemsByType[type] which may crash for custom types
        /// even after we populate the dictionary (race conditions, re-initialization, etc.).
        /// Our prefixes return the item's own values for custom types, bypassing the dictionary.
        /// </summary>
        private static void PatchOriginalProperties()
        {
            var itemType = typeof(Item);

            foreach (var propName in new[] { "OriginalRarity", "OriginalDamage", "OriginalDefense" })
            {
                try
                {
                    var prop = itemType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    var getter = prop?.GetGetMethod();
                    if (getter == null) continue;

                    var prefix = typeof(SetDefaultsPatch).GetMethod($"{propName}_Prefix",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (prefix == null) continue;

                    _harmony.Patch(getter, prefix: new HarmonyMethod(prefix));
                    _log?.Debug($"[SetDefaultsPatch] Patched Item.{propName}");
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[SetDefaultsPatch] Failed to patch {propName}: {ex.Message}");
                }
            }
        }

        private static bool OriginalRarity_Prefix(Item __instance, ref int __result)
        {
            if (__instance.type < ItemRegistry.VanillaItemCount) return true;
            __result = __instance.rare;
            return false;
        }

        private static bool OriginalDamage_Prefix(Item __instance, ref int __result)
        {
            if (__instance.type < ItemRegistry.VanillaItemCount) return true;
            __result = __instance.damage;
            return false;
        }

        private static bool OriginalDefense_Prefix(Item __instance, ref int __result)
        {
            if (__instance.type < ItemRegistry.VanillaItemCount) return true;
            __result = __instance.defense;
            return false;
        }
    }
}
