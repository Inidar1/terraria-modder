using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace PetChests
{
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "pet-chests";
        public string Name => "Pet Chests";
        public string Version => "2.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static PetChestsConfig _config;
        private static bool _pendingHint;

        // Config
        internal static bool Enabled = true;
        internal static int InteractionRange = 200;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;
            _config = context.GetConfig<PetChestsConfig>();

            LoadConfig();

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("PetChests: dedicated server — skipping client init");
                return;
            }

            _log.Info("Pet Chests initializing...");

            try
            {
                _harmony = new Harmony("com.terrariamodder.petchests");

                // Apply on the game-thread content-ready lifecycle, once Terraria is initialized.
            }
            catch (Exception ex)
            {
                _log.Error($"Init failed: {ex.Message}");
            }
        }

        private static void ApplyPatches()
        {
            try
            {
                _log?.Info("Applying patches now...");

                // Patch Projectile.IsInteractable
                var isInteractableMethod = typeof(Projectile).GetMethod("IsInteractable",
                    BindingFlags.Public | BindingFlags.Instance);
                if (isInteractableMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod("IsInteractible_Postfix",
                        BindingFlags.Public | BindingFlags.Static);
                    _harmony.Patch(isInteractableMethod, postfix: new HarmonyMethod(postfix));
                    _log?.Info("Patched Projectile.IsInteractable");
                }

                // Patch Projectile.TryGetContainerIndex
                var tryGetContainerMethod = typeof(Projectile).GetMethod("TryGetContainerIndex",
                    BindingFlags.Public | BindingFlags.Instance);
                if (tryGetContainerMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod("TryGetContainerIndex_Prefix",
                        BindingFlags.Public | BindingFlags.Static);
                    _harmony.Patch(tryGetContainerMethod, prefix: new HarmonyMethod(prefix));
                    _log?.Info("Patched Projectile.TryGetContainerIndex");
                }

                // Patch Player.Update - use dynamic method lookup
                var updateMethod = typeof(Player).GetMethod("Update", new Type[] { typeof(int) });
                if (updateMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod("PlayerUpdate_Prefix",
                        BindingFlags.Public | BindingFlags.Static);
                    var postfix = typeof(Mod).GetMethod("PlayerUpdate_Postfix",
                        BindingFlags.Public | BindingFlags.Static);
                    _harmony.Patch(updateMethod,
                        prefix: new HarmonyMethod(prefix),
                        postfix: new HarmonyMethod(postfix));
                    _log?.Info("Patched Player.Update");
                }

                // Patch Player.HandleBeingInChestRange to skip tile-based chest checks when our pet piggy is open
                var handleChestMethod = typeof(Player).GetMethod("HandleBeingInChestRange",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (handleChestMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod("HandleChestRange_Prefix",
                        BindingFlags.Public | BindingFlags.Static);
                    _harmony.Patch(handleChestMethod, prefix: new HarmonyMethod(prefix));
                    _log?.Info("Patched Player.HandleBeingInChestRange");
                }
                else
                {
                    _log?.Info("Could not find HandleBeingInChestRange");
                }

                _log?.Info("All patches applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"Patch error: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            Enabled = _config.Enabled;
            InteractionRange = _config.InteractionRange;
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            if (!Enabled) PetInteraction.Reset(closeChest: true);
            _log.Info($"Config reloaded - Enabled: {Enabled}, Range: {InteractionRange}");
        }

        public void OnContentReady(ModContext context)
        {
            if (_harmony != null) ApplyPatches();
        }

        public void OnWorldLoad()
        {
            PetInteraction.Reset();
            _log.Info("World loaded - interaction state reset");

            // Schedule first-run hint if not yet shown
            if (_config != null && !_config.ShownHint)
            {
                _pendingHint = true;
            }
        }

        public void OnWorldUnload()
        {
            PetInteraction.Reset();
            _pendingHint = false;
        }

        public void Unload()
        {
            PetInteraction.Reset(closeChest: true);
            _harmony?.UnpatchAll("com.terrariamodder.petchests");
            _log.Info("Pet Chests unloaded");
        }

        internal static void Log(string message) => _log?.Info(message);

        #region Harmony Patches

        /// <summary>
        /// Make cosmetic pets interactible like Chester
        /// But NOT while piggy bank is already open or just closed
        /// </summary>
        public static void IsInteractible_Postfix(Projectile __instance, ref bool __result)
        {
            if (!Enabled) return;

            try
            {
                // If piggy bank is open via pet OR just closed, make pet NOT interactible
                // This prevents vanilla from repeatedly trying to interact with it
                // and prevents immediate reopen after closing
                if (PetHelper.IsCosmeticPet(__instance) && PetInteraction.ShouldBlockInteraction())
                {
                    __result = false;
                    return;
                }

                if (__result) return;

                if (PetHelper.IsCosmeticPet(__instance))
                {
                    __result = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// Return piggy bank container index (-2) for cosmetic pets
        /// </summary>
        public static bool TryGetContainerIndex_Prefix(Projectile __instance, ref int containerIndex, ref bool __result)
        {
            if (!Enabled) return true;

            try
            {
                if (PetHelper.IsCosmeticPet(__instance))
                {
                    containerIndex = -2; // Piggy bank
                    __result = true;
                    return false; // Skip original
                }
            }
            catch { }

            return true;
        }


        /// <summary>
        /// Prefix: Consume a close click on the bound pet before vanilla processes it
        /// </summary>
        public static void PlayerUpdate_Prefix(Player __instance, int i)
        {
            if (!Enabled) return;

            try
            {
                if (Main.gameMenu) return;
                if (i != Main.myPlayer) return;

                // Inventory right-clicks and item timers remain owned by vanilla.
                PetInteraction.BlockInputInPrefix(__instance);

            }
            catch { }
        }

        /// <summary>
        /// Postfix: Handle pet interactions
        /// </summary>
        public static void PlayerUpdate_Postfix(Player __instance, int i)
        {
            if (!Enabled) return;

            try
            {
                if (Main.gameMenu) return;
                if (i != Main.myPlayer) return;

                // Show first-run hint once the player is in-world
                if (_pendingHint)
                {
                    _pendingHint = false;
                    try
                    {
                        var newText = typeof(Main).GetMethod("NewText",
                            BindingFlags.Public | BindingFlags.Static, null,
                            new[] { typeof(string), typeof(byte), typeof(byte), typeof(byte) }, null);
                        newText?.Invoke(null, new object[] {
                            "[Pet Chests] Tip: Right-click your summoned pet to open piggy bank!",
                            (byte)180, (byte)220, (byte)255
                        });
                        if (_config != null)
                        {
                            _config.ShownHint = true;
                            _config.Save();
                        }
                        _log?.Info("First-run hint shown");
                    }
                    catch { }
                }

                // Set interactible flags for cosmetic pets
                PetInteraction.SetInteractableFlags(__instance);

                // Handle pet interaction
                PetInteraction.HandleInteraction(__instance);
            }
            catch (Exception ex)
            {
                Log($"Update error: {ex.Message}");
            }
        }

        /// <summary>Our bound local pet uses the configured range, checked in the update postfix.</summary>
        public static bool HandleChestRange_Prefix(Player __instance)
        {
            if (!Enabled) return true;

            // If we're keeping piggy open via pet, skip the entire method
            if (__instance.whoAmI == Main.myPlayer && __instance.chest == -2 && PetInteraction.IsKeepingPiggyOpen())
            {
                return false; // Skip original
            }

            return true;
        }

        #endregion
    }
}
