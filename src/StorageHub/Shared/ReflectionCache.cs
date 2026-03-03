using Terraria;
using TerrariaModder.Core.Logging;

namespace StorageHub.Shared
{
    /// <summary>
    /// Centralized helper for common Terraria type access.
    /// Previously used reflection for all lookups — now uses direct compile-time references.
    ///
    /// Initialize once at mod startup. All fields are static and shared across the mod.
    /// </summary>
    public static class ReflectionCache
    {
        // Initialization state
        public static bool Initialized { get; private set; }
        public static bool InitFailed { get; private set; }

        /// <summary>
        /// Initialize. Call once at mod startup.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void Initialize(ILogger log)
        {
            if (Initialized || InitFailed) return;

            Initialized = true;
            log.Info("ReflectionCache: Initialized (direct references)");
        }

        /// <summary>
        /// Get Main.player[Main.myPlayer] — the local player object.
        /// Returns null if player not available.
        /// </summary>
        public static Player GetLocalPlayer()
        {
            try
            {
                int myPlayer = Main.myPlayer;
                var players = Main.player;
                if (players == null || myPlayer < 0 || myPlayer >= players.Length)
                    return null;

                return players[myPlayer];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Create a new Item instance and call SetDefaults(type).
        /// Returns the item, or null if creation failed.
        /// </summary>
        public static Item CreateItem(int type)
        {
            try
            {
                var item = new Item();
                item.SetDefaults(type);
                return item;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read Item.maxStack from an item object. Returns 9999 on failure.
        /// </summary>
        public static int GetItemMaxStack(Item item)
        {
            if (item == null) return 9999;
            int maxStack = item.maxStack;
            return maxStack > 0 ? maxStack : 9999;
        }

        /// <summary>
        /// Read Item.Name from an item object. Returns "" on failure.
        /// </summary>
        public static string GetItemName(Item item)
        {
            if (item == null) return "";
            return item.Name ?? "";
        }

    }
}
