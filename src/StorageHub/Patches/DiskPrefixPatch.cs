using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StorageHub.DedicatedBlocks;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.Patches
{
    /// <summary>
    /// Keeps storage disk UID usage in the prefix byte while suppressing vanilla affix behavior.
    /// </summary>
    internal static class DiskPrefixPatch
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static MethodBase _affixNameMethod;
        private static readonly List<MethodBase> _prefixMethods = new List<MethodBase>();
        private static Type _itemType;
        private static FieldInfo _itemTypeField;
        private static FieldInfo _itemPrefixField;

        public static void Initialize(ILogger log)
        {
            _log = log;
            if (_applied)
                return;

            try
            {
                _itemType = Type.GetType("Terraria.Item, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Item");
                if (_itemType == null)
                {
                    _log?.Warn("[DiskPrefixPatch] Terraria.Item type not found");
                    return;
                }

                _itemTypeField = _itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                _itemPrefixField = _itemType.GetField("prefix", BindingFlags.Public | BindingFlags.Instance);

                _affixNameMethod = _itemType.GetMethod(
                    "AffixName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);

                _prefixMethods.Clear();
                foreach (var method in _itemType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(method.Name, "Prefix", StringComparison.Ordinal))
                        continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(int))
                        _prefixMethods.Add(method);
                }

                if (_affixNameMethod == null && _prefixMethods.Count == 0)
                {
                    _log?.Warn("[DiskPrefixPatch] No AffixName/Prefix methods found to patch");
                    return;
                }

                var affixPrefix = new HarmonyMethod(typeof(DiskPrefixPatch).GetMethod(
                    nameof(AffixName_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static));
                var prefixPrefix = new HarmonyMethod(typeof(DiskPrefixPatch).GetMethod(
                    nameof(Prefix_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static));

                _harmony = new Harmony("com.storagehub.diskprefix");
                if (_affixNameMethod != null)
                    _harmony.Patch(_affixNameMethod, prefix: affixPrefix);

                for (int i = 0; i < _prefixMethods.Count; i++)
                    _harmony.Patch(_prefixMethods[i], prefix: prefixPrefix);

                _applied = true;
                _log?.Info($"[DiskPrefixPatch] Applied (AffixName={_affixNameMethod != null}, PrefixOverloads={_prefixMethods.Count})");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[DiskPrefixPatch] Failed to apply: {ex.Message}");
            }
        }

        public static void Unload()
        {
            try
            {
                if (_harmony != null)
                {
                    if (_affixNameMethod != null)
                        _harmony.Unpatch(_affixNameMethod, HarmonyPatchType.Prefix, _harmony.Id);

                    for (int i = 0; i < _prefixMethods.Count; i++)
                        _harmony.Unpatch(_prefixMethods[i], HarmonyPatchType.Prefix, _harmony.Id);
                }
            }
            catch
            {
                // Best-effort unpatch.
            }
            finally
            {
                _harmony = null;
                _applied = false;
                _affixNameMethod = null;
                _prefixMethods.Clear();
            }
        }

        private static bool AffixName_Prefix(object __instance, ref string __result)
        {
            if (!IsDiskItem(__instance))
                return true;

            int itemType = GetSafeInt(_itemTypeField, __instance, 0);
            if (itemType > 0)
            {
                var definition = ItemRegistry.GetDefinition(itemType);
                if (definition != null && !string.IsNullOrWhiteSpace(definition.DisplayName))
                {
                    __result = definition.DisplayName;
                    return false;
                }
            }

            // Fallback: allow vanilla if we couldn't resolve a custom display name.
            return true;
        }

        private static bool Prefix_Prefix(object __instance, int __0)
        {
            if (!IsDiskItem(__instance))
                return true;

            int prefixWeWant = __0;
            if (prefixWeWant == 0)
            {
                SetPrefix(__instance, 0);
            }
            else if (prefixWeWant > 0)
            {
                int clamped = Math.Min(byte.MaxValue, prefixWeWant);
                SetPrefix(__instance, clamped);
            }

            // Skip vanilla prefix roll/stat application for disk items.
            return false;
        }

        private static bool IsDiskItem(object item)
        {
            if (item == null || _itemTypeField == null)
                return false;

            int itemType = GetSafeInt(_itemTypeField, item, 0);
            if (itemType <= 0)
                return false;

            return DedicatedBlocksManager.TryGetDiskTierForItemType(itemType, out _);
        }

        private static void SetPrefix(object item, int prefix)
        {
            if (item == null || _itemPrefixField == null)
                return;

            try
            {
                int clamped = Math.Max(0, Math.Min(byte.MaxValue, prefix));
                if (_itemPrefixField.FieldType == typeof(byte))
                    _itemPrefixField.SetValue(item, (byte)clamped);
                else
                    _itemPrefixField.SetValue(item, clamped);
            }
            catch
            {
                // Best effort.
            }
        }

        private static int GetSafeInt(FieldInfo field, object instance, int defaultValue)
        {
            if (field == null || instance == null)
                return defaultValue;

            try
            {
                var value = field.GetValue(instance);
                if (value == null)
                    return defaultValue;
                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
