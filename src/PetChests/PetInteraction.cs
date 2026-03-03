using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;

namespace PetChests
{
    /// <summary>
    /// Handles pet interaction - right-click to open/close piggy bank.
    /// </summary>
    public static class PetInteraction
    {
        // State
        private static bool _lastMouseRight = false;
        private static int _framesSinceLastInteraction = 9999;
        private const int INTERACTION_COOLDOWN = 15;
        private static bool _keepPiggyOpen = false;
        private static int _boundPetIndex = -1;
        private static int _closeCooldown = 0;  // Prevents immediate reopen after close
        private const int CLOSE_COOLDOWN_FRAMES = 10;
        private const float SOUND_MUTE_RANGE = 120f;  // Distance in pixels to mute projectile sounds
        private const int STACK_SPLIT_DELAY = 600;     // Delay before stack splitting is allowed
        private const int NO_THROW_VALUE = 4;          // Frames to prevent item throw after opening

        public static bool IsKeepingPiggyOpen() => _keepPiggyOpen;

        /// <summary>
        /// Returns true if pet should NOT be interactible (either open or just closed)
        /// </summary>
        public static bool ShouldBlockInteraction() => _keepPiggyOpen || _closeCooldown > 0;

        /// <summary>
        /// Block input in prefix (before vanilla processes) to prevent clicking sounds.
        /// Also handles right-click to close before blocking.
        /// </summary>
        public static void BlockInputInPrefix(Player player)
        {
            // If in close cooldown, still block input to prevent reopen
            if (_closeCooldown > 0)
            {
                BlockAllInput(player);
                return;
            }

            if (!_keepPiggyOpen) return;

            try
            {
                // First check if user right-clicked to close (BEFORE we block mouseRight)
                bool mouseRight = Main.mouseRight;
                if (mouseRight && !_lastMouseRight)
                {
                    // Rising edge - user just clicked
                    // Check if clicking on bound pet to close
                    if (_boundPetIndex >= 0 && CheckClickOnBoundPet(player))
                    {
                        ClosePiggyBank(player);
                        // Set to false so HandleInteraction doesn't see a false "mouse release"
                        // (we blocked mouseRight to false, so !false && true would trigger open)
                        _lastMouseRight = false;
                        // STILL block input after close to prevent vanilla reopen
                        BlockAllInput(player);
                        return;
                    }
                }
                _lastMouseRight = mouseRight;

                BlockAllInput(player);
            }
            catch { }
        }

        private static void BlockAllInput(Player player)
        {
            try
            {
                // Block all use-related input before vanilla gets to process it
                player.controlUseItem = false;
                player.controlUseTile = false;
                player.releaseUseItem = true;
                player.releaseUseTile = true;
                player.noItems = true;
                Main.mouseRight = false;
                Main.mouseRightRelease = false;

                // Block vanilla projectile interactions
                Player.BlockInteractionWithProjectiles = 3;

                // Also mute projectiles in prefix to prevent sounds during Player.Update
                MuteNearbyProjectiles(player);
            }
            catch { }
        }

        private static bool CheckClickOnBoundPet(Player player)
        {
            try
            {
                Projectile boundPet = Main.projectile[_boundPetIndex];
                if (!boundPet.active) return false;

                float mouseWorldX, mouseWorldY;
                if (!GetMouseWorldPosition(out mouseWorldX, out mouseWorldY)) return false;

                return PetHelper.IsPointInHitbox(boundPet, mouseWorldX, mouseWorldY);
            }
            catch
            {
                return false;
            }
        }

        public static void Reset()
        {
            _keepPiggyOpen = false;
            _boundPetIndex = -1;
            _lastMouseRight = false;
            _framesSinceLastInteraction = 9999;
            _closeCooldown = 0;
        }

        public static void SetInteractableFlags(Player player)
        {
            try
            {
                int whoAmI = player.whoAmI;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (!proj.active) continue;
                    if (proj.owner != whoAmI) continue;

                    if (PetHelper.IsCosmeticPet(proj))
                    {
                        Main.CurrentFrameFlags.HadAnActiveInteractableProjectile = true;
                        return;
                    }
                }
            }
            catch { }
        }

