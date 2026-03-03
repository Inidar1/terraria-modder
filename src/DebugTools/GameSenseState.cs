using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Reflection;
using Game = TerrariaModder.Core.Reflection.Game;

namespace DebugTools
{
    /// <summary>
    /// Reads rich structured game state for external tooling.
    /// All methods return JSON strings ready for HTTP response.
    /// All methods are safe to call from any thread - they never throw.
    /// </summary>
    public static class GameSenseState
    {
        #region Inventory (GS-001)

        /// <summary>
        /// Full inventory state: items, armor, accessories, buffs, held item, defense.
        /// </summary>
        public static string GetInventory()
        {
            try
            {
                if (!Game.InWorld)
                    return NotInWorld();

                var player = Game.LocalPlayer;
                if (player == null)
                    return NotInWorld();

                var sb = new StringBuilder(4096);
                sb.Append("{");

                // Selected item info
                int selectedIdx = 0;
                try { selectedIdx = player.selectedItem; } catch { }
                sb.Append("\"selectedSlot\": ").Append(selectedIdx).Append(", ");

                sb.Append("\"heldItem\": ");
                try { AppendItem(sb, player.HeldItem); }
                catch { sb.Append("null"); }
                sb.Append(", ");

                // Defense
                int defense = 0;
                try { defense = player.statDefense; } catch { }
                sb.Append("\"defense\": ").Append(defense).Append(", ");

                // Inventory slots (0-49 are main inventory, 50-53 are coins, 54-57 are ammo, 58 is trash)
                sb.Append("\"inventory\": [");
                AppendItemSlots(sb, player, 0, 50);
                sb.Append("], ");

                // Coins (slots 50-53)
                sb.Append("\"coins\": [");
                AppendItemSlots(sb, player, 50, 54);
                sb.Append("], ");

                // Ammo (slots 54-57)
                sb.Append("\"ammo\": [");
                AppendItemSlots(sb, player, 54, 58);
                sb.Append("], ");

                // Armor and accessories (armor[0-2] = armor, armor[3-9] = accessories, armor[10-19] = vanity/social)
                sb.Append("\"armor\": [");
                AppendArmorSlots(sb, player, 0, 3);
                sb.Append("], ");

                sb.Append("\"accessories\": [");
                AppendArmorSlots(sb, player, 3, 10);
                sb.Append("], ");

                // Active buffs
                sb.Append("\"buffs\": [");
                AppendBuffs(sb, player);
                sb.Append("]");

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorResponse("GetInventory", ex);
            }
        }

        private static void AppendItemSlots(StringBuilder sb, Player player, int start, int end)
        {
            bool first = true;
            for (int i = start; i < end; i++)
            {
                try
                {
                    var item = player.inventory[i];
                    if (item == null || item.type == 0) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append("{\"slot\": ").Append(i).Append(", ");
                    AppendItemFields(sb, item);
                    sb.Append("}");
                }
                catch
                {
                    // Skip slots that throw during read
                }
            }
        }

        private static void AppendArmorSlots(StringBuilder sb, Player player, int start, int end)
        {
            bool first = true;
            for (int i = start; i < end; i++)
            {
                try
                {
                    var item = player.armor[i];
                    if (item == null || item.type == 0) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append("{\"slot\": ").Append(i).Append(", ");
                    AppendItemFields(sb, item);
                    sb.Append("}");
                }
                catch
                {
                    // Skip armor slots that throw during read
                }
            }
        }

        private static void AppendBuffs(StringBuilder sb, Player player)
        {
            bool first = true;
            try
            {
                var buffTypes = player.buffType;
                var buffTimes = player.buffTime;
                if (buffTypes == null || buffTimes == null) return;

                for (int i = 0; i < buffTypes.Length; i++)
                {
                    try
                    {
                        int buffId = buffTypes[i];
                        if (buffId == 0) continue;
                        if (!first) sb.Append(", ");
                        first = false;

                        string buffName = "";
                        try { buffName = Lang.GetBuffName(buffId); } catch { }

                        sb.Append("{\"id\": ").Append(buffId);
                        sb.Append(", \"name\": \"").Append(EscapeJson(buffName)).Append("\"");
                        int timeSeconds = i < buffTimes.Length ? buffTimes[i] / 60 : 0;
                        sb.Append(", \"time\": ").Append(timeSeconds);
                        sb.Append("}");
                    }
                    catch
                    {
                        // Skip buffs that throw
                    }
                }
            }
            catch
            {
                // Buff arrays inaccessible
            }
        }

