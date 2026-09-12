using System;
using System.Reflection;
using Terraria;

namespace FpsUnlocked
{
    /// <summary>
    /// Flat float arrays storing entity positions/rotations at two keyframes:
    /// Begin (before game tick) and End (after game tick).
    /// On partial ticks, the Interpolator blends between Begin and End.
    /// Also stores "active" state to detect spawn/death transitions.
    /// </summary>
    public static class KeyframeStore
    {
        // Per-entity field count (number of floats per entity in the keyframe arrays):
        // Player: bottomX, bottomY, gfxOffY, headRot, bodyRot, legRot, itemOffsetX, itemOffsetY, itemRot = 9
        // NPC: posX, posY, rotation, gfxOffY = 4
        // Projectile: posX, posY, rotation = 3
        // Gore: posX, posY, rotation = 3
        // WorldItem: posX, posY = 2
        // CombatText: posX, posY, scale = 3
        // PopupText: posX, posY, scale = 3
        // NOTE: Dust interpolation disabled (slot reuse causes flickering artifacts)

        public const int PlayerStride = 9;
        public const int NpcStride = 4;
        public const int ProjStride = 3;
        public const int DustStride = 3;
        public const int GoreStride = 3;
        public const int ItemStride = 2;
        public const int CombatTextStride = 3;
        public const int PopupTextStride = 3;

        // Begin keyframe (state before game tick)
        public static float[] PlayerBegin;
        public static float[] NpcBegin;
        public static float[] ProjBegin;
        public static float[] DustBegin;
        public static float[] GoreBegin;
        public static float[] ItemBegin;
        public static float[] CombatTextBegin;
        public static float[] PopupTextBegin;

        // End keyframe (state after game tick)
        public static float[] PlayerEnd;
        public static float[] NpcEnd;
        public static float[] ProjEnd;
        public static float[] DustEnd;
        public static float[] GoreEnd;
        public static float[] ItemEnd;
        public static float[] CombatTextEnd;
        public static float[] PopupTextEnd;

        // Active state per entity (to detect spawn/death)
        public static bool[] PlayerActiveBegin;
        public static bool[] NpcActiveBegin, NpcActiveEnd;
        public static bool[] ProjActiveBegin, ProjActiveEnd;
        public static bool[] DustActiveBegin, DustActiveEnd;
        public static bool[] GoreActiveBegin, GoreActiveEnd;
        public static bool[] ItemActiveBegin, ItemActiveEnd;
        public static bool[] CombatTextActiveBegin, CombatTextActiveEnd;
        public static bool[] PopupTextActiveBegin, PopupTextActiveEnd;

        // Player velocity (for teleport detection)
        public static float[] PlayerVelBegin; // velX, velY per player = stride 2

        // Projectile trail arrays: oldPos[10] (x,y each) + oldRot[10]
        public const int TrailLength = 10;
        public const int ProjTrailStride = TrailLength * 3; // 10 * (posX + posY + rot)
        public static float[] ProjTrailBegin;
        public static float[] ProjTrailEnd;

        // Skip flags: set when entity teleported or just spawned (skip interpolation for 1 tick)
        public static bool[] PlayerSkip;
        public static bool[] NpcSkip;
        public static bool[] ProjSkip;

        /// <summary>
        /// Allocate all keyframe arrays. Called once at init.
        /// </summary>
        public static void Allocate()
        {
            int maxP = ReflectionCache.MaxPlayers;
            int maxN = ReflectionCache.MaxNpcs;
            int maxPr = ReflectionCache.MaxProjectiles;
            int maxD = ReflectionCache.MaxDust;
            int maxG = ReflectionCache.MaxGore;
            int maxI = ReflectionCache.MaxItems;
            int maxCt = ReflectionCache.MaxCombatText;
            int maxPt = ReflectionCache.MaxPopupText;

            PlayerBegin = new float[maxP * PlayerStride];
            PlayerEnd = new float[maxP * PlayerStride];
            PlayerActiveBegin = new bool[maxP];
            PlayerVelBegin = new float[maxP * 2];
            PlayerSkip = new bool[maxP];

            NpcBegin = new float[maxN * NpcStride];
            NpcEnd = new float[maxN * NpcStride];
            NpcActiveBegin = new bool[maxN];
            NpcActiveEnd = new bool[maxN];
            NpcSkip = new bool[maxN];

            ProjBegin = new float[maxPr * ProjStride];
            ProjEnd = new float[maxPr * ProjStride];
            ProjActiveBegin = new bool[maxPr];
            ProjActiveEnd = new bool[maxPr];
            ProjSkip = new bool[maxPr];
            ProjTrailBegin = new float[maxPr * ProjTrailStride];
            ProjTrailEnd = new float[maxPr * ProjTrailStride];

            DustBegin = new float[maxD * DustStride];
            DustEnd = new float[maxD * DustStride];
            DustActiveBegin = new bool[maxD];
            DustActiveEnd = new bool[maxD];

            GoreBegin = new float[maxG * GoreStride];
            GoreEnd = new float[maxG * GoreStride];
            GoreActiveBegin = new bool[maxG];
            GoreActiveEnd = new bool[maxG];

            ItemBegin = new float[maxI * ItemStride];
            ItemEnd = new float[maxI * ItemStride];
            ItemActiveBegin = new bool[maxI];
            ItemActiveEnd = new bool[maxI];

            CombatTextBegin = new float[maxCt * CombatTextStride];
            CombatTextEnd = new float[maxCt * CombatTextStride];
            CombatTextActiveBegin = new bool[maxCt];
            CombatTextActiveEnd = new bool[maxCt];

            PopupTextBegin = new float[maxPt * PopupTextStride];
            PopupTextEnd = new float[maxPt * PopupTextStride];
            PopupTextActiveBegin = new bool[maxPt];
            PopupTextActiveEnd = new bool[maxPt];
        }

