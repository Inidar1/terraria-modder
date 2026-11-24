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
        private static Type _serverItemType; // TerrariaServer.exe's Item type (different from Terraria.exe's)

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
                _log?.Info("[SetDefaultsPatch] Patched Item.SetDefaults");

                // Also patch TerrariaServer.exe's Item.SetDefaults (for dedicated server).
                // On ded server, Item.NewItem creates items using TerrariaServer's Item class.
                // Without this, SetDefaults(customType) falls through to vanilla which sets type=0 (air).
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name;
                    if (asmName != "TerrariaServer" && asmName != "Terraria") continue;
                    var serverItemType = asm.GetType("Terraria.Item");
                    if (serverItemType == null || serverItemType == itemType) continue;

                    MethodInfo serverSetDefaults = null;
                    foreach (var m in serverItemType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "SetDefaults") continue;
                        var parms = m.GetParameters();
                        if (parms.Length == 2 && parms[0].ParameterType == typeof(int))
                        {
                            serverSetDefaults = m;
                            break;
                        }
                    }
                    if (serverSetDefaults == null)
                    {
                        serverSetDefaults = serverItemType.GetMethod("SetDefaults",
                            BindingFlags.Public | BindingFlags.Instance, null,
                            new[] { typeof(int) }, null);
                    }
                    if (serverSetDefaults != null && serverSetDefaults != setDefaultsMethod)
                    {
                        // Use a separate prefix that takes object __instance instead of Item __instance.
                        // TerrariaServer.Item is a different Type from Terraria.Item — Harmony can't
                        // inject cross-assembly typed parameters. The server prefix uses reflection.
                        var serverPrefix = typeof(SetDefaultsPatch).GetMethod(nameof(ServerPrefix),
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _serverItemType = serverItemType;
                        _harmony.Patch(serverSetDefaults, prefix: new HarmonyMethod(serverPrefix));
                        _log?.Info($"[SetDefaultsPatch] Patched Item.SetDefaults ({asmName} assembly, server prefix)");
                    }
                }

                _applied = true;
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
            if (definition == null)
            {
                // I3: Check if this is a KnownUnknown — server-known Optional mod item client doesn't have
                if (ItemRegistry.IsKnownUnknown(Type, out string fullId))
                {
                    try
                    {
                        __instance.ResetStats(Type);
                        __instance.type = Type;
                        __instance.stack = 1;
                        __instance.maxStack = 1;
                        __instance.rare = 0;
                        __instance.value = 0;
                        SetItemName(Type, $"[{fullId}]");
                        SetItemTooltip(Type, new[] { $"This item requires [{fullId.Split(':')[0]}] to be installed." });
                        __instance.RebuildTooltip();
                        return false; // skip vanilla (which would set type to 0)
                    }
                    catch (Exception ex)
                    {
                        _log?.Debug($"[SetDefaultsPatch] KnownUnknown placeholder error for type {Type}: {ex.Message}");
                    }
                }
                return true; // Unknown type, let vanilla handle (will become air)
            }

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

        /// <summary>
        /// Server prefix for TerrariaServer.exe's Item.SetDefaults. Uses object __instance
        /// and reflection because TerrariaServer.Item is a different Type from Terraria.Item.
        /// Harmony can't inject cross-assembly typed parameters.
        /// </summary>
        private static bool ServerPrefix(object __instance, int Type)
        {
            if (Type < ItemRegistry.VanillaItemCount) return true;

            var definition = ItemRegistry.GetDefinition(Type);
            if (definition == null) return true; // Unknown type, let vanilla handle

            try
            {
                var itemType = __instance.GetType();
                _log?.Info($"[SetDefaultsPatch] ServerPrefix: Type={Type}, itemType={itemType.FullName}");

                // Skip ResetStats — it may access OOB arrays on TerrariaServer.
                // Just set type and the properties we need directly.
                var typeField = itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                typeField?.SetValue(__instance, Type);

                // Apply properties via reflection
                ApplyDefinitionReflection(__instance, itemType, definition);

                // Set material
                var materialField = itemType.GetField("material", BindingFlags.Public | BindingFlags.Instance);
                materialField?.SetValue(__instance, definition.Material);

                // Ensure stack >= 1
                var stackField = itemType.GetField("stack", BindingFlags.Public | BindingFlags.Instance);
                if (stackField != null)
                {
                    int stack = (int)stackField.GetValue(__instance);
                    if (stack <= 0) stackField.SetValue(__instance, 1);
                }

                _log?.Info($"[SetDefaultsPatch] ServerPrefix applied for type {Type}: '{definition.DisplayName}'");
                return false; // Skip vanilla
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"[SetDefaultsPatch] ServerPrefix error for type {Type}: {inner.GetType().Name}: {inner.Message} | Stack: {inner.StackTrace ?? "null"} | Outer: {ex.StackTrace ?? "null"}");
                return true;
            }
        }

        private static void ApplyDefinitionReflection(object item, Type itemType, ItemDefinition def)
        {
            void SetInt(string name, int val) { itemType.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(item, val); }
            void SetFloat(string name, float val) { itemType.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(item, val); }
            void SetBool(string name, bool val) { itemType.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(item, val); }

            // Combat
            SetInt("damage", def.Damage);
            SetFloat("knockBack", def.KnockBack);
            SetInt("useTime", def.UseTime);
            SetInt("useAnimation", def.UseAnimation);
            SetInt("useStyle", def.UseStyle);
            SetInt("crit", def.Crit);
            SetInt("mana", def.Mana);
            SetBool("autoReuse", def.AutoReuse);

            // Damage types
            SetBool("melee", def.Melee);
            SetBool("ranged", def.Ranged);
            SetBool("magic", def.Magic);
            SetBool("summon", def.Summon);

            // Projectile
            SetInt("shoot", def.Shoot);
            SetFloat("shootSpeed", def.ShootSpeed);

            // Defense / Equipment
            SetInt("defense", def.Defense);
            SetBool("accessory", def.Accessory);
            SetBool("vanity", def.Vanity);
            if (def.HeadSlot >= 0) SetInt("headSlot", def.HeadSlot);
            if (def.BodySlot >= 0) SetInt("bodySlot", def.BodySlot);
            if (def.LegSlot >= 0) SetInt("legSlot", def.LegSlot);

            // Consumable / Potion
            SetInt("maxStack", def.MaxStack);
            SetBool("consumable", def.Consumable);
            SetBool("potion", def.Potion);
            SetInt("healLife", def.HealLife);
            SetInt("healMana", def.HealMana);
            SetInt("buffType", def.BuffType);
            SetInt("buffTime", def.BuffTime);

            // Ammo
            SetInt("ammo", def.Ammo);
            SetInt("useAmmo", def.UseAmmo);

            // Placement
            SetInt("createTile", def.CreateTile);
            SetInt("createWall", def.CreateWall);
            SetInt("placeStyle", def.PlaceStyle);

            // Visual
            SetFloat("scale", def.Scale);
            SetInt("holdStyle", def.HoldStyle);
            SetBool("noUseGraphic", def.NoUseGraphic);
            SetBool("noMelee", def.NoMelee);
            SetBool("channel", def.Channel);

            // Economy
            SetInt("rare", def.Rarity);
            SetInt("value", def.Value);

            // Skip name/tooltip on server — Lang caches are sized to ItemID.Count in
            // TerrariaServer.exe (not extended). Server doesn't need display strings.
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
            _log?.Debug($"[SetDefaultsPatch] Applied def for type {item.type}: name='{def.DisplayName}', tooltip={def.Tooltip?.Length ?? 0} lines");

            // Ensure stack is at least 1 for non-air items
            if (item.stack <= 0) item.stack = 1;
        }

        private static bool[] _materialSet;

        /// <summary>Clear cached material set ref so it's re-fetched after TypeExtension resizes it.</summary>
        public static void InvalidateMaterialCache() { _materialSet = null; }

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
