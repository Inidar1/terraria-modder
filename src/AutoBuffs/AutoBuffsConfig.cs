using TerrariaModder.Core.Config;

namespace AutoBuffs
{
    public class AutoBuffsConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Automatically apply buffs from nearby buff stations, including the Dead Cells Potion Station.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Scan Radius"), Description("How far (in tiles) to scan for buff furniture around the player. Higher values find more furniture but use more CPU."), Range(5, 100)]
        public int ScanRadius { get; set; } = 40;

        [Client, Label("Crystal Ball"), Description("Apply the Crystal Ball buff when nearby.")]
        public bool CrystalBall { get; set; } = true;

        [Client, Label("Ammo Box"), Description("Apply the Ammo Box buff when nearby.")]
        public bool AmmoBox { get; set; } = true;

        [Client, Label("Bewitching Table"), Description("Apply the Bewitching Table buff when nearby.")]
        public bool BewitchingTable { get; set; } = true;

        [Client, Label("Sharpening Station"), Description("Apply the Sharpening Station buff when nearby.")]
        public bool SharpeningStation { get; set; } = true;

        [Client, Label("War Table"), Description("Apply the War Table buff when nearby.")]
        public bool WarTable { get; set; } = true;

        [Client, Label("Slice of Cake"), Description("Apply the Slice of Cake buff when nearby.")]
        public bool SliceOfCake { get; set; } = true;

        [Client, Label("Dead Cells Potion Station"), Description("Apply the potion-duration buff from a Dead Cells Potion Station when nearby.")]
        public bool DeadCellsPotionStation { get; set; } = true;

        [Client, Label("Debug Logging"), Description("Log buff scan details to the log file. Useful for troubleshooting.")]
        public bool DebugLogging { get; set; } = false;
    }
}
