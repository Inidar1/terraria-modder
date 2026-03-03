using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Terraria;
using Terraria.GameContent.Items;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace WhipStacking
{
    public class Mod : IMod
    {
        public string Id => "whip-stacking";
        public string Name => "Whip Stacking";
        public string Version => "1.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static Timer _patchTimer;

        internal static bool Enabled = true;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;
            LoadConfig();

            _harmony = new Harmony("com.terrariamodder.whipstacking");
            _patchTimer = new Timer(ApplyPatches, null, 5000, Timeout.Infinite);
            _log.Info("Whip Stacking initializing, patches in 5s...");
        }

        private static void ApplyPatches(object state)
        {
            try
            {
                if (!TagPatches.Initialize(_log))
                {
                    _log.Error("Reflection init failed, mod disabled");
                    return;
                }

                var tagType = typeof(TagEffectState);
                var npcType = typeof(NPC);
                var projType = typeof(Projectile);
                var patchType = typeof(TagPatches);

                int count = 0;
                count += Patch(tagType, "TrySetActiveEffect", new[] { typeof(int) },
                    patchType, nameof(TagPatches.TrySetActiveEffect_Prefix));
                count += Patch(tagType, "TryApplyTagToNPC", new[] { typeof(int), npcType },
                    patchType, nameof(TagPatches.TryApplyTagToNPC_Prefix));
                count += Patch(tagType, "ModifyHit",
                    new[] { projType, npcType, typeof(int).MakeByRefType(), typeof(bool).MakeByRefType() },
                    patchType, nameof(TagPatches.ModifyHit_Prefix));
                count += Patch(tagType, "OnHit", new[] { projType, npcType, typeof(int) },
                    patchType, nameof(TagPatches.OnHit_Prefix));
                count += Patch(tagType, "Update", Type.EmptyTypes,
                    patchType, nameof(TagPatches.Update_Prefix));
                count += Patch(tagType, "IsNPCTagged", new[] { typeof(int) },
                    patchType, nameof(TagPatches.IsNPCTagged_Prefix));
                count += Patch(tagType, "CanProcOnNPC", new[] { typeof(int) },
                    patchType, nameof(TagPatches.CanProcOnNPC_Prefix));
                count += Patch(tagType, "TryEnableProcOnNPC", new[] { typeof(int), npcType },
                    patchType, nameof(TagPatches.TryEnableProcOnNPC_Prefix));
                count += Patch(tagType, "ClearProcOnNPC", new[] { typeof(int) },
                    patchType, nameof(TagPatches.ClearProcOnNPC_Prefix));
                count += Patch(tagType, "ResetNPCSlotData", new[] { typeof(int) },
                    patchType, nameof(TagPatches.ResetNPCSlotData_Prefix));

                _log.Info($"Whip Stacking: {count}/10 patches applied");
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex}");
            }
        }

        private static int Patch(Type targetType, string methodName, Type[] paramTypes,
            Type patchType, string prefixName)
        {
            var original = targetType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, paramTypes, null);
            var prefix = patchType.GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);

            if (original == null)
            {
                _log.Error($"Target not found: {methodName}");
                return 0;
            }
            if (prefix == null)
            {
                _log.Error($"Prefix not found: {prefixName}");
                return 0;
            }

            _harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            _log.Debug($"  Patched {methodName}");
            return 1;
        }

        private void LoadConfig()
        {
            Enabled = _context.Config.Get("enabled", true);
            TagPatches.Enabled = Enabled;
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            MultiTagState.Reset(); // clear stale timers when toggling enabled/disabled
            _log.Info($"Config reloaded - Enabled: {Enabled}");
        }

        public void OnWorldLoad()
        {
            MultiTagState.Reset();
            _log.Info("World loaded, multi-tag state reset");
        }

        public void OnWorldUnload()
        {
            MultiTagState.Reset();
        }

        public void Unload()
        {
            _patchTimer?.Dispose();
            _harmony?.UnpatchAll("com.terrariamodder.whipstacking");
            MultiTagState.Reset();
            _patchTimer = null;
            _log.Info("Whip Stacking unloaded");
        }
    }
}
