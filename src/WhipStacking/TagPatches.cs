using Terraria;
using Terraria.GameContent.Items;

namespace WhipStacking
{
    internal static class TagPatches
    {
        internal static bool Enabled = true;

        public static void UpdateEquips_Postfix(Player __instance)
        {
            if (Enabled && __instance != null)
            {
                // Vanilla accessories add capacity during UpdateEquips. Apply the native
                // ceiling afterward so they retain other bonuses without exceeding the array.
                __instance.maxTagEffects = TagEffectStack.MaxEffects;
            }
        }
    }
}