        #endregion

        #region Entities (GS-002)

        /// <summary>
        /// Nearby entities: hostile and friendly NPCs sorted by distance.
        /// </summary>
        public static string GetEntities(int range = 600, int maxEntities = 15)
        {
            try
            {
                if (!Game.InWorld)
                    return NotInWorld();

                var player = Game.LocalPlayer;
                if (player == null)
                    return NotInWorld();

                var playerPos = Game.PlayerPosition;
                float rangeSquared = (float)range * range;

                var hostiles = new List<NpcInfo>();
                var friendlies = new List<NpcInfo>();

                ScanNpcs(hostiles, friendlies, playerPos, rangeSquared);

                hostiles.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                friendlies.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                // Cap total entities
                int hostileCap = Math.Min(hostiles.Count, maxEntities);
                int friendlyCap = Math.Min(friendlies.Count, maxEntities - hostileCap);
                if (friendlyCap < 0) friendlyCap = 0;

                var sb = new StringBuilder(2048);
                sb.Append("{");
                sb.Append("\"range\": ").Append(range).Append(", ");
                sb.Append("\"rangeTiles\": ").Append(range / 16).Append(", ");

                sb.Append("\"hostile\": [");
                for (int i = 0; i < hostileCap; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendNpcInfo(sb, hostiles[i]);
                }
                sb.Append("], ");

                sb.Append("\"hostileCount\": ").Append(hostiles.Count).Append(", ");

                sb.Append("\"friendly\": [");
                for (int i = 0; i < friendlyCap; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendNpcInfo(sb, friendlies[i]);
                }
                sb.Append("], ");

                sb.Append("\"friendlyCount\": ").Append(friendlies.Count);

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorResponse("GetEntities", ex);
            }
        }

        #endregion

        #region Tiles (GS-003)

        /// <summary>
        /// Tile grid around the player as a compact character grid.
        /// </summary>
        public static string GetTiles(int width = 60, int height = 34)
        {
            try
            {
                if (!Game.InWorld)
                    return NotInWorld();

                var player = Game.LocalPlayer;
                if (player == null)
                    return NotInWorld();

                var playerPos = Game.PlayerPosition;
                int playerTileX = (int)(playerPos.X / 16f);
                int playerTileY = (int)(playerPos.Y / 16f);

                int halfW = width / 2;
                int halfH = height / 2;

                int startX = playerTileX - halfW;
                int startY = playerTileY - halfH;
                int endX = startX + width;
                int endY = startY + height;

                // Clamp to world bounds
                int maxX = Game.MaxTilesX;
                int maxY = Game.MaxTilesY;

                var sb = new StringBuilder(width * height + 512);
                sb.Append("{");
                sb.Append("\"playerTileX\": ").Append(playerTileX).Append(", ");
                sb.Append("\"playerTileY\": ").Append(playerTileY).Append(", ");
                sb.Append("\"gridStartX\": ").Append(startX).Append(", ");
                sb.Append("\"gridStartY\": ").Append(startY).Append(", ");
                sb.Append("\"width\": ").Append(width).Append(", ");
                sb.Append("\"height\": ").Append(height).Append(", ");
                sb.Append("\"legend\": \"#=solid .=empty ~=water L=lava H=honey S=shimmer ==platform D=door C=chest T=workbench @=player\", ");
                sb.Append("\"grid\": [");

                for (int y = startY; y < endY; y++)
                {
                    if (y > startY) sb.Append(", ");
                    sb.Append("\"");

                    for (int x = startX; x < endX; x++)
                    {
                        // Player marker
                        if (x == playerTileX && y == playerTileY)
                        {
                            sb.Append('@');
                            continue;
                        }

                        // Out of bounds
                        if (x < 0 || x >= maxX || y < 0 || y >= maxY)
                        {
                            sb.Append(' ');
                            continue;
                        }

                        sb.Append(SafeClassifyTile(x, y));
                    }

                    sb.Append("\"");
                }

                sb.Append("]");
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorResponse("GetTiles", ex);
            }
        }

