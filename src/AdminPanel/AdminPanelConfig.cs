using TerrariaModder.Core.Config;

namespace AdminPanel
{
    public class AdminPanelConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable the admin panel."), RestartRequired]
        public bool Enabled { get; set; } = true;

        // Runtime state saved by the panel (not user-facing settings, stored in config for persistence)
        [Client, Label("God Mode"), Description("Start with god mode active.")]
        public bool GodMode { get; set; } = false;

        [Client, Label("Time Speed"), Description("Time speed multiplier (1-60x)."), Range(1, 60)]
        public int TimeSpeed { get; set; } = 1;

        [Client, Label("Normal Respawn Index"), Description("Index into respawn time presets for normal death."), Range(0, 8)]
        public int NormalRespawnIndex { get; set; } = 4;

        [Client, Label("Boss Respawn Index"), Description("Index into respawn time presets for boss fight death."), Range(0, 8)]
        public int BossRespawnIndex { get; set; } = 4;

        [Client, Label("Move Speed"), Description("Movement speed multiplier (1-10x)."), Range(1, 10)]
        public int MoveSpeed { get; set; } = 1;

        [Client, Label("Boss Favourites"), Description("Comma-separated list of favourite boss NPC IDs.")]
        public string BossFavourites { get; set; } = "";

        [Client, Label("NPC Favourites"), Description("Comma-separated list of favourite NPC IDs.")]
        public string NpcFavourites { get; set; } = "";

        [Client, Label("Right-Click Spawn"), Description("Enable right-click to spawn NPCs.")]
        public bool RightClickSpawn { get; set; } = false;
    }
}
