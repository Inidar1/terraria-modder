using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace StorageHub.Patches
{
    /// <summary>
    /// Hooks the vanilla "Quick Stack to Nearby Chests" action so Storage Hub can
    /// apply additional quick-stack behavior after Terraria handles nearby chests.
    /// </summary>
    internal static class VanillaQuickStackPatch
    {
        private readonly struct SuppressedChestEntry
        {
            public readonly int Index;
            public readonly object Chest;

            public SuppressedChestEntry(int index, object chest)
            {
                Index = index;
                Chest = chest;
            }
        }

        private struct VanillaQuickStackState
        {
            public int TileType;
            public bool PreviousTileContainerValue;
            public bool HasTileContainerState;
            public List<SuppressedChestEntry> SuppressedChests;
        }

        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static Action _onQuickStackAllChests;
        private static Func<int> _getSuppressedTileType;
        private static Func<IEnumerable<(int x, int y)>> _getSuppressedChestPositions;

        private static FieldInfo _tileContainerField;
        private static FieldInfo _mainChestArrayField;
        private static Type _mainType;
        private static Type _chestType;
        private static MethodInfo _findChestMethod;

        public static void Initialize(ILogger log)
        {
            _log = log;
            if (_applied) return;

            try
            {
                var playerType = Type.GetType("Terraria.Player, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Player");
                if (playerType == null)
                {
                    _log?.Warn("[VanillaQuickStackPatch] Terraria.Player type not found");
                    return;
                }

                MethodInfo quickStack = playerType.GetMethod("QuickStackAllChests",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                if (quickStack == null)
                {
                    foreach (var method in playerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "QuickStackAllChests" && method.GetParameters().Length == 0)
                        {
                            quickStack = method;
                            break;
                        }
                    }
                }

                if (quickStack == null)
                {
                    _log?.Warn("[VanillaQuickStackPatch] Player.QuickStackAllChests not found");
                    return;
                }

                _harmony = new Harmony("com.storagehub.vanilla.quickstack");
                _harmony.Patch(quickStack,
                    prefix: new HarmonyMethod(typeof(VanillaQuickStackPatch), nameof(QuickStackAllChests_Prefix)),
                    postfix: new HarmonyMethod(typeof(VanillaQuickStackPatch), nameof(QuickStackAllChests_Postfix)));

                _applied = true;
                _log?.Info("[VanillaQuickStackPatch] Applied Player.QuickStackAllChests prefix/postfix");
            }
            catch (Exception ex)
            {
                _log?.Error($"[VanillaQuickStackPatch] Failed to apply: {ex.Message}");
            }
        }

        public static void Unload()
        {
            try
            {
                _onQuickStackAllChests = null;
                _getSuppressedTileType = null;
                _getSuppressedChestPositions = null;

                if (_harmony != null)
                {
                    _harmony.UnpatchAll(_harmony.Id);
                    _harmony = null;
                }
            }
            catch
            {
                // Best effort unpatch.
            }
            finally
            {
                _applied = false;
                _tileContainerField = null;
                _mainChestArrayField = null;
                _mainType = null;
                _chestType = null;
                _findChestMethod = null;
            }
        }

        public static void SetCallback(
            Action onQuickStackAllChests,
            Func<int> getSuppressedTileType,
            Func<IEnumerable<(int x, int y)>> getSuppressedChestPositions)
        {
            _onQuickStackAllChests = onQuickStackAllChests;
            _getSuppressedTileType = getSuppressedTileType;
            _getSuppressedChestPositions = getSuppressedChestPositions;
        }

        public static void ClearCallback()
        {
            _onQuickStackAllChests = null;
            _getSuppressedTileType = null;
            _getSuppressedChestPositions = null;
        }

        private static void QuickStackAllChests_Prefix(ref VanillaQuickStackState __state)
        {
            __state = default;

            try
            {
                int tileType = _getSuppressedTileType?.Invoke() ?? -1;
                if (tileType < 0)
                    return;

                var tileContainer = GetTileContainerArray();
                if (tileContainer == null || tileType >= tileContainer.Length)
                    return;

                __state.TileType = tileType;
                __state.PreviousTileContainerValue = tileContainer[tileType];
                __state.HasTileContainerState = true;

                // Prevent vanilla quick-stack from treating Storage Units as nearby chests.
                tileContainer[tileType] = false;

                var suppressedPositions = _getSuppressedChestPositions?.Invoke();
                if (suppressedPositions == null)
                    return;

                var chestArray = GetChestArray();
                if (chestArray == null)
                    return;

                var seen = new HashSet<int>();
                foreach (var pos in suppressedPositions)
                {
                    int chestIndex = FindChestIndex(pos.x, pos.y);
                    if (chestIndex < 0 || chestIndex >= chestArray.Length || !seen.Add(chestIndex))
                        continue;

                    object chest = chestArray.GetValue(chestIndex);
                    if (chest == null)
                        continue;

                    (__state.SuppressedChests ??= new List<SuppressedChestEntry>()).Add(new SuppressedChestEntry(chestIndex, chest));
                    chestArray.SetValue(null, chestIndex);
                }

                if (__state.SuppressedChests != null && __state.SuppressedChests.Count > 0)
                    _log?.Debug($"[VanillaQuickStackPatch] Suppressed {__state.SuppressedChests.Count} storage-unit chest(s) for vanilla quick-stack");
            }
            catch (Exception ex)
            {
                _log?.Error($"[VanillaQuickStackPatch] Prefix error: {ex.Message}");
            }
        }

        private static void QuickStackAllChests_Postfix(ref VanillaQuickStackState __state)
        {
            try
            {
                if (__state.HasTileContainerState)
                {
                    var tileContainer = GetTileContainerArray();
                    if (tileContainer != null && __state.TileType >= 0 && __state.TileType < tileContainer.Length)
                    {
                        tileContainer[__state.TileType] = __state.PreviousTileContainerValue;
                    }
                }

                if (__state.SuppressedChests != null && __state.SuppressedChests.Count > 0)
                {
                    var chestArray = GetChestArray();
                    if (chestArray != null)
                    {
                        foreach (var entry in __state.SuppressedChests)
                        {
                            if (entry.Index >= 0 && entry.Index < chestArray.Length)
                                chestArray.SetValue(entry.Chest, entry.Index);
                        }
                    }

                    _log?.Debug($"[VanillaQuickStackPatch] Restored {__state.SuppressedChests.Count} storage-unit chest(s) after vanilla quick-stack");
                }

                _log?.Debug("[VanillaQuickStackPatch] Player.QuickStackAllChests postfix invoked");
                _onQuickStackAllChests?.Invoke();
            }
            catch (Exception ex)
            {
                _log?.Error($"[VanillaQuickStackPatch] Postfix error: {ex.Message}");
            }
        }

        private static bool[] GetTileContainerArray()
        {
            try
            {
                if (_tileContainerField == null)
                {
                    _mainType ??= Type.GetType("Terraria.Main, Terraria")
                                 ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                    _tileContainerField = _mainType?.GetField("tileContainer", BindingFlags.Public | BindingFlags.Static);
                }

                return _tileContainerField?.GetValue(null) as bool[];
            }
            catch
            {
                return null;
            }
        }

        private static Array GetChestArray()
        {
            try
            {
                if (_mainChestArrayField == null)
                {
                    _mainType ??= Type.GetType("Terraria.Main, Terraria")
                                 ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                    _mainChestArrayField = _mainType?.GetField("chest", BindingFlags.Public | BindingFlags.Static);
                }

                return _mainChestArrayField?.GetValue(null) as Array;
            }
            catch
            {
                return null;
            }
        }

        private static int FindChestIndex(int x, int y)
        {
            try
            {
                if (_findChestMethod == null)
                {
                    _chestType ??= Type.GetType("Terraria.Chest, Terraria")
                                   ?? Assembly.Load("Terraria").GetType("Terraria.Chest");
                    _findChestMethod = _chestType?.GetMethod("FindChest",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(int), typeof(int) },
                        null);
                }

                if (_findChestMethod == null)
                    return -1;

                object indexObj = _findChestMethod.Invoke(null, new object[] { x, y });
                if (indexObj == null)
                    return -1;

                return indexObj is int index ? index : Convert.ToInt32(indexObj);
            }
            catch
            {
                return -1;
            }
        }
    }
}