        public static void HandleInteraction(Player player)
        {
            _framesSinceLastInteraction++;
            if (_closeCooldown > 0) _closeCooldown--;

            // Don't process any new interactions during close cooldown
            if (_closeCooldown > 0) return;

            try
            {
                int whoAmI = player.whoAmI;

                // Handle keep-open state
                if (_keepPiggyOpen)
                {
                    HandleKeepOpenState(player, whoAmI);
                    return;
                }

                // Detect mouse release (falling edge)
                bool mouseRight = Main.mouseRight;
                bool mouseReleased = !mouseRight && _lastMouseRight;
                _lastMouseRight = mouseRight;

                if (!mouseReleased) return;
                if (_framesSinceLastInteraction < INTERACTION_COOLDOWN) return;

                // Get mouse world position
                float mouseWorldX, mouseWorldY;
                if (!GetMouseWorldPosition(out mouseWorldX, out mouseWorldY)) return;

                // Get player center
                float playerCenterX = player.position.X + player.width / 2f;
                float playerCenterY = player.position.Y + player.height / 2f;

                // Find clicked pet
                Projectile clickedPet = null;
                int clickedIndex = -1;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (!proj.active) continue;
                    if (proj.owner != whoAmI) continue;

                    if (!PetHelper.IsCosmeticPet(proj)) continue;

                    if (PetHelper.IsPointInHitbox(proj, mouseWorldX, mouseWorldY))
                    {
                        float petCenterX, petCenterY;
                        if (PetHelper.GetCenter(proj, out petCenterX, out petCenterY))
                        {
                            float dist = Distance(playerCenterX, playerCenterY, petCenterX, petCenterY);
                            if (dist <= Mod.InteractionRange)
                            {
                                clickedPet = proj;
                                clickedIndex = i;
                                break;
                            }
                        }
                    }
                }

                if (clickedPet == null) return;

                _framesSinceLastInteraction = 0;
                _keepPiggyOpen = true;
                _boundPetIndex = clickedIndex;

                OpenPiggyBank(player, clickedPet);

                // Block vanilla from processing this click
                player.mouseInterface = true;
            }
            catch (Exception ex)
            {
                Mod.Log($"Interaction error: {ex.Message}");
            }
        }

        private static void HandleKeepOpenState(Player player, int whoAmI)
        {
            // Check if inventory was closed (user pressed ESC) - do this FIRST before blocking input
            bool invOpen = Main.playerInventory;
            if (!invOpen)
            {
                // User closed inventory via ESC
                ClosePiggyBank(player);
                return;
            }

            // CRITICAL: Block all input to prevent clicking sounds
            // Match the tModLoader version exactly
            player.controlUseItem = false;
            player.controlUseTile = false;
            player.releaseUseItem = true;  // Tell game button was released
            player.releaseUseTile = true;
            player.itemAnimation = 0;
            player.itemTime = 0;
            player.noItems = true;  // Prevent item use
            Main.mouseRight = false;
            Main.mouseRightRelease = false;

            // Keep chest open (in case game logic tried to close it)
            if (player.chest != -2)
            {
                player.chest = -2;
                // Re-clear tracker - vanilla may have reset it
                ClearPiggyBankTracker(player);
            }

            // Mute all nearby projectiles to prevent sounds
            MuteNearbyProjectiles(player);

            // Check if bound pet is still valid
            if (_boundPetIndex >= 0)
            {
                Projectile boundPet = Main.projectile[_boundPetIndex];

                if (!boundPet.active || boundPet.owner != whoAmI)
                {
                    ClosePiggyBank(player);
                    return;
                }

                // Check distance
                float playerCX = player.position.X + player.width / 2f;
                float playerCY = player.position.Y + player.height / 2f;
                float petCX, petCY;
                if (PetHelper.GetCenter(boundPet, out petCX, out petCY))
                {
                    float dist = Distance(playerCX, playerCY, petCX, petCY);
                    if (dist > Mod.InteractionRange)
                    {
                        ClosePiggyBank(player);
                        return;
                    }
                }
            }
        }

        private static void MuteNearbyProjectiles(Player player)
        {
            try
            {
                int whoAmI = player.whoAmI;
                float playerCX = player.position.X + player.width / 2f;
                float playerCY = player.position.Y + player.height / 2f;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (!proj.active) continue;
                    if (proj.owner != whoAmI) continue;

                    float petCX, petCY;
                    if (PetHelper.GetCenter(proj, out petCX, out petCY))
                    {
                        float dist = Distance(playerCX, playerCY, petCX, petCY);
                        if (dist <= SOUND_MUTE_RANGE)
                        {
                            proj.soundDelay = int.MaxValue;
                        }
                    }
                }
            }
            catch { }
        }

        private static void OpenPiggyBank(Player player, Projectile pet)
        {
            try
            {
                player.chest = -2;

                // Set chest position to pet location
                float petCenterX, petCenterY;
                if (PetHelper.GetCenter(pet, out petCenterX, out petCenterY))
                {
                    player.chestX = (int)(petCenterX / 16f);
                    player.chestY = (int)(petCenterY / 16f);
                }

                // Clear talk NPC
                player.SetTalkNPC(-1);

                // Open inventory
                Main.playerInventory = true;

                // Set stack split delay
                Main.stackSplit = STACK_SPLIT_DELAY;

                // IMPORTANT: Do NOT set piggy bank tracker to our pet!
                // Vanilla validates that tracked projectile is type 525 or 960.
                // If not, it sets chest=-1 and plays close sound every frame.
                // Instead, clear the tracker so validation is skipped.
                ClearPiggyBankTracker(player);

                // Play open sound
                SoundEngine.PlaySound(10);

                // Prevent item throw
                player.noThrow = NO_THROW_VALUE;
            }
            catch (Exception ex)
            {
                Mod.Log($"OpenPiggyBank error: {ex.Message}");
            }
        }

        private static void ClosePiggyBank(Player player)
        {
            _keepPiggyOpen = false;
            _boundPetIndex = -1;
            _closeCooldown = CLOSE_COOLDOWN_FRAMES;  // Prevent immediate reopen
            player.chest = -1;
            SoundEngine.PlaySound(11);
        }

        /// <summary>
        /// Clear the piggy bank tracker so vanilla doesn't try to validate our pet.
        /// Vanilla checks if tracked projectile is type 525 or 960 - if not, it closes chest every frame.
        /// </summary>
        private static void ClearPiggyBankTracker(Player player)
        {
            try
            {
                player.piggyBankProjTracker.Clear();
            }
            catch { }
        }

        private static bool GetMouseWorldPosition(out float x, out float y)
        {
            x = 0;
            y = 0;

            try
            {
                Vector2 mouseScreen = Main.MouseScreen;
                Vector2 screenPos = Main.screenPosition;

                x = mouseScreen.X + screenPos.X;
                y = mouseScreen.Y + screenPos.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
