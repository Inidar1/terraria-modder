using System;
using System.Reflection;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Adapts the existing Core tile definition shape into the runtime-owned definition contract.
    /// Reflection keeps TileRuntime independent from Core's asset namespace.
    /// </summary>
    internal static class CoreTileDefinitionAdapter
    {
        public static bool TryAdapt(object source, out TileDefinition definition)
        {
            definition = null;
            if (source == null)
                return false;

            Type sourceType = source.GetType();
            definition = new TileDefinition
            {
                DisplayName = Read<string>(source, sourceType, "DisplayName"),
                TexturePath = Read<string>(source, sourceType, "Texture"),
                Width = Read<int>(source, sourceType, "Width", 1),
                Height = Read<int>(source, sourceType, "Height", 1),
                OriginX = Read<int>(source, sourceType, "OriginX"),
                OriginY = Read<int>(source, sourceType, "OriginY"),
                CoordinateWidth = Read<int>(source, sourceType, "CoordinateWidth", 16),
                CoordinatePadding = Read<int>(source, sourceType, "CoordinatePadding", 2),
                CoordinateHeights = Read<int[]>(source, sourceType, "CoordinateHeights"),
                StyleHorizontal = Read<bool>(source, sourceType, "StyleHorizontal", true),
                StyleWrapLimit = Read<int>(source, sourceType, "StyleWrapLimit"),
                StyleMultiplier = Read<int>(source, sourceType, "StyleMultiplier", 1),
                IsContainer = Read<bool>(source, sourceType, "IsContainer"),
                RegisterAsBasicChest = Read<bool>(source, sourceType, "RegisterAsBasicChest", true),
                ContainerInteractable = Read<bool>(source, sourceType, "ContainerInteractable", true),
                ContainerRequiresEmptyToBreak = Read<bool>(source, sourceType, "ContainerRequiresEmptyToBreak", true),
                ContainerCapacity = Read<int>(source, sourceType, "ContainerCapacity", 40),
                ContainerName = Read<string>(source, sourceType, "ContainerName"),
                DropItemId = Read<string>(source, sourceType, "DropItemId"),
                Solid = Read<bool>(source, sourceType, "Solid"),
                SolidTop = Read<bool>(source, sourceType, "SolidTop"),
                Brick = Read<bool>(source, sourceType, "Brick"),
                NoAttach = Read<bool>(source, sourceType, "NoAttach"),
                Table = Read<bool>(source, sourceType, "Table"),
                Lighted = Read<bool>(source, sourceType, "Lighted"),
                LavaDeath = Read<bool>(source, sourceType, "LavaDeath", true),
                FrameImportant = Read<bool>(source, sourceType, "FrameImportant", true),
                NoFail = Read<bool>(source, sourceType, "NoFail"),
                Cut = Read<bool>(source, sourceType, "Cut"),
                MergeDirt = Read<bool>(source, sourceType, "MergeDirt"),
                DisableSmartCursor = Read<bool>(source, sourceType, "DisableSmartCursor"),
                MapColorR = Read<byte>(source, sourceType, "MapColorR", 180),
                MapColorG = Read<byte>(source, sourceType, "MapColorG", 180),
                MapColorB = Read<byte>(source, sourceType, "MapColorB", 180),
                DustType = Read<int>(source, sourceType, "DustType", -1),
                HitSoundStyle = Read<int>(source, sourceType, "HitSoundStyle", -1),
                OnRightClick = AdaptRightClick(source, sourceType),
                OnPlace = AdaptAction(source, sourceType, "OnPlace"),
                OnBreak = AdaptAction(source, sourceType, "OnBreak")
            };

            return true;
        }

        private static Func<object, int, int, bool> AdaptRightClick(object source, Type sourceType)
        {
            object value = ReadRaw(source, sourceType, "OnRightClick");
            if (!(value is Delegate callback))
                return null;

            return (player, tileX, tileY) =>
            {
                object result = callback.DynamicInvoke(tileX, tileY, player);
                return result is bool handled && handled;
            };
        }

        private static Action<int, int> AdaptAction(object source, Type sourceType, string propertyName)
        {
            object value = ReadRaw(source, sourceType, propertyName);
            if (!(value is Delegate callback))
                return null;

            return (tileX, tileY) => callback.DynamicInvoke(tileX, tileY);
        }

        private static T Read<T>(object source, Type sourceType, string propertyName, T fallback = default(T))
        {
            object value = ReadRaw(source, sourceType, propertyName);
            if (value == null)
                return fallback;

            if (value is T typed)
                return typed;

            return fallback;
        }

        private static object ReadRaw(object source, Type sourceType, string propertyName)
        {
            PropertyInfo property = sourceType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(source);
        }
    }
}
