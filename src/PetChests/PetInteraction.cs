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
        private static int _boundPetIdentity = -1;
        private static int _boundPetType = -1;
        private static int _closeCooldown = 0;  // Prevents immediate reopen after close
        private const int CLOSE_COOLDOWN_FRAMES = 10;
        private const int STACK_SPLIT_DELAY = 600;     // Delay before stack splitting is allowed
        private const int NO_THROW_VALUE = 4;          // Frames to prevent item throw after opening

        public static bool IsKeepingPiggyOpen() => _keepPiggyOpen;

        /// <summary>
        /// Returns true if pet should NOT be interactible (either open or just closed)
        /// </summary>
        public static bool ShouldBlockInteraction() => _keepPiggyOpen || _closeCooldown > 0;

        /// <summary>Consume only the pet-close click; inventory and item input remain native.</summary>
        public static void BlockInputInPrefix(Player player)
        {
            if (!_keepPiggyOpen) return;
            bool mouseRight = Main.mouseRight;
            if (mouseRight && !_lastMouseRight && !player.mouseInterface && !player.lastMouseInterface &&
                _boundPetIndex >= 0 && CheckClickOnBoundPet(player))
            {
                ClosePiggyBank(player);
                Main.mouseRightRelease = false;
                player.releaseUseTile = false;
                return;
            }
            _lastMouseRight = mouseRight;
        }

        private static bool CheckClickOnBoundPet(Player player)
        {
            try
            {
                Projectile boundPet = Main.projectile[_boundPetIndex];
                if (!boundPet.active || (int)boundPet.key != _boundPetIdentity || boundPet.type != _boundPetType) return false;

                float mouseWorldX, mouseWorldY;
                if (!GetMouseWorldPosition(out mouseWorldX, out mouseWorldY)) return false;

                return PetHelper.IsPointInHitbox(boundPet, mouseWorldX, mouseWorldY);
            }
            catch
            {
                return false;
            }
        }

        public static void Reset(bool closeChest = false)
        {
            if (closeChest && _keepPiggyOpen && Main.LocalPlayer != null && Main.LocalPlayer.chest == -2)
            {
                Main.LocalPlayer.chest = -1;
                Main.LocalPlayer.piggyBankProjTracker.Clear();
            }
            _keepPiggyOpen = false;
            _boundPetIndex = -1;
            _boundPetIdentity = -1;
            _boundPetType = -1;
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

                if (clickedPet == null || player.mouseInterface || player.lastMouseInterface) return;

                _framesSinceLastInteraction = 0;
                _keepPiggyOpen = true;
                _boundPetIndex = clickedIndex;
                _boundPetIdentity = (int)clickedPet.key;
                _boundPetType = clickedPet.type;

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

            // A different container or native close takes precedence over our previous binding.
            if (player.chest != -2)
            {
                Reset();
                return;
            }

            // Check if bound pet is still valid
            if (_boundPetIndex >= 0)
            {
                Projectile boundPet = Main.projectile[_boundPetIndex];

                if (!boundPet.active || boundPet.owner != whoAmI || (int)boundPet.key != _boundPetIdentity ||
                    boundPet.type != _boundPetType || !PetHelper.IsCosmeticPet(boundPet))
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

                // Current vanilla tracks the exact projectile identity, including cosmetic pets.
                // Keep native bank/peer state intact; our range guard only supplies the configured range.
                player.piggyBankProjTracker.Set(pet);

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
            _boundPetIdentity = -1;
            _boundPetType = -1;
            _lastMouseRight = false;  // Reset to prevent stale state causing false reopen after cooldown
            _closeCooldown = CLOSE_COOLDOWN_FRAMES;  // Prevent immediate reopen
            if (player.chest == -2)
            {
                player.chest = -1;
                player.piggyBankProjTracker.Clear();
                SoundEngine.PlaySound(11);
            }
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

                // Account for reverse gravity (gravDir == -1):
                // Vanilla Main.MouseWorld flips the Y coordinate when gravity is reversed.
                // Without this, the click position is mirrored vertically, causing
                // interaction checks to target the wrong world position.
                if (Main.player[Main.myPlayer].gravDir == -1f)
                    y = screenPos.Y + (float)Main.screenHeight - (float)Main.mouseY;
                else
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
