using System.Collections.Generic;
using Terraria.GameContent.Items;

namespace WhipStacking
{
    /// <summary>
    /// Per-whip tag tracking entry. Each whip the player has used gets its own
    /// NPC timer arrays, replacing vanilla's single shared arrays.
    /// </summary>
    internal class WhipTagEntry
    {
        public int WhipType;
        public UniqueTagEffect Effect;
        public int[] TimeLeftOnNPC;     // size 200 (Main.maxNPCs)
        public int[] ProcTimeLeftOnNPC; // size 200 (Main.maxNPCs)

        public WhipTagEntry(int whipType, UniqueTagEffect effect, int maxNPCs)
        {
            WhipType = whipType;
            Effect = effect;
            TimeLeftOnNPC = new int[maxNPCs];
            ProcTimeLeftOnNPC = new int[maxNPCs];
        }

        public bool HasAnyActiveTags()
        {
            for (int i = 0; i < TimeLeftOnNPC.Length; i++)
            {
                if (TimeLeftOnNPC[i] > 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Global multi-tag state for all players. Indexed by player.whoAmI.
    /// Each player gets a dictionary mapping whip item type -> WhipTagEntry.
    /// </summary>
    internal static class MultiTagState
    {
        // 255 = Main.maxPlayers
        internal static Dictionary<int, WhipTagEntry>[] PlayerWhips = new Dictionary<int, WhipTagEntry>[255];

        internal static Dictionary<int, WhipTagEntry> GetOrCreate(int playerIndex)
        {
            if (PlayerWhips[playerIndex] == null)
                PlayerWhips[playerIndex] = new Dictionary<int, WhipTagEntry>();
            return PlayerWhips[playerIndex];
        }

        internal static void Reset()
        {
            for (int i = 0; i < PlayerWhips.Length; i++)
            {
                PlayerWhips[i] = null;
            }
        }
    }
}
