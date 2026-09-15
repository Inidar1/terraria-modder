using System;
using System.Collections.Generic;
using TerrariaModder.Core.Config;

namespace BiomeSpread
{
    public class BiomeSpreadConfig : ModConfig
    {
        public override int Version => 2;

        protected override void Migrate(Dictionary<string, object> raw, int fromVersion)
        {
            if (fromVersion >= 2) return;
            foreach (var entry in raw)
            {
                if (string.Equals(entry.Key, "Enabled", StringComparison.OrdinalIgnoreCase) &&
                    entry.Value is bool enabled && !enabled)
                    DisableSpread = false;
            }
        }

        [Client, Label("Disable Biome Spread"), Description("Prevent corruption, crimson, and hallow from spreading to new tiles. Crystal shards and chlorophyte are unaffected.")]
        public bool DisableSpread { get; set; } = true;
    }
}
