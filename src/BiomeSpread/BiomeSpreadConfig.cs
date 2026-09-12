using TerrariaModder.Core.Config;

namespace BiomeSpread
{
    public class BiomeSpreadConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable or disable this mod.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Disable Evil Spread"), Description("When enabled, prevents corruption, crimson, and hallow from spreading to new tiles. Crystal shards and chlorophyte are unaffected.")]
        public bool DisableSpread { get; set; } = true;
    }
}
