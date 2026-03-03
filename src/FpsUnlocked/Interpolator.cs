using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    /// <summary>
    /// Applies interpolated positions to all entities before Draw, and restores
    /// real positions after Draw. Uses flat keyframe arrays for speed.
    /// </summary>
    public static class Interpolator
    {
        // Saved "real" positions to restore after draw
        private static float[] _savedPlayer;
        private static float[] _savedNpc;
        private static float[] _savedProj;
        private static float[] _savedDust;
        private static float[] _savedGore;
        private static float[] _savedItem;
        private static float[] _savedCombatText;
        private static float[] _savedPopupText;
        private static float[] _savedProjTrail;

        // Per-entity interpolation deltas (interpolated - real position)
        private static float[] _playerDeltaX, _playerDeltaY;
        private static float[] _npcDeltaX, _npcDeltaY;
        private static float[] _projDeltaX, _projDeltaY;
        private static float[] _itemDeltaX, _itemDeltaY;

        // Saved oldPosition for Entity-based types (2 floats per entity: X, Y)
        private static float[] _savedPlayerOldPos;
        private static float[] _savedNpcOldPos;
        private static float[] _savedProjOldPos;
        private static float[] _savedItemOldPos;

        // Saved player shadowPos (3 entries * 2 floats = 6 per player)
        private static float[] _savedPlayerShadowPos;

        // Saved dust positions for customData-linked offset
        private static float[] _savedDustCustom;
        private static bool[] _dustCustomApplied;

        // Guard: only restore if apply succeeded this frame
        private static bool _applied;

        // Logger (for pylon-fix diagnostics)
        private static ILogger _log;

        // Tracks what position was written during Apply — used in RestorePlayers to detect
        // teleports that happen during DoDraw (e.g., pylon confirms via UI inside DoDraw)
        private static float[] _writtenPlayerX;
        private static float[] _writtenPlayerY;

        private const float PI = 3.14159265f;
        private const float TWO_PI = 6.2831853f;

        // Teleport detection: skip interpolation when keyframes are too far apart.
        // Minions (Imp, Stardust Dragon, etc.) teleport to follow the player,
        // which would otherwise lerp across the entire map and crash XNA rendering.
        // 256px = 16 tiles — generous for fast projectiles, catches teleports.
        private const float TELEPORT_DIST_SQ = 256f * 256f; // 65536

        public static void Initialize(ILogger log = null)
        {
            _log = log;
            int maxP = ReflectionCache.MaxPlayers;
            int maxN = ReflectionCache.MaxNpcs;
            int maxPr = ReflectionCache.MaxProjectiles;
            int maxD = ReflectionCache.MaxDust;
            int maxG = ReflectionCache.MaxGore;
            int maxI = ReflectionCache.MaxItems;
            int maxCt = ReflectionCache.MaxCombatText;
            int maxPt = ReflectionCache.MaxPopupText;

            _savedPlayer = new float[maxP * KeyframeStore.PlayerStride];
            _savedNpc = new float[maxN * KeyframeStore.NpcStride];
            _savedProj = new float[maxPr * KeyframeStore.ProjStride];
            _savedDust = new float[maxD * KeyframeStore.DustStride];
            _savedGore = new float[maxG * KeyframeStore.GoreStride];
            _savedItem = new float[maxI * KeyframeStore.ItemStride];
            _savedCombatText = new float[maxCt * KeyframeStore.CombatTextStride];
            _savedPopupText = new float[maxPt * KeyframeStore.PopupTextStride];
            _savedProjTrail = new float[maxPr * KeyframeStore.ProjTrailStride];

            // Delta arrays (interpolated - real position)
            _playerDeltaX = new float[maxP];
            _playerDeltaY = new float[maxP];
            _npcDeltaX = new float[maxN];
            _npcDeltaY = new float[maxN];
            _projDeltaX = new float[maxPr];
            _projDeltaY = new float[maxPr];
            _itemDeltaX = new float[maxI];
            _itemDeltaY = new float[maxI];

            // Saved oldPosition (2 floats per entity)
            _savedPlayerOldPos = new float[maxP * 2];
            _savedNpcOldPos = new float[maxN * 2];
            _savedProjOldPos = new float[maxPr * 2];
            _savedItemOldPos = new float[maxI * 2];

            // Saved player shadowPos (3 entries * 2 floats each)
            _savedPlayerShadowPos = new float[maxP * 6];

            // Written position tracking (for pylon-fix in RestorePlayers)
            _writtenPlayerX = new float[maxP];
            _writtenPlayerY = new float[maxP];

            // Dust customData offset
            _savedDustCustom = new float[maxD * 2];
            _dustCustomApplied = new bool[maxD];
        }

        /// <summary>
        /// Apply interpolated positions to all entities. Called in DoDraw prefix.
        /// Saves current (real) positions first so they can be restored after draw.
        /// </summary>
        public static void ApplyAll()
        {
            _applied = false;
            float t = FrameState.PartialTick;

            ApplyPlayers(t);
            ApplyNpcs(t);
            ApplyProjectiles(t);
            // Dust interpolation disabled — slot reuse causes flickering (torch, molten armor, etc.)
            ApplyGore(t);
            ApplyItems(t);
            ApplyCombatText(t);
            ApplyPopupText(t);

            // Offset dust particles linked to interpolated entities via customData
            ApplyDustEntityOffset();

            _applied = true;
        }

        /// <summary>
        /// Restore all entities to their real (non-interpolated) positions.
        /// Called in DoDraw postfix. Only restores if ApplyAll completed successfully.
        /// </summary>
        public static void RestoreAll()
        {
            if (!_applied) return;
            _applied = false;

            // Restore dust customData offset first (before entity positions change back)
            RestoreDustEntityOffset();

            RestorePlayers();
            RestoreNpcs();
            RestoreProjectiles();
            // Dust interpolation disabled — matches ApplyAll
            RestoreGore();
            RestoreItems();
            RestoreCombatText();
            RestorePopupText();
        }

        #region Players

        private static void ApplyPlayers(float t)
        {
            var players = Main.player;
            if (players == null) return;
            int count = Math.Min(players.Length, ReflectionCache.MaxPlayers);

            for (int i = 0; i < count; i++)
            {
                var p = players[i];
                if (p == null) { _playerDeltaX[i] = 0f; _playerDeltaY[i] = 0f; continue; }
                if (KeyframeStore.PlayerSkip[i]) { _playerDeltaX[i] = 0f; _playerDeltaY[i] = 0f; continue; }

                int offset = i * KeyframeStore.PlayerStride;

                // Save current (real) position
                float realX = ReflectionCache.PlayerPosX(p);
                float realY = ReflectionCache.PlayerPosY(p);
                _savedPlayer[offset + 0] = realX;
                _savedPlayer[offset + 1] = realY;
                _savedPlayer[offset + 2] = ReflectionCache.PlayerGfxOffY(p);
                _savedPlayer[offset + 3] = ReflectionCache.PlayerHeadRot(p);
                _savedPlayer[offset + 4] = ReflectionCache.PlayerBodyRot(p);
                _savedPlayer[offset + 5] = ReflectionCache.PlayerLegRot(p);
                _savedPlayer[offset + 6] = ReflectionCache.PlayerItemLocX(p);
                _savedPlayer[offset + 7] = ReflectionCache.PlayerItemLocY(p);
                _savedPlayer[offset + 8] = ReflectionCache.PlayerItemRot(p);

                // Compute interpolated position
                float interpX = Lerp(KeyframeStore.PlayerBegin[offset + 0], KeyframeStore.PlayerEnd[offset + 0], t);
                float interpY = Lerp(KeyframeStore.PlayerBegin[offset + 1], KeyframeStore.PlayerEnd[offset + 1], t);

                // Apply interpolated values
                ReflectionCache.SetPlayerPosX(p, interpX);
                ReflectionCache.SetPlayerPosY(p, interpY);
                _writtenPlayerX[i] = interpX;
                _writtenPlayerY[i] = interpY;
                ReflectionCache.SetPlayerGfxOffY(p, Lerp(KeyframeStore.PlayerBegin[offset + 2], KeyframeStore.PlayerEnd[offset + 2], t));
                ReflectionCache.SetPlayerHeadRot(p, AngleLerp(KeyframeStore.PlayerBegin[offset + 3], KeyframeStore.PlayerEnd[offset + 3], t));
                ReflectionCache.SetPlayerBodyRot(p, AngleLerp(KeyframeStore.PlayerBegin[offset + 4], KeyframeStore.PlayerEnd[offset + 4], t));
                ReflectionCache.SetPlayerLegRot(p, AngleLerp(KeyframeStore.PlayerBegin[offset + 5], KeyframeStore.PlayerEnd[offset + 5], t));
                ReflectionCache.SetPlayerItemLocX(p, Lerp(KeyframeStore.PlayerBegin[offset + 6], KeyframeStore.PlayerEnd[offset + 6], t));
                ReflectionCache.SetPlayerItemLocY(p, Lerp(KeyframeStore.PlayerBegin[offset + 7], KeyframeStore.PlayerEnd[offset + 7], t));
                ReflectionCache.SetPlayerItemRot(p, AngleLerp(KeyframeStore.PlayerBegin[offset + 8], KeyframeStore.PlayerEnd[offset + 8], t));

                // Compute interpolation delta
                float dx = interpX - realX;
                float dy = interpY - realY;
                _playerDeltaX[i] = dx;
                _playerDeltaY[i] = dy;

                // Offset shadowPos (afterimage trail) by interpolation delta
                var shadowArr = p.shadowPos;
                if (shadowArr != null)
                {
                    int shadowCount = Math.Min(3, shadowArr.Length);
                    for (int j = 0; j < shadowCount; j++)
                    {
                        int sIdx = i * 6 + j * 2;
                        float sx = shadowArr[j].X;
                        float sy = shadowArr[j].Y;
                        _savedPlayerShadowPos[sIdx + 0] = sx;
                        _savedPlayerShadowPos[sIdx + 1] = sy;
                        shadowArr[j].X = sx + dx;
                        shadowArr[j].Y = sy + dy;
                    }
                }

                // Offset oldPosition by interpolation delta
                _savedPlayerOldPos[i * 2 + 0] = ReflectionCache.PlayerOldPosX(p);
                _savedPlayerOldPos[i * 2 + 1] = ReflectionCache.PlayerOldPosY(p);
                ReflectionCache.SetPlayerOldPosX(p, _savedPlayerOldPos[i * 2 + 0] + dx);
                ReflectionCache.SetPlayerOldPosY(p, _savedPlayerOldPos[i * 2 + 1] + dy);
            }
        }

        private static void RestorePlayers()
        {
            var players = Main.player;
            if (players == null) return;
            int count = Math.Min(players.Length, ReflectionCache.MaxPlayers);

            for (int i = 0; i < count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                if (KeyframeStore.PlayerSkip[i]) continue;

                int offset = i * KeyframeStore.PlayerStride;

                // Pylon-fix: if position changed during DoDraw (e.g. pylon confirm fires
                // inside DoDraw UI), keep the new position rather than restoring the old one.
                float currentX = ReflectionCache.PlayerPosX(p);
                float currentY = ReflectionCache.PlayerPosY(p);
                if (Math.Abs(currentX - _writtenPlayerX[i]) > 1f ||
                    Math.Abs(currentY - _writtenPlayerY[i]) > 1f)
                {
                    _log?.Info($"[pylon-fix] Teleport during DoDraw preserved! " +
                        $"saved=({_savedPlayer[offset]:F1},{_savedPlayer[offset + 1]:F1}), " +
                        $"written=({_writtenPlayerX[i]:F1},{_writtenPlayerY[i]:F1}), " +
                        $"current=({currentX:F1},{currentY:F1})");
                    continue;
                }

                ReflectionCache.SetPlayerPosX(p, _savedPlayer[offset + 0]);
                ReflectionCache.SetPlayerPosY(p, _savedPlayer[offset + 1]);
                ReflectionCache.SetPlayerGfxOffY(p, _savedPlayer[offset + 2]);
                ReflectionCache.SetPlayerHeadRot(p, _savedPlayer[offset + 3]);
                ReflectionCache.SetPlayerBodyRot(p, _savedPlayer[offset + 4]);
                ReflectionCache.SetPlayerLegRot(p, _savedPlayer[offset + 5]);
                ReflectionCache.SetPlayerItemLocX(p, _savedPlayer[offset + 6]);
                ReflectionCache.SetPlayerItemLocY(p, _savedPlayer[offset + 7]);
                ReflectionCache.SetPlayerItemRot(p, _savedPlayer[offset + 8]);

                // Restore shadowPos
                var shadowArr = p.shadowPos;
                if (shadowArr != null)
                {
                    int shadowCount = Math.Min(3, shadowArr.Length);
                    for (int j = 0; j < shadowCount; j++)
                    {
                        int sIdx = i * 6 + j * 2;
                        shadowArr[j].X = _savedPlayerShadowPos[sIdx + 0];
                        shadowArr[j].Y = _savedPlayerShadowPos[sIdx + 1];
                    }
                }

                // Restore oldPosition
                ReflectionCache.SetPlayerOldPosX(p, _savedPlayerOldPos[i * 2 + 0]);
                ReflectionCache.SetPlayerOldPosY(p, _savedPlayerOldPos[i * 2 + 1]);
            }
        }

        #endregion

        #region NPCs

        private static void ApplyNpcs(float t)
        {
            var npcs = Main.npc;
            if (npcs == null) return;
            int count = Math.Min(npcs.Length, ReflectionCache.MaxNpcs);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.NpcActiveEnd[i] || KeyframeStore.NpcSkip[i] || !KeyframeStore.NpcActiveBegin[i])
                {
                    _npcDeltaX[i] = 0f; _npcDeltaY[i] = 0f; continue;
                }

                var npc = npcs[i];
                if (npc == null) { _npcDeltaX[i] = 0f; _npcDeltaY[i] = 0f; continue; }

                int offset = i * KeyframeStore.NpcStride;

                // Teleport detection: skip interpolation if NPC moved too far between keyframes
                // (e.g., Chaos Elemental teleport, NPC despawn/respawn at different location)
                {
                    float kfDx = KeyframeStore.NpcEnd[offset + 0] - KeyframeStore.NpcBegin[offset + 0];
                    float kfDy = KeyframeStore.NpcEnd[offset + 1] - KeyframeStore.NpcBegin[offset + 1];
                    if (kfDx * kfDx + kfDy * kfDy > TELEPORT_DIST_SQ)
                    {
                        _npcDeltaX[i] = 0f; _npcDeltaY[i] = 0f; continue;
                    }
                }

                // Save current (real) position
                float realX = ReflectionCache.NpcPosX(npc);
                float realY = ReflectionCache.NpcPosY(npc);
                _savedNpc[offset + 0] = realX;
                _savedNpc[offset + 1] = realY;
                _savedNpc[offset + 2] = ReflectionCache.NpcRotation(npc);
                _savedNpc[offset + 3] = ReflectionCache.NpcGfxOffY(npc);

                // Compute interpolated position
                float interpX = Lerp(KeyframeStore.NpcBegin[offset + 0], KeyframeStore.NpcEnd[offset + 0], t);
                float interpY = Lerp(KeyframeStore.NpcBegin[offset + 1], KeyframeStore.NpcEnd[offset + 1], t);

                // Apply interpolated values
                ReflectionCache.SetNpcPosX(npc, interpX);
                ReflectionCache.SetNpcPosY(npc, interpY);
                ReflectionCache.SetNpcRotation(npc, AngleLerp(KeyframeStore.NpcBegin[offset + 2], KeyframeStore.NpcEnd[offset + 2], t));
                ReflectionCache.SetNpcGfxOffY(npc, Lerp(KeyframeStore.NpcBegin[offset + 3], KeyframeStore.NpcEnd[offset + 3], t));

                // Compute and store delta
                float dx = interpX - realX;
                float dy = interpY - realY;
                _npcDeltaX[i] = dx;
                _npcDeltaY[i] = dy;

                // Offset oldPosition
                _savedNpcOldPos[i * 2 + 0] = ReflectionCache.NpcOldPosX(npc);
                _savedNpcOldPos[i * 2 + 1] = ReflectionCache.NpcOldPosY(npc);
                ReflectionCache.SetNpcOldPosX(npc, _savedNpcOldPos[i * 2 + 0] + dx);
                ReflectionCache.SetNpcOldPosY(npc, _savedNpcOldPos[i * 2 + 1] + dy);
            }
        }

        private static void RestoreNpcs()
        {
            var npcs = Main.npc;
            if (npcs == null) return;
            int count = Math.Min(npcs.Length, ReflectionCache.MaxNpcs);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.NpcActiveEnd[i]) continue;
                if (KeyframeStore.NpcSkip[i]) continue;
                if (!KeyframeStore.NpcActiveBegin[i]) continue;

                var npc = npcs[i];
                if (npc == null) continue;

                int offset = i * KeyframeStore.NpcStride;
                ReflectionCache.SetNpcPosX(npc, _savedNpc[offset + 0]);
                ReflectionCache.SetNpcPosY(npc, _savedNpc[offset + 1]);
                ReflectionCache.SetNpcRotation(npc, _savedNpc[offset + 2]);
                ReflectionCache.SetNpcGfxOffY(npc, _savedNpc[offset + 3]);

                // Restore oldPosition
                ReflectionCache.SetNpcOldPosX(npc, _savedNpcOldPos[i * 2 + 0]);
                ReflectionCache.SetNpcOldPosY(npc, _savedNpcOldPos[i * 2 + 1]);
            }
        }

        #endregion

        #region Projectiles

        private static void ApplyProjectiles(float t)
        {
            var projs = Main.projectile;
            if (projs == null) return;
            int count = Math.Min(projs.Length, ReflectionCache.MaxProjectiles);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.ProjActiveEnd[i] || KeyframeStore.ProjSkip[i] || !KeyframeStore.ProjActiveBegin[i])
                {
                    _projDeltaX[i] = 0f; _projDeltaY[i] = 0f; continue;
                }

                var proj = projs[i];
                if (proj == null) { _projDeltaX[i] = 0f; _projDeltaY[i] = 0f; continue; }

                int offset = i * KeyframeStore.ProjStride;

                // Teleport detection: skip interpolation if projectile moved too far between keyframes
                // (e.g., summoner minions teleporting to follow player, slot reuse)
                {
                    float kfDx = KeyframeStore.ProjEnd[offset + 0] - KeyframeStore.ProjBegin[offset + 0];
                    float kfDy = KeyframeStore.ProjEnd[offset + 1] - KeyframeStore.ProjBegin[offset + 1];
                    if (kfDx * kfDx + kfDy * kfDy > TELEPORT_DIST_SQ)
                    {
                        _projDeltaX[i] = 0f; _projDeltaY[i] = 0f; continue;
                    }
                }

                // Save current (real) position
                float realX = ReflectionCache.ProjPosX(proj);
                float realY = ReflectionCache.ProjPosY(proj);
                _savedProj[offset + 0] = realX;
                _savedProj[offset + 1] = realY;
                _savedProj[offset + 2] = ReflectionCache.ProjRotation(proj);

                // Compute interpolated position
                float interpX = Lerp(KeyframeStore.ProjBegin[offset + 0], KeyframeStore.ProjEnd[offset + 0], t);
                float interpY = Lerp(KeyframeStore.ProjBegin[offset + 1], KeyframeStore.ProjEnd[offset + 1], t);

                // Apply interpolated values
                ReflectionCache.SetProjPosX(proj, interpX);
                ReflectionCache.SetProjPosY(proj, interpY);
                ReflectionCache.SetProjRotation(proj, AngleLerp(KeyframeStore.ProjBegin[offset + 2], KeyframeStore.ProjEnd[offset + 2], t));

                // Trail interpolation disabled — slot reuse causes visual artifacts
                // ApplyProjectileTrail(proj, i, t);

                // Compute and store delta
                float dx = interpX - realX;
                float dy = interpY - realY;
                _projDeltaX[i] = dx;
                _projDeltaY[i] = dy;

                // Offset oldPosition
                _savedProjOldPos[i * 2 + 0] = ReflectionCache.ProjOldPosX(proj);
                _savedProjOldPos[i * 2 + 1] = ReflectionCache.ProjOldPosY(proj);
                ReflectionCache.SetProjOldPosX(proj, _savedProjOldPos[i * 2 + 0] + dx);
                ReflectionCache.SetProjOldPosY(proj, _savedProjOldPos[i * 2 + 1] + dy);
            }
        }

        private static void ApplyProjectileTrail(Projectile proj, int index, float t)
        {
            var oldPosArr = proj.oldPos;
            var oldRotArr = proj.oldRot;
            if (oldPosArr == null || oldRotArr == null) return;

            int baseOffset = index * KeyframeStore.ProjTrailStride;
            int trailCount = Math.Min(KeyframeStore.TrailLength, oldPosArr.Length);

            // Save current trail
            for (int t2 = 0; t2 < trailCount; t2++)
            {
                int tOff = baseOffset + t2 * 3;
                _savedProjTrail[tOff + 0] = oldPosArr[t2].X;
                _savedProjTrail[tOff + 1] = oldPosArr[t2].Y;
                _savedProjTrail[tOff + 2] = oldRotArr[t2];
            }

            // Apply interpolated trail
            for (int t2 = 0; t2 < trailCount; t2++)
            {
                int tOff = baseOffset + t2 * 3;
                float ix = Lerp(KeyframeStore.ProjTrailBegin[tOff + 0], KeyframeStore.ProjTrailEnd[tOff + 0], t);
                float iy = Lerp(KeyframeStore.ProjTrailBegin[tOff + 1], KeyframeStore.ProjTrailEnd[tOff + 1], t);
                float ir = AngleLerp(KeyframeStore.ProjTrailBegin[tOff + 2], KeyframeStore.ProjTrailEnd[tOff + 2], t);

                oldPosArr[t2] = new Vector2(ix, iy);
                oldRotArr[t2] = ir;
            }
        }

        private static void RestoreProjectiles()
        {
            var projs = Main.projectile;
            if (projs == null) return;
            int count = Math.Min(projs.Length, ReflectionCache.MaxProjectiles);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.ProjActiveEnd[i]) continue;
                if (KeyframeStore.ProjSkip[i]) continue;
                if (!KeyframeStore.ProjActiveBegin[i]) continue;

                var proj = projs[i];
                if (proj == null) continue;

                int offset = i * KeyframeStore.ProjStride;
                ReflectionCache.SetProjPosX(proj, _savedProj[offset + 0]);
                ReflectionCache.SetProjPosY(proj, _savedProj[offset + 1]);
                ReflectionCache.SetProjRotation(proj, _savedProj[offset + 2]);

                // Trail restore disabled — matches ApplyProjectiles
                // RestoreProjectileTrail(proj, i);

                // Restore oldPosition
                ReflectionCache.SetProjOldPosX(proj, _savedProjOldPos[i * 2 + 0]);
                ReflectionCache.SetProjOldPosY(proj, _savedProjOldPos[i * 2 + 1]);
            }
        }

        private static void RestoreProjectileTrail(Projectile proj, int index)
        {
            var oldPosArr = proj.oldPos;
            var oldRotArr = proj.oldRot;
            if (oldPosArr == null || oldRotArr == null) return;

            int baseOffset = index * KeyframeStore.ProjTrailStride;
            int trailCount = Math.Min(KeyframeStore.TrailLength, oldPosArr.Length);

            for (int t = 0; t < trailCount; t++)
            {
                int tOff = baseOffset + t * 3;
                oldPosArr[t] = new Vector2(_savedProjTrail[tOff + 0], _savedProjTrail[tOff + 1]);
                oldRotArr[t] = _savedProjTrail[tOff + 2];
            }
        }

        #endregion

        #region Dust

        private static void ApplyDust(float t)
        {
            var dust = Main.dust;
            if (dust == null) return;
            int count = Math.Min(dust.Length, ReflectionCache.MaxDust);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.DustActiveEnd[i]) continue;
                if (!KeyframeStore.DustActiveBegin[i]) continue;

                var d = dust[i];
                if (d == null) continue;

                int offset = i * KeyframeStore.DustStride;

                _savedDust[offset + 0] = ReflectionCache.DustPosX(d);
                _savedDust[offset + 1] = ReflectionCache.DustPosY(d);
                _savedDust[offset + 2] = ReflectionCache.DustRotation(d);

                ReflectionCache.SetDustPosX(d, Lerp(KeyframeStore.DustBegin[offset + 0], KeyframeStore.DustEnd[offset + 0], t));
                ReflectionCache.SetDustPosY(d, Lerp(KeyframeStore.DustBegin[offset + 1], KeyframeStore.DustEnd[offset + 1], t));
                ReflectionCache.SetDustRotation(d, AngleLerp(KeyframeStore.DustBegin[offset + 2], KeyframeStore.DustEnd[offset + 2], t));
            }
        }

        private static void RestoreDust()
        {
            var dust = Main.dust;
            if (dust == null) return;
            int count = Math.Min(dust.Length, ReflectionCache.MaxDust);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.DustActiveEnd[i]) continue;
                if (!KeyframeStore.DustActiveBegin[i]) continue;

                var d = dust[i];
                if (d == null) continue;

                int offset = i * KeyframeStore.DustStride;
                ReflectionCache.SetDustPosX(d, _savedDust[offset + 0]);
                ReflectionCache.SetDustPosY(d, _savedDust[offset + 1]);
                ReflectionCache.SetDustRotation(d, _savedDust[offset + 2]);
            }
        }

        #endregion

        #region Gore

        private static void ApplyGore(float t)
        {
            var gore = Main.gore;
            if (gore == null) return;
            int count = Math.Min(gore.Length, ReflectionCache.MaxGore);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.GoreActiveEnd[i]) continue;
                if (!KeyframeStore.GoreActiveBegin[i]) continue;

                var g = gore[i];
                if (g == null) continue;

                int offset = i * KeyframeStore.GoreStride;

                _savedGore[offset + 0] = ReflectionCache.GorePosX(g);
                _savedGore[offset + 1] = ReflectionCache.GorePosY(g);
                _savedGore[offset + 2] = ReflectionCache.GoreRotation(g);

                ReflectionCache.SetGorePosX(g, Lerp(KeyframeStore.GoreBegin[offset + 0], KeyframeStore.GoreEnd[offset + 0], t));
                ReflectionCache.SetGorePosY(g, Lerp(KeyframeStore.GoreBegin[offset + 1], KeyframeStore.GoreEnd[offset + 1], t));
                ReflectionCache.SetGoreRotation(g, AngleLerp(KeyframeStore.GoreBegin[offset + 2], KeyframeStore.GoreEnd[offset + 2], t));
            }
        }

        private static void RestoreGore()
        {
            var gore = Main.gore;
            if (gore == null) return;
            int count = Math.Min(gore.Length, ReflectionCache.MaxGore);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.GoreActiveEnd[i]) continue;
                if (!KeyframeStore.GoreActiveBegin[i]) continue;

                var g = gore[i];
                if (g == null) continue;

                int offset = i * KeyframeStore.GoreStride;
                ReflectionCache.SetGorePosX(g, _savedGore[offset + 0]);
                ReflectionCache.SetGorePosY(g, _savedGore[offset + 1]);
                ReflectionCache.SetGoreRotation(g, _savedGore[offset + 2]);
            }
        }

        #endregion

        #region WorldItems

        private static void ApplyItems(float t)
        {
            var items = Main.item;
            if (items == null) return;
            int count = Math.Min(items.Length, ReflectionCache.MaxItems);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.ItemActiveEnd[i] || !KeyframeStore.ItemActiveBegin[i])
                {
                    _itemDeltaX[i] = 0f; _itemDeltaY[i] = 0f; continue;
                }

                var item = items[i];
                if (item == null) { _itemDeltaX[i] = 0f; _itemDeltaY[i] = 0f; continue; }

                int offset = i * KeyframeStore.ItemStride;

                // Save current (real) position
                float realX = ReflectionCache.ItemPosX(item);
                float realY = ReflectionCache.ItemPosY(item);
                _savedItem[offset + 0] = realX;
                _savedItem[offset + 1] = realY;

                // Compute interpolated position
                float interpX = Lerp(KeyframeStore.ItemBegin[offset + 0], KeyframeStore.ItemEnd[offset + 0], t);
                float interpY = Lerp(KeyframeStore.ItemBegin[offset + 1], KeyframeStore.ItemEnd[offset + 1], t);

                // Apply interpolated values
                ReflectionCache.SetItemPosX(item, interpX);
                ReflectionCache.SetItemPosY(item, interpY);

                // Compute and store delta
                float dx = interpX - realX;
                float dy = interpY - realY;
                _itemDeltaX[i] = dx;
                _itemDeltaY[i] = dy;

                // Offset oldPosition
                _savedItemOldPos[i * 2 + 0] = ReflectionCache.ItemOldPosX(item);
                _savedItemOldPos[i * 2 + 1] = ReflectionCache.ItemOldPosY(item);
                ReflectionCache.SetItemOldPosX(item, _savedItemOldPos[i * 2 + 0] + dx);
                ReflectionCache.SetItemOldPosY(item, _savedItemOldPos[i * 2 + 1] + dy);
            }
        }

        private static void RestoreItems()
        {
            var items = Main.item;
            if (items == null) return;
            int count = Math.Min(items.Length, ReflectionCache.MaxItems);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.ItemActiveEnd[i]) continue;
                if (!KeyframeStore.ItemActiveBegin[i]) continue;

                var item = items[i];
                if (item == null) continue;

                int offset = i * KeyframeStore.ItemStride;
                ReflectionCache.SetItemPosX(item, _savedItem[offset + 0]);
                ReflectionCache.SetItemPosY(item, _savedItem[offset + 1]);

                // Restore oldPosition
                ReflectionCache.SetItemOldPosX(item, _savedItemOldPos[i * 2 + 0]);
                ReflectionCache.SetItemOldPosY(item, _savedItemOldPos[i * 2 + 1]);
            }
        }

        #endregion

        #region CombatText

        private static void ApplyCombatText(float t)
        {
            var texts = Main.combatText;
            if (texts == null) return;
            int count = Math.Min(texts.Length, ReflectionCache.MaxCombatText);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.CombatTextActiveEnd[i]) continue;
                if (!KeyframeStore.CombatTextActiveBegin[i]) continue;

                var ct = texts[i];
                if (ct == null) continue;

                int offset = i * KeyframeStore.CombatTextStride;

                _savedCombatText[offset + 0] = ReflectionCache.CtPosX(ct);
                _savedCombatText[offset + 1] = ReflectionCache.CtPosY(ct);
                _savedCombatText[offset + 2] = ReflectionCache.CtScale(ct);

                ReflectionCache.SetCtPosX(ct, Lerp(KeyframeStore.CombatTextBegin[offset + 0], KeyframeStore.CombatTextEnd[offset + 0], t));
                ReflectionCache.SetCtPosY(ct, Lerp(KeyframeStore.CombatTextBegin[offset + 1], KeyframeStore.CombatTextEnd[offset + 1], t));
                ReflectionCache.SetCtScale(ct, Lerp(KeyframeStore.CombatTextBegin[offset + 2], KeyframeStore.CombatTextEnd[offset + 2], t));
            }
        }

        private static void RestoreCombatText()
        {
            var texts = Main.combatText;
            if (texts == null) return;
            int count = Math.Min(texts.Length, ReflectionCache.MaxCombatText);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.CombatTextActiveEnd[i]) continue;
                if (!KeyframeStore.CombatTextActiveBegin[i]) continue;

                var ct = texts[i];
                if (ct == null) continue;

                int offset = i * KeyframeStore.CombatTextStride;
                ReflectionCache.SetCtPosX(ct, _savedCombatText[offset + 0]);
                ReflectionCache.SetCtPosY(ct, _savedCombatText[offset + 1]);
                ReflectionCache.SetCtScale(ct, _savedCombatText[offset + 2]);
            }
        }

        #endregion

        #region PopupText

        private static void ApplyPopupText(float t)
        {
            var texts = PopupText.popupText;
            if (texts == null) return;
            int count = Math.Min(texts.Length, ReflectionCache.MaxPopupText);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.PopupTextActiveEnd[i]) continue;
                if (!KeyframeStore.PopupTextActiveBegin[i]) continue;

                var pt = texts[i];
                if (pt == null) continue;

                int offset = i * KeyframeStore.PopupTextStride;

                _savedPopupText[offset + 0] = ReflectionCache.PtPosX(pt);
                _savedPopupText[offset + 1] = ReflectionCache.PtPosY(pt);
                _savedPopupText[offset + 2] = ReflectionCache.PtScale(pt);

                ReflectionCache.SetPtPosX(pt, Lerp(KeyframeStore.PopupTextBegin[offset + 0], KeyframeStore.PopupTextEnd[offset + 0], t));
                ReflectionCache.SetPtPosY(pt, Lerp(KeyframeStore.PopupTextBegin[offset + 1], KeyframeStore.PopupTextEnd[offset + 1], t));
                ReflectionCache.SetPtScale(pt, Lerp(KeyframeStore.PopupTextBegin[offset + 2], KeyframeStore.PopupTextEnd[offset + 2], t));
            }
        }

        private static void RestorePopupText()
        {
            var texts = PopupText.popupText;
            if (texts == null) return;
            int count = Math.Min(texts.Length, ReflectionCache.MaxPopupText);

            for (int i = 0; i < count; i++)
            {
                if (!KeyframeStore.PopupTextActiveEnd[i]) continue;
                if (!KeyframeStore.PopupTextActiveBegin[i]) continue;

                var pt = texts[i];
                if (pt == null) continue;

                int offset = i * KeyframeStore.PopupTextStride;
                ReflectionCache.SetPtPosX(pt, _savedPopupText[offset + 0]);
                ReflectionCache.SetPtPosY(pt, _savedPopupText[offset + 1]);
                ReflectionCache.SetPtScale(pt, _savedPopupText[offset + 2]);
            }
        }

        #endregion

        #region Dust Entity Offset

        /// <summary>
        /// Offset dust particles that have customData linked to an interpolated entity.
        /// Uses Entity.whoAmI for O(1) lookup of the entity's interpolation delta.
        /// Called after all entity Apply methods (deltas are computed).
        /// </summary>
        private static void ApplyDustEntityOffset()
        {
            if (ReflectionCache.DustCustomData == null || ReflectionCache.EntityWhoAmI == null)
                return;

            var dust = Main.dust;
            if (dust == null) return;
            int count = Math.Min(dust.Length, ReflectionCache.MaxDust);

            for (int i = 0; i < count; i++)
            {
                _dustCustomApplied[i] = false;

                var d = dust[i];
                if (d == null) continue;
                if (!ReflectionCache.DustActive(d)) continue;

                object cd = ReflectionCache.DustCustomData(d);
                if (cd == null) continue;

                float deltaX = 0f, deltaY = 0f;
                bool matched = false;

                if (cd is Player)
                {
                    int idx = ReflectionCache.EntityWhoAmI(cd);
                    if (idx >= 0 && idx < _playerDeltaX.Length)
                    {
                        deltaX = _playerDeltaX[idx];
                        deltaY = _playerDeltaY[idx];
                        matched = true;
                    }
                }
                else if (cd is NPC)
                {
                    int idx = ReflectionCache.EntityWhoAmI(cd);
                    if (idx >= 0 && idx < _npcDeltaX.Length)
                    {
                        deltaX = _npcDeltaX[idx];
                        deltaY = _npcDeltaY[idx];
                        matched = true;
                    }
                }
                else if (cd is Projectile)
                {
                    int idx = ReflectionCache.EntityWhoAmI(cd);
                    if (idx >= 0 && idx < _projDeltaX.Length)
                    {
                        deltaX = _projDeltaX[idx];
                        deltaY = _projDeltaY[idx];
                        matched = true;
                    }
                }

                if (!matched || (deltaX == 0f && deltaY == 0f)) continue;

                // Save and offset dust position
                float px = ReflectionCache.DustPosX(d);
                float py = ReflectionCache.DustPosY(d);
                _savedDustCustom[i * 2 + 0] = px;
                _savedDustCustom[i * 2 + 1] = py;

                ReflectionCache.SetDustPosX(d, px + deltaX);
                ReflectionCache.SetDustPosY(d, py + deltaY);
                _dustCustomApplied[i] = true;
            }
        }

        /// <summary>
        /// Restore dust positions that were offset by ApplyDustEntityOffset.
        /// </summary>
        private static void RestoreDustEntityOffset()
        {
            var dust = Main.dust;
            if (dust == null) return;
            int count = Math.Min(dust.Length, ReflectionCache.MaxDust);

            for (int i = 0; i < count; i++)
            {
                if (!_dustCustomApplied[i]) continue;

                var d = dust[i];
                if (d == null) continue;

                ReflectionCache.SetDustPosX(d, _savedDustCustom[i * 2 + 0]);
                ReflectionCache.SetDustPosY(d, _savedDustCustom[i * 2 + 1]);
            }
        }

        #endregion

        #region Math

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Lerp for angles in radians, handling 2*pi wraparound.
        /// </summary>
        private static float AngleLerp(float a, float b, float t)
        {
            float diff = b - a;
            // Guard against infinity/NaN (e.g. newly-spawned projectiles with uninitialized rotation)
            if (float.IsNaN(diff) || float.IsInfinity(diff))
                return b;
            // Normalize to [-PI, PI] using modular arithmetic (no while loops)
            diff %= TWO_PI;
            if (diff > PI) diff -= TWO_PI;
            else if (diff < -PI) diff += TWO_PI;
            return a + diff * t;
        }

        #endregion
    }
}