        /// <summary>
        /// Zero all arrays. Called on world load/unload.
        /// </summary>
        public static void Clear()
        {
            if (PlayerBegin == null) return;

            Array.Clear(PlayerBegin, 0, PlayerBegin.Length);
            Array.Clear(PlayerEnd, 0, PlayerEnd.Length);
            Array.Clear(PlayerActiveBegin, 0, PlayerActiveBegin.Length);
            Array.Clear(PlayerVelBegin, 0, PlayerVelBegin.Length);
            Array.Clear(PlayerSkip, 0, PlayerSkip.Length);

            Array.Clear(NpcBegin, 0, NpcBegin.Length);
            Array.Clear(NpcEnd, 0, NpcEnd.Length);
            Array.Clear(NpcActiveBegin, 0, NpcActiveBegin.Length);
            Array.Clear(NpcActiveEnd, 0, NpcActiveEnd.Length);
            Array.Clear(NpcSkip, 0, NpcSkip.Length);

            Array.Clear(ProjBegin, 0, ProjBegin.Length);
            Array.Clear(ProjEnd, 0, ProjEnd.Length);
            Array.Clear(ProjActiveBegin, 0, ProjActiveBegin.Length);
            Array.Clear(ProjActiveEnd, 0, ProjActiveEnd.Length);
            Array.Clear(ProjSkip, 0, ProjSkip.Length);
            Array.Clear(ProjTrailBegin, 0, ProjTrailBegin.Length);
            Array.Clear(ProjTrailEnd, 0, ProjTrailEnd.Length);

            Array.Clear(DustBegin, 0, DustBegin.Length);
            Array.Clear(DustEnd, 0, DustEnd.Length);
            Array.Clear(DustActiveBegin, 0, DustActiveBegin.Length);
            Array.Clear(DustActiveEnd, 0, DustActiveEnd.Length);

            Array.Clear(GoreBegin, 0, GoreBegin.Length);
            Array.Clear(GoreEnd, 0, GoreEnd.Length);
            Array.Clear(GoreActiveBegin, 0, GoreActiveBegin.Length);
            Array.Clear(GoreActiveEnd, 0, GoreActiveEnd.Length);

            Array.Clear(ItemBegin, 0, ItemBegin.Length);
            Array.Clear(ItemEnd, 0, ItemEnd.Length);
            Array.Clear(ItemActiveBegin, 0, ItemActiveBegin.Length);
            Array.Clear(ItemActiveEnd, 0, ItemActiveEnd.Length);

            Array.Clear(CombatTextBegin, 0, CombatTextBegin.Length);
            Array.Clear(CombatTextEnd, 0, CombatTextEnd.Length);
            Array.Clear(CombatTextActiveBegin, 0, CombatTextActiveBegin.Length);
            Array.Clear(CombatTextActiveEnd, 0, CombatTextActiveEnd.Length);

            Array.Clear(PopupTextBegin, 0, PopupTextBegin.Length);
            Array.Clear(PopupTextEnd, 0, PopupTextEnd.Length);
            Array.Clear(PopupTextActiveBegin, 0, PopupTextActiveBegin.Length);
            Array.Clear(PopupTextActiveEnd, 0, PopupTextActiveEnd.Length);
        }

        #region Snapshot Capture

