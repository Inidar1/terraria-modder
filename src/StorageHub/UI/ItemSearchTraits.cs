using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StorageHub.Config;
using StorageHub.Storage;
using TerrariaModder.Core.Logging;

namespace StorageHub.UI
{
    /// <summary>
    /// Compact item trait set used for MagicStorage-style search and sorting.
    /// </summary>
    internal readonly struct ItemSearchTraits
    {
        public static ItemSearchTraits Default => new ItemSearchTraits(
            primaryCategory: CategoryFilter.Misc,
            isWeapon: false,
            isMelee: false,
            isRanged: false,
            isMagic: false,
            isSummon: false,
            isThrown: false,
            isAmmo: false,
            isTool: false,
            isArmor: false,
            isAccessory: false,
            isConsumable: false,
            isPotion: false,
            isPlaceable: false,
            isMaterial: false,
            isVanity: false,
            isEquipment: false,
            isFishing: false,
            isStackable: false
        );

        public readonly CategoryFilter PrimaryCategory;
        public readonly bool IsWeapon;
        public readonly bool IsMelee;
        public readonly bool IsRanged;
        public readonly bool IsMagic;
        public readonly bool IsSummon;
        public readonly bool IsThrown;
        public readonly bool IsAmmo;
        public readonly bool IsTool;
        public readonly bool IsArmor;
        public readonly bool IsAccessory;
        public readonly bool IsConsumable;
        public readonly bool IsPotion;
        public readonly bool IsPlaceable;
        public readonly bool IsMaterial;
        public readonly bool IsVanity;
        public readonly bool IsEquipment;
        public readonly bool IsFishing;
        public readonly bool IsStackable;

        public ItemSearchTraits(
            CategoryFilter primaryCategory,
            bool isWeapon,
            bool isMelee,
            bool isRanged,
            bool isMagic,
            bool isSummon,
            bool isThrown,
            bool isAmmo,
            bool isTool,
            bool isArmor,
            bool isAccessory,
            bool isConsumable,
            bool isPotion,
            bool isPlaceable,
            bool isMaterial,
            bool isVanity,
            bool isEquipment,
            bool isFishing,
            bool isStackable)
        {
            PrimaryCategory = primaryCategory;
            IsWeapon = isWeapon;
            IsMelee = isMelee;
            IsRanged = isRanged;
            IsMagic = isMagic;
            IsSummon = isSummon;
            IsThrown = isThrown;
            IsAmmo = isAmmo;
            IsTool = isTool;
            IsArmor = isArmor;
            IsAccessory = isAccessory;
            IsConsumable = isConsumable;
            IsPotion = isPotion;
            IsPlaceable = isPlaceable;
            IsMaterial = isMaterial;
            IsVanity = isVanity;
            IsEquipment = isEquipment;
            IsFishing = isFishing;
            IsStackable = isStackable;
        }
    }

    internal static class ItemSearchTraitsBuilder
    {
        private static readonly object _cacheLock = new object();
        private static Dictionary<int, ItemSearchTraits> _cachedTraits;
        private static bool _cacheInitialized;

        /// <summary>
        /// Build traits from a live item snapshot.
        /// </summary>
        public static ItemSearchTraits FromSnapshot(ItemSnapshot item)
        {
            bool isTool = item.IsPickaxe || item.IsAxe || item.IsHammer;
            bool isWeapon = (item.Damage ?? 0) > 0 && !isTool && !item.IsAmmo;
            bool isMelee = item.IsMelee;
            bool isRanged = item.IsRanged;
            bool isMagic = item.IsMagic;
            bool isSummon = item.IsSummon;
            bool isThrown = item.IsThrown;
            if (!isMelee && !isRanged && !isMagic && !isSummon && !isThrown && isWeapon)
                isMelee = true;

            return BuildTraits(
                damage: item.Damage ?? 0,
                pick: item.PickPower,
                axe: item.AxePower,
                hammer: item.HammerPower,
                headSlot: item.IsArmor ? 0 : -1,
                bodySlot: -1,
                legSlot: -1,
                accessory: item.IsAccessory,
                consumable: item.IsConsumable,
                createTile: item.IsPlaceable ? 0 : -1,
                createWall: -1,
                material: item.IsMaterial,
                vanity: item.IsVanity,
                ammo: item.AmmoType,
                notAmmo: false,
                melee: isMelee,
                ranged: isRanged,
                magic: isMagic,
                summon: isSummon,
                thrown: isThrown,
                sentry: false,
                shoot: isThrown ? 1 : 0,
                healLife: item.HealLife,
                healMana: item.HealMana,
                potion: item.IsPotion,
                dye: item.IsVanity ? 1 : 0,
                hairDye: item.IsVanity ? 0 : -1,
                mountType: item.IsEquipment ? 0 : -1,
                buffType: item.IsEquipment ? 1 : 0,
                fishingPole: item.FishingPolePower,
                bait: item.FishingBaitPower,
                maxStack: item.MaxStack
            );
        }

