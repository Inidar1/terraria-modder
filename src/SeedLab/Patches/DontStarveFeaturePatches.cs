using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Terraria;
using Terraria.GameContent;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>Route independent DST effects without changing the global seed flag mid-frame.</summary>
    internal static class DontStarveFeaturePatches
    {
        private static FeatureManager _manager;
        private static readonly FieldInfo Flag = AccessTools.Field(typeof(Main), nameof(Main.dontStarveWorld));

        internal static void Apply(Harmony harmony, FeatureManager manager, ILogger log)
        {
            _manager = manager;
            Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.Update), new[] { typeof(int) }), nameof(DarknessTranspiler));
            Patch(harmony, AccessTools.Method(typeof(Main), "SetBackColor"), nameof(LightingTranspiler));
            Patch(harmony, AccessTools.Method(typeof(DontStarveSeed), nameof(DontStarveSeed.FixBiomeDarkness)), nameof(LightingTranspiler));
            Patch(harmony, AccessTools.Method(typeof(Player), "PlayDeathSound"), nameof(DeathSoundTranspiler));
            log.Info("[SeedLab] Independent darkness, night lighting and death-sound predicates patched");
        }

        private static void Patch(Harmony harmony, MethodInfo target, string transpiler)
        {
            if (target == null) throw new MissingMethodException("Missing DST feature target for " + transpiler);
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DontStarveFeaturePatches), transpiler));
        }

        private static bool Feature(bool vanilla, string id) => _manager != null && _manager.Initialized
            ? _manager.IsFeatureEnabled(id) : vanilla;
        private static bool Darkness(bool vanilla) => Feature(vanilla, "ds_darkness_damage");
        private static bool Lighting(bool vanilla) => Feature(vanilla, "ds_night_darkness");
        private static bool DeathSound(bool vanilla) => Feature(vanilla, "ds_death_sound");

        private static IEnumerable<CodeInstruction> DarknessTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var update = AccessTools.Method(typeof(DontStarveDarknessDamageDealer), nameof(DontStarveDarknessDamageDealer.Update));
            int call = code.FindIndex(i => i.Calls(update));
            if (call < 0 || code.Count(i => i.Calls(update)) != 1)
                throw new InvalidOperationException("Unexpected darkness update call sites");
            for (int i = call - 1; i >= Math.Max(0, call - 8); i--)
            {
                if (!code[i].LoadsField(Flag)) continue;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DontStarveFeaturePatches), nameof(Darkness))));
                return code;
            }
            throw new InvalidOperationException("Darkness seed predicate was not found beside its native call");
        }

        private static IEnumerable<CodeInstruction> LightingTranspiler(IEnumerable<CodeInstruction> instructions) => ReplaceFlags(instructions, nameof(Lighting));
        private static IEnumerable<CodeInstruction> DeathSoundTranspiler(IEnumerable<CodeInstruction> instructions) => ReplaceFlags(instructions, nameof(DeathSound));

        private static IEnumerable<CodeInstruction> ReplaceFlags(IEnumerable<CodeInstruction> instructions, string predicate)
        {
            int count = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!instruction.LoadsField(Flag)) continue;
                count++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DontStarveFeaturePatches), predicate));
            }
            if (count == 0) throw new InvalidOperationException("Missing native DST predicate for " + predicate);
        }
    }
}
