using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.GameContent.Items;
using Terraria.ID;
using TerrariaModder.Core.Logging;

namespace WhipStacking
{
    internal static class TagPatches
    {
        private static ILogger _log;
        internal static bool Enabled = true;

        // TagEffectState private fields (must stay reflection)
        private static FieldInfo _ownerField;
        private static FieldInfo _effectField;
        private static MethodInfo _typeSetter;

        // Reusable list for Update cleanup (avoids allocation per frame)
        private static readonly List<int> _toRemove = new List<int>();

        internal static bool Initialize(ILogger log)
        {
            _log = log;
            try
            {
                var tagType = typeof(TagEffectState);

                // Private fields on TagEffectState — must use reflection
                _ownerField = tagType.GetField("_owner", BindingFlags.NonPublic | BindingFlags.Instance);
                _effectField = tagType.GetField("_effect", BindingFlags.NonPublic | BindingFlags.Instance);
                _typeSetter = tagType.GetProperty("Type").GetSetMethod(true);

                // Verify critical fields
                if (_ownerField == null) { log.Error("_owner not found"); return false; }
                if (_effectField == null) { log.Error("_effect not found"); return false; }
                if (_typeSetter == null) { log.Error("Type setter not found"); return false; }

                log.Info($"Reflection initialized, maxNPCs={Main.maxNPCs}");
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"Reflection init failed: {ex.Message}");
                return false;
            }
        }

        // --- Helpers ---

        private static Player GetOwner(TagEffectState tagState)
        {
            return (Player)_ownerField.GetValue(tagState);
        }

        private static int GetPlayerIndex(TagEffectState tagState)
        {
            return GetOwner(tagState).whoAmI;
        }

        private static UniqueTagEffect GetEffect(int itemType)
        {
            return ItemID.Sets.UniqueTagEffects[itemType];
        }

        private static WhipTagEntry GetOrCreateEntry(int playerIdx, int whipType, UniqueTagEffect effect)
        {
            var dict = MultiTagState.GetOrCreate(playerIdx);
            if (!dict.TryGetValue(whipType, out var entry))
            {
                entry = new WhipTagEntry(whipType, effect, Main.maxNPCs);
                dict[whipType] = entry;
            }
            return entry;
        }

        /// <summary>
        /// Shared logic for setting active effect. Updates vanilla fields for compatibility,
        /// adds new whip to our dict without clearing existing whips.
        /// </summary>
        private static void SetActiveEffect(TagEffectState tagState, int type)
        {
            var effect = GetEffect(type);

            // Update vanilla _effect/Type for compatibility with unpatched code
            _effectField.SetValue(tagState, effect);
            _typeSetter.Invoke(tagState, new object[] { type });

            // Add to our dict if not already tracked
            int playerIdx = GetPlayerIndex(tagState);
            var dict = MultiTagState.GetOrCreate(playerIdx);
            if (!dict.ContainsKey(type))
            {
                dict[type] = new WhipTagEntry(type, effect, Main.maxNPCs);
                if (effect != null)
                    effect.OnSetToPlayer(GetOwner(tagState));
            }
        }

        // === HARMONY PREFIXES ===

