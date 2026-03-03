using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>
    /// Harmony prefix/postfix pairs for each patched method.
    ///
    /// Uses a hybrid approach with RecalculateGlobalFlags:
    /// - When a seed is fully ON or OFF, the global Main.* flag is set directly
    ///   and prefixes skip that flag (fast path — zero overhead).
    /// - When a seed is partially enabled (mixed), the global flag is set to the
    ///   world's original value and prefixes override per-method based on features.
    /// </summary>
    public static class SeedFeaturePatches
    {
        private static ILogger _log;
        private static FeatureManager _manager;
        private static Harmony _harmony;

        // Saved flag state per patched method (each method gets its own set to handle nesting)
        // Tracking booleans ensure postfix only restores if prefix actually overrode
        // NPC.SetDefaults — uses depth counter to handle recursive calls (SetDefaults can call itself)
        private static int _sd_depth;
        private static bool _sd_active, _sd_getGood, _sd_zenith, _sd_tenth;
        // NPC.AI
        private static bool _ai_active, _ai_getGood, _ai_notBees, _ai_noTraps;
        // NPC.ScaleStats_ByDifficulty_Tweaks
        private static bool _ss_active, _ss_getGood;
        // Spawner.GetSpawnRate
        private static bool _gsr_active, _gsr_getGood, _gsr_drunk, _gsr_remix;
        // Spawner.SpawnNPC
        private static bool _snpc_active, _snpc_getGood, _snpc_notBees, _snpc_remix, _snpc_drunk, _snpc_dontStarve, _snpc_tenth;
        // Player.UpdateBuffs
        private static bool _ub_active, _ub_dontStarve;
        // WorldGen.ShakeTree — getGoodWorld, tenthAnniversaryWorld
        private static bool _st_active, _st_getGood, _st_tenth;
        // Projectile.SetDefaults — getGoodWorld (uses depth counter for recursive calls)
        private static int _psd_depth;
        private static bool _psd_active, _psd_getGood;
        // Projectile.AI — getGoodWorld
        private static bool _pai_active, _pai_getGood;
        // Player.PickTile_DetermineDamage — getGoodWorld
        private static bool _pt_active, _pt_getGood;
        // Projectile.FishingCheck — remixWorld
        private static bool _fc_active, _fc_remix;

        public static void Apply(Harmony harmony, FeatureManager manager, ILogger log)
        {
            _harmony = harmony;
            _manager = manager;
            _log = log;

            var npcType = typeof(NPC);
            var playerType = typeof(Player);
            var projectileType = typeof(Projectile);
            var worldGenType = typeof(WorldGen);
            var spawnerType = npcType.GetNestedType("Spawner", BindingFlags.Public | BindingFlags.NonPublic);

            int patched = 0;

            // 1. NPC.SetDefaults(int, NPCSpawnParams)
            {
                var setDefaults = FindMethod(npcType, "SetDefaults", BindingFlags.Public | BindingFlags.Instance, "Int32");
                if (setDefaults != null)
                {
                    PatchMethod(setDefaults, nameof(SetDefaults_Prefix), nameof(SetDefaults_Postfix));
                    patched++;
                }
            }

            // 2. NPC.AI()
            {
                var ai = npcType.GetMethod("AI", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (ai != null)
                {
                    PatchMethod(ai, nameof(AI_Prefix), nameof(AI_Postfix));
                    patched++;
                }
            }

            // 3. NPC.ScaleStats_ByDifficulty_Tweaks()
            {
                var scaleTweaks = npcType.GetMethod("ScaleStats_ByDifficulty_Tweaks",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (scaleTweaks != null)
                {
                    PatchMethod(scaleTweaks, nameof(ScaleStatsTweaks_Prefix), nameof(ScaleStatsTweaks_Postfix));
                    patched++;
                }
            }

            // 4. Spawner.GetSpawnRate(Player, out int, out int)
            if (spawnerType != null)
            {
                var getSpawnRate = spawnerType.GetMethod("GetSpawnRate",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (getSpawnRate != null)
                {
                    PatchMethod(getSpawnRate, nameof(GetSpawnRate_Prefix), nameof(GetSpawnRate_Postfix));
                    patched++;
                }
            }

            // 5. Spawner.SpawnNPC() (instance, public)
            if (spawnerType != null)
            {
                var spawnNPC = spawnerType.GetMethod("SpawnNPC",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (spawnNPC != null)
                {
                    PatchMethod(spawnNPC, nameof(SpawnerSpawnNPC_Prefix), nameof(SpawnerSpawnNPC_Postfix));
                    patched++;
                }
            }

            // 6. Player.UpdateBuffs(int)
            {
                var updateBuffs = playerType.GetMethod("UpdateBuffs",
                    BindingFlags.Public | BindingFlags.Instance);
                if (updateBuffs != null)
                {
                    PatchMethod(updateBuffs, nameof(UpdateBuffs_Prefix), nameof(UpdateBuffs_Postfix));
                    patched++;
                }
            }

            // 7. WorldGen.ShakeTree (private static, no params)
            {
                var shakeTree = worldGenType.GetMethod("ShakeTree",
                    BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (shakeTree != null)
                {
                    PatchMethod(shakeTree, nameof(ShakeTree_Prefix), nameof(ShakeTree_Postfix));
                    patched++;
                }
            }

            // 8. Projectile.SetDefaults(int)
            {
                var projSetDefaults = FindMethod(projectileType, "SetDefaults", BindingFlags.Public | BindingFlags.Instance, "Int32");
                if (projSetDefaults != null)
                {
                    PatchMethod(projSetDefaults, nameof(ProjSetDefaults_Prefix), nameof(ProjSetDefaults_Postfix));
                    patched++;
                }
            }

            // 9. Projectile.AI()
            {
                var projAI = projectileType.GetMethod("AI",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (projAI != null)
                {
                    PatchMethod(projAI, nameof(ProjAI_Prefix), nameof(ProjAI_Postfix));
                    patched++;
                }
            }

            // 10. Player.PickTile_DetermineDamage
            {
                var pickTile = playerType.GetMethod("PickTile_DetermineDamage",
                    BindingFlags.Public | BindingFlags.Instance);
                if (pickTile != null)
                {
                    PatchMethod(pickTile, nameof(PickTile_Prefix), nameof(PickTile_Postfix));
                    patched++;
                }
            }

            // 11. Projectile.FishingCheck()
            {
                var fishingCheck = projectileType.GetMethod("FishingCheck",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (fishingCheck != null)
                {
                    PatchMethod(fishingCheck, nameof(FishingCheck_Prefix), nameof(FishingCheck_Postfix));
                    patched++;
                }
            }

            _log.Info($"[SeedLab] Applied {patched} Harmony patches");
        }

        private static MethodInfo FindMethod(Type type, string name, BindingFlags flags, string firstParamType)
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name) continue;
                var parms = method.GetParameters();
                if (parms.Length >= 1 && parms[0].ParameterType.Name == firstParamType)
                    return method;
            }
            return null;
        }

        private static void PatchMethod(MethodInfo target, string prefixName, string postfixName)
        {
            try
            {
                var prefix = typeof(SeedFeaturePatches).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
                var postfix = typeof(SeedFeaturePatches).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);

                _harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);

                _log.Debug($"[SeedLab] Patched {target.DeclaringType.Name}.{target.Name}");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to patch {target.DeclaringType.Name}.{target.Name}: {ex.Message}");
            }
        }

        #region NPC.SetDefaults — getGoodWorld, zenithWorld, tenthAnniversaryWorld
        // Uses depth counter: SetDefaults can call itself recursively (e.g. NPC transforms).
        // Only save/override on outermost call, only restore when depth returns to 0.

        private static void SetDefaults_Prefix()
        {
            _sd_depth++;
            if (_sd_depth > 1) return; // Recursive call — flags already overridden
            _sd_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.ZenithWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.TenthAnniversaryWorld))
                return;
            try
            {
                _sd_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _sd_zenith = FeatureManager.GetFlag(SeedFeatures.ZenithWorld);
                _sd_tenth = FeatureManager.GetFlag(SeedFeatures.TenthAnniversaryWorld);
                _sd_active = true;

                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_SetDefaults, SeedFeatures.GetGoodWorld));
                FeatureManager.SetFlag(SeedFeatures.ZenithWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_SetDefaults, SeedFeatures.ZenithWorld));
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_SetDefaults, SeedFeatures.TenthAnniversaryWorld));
            }
            catch { _sd_active = false; }
        }

        private static void SetDefaults_Postfix()
        {
            _sd_depth--;
            if (_sd_depth > 0) return; // Still inside recursive call
            if (!_sd_active) return;
            _sd_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _sd_getGood);
                FeatureManager.SetFlag(SeedFeatures.ZenithWorld, _sd_zenith);
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld, _sd_tenth);
            }
            catch { }
        }

        #endregion

        #region NPC.AI — getGoodWorld (boss AI checks), notTheBeesWorld (spider/slime behavior)

        private static void AI_Prefix()
        {
            _ai_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.NotTheBeesWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.NoTrapsWorld))
                return;
            try
            {
                _ai_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _ai_notBees = FeatureManager.GetFlag(SeedFeatures.NotTheBeesWorld);
                _ai_noTraps = FeatureManager.GetFlag(SeedFeatures.NoTrapsWorld);
                _ai_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_AI, SeedFeatures.GetGoodWorld));
                FeatureManager.SetFlag(SeedFeatures.NotTheBeesWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_AI, SeedFeatures.NotTheBeesWorld));
                FeatureManager.SetFlag(SeedFeatures.NoTrapsWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_AI, SeedFeatures.NoTrapsWorld));
            }
            catch { _ai_active = false; }
        }

        private static void AI_Postfix()
        {
            if (!_ai_active) return;
            _ai_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _ai_getGood);
                FeatureManager.SetFlag(SeedFeatures.NotTheBeesWorld, _ai_notBees);
                FeatureManager.SetFlag(SeedFeatures.NoTrapsWorld, _ai_noTraps);
            }
            catch { }
        }

        #endregion

        #region NPC.ScaleStats_ByDifficulty_Tweaks — getGoodWorld

        private static void ScaleStatsTweaks_Prefix()
        {
            _ss_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld))
                return;
            try
            {
                _ss_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _ss_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_NPC_ScaleStatsTweaks, SeedFeatures.GetGoodWorld));
            }
            catch { _ss_active = false; }
        }

        private static void ScaleStatsTweaks_Postfix()
        {
            if (!_ss_active) return;
            _ss_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _ss_getGood);
            }
            catch { }
        }

        #endregion

        #region Spawner.GetSpawnRate — getGoodWorld, drunkWorld, remixWorld

        private static void GetSpawnRate_Prefix()
        {
            _gsr_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.DrunkWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.RemixWorld))
                return;
            try
            {
                _gsr_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _gsr_drunk = FeatureManager.GetFlag(SeedFeatures.DrunkWorld);
                _gsr_remix = FeatureManager.GetFlag(SeedFeatures.RemixWorld);
                _gsr_active = true;

                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_GetSpawnRate, SeedFeatures.GetGoodWorld));
                FeatureManager.SetFlag(SeedFeatures.DrunkWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_GetSpawnRate, SeedFeatures.DrunkWorld));
                FeatureManager.SetFlag(SeedFeatures.RemixWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_GetSpawnRate, SeedFeatures.RemixWorld));
            }
            catch { _gsr_active = false; }
        }

        private static void GetSpawnRate_Postfix()
        {
            if (!_gsr_active) return;
            _gsr_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _gsr_getGood);
                FeatureManager.SetFlag(SeedFeatures.DrunkWorld, _gsr_drunk);
                FeatureManager.SetFlag(SeedFeatures.RemixWorld, _gsr_remix);
            }
            catch { }
        }

        #endregion

        #region Spawner.SpawnNPC — getGoodWorld, notTheBeesWorld, remixWorld, drunkWorld, dontStarveWorld, tenthAnniversaryWorld

        private static void SpawnerSpawnNPC_Prefix()
        {
            _snpc_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.NotTheBeesWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.RemixWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.DrunkWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.DontStarveWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.TenthAnniversaryWorld))
                return;
            try
            {
                _snpc_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _snpc_notBees = FeatureManager.GetFlag(SeedFeatures.NotTheBeesWorld);
                _snpc_remix = FeatureManager.GetFlag(SeedFeatures.RemixWorld);
                _snpc_drunk = FeatureManager.GetFlag(SeedFeatures.DrunkWorld);
                _snpc_dontStarve = FeatureManager.GetFlag(SeedFeatures.DontStarveWorld);
                _snpc_tenth = FeatureManager.GetFlag(SeedFeatures.TenthAnniversaryWorld);
                _snpc_active = true;

                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.GetGoodWorld));
                FeatureManager.SetFlag(SeedFeatures.NotTheBeesWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.NotTheBeesWorld));
                FeatureManager.SetFlag(SeedFeatures.RemixWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.RemixWorld));
                FeatureManager.SetFlag(SeedFeatures.DrunkWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.DrunkWorld));
                FeatureManager.SetFlag(SeedFeatures.DontStarveWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.DontStarveWorld));
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Spawner_SpawnNPC, SeedFeatures.TenthAnniversaryWorld));
            }
            catch { _snpc_active = false; }
        }

        private static void SpawnerSpawnNPC_Postfix()
        {
            if (!_snpc_active) return;
            _snpc_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _snpc_getGood);
                FeatureManager.SetFlag(SeedFeatures.NotTheBeesWorld, _snpc_notBees);
                FeatureManager.SetFlag(SeedFeatures.RemixWorld, _snpc_remix);
                FeatureManager.SetFlag(SeedFeatures.DrunkWorld, _snpc_drunk);
                FeatureManager.SetFlag(SeedFeatures.DontStarveWorld, _snpc_dontStarve);
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld, _snpc_tenth);
            }
            catch { }
        }

        #endregion

        #region Player.UpdateBuffs — dontStarveWorld

        private static void UpdateBuffs_Prefix()
        {
            _ub_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.DontStarveWorld))
                return;
            try
            {
                _ub_dontStarve = FeatureManager.GetFlag(SeedFeatures.DontStarveWorld);
                _ub_active = true;
                FeatureManager.SetFlag(SeedFeatures.DontStarveWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Player_UpdateBuffs, SeedFeatures.DontStarveWorld));
            }
            catch { _ub_active = false; }
        }

        private static void UpdateBuffs_Postfix()
        {
            if (!_ub_active) return;
            _ub_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.DontStarveWorld, _ub_dontStarve);
            }
            catch { }
        }

        #endregion

        #region WorldGen.ShakeTree — getGoodWorld, tenthAnniversaryWorld

        private static void ShakeTree_Prefix()
        {
            _st_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld) &&
                !_manager.NeedsMixedOverride(SeedFeatures.TenthAnniversaryWorld))
                return;
            try
            {
                _st_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _st_tenth = FeatureManager.GetFlag(SeedFeatures.TenthAnniversaryWorld);
                _st_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_WorldGen_ShakeTree, SeedFeatures.GetGoodWorld));
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_WorldGen_ShakeTree, SeedFeatures.TenthAnniversaryWorld));
            }
            catch { _st_active = false; }
        }

        private static void ShakeTree_Postfix()
        {
            if (!_st_active) return;
            _st_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _st_getGood);
                FeatureManager.SetFlag(SeedFeatures.TenthAnniversaryWorld, _st_tenth);
            }
            catch { }
        }

        #endregion

        #region Projectile.SetDefaults — getGoodWorld
        // Uses depth counter for recursive calls (SetDefaults may trigger nested calls)

        private static void ProjSetDefaults_Prefix()
        {
            _psd_depth++;
            if (_psd_depth > 1) return;
            _psd_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld))
                return;
            try
            {
                _psd_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _psd_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Projectile_SetDefaults, SeedFeatures.GetGoodWorld));
            }
            catch { _psd_active = false; }
        }

        private static void ProjSetDefaults_Postfix()
        {
            _psd_depth--;
            if (_psd_depth > 0) return;
            if (!_psd_active) return;
            _psd_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _psd_getGood);
            }
            catch { }
        }

        #endregion

        #region Projectile.AI — getGoodWorld

        private static void ProjAI_Prefix()
        {
            _pai_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld))
                return;
            try
            {
                _pai_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _pai_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Projectile_AI, SeedFeatures.GetGoodWorld));
            }
            catch { _pai_active = false; }
        }

        private static void ProjAI_Postfix()
        {
            if (!_pai_active) return;
            _pai_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _pai_getGood);
            }
            catch { }
        }

        #endregion

        #region Player.PickTile_DetermineDamage — getGoodWorld

        private static void PickTile_Prefix()
        {
            _pt_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.GetGoodWorld))
                return;
            try
            {
                _pt_getGood = FeatureManager.GetFlag(SeedFeatures.GetGoodWorld);
                _pt_active = true;
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Player_PickTile, SeedFeatures.GetGoodWorld));
            }
            catch { _pt_active = false; }
        }

        private static void PickTile_Postfix()
        {
            if (!_pt_active) return;
            _pt_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.GetGoodWorld, _pt_getGood);
            }
            catch { }
        }

        #endregion

        #region Projectile.FishingCheck — remixWorld

        private static void FishingCheck_Prefix()
        {
            _fc_active = false;
            if (_manager == null || !_manager.Initialized) return;
            if (!_manager.NeedsMixedOverride(SeedFeatures.RemixWorld))
                return;
            try
            {
                _fc_remix = FeatureManager.GetFlag(SeedFeatures.RemixWorld);
                _fc_active = true;
                FeatureManager.SetFlag(SeedFeatures.RemixWorld,
                    _manager.GetFlagForTarget(SeedFeatures.Target_Projectile_FishingCheck, SeedFeatures.RemixWorld));
            }
            catch { _fc_active = false; }
        }

        private static void FishingCheck_Postfix()
        {
            if (!_fc_active) return;
            _fc_active = false;
            try
            {
                FeatureManager.SetFlag(SeedFeatures.RemixWorld, _fc_remix);
            }
            catch { }
        }

        #endregion
    }
}