        /// <summary>
        /// Build/cached traits for all vanilla items using ContentSamples.
        /// </summary>
        public static Dictionary<int, ItemSearchTraits> GetAllFromContentSamples(ILogger log)
        {
            lock (_cacheLock)
            {
                if (_cacheInitialized)
                    return _cachedTraits ?? new Dictionary<int, ItemSearchTraits>();

                _cacheInitialized = true;
                _cachedTraits = BuildFromContentSamples(log);
                return _cachedTraits ?? new Dictionary<int, ItemSearchTraits>();
            }
        }

        public static int GetMagicSortRank(ItemSearchTraits t)
        {
            if (t.IsMelee) return 0;
            if (t.IsRanged) return 1;
            if (t.IsMagic) return 2;
            if (t.IsSummon) return 3;
            if (t.IsThrown) return 4;
            if (t.IsWeapon) return 5;
            if (t.IsAmmo) return 6;
            if (t.IsTool) return 7;
            if (t.IsArmor) return 8;
            if (t.IsAccessory) return 9;
            if (t.IsVanity) return 10;
            if (t.IsPotion) return 11;
            if (t.IsConsumable) return 12;
            if (t.IsPlaceable) return 13;
            if (t.IsMaterial) return 14;
            if (t.IsFishing) return 15;
            return 16;
        }

        private static Dictionary<int, ItemSearchTraits> BuildFromContentSamples(ILogger log)
        {
            var result = new Dictionary<int, ItemSearchTraits>();

            try
            {
                var contentSamplesType = Type.GetType("Terraria.ID.ContentSamples, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.ID.ContentSamples");
                if (contentSamplesType == null)
                    return result;

                var itemsByTypeField = contentSamplesType.GetField("ItemsByType", BindingFlags.Public | BindingFlags.Static);
                var dict = itemsByTypeField?.GetValue(null) as IDictionary;
                if (dict == null)
                    return result;

                FieldInfo damageField = null, pickField = null, axeField = null, hammerField = null;
                FieldInfo headSlotField = null, bodySlotField = null, legSlotField = null;
                FieldInfo accessoryField = null, consumableField = null;
                FieldInfo createTileField = null, createWallField = null, materialField = null;
                FieldInfo vanityField = null, ammoField = null, notAmmoField = null;
                FieldInfo meleeField = null, rangedField = null, magicField = null, summonField = null, thrownField = null;
                FieldInfo sentryField = null, shootField = null;
                FieldInfo healLifeField = null, healManaField = null, potionField = null;
                FieldInfo dyeField = null, hairDyeField = null, mountTypeField = null, buffTypeField = null;
                FieldInfo fishingPoleField = null, baitField = null, maxStackField = null;

                foreach (DictionaryEntry entry in dict)
                {
                    if (!(entry.Key is int itemId))
                        continue;

                    var item = entry.Value;
                    if (item == null)
                        continue;

                    if (damageField == null)
                    {
                        var t = item.GetType();
                        damageField = t.GetField("damage");
                        pickField = t.GetField("pick");
                        axeField = t.GetField("axe");
                        hammerField = t.GetField("hammer");
                        headSlotField = t.GetField("headSlot");
                        bodySlotField = t.GetField("bodySlot");
                        legSlotField = t.GetField("legSlot");
                        accessoryField = t.GetField("accessory");
                        consumableField = t.GetField("consumable");
                        createTileField = t.GetField("createTile");
                        createWallField = t.GetField("createWall");
                        materialField = t.GetField("material");
                        vanityField = t.GetField("vanity");
                        ammoField = t.GetField("ammo");
                        notAmmoField = t.GetField("notAmmo");
                        meleeField = t.GetField("melee");
                        rangedField = t.GetField("ranged");
                        magicField = t.GetField("magic");
                        summonField = t.GetField("summon");
                        thrownField = t.GetField("thrown");
                        sentryField = t.GetField("sentry");
                        shootField = t.GetField("shoot");
                        healLifeField = t.GetField("healLife");
                        healManaField = t.GetField("healMana");
                        potionField = t.GetField("potion");
                        dyeField = t.GetField("dye");
                        hairDyeField = t.GetField("hairDye");
                        mountTypeField = t.GetField("mountType");
                        buffTypeField = t.GetField("buffType");
                        fishingPoleField = t.GetField("fishingPole");
                        baitField = t.GetField("bait");
                        maxStackField = t.GetField("maxStack");
                    }

                    result[itemId] = BuildTraits(
                        damage: GetInt(damageField, item, 0),
                        pick: GetInt(pickField, item, 0),
                        axe: GetInt(axeField, item, 0),
                        hammer: GetInt(hammerField, item, 0),
                        headSlot: GetInt(headSlotField, item, -1),
                        bodySlot: GetInt(bodySlotField, item, -1),
                        legSlot: GetInt(legSlotField, item, -1),
                        accessory: GetBool(accessoryField, item),
                        consumable: GetBool(consumableField, item),
                        createTile: GetInt(createTileField, item, -1),
                        createWall: GetInt(createWallField, item, -1),
                        material: GetBool(materialField, item),
                        vanity: GetBool(vanityField, item),
                        ammo: GetInt(ammoField, item, 0),
                        notAmmo: GetBool(notAmmoField, item),
                        melee: GetBool(meleeField, item),
                        ranged: GetBool(rangedField, item),
                        magic: GetBool(magicField, item),
                        summon: GetBool(summonField, item),
                        thrown: GetBool(thrownField, item),
                        sentry: GetBool(sentryField, item),
                        shoot: GetInt(shootField, item, 0),
                        healLife: GetInt(healLifeField, item, 0),
                        healMana: GetInt(healManaField, item, 0),
                        potion: GetBool(potionField, item),
                        dye: GetInt(dyeField, item, 0),
                        hairDye: GetInt(hairDyeField, item, -1),
                        mountType: GetInt(mountTypeField, item, -1),
                        buffType: GetInt(buffTypeField, item, 0),
                        fishingPole: GetInt(fishingPoleField, item, 0),
                        bait: GetInt(baitField, item, 0),
                        maxStack: GetInt(maxStackField, item, 1)
                    );
                }

                log?.Debug($"[SearchTraits] Cached traits for {result.Count} item types");
            }
            catch (Exception ex)
            {
                log?.Error($"[SearchTraits] Failed to build trait cache: {ex.Message}");
            }

            return result;
        }

