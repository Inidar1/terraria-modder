using System;
using System.Collections;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Applies tile flags and object-data metadata for runtime-owned custom tile IDs.
    /// </summary>
    internal static class TileObjectRegistrar
    {
        private sealed class TileObjectDataNotReadyException : Exception
        {
            public TileObjectDataNotReadyException(string message) : base(message) { }
        }

        private static ILogger _log;
        private static bool _applied;
        private static int _lastFailureCount = -1;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        public static void ApplyDefinitions()
        {
            if (TileRegistry.Count == 0) return;
            if (_applied)
            {
                if (!NeedsReapply())
                    return;

                _applied = false;
                _lastFailureCount = -1;
                _log?.Warn("[TileRuntime.TileObjectRegistrar] Detected missing TileObjectData entries after prior apply; reapplying");
            }

            int applied = 0;
            int failed = 0;
            int deferred = 0;
            foreach (var fullId in TileRegistry.AllIds)
            {
                int runtimeType = TileRegistry.GetRuntimeType(fullId);
                if (runtimeType < 0) continue;

                var def = TileRegistry.GetDefinition(runtimeType);
                if (def == null) continue;

                try
                {
                    ApplyTileFlags(runtimeType, def);
                    RegisterTileObjectData(runtimeType, def);
                    RegisterMapEntry(runtimeType, def);
                    applied++;
                }
                catch (Exception ex)
                {
                    Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    if (root is TileObjectDataNotReadyException)
                    {
                        deferred++;
                        continue;
                    }

                    failed++;
                    _log?.Error($"[TileRuntime.TileObjectRegistrar] Failed applying {fullId} (type {runtimeType}): {root.GetType().Name}: {root.Message}");
                }
            }

            if (failed == 0 && deferred == 0)
            {
                _applied = true;
                _log?.Info($"[TileRuntime.TileObjectRegistrar] Applied metadata for {applied} custom tiles");
            }
            else if (_lastFailureCount != failed)
            {
                _lastFailureCount = failed;
                if (failed > 0)
                    _log?.Warn($"[TileRuntime.TileObjectRegistrar] Applied metadata for {applied}/{TileRegistry.Count} custom tiles; will retry failed registrations");
                else if (deferred > 0)
                    _log?.Info($"[TileRuntime.TileObjectRegistrar] TileObjectData not ready yet ({deferred} pending); will retry registration");
            }
        }

        private static void ApplyTileFlags(int runtimeType, TileDefinition def)
        {
            SetMainBool("tileSolid", runtimeType, def.Solid);
            SetMainBool("tileSolidTop", runtimeType, def.SolidTop);
            SetMainBool("tileBrick", runtimeType, def.Brick);
            SetMainBool("tileNoAttach", runtimeType, def.NoAttach);
            SetMainBool("tileTable", runtimeType, def.Table);
            SetMainBool("tileLighted", runtimeType, def.Lighted);
            SetMainBool("tileLavaDeath", runtimeType, def.LavaDeath);
            SetMainBool("tileFrameImportant", runtimeType, def.FrameImportant);
            SetMainBool("tileNoFail", runtimeType, def.NoFail);
            SetMainBool("tileCut", runtimeType, def.Cut);
            SetMainBool("tileMergeDirt", runtimeType, def.MergeDirt);
            SetMainBool("tileContainer", runtimeType, def.IsContainer);

            SetTileSetBool("DisableSmartCursor", runtimeType, def.DisableSmartCursor);
            SetTileSetBool("BasicChest", runtimeType, def.IsContainer && def.RegisterAsBasicChest);
        }

        private static void RegisterTileObjectData(int runtimeType, TileDefinition def)
        {
            if (def.Width == 1 && def.Height == 1)
                return;

            var asm = typeof(Main).Assembly;
            var todType = asm.GetType("Terraria.ObjectData.TileObjectData");
            if (todType == null)
                throw new TileObjectDataNotReadyException("TileObjectData type not found");

            if (HasTileObjectData(todType, runtimeType))
                return;

            var newTileField = todType.GetField("newTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (newTileField == null)
                throw new TileObjectDataNotReadyException("TileObjectData.newTile field not found");

            object styleTemplate = GetRequiredStyleTemplate(todType, def);
            object data = CreateTileObjectDataCopy(todType, styleTemplate);
            if (data == null)
                throw new InvalidOperationException($"Could not clone TileObjectData style template for {def.DisplayName}");

            try
            {
                WithTileObjectDataWriteAccess(todType, () =>
                {
                    newTileField.SetValue(null, data);
                    ApplyTileObjectDataFields(data, def);
                    AssignTileObjectDataEntry(todType, runtimeType, data);
                });
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                throw new InvalidOperationException(
                    $"TileObjectData registration failed for tile {runtimeType} ({def.DisplayName}): {root.GetType().Name}: {root.Message}",
                    root);
            }

            if (!HasTileObjectData(todType, runtimeType))
                throw new InvalidOperationException($"Could not register TileObjectData for tile {runtimeType} ({def.DisplayName})");
        }

        private static void AssignTileObjectDataEntry(Type todType, int runtimeType, object data)
        {
            var dataField = todType.GetField("_data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (!(dataField?.GetValue(null) is IList list))
                throw new InvalidOperationException("TileObjectData._data static list unavailable");

            while (list.Count <= runtimeType)
                list.Add(null);

            list[runtimeType] = data;
        }

        private static object GetRequiredStyleTemplate(Type todType, TileDefinition def)
        {
            int width = Math.Max(1, def?.Width ?? 1);
            int height = Math.Max(1, def?.Height ?? 1);
            string styleFieldName = $"Style{width}x{height}";

            object styleTemplate = GetStaticFieldValue(todType, styleFieldName);
            if (styleTemplate == null)
                throw new TileObjectDataNotReadyException($"TileObjectData.{styleFieldName} is not initialized yet");

            return styleTemplate;
        }

        private static void ApplyTileObjectDataFields(object tileObjectData, TileDefinition def)
        {
            SetMember(tileObjectData, "Width", def.Width);
            SetMember(tileObjectData, "Height", def.Height);
            SetMember(tileObjectData, "CoordinateWidth", def.CoordinateWidth);
            SetMember(tileObjectData, "CoordinatePadding", def.CoordinatePadding);
            SetMember(tileObjectData, "StyleHorizontal", def.StyleHorizontal);
            if (def.StyleWrapLimit > 0) SetMember(tileObjectData, "StyleWrapLimit", def.StyleWrapLimit);
            if (def.StyleMultiplier > 0) SetMember(tileObjectData, "StyleMultiplier", def.StyleMultiplier);

            var coordHeights = def.CoordinateHeights != null && def.CoordinateHeights.Length > 0
                ? def.CoordinateHeights
                : BuildDefaultCoordinateHeights(def.Height);
            SetMember(tileObjectData, "CoordinateHeights", coordHeights);

            object origin = CreatePoint16(def.OriginX, def.OriginY);
            if (origin != null) SetMember(tileObjectData, "Origin", origin);

            if (!def.Solid && def.SolidTop && def.Width > 0)
            {
                var anchorBottom = new AnchorData(
                    AnchorType.SolidTile | AnchorType.SolidWithTop | AnchorType.Table,
                    def.Width,
                    0);
                SetMember(tileObjectData, "AnchorBottom", anchorBottom);
            }

            if (def.IsContainer)
                SetMember(tileObjectData, "LavaDeath", def.LavaDeath);
        }

        private static bool HasTileObjectData(Type todType, int runtimeType)
        {
            try
            {
                var getTileData = todType.GetMethod("GetTileData", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int), typeof(int), typeof(int) }, null);
                if (getTileData == null)
                    return false;

                return getTileData.Invoke(null, new object[] { runtimeType, 0, 0 }) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool NeedsReapply()
        {
            try
            {
                var todType = typeof(Main).Assembly.GetType("Terraria.ObjectData.TileObjectData");
                if (todType == null)
                    return false;

                foreach (var fullId in TileRegistry.AllIds)
                {
                    int runtimeType = TileRegistry.GetRuntimeType(fullId);
                    if (runtimeType < 0) continue;

                    var def = TileRegistry.GetDefinition(runtimeType);
                    if (def == null) continue;
                    if (def.Width <= 1 && def.Height <= 1) continue;

                    if (!HasTileObjectData(todType, runtimeType))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static object GetStaticFieldValue(Type type, string fieldName)
        {
            try
            {
                var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return field?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static void WithTileObjectDataWriteAccess(Type todType, Action action)
        {
            FieldInfo roField = null;
            bool originalReadOnly = true;
            bool restore = false;
            try
            {
                roField = todType?.GetField("readOnlyData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (roField != null && roField.FieldType == typeof(bool))
                {
                    originalReadOnly = (bool)roField.GetValue(null);
                    if (originalReadOnly)
                    {
                        roField.SetValue(null, false);
                        restore = true;
                    }
                }

                action();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("TileObjectData mutation failed", ex);
            }
            finally
            {
                if (restore && roField != null)
                {
                    try { roField.SetValue(null, originalReadOnly); }
                    catch { }
                }
            }
        }

        private static object CreateTileObjectDataCopy(Type todType, object template)
        {
            if (todType == null || template == null) return null;

            try
            {
                var ctor = todType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { todType },
                    null);
                return ctor?.Invoke(new[] { template });
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileObjectRegistrar] Failed to create TileObjectData copy: {ex.Message}");
                return null;
            }
        }

        private static void RegisterMapEntry(int runtimeType, TileDefinition def)
        {
            try
            {
                var langType = typeof(Lang);
                foreach (var fieldName in new[] { "_tileNameCache", "_mapLegendCache" })
                {
                    var field = langType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                    var arr = field?.GetValue(null) as Array;
                    if (arr == null || runtimeType < 0 || runtimeType >= arr.Length) continue;

                    object text = CreateLocalizedText($"TileName.Custom_{runtimeType}", def.DisplayName ?? $"Custom Tile {runtimeType}");
                    if (text != null)
                        arr.SetValue(text, runtimeType);
                }
            }
            catch
            {
            }
        }

        private static void SetMainBool(string fieldName, int index, bool value)
        {
            try
            {
                var field = typeof(Main).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var arr = field?.GetValue(null) as bool[];
                if (arr != null && index >= 0 && index < arr.Length)
                    arr[index] = value;
            }
            catch { }
        }

        private static void SetTileSetBool(string fieldName, int index, bool value)
        {
            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var field = setsType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as bool[];
                if (arr != null && index >= 0 && index < arr.Length)
                    arr[index] = value;
            }
            catch { }
        }

        private static void SetMember(object instance, string memberName, object value)
        {
            if (instance == null) return;

            var type = instance.GetType();
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
                prop.SetValue(instance, value, null);
        }

        private static int[] BuildDefaultCoordinateHeights(int height)
        {
            var arr = new int[Math.Max(1, height)];
            for (int i = 0; i < arr.Length; i++) arr[i] = 16;
            return arr;
        }

        private static object CreatePoint16(int x, int y)
        {
            try
            {
                var point16Type = typeof(Main).Assembly.GetType("Terraria.DataStructures.Point16");
                if (point16Type == null) return null;

                var ctor = point16Type.GetConstructor(new[] { typeof(short), typeof(short) });
                if (ctor != null) return ctor.Invoke(new object[] { (short)x, (short)y });

                ctor = point16Type.GetConstructor(new[] { typeof(int), typeof(int) });
                if (ctor != null) return ctor.Invoke(new object[] { x, y });

                return Activator.CreateInstance(point16Type);
            }
            catch
            {
                return null;
            }
        }

        private static object CreateLocalizedText(string key, string value)
        {
            try
            {
                var type = typeof(Terraria.Localization.LocalizedText);
                var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
                return ctor?.Invoke(new object[] { key, value ?? string.Empty });
            }
            catch
            {
                return null;
            }
        }
    }
}
