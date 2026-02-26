using System;
using System.Collections.Generic;
using StorageHub.Storage;

namespace StorageHub.UI
{
    /// <summary>
    /// Lightweight search parser with MagicStorage-style tag syntax.
    /// Example: "sword #weapon -#favorited".
    /// </summary>
    internal sealed class MagicSearchQuery
    {
        private static readonly Dictionary<string, string> TagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "weapon", "weapon" }, { "weapons", "weapon" }, { "wpn", "weapon" }, { "wpns", "weapon" },
            { "melee", "melee" }, { "ranged", "ranged" }, { "magic", "magic" }, { "summon", "summon" },
            { "thrown", "thrown" }, { "throwing", "thrown" },
            { "ammo", "ammo" },
            { "tool", "tool" }, { "tools", "tool" },
            { "armor", "armor" }, { "armour", "armor" },
            { "accessory", "accessory" }, { "accessories", "accessory" }, { "acc", "accessory" }, { "accs", "accessory" },
            { "consumable", "consumable" }, { "consumables", "consumable" }, { "cons", "consumable" },
            { "potion", "potion" }, { "potions", "potion" },
            { "placeable", "placeable" }, { "place", "placeable" }, { "tile", "placeable" }, { "tiles", "placeable" }, { "block", "placeable" }, { "blocks", "placeable" },
            { "material", "material" }, { "materials", "material" }, { "mat", "material" }, { "mats", "material" },
            { "vanity", "vanity" },
            { "equipment", "equipment" }, { "equip", "equipment" },
            { "fishing", "fishing" }, { "fish", "fishing" },
            { "stackable", "stackable" }, { "stack", "stackable" },
            { "unstackable", "unstackable" }, { "unstack", "unstackable" },
            { "favorite", "favorite" }, { "favorites", "favorite" }, { "favourite", "favorite" }, { "favourites", "favorite" }, { "favorited", "favorite" }, { "favourited", "favorite" }, { "fav", "favorite" },
            { "misc", "misc" }
        };

        private readonly List<string> _includeText;
        private readonly List<string> _excludeText;
        private readonly List<string> _includeTags;
        private readonly List<string> _excludeTags;

        private MagicSearchQuery(
            List<string> includeText,
            List<string> excludeText,
            List<string> includeTags,
            List<string> excludeTags)
        {
            _includeText = includeText;
            _excludeText = excludeText;
            _includeTags = includeTags;
            _excludeTags = excludeTags;
        }

        public static MagicSearchQuery Parse(string raw)
        {
            var includeText = new List<string>();
            var excludeText = new List<string>();
            var includeTags = new List<string>();
            var excludeTags = new List<string>();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string token = parts[i]?.Trim();
                    if (string.IsNullOrEmpty(token))
                        continue;

                    bool negate = false;
                    while (token.Length > 0 && (token[0] == '-' || token[0] == '!'))
                    {
                        negate = true;
                        token = token.Substring(1);
                    }

                    if (token.Length == 0)
                        continue;

                    if (token[0] == '#')
                    {
                        string tag = NormalizeTag(token.Substring(1));
                        if (string.IsNullOrEmpty(tag))
                            continue;

                        var list = negate ? excludeTags : includeTags;
                        if (!list.Contains(tag))
                            list.Add(tag);
                    }
                    else
                    {
                        string term = token.ToLowerInvariant();
                        var list = negate ? excludeText : includeText;
                        if (!list.Contains(term))
                            list.Add(term);
                    }
                }
            }

            return new MagicSearchQuery(includeText, excludeText, includeTags, excludeTags);
        }

        public bool Matches(string name, Func<string, bool> tagMatcher)
        {
            string lower = name?.ToLowerInvariant() ?? string.Empty;

            for (int i = 0; i < _includeText.Count; i++)
            {
                if (!lower.Contains(_includeText[i]))
                    return false;
            }

            for (int i = 0; i < _excludeText.Count; i++)
            {
                if (lower.Contains(_excludeText[i]))
                    return false;
            }

            if (tagMatcher != null)
            {
                for (int i = 0; i < _includeTags.Count; i++)
                {
                    if (!tagMatcher(_includeTags[i]))
                        return false;
                }

                for (int i = 0; i < _excludeTags.Count; i++)
                {
                    if (tagMatcher(_excludeTags[i]))
                        return false;
                }
            }

            return true;
        }

        public static bool MatchesTag(ItemSearchTraits traits, string normalizedTag, bool isFavorite)
        {
            switch (normalizedTag)
            {
                case "weapon": return traits.IsWeapon;
                case "melee": return traits.IsMelee;
                case "ranged": return traits.IsRanged;
                case "magic": return traits.IsMagic;
                case "summon": return traits.IsSummon;
                case "thrown": return traits.IsThrown;
                case "ammo": return traits.IsAmmo;
                case "tool": return traits.IsTool;
                case "armor": return traits.IsArmor;
                case "accessory": return traits.IsAccessory;
                case "consumable": return traits.IsConsumable;
                case "potion": return traits.IsPotion;
                case "placeable": return traits.IsPlaceable;
                case "material": return traits.IsMaterial;
                case "vanity": return traits.IsVanity;
                case "equipment": return traits.IsEquipment;
                case "fishing": return traits.IsFishing;
                case "stackable": return traits.IsStackable;
                case "unstackable": return !traits.IsStackable;
                case "favorite": return isFavorite;
                case "misc":
                    return !traits.IsWeapon &&
                           !traits.IsAmmo &&
                           !traits.IsTool &&
                           !traits.IsArmor &&
                           !traits.IsAccessory &&
                           !traits.IsConsumable &&
                           !traits.IsPlaceable &&
                           !traits.IsMaterial &&
                           !traits.IsVanity &&
                           !traits.IsEquipment &&
                           !traits.IsFishing;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Approximation of MagicStorage's default grouped ordering.
        /// </summary>
        public static int CompareMagicDefault(ItemSnapshot a, ItemSnapshot b)
        {
            var ta = ItemSearchTraitsBuilder.FromSnapshot(a);
            var tb = ItemSearchTraitsBuilder.FromSnapshot(b);

            int rankA = ItemSearchTraitsBuilder.GetMagicSortRank(ta);
            int rankB = ItemSearchTraitsBuilder.GetMagicSortRank(tb);
            if (rankA != rankB)
                return rankA.CompareTo(rankB);

            if (rankA <= 5 || rankA == 6)
            {
                int damageCmp = CompareDesc(a.Damage ?? 0, b.Damage ?? 0);
                if (damageCmp != 0) return damageCmp;
            }
            else if (rankA == 7)
            {
                int toolA = a.PickPower + a.AxePower + a.HammerPower;
                int toolB = b.PickPower + b.AxePower + b.HammerPower;
                int toolCmp = CompareDesc(toolA, toolB);
                if (toolCmp != 0) return toolCmp;
            }
            else if (rankA == 11)
            {
                int potA = a.HealLife + a.HealMana;
                int potB = b.HealLife + b.HealMana;
                int potCmp = CompareDesc(potA, potB);
                if (potCmp != 0) return potCmp;
            }

            int rarityCmp = CompareDesc(a.Rarity, b.Rarity);
            if (rarityCmp != 0) return rarityCmp;

            int stackCmp = CompareDesc(a.Stack, b.Stack);
            if (stackCmp != 0) return stackCmp;

            int nameCmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (nameCmp != 0) return nameCmp;

            return a.ItemId.CompareTo(b.ItemId);
        }

        private static string NormalizeTag(string rawTag)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
                return null;

            string lower = rawTag.Trim().ToLowerInvariant();
            if (TagAliases.TryGetValue(lower, out string normalized))
                return normalized;

            return lower;
        }

        private static int CompareDesc(int a, int b)
        {
            return b.CompareTo(a);
        }
    }
}
