using System;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Definition for a custom tile type registered via ModContext.RegisterTile.
    /// </summary>
    public class TileDefinition
    {
        // Identity / assets
        public string DisplayName { get; set; }
        public string Texture { get; set; } // relative path within mod folder

        // Tile flags (mirrors common Main.tile* / TileID.Sets flags)
        public bool Solid { get; set; }
        public bool SolidTop { get; set; }
        public bool Brick { get; set; }
        public bool NoAttach { get; set; }
        public bool Table { get; set; }
        public bool Lighted { get; set; }
        public bool LavaDeath { get; set; } = true;
        public bool FrameImportant { get; set; } = true;
        public bool NoFail { get; set; }
        public bool Cut { get; set; }
        public bool MergeDirt { get; set; }
        public bool DisableSmartCursor { get; set; }

        // Map entry
        public byte MapColorR { get; set; } = 180;
        public byte MapColorG { get; set; } = 180;
        public byte MapColorB { get; set; } = 180;

        // Dust / hit sound hints (best effort; dependent arrays may differ by Terraria build)
        public int DustType { get; set; } = -1;
        public int HitSoundStyle { get; set; } = -1;

        // Framing / object data
        public int Width { get; set; } = 1;
        public int Height { get; set; } = 1;
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int CoordinateWidth { get; set; } = 16;
        public int CoordinatePadding { get; set; } = 2;
        public int[] CoordinateHeights { get; set; }
        public bool StyleHorizontal { get; set; } = true;
        public int StyleWrapLimit { get; set; }
        public int StyleMultiplier { get; set; } = 1;

        // Container behavior hints
        public bool IsContainer { get; set; }
        public bool RegisterAsBasicChest { get; set; } = true;
        public bool ContainerInteractable { get; set; } = true;
        public bool ContainerRequiresEmptyToBreak { get; set; } = true;
        public int ContainerCapacity { get; set; } = 40;
        public string ContainerName { get; set; }

        // Drop behavior
        public string DropItemId { get; set; } // "modid:item-name" or vanilla name/id

        // Runtime hooks
        public Func<int, int, object, bool> OnRightClick { get; set; } // tileX, tileY, player -> handled?
        public Action<int, int> OnPlace { get; set; } // top-left tile
        public Action<int, int> OnBreak { get; set; } // top-left tile

        /// <summary>
        /// Validate this definition. Returns null if valid, otherwise a message.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
                return "DisplayName is required";
            if (Width <= 0 || Height <= 0)
                return "Width and Height must be positive";
            if (CoordinateWidth <= 0)
                return "CoordinateWidth must be positive";
            if (CoordinatePadding < 0)
                return "CoordinatePadding must be non-negative";
            if (StyleMultiplier <= 0)
                return "StyleMultiplier must be positive";
            if (ContainerCapacity < 1)
                return "ContainerCapacity must be >= 1";

            if (CoordinateHeights != null && CoordinateHeights.Length > 0)
            {
                for (int i = 0; i < CoordinateHeights.Length; i++)
                {
                    if (CoordinateHeights[i] <= 0)
                        return "CoordinateHeights entries must be positive";
                }
            }

            return null;
        }
    }
}
