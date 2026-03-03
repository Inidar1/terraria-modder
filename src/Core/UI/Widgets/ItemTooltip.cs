using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.UI.Widgets
{
    /// <summary>
    /// Vanilla-style item tooltip widget. Shows full item stats like Terraria's native tooltips:
    /// rarity-colored name, damage+type, speed tier, knockback tier, defense, tool power,
    /// tooltip description lines, prefix modifications (green/red), and more.
    ///
    /// Usage: Call Set() during hover detection, DrawDeferred() renders at end of frame.
    /// Automatically integrated into Tooltip.Clear()/DrawDeferred() for DraggablePanel users.
    /// For manual draw loops (like StorageHub), call DrawDeferred() explicitly after Set().
    /// </summary>
    public static class ItemTooltip
    {
        // Pending state
        private static bool _hasTooltip;
        private static int _itemType;
        private static int _prefix;
        private static int _stack;
        private static string[] _extraLines;
        private static string _nameOverride;

        // Tooltip line model
        private struct TooltipLine
        {
            public string Text;
            public Color4 Color;
        }

        private static readonly List<TooltipLine> _lines = new List<TooltipLine>(24);

        // Prefix colors (matching vanilla)
        private static readonly Color4 PrefixBetter = new Color4(120, 190, 120);
        private static readonly Color4 PrefixWorse = new Color4(190, 120, 120);

        // Logging
        private static readonly ILogger _log = LogManager.GetLogger("ItemTooltip");

        /// <summary>
        /// Set an item tooltip to display. Call during hover detection.
        /// Extra lines are appended at the end (e.g. "In: Chest #3").
        /// </summary>
        public static void Set(int itemType, int prefix = 0, int stack = 1, params string[] extraLines)
        {
            if (itemType <= 0) return;
            _itemType = itemType;
            _prefix = prefix;
            _stack = stack;
            _extraLines = extraLines;
            _nameOverride = null;
            _hasTooltip = true;
        }

        /// <summary>
        /// Set an item tooltip with a custom display name (e.g. "Any Iron Bar" for recipe groups).
        /// Stats and tooltip lines come from the item type; only the title is overridden.
        /// </summary>
        public static void SetWithName(int itemType, string displayName, params string[] extraLines)
        {
            if (itemType <= 0) return;
            _itemType = itemType;
            _prefix = 0;
            _stack = 1;
            _extraLines = extraLines;
            _nameOverride = displayName;
            _hasTooltip = true;
        }

        /// <summary>
        /// Clear pending tooltip. Called automatically via Tooltip.Clear().
        /// </summary>
        public static void Clear()
        {
            _hasTooltip = false;
            _nameOverride = null;
        }

        /// <summary>
        /// Draw the item tooltip near the mouse cursor.
        /// Called automatically via Tooltip.DrawDeferred(), or manually for non-DraggablePanel UIs.
        /// </summary>
        public static void DrawDeferred()
        {
            if (!_hasTooltip) return;
            _hasTooltip = false;

            if (ContentSamples.ItemsByType == null || ContentSamples.ItemsByType.Count == 0)
                return;

            BuildLines();
            if (_lines.Count == 0) return;

            RenderLines();
        }

        #region Line Building

        private static void BuildLines()
        {
            _lines.Clear();

            if (!ContentSamples.ItemsByType.ContainsKey(_itemType)) return;

            // Get base item from ContentSamples
            Item baseItem = ContentSamples.ItemsByType[_itemType];

            // If prefix specified, create a temp prefixed item for display
            Item displayItem = baseItem;
            if (_prefix > 0)
            {
                try
                {
                    displayItem = new Item();
                    displayItem.SetDefaults(_itemType);
                    displayItem.Prefix(_prefix);
                }
                catch (Exception ex)
                {
                    _log.Debug($"[ItemTooltip] Prefix item creation failed: {ex.Message}");
                    displayItem = baseItem;
                }
            }

            // Read fields from display item (prefixed if applicable)
            int damage = displayItem.damage;
            int crit = displayItem.crit;
            int useAnimation = displayItem.useAnimation;
            int useStyle = displayItem.useStyle;
            float knockBack = displayItem.knockBack;
            int defense = displayItem.defense;
            int pick = displayItem.pick;
            int axe = displayItem.axe;
            int hammer = displayItem.hammer;
            int tileBoost = displayItem.tileBoost;
            int healLife = displayItem.healLife;
            int healMana = displayItem.healMana;
            int mana = displayItem.mana;
            bool consumable = displayItem.consumable;
            bool material = displayItem.material;
            bool vanity = displayItem.vanity;
            bool accessory = displayItem.accessory;
            int headSlot = displayItem.headSlot;
            int bodySlot = displayItem.bodySlot;
            int legSlot = displayItem.legSlot;
            int createTile = displayItem.createTile;
            int createWall = displayItem.createWall;
            int ammo = displayItem.ammo;
            bool notAmmo = displayItem.notAmmo;
            int buffTime = displayItem.buffTime;
            bool melee = displayItem.melee;
            bool ranged = displayItem.ranged;
            bool magic = displayItem.magic;
            bool summon = displayItem.summon;
            int rarity = displayItem.rare;
            int bait = displayItem.bait;
            int fishingPole = displayItem.fishingPole;
            bool questItem = displayItem.questItem;

            // === Line 0: Name (rarity colored) ===
            string name;
            if (_nameOverride != null)
            {
                name = _nameOverride;
            }
            else
            {
                try
                {
                    name = displayItem.AffixName() ?? "???";
                }
                catch
                {
                    name = "???";
                }
            }
            if (_stack > 1) name += " (" + _stack + ")";
            _lines.Add(new TooltipLine { Text = name, Color = UIColors.GetRarityColor(rarity) });

            // === Damage + type ===
            if (damage > 0 && (!notAmmo || useStyle != 0))
            {
                string damageType;
                if (melee) damageType = GetTip(2);
                else if (ranged) damageType = GetTip(3);
                else if (magic) damageType = GetTip(4);
                else if (summon) damageType = GetTip(53);
                else damageType = GetTip(55);

                AddLine(damage + damageType, UIColors.Text);

                // Crit chance
                if (melee || ranged || magic)
                    AddLine(crit + GetTip(5), UIColors.Text);

                // Speed tier
                if (useStyle != 0 && !summon)
                {
                    if (useAnimation <= 8) AddLine(GetTip(6), UIColors.Text);
                    else if (useAnimation <= 20) AddLine(GetTip(7), UIColors.Text);
                    else if (useAnimation <= 25) AddLine(GetTip(8), UIColors.Text);
                    else if (useAnimation <= 30) AddLine(GetTip(9), UIColors.Text);
                    else if (useAnimation <= 35) AddLine(GetTip(10), UIColors.Text);
                    else if (useAnimation <= 45) AddLine(GetTip(11), UIColors.Text);
                    else if (useAnimation <= 55) AddLine(GetTip(12), UIColors.Text);
                    else AddLine(GetTip(13), UIColors.Text);
                }

                // Knockback tier
                if (knockBack == 0f) AddLine(GetTip(14), UIColors.Text);
                else if (knockBack <= 1.5f) AddLine(GetTip(15), UIColors.Text);
                else if (knockBack <= 3f) AddLine(GetTip(16), UIColors.Text);
                else if (knockBack <= 4f) AddLine(GetTip(17), UIColors.Text);
                else if (knockBack <= 6f) AddLine(GetTip(18), UIColors.Text);
                else if (knockBack <= 7f) AddLine(GetTip(19), UIColors.Text);
                else if (knockBack <= 9f) AddLine(GetTip(20), UIColors.Text);
                else if (knockBack <= 11f) AddLine(GetTip(21), UIColors.Text);
                else AddLine(GetTip(22), UIColors.Text);
            }

            // === Fishing / bait ===
            if (fishingPole > 0)
                AddLine(fishingPole + "% fishing power", UIColors.Text);
            if (bait > 0)
                AddLine(bait + "% bait power", UIColors.Text);

            // === Equippable ===
            if (accessory || headSlot > 0 || bodySlot > 0 || legSlot > 0)
                AddLine(GetTip(23), UIColors.Text);

            // === Quest item ===
            if (questItem)
                AddLine("Quest Item", UIColors.Warning);

            // === Vanity ===
            if (vanity)
                AddLine(GetTip(24), UIColors.Text);

            // === Defense ===
            if (defense > 0)
                AddLine(defense + GetTip(25), UIColors.Text);

            // === Tool power ===
            if (pick > 0) AddLine(pick + GetTip(26), UIColors.Text);
            if (axe > 0) AddLine((axe * 5) + GetTip(27), UIColors.Text);
            if (hammer > 0) AddLine(hammer + GetTip(28), UIColors.Text);

            // === Tile range boost ===
            if (tileBoost != 0)
            {
                string sign = tileBoost > 0 ? "+" : "";
                AddLine(sign + tileBoost + GetTip(54), UIColors.Text);
            }

            // === Heal life/mana ===
            if (healLife > 0)
                AddLine("Restores " + healLife + " life", UIColors.Text);
            if (healMana > 0)
                AddLine("Restores " + healMana + " mana", UIColors.Text);

            // === Mana cost ===
            if (mana > 0)
                AddLine("Uses " + mana + " mana", UIColors.Text);

            // === Placeable / Ammo / Consumable ===
            if (createWall > 0 || createTile > -1)
            {
                if (consumable)
                    AddLine(GetTip(33), UIColors.Text);
            }
            else if (ammo > 0 && !notAmmo)
            {
                AddLine(GetTip(34), UIColors.Text);
            }
            else if (consumable)
            {
                AddLine(GetTip(35), UIColors.Text);
            }

            // === Material ===
            if (material)
                AddLine(GetTip(36), UIColors.Text);

            // === Tooltip description lines ===
            try
            {
                var tooltip = displayItem.ToolTip;
                if (tooltip != null)
                {
                    int lineCount = tooltip.Lines;
                    for (int i = 0; i < lineCount; i++)
                    {
                        string line = tooltip.GetLine(i);
                        if (!string.IsNullOrEmpty(line))
                            AddLine(line, UIColors.Text);
                    }
                }
            }
            catch { }

            // === Buff duration ===
            if (buffTime > 0)
            {
                int seconds = buffTime / 60;
                if (seconds < 60)
                    AddLine(Math.Round((double)buffTime / 60.0) + " second duration", UIColors.Text);
                else
                    AddLine(Math.Round((double)seconds / 60.0) + " minute duration", UIColors.Text);
            }

            // === Prefix modifications ===
            if (_prefix > 0 && displayItem != baseItem)
                BuildPrefixLines(baseItem, displayItem);

            // === Extra context lines from caller ===
            if (_extraLines != null)
            {
                for (int i = 0; i < _extraLines.Length; i++)
                {
                    if (!string.IsNullOrEmpty(_extraLines[i]))
                        AddLine(_extraLines[i], UIColors.Success);
                }
            }
        }

        private static void BuildPrefixLines(Item baseItem, Item prefixedItem)
        {
            // Damage
            int baseDmg = baseItem.damage;
            int prefDmg = prefixedItem.damage;
            if (baseDmg > 0 && prefDmg != baseDmg)
            {
                float pct = ((float)(prefDmg - baseDmg) / baseDmg) * 100f;
                bool better = pct > 0;
                AddLine((better ? "+" : "") + (int)Math.Round(pct) + GetTip(39), better ? PrefixBetter : PrefixWorse);
            }

            // Speed (useAnimation — lower is faster, so invert)
            int baseSpd = baseItem.useAnimation;
            int prefSpd = prefixedItem.useAnimation;
            if (baseSpd > 0 && prefSpd != baseSpd)
            {
                float pct = ((float)(baseSpd - prefSpd) / baseSpd) * 100f;
                bool better = pct > 0;
                AddLine((better ? "+" : "") + (int)Math.Round(pct) + GetTip(40), better ? PrefixBetter : PrefixWorse);
            }

            // Crit
            int baseCrit = baseItem.crit;
            int prefCrit = prefixedItem.crit;
            if (prefCrit != baseCrit)
            {
                int diff = prefCrit - baseCrit;
                bool better = diff > 0;
                AddLine((better ? "+" : "") + diff + GetTip(41), better ? PrefixBetter : PrefixWorse);
            }

            // Mana (lower is better, so invert display)
            int baseMana = baseItem.mana;
            int prefMana = prefixedItem.mana;
            if (baseMana > 0 && prefMana != baseMana)
            {
                float pct = ((float)(prefMana - baseMana) / baseMana) * 100f;
                bool better = pct < 0;
                AddLine((pct > 0 ? "+" : "") + (int)Math.Round(pct) + GetTip(42), better ? PrefixBetter : PrefixWorse);
            }

            // Size (scale)
            float baseScale = baseItem.scale;
            float prefScale = prefixedItem.scale;
            if (baseScale > 0f && Math.Abs(prefScale - baseScale) > 0.001f)
            {
                float pct = ((prefScale - baseScale) / baseScale) * 100f;
                bool better = pct > 0;
                AddLine((better ? "+" : "") + (int)Math.Round(pct) + GetTip(43), better ? PrefixBetter : PrefixWorse);
            }

            // Shoot speed (velocity)
            float baseVel = baseItem.shootSpeed;
            float prefVel = prefixedItem.shootSpeed;
            if (baseVel > 0f && Math.Abs(prefVel - baseVel) > 0.001f)
            {
                float pct = ((prefVel - baseVel) / baseVel) * 100f;
                bool better = pct > 0;
                AddLine((better ? "+" : "") + (int)Math.Round(pct) + GetTip(44), better ? PrefixBetter : PrefixWorse);
            }

            // Knockback
            float baseKb = baseItem.knockBack;
            float prefKb = prefixedItem.knockBack;
            if (baseKb > 0f && Math.Abs(prefKb - baseKb) > 0.001f)
            {
                float pct = ((prefKb - baseKb) / baseKb) * 100f;
                bool better = pct > 0;
                AddLine((better ? "+" : "") + (int)Math.Round(pct) + GetTip(45), better ? PrefixBetter : PrefixWorse);
            }
        }

        #endregion

        #region Rendering

        private static void RenderLines()
        {
            int maxWidth = 350;
            int lineHeight = 16;
            int padding = 8;
            int maxContentWidth = maxWidth - padding * 2;

            // Build wrapped lines with colors
            var wrappedLines = new List<TooltipLine>();
            foreach (var line in _lines)
            {
                if (string.IsNullOrEmpty(line.Text))
                {
                    wrappedLines.Add(line);
                    continue;
                }

                if (TextUtil.MeasureWidth(line.Text) <= maxContentWidth)
                {
                    wrappedLines.Add(line);
                    continue;
                }

                // Word-wrap
                string remaining = line.Text;
                while (TextUtil.MeasureWidth(remaining) > maxContentWidth)
                {
                    int breakAt = -1;
                    for (int i = remaining.Length - 1; i > 0; i--)
                    {
                        if (remaining[i] == ' ' && TextUtil.MeasureWidth(remaining.Substring(0, i)) <= maxContentWidth)
                        {
                            breakAt = i;
                            break;
                        }
                    }
                    if (breakAt <= 0)
                    {
                        breakAt = remaining.Length;
                        for (int i = 1; i < remaining.Length; i++)
                        {
                            if (TextUtil.MeasureWidth(remaining.Substring(0, i)) > maxContentWidth)
                            {
                                breakAt = Math.Max(1, i - 1);
                                break;
                            }
                        }
                    }
                    wrappedLines.Add(new TooltipLine { Text = remaining.Substring(0, breakAt), Color = line.Color });
                    remaining = remaining.Substring(breakAt).TrimStart();
                }
                if (remaining.Length > 0)
                    wrappedLines.Add(new TooltipLine { Text = remaining, Color = line.Color });
            }

            // Calculate dimensions
            int tooltipWidth = 0;
            foreach (var line in wrappedLines)
                tooltipWidth = Math.Max(tooltipWidth, TextUtil.MeasureWidth(line.Text));
            tooltipWidth = Math.Min(tooltipWidth + padding * 2, maxWidth);

            int tooltipHeight = wrappedLines.Count * lineHeight + padding * 2;

            // Position near mouse, clamped to screen
            int tx = WidgetInput.MouseX + 16;
            int ty = WidgetInput.MouseY + 16;

            if (tx + tooltipWidth > WidgetInput.ScreenWidth - 4)
                tx = WidgetInput.MouseX - tooltipWidth - 4;
            if (ty + tooltipHeight > WidgetInput.ScreenHeight - 4)
                ty = WidgetInput.MouseY - tooltipHeight - 4;

            tx = Math.Max(4, tx);
            ty = Math.Max(4, ty);

            // Draw background
            UIRenderer.DrawRect(tx, ty, tooltipWidth, tooltipHeight, UIColors.TooltipBg);
            UIRenderer.DrawRectOutline(tx, ty, tooltipWidth, tooltipHeight, UIColors.Border);

            // Draw lines
            int ly = ty + padding;
            for (int i = 0; i < wrappedLines.Count; i++)
            {
                UIRenderer.DrawText(wrappedLines[i].Text, tx + padding, ly, wrappedLines[i].Color);
                ly += lineHeight;
            }
        }

        #endregion

        #region Helpers

        private static void AddLine(string text, Color4 color)
        {
            if (!string.IsNullOrEmpty(text))
                _lines.Add(new TooltipLine { Text = text, Color = color });
        }

        private static string GetTip(int index)
        {
            if (Lang.tip == null || index < 0 || index >= Lang.tip.Length)
                return "";
            try
            {
                var lt = Lang.tip[index];
                return lt?.Value ?? "";
            }
            catch { return ""; }
        }

        #endregion
    }
}
