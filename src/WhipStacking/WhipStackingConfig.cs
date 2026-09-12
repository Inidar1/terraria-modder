using TerrariaModder.Core.Config;

namespace WhipStacking
{
    public class WhipStackingConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable Terraria's native maximum of five simultaneous whip tag effects.")]
        public bool Enabled { get; set; } = true;
    }
}
