using System;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    /// <summary>
    /// Caches all reflection lookups and creates IL-emitted delegates for fast
    /// per-frame entity field access. Initialized once when patches are applied.
    /// </summary>
    public static class ReflectionCache
    {
        // --- Terraria types ---
        public static Type MainType;
        public static Type PlayerType;
        public static Type NPCType;
        public static Type ProjectileType;
        public static Type DustType;
        public static Type GoreType;
        public static Type WorldItemType;
        public static Type CombatTextType;
        public static Type PopupTextType;
        public static Type Vector2Type;

        // --- Entity arrays (static fields on Main) ---
        public static FieldInfo MainPlayerField;   // Main.player
        public static FieldInfo MainNpcField;      // Main.npc
        public static FieldInfo MainProjectileField; // Main.projectile
        public static FieldInfo MainDustField;     // Main.dust
        public static FieldInfo MainGoreField;     // Main.gore
        public static FieldInfo MainItemField;     // Main.item (WorldItem[])
        public static FieldInfo MainCombatTextField; // Main.combatText
        public static FieldInfo MainPopupTextField;  // Main.popupText

        // --- Timing fields ---
        public static FieldInfo UpdateTimeAccumulator; // Main.UpdateTimeAccumulator (public static double)
        public static FieldInfo FrameSkipModeField;    // Main.FrameSkipMode
        public static PropertyInfo IsFixedTimeStepProp;
        public static PropertyInfo TargetElapsedTimeProp;
        public static PropertyInfo VSyncProp;          // GraphicsDeviceManager.SynchronizeWithVerticalRetrace
        public static MethodInfo ApplyChangesMethod;

        // --- Entity field accessors (IL-emitted for speed) ---
        // Player
        public static Func<object, float> PlayerPosX, PlayerPosY;
        public static Func<object, float> PlayerGfxOffY, PlayerHeadRot, PlayerBodyRot, PlayerLegRot;
        public static Action<object, float> SetPlayerPosX, SetPlayerPosY;
        public static Action<object, float> SetPlayerGfxOffY, SetPlayerHeadRot, SetPlayerBodyRot, SetPlayerLegRot;
        public static Func<object, float> PlayerVelX, PlayerVelY;
        public static Func<object, float> PlayerItemLocX, PlayerItemLocY, PlayerItemRot;
        public static Action<object, float> SetPlayerItemLocX, SetPlayerItemLocY, SetPlayerItemRot;

        // NPC
        public static Func<object, float> NpcPosX, NpcPosY;
        public static Func<object, float> NpcRotation, NpcGfxOffY;
        public static Action<object, float> SetNpcPosX, SetNpcPosY;
        public static Action<object, float> SetNpcRotation, SetNpcGfxOffY;
        public static Func<object, float> NpcVelX, NpcVelY;
        public static Func<object, bool> NpcActive;

        // Projectile
        public static Func<object, float> ProjPosX, ProjPosY;
        public static Func<object, float> ProjRotation;
        public static Action<object, float> SetProjPosX, SetProjPosY;
        public static Action<object, float> SetProjRotation;
        public static Func<object, float> ProjVelX, ProjVelY;
        public static Func<object, bool> ProjActive;

        // Dust
        public static Func<object, float> DustPosX, DustPosY;
        public static Func<object, float> DustRotation;
        public static Action<object, float> SetDustPosX, SetDustPosY;
        public static Action<object, float> SetDustRotation;
        public static Func<object, bool> DustActive;

        // Gore
        public static Func<object, float> GorePosX, GorePosY;
        public static Func<object, float> GoreRotation;
        public static Action<object, float> SetGorePosX, SetGorePosY;
        public static Action<object, float> SetGoreRotation;
        public static Func<object, bool> GoreActive;

        // WorldItem (extends Entity)
        public static Func<object, float> ItemPosX, ItemPosY;
        public static Action<object, float> SetItemPosX, SetItemPosY;
        // WorldItem.active is a property, not a field
        public static Func<object, bool> ItemActive;

        // CombatText
        public static Func<object, float> CtPosX, CtPosY, CtScale;
        public static Action<object, float> SetCtPosX, SetCtPosY, SetCtScale;
        public static Func<object, bool> CtActive;

        // PopupText
        public static Func<object, float> PtPosX, PtPosY, PtScale;
        public static Action<object, float> SetPtPosX, SetPtPosY, SetPtScale;
        public static Func<object, bool> PtActive;

        // Projectile trail arrays
        public static FieldInfo ProjOldPosField; // Vector2[10]
        public static FieldInfo ProjOldRotField; // float[10]

        // Player shadow trail (Vector2[3] array for afterimage)
        public static FieldInfo PlayerShadowPosField;

        // Entity.oldPosition (Vec2 X/Y getter/setter per entity type)
        public static Func<object, float> PlayerOldPosX, PlayerOldPosY;
        public static Action<object, float> SetPlayerOldPosX, SetPlayerOldPosY;
        public static Func<object, float> NpcOldPosX, NpcOldPosY;
        public static Action<object, float> SetNpcOldPosX, SetNpcOldPosY;
        public static Func<object, float> ProjOldPosX, ProjOldPosY;
        public static Action<object, float> SetProjOldPosX, SetProjOldPosY;
        public static Func<object, float> ItemOldPosX, ItemOldPosY;
        public static Action<object, float> SetItemOldPosX, SetItemOldPosY;

        // Dust customData (object field, IL-emitted for no boxing)
        public static Func<object, object> DustCustomData;

        // Entity.whoAmI (int field, IL-emitted to avoid boxing)
        public static Func<object, int> EntityWhoAmI;

        // Mouse polling (for per-frame cursor updates)
        public static FieldInfo Vec2XField;            // Vector2.X (cached for RawMouseScale)
        public static FieldInfo Vec2YField;            // Vector2.Y

        // Max entity counts
        public static int MaxPlayers = 256;
        public static int MaxNpcs = 200;
        public static int MaxProjectiles = 1001;
        public static int MaxDust = 6001;
        public static int MaxGore = 601;
        public static int MaxItems = 401;
        public static int MaxCombatText = 100;
        public static int MaxPopupText = 20;

        public static bool Initialized { get; private set; }

        public static bool Initialize(ILogger log)
        {
            try
            {
                // Direct type references (no reflection needed)
                MainType = typeof(Main);
                PlayerType = typeof(Player);
                NPCType = typeof(NPC);
                ProjectileType = typeof(Projectile);
                DustType = typeof(Dust);
                GoreType = typeof(Gore);
                WorldItemType = typeof(WorldItem);
                CombatTextType = typeof(CombatText);
                PopupTextType = typeof(PopupText);
                Vector2Type = typeof(Vector2);

                // Entity arrays on Main (FieldInfo needed for GetEntityArray helper)
                MainPlayerField = MainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                MainNpcField = MainType.GetField("npc", BindingFlags.Public | BindingFlags.Static);
                MainProjectileField = MainType.GetField("projectile", BindingFlags.Public | BindingFlags.Static);
                MainDustField = MainType.GetField("dust", BindingFlags.Public | BindingFlags.Static);
                MainGoreField = MainType.GetField("gore", BindingFlags.Public | BindingFlags.Static);
                MainItemField = MainType.GetField("item", BindingFlags.Public | BindingFlags.Static);
                MainCombatTextField = MainType.GetField("combatText", BindingFlags.Public | BindingFlags.Static);
                MainPopupTextField = MainType.GetField("popupText", BindingFlags.Public | BindingFlags.Static);

                // Timing (FieldInfo needed for GetValue/SetValue in Patches — enum boxing)
                UpdateTimeAccumulator = MainType.GetField("UpdateTimeAccumulator",
                    BindingFlags.Public | BindingFlags.Static);
                FrameSkipModeField = MainType.GetField("FrameSkipMode",
                    BindingFlags.Public | BindingFlags.Static);

                // XNA Game properties (PropertyInfo needed for SetValue on base class)
                var gameType = typeof(Microsoft.Xna.Framework.Game);
                IsFixedTimeStepProp = gameType.GetProperty("IsFixedTimeStep",
                    BindingFlags.Public | BindingFlags.Instance);
                TargetElapsedTimeProp = gameType.GetProperty("TargetElapsedTime",
                    BindingFlags.Public | BindingFlags.Instance);

                // Graphics device manager (PropertyInfo/MethodInfo for VSync control)
                var gdm = Main.graphics;
                if (gdm != null)
                {
                    var gdmType = gdm.GetType();
                    VSyncProp = gdmType.GetProperty("SynchronizeWithVerticalRetrace",
                        BindingFlags.Public | BindingFlags.Instance);
                    ApplyChangesMethod = gdmType.GetMethod("ApplyChanges",
                        BindingFlags.Public | BindingFlags.Instance);
                }

                // Max entity counts from Main
                if (Main.maxNPCs > 0)
                    MaxNpcs = Main.maxNPCs;

                // Projectile trail arrays (FieldInfo needed for GetValue on instance)
                ProjOldPosField = ProjectileType.GetField("oldPos", BindingFlags.Public | BindingFlags.Instance);
                ProjOldRotField = ProjectileType.GetField("oldRot", BindingFlags.Public | BindingFlags.Instance);

                // Player shadow trail (FieldInfo needed for GetValue on instance)
                PlayerShadowPosField = PlayerType.GetField("shadowPos", BindingFlags.Public | BindingFlags.Instance);

                // Vector2 field references (needed for shadowPos/trail manipulation)
                Vec2XField = Vector2Type.GetField("X", BindingFlags.Public | BindingFlags.Instance);
                Vec2YField = Vector2Type.GetField("Y", BindingFlags.Public | BindingFlags.Instance);

                // Build IL-emitted accessors
                BuildEntityAccessors(log);

                Initialized = true;
                log.Info($"ReflectionCache initialized - Types: Player={PlayerType != null}, " +
                    $"NPC={NPCType != null}, Proj={ProjectileType != null}, Dust={DustType != null}, " +
                    $"Gore={GoreType != null}, WorldItem={WorldItemType != null}, " +
                    $"CombatText={CombatTextType != null}, PopupText={PopupTextType != null}");
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"ReflectionCache init failed: {ex}");
                return false;
            }
        }

        private static void BuildEntityAccessors(ILogger log)
        {
            var vec2X = Vector2Type.GetField("X", BindingFlags.Public | BindingFlags.Instance);
            var vec2Y = Vector2Type.GetField("Y", BindingFlags.Public | BindingFlags.Instance);

            var entityType = typeof(Entity);
            var entityPos = entityType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            var entityVel = entityType.GetField("velocity", BindingFlags.Public | BindingFlags.Instance);

            // --- Player (extends Entity) ---
            PlayerPosX = MakeVec2FieldGetter(PlayerType, entityPos, vec2X, "PlayerPosX");
            PlayerPosY = MakeVec2FieldGetter(PlayerType, entityPos, vec2Y, "PlayerPosY");
            SetPlayerPosX = MakeVec2FieldSetter(PlayerType, entityPos, vec2X, "SetPlayerPosX");
            SetPlayerPosY = MakeVec2FieldSetter(PlayerType, entityPos, vec2Y, "SetPlayerPosY");
            PlayerVelX = MakeVec2FieldGetter(PlayerType, entityVel, vec2X, "PlayerVelX");
            PlayerVelY = MakeVec2FieldGetter(PlayerType, entityVel, vec2Y, "PlayerVelY");

            var playerGfxOffY = PlayerType.GetField("gfxOffY", BindingFlags.Public | BindingFlags.Instance);
            var playerHeadRot = PlayerType.GetField("headRotation", BindingFlags.Public | BindingFlags.Instance);
            var playerBodyRot = PlayerType.GetField("bodyRotation", BindingFlags.Public | BindingFlags.Instance);
            var playerLegRot = PlayerType.GetField("legRotation", BindingFlags.Public | BindingFlags.Instance);

            PlayerGfxOffY = MakeFloatGetter(PlayerType, playerGfxOffY, "PlayerGfxOffY");
            PlayerHeadRot = MakeFloatGetter(PlayerType, playerHeadRot, "PlayerHeadRot");
            PlayerBodyRot = MakeFloatGetter(PlayerType, playerBodyRot, "PlayerBodyRot");
            PlayerLegRot = MakeFloatGetter(PlayerType, playerLegRot, "PlayerLegRot");
            SetPlayerGfxOffY = MakeFloatSetter(PlayerType, playerGfxOffY, "SetPlayerGfxOffY");
            SetPlayerHeadRot = MakeFloatSetter(PlayerType, playerHeadRot, "SetPlayerHeadRot");
            SetPlayerBodyRot = MakeFloatSetter(PlayerType, playerBodyRot, "SetPlayerBodyRot");
            SetPlayerLegRot = MakeFloatSetter(PlayerType, playerLegRot, "SetPlayerLegRot");

            var playerItemLoc = PlayerType.GetField("itemLocation", BindingFlags.Public | BindingFlags.Instance);
            var playerItemRot = PlayerType.GetField("itemRotation", BindingFlags.Public | BindingFlags.Instance);
            PlayerItemLocX = MakeVec2FieldGetter(PlayerType, playerItemLoc, vec2X, "PlayerItemLocX");
            PlayerItemLocY = MakeVec2FieldGetter(PlayerType, playerItemLoc, vec2Y, "PlayerItemLocY");
            SetPlayerItemLocX = MakeVec2FieldSetter(PlayerType, playerItemLoc, vec2X, "SetPlayerItemLocX");
            SetPlayerItemLocY = MakeVec2FieldSetter(PlayerType, playerItemLoc, vec2Y, "SetPlayerItemLocY");
            PlayerItemRot = MakeFloatGetter(PlayerType, playerItemRot, "PlayerItemRot");
            SetPlayerItemRot = MakeFloatSetter(PlayerType, playerItemRot, "SetPlayerItemRot");

            // --- Entity.oldPosition (inherited by all Entity subclasses) ---
            var entityOldPos = entityType.GetField("oldPosition", BindingFlags.Public | BindingFlags.Instance);
            PlayerOldPosX = MakeVec2FieldGetter(PlayerType, entityOldPos, vec2X, "PlayerOldPosX");
            PlayerOldPosY = MakeVec2FieldGetter(PlayerType, entityOldPos, vec2Y, "PlayerOldPosY");
            SetPlayerOldPosX = MakeVec2FieldSetter(PlayerType, entityOldPos, vec2X, "SetPlayerOldPosX");
            SetPlayerOldPosY = MakeVec2FieldSetter(PlayerType, entityOldPos, vec2Y, "SetPlayerOldPosY");

            // --- NPC (extends Entity) ---
            NpcPosX = MakeVec2FieldGetter(NPCType, entityPos, vec2X, "NpcPosX");
            NpcPosY = MakeVec2FieldGetter(NPCType, entityPos, vec2Y, "NpcPosY");
            SetNpcPosX = MakeVec2FieldSetter(NPCType, entityPos, vec2X, "SetNpcPosX");
            SetNpcPosY = MakeVec2FieldSetter(NPCType, entityPos, vec2Y, "SetNpcPosY");
            NpcVelX = MakeVec2FieldGetter(NPCType, entityVel, vec2X, "NpcVelX");
            NpcVelY = MakeVec2FieldGetter(NPCType, entityVel, vec2Y, "NpcVelY");

            var npcRotation = NPCType.GetField("rotation", BindingFlags.Public | BindingFlags.Instance);
            var npcGfxOffY = NPCType.GetField("gfxOffY", BindingFlags.Public | BindingFlags.Instance);
            NpcRotation = MakeFloatGetter(NPCType, npcRotation, "NpcRotation");
            NpcGfxOffY = MakeFloatGetter(NPCType, npcGfxOffY, "NpcGfxOffY");
            SetNpcRotation = MakeFloatSetter(NPCType, npcRotation, "SetNpcRotation");
            SetNpcGfxOffY = MakeFloatSetter(NPCType, npcGfxOffY, "SetNpcGfxOffY");
            NpcActive = MakeBoolGetter(NPCType, NPCType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "NpcActive");
            NpcOldPosX = MakeVec2FieldGetter(NPCType, entityOldPos, vec2X, "NpcOldPosX");
            NpcOldPosY = MakeVec2FieldGetter(NPCType, entityOldPos, vec2Y, "NpcOldPosY");
            SetNpcOldPosX = MakeVec2FieldSetter(NPCType, entityOldPos, vec2X, "SetNpcOldPosX");
            SetNpcOldPosY = MakeVec2FieldSetter(NPCType, entityOldPos, vec2Y, "SetNpcOldPosY");

            // --- Projectile (extends Entity) ---
            ProjPosX = MakeVec2FieldGetter(ProjectileType, entityPos, vec2X, "ProjPosX");
            ProjPosY = MakeVec2FieldGetter(ProjectileType, entityPos, vec2Y, "ProjPosY");
            SetProjPosX = MakeVec2FieldSetter(ProjectileType, entityPos, vec2X, "SetProjPosX");
            SetProjPosY = MakeVec2FieldSetter(ProjectileType, entityPos, vec2Y, "SetProjPosY");
            ProjVelX = MakeVec2FieldGetter(ProjectileType, entityVel, vec2X, "ProjVelX");
            ProjVelY = MakeVec2FieldGetter(ProjectileType, entityVel, vec2Y, "ProjVelY");

            var projRotation = ProjectileType.GetField("rotation", BindingFlags.Public | BindingFlags.Instance);
            ProjRotation = MakeFloatGetter(ProjectileType, projRotation, "ProjRotation");
            SetProjRotation = MakeFloatSetter(ProjectileType, projRotation, "SetProjRotation");
            ProjActive = MakeBoolGetter(ProjectileType,
                ProjectileType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "ProjActive");
            ProjOldPosX = MakeVec2FieldGetter(ProjectileType, entityOldPos, vec2X, "ProjOldPosX");
            ProjOldPosY = MakeVec2FieldGetter(ProjectileType, entityOldPos, vec2Y, "ProjOldPosY");
            SetProjOldPosX = MakeVec2FieldSetter(ProjectileType, entityOldPos, vec2X, "SetProjOldPosX");
            SetProjOldPosY = MakeVec2FieldSetter(ProjectileType, entityOldPos, vec2Y, "SetProjOldPosY");

            // --- Dust (own fields, not Entity) ---
            var dustPos = DustType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            DustPosX = MakeVec2FieldGetter(DustType, dustPos, vec2X, "DustPosX");
            DustPosY = MakeVec2FieldGetter(DustType, dustPos, vec2Y, "DustPosY");
            SetDustPosX = MakeVec2FieldSetter(DustType, dustPos, vec2X, "SetDustPosX");
            SetDustPosY = MakeVec2FieldSetter(DustType, dustPos, vec2Y, "SetDustPosY");

            var dustRotation = DustType.GetField("rotation", BindingFlags.Public | BindingFlags.Instance);
            DustRotation = MakeFloatGetter(DustType, dustRotation, "DustRotation");
            SetDustRotation = MakeFloatSetter(DustType, dustRotation, "SetDustRotation");
            DustActive = MakeBoolGetter(DustType,
                DustType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "DustActive");
            DustCustomData = MakeObjectGetter(DustType,
                DustType.GetField("customData", BindingFlags.Public | BindingFlags.Instance), "DustCustomData");

            // --- Entity.whoAmI (used for dust customData -> entity index lookup) ---
            var entityWhoAmI = entityType.GetField("whoAmI", BindingFlags.Public | BindingFlags.Instance);
            EntityWhoAmI = MakeIntGetter(entityType, entityWhoAmI, "EntityWhoAmI");

            // --- Gore (own fields, not Entity) ---
            var gorePos = GoreType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            GorePosX = MakeVec2FieldGetter(GoreType, gorePos, vec2X, "GorePosX");
            GorePosY = MakeVec2FieldGetter(GoreType, gorePos, vec2Y, "GorePosY");
            SetGorePosX = MakeVec2FieldSetter(GoreType, gorePos, vec2X, "SetGorePosX");
            SetGorePosY = MakeVec2FieldSetter(GoreType, gorePos, vec2Y, "SetGorePosY");

            var goreRotation = GoreType.GetField("rotation", BindingFlags.Public | BindingFlags.Instance);
            GoreRotation = MakeFloatGetter(GoreType, goreRotation, "GoreRotation");
            SetGoreRotation = MakeFloatSetter(GoreType, goreRotation, "SetGoreRotation");
            GoreActive = MakeBoolGetter(GoreType,
                GoreType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "GoreActive");

            // --- WorldItem (extends Entity) ---
            ItemPosX = MakeVec2FieldGetter(WorldItemType, entityPos, vec2X, "ItemPosX");
            ItemPosY = MakeVec2FieldGetter(WorldItemType, entityPos, vec2Y, "ItemPosY");
            SetItemPosX = MakeVec2FieldSetter(WorldItemType, entityPos, vec2X, "SetItemPosX");
            SetItemPosY = MakeVec2FieldSetter(WorldItemType, entityPos, vec2Y, "SetItemPosY");
            // WorldItem.active is a property (=> inner.active), use PropertyInfo
            var itemActiveProp = WorldItemType.GetProperty("active", BindingFlags.Public | BindingFlags.Instance);
            if (itemActiveProp != null)
                ItemActive = MakePropertyBoolGetter(WorldItemType, itemActiveProp, "ItemActive");
            ItemOldPosX = MakeVec2FieldGetter(WorldItemType, entityOldPos, vec2X, "ItemOldPosX");
            ItemOldPosY = MakeVec2FieldGetter(WorldItemType, entityOldPos, vec2Y, "ItemOldPosY");
            SetItemOldPosX = MakeVec2FieldSetter(WorldItemType, entityOldPos, vec2X, "SetItemOldPosX");
            SetItemOldPosY = MakeVec2FieldSetter(WorldItemType, entityOldPos, vec2Y, "SetItemOldPosY");

            // --- CombatText ---
            var ctPos = CombatTextType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            CtPosX = MakeVec2FieldGetter(CombatTextType, ctPos, vec2X, "CtPosX");
            CtPosY = MakeVec2FieldGetter(CombatTextType, ctPos, vec2Y, "CtPosY");
            SetCtPosX = MakeVec2FieldSetter(CombatTextType, ctPos, vec2X, "SetCtPosX");
            SetCtPosY = MakeVec2FieldSetter(CombatTextType, ctPos, vec2Y, "SetCtPosY");

            var ctScale = CombatTextType.GetField("scale", BindingFlags.Public | BindingFlags.Instance);
            CtScale = MakeFloatGetter(CombatTextType, ctScale, "CtScale");
            SetCtScale = MakeFloatSetter(CombatTextType, ctScale, "SetCtScale");
            CtActive = MakeBoolGetter(CombatTextType,
                CombatTextType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "CtActive");

            // --- PopupText ---
            var ptPos = PopupTextType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            PtPosX = MakeVec2FieldGetter(PopupTextType, ptPos, vec2X, "PtPosX");
            PtPosY = MakeVec2FieldGetter(PopupTextType, ptPos, vec2Y, "PtPosY");
            SetPtPosX = MakeVec2FieldSetter(PopupTextType, ptPos, vec2X, "SetPtPosX");
            SetPtPosY = MakeVec2FieldSetter(PopupTextType, ptPos, vec2Y, "SetPtPosY");

            var ptScale = PopupTextType.GetField("scale", BindingFlags.Public | BindingFlags.Instance);
            PtScale = MakeFloatGetter(PopupTextType, ptScale, "PtScale");
            SetPtScale = MakeFloatSetter(PopupTextType, ptScale, "SetPtScale");
            PtActive = MakeBoolGetter(PopupTextType,
                PopupTextType.GetField("active", BindingFlags.Public | BindingFlags.Instance), "PtActive");

            log.Info("IL-emitted field accessors built successfully");
        }

        #region IL Emit Helpers

        /// <summary>
        /// Creates a getter for structField.component (e.g., entity.position.X).
        /// IL: ldarg.0 -> castclass -> ldflda structField -> ldfld component -> ret
        /// </summary>
        private static Func<object, float> MakeVec2FieldGetter(
            Type ownerType, FieldInfo structField, FieldInfo component, string name)
        {
            var dm = new DynamicMethod(name, typeof(float), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldflda, structField);
            il.Emit(OpCodes.Ldfld, component);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)dm.CreateDelegate(typeof(Func<object, float>));
        }

        /// <summary>
        /// Creates a setter for structField.component (e.g., entity.position.X = value).
        /// IL: ldarg.0 -> castclass -> ldflda structField -> ldarg.1 -> stfld component -> ret
        /// </summary>
        private static Action<object, float> MakeVec2FieldSetter(
            Type ownerType, FieldInfo structField, FieldInfo component, string name)
        {
            var dm = new DynamicMethod(name, null, new[] { typeof(object), typeof(float) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldflda, structField);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, component);
            il.Emit(OpCodes.Ret);
            return (Action<object, float>)dm.CreateDelegate(typeof(Action<object, float>));
        }

        /// <summary>
        /// Creates a getter for a float instance field.
        /// </summary>
        private static Func<object, float> MakeFloatGetter(Type ownerType, FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, typeof(float), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)dm.CreateDelegate(typeof(Func<object, float>));
        }

        /// <summary>
        /// Creates a setter for a float instance field.
        /// </summary>
        private static Action<object, float> MakeFloatSetter(Type ownerType, FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, null, new[] { typeof(object), typeof(float) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, float>)dm.CreateDelegate(typeof(Action<object, float>));
        }

        /// <summary>
        /// Creates a getter for a bool instance field.
        /// </summary>
        private static Func<object, bool> MakeBoolGetter(Type ownerType, FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, typeof(bool), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));
        }

        /// <summary>
        /// Creates a getter for an object instance field (reference types, no boxing needed).
        /// </summary>
        private static Func<object, object> MakeObjectGetter(Type ownerType, FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, typeof(object), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
        }

        /// <summary>
        /// Creates a getter for an int instance field.
        /// </summary>
        private static Func<object, int> MakeIntGetter(Type ownerType, FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, typeof(int), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, int>)dm.CreateDelegate(typeof(Func<object, int>));
        }

        /// <summary>
        /// Creates a getter for a bool property (uses callvirt on the getter method).
        /// </summary>
        private static Func<object, bool> MakePropertyBoolGetter(Type ownerType, PropertyInfo prop, string name)
        {
            var getter = prop.GetGetMethod();
            var dm = new DynamicMethod(name, typeof(bool), new[] { typeof(object) },
                typeof(ReflectionCache).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));
        }

        #endregion

        #region Array Access Helpers

        /// <summary>
        /// Gets an entity array from Main (e.g., Main.player, Main.npc).
        /// Returns the Array object. Caller iterates with Array indexing.
        /// </summary>
        public static Array GetEntityArray(FieldInfo field)
        {
            return field?.GetValue(null) as Array;
        }

        #endregion
    }
}
