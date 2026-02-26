using System;
using System.Collections.Generic;
using StorageHub.Storage;
using TerrariaModder.Core;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.DedicatedBlocks
{
    /// <summary>
    /// Registers Storage Hub dedicated blocks using Core custom tile IDs.
    /// </summary>
    internal static class DedicatedBlocksManager
    {
        public const string TileComponentId = "storage-hub:storage-component";
        public const string TileConnectorId = "storage-hub:storage-connector";
        public const string TileHeartId = "storage-hub:storage-heart";
        public const string TileUnitId = "storage-hub:storage-unit";
        public const string TileAccessId = "storage-hub:storage-access";
        public const string TileCraftingAccessId = "storage-hub:storage-crafting-access";
        public const string TileDiskUpgraderId = "storage-hub:disk-upgrader";

        public const string ItemComponentId = "storage-hub:storage-component-item";
        public const string ItemConnectorId = "storage-hub:storage-connector-item";
        public const string ItemHeartId = "storage-hub:storage-heart-item";
        public const string ItemUnitId = "storage-hub:storage-unit-item";
        public const string ItemAccessId = "storage-hub:storage-access-item";
        public const string ItemCraftingAccessId = "storage-hub:storage-crafting-access-item";
        public const string ItemDiskUpgraderId = "storage-hub:disk-upgrader-item";
        public const string ItemDiskBasicId = "storage-hub:storage-disk-basic-item";
        public const string ItemDiskImprovedId = "storage-hub:storage-disk-improved-item";
        public const string ItemDiskAdvancedId = "storage-hub:storage-disk-advanced-item";
        public const string ItemDiskQuantumId = "storage-hub:storage-disk-quantum-item";

        // Keep implementation in source, but disabled for release builds until complete.
        private static readonly bool EnableStorageCraftingInterface = false;

        private static bool _diskItemTypesCached;
        private static int _basicDiskItemType = -1;
        private static int _improvedDiskItemType = -1;
        private static int _advancedDiskItemType = -1;
        private static int _quantumDiskItemType = -1;

        public static void Register(
            ModContext context,
            ILogger log,
            Func<int, int, bool> onStorageHeartRightClick,
            Func<int, int, bool> onStorageDriveRightClick,
            Func<int, int, bool> onStorageAccessRightClick,
            Func<int, int, bool> onStorageCraftingAccessRightClick,
            Func<int, int, bool> onDiskUpgraderRightClick)
        {
            if (context == null) return;

            context.RegisterTile("storage-component", new TileDefinition
            {
                DisplayName = "Storage Component",
                Texture = "assets/tiles/storage-component.png",
                Width = 2,
                Height = 2,
                OriginX = 1,
                OriginY = 1,
                CoordinateHeights = new[] { 16, 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = false,
                Solid = false,
                SolidTop = true,
                Table = true,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 153,
                MapColorG = 107,
                MapColorB = 61,
                DropItemId = ItemComponentId
            });

            context.RegisterTile("storage-connector", new TileDefinition
            {
                DisplayName = "Storage Connector",
                Texture = "assets/tiles/storage-connector.png",
                Width = 1,
                Height = 1,
                OriginX = 0,
                OriginY = 0,
                CoordinateHeights = new[] { 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = true,
                Solid = false,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 153,
                MapColorG = 107,
                MapColorB = 61,
                DropItemId = ItemConnectorId
            });

            context.RegisterTile("storage-heart", new TileDefinition
            {
                DisplayName = "Storage Core",
                Texture = "assets/tiles/storage-heart.png",
                Width = 2,
                Height = 2,
                OriginX = 1,
                OriginY = 1,
                CoordinateHeights = new[] { 16, 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = false,
                Solid = false,
                SolidTop = true,
                Table = true,
                Lighted = true,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 88,
                MapColorG = 190,
                MapColorB = 216,
                DropItemId = ItemHeartId,
                OnRightClick = (x, y, player) =>
                {
                    try
                    {
                        return onStorageHeartRightClick?.Invoke(x, y) ?? false;
                    }
                    catch (Exception ex)
                    {
                        log?.Error($"Storage heart right-click error: {ex.Message}");
                        return false;
                    }
                }
            });

            context.RegisterTile("storage-unit", new TileDefinition
            {
                DisplayName = "Storage Drive",
                Texture = "assets/tiles/storage-unit.png",
                Width = 2,
                Height = 2,
                OriginX = 1,
                OriginY = 1,
                CoordinateHeights = new[] { 16, 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = false,
                Solid = false,
                SolidTop = true,
                Table = true,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 120,
                MapColorG = 120,
                MapColorB = 140,
                IsContainer = true,
                RegisterAsBasicChest = false,
                ContainerCapacity = 8,
                ContainerInteractable = true,
                ContainerName = "Storage Drive",
                DropItemId = ItemUnitId,
                OnRightClick = (x, y, player) =>
                {
                    try
                    {
                        return onStorageDriveRightClick?.Invoke(x, y) ?? false;
                    }
                    catch (Exception ex)
                    {
                        log?.Error($"Storage drive right-click error: {ex.Message}");
                        return false;
                    }
                }
            });

            context.RegisterTile("storage-access", new TileDefinition
            {
                DisplayName = "Storage Access",
                Texture = "assets/tiles/storage-access.png",
                Width = 2,
                Height = 2,
                OriginX = 1,
                OriginY = 1,
                CoordinateHeights = new[] { 16, 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = false,
                Solid = false,
                SolidTop = true,
                Table = true,
                Lighted = true,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 88,
                MapColorG = 190,
                MapColorB = 216,
                DropItemId = ItemAccessId,
                OnRightClick = (x, y, player) =>
                {
                    try
                    {
                        return onStorageAccessRightClick?.Invoke(x, y) ?? false;
                    }
                    catch (Exception ex)
                    {
                        log?.Error($"Storage access right-click error: {ex.Message}");
                        return false;
                    }
                }
            });

            if (EnableStorageCraftingInterface)
            {
                context.RegisterTile("storage-crafting-access", new TileDefinition
                {
                    DisplayName = "Storage Crafting Interface",
                    Texture = "assets/tiles/storage-crafting-access.png",
                    Width = 2,
                    Height = 2,
                    OriginX = 1,
                    OriginY = 1,
                    CoordinateHeights = new[] { 16, 16 },
                    CoordinateWidth = 16,
                    CoordinatePadding = 2,
                    FrameImportant = true,
                    NoAttach = false,
                    Solid = false,
                    SolidTop = true,
                    Table = true,
                    Lighted = true,
                    LavaDeath = true,
                    HitSoundStyle = 1,
                    MapColorR = 127,
                    MapColorG = 194,
                    MapColorB = 97,
                    DropItemId = ItemCraftingAccessId,
                    OnRightClick = (x, y, player) =>
                    {
                        try
                        {
                            return onStorageCraftingAccessRightClick?.Invoke(x, y) ?? false;
                        }
                        catch (Exception ex)
                        {
                            log?.Error($"Storage crafting access right-click error: {ex.Message}");
                            return false;
                        }
                    }
                });
            }

            context.RegisterTile("disk-upgrader", new TileDefinition
            {
                DisplayName = "Disk Upgrader",
                Texture = "assets/tiles/disk-upgrader.png",
                Width = 2,
                Height = 2,
                OriginX = 1,
                OriginY = 1,
                CoordinateHeights = new[] { 16, 16 },
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                FrameImportant = true,
                NoAttach = false,
                Solid = false,
                SolidTop = true,
                Table = true,
                Lighted = true,
                LavaDeath = true,
                HitSoundStyle = 1,
                MapColorR = 200,
                MapColorG = 170,
                MapColorB = 90,
                DropItemId = ItemDiskUpgraderId,
                OnRightClick = (x, y, player) =>
                {
                    try
                    {
                        return onDiskUpgraderRightClick?.Invoke(x, y) ?? false;
                    }
                    catch (Exception ex)
                    {
                        log?.Error($"Disk upgrader right-click error: {ex.Message}");
                        return false;
                    }
                }
            });

            context.RegisterItem("storage-component-item", new ItemDefinition
            {
                DisplayName = "Storage Component",
                Tooltip = new[] { "Basic structural piece for Storage Hub networks" },
                Texture = "assets/items/storage-component.png",
                CreateTileId = TileComponentId,
                PlaceStyle = 0,
                Width = 24,
                Height = 24,
                MaxStack = 999,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 1,
                Value = 1000
            });

            context.RegisterItem("storage-connector-item", new ItemDefinition
            {
                DisplayName = "Storage Connector",
                Tooltip = new[] { "Connects Storage Hub components across distance" },
                Texture = "assets/items/storage-connector.png",
                CreateTileId = TileConnectorId,
                PlaceStyle = 0,
                Width = 20,
                Height = 20,
                MaxStack = 999,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 1,
                Value = 500
            });

            context.RegisterItem("storage-heart-item", new ItemDefinition
            {
                DisplayName = "Storage Core",
                Tooltip = new[] { "Core of your storage network", "Right click the placed block to open Storage Hub" },
                Texture = "assets/items/storage-heart.png",
                CreateTileId = TileHeartId,
                PlaceStyle = 0,
                Width = 24,
                Height = 24,
                MaxStack = 99,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 2,
                Value = 50000
            });

            context.RegisterItem("storage-unit-item", new ItemDefinition
            {
                DisplayName = "Storage Drive",
                Tooltip = new[] { "Holds up to 8 storage disks", "Disk contents move with the disk item" },
                Texture = "assets/items/storage-unit.png",
                CreateTileId = TileUnitId,
                PlaceStyle = 0,
                Width = 24,
                Height = 24,
                MaxStack = 999,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 1,
                Value = 2500
            });

            context.RegisterItem("storage-disk-basic-item", new ItemDefinition
            {
                DisplayName = "Basic Storage Disk",
                Tooltip = new[] { "Stores up to 80 item stacks", "Insert into a Storage Drive" },
                Texture = "assets/items/storage-disk.png",
                Width = 24,
                Height = 24,
                MaxStack = 1,
                Consumable = false,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = false,
                Rarity = 1,
                Value = 2000
            });

            context.RegisterItem("storage-disk-improved-item", new ItemDefinition
            {
                DisplayName = "Improved Storage Disk",
                Tooltip = new[] { "Stores up to 160 item stacks", "Insert into a Storage Drive" },
                Texture = "assets/items/storage-disk.png",
                Width = 24,
                Height = 24,
                MaxStack = 1,
                Consumable = false,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = false,
                Rarity = 2,
                Value = 5000
            });

            context.RegisterItem("storage-disk-advanced-item", new ItemDefinition
            {
                DisplayName = "Advanced Storage Disk",
                Tooltip = new[] { "Stores up to 320 item stacks", "Insert into a Storage Drive" },
                Texture = "assets/items/storage-disk.png",
                Width = 24,
                Height = 24,
                MaxStack = 1,
                Consumable = false,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = false,
                Rarity = 3,
                Value = 12000
            });

            context.RegisterItem("storage-disk-quantum-item", new ItemDefinition
            {
                DisplayName = "Quantum Storage Disk",
                Tooltip = new[] { "Stores up to 640 item stacks", "Insert into a Storage Drive" },
                Texture = "assets/items/storage-disk.png",
                Width = 24,
                Height = 24,
                MaxStack = 1,
                Consumable = false,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = false,
                Rarity = 4,
                Value = 30000
            });

            context.RegisterItem("disk-upgrader-item", new ItemDefinition
            {
                DisplayName = "Disk Upgrader",
                Tooltip = new[] { "Open the upgrader UI to upgrade storage disks while keeping contents" },
                Texture = "assets/items/disk-upgrader.png",
                CreateTileId = TileDiskUpgraderId,
                PlaceStyle = 0,
                Width = 24,
                Height = 24,
                MaxStack = 99,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 2,
                Value = 15000
            });

            context.RegisterItem("storage-access-item", new ItemDefinition
            {
                DisplayName = "Storage Access",
                Tooltip = new[] { "Extra access point for your Storage Hub network" },
                Texture = "assets/items/storage-access.png",
                CreateTileId = TileAccessId,
                PlaceStyle = 0,
                Width = 24,
                Height = 24,
                MaxStack = 99,
                Consumable = true,
                UseStyle = 1,
                UseTurn = true,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 2,
                Value = 15000
            });

            if (EnableStorageCraftingInterface)
            {
                context.RegisterItem("storage-crafting-access-item", new ItemDefinition
                {
                    DisplayName = "Storage Crafting Interface",
                    Tooltip = new[] { "Access storage and crafting from one terminal" },
                    Texture = "assets/items/storage-crafting-access.png",
                    CreateTileId = TileCraftingAccessId,
                    PlaceStyle = 0,
                    Width = 24,
                    Height = 24,
                    MaxStack = 99,
                    Consumable = true,
                    UseStyle = 1,
                    UseTurn = true,
                    UseTime = 10,
                    UseAnimation = 15,
                    AutoReuse = true,
                    Rarity = 3,
                    Value = 30000
                });
            }

            // Starter placeholder recipes.
            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemHeartId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["Diamond"] = 1,
                    ["IronBar"] = 4,
                    ["Wood"] = 10
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemHeartId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["Diamond"] = 1,
                    ["LeadBar"] = 4,
                    ["Wood"] = 10
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemDiskBasicId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["Diamond"] = 1,
                    ["Ruby"] = 1,
                    ["GoldBar"] = 1
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemUnitId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["GoldBar"] = 5,
                    ["IronBar"] = 5
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemUnitId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["GoldBar"] = 5,
                    ["LeadBar"] = 5
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemDiskUpgraderId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["GoldBar"] = 5,
                    ["IronBar"] = 10
                },
                Station = "Anvils"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemDiskUpgraderId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    ["GoldBar"] = 5,
                    ["LeadBar"] = 10
                },
                Station = "Anvils"
            });
        }

        public static int ResolveStorageComponentTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileComponentId);
        }

        public static int ResolveStorageConnectorTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileConnectorId);
        }

        public static int ResolveStorageHeartTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileHeartId);
        }

        public static int ResolveStorageAccessTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileAccessId);
        }

        public static int ResolveStorageCraftingAccessTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileCraftingAccessId);
        }

        public static int ResolveDiskUpgraderTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileDiskUpgraderId);
        }

        public static int ResolveStorageUnitTileType()
        {
            return AssetSystem.GetTileRuntimeType(TileUnitId);
        }

        public static bool TryGetDiskTierForItemType(int itemType, out int tier)
        {
            tier = StorageDiskCatalog.None;
            if (itemType <= 0)
                return false;

            EnsureDiskItemTypesResolved();

            if (itemType == _basicDiskItemType)
            {
                tier = StorageDiskCatalog.Basic;
                return true;
            }
            if (itemType == _improvedDiskItemType)
            {
                tier = StorageDiskCatalog.Improved;
                return true;
            }
            if (itemType == _advancedDiskItemType)
            {
                tier = StorageDiskCatalog.Advanced;
                return true;
            }
            if (itemType == _quantumDiskItemType)
            {
                tier = StorageDiskCatalog.Quantum;
                return true;
            }

            return false;
        }

        public static int ResolveDiskItemType(int diskTier)
        {
            EnsureDiskItemTypesResolved();
            return diskTier switch
            {
                StorageDiskCatalog.Basic => _basicDiskItemType,
                StorageDiskCatalog.Improved => _improvedDiskItemType,
                StorageDiskCatalog.Advanced => _advancedDiskItemType,
                StorageDiskCatalog.Quantum => _quantumDiskItemType,
                _ => -1
            };
        }

        private static void EnsureDiskItemTypesResolved()
        {
            if (_diskItemTypesCached)
                return;

            _basicDiskItemType = ItemRegistry.ResolveItemType(ItemDiskBasicId);
            _improvedDiskItemType = ItemRegistry.ResolveItemType(ItemDiskImprovedId);
            _advancedDiskItemType = ItemRegistry.ResolveItemType(ItemDiskAdvancedId);
            _quantumDiskItemType = ItemRegistry.ResolveItemType(ItemDiskQuantumId);
            _diskItemTypesCached = true;
        }
    }
}
