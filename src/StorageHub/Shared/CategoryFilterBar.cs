using StorageHub.Config;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace StorageHub.Shared
{
    /// <summary>
    /// Shared category filter button row — single source of truth for button labels,
    /// widths, colors, and ordering. Used by Items tab, Craft tab, and Recipes tab.
    /// </summary>
    public static class CategoryFilterBar
    {
        private struct FilterDef
        {
            public string Label;
            public CategoryFilter Filter;
            public Color4 Color;
            public string Tooltip;
            public int Width;
            public int Spacing;
        }

        private static readonly FilterDef[] Filters =
        {
            new FilterDef { Label = "All",   Filter = CategoryFilter.All,         Color = UIColors.TextDim,    Tooltip = "All Categories",  Width = 36, Spacing = 40 },
            new FilterDef { Label = "Wpns",  Filter = CategoryFilter.Weapons,     Color = UIColors.Error,      Tooltip = "Weapons",         Width = 50, Spacing = 54 },
            new FilterDef { Label = "Tools", Filter = CategoryFilter.Tools,       Color = UIColors.Info,       Tooltip = "Tools",           Width = 50, Spacing = 54 },
            new FilterDef { Label = "Armor", Filter = CategoryFilter.Armor,       Color = UIColors.Accent,     Tooltip = "Armor",           Width = 50, Spacing = 54 },
            new FilterDef { Label = "Accs",  Filter = CategoryFilter.Accessories, Color = UIColors.AccentText, Tooltip = "Accessories",     Width = 46, Spacing = 50 },
            new FilterDef { Label = "Cons",  Filter = CategoryFilter.Consumables, Color = UIColors.Success,    Tooltip = "Consumables",     Width = 46, Spacing = 50 },
            new FilterDef { Label = "Place", Filter = CategoryFilter.Placeable,   Color = UIColors.Warning,    Tooltip = "Placeable",       Width = 50, Spacing = 54 },
            new FilterDef { Label = "Mats",  Filter = CategoryFilter.Materials,   Color = UIColors.TextDim,    Tooltip = "Materials",       Width = 50, Spacing = 54 },
            new FilterDef { Label = "Misc",  Filter = CategoryFilter.Misc,        Color = UIColors.TextHint,   Tooltip = "Miscellaneous",   Width = 50, Spacing = 54 },
        };

        /// <summary>
        /// Draw the filter bar and return the result.
        /// </summary>
        /// <param name="x">Left edge X</param>
        /// <param name="y">Top edge Y</param>
        /// <param name="label">Row label (e.g. "Filter:", "Cat:")</param>
        /// <param name="labelWidth">Width reserved for the label</param>
        /// <param name="activeFilter">Currently active filter</param>
        /// <param name="tooltipText">Output: tooltip text if hovering a button, null otherwise</param>
        /// <param name="tooltipX">Output: tooltip X position</param>
        /// <param name="tooltipY">Output: tooltip Y position</param>
        /// <returns>The new active filter (changed if a button was clicked)</returns>
        public static CategoryFilter Draw(int x, int y, string label, int labelWidth,
            CategoryFilter activeFilter,
            out string tooltipText, out int tooltipX, out int tooltipY)
        {
            const int btnHeight = 25;
            tooltipText = null;
            tooltipX = 0;
            tooltipY = 0;

            UIRenderer.DrawText(label, x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            var newFilter = activeFilter;

            for (int i = 0; i < Filters.Length; i++)
            {
                var f = Filters[i];
                bool isActive = activeFilter == f.Filter;
                bool isHovered = WidgetInput.IsMouseOver(xPos, y, f.Width, btnHeight);

                Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
                UIRenderer.DrawRect(xPos, y, f.Width, btnHeight, bgColor);

                if (isActive)
                    UIRenderer.DrawRect(xPos, y + btnHeight - 2, f.Width, 2, UIColors.Accent);

                UIRenderer.DrawText(f.Label, xPos + 5, y + 6, UIColors.TextDim);

                if (isHovered)
                {
                    tooltipText = f.Tooltip;
                    tooltipX = xPos;
                    tooltipY = y + btnHeight + 5;

                    if (WidgetInput.MouseLeftClick)
                    {
                        newFilter = f.Filter;
                        WidgetInput.ConsumeClick();
                    }
                }

                xPos += f.Spacing;
            }

            return newFilter;
        }
    }
}