        /// <summary>
        /// TrySetActiveEffect(int type) — Don't clear old tags or remove old player buff.
        /// Add new whip to multi-tag dict, update vanilla fields for compat.
        /// </summary>
        public static bool TrySetActiveEffect_Prefix(TagEffectState __instance, int type)
        {
            if (!Enabled) return true;
            try
            {
                SetActiveEffect(__instance, type);
            }
            catch (Exception ex)
            {
                _log?.Error($"TrySetActiveEffect error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// TryApplyTagToNPC(int itemType, NPC npc) — Tag NPC in the specific whip's entry.
        /// </summary>
        public static bool TryApplyTagToNPC_Prefix(TagEffectState __instance, int itemType, NPC npc)
        {
            if (!Enabled) return true;
            try
            {
                var effect = GetEffect(itemType);
                if (effect == null) return true;

                if (!effect.CanApplyTagToNPC(npc.type)) return false;

                SetActiveEffect(__instance, itemType);

                int playerIdx = GetPlayerIndex(__instance);
                var entry = GetOrCreateEntry(playerIdx, itemType, effect);
                entry.TimeLeftOnNPC[npc.whoAmI] = effect.TagDuration;

                effect.OnTagAppliedToNPC(GetOwner(__instance), npc);
            }
            catch (Exception ex)
            {
                _log?.Error($"TryApplyTagToNPC error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// ModifyHit — Apply tag damage from ALL active whips on this NPC.
        /// </summary>
        public static bool ModifyHit_Prefix(TagEffectState __instance, Projectile optionalProjectile, NPC npcHit,
            ref int damageDealt, ref bool crit)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);
                if (dict.Count == 0) return false;

                int npcIdx = npcHit.whoAmI;
                var owner = GetOwner(__instance);

                foreach (var entry in dict.Values)
                {
                    if (entry.TimeLeftOnNPC[npcIdx] <= 0 || entry.Effect == null) continue;

                    if (!entry.Effect.CanRunHitEffects(owner, optionalProjectile, npcHit)) continue;

                    entry.Effect.ModifyTaggedHit(owner, optionalProjectile, npcHit, ref damageDealt, ref crit);

                    if (entry.ProcTimeLeftOnNPC[npcIdx] > 0)
                    {
                        entry.Effect.ModifyProcHit(owner, optionalProjectile, npcHit, ref damageDealt, ref crit);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"ModifyHit error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// OnHit — Fire hit effects from ALL active whips on this NPC.
        /// </summary>
        public static bool OnHit_Prefix(TagEffectState __instance, Projectile optionalProjectile, NPC npcHit, int calcDamage)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);
                if (dict.Count == 0) return false;

                int npcIdx = npcHit.whoAmI;
                var owner = GetOwner(__instance);

                foreach (var entry in dict.Values)
                {
                    if (entry.TimeLeftOnNPC[npcIdx] <= 0 || entry.Effect == null) continue;

                    if (!entry.Effect.CanRunHitEffects(owner, optionalProjectile, npcHit)) continue;

                    entry.Effect.OnTaggedHit(owner, optionalProjectile, npcHit, calcDamage);

                    if (entry.ProcTimeLeftOnNPC[npcIdx] > 0)
                    {
                        entry.ProcTimeLeftOnNPC[npcIdx] = 0; // clear proc before firing (matches vanilla)
                        entry.Effect.OnProcHit(owner, optionalProjectile, npcHit, calcDamage);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"OnHit error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Update — Tick down ALL whip timers, remove expired entries.
        /// </summary>
        public static bool Update_Prefix(TagEffectState __instance)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);
                if (dict.Count == 0) return false;

                _toRemove.Clear();
                foreach (var kvp in dict)
                {
                    var entry = kvp.Value;
                    for (int i = 0; i < entry.TimeLeftOnNPC.Length; i++)
                    {
                        if (entry.TimeLeftOnNPC[i] > 0) entry.TimeLeftOnNPC[i]--;
                    }
                    for (int i = 0; i < entry.ProcTimeLeftOnNPC.Length; i++)
                    {
                        if (entry.ProcTimeLeftOnNPC[i] > 0) entry.ProcTimeLeftOnNPC[i]--;
                    }

                    if (!entry.HasAnyActiveTags())
                        _toRemove.Add(kvp.Key);
                }

                for (int i = 0; i < _toRemove.Count; i++)
                    dict.Remove(_toRemove[i]);
            }
            catch (Exception ex)
            {
                _log?.Error($"Update error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// IsNPCTagged — True if ANY whip has active tag on this NPC.
        /// </summary>
        public static bool IsNPCTagged_Prefix(TagEffectState __instance, int npcIndex, ref bool __result)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);

                __result = false;
                foreach (var entry in dict.Values)
                {
                    if (entry.TimeLeftOnNPC[npcIndex] > 0)
                    {
                        __result = true;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"IsNPCTagged error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// CanProcOnNPC — True if ANY whip has active proc on this NPC.
        /// </summary>
        public static bool CanProcOnNPC_Prefix(TagEffectState __instance, int npcIndex, ref bool __result)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);

                __result = false;
                foreach (var entry in dict.Values)
                {
                    if (entry.ProcTimeLeftOnNPC[npcIndex] > 0)
                    {
                        __result = true;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"CanProcOnNPC error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// TryEnableProcOnNPC — Enable proc on the matching whip's entry.
        /// </summary>
        public static bool TryEnableProcOnNPC_Prefix(TagEffectState __instance, int expectedActiveEffectType, NPC npc)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);

                if (dict.TryGetValue(expectedActiveEffectType, out var entry) && entry.Effect != null)
                {
                    entry.ProcTimeLeftOnNPC[npc.whoAmI] = entry.Effect.TagDuration;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"TryEnableProcOnNPC error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// ClearProcOnNPC — Clear proc for all whips on this NPC.
        /// </summary>
        public static bool ClearProcOnNPC_Prefix(TagEffectState __instance, int npcIndex)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);

                foreach (var entry in dict.Values)
                    entry.ProcTimeLeftOnNPC[npcIndex] = 0;
            }
            catch (Exception ex)
            {
                _log?.Error($"ClearProcOnNPC error: {ex.Message}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// ResetNPCSlotData — Clear all whip data for this NPC index.
        /// </summary>
        public static bool ResetNPCSlotData_Prefix(TagEffectState __instance, int npcIndex)
        {
            if (!Enabled) return true;
            try
            {
                int playerIdx = GetPlayerIndex(__instance);
                var dict = MultiTagState.GetOrCreate(playerIdx);

                foreach (var entry in dict.Values)
                {
                    entry.TimeLeftOnNPC[npcIndex] = 0;
                    entry.ProcTimeLeftOnNPC[npcIndex] = 0;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"ResetNPCSlotData error: {ex.Message}");
                return true;
            }
            return false;
        }
    }
}