        /// <summary>
        /// Capture the "End" keyframe: snapshot all entity state after game tick.
        /// Called in DoUpdate postfix on full tick frames.
        /// Also detects teleports and spawn/death transitions.
        /// </summary>
        public static void CaptureEndKeyframe()
        {
            // Players (with teleport detection)
            var players = ReflectionCache.GetEntityArray(ReflectionCache.MainPlayerField);
            if (players != null)
            {
                int count = Math.Min(players.Length, ReflectionCache.MaxPlayers);
                for (int i = 0; i < count; i++)
                {
                    var p = players.GetValue(i);
                    var player = p as Player;
                    if (player == null) continue;
                    int offset = i * PlayerStride;
                    float endX = player.Bottom.X;
                    float endY = player.Bottom.Y;
                    PlayerEnd[offset + 0] = endX;
                    PlayerEnd[offset + 1] = endY;
                    PlayerEnd[offset + 2] = ReflectionCache.PlayerGfxOffY(p);
                    PlayerEnd[offset + 3] = ReflectionCache.PlayerHeadRot(p);
                    PlayerEnd[offset + 4] = ReflectionCache.PlayerBodyRot(p);
                    PlayerEnd[offset + 5] = ReflectionCache.PlayerLegRot(p);
                    PlayerEnd[offset + 6] = ReflectionCache.PlayerItemLocX(p) - endX;
                    PlayerEnd[offset + 7] = ReflectionCache.PlayerItemLocY(p) - endY;
                    PlayerEnd[offset + 8] = ReflectionCache.PlayerItemRot(p);

                    // Teleport detection: if actual movement >> expected (velocity-based)
                    float beginX = PlayerBegin[offset + 0];
                    float beginY = PlayerBegin[offset + 1];
                    float velX = PlayerVelBegin[i * 2 + 0];
                    float velY = PlayerVelBegin[i * 2 + 1];
                    float expectedX = beginX + velX;
                    float expectedY = beginY + velY;
                    float dx = endX - expectedX;
                    float dy = endY - expectedY;
                    float distSq = dx * dx + dy * dy;
                    // Threshold: 64 pixels (~4 tiles) beyond expected movement
                    PlayerSkip[i] = distSq > 4096f;
                }
            }

            // NPCs (with spawn detection)
            CaptureEndArray(ReflectionCache.MainNpcField, ReflectionCache.MaxNpcs,
                NpcEnd, NpcActiveBegin, NpcActiveEnd, NpcSkip, NpcStride,
                ReflectionCache.NpcActive, ReflectionCache.NpcPosX, ReflectionCache.NpcPosY,
                ReflectionCache.NpcRotation, ReflectionCache.NpcGfxOffY);

            // Projectiles
            CaptureEndArray(ReflectionCache.MainProjectileField, ReflectionCache.MaxProjectiles,
                ProjEnd, ProjActiveBegin, ProjActiveEnd, ProjSkip, ProjStride,
                ReflectionCache.ProjActive, ReflectionCache.ProjPosX, ReflectionCache.ProjPosY,
                ReflectionCache.ProjRotation, null);
            // Trail interpolation disabled — slot reuse causes visual artifacts
            // CaptureProjectileTrails(ProjTrailEnd);

            // Dust interpolation disabled — slot reuse causes flickering (torch, molten armor, etc.)
            // CaptureEndSimpleNoSkip(ReflectionCache.MainDustField, ReflectionCache.MaxDust,
            //     DustEnd, DustActiveEnd, DustStride,
            //     ReflectionCache.DustActive, ReflectionCache.DustPosX, ReflectionCache.DustPosY,
            //     ReflectionCache.DustRotation, null);

            // Gore
            CaptureEndSimpleNoSkip(ReflectionCache.MainGoreField, ReflectionCache.MaxGore,
                GoreEnd, GoreActiveEnd, GoreStride,
                ReflectionCache.GoreActive, ReflectionCache.GorePosX, ReflectionCache.GorePosY,
                ReflectionCache.GoreRotation, null);

            // WorldItems
            CaptureEndItems();

            // CombatText
            CaptureEndSimpleNoSkip(ReflectionCache.MainCombatTextField, ReflectionCache.MaxCombatText,
                CombatTextEnd, CombatTextActiveEnd, CombatTextStride,
                ReflectionCache.CtActive, ReflectionCache.CtPosX, ReflectionCache.CtPosY,
                ReflectionCache.CtScale, null);

            // PopupText
            CaptureEndSimpleNoSkip(ReflectionCache.MainPopupTextField, ReflectionCache.MaxPopupText,
                PopupTextEnd, PopupTextActiveEnd, PopupTextStride,
                ReflectionCache.PtActive, ReflectionCache.PtPosX, ReflectionCache.PtPosY,
                ReflectionCache.PtScale, null);
        }

