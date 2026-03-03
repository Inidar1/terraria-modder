using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Harmony patches for NPC drops and NPC shops.
    /// - Drops: Postfix on NPC.NPCLoot() to spawn custom item drops
    /// - Shops: Postfix on Chest.SetupShop(int) to add custom items to NPC shops
    /// </summary>
    internal static class ContentPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // Drop registry: NPC type → list of drops
        private static readonly Dictionary<int, List<DropDefinition>> _drops
            = new Dictionary<int, List<DropDefinition>>();

        // Shop registry: NPC type → list of shop items
        private static readonly Dictionary<int, List<ShopDefinition>> _shopItems
            = new Dictionary<int, List<ShopDefinition>>();

        // Reflection cache for Item.NewItem
        private static MethodInfo _newItemMethod;
        private static bool _newItemSearched;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.content");
        }

        /// <summary>
        /// Register a drop from a specific NPC.
        /// </summary>
        public static void RegisterDrop(DropDefinition drop)
        {
            if (drop == null) return;
            if (!_drops.TryGetValue(drop.NpcType, out var list))
            {
                list = new List<DropDefinition>();
                _drops[drop.NpcType] = list;
            }
            list.Add(drop);
        }

        /// <summary>
        /// Register a shop item for a specific NPC.
        /// </summary>
        public static void RegisterShopItem(ShopDefinition shopItem)
        {
            if (shopItem == null) return;
            if (!_shopItems.TryGetValue(shopItem.NpcType, out var list))
            {
                list = new List<ShopDefinition>();
                _shopItems[shopItem.NpcType] = list;
            }
            list.Add(shopItem);
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                if (_drops.Count > 0)
                    PatchNPCLoot();
                if (_shopItems.Count > 0)
                    PatchSetupShop();

                _applied = true;
                _log?.Info($"[ContentPatches] Applied (drops: {_drops.Count} NPCs, shops: {_shopItems.Count} NPCs)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[ContentPatches] Failed: {ex.Message}");
            }
        }

        // ── NPC Drop Patches ──

        private static void PatchNPCLoot()
        {
            try
            {
                var method = typeof(NPC).GetMethod("NPCLoot",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                if (method == null)
                {
                    // Try NPCLoot_DropItems (private, called from NPCLoot)
                    method = typeof(NPC).GetMethod("NPCLoot_DropItems",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(Player) }, null);
                }

                if (method == null)
                {
                    _log?.Warn("[ContentPatches] NPC.NPCLoot method not found");
                    return;
                }

                _harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(ContentPatches), nameof(NPCLoot_Postfix)));
                _log?.Debug("[ContentPatches] Patched NPC.NPCLoot");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ContentPatches] Failed to patch NPCLoot: {ex.Message}");
            }
        }

        private static void NPCLoot_Postfix(NPC __instance)
        {
            try
            {
                if (!_drops.TryGetValue(__instance.type, out var drops)) return;

                foreach (var drop in drops)
                {
                    // Roll chance
                    if (drop.Chance < 1.0f)
                    {
                        float roll = (float)Main.rand.NextDouble();
                        if (roll > drop.Chance) continue;
                    }

                    // Resolve item
                    int itemType = ItemRegistry.ResolveItemType(drop.ItemId);
                    if (itemType < 0) continue;

                    // Determine stack
                    int stack = drop.MinStack;
                    if (drop.MaxStack > drop.MinStack)
                        stack = Main.rand.Next(drop.MinStack, drop.MaxStack + 1);

                    // Spawn item at NPC position
                    SpawnItem(__instance, itemType, stack);
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ContentPatches] Drop error for NPC {__instance.type}: {ex.Message}");
            }
        }

        private static void SpawnItem(NPC npc, int itemType, int stack)
        {
            try
            {
                // Use Item.NewItem to spawn at NPC center
                // Item.NewItem has many overloads, try the common one:
                // int NewItem(IEntitySource source, int X, int Y, int Width, int Height, int Type, int Stack = 1, ...)
                if (!_newItemSearched)
                {
                    _newItemSearched = true;
                    // Find a suitable overload
                    foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "NewItem") continue;
                        var parms = m.GetParameters();
                        // Look for overload with source, x, y, w, h, type, stack
                        if (parms.Length >= 7 && parms[1].ParameterType == typeof(int) &&
                            parms[5].ParameterType == typeof(int))
                        {
                            _newItemMethod = m;
                            break;
                        }
                    }
                }

                if (_newItemMethod != null)
                {
                    // Get entity source for NPC loot
                    object source = GetNpcLootSource(npc);

                    int x = (int)npc.position.X;
                    int y = (int)npc.position.Y;
                    int w = npc.width;
                    int h = npc.height;

                    // Build params array with defaults for optional params
                    var parms = _newItemMethod.GetParameters();
                    var args = new object[parms.Length];
                    args[0] = source;
                    args[1] = x;
                    args[2] = y;
                    args[3] = w;
                    args[4] = h;
                    args[5] = itemType;
                    args[6] = stack;
                    // Fill remaining with defaults
                    for (int i = 7; i < parms.Length; i++)
                    {
                        if (parms[i].HasDefaultValue)
                            args[i] = parms[i].DefaultValue;
                        else if (parms[i].ParameterType.IsValueType)
                            args[i] = Activator.CreateInstance(parms[i].ParameterType);
                        else
                            args[i] = null;
                    }

                    _newItemMethod.Invoke(null, args);
                }
                else
                {
                    _log?.Debug("[ContentPatches] Item.NewItem not found, cannot spawn drop");
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ContentPatches] SpawnItem failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get an IEntitySource for NPC loot via reflection.
        /// NPC.GetItemSource_Loot() returns the appropriate source.
        /// </summary>
        private static object GetNpcLootSource(NPC npc)
        {
            try
            {
                var method = typeof(NPC).GetMethod("GetItemSource_Loot",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                    return method.Invoke(npc, null);

                // Fallback: try GetSource_Loot
                method = typeof(NPC).GetMethod("GetSource_Loot",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                    return method.Invoke(npc, null);
            }
            catch { }
            return null;
        }

        // ── NPC Shop Patches ──

        private static void PatchSetupShop()
        {
            try
            {
                var method = typeof(Chest).GetMethod("SetupShop",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);

                if (method == null)
                {
                    _log?.Warn("[ContentPatches] Chest.SetupShop not found");
                    return;
                }

                _harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(ContentPatches), nameof(SetupShop_Postfix)));
                _log?.Debug("[ContentPatches] Patched Chest.SetupShop");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ContentPatches] Failed to patch SetupShop: {ex.Message}");
            }
        }

        /// <summary>
        /// After vanilla shop setup, add our custom items to empty slots.
        /// </summary>
        private static void SetupShop_Postfix(Chest __instance, int type)
        {
            try
            {
                if (!_shopItems.TryGetValue(type, out var items)) return;

                foreach (var shopItem in items)
                {
                    int itemType = ItemRegistry.ResolveItemType(shopItem.ItemId);
                    if (itemType < 0) continue;

                    // Find empty slot
                    int emptySlot = -1;
                    for (int i = 0; i < __instance.item.Length; i++)
                    {
                        if (__instance.item[i] == null || __instance.item[i].type == 0)
                        {
                            emptySlot = i;
                            break;
                        }
                    }

                    if (emptySlot < 0)
                    {
                        _log?.Debug($"[ContentPatches] Shop full for NPC {type}");
                        break;
                    }

                    __instance.item[emptySlot] = new Item();
                    __instance.item[emptySlot].SetDefaults(itemType);

                    // Set custom price if specified
                    if (shopItem.Price > 0)
                    {
                        __instance.item[emptySlot].value = shopItem.Price;
                        __instance.item[emptySlot].shopCustomPrice = shopItem.Price;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ContentPatches] SetupShop error for NPC {type}: {ex.Message}");
            }
        }

        public static void Clear()
        {
            _drops.Clear();
            _shopItems.Clear();
            _newItemMethod = null;
            _newItemSearched = false;
        }
    }
}
