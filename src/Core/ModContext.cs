using System;
using System.Collections.Generic;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core
{
    /// <summary>
    /// Context provided to each mod during initialization.
    /// Contains per-mod services like logging and configuration.
    /// </summary>
    public class ModContext
    {
        /// <summary>
        /// Per-mod logger instance.
        /// </summary>
        public ILogger Logger { get; }

        /// <summary>
        /// Mod configuration (null if mod has no config_schema).
        /// </summary>
        public IModConfig Config { get; }

        /// <summary>
        /// Path to the mod's folder.
        /// </summary>
        public string ModFolder { get; }

        /// <summary>
        /// The parsed manifest for this mod.
        /// </summary>
        public ModManifest Manifest { get; }

        private readonly List<Keybind> _registeredKeybinds = new List<Keybind>();

        public ModContext(ILogger logger, string modFolder, ModManifest manifest, IModConfig config = null)
        {
            Logger = logger;
            ModFolder = modFolder;
            Manifest = manifest;
            Config = config;

            // Register mod folder for texture loading
            AssetSystem.RegisterModFolder(manifest.Id, modFolder);
        }

        /// <summary>
        /// Register a keybind for this mod.
        /// </summary>
        /// <param name="keybindId">Unique ID within this mod</param>
        /// <param name="label">Display label</param>
        /// <param name="description">Description for tooltip</param>
        /// <param name="defaultKey">Default key combo (e.g., "F5", "Ctrl+G")</param>
        /// <param name="callback">Action to execute when pressed</param>
        public Keybind RegisterKeybind(string keybindId, string label, string description, string defaultKey, Action callback)
        {
            var keybind = KeybindManager.Register(Manifest.Id, keybindId, label, description, defaultKey, callback);
            _registeredKeybinds.Add(keybind);
            return keybind;
        }

        /// <summary>
        /// Register a keybind with a KeyCombo.
        /// </summary>
        public Keybind RegisterKeybind(string keybindId, string label, string description, KeyCombo defaultKey, Action callback)
        {
            var keybind = KeybindManager.Register(Manifest.Id, keybindId, label, description, defaultKey, callback);
            _registeredKeybinds.Add(keybind);
            return keybind;
        }

        /// <summary>
        /// Get a keybind registered by this mod.
        /// </summary>
        public Keybind GetKeybind(string keybindId)
        {
            return KeybindManager.GetKeybind($"{Manifest.Id}.{keybindId}");
        }

        /// <summary>
        /// Get all keybinds registered by this mod.
        /// </summary>
        public IEnumerable<Keybind> GetKeybinds()
        {
            return KeybindManager.GetKeybindsForMod(Manifest.Id);
        }

        #region Custom Items

        /// <summary>
        /// Register a custom modded item.
        /// </summary>
        /// <param name="itemName">Unique item name within this mod (e.g., "fire-sword").</param>
        /// <param name="definition">The item definition with all properties.</param>
        /// <returns>True if registered successfully.</returns>
        public bool RegisterItem(string itemName, ItemDefinition definition)
        {
            return AssetSystem.RegisterItem(Manifest.Id, itemName, definition);
        }

        /// <summary>
        /// Get all custom item names registered by this mod.
        /// </summary>
        public IEnumerable<string> GetItems()
        {
            return ItemRegistry.GetItemsForMod(Manifest.Id);
        }

        #endregion

        #region Custom Tiles

        /// <summary>
        /// Register a custom modded tile type.
        /// Requires core config flag: experimental_custom_tiles=true.
        /// </summary>
        /// <param name="tileName">Unique tile name within this mod (e.g. "storage-heart").</param>
        /// <param name="definition">Tile definition and behavior metadata.</param>
        /// <returns>True if registered successfully.</returns>
        public bool RegisterTile(string tileName, TileDefinition definition)
        {
            return AssetSystem.RegisterTile(Manifest.Id, tileName, definition);
        }

        /// <summary>
        /// Get all custom tile names registered by this mod.
        /// </summary>
        public IEnumerable<string> GetTiles()
        {
            return TileRegistry.GetTilesForMod(Manifest.Id);
        }

        #endregion

        #region Custom Recipes

        /// <summary>
        /// Register a crafting recipe.
        /// </summary>
        public void RegisterRecipe(RecipeDefinition recipe)
        {
            AssetSystem.RegisterRecipe(recipe);
        }

        #endregion

        #region NPC Shops

        /// <summary>
        /// Add a modded item to an NPC's shop.
        /// </summary>
        public void AddShopItem(ShopDefinition shopItem)
        {
            AssetSystem.RegisterShopItem(shopItem);
        }

        #endregion

        #region Enemy Drops

        /// <summary>
        /// Register a modded item drop from an NPC.
        /// </summary>
        public void RegisterDrop(DropDefinition drop)
        {
            AssetSystem.RegisterDrop(drop);
        }

        #endregion

        #region Debug Commands

        /// <summary>
        /// Register a debug command for this mod.
        /// The command will be namespaced as "modid.name".
        /// </summary>
        /// <param name="name">Command name (lowercase, no spaces, e.g., "status").</param>
        /// <param name="description">Human-readable description of what the command does.</param>
        /// <param name="callback">Callback invoked with parsed arguments.</param>
        /// <returns>True if registered successfully, false if name is invalid or already taken.</returns>
        public bool RegisterCommand(string name, string description, Action<string[]> callback)
        {
            if (string.IsNullOrEmpty(name))
            {
                Logger.Warn("Cannot register command with empty name");
                return false;
            }

            // Validate: lowercase, no spaces
            if (name != name.ToLowerInvariant())
            {
                Logger.Warn($"Command name '{name}' must be lowercase");
                return false;
            }

            if (name.Contains(" "))
            {
                Logger.Warn($"Command name '{name}' cannot contain spaces");
                return false;
            }

            return CommandRegistry.RegisterForMod(Manifest.Id, name, description, callback);
        }

        /// <summary>
        /// Get all debug commands registered by this mod.
        /// </summary>
        public IReadOnlyList<CommandInfo> GetCommands()
        {
            return CommandRegistry.GetCommandsForMod(Manifest.Id);
        }

        #endregion

        // ChestLoot removed in v3 — world chest items are handled via save interception
    }
}
