using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.GameContent.Items;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace WhipStacking
{
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "whip-stacking";
        public string Name => "Whip Stacking";
        public string Version => "2.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static bool _patchesApplied;
        private static WhipStackingConfig _config;

        internal static bool Enabled = true;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;
            _config = context.GetConfig<WhipStackingConfig>();
            LoadConfig();

            _harmony = new Harmony("com.terrariamodder.whipstacking");
            _log.Info("Whip Stacking initialized; patches will apply when game content is ready");
        }

        private static void ApplyPatches()
        {
            if (_patchesApplied || _harmony == null) return;

            try
            {
                var updateEquips = typeof(Player).GetMethod("UpdateEquips",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                var postfix = typeof(TagPatches).GetMethod(nameof(TagPatches.UpdateEquips_Postfix),
                    BindingFlags.Public | BindingFlags.Static);

                if (updateEquips == null || postfix == null)
                {
                    _log.Error("Whip Stacking: Player.UpdateEquips native-stack adapter not found");
                    return;
                }

                _harmony.Patch(updateEquips, postfix: new HarmonyMethod(postfix));
                _patchesApplied = true;
                _log.Info($"Whip Stacking: Terraria native tag stack enabled ({TagEffectStack.MaxEffects} effects)");
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex}");
            }
        }


        private void LoadConfig()
        {
            Enabled = _config != null ? _config.Enabled : true;
            TagPatches.Enabled = Enabled;
        }

        public void OnConfigChanged()
        {
            LoadConfig();

            _log.Info($"Config reloaded - Enabled: {Enabled}");
        }

        public void OnContentReady(ModContext context)
        {
            ApplyPatches();
        }

        public void OnWorldLoad()
        {
            _log.Info("World loaded; native tag stack limit will be applied after equipment effects");
        }

        public void OnWorldUnload() { }

        public void Unload()
        {
            _harmony?.UnpatchAll("com.terrariamodder.whipstacking");
            _patchesApplied = false;
            _log.Info("Whip Stacking unloaded");
        }
    }
}
