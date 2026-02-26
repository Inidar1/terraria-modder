using System;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;

namespace StorageHub.Patches
{
    /// <summary>
    /// Enables shift-click quick-deposit from player inventory into Storage Hub
    /// while the Storage Hub UI is open.
    /// </summary>
    internal static class InventoryQuickDepositPatch
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static Func<bool> _isStorageHubOpen;
        private static Func<int, bool> _tryDepositInventorySlot;

        public static void Initialize(ILogger log)
        {
            _log = log;
            if (_applied) return;

            try
            {
                var terrariaAsm = Assembly.Load("Terraria");
                var itemSlotType = terrariaAsm.GetType("Terraria.UI.ItemSlot");
                if (itemSlotType == null)
                {
                    _log?.Warn("[InventoryQuickDepositPatch] Terraria.UI.ItemSlot type not found");
                    return;
                }

                MethodInfo leftClick = FindLeftClickMethod(itemSlotType);
                if (leftClick == null)
                {
                    _log?.Warn("[InventoryQuickDepositPatch] ItemSlot.LeftClick method not found");
                    return;
                }

                _harmony = new Harmony("com.storagehub.inventory.quickdeposit");
                _harmony.Patch(leftClick,
                    prefix: new HarmonyMethod(typeof(InventoryQuickDepositPatch), nameof(LeftClick_Prefix)));

                _applied = true;
                _log?.Info("[InventoryQuickDepositPatch] Applied shift-click quick-deposit patch");
            }
            catch (Exception ex)
            {
                _log?.Error($"[InventoryQuickDepositPatch] Failed to apply: {ex.Message}");
            }
        }

        public static void Unload()
        {
            try
            {
                _isStorageHubOpen = null;
                _tryDepositInventorySlot = null;

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
            }
        }

        public static void SetCallbacks(Func<bool> isStorageHubOpen, Func<int, bool> tryDepositInventorySlot)
        {
            _isStorageHubOpen = isStorageHubOpen;
            _tryDepositInventorySlot = tryDepositInventorySlot;
        }

        public static void ClearCallbacks()
        {
            _isStorageHubOpen = null;
            _tryDepositInventorySlot = null;
        }

        // Target: Terraria.UI.ItemSlot.LeftClick(Item[] inv, int context, int slot)
        private static bool LeftClick_Prefix(Array inv, int context, int slot)
        {
            try
            {
                if (_isStorageHubOpen == null || !_isStorageHubOpen.Invoke())
                    return true;

                if (!InputState.IsShiftDown())
                    return true;

                // Require an actual click edge so holding shift while hovering
                // does not repeatedly transfer items.
                if (!InputState.IsKeyJustPressed(KeyCode.MouseLeft))
                    return true;

                // Context 0 = main inventory/hotbar item slots.
                if (context != 0)
                    return true;

                if (slot < 0 || slot >= 50)
                    return true;

                if (_tryDepositInventorySlot == null)
                    return true;

                bool deposited = _tryDepositInventorySlot.Invoke(slot);
                if (deposited)
                    return false; // Suppress vanilla shift-click behavior when we handled it.
            }
            catch
            {
                // Fall through to vanilla behavior on any error.
            }

            return true;
        }

        private static MethodInfo FindLeftClickMethod(Type itemSlotType)
        {
            try
            {
                foreach (var method in itemSlotType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(method.Name, "LeftClick", StringComparison.Ordinal))
                        continue;

                    var p = method.GetParameters();
                    if (p.Length == 3 &&
                        p[0].ParameterType.IsArray &&
                        p[1].ParameterType == typeof(int) &&
                        p[2].ParameterType == typeof(int))
                    {
                        return method;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }
    }
}

