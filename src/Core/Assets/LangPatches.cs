using System;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Patches Lang.GetItemName and Lang.GetTooltip to handle custom item types.
    ///
    /// Problem: Lang.GetItemName(int id) has a bounds check: id &lt; ItemID.Count.
    /// While TypeExtension now updates ItemID.Count, these prefixes are still needed
    /// to return our custom display names and tooltips from the extended cache arrays.
    ///
    /// Fix: Prefix patches that intercept custom type IDs and return our cached
    /// values directly.
    /// </summary>
    internal static class LangPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // Cached reflection for accessing the arrays directly
        private static Array _nameCache;
        private static Array _tooltipCache;
        private static bool _nameTraceLogged;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.lang");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                CacheArrayRefs();
                PatchGetItemName();
                PatchGetTooltip();
                _applied = true;
                _log?.Info("[LangPatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[LangPatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void CacheArrayRefs()
        {
            var langType = typeof(Terraria.Lang);

            var nameField = langType.GetField("_itemNameCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            _nameCache = nameField?.GetValue(null) as Array;

            var tooltipField = langType.GetField("_itemTooltipCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            _tooltipCache = tooltipField?.GetValue(null) as Array;

            _log?.Info($"[LangPatches] Cache refs: names={_nameCache?.Length ?? -1}, tooltips={_tooltipCache?.Length ?? -1}");
        }

        private static void PatchGetItemName()
        {
            var langType = typeof(Terraria.Lang);
            var getItemName = langType.GetMethod("GetItemName",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int) }, null);

            if (getItemName == null)
            {
                _log?.Warn("[LangPatches] Lang.GetItemName not found");
                return;
            }

            var prefix = typeof(LangPatches).GetMethod(nameof(GetItemName_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(getItemName, prefix: new HarmonyMethod(prefix));
            _log?.Info("[LangPatches] Patched Lang.GetItemName");
        }

        private static void PatchGetTooltip()
        {
            var langType = typeof(Terraria.Lang);
            var getTooltip = langType.GetMethod("GetTooltip",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int) }, null);

            if (getTooltip == null)
            {
                _log?.Warn("[LangPatches] Lang.GetTooltip not found");
                return;
            }

            var prefix = typeof(LangPatches).GetMethod(nameof(GetTooltip_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(getTooltip, prefix: new HarmonyMethod(prefix));
            _log?.Info("[LangPatches] Patched Lang.GetTooltip");
        }

        /// <summary>
        /// Prefix for Lang.GetItemName(int id).
        /// For custom types (>= VanillaItemCount), read directly from our resized cache,
        /// bypassing the vanilla "id &lt; ItemID.Count" gate.
        /// </summary>
        private static bool GetItemName_Prefix(int id, ref object __result)
        {
            if (id < ItemRegistry.VanillaItemCount) return true; // vanilla handles it

            // Re-fetch cache ref if stale (can happen if Lang re-initializes)
            if (_nameCache == null)
            {
                var field = typeof(Terraria.Lang).GetField("_itemNameCache",
                    BindingFlags.NonPublic | BindingFlags.Static);
                _nameCache = field?.GetValue(null) as Array;
            }

            if (_nameCache != null && id >= 0 && id < _nameCache.Length)
            {
                var entry = _nameCache.GetValue(id);
                if (entry != null)
                {
                    if (!_nameTraceLogged)
                    {
                        _nameTraceLogged = true;
                        // Log value for diagnosis
                        try
                        {
                            var valProp = entry.GetType().GetProperty("Value");
                            var val = valProp?.GetValue(entry);
                            _log?.Info($"[LangPatches] GetItemName trace: id={id}, value='{val}', type={entry.GetType().Name}");
                        }
                        catch { _log?.Info($"[LangPatches] GetItemName trace: id={id}, entry non-null"); }
                    }
                    __result = entry;
                    return false; // skip vanilla
                }
                else if (!_nameTraceLogged)
                {
                    _nameTraceLogged = true;
                    _log?.Warn($"[LangPatches] GetItemName: id={id} in cache but entry is NULL (cache len={_nameCache.Length})");
                }
            }
            else if (!_nameTraceLogged)
            {
                _nameTraceLogged = true;
                _log?.Warn($"[LangPatches] GetItemName: id={id} out of range (cache={_nameCache?.Length ?? -1})");
            }

            // Fall through: return LocalizedText.Empty
            var emptyField = typeof(Terraria.Localization.LocalizedText).GetField("Empty",
                BindingFlags.Public | BindingFlags.Static);
            __result = emptyField?.GetValue(null);
            return false; // skip vanilla (would return Empty anyway since id >= Count)
        }

        /// <summary>
        /// Prefix for Lang.GetTooltip(int itemId).
        /// For custom types, read directly from our resized cache.
        /// Vanilla GetTooltip does direct array access without bounds check,
        /// but may still fail if the array reference was stale.
        /// </summary>
        private static bool GetTooltip_Prefix(int itemId, ref object __result)
        {
            if (itemId < ItemRegistry.VanillaItemCount) return true; // vanilla handles it

            // Re-fetch cache ref if stale
            if (_tooltipCache == null)
            {
                var field = typeof(Terraria.Lang).GetField("_itemTooltipCache",
                    BindingFlags.NonPublic | BindingFlags.Static);
                _tooltipCache = field?.GetValue(null) as Array;
            }

            if (_tooltipCache != null && itemId >= 0 && itemId < _tooltipCache.Length)
            {
                var entry = _tooltipCache.GetValue(itemId);
                if (entry != null)
                {
                    __result = entry;
                    return false;
                }
            }

            // Return ItemTooltip.None for unknown custom types
            if (_tooltipCache != null && _tooltipCache.Length > 0)
            {
                // ItemTooltip.None is a static field — but safest to just return slot 0 (air's tooltip)
                __result = _tooltipCache.GetValue(0);
            }
            return false;
        }
    }
}