        private static char SafeClassifyTile(int x, int y)
        {
            try
            {
                var tile = Main.tile[x, y];
                if (tile == null) return ' ';
                return ClassifyTile(tile);
            }
            catch
            {
                return '?';
            }
        }

        private static char ClassifyTile(Tile tile)
        {
            // Check liquid first (can have liquid on top of or without tiles)
            if (tile.liquid > 0 && !tile.active())
            {
                int lType = tile.liquidType();
                switch (lType)
                {
                    case 0: return '~'; // water
                    case 1: return 'L'; // lava
                    case 2: return 'H'; // honey
                    case 3: return 'S'; // shimmer
                    default: return '~';
                }
            }

            if (!tile.active())
                return '.';

            ushort type = tile.type;

            // Platform (TileID.Platforms = 19)
            if (type == 19) return '=';

            // Doors (TileID.ClosedDoor = 10, OpenDoor = 11)
            if (type == 10 || type == 11) return 'D';

            // Chests (TileID.Containers = 21)
            if (type == 21) return 'C';

            // Workbenches (TileID.WorkBenches = 18)
            if (type == 18) return 'T';

            // Any other solid foreground tile
            return '#';
        }

        #endregion

        #region UI State (GS-004)

        /// <summary>
        /// Current UI and menu state.
        /// </summary>
        public static string GetUIState()
        {
            try
            {
                var sb = new StringBuilder(1024);
                sb.Append("{");

                bool inMenu = false;
                try { inMenu = Main.gameMenu; } catch { }
                sb.Append("\"inMenu\": ").Append(inMenu ? "true" : "false").Append(", ");

                if (inMenu)
                {
                    int mode = 0;
                    try { mode = Main.menuMode; } catch { }
                    sb.Append("\"menuMode\": ").Append(mode).Append(", ");
                    sb.Append("\"menuDescription\": \"").Append(EscapeJson(DescribeMenuMode(mode))).Append("\"");
                    sb.Append("}");
                    return sb.ToString();
                }

                // In-game UI state
                bool invOpen = false;
                try { invOpen = Main.playerInventory; } catch { }
                sb.Append("\"inventoryOpen\": ").Append(invOpen ? "true" : "false").Append(", ");

                bool chatOpen = false;
                try { chatOpen = Main.drawingPlayerChat; } catch { }
                sb.Append("\"chatOpen\": ").Append(chatOpen ? "true" : "false").Append(", ");

                string chatText = "";
                try { chatText = Main.chatText ?? ""; } catch { }
                sb.Append("\"chatText\": \"").Append(EscapeJson(chatText)).Append("\", ");

                // NPC dialog
                var player = Game.LocalPlayer;
                int talkNpc = -1;
                if (player != null)
                {
                    try { talkNpc = player.talkNPC; } catch { }
                }
                sb.Append("\"talkingToNPC\": ").Append(talkNpc >= 0 ? "true" : "false").Append(", ");

                if (talkNpc >= 0)
                {
                    string npcName = "";
                    try { npcName = Main.npc[talkNpc].GivenOrTypeName ?? ""; } catch { }
                    sb.Append("\"talkNPCName\": \"").Append(EscapeJson(npcName)).Append("\", ");

                    string npcText = "";
                    try { npcText = Main.npcChatText ?? ""; } catch { }
                    sb.Append("\"npcChatText\": \"").Append(EscapeJson(npcText)).Append("\", ");
                }

                // Container state
                int chest = -1;
                if (player != null)
                {
                    try { chest = player.chest; } catch { }
                }
                sb.Append("\"chestOpen\": ").Append(chest >= 0 ? "true" : "false").Append(", ");
                sb.Append("\"chestIndex\": ").Append(chest).Append(", ");

                // Edit states
                bool editSign = false;
                bool editChest = false;
                try { editSign = Main.editSign; } catch { }
                try { editChest = Main.editChest; } catch { }
                sb.Append("\"editingSign\": ").Append(editSign ? "true" : "false").Append(", ");
                sb.Append("\"editingChest\": ").Append(editChest ? "true" : "false");

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorResponse("GetUIState", ex);
            }
        }