        private static ItemSearchTraits BuildTraits(
            int damage,
            int pick,
            int axe,
            int hammer,
            int headSlot,
            int bodySlot,
            int legSlot,
            bool accessory,
            bool consumable,
            int createTile,
            int createWall,
            bool material,
            bool vanity,
            int ammo,
            bool notAmmo,
            bool melee,
            bool ranged,
            bool magic,
            bool summon,
            bool thrown,
            bool sentry,
            int shoot,
            int healLife,
            int healMana,
            bool potion,
            int dye,
            int hairDye,
            int mountType,
            int buffType,
            int fishingPole,
            int bait,
            int maxStack)
        {
            bool isTool = pick > 0 || axe > 0 || hammer > 0;
            bool isWeapon = damage > 0 && !isTool && ammo == 0;
            bool isMelee = melee && isWeapon;
            bool isRanged = ranged && isWeapon;
            bool isMagic = magic && isWeapon;
            bool isSummon = (summon || sentry) && isWeapon;
            bool isThrown = thrown && damage > 0 && (ammo == 0 || notAmmo) && shoot > 0;
            if (!isMelee && !isRanged && !isMagic && !isSummon && !isThrown && isWeapon)
                isMelee = true;

            bool isAmmo = ammo > 0 && damage > 0;
            bool isPlaceable = createTile >= 0 || createWall >= 0;
            bool isArmor = !vanity && (headSlot >= 0 || bodySlot >= 0 || legSlot >= 0);
            bool isVanity = vanity || dye > 0 || hairDye >= 0;
            bool isPotion = consumable && (healLife > 0 || healMana > 0 || buffType > 0 || potion);
            bool isFishing = fishingPole > 0 || bait > 0;
            bool isEquipment = accessory || mountType >= 0 || buffType > 0 || isFishing;
            bool isMaterial = material && !isPlaceable && !isWeapon && !isTool && !accessory && !isVanity;
            bool isStackable = maxStack > 1;

            CategoryFilter primary;
            if (isTool)
                primary = CategoryFilter.Tools;
            else if (isWeapon)
                primary = CategoryFilter.Weapons;
            else if (isArmor)
                primary = CategoryFilter.Armor;
            else if (accessory)
                primary = CategoryFilter.Accessories;
            else if (isPlaceable)
                primary = CategoryFilter.Placeable;
            else if (consumable)
                primary = CategoryFilter.Consumables;
            else if (isMaterial)
                primary = CategoryFilter.Materials;
            else
                primary = CategoryFilter.Misc;

            return new ItemSearchTraits(
                primaryCategory: primary,
                isWeapon: isWeapon,
                isMelee: isMelee,
                isRanged: isRanged,
                isMagic: isMagic,
                isSummon: isSummon,
                isThrown: isThrown,
                isAmmo: isAmmo,
                isTool: isTool,
                isArmor: isArmor,
                isAccessory: accessory,
                isConsumable: consumable,
                isPotion: isPotion,
                isPlaceable: isPlaceable,
                isMaterial: isMaterial,
                isVanity: isVanity,
                isEquipment: isEquipment,
                isFishing: isFishing,
                isStackable: isStackable
            );
        }

        private static int GetInt(FieldInfo field, object obj, int fallback)
        {
            if (field == null || obj == null)
                return fallback;

            try
            {
                var value = field.GetValue(obj);
                if (value == null)
                    return fallback;

                if (value is int i)
                    return i;

                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool GetBool(FieldInfo field, object obj)
        {
            if (field == null || obj == null)
                return false;

            try
            {
                var value = field.GetValue(obj);
                if (value == null)
                    return false;

                if (value is bool b)
                    return b;

                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }
    }
}
