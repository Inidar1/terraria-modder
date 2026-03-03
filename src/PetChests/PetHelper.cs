using Terraria;
using Terraria.ID;
using Microsoft.Xna.Framework;

namespace PetChests
{
    /// <summary>
    /// Helper class for identifying cosmetic pet projectiles.
    /// </summary>
    public static class PetHelper
    {
        // Chester = 960, Flying Piggy Bank = 525 - skip these, vanilla handles them
        public const int CHESTER_PROJ_TYPE = 960;
        public const int FLYING_PIGGY_BANK_PROJ_TYPE = 525;

        /// <summary>
        /// Check if a projectile is a cosmetic pet (not a light pet, not Chester)
        /// </summary>
        public static bool IsCosmeticPet(Projectile proj)
        {
            if (proj == null) return false;
            if (!proj.active) return false;

            int type = proj.type;

            // Bounds check
            if (type < 0 || type >= Main.projPet.Length)
                return false;

            // Must be a pet
            if (!Main.projPet[type])
                return false;

            // Must NOT be a light pet
            if (type < ProjectileID.Sets.LightPet.Length && ProjectileID.Sets.LightPet[type])
                return false;

            // Skip Chester and Flying Piggy Bank
            if (type == CHESTER_PROJ_TYPE || type == FLYING_PIGGY_BANK_PROJ_TYPE)
                return false;

            return true;
        }

        public static int GetType(Projectile proj)
        {
            if (proj == null) return -1;
            return proj.type;
        }

        public static bool IsActive(Projectile proj)
        {
            if (proj == null) return false;
            return proj.active;
        }

        public static int GetOwner(Projectile proj)
        {
            if (proj == null) return -1;
            return proj.owner;
        }

        public static int GetWidth(Projectile proj)
        {
            if (proj == null) return 0;
            return proj.width;
        }

        public static int GetHeight(Projectile proj)
        {
            if (proj == null) return 0;
            return proj.height;
        }

        public static bool GetPosition(Projectile proj, out float x, out float y)
        {
            x = 0;
            y = 0;
            if (proj == null) return false;

            x = proj.position.X;
            y = proj.position.Y;
            return true;
        }

        public static bool GetCenter(Projectile proj, out float centerX, out float centerY)
        {
            centerX = 0;
            centerY = 0;

            float x, y;
            if (!GetPosition(proj, out x, out y)) return false;

            centerX = x + proj.width / 2f;
            centerY = y + proj.height / 2f;
            return true;
        }

        public static bool IsPointInHitbox(Projectile proj, float worldX, float worldY)
        {
            if (proj == null) return false;

            float x = proj.position.X;
            float y = proj.position.Y;

            return worldX >= x && worldX <= x + proj.width &&
                   worldY >= y && worldY <= y + proj.height;
        }
    }
}