        private static string DescribeMenuMode(int mode)
        {
            switch (mode)
            {
                case 0: return "title_screen";
                case 1: return "character_select";
                case 2: return "new_character";
                case 3: return "character_name";
                case 5: return "character_deletion_confirm";
                case 6: return "world_select";
                case 7: return "world_name";
                case 10: return "loading";
                case 11: return "settings";
                case 12: return "multiplayer";
                case 13: return "server_ip";
                case 14: return "multiplayer_connecting";
                case 15: return "disconnected";
                case 16: return "world_size_select";
                case 888: return "fancy_ui";
                default: return "unknown_" + mode;
            }
        }

        #endregion

        #region Surroundings (GS-005)

        /// <summary>
        /// Combined game state snapshot: player, inventory summary, entities, tiles, UI, world.
        /// </summary>
        public static string GetSurroundings()
        {
            try
            {
                if (!Game.InWorld)
                    return NotInWorld();

                var player = Game.LocalPlayer;
                if (player == null)
                    return NotInWorld();

                var playerPos = Game.PlayerPosition;

                var sb = new StringBuilder(8192);
                sb.Append("{");

                // Player summary
                sb.Append("\"player\": {");
                try
                {
                    sb.Append("\"name\": \"").Append(EscapeJson(player.name ?? "")).Append("\", ");
                    sb.Append("\"health\": ").Append(player.statLife).Append(", ");
                    sb.Append("\"maxHealth\": ").Append(player.statLifeMax2).Append(", ");
                    sb.Append("\"mana\": ").Append(player.statMana).Append(", ");
                    sb.Append("\"maxMana\": ").Append(player.statManaMax2).Append(", ");
                    sb.Append("\"defense\": ").Append(player.statDefense).Append(", ");
                    sb.Append("\"dead\": ").Append(player.dead ? "true" : "false").Append(", ");
                    sb.Append("\"tileX\": ").Append((int)(playerPos.X / 16f)).Append(", ");
                    sb.Append("\"tileY\": ").Append((int)(playerPos.Y / 16f)).Append(", ");
                    sb.Append("\"selectedSlot\": ").Append(player.selectedItem).Append(", ");
                    sb.Append("\"heldItem\": ");
                    AppendItem(sb, player.HeldItem);
                }
                catch
                {
                    // Player state partially read - close what we have
                    sb.Append("\"error\": \"player state unavailable\"");
                }
                sb.Append("}, ");

                // Inventory summary: just count non-empty slots + notable items
                int filledSlots = 0;
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        var inv = player.inventory;
                        if (inv != null && i < inv.Length && inv[i] != null && inv[i].type != 0)
                            filledSlots++;
                    }
                }
                catch { }
                sb.Append("\"inventorySummary\": {");
                sb.Append("\"filledSlots\": ").Append(filledSlots).Append(", ");
                sb.Append("\"totalSlots\": 50");
                sb.Append("}, ");

                // Nearby entities (5 hostile, 5 friendly for compact response)
                AppendCompactEntities(sb, playerPos);

                // Tile grid (smaller for surroundings: 40x24)
                sb.Append(", \"tileGrid\": ");
                AppendTileGridRaw(sb, playerPos, 40, 24);

                // UI state
                sb.Append(", \"ui\": {");
                bool invOpen = false;
                try { invOpen = Main.playerInventory; } catch { }
                sb.Append("\"inventoryOpen\": ").Append(invOpen ? "true" : "false").Append(", ");

                bool chatOpen = false;
                try { chatOpen = Main.drawingPlayerChat; } catch { }
                sb.Append("\"chatOpen\": ").Append(chatOpen ? "true" : "false").Append(", ");

                int talkNpc = -1;
                try { talkNpc = player.talkNPC; } catch { }
                sb.Append("\"talkingToNPC\": ").Append(talkNpc >= 0 ? "true" : "false").Append(", ");

                int chest = -1;
                try { chest = player.chest; } catch { }
                sb.Append("\"chestOpen\": ").Append(chest >= 0 ? "true" : "false");
                sb.Append("}, ");

