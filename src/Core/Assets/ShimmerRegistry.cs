using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.GameContent.Achievements;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Shimmer transform registry for custom items.
    /// Mods register transforms during initialization; types are resolved after
    /// custom item IDs have been assigned.
    /// </summary>
    public static class ShimmerRegistry
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static readonly Dictionary<int, ResolvedShimmer> _transforms
            = new Dictionary<int, ResolvedShimmer>();
        private static readonly List<ShimmerDefinition> _pending
            = new List<ShimmerDefinition>();

        private struct ResolvedShimmer
        {
            public int OutputType;
            public int InputStack;
            public int OutputStack;
        }

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.shimmer");
        }

        /// <summary>Register a shimmer transform during Initialize or OnContentReady.</summary>
        public static void Register(ShimmerDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.InputId) || string.IsNullOrEmpty(def.OutputId))
                return;
            _pending.Add(def);
        }

        /// <summary>Resolve registrations and patch Terraria's native world-item path.</summary>
        public static void ApplyPatches()
        {
            if (_applied) return;
            ResolvePending();

            if (_transforms.Count == 0)
            {
                _log?.Info("[ShimmerRegistry] No shimmer transforms registered");
                _applied = true;
                return;
            }

            try
            {
                PatchCanShimmer();
                PatchWorldItemGetShimmered();
                _applied = true;
                _log?.Info($"[ShimmerRegistry] Applied shimmer patches ({_transforms.Count} transform(s))");
            }
            catch (Exception ex)
            {
                _log?.Error($"[ShimmerRegistry] Failed to apply patches: {ex.Message}");
            }
        }

        private static void ResolvePending()
        {
            foreach (var def in _pending)
            {
                int inputType = ItemRegistry.ResolveItemType(def.InputId);
                int outputType = ItemRegistry.ResolveItemType(def.OutputId);

                if (inputType < 0)
                {
                    _log?.Warn($"[ShimmerRegistry] Cannot resolve input '{def.InputId}' - skipping");
                    continue;
                }
                if (outputType < 0)
                {
                    _log?.Warn($"[ShimmerRegistry] Cannot resolve output '{def.OutputId}' - skipping");
                    continue;
                }

                _transforms[inputType] = new ResolvedShimmer
                {
                    OutputType = outputType,
                    InputStack = Math.Max(1, def.InputStack),
                    OutputStack = Math.Max(1, def.OutputStack),
                };
                _log?.Debug($"[ShimmerRegistry] Registered: {def.InputId} (type {inputType}) -> {def.OutputId} (type {outputType})");
            }
            _pending.Clear();
        }

        public static bool TryGetTransform(int inputType, out int outputType)
        {
            if (_transforms.TryGetValue(inputType, out var resolved))
            {
                outputType = resolved.OutputType;
                return true;
            }
            outputType = -1;
            return false;
        }

        public static bool TryGetTransform(int inputType, out int outputType, out int inputStack, out int outputStack)
        {
            if (_transforms.TryGetValue(inputType, out var resolved))
            {
                outputType = resolved.OutputType;
                inputStack = resolved.InputStack;
                outputStack = resolved.OutputStack;
                return true;
            }
            outputType = -1;
            inputStack = 1;
            outputStack = 1;
            return false;
        }

        private static void PatchCanShimmer()
        {
            var method = typeof(Item).GetMethod("CanShimmer",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
                throw new MissingMethodException(typeof(Item).FullName, "CanShimmer");

            var prefix = typeof(ShimmerRegistry).GetMethod(nameof(CanShimmer_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            _log?.Debug("[ShimmerRegistry] Patched Item.CanShimmer");
        }

        private static void PatchWorldItemGetShimmered()
        {
            var method = typeof(WorldItem).GetMethod("GetShimmered",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
                throw new MissingMethodException(typeof(WorldItem).FullName, "GetShimmered");

            var prefix = typeof(ShimmerRegistry).GetMethod(nameof(WorldItemGetShimmered_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            _log?.Debug("[ShimmerRegistry] Patched WorldItem.GetShimmered");
        }

        private static bool CanShimmer_Prefix(Item __instance, ref bool __result)
        {
            if (_transforms.TryGetValue(__instance.type, out var resolved))
            {
                __result = __instance.stack >= resolved.InputStack;
                return false;
            }
            return true;
        }

        private static bool WorldItemGetShimmered_Prefix(WorldItem __instance)
        {
            if (!_transforms.TryGetValue(__instance.type, out var resolved))
                return true;

            int batches = __instance.stack / resolved.InputStack;
            if (batches <= 0)
                return false;

            int remainder = __instance.stack - batches * resolved.InputStack;
            long outputRemaining = (long)batches * resolved.OutputStack;

            var outputPrototype = new Item();
            outputPrototype.SetDefaults(resolved.OutputType);
            int outputMaxStack = Math.Max(1, outputPrototype.maxStack);
            int spawnNumber = 0;

            if (remainder == 0)
            {
                __instance.inner.SetDefaults(resolved.OutputType);
                int firstStack = (int)Math.Min(outputRemaining, outputMaxStack);
                __instance.stack = firstStack;
                outputRemaining -= firstStack;
                __instance.shimmered = true;
            }
            else
            {
                __instance.stack = remainder;
            }

            while (outputRemaining > 0)
            {
                int stack = (int)Math.Min(outputRemaining, outputMaxStack);
                __instance.SpawnShimmeredItem(spawnNumber++, resolved.OutputType, stack);
                outputRemaining -= stack;
            }

            __instance.shimmerTime = __instance.stack > 0 ? 1f : 0f;
            __instance.shimmerWet = true;
            __instance.wet = true;
            __instance.velocity *= 0.1f;
            WorldItem.ShimmerEffect(__instance.Center);
            __instance.SyncItem();
            AchievementsHelper.NotifyProgressionEvent(27);
            if (__instance.stack == 0)
                __instance.TurnToAir();

            return false;
        }

        public static void Clear()
        {
            _harmony?.UnpatchAll("com.terrariamodder.assets.shimmer");
            _transforms.Clear();
            _pending.Clear();
            _applied = false;
        }
    }
}