        #endregion

        #region Capture Helpers

        private static void CaptureEndArray(FieldInfo arrayField, int max,
            float[] dest, bool[] activeBegin, bool[] activeEnd, bool[] skip, int stride,
            Func<object, bool> getActive,
            Func<object, float> getPosX, Func<object, float> getPosY,
            Func<object, float> getField3, Func<object, float> getField4)
        {
            var arr = ReflectionCache.GetEntityArray(arrayField);
            if (arr == null) return;
            int count = Math.Min(arr.Length, max);
            for (int i = 0; i < count; i++)
            {
                var entity = arr.GetValue(i);
                if (entity == null) { activeEnd[i] = false; continue; }
                bool active = getActive(entity);
                activeEnd[i] = active;

                // Skip if entity just spawned (wasn't active before, is now)
                if (skip != null)
                    skip[i] = active && !activeBegin[i];

                if (!active) continue;

                int offset = i * stride;
                dest[offset + 0] = getPosX(entity);
                dest[offset + 1] = getPosY(entity);
                if (stride >= 3 && getField3 != null)
                    dest[offset + 2] = getField3(entity);
                if (stride >= 4 && getField4 != null)
                    dest[offset + 3] = getField4(entity);
            }
        }

        private static void CaptureEndSimpleNoSkip(FieldInfo arrayField, int max,
            float[] dest, bool[] activeEnd, int stride,
            Func<object, bool> getActive,
            Func<object, float> getPosX, Func<object, float> getPosY,
            Func<object, float> getField3, Func<object, float> getField4)
        {
            var arr = ReflectionCache.GetEntityArray(arrayField);
            if (arr == null) return;
            int count = Math.Min(arr.Length, max);
            for (int i = 0; i < count; i++)
            {
                var entity = arr.GetValue(i);
                if (entity == null) { activeEnd[i] = false; continue; }
                bool active = getActive(entity);
                activeEnd[i] = active;
                if (!active) continue;

                int offset = i * stride;
                dest[offset + 0] = getPosX(entity);
                dest[offset + 1] = getPosY(entity);
                if (stride >= 3 && getField3 != null)
                    dest[offset + 2] = getField3(entity);
                if (stride >= 4 && getField4 != null)
                    dest[offset + 3] = getField4(entity);
            }
        }

        private static void CaptureEndItems()
        {
            var arr = ReflectionCache.GetEntityArray(ReflectionCache.MainItemField);
            if (arr == null) return;
            int count = Math.Min(arr.Length, ReflectionCache.MaxItems);
            for (int i = 0; i < count; i++)
            {
                var entity = arr.GetValue(i);
                if (entity == null) { ItemActiveEnd[i] = false; continue; }
                bool active = ReflectionCache.ItemActive(entity);
                ItemActiveEnd[i] = active;
                if (!active) continue;

                int offset = i * ItemStride;
                ItemEnd[offset + 0] = ReflectionCache.ItemPosX(entity);
                ItemEnd[offset + 1] = ReflectionCache.ItemPosY(entity);
            }
        }

        private static void CaptureProjectileTrails(float[] dest)
        {
            var arr = ReflectionCache.GetEntityArray(ReflectionCache.MainProjectileField);
            if (arr == null || ReflectionCache.ProjOldPosField == null || ReflectionCache.ProjOldRotField == null)
                return;

            var vec2X = ReflectionCache.Vector2Type.GetField("X");
            var vec2Y = ReflectionCache.Vector2Type.GetField("Y");

            int count = Math.Min(arr.Length, ReflectionCache.MaxProjectiles);
            for (int i = 0; i < count; i++)
            {
                var proj = arr.GetValue(i);
                if (proj == null) continue;
                if (!ReflectionCache.ProjActive(proj)) continue;

                var oldPosArr = ReflectionCache.ProjOldPosField.GetValue(proj) as Array;
                var oldRotArr = ReflectionCache.ProjOldRotField.GetValue(proj) as float[];
                if (oldPosArr == null || oldRotArr == null) continue;

                int baseOffset = i * ProjTrailStride;
                int trailCount = Math.Min(TrailLength, oldPosArr.Length);
                for (int t = 0; t < trailCount; t++)
                {
                    var vec2 = oldPosArr.GetValue(t);
                    int tOffset = baseOffset + t * 3;
                    dest[tOffset + 0] = (float)vec2X.GetValue(vec2);
                    dest[tOffset + 1] = (float)vec2Y.GetValue(vec2);
                    dest[tOffset + 2] = oldRotArr[t];
                }
            }
        }

        #endregion
    }
}