                // World context
                AppendWorldContext(sb);

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorResponse("GetSurroundings", ex);
            }
        }

        private static void AppendWorldContext(StringBuilder sb)
        {
            sb.Append("\"world\": {");
            try
            {
                string worldName = "";
                try { worldName = Main.worldName ?? ""; } catch { }
                sb.Append("\"name\": \"").Append(EscapeJson(worldName)).Append("\", ");

                bool dayTime = false;
                try { dayTime = Main.dayTime; } catch { }
                sb.Append("\"dayTime\": ").Append(dayTime ? "true" : "false").Append(", ");

                // Time as human-readable
                double time = 0;
                try { time = Main.time; } catch { }
                int hours, minutes;
                if (dayTime)
                {
                    double totalSeconds = time / 60.0;
                    hours = 4 + (int)(totalSeconds / 3600.0);
                    minutes = (int)((totalSeconds % 3600.0) / 60.0);
                }
                else
                {
                    double totalSeconds = time / 60.0;
                    hours = 19 + (int)(totalSeconds / 3600.0);
                    minutes = (int)((totalSeconds % 3600.0) / 60.0);
                }
                if (hours >= 24) hours -= 24;
                sb.Append("\"timeOfDay\": \"").Append(hours.ToString("D2")).Append(":").Append(minutes.ToString("D2")).Append("\", ");

                bool hardMode = false;
                try { hardMode = Main.hardMode; } catch { }
                sb.Append("\"hardMode\": ").Append(hardMode ? "true" : "false").Append(", ");

                bool bloodMoon = false;
                try { bloodMoon = Main.bloodMoon; } catch { }
                sb.Append("\"bloodMoon\": ").Append(bloodMoon ? "true" : "false").Append(", ");

                bool eclipse = false;
                try { eclipse = Main.eclipse; } catch { }
                sb.Append("\"eclipse\": ").Append(eclipse ? "true" : "false").Append(", ");

                bool raining = false;
                try { raining = Main.raining; } catch { }
                sb.Append("\"raining\": ").Append(raining ? "true" : "false");
            }
            catch
            {
                sb.Append("\"error\": \"world state unavailable\"");
            }
            sb.Append("}");
        }

        private static void ScanNpcs(List<NpcInfo> hostiles, List<NpcInfo> friendlies, Vector2 playerPos, float rangeSquared)
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var npc = Main.npc[i];
                    if (npc == null || !npc.active) continue;

                    var npcPos = npc.position;

                    float dx = npcPos.X - playerPos.X;
                    float dy = npcPos.Y - playerPos.Y;
                    float distSq = dx * dx + dy * dy;

                    if (distSq > rangeSquared) continue;

                    float dist = (float)Math.Sqrt(distSq);
                    string name = "";
                    try { name = npc.GivenOrTypeName ?? ""; } catch { }

                    var info = new NpcInfo
                    {
                        Type = npc.type,
                        Name = name,
                        Life = npc.life,
                        LifeMax = npc.lifeMax,
                        Distance = dist,
                        DistanceTiles = dist / 16f,
                        RelX = dx / 16f,
                        RelY = dy / 16f,
                        Friendly = npc.friendly,
                        IsBoss = npc.boss
                    };

                    if (npc.friendly)
                        friendlies.Add(info);
                    else
                        hostiles.Add(info);
                }
                catch
                {
                    // Skip NPCs that throw during state read
                }
            }
        }

        private static void AppendCompactEntities(StringBuilder sb, Vector2 playerPos)
        {
            var hostiles = new List<NpcInfo>();
            var friendlies = new List<NpcInfo>();

            ScanNpcs(hostiles, friendlies, playerPos, 800f * 800f);

            hostiles.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            friendlies.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            sb.Append("\"nearbyHostile\": [");
            int cap = Math.Min(hostiles.Count, 5);
            for (int i = 0; i < cap; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendNpcInfo(sb, hostiles[i]);
            }
            sb.Append("], ");

            sb.Append("\"nearbyFriendly\": [");
            cap = Math.Min(friendlies.Count, 5);
            for (int i = 0; i < cap; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendNpcInfo(sb, friendlies[i]);
            }
            sb.Append("]");
        }

        private static void AppendTileGridRaw(StringBuilder sb, Vector2 playerPos, int width, int height)
        {
            int playerTileX = (int)(playerPos.X / 16f);
            int playerTileY = (int)(playerPos.Y / 16f);

            int halfW = width / 2;
            int halfH = height / 2;
            int startX = playerTileX - halfW;
            int startY = playerTileY - halfH;
            int endX = startX + width;
            int endY = startY + height;

            int maxX = Game.MaxTilesX;
            int maxY = Game.MaxTilesY;

            sb.Append("{");
            sb.Append("\"playerTileX\": ").Append(playerTileX).Append(", ");
            sb.Append("\"playerTileY\": ").Append(playerTileY).Append(", ");
            sb.Append("\"gridStartX\": ").Append(startX).Append(", ");
            sb.Append("\"gridStartY\": ").Append(startY).Append(", ");
            sb.Append("\"width\": ").Append(width).Append(", ");
            sb.Append("\"height\": ").Append(height).Append(", ");
            sb.Append("\"grid\": [");

            for (int y = startY; y < endY; y++)
            {
                if (y > startY) sb.Append(", ");
                sb.Append("\"");

                for (int x = startX; x < endX; x++)
                {
                    if (x == playerTileX && y == playerTileY)
                    {
                        sb.Append('@');
                        continue;
                    }

                    if (x < 0 || x >= maxX || y < 0 || y >= maxY)
                    {
                        sb.Append(' ');
                        continue;
                    }

                    sb.Append(SafeClassifyTile(x, y));
                }

                sb.Append("\"");
            }

            sb.Append("]");
            sb.Append("}");
        }

        #endregion

        #region Helpers

        private struct NpcInfo
        {
            public int Type;
            public string Name;
            public int Life;
            public int LifeMax;
            public float Distance;
            public float DistanceTiles;
            public float RelX;
            public float RelY;
            public bool Friendly;
            public bool IsBoss;
        }

        private static void AppendItem(StringBuilder sb, Item item)
        {
            if (item == null || item.type == 0)
            {
                sb.Append("null");
                return;
            }
            sb.Append("{");
            AppendItemFields(sb, item);
            sb.Append("}");
        }

        private static void AppendItemFields(StringBuilder sb, Item item)
        {
            sb.Append("\"type\": ").Append(item.type).Append(", ");

            string name = "";
            try { name = item.Name ?? ""; } catch { }
            sb.Append("\"name\": \"").Append(EscapeJson(name)).Append("\", ");

            sb.Append("\"stack\": ").Append(item.stack);
            sb.Append(", \"maxStack\": ").Append(item.maxStack);

            if (item.damage > 0)
                sb.Append(", \"damage\": ").Append(item.damage);

            if (item.defense > 0)
                sb.Append(", \"defense\": ").Append(item.defense);

            try
            {
                if (item.accessory)
                    sb.Append(", \"accessory\": true");
            }
            catch { }

            // Modded item info (v3: uses ItemRegistry type lookup)
            try
            {
                if (ItemRegistry.IsCustomItem(item.type))
                {
                    string fullId = ItemRegistry.GetFullId(item.type);
                    sb.Append(", \"modded\": true");
                    if (fullId != null)
                    {
                        int colonIdx = fullId.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            sb.Append(", \"modId\": \"").Append(EscapeJson(fullId.Substring(0, colonIdx))).Append("\"");
                            sb.Append(", \"itemName\": \"").Append(EscapeJson(fullId.Substring(colonIdx + 1))).Append("\"");
                        }
                        sb.Append(", \"fullId\": \"").Append(EscapeJson(fullId)).Append("\"");
                    }
                }
            }
            catch { }
        }

        private static void AppendNpcInfo(StringBuilder sb, NpcInfo info)
        {
            sb.Append("{");
            sb.Append("\"type\": ").Append(info.Type).Append(", ");
            sb.Append("\"name\": \"").Append(EscapeJson(info.Name)).Append("\", ");
            sb.Append("\"life\": ").Append(info.Life).Append(", ");
            sb.Append("\"lifeMax\": ").Append(info.LifeMax).Append(", ");
            sb.Append("\"distTiles\": ").Append((int)info.DistanceTiles).Append(", ");
            sb.Append("\"relX\": ").Append((int)info.RelX).Append(", ");
            sb.Append("\"relY\": ").Append((int)info.RelY);
            if (info.IsBoss)
                sb.Append(", \"boss\": true");
            sb.Append("}");
        }

        private static string NotInWorld()
        {
            return "{\"error\": \"Not in a world\", \"inWorld\": false}";
        }

        private static string ErrorResponse(string method, Exception ex)
        {
            string msg = "";
            try { msg = ex.GetType().Name + ": " + (ex.Message ?? ""); } catch { msg = "unknown error"; }
            return "{\"error\": \"" + EscapeJson(method + " failed: " + msg) + "\"}";
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        #endregion
    }
}
