// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using System;
using Mirror;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using FullSerializer;

namespace DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects
{
    /// <summary>
    /// Teleport
    /// </summary>
    public class Teleport : BaseEntityEffect
    {
        public static readonly string EffectKey = "Teleport-Effect";

        #region Fields

        // Constants
        const int teleportOrSetAnchor = 4000;
        const int achorMustBeSet = 4001;

        // Anchor is now stored in PlayerEntity
        // Below field is maintained for backwards compatibility with old save files
        // If non-null this anchor data will be migrated to PlayerEntity on load and the effect will end
        PlayerPositionData_v1 anchorPosition;

        // Volatile references
        SerializablePlayer serializablePlayer = null;
        PlayerEnterExit playerEnterExit = null;

        #endregion

        #region Overrides

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ClassicKey = MakeClassicKey(43, 255);
            properties.AllowedTargets = EntityEffectBroker.TargetFlags_Self;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.SpellMaker;
            properties.ShowSpellIcon = false;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
        }

        public override string GroupName => TextManager.Instance.GetLocalizedText("teleport");
        public override TextFile.Token[] SpellMakerDescription => DaggerfallUnity.Instance.TextProvider.GetRSCTokens(1602);
        public override TextFile.Token[] SpellBookDescription => DaggerfallUnity.Instance.TextProvider.GetRSCTokens(1302);

        public override void Start(EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Start(manager, caster);
            CacheReferences();
            PromptPlayer();
        }

        public override void End()
        {
            anchorPosition = null;
            RoundsRemaining = 0;
            base.End();
        }

        #endregion

        #region Private Methods

        void PromptPlayer()
        {
            // Get peered entity gameobject
            DaggerfallEntityBehaviour entityBehaviour = GetPeeredEntityBehaviour(manager);
            if (!entityBehaviour)
                return;

            // Target must be player - no effect on other entities
            if (entityBehaviour != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Prompt for outcome
            DaggerfallMessageBox mb = new DaggerfallMessageBox(DaggerfallUI.Instance.UserInterfaceManager, DaggerfallMessageBox.CommonMessageBoxButtons.AnchorTeleport, teleportOrSetAnchor, DaggerfallUI.Instance.UserInterfaceManager.TopWindow);
            // QoL, does not match classic. No magicka refund, though
            mb.AllowCancel = true;
            mb.OnButtonClick += EffectActionPrompt_OnButtonClick;
            mb.Show();
        }

        void SetAnchor()
        {
            // Validate references
            if (!serializablePlayer || !playerEnterExit)
                return;

            // Get position information
            PlayerPositionData_v1 newAnchorPosition = serializablePlayer.GetPlayerPositionData();
            if (playerEnterExit.IsPlayerInsideBuilding)
            {
                newAnchorPosition.exteriorDoors = playerEnterExit.ExteriorDoors;
                newAnchorPosition.buildingDiscoveryData = playerEnterExit.BuildingDiscoveryData;
            }

            // Assign anchor to player entity then end effect
            GameManager.Instance.PlayerEntity.AnchorPosition = newAnchorPosition;
            End();
        }

        void TeleportPlayer()
        {
            // Validate references
            if (!serializablePlayer || !playerEnterExit)
                return;

            // Get anchor position from player entity
            anchorPosition = GameManager.Instance.PlayerEntity.AnchorPosition;

            // Is player in same interior as anchor?
            if (IsSameInterior())
            {
                if (IsMultiplayerDungeonRecall())
                {
                    // A dungeon may be recreated in a different XYZ grid slot since the
                    // Recall anchor was set. Restore the saved LOCAL point in the live
                    // dungeon, never the historical scene-space position.
                    DaggerfallDungeon liveDungeon = playerEnterExit.Dungeon;
                    Vector3 localPosition = GetDungeonRecallLocalPosition(anchorPosition);
                    bool savedSmaller = anchorPosition.smallerDungeonsState == QuestSmallerDungeonsState.Enabled;
                    bool layoutSettingChanged = savedSmaller != DaggerfallUnity.Settings.SmallerDungeons;
                    if (layoutSettingChanged && !DaggerfallDungeon.IsMainStoryDungeon(liveDungeon.Summary.ID) &&
                        liveDungeon.StartMarker != null)
                    {
                        // Preserve vanilla's safety fallback when the local dungeon layout
                        // may have changed since the marker was placed.
                        playerEnterExit.transform.position = liveDungeon.StartMarker.transform.position;
                        DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("smallerDungeonsChanged"));
                    }
                    else
                    {
                        playerEnterExit.transform.position = liveDungeon.transform.position + localPosition;
                    }
                    RestoreDungeonRecallOrientation(anchorPosition);
                    GameManager.Instance.PlayerEnterExit.PlayerTeleportedIntoDungeon = true;
                    Debug.Log($"[RecallMP] Same live dungeon. grid={liveDungeon.transform.position} local={localPosition} final={playerEnterExit.transform.position}");
                }
                else
                {
                    // Preserve original singleplayer and building Recall behaviour.
                    serializablePlayer.RestorePosition(anchorPosition);
                }
                GameManager.Instance.PlayerEntity.AnchorPosition = null;
            }
            else
            {
                // When teleporting to interior anchor, restore world compensation height early before initworld
                // Ensures exterior world level is aligned with building height at time of anchor
                // Only works with floating origin v3 saves and above with both serialized world compensation and context
                if (anchorPosition.worldContext == WorldContext.Interior)
                    GameManager.Instance.StreamingWorld.RestoreWorldCompensationHeight(anchorPosition.worldCompensation.y);
                else
                    GameManager.Instance.StreamingWorld.RestoreWorldCompensationHeight(0);

                // Cache scene before departing
                if (!playerEnterExit.IsPlayerInside)
                    SaveLoadManager.CacheScene(GameManager.Instance.StreamingWorld.SceneName);      // Player is outside
                else if (playerEnterExit.IsPlayerInsideBuilding)
                    SaveLoadManager.CacheScene(playerEnterExit.Interior.name);                      // Player inside a building
                else // Player inside a dungeon
                    playerEnterExit.TransitionDungeonExteriorImmediate();

                // Need to load some other part of the world again - player could be anywhere
                PlayerEnterExit.OnRespawnerComplete += PlayerEnterExit_OnRespawnerComplete;
                playerEnterExit.RestorePositionHelper(anchorPosition, false, true);

                // Restore building summary data
                if (anchorPosition.insideBuilding)
                    playerEnterExit.BuildingDiscoveryData = anchorPosition.buildingDiscoveryData;

                // Network dungeon conversion remains black until its authoritative
                // dungeon has arrived and the final local-position snap has completed.
                // For every other path preserve the original fade behaviour.
                if (!IsMultiplayerDungeonRecall() || !playerEnterExit.IsNetworkDungeonConversionInProgress)
                    DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack();
            }
        }

        #endregion

        #region Helpers

        // Cache required references
        bool CacheReferences()
        {
            // Get peered SerializablePlayer and PlayerEnterExit
            if (!serializablePlayer)
                serializablePlayer = caster.GetComponent<SerializablePlayer>();

            if (!playerEnterExit)
                playerEnterExit = caster.GetComponent<PlayerEnterExit>();

            if (!serializablePlayer || !playerEnterExit)
            {
                Debug.LogError("Teleport effect could not find both SerializablePlayer and PlayerEnterExit components.");
                return false;
            }

            return true;
        }

        // Only dungeon Recall in an active network session needs grid-aware handling.
        // Neither SP Recall nor building Recall passes through the new code paths.
        bool IsMultiplayerDungeonRecall()
        {
            return anchorPosition != null && anchorPosition.insideDungeon &&
                (NetworkClient.active || NetworkServer.active);
        }

        // Same local-coordinate fallback as PlayerEnterExit.GetSavedDungeonLocalPosition().
        // New anchors include a root-relative point; older MP anchors may have only
        // an original dungeon grid origin, and old SP anchors use the Y=0 scene origin.
        Vector3 GetDungeonRecallLocalPosition(PlayerPositionData_v1 savedAnchor)
        {
            if (savedAnchor.hasSavedDungeonLocalPosition)
                return savedAnchor.savedDungeonLocalPosition;

            if (savedAnchor.savedInsideNetworkDungeon)
                return savedAnchor.position - savedAnchor.GetSavedDungeonOrigin();

            return savedAnchor.position;
        }

        // Conversion already placed the player at the correct live dungeon position.
        // Apply only the original Recall camera direction and crouch state here.
        void RestoreDungeonRecallOrientation(PlayerPositionData_v1 savedAnchor)
        {
            PlayerMouseLook mouseLook = GameManager.Instance.PlayerMouseLook;
            if (mouseLook != null)
                mouseLook.SetFacing(savedAnchor.yaw, savedAnchor.pitch);

            PlayerMotor motor = GameManager.Instance.PlayerMotor;
            if (motor != null)
                motor.IsCrouching = savedAnchor.isCrouching;
        }

        // Checks if player is in same building or dungeon interior as anchor
        bool IsSameInterior()
        {
            // Reject if outside or anchor not set
            if (!playerEnterExit.IsPlayerInside || anchorPosition == null)
                return false;

            // Test depends on if player is inside a building or a dungeon
            if (playerEnterExit.IsPlayerInsideBuilding && anchorPosition.insideBuilding)
            {
                // Compare building key
                if (anchorPosition.buildingDiscoveryData.buildingKey == playerEnterExit.BuildingDiscoveryData.buildingKey)
                {
                    // Also compare map pixel, in case we're unlucky https://forums.dfworkshop.net/viewtopic.php?f=24&t=2018
                    DaggerfallConnect.Utility.DFPosition anchorMapPixel = DaggerfallConnect.Arena2.MapsFile.WorldCoordToMapPixel(anchorPosition.worldPosX, anchorPosition.worldPosZ);
                    DaggerfallConnect.Utility.DFPosition playerMapPixel = GameManager.Instance.PlayerGPS.CurrentMapPixel;
                    if (anchorMapPixel.X == playerMapPixel.X && anchorMapPixel.Y == playerMapPixel.Y)
                        return true;
                }
            }
            else if (playerEnterExit.IsPlayerInsideDungeon && anchorPosition.insideDungeon)
            {
                if (IsMultiplayerDungeonRecall())
                {
                    // The original map-pixel-only test is not sufficient for the
                    // multiplayer grid. Verify the actual live network dungeon identity.
                    DaggerfallDungeon liveDungeon = playerEnterExit.Dungeon;
                    if (liveDungeon == null || !liveDungeon.isSet || !liveDungeon.IsNetworkDungeonInstance)
                        return false;

                    if (anchorPosition.savedDungeonMapId != 0 &&
                        anchorPosition.savedDungeonMapId != liveDungeon.Summary.ID)
                        return false;

                    if (!string.IsNullOrEmpty(anchorPosition.savedDungeonRegionName) &&
                        !string.Equals(anchorPosition.savedDungeonRegionName, liveDungeon.Summary.RegionName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (!string.IsNullOrEmpty(anchorPosition.savedDungeonLocationName) &&
                        !string.Equals(anchorPosition.savedDungeonLocationName, liveDungeon.Summary.LocationName, StringComparison.OrdinalIgnoreCase))
                        return false;

                    // New anchors include full dungeon identity. PlayerGPS may retain
                    // stale exterior coordinates through asynchronous network entry,
                    // so do not require a map-pixel match once identity is established.
                    if (!string.IsNullOrEmpty(anchorPosition.savedDungeonRegionName) &&
                        !string.IsNullOrEmpty(anchorPosition.savedDungeonLocationName))
                    {
                        GameManager.Instance.PlayerEnterExit.PlayerTeleportedIntoDungeon = true;
                        return true;
                    }
                }

                // Legacy anchors (and SP) retain the original map-pixel comparison.
                DaggerfallConnect.Utility.DFPosition anchorMapPixel = DaggerfallConnect.Arena2.MapsFile.WorldCoordToMapPixel(anchorPosition.worldPosX, anchorPosition.worldPosZ);
                DaggerfallConnect.Utility.DFPosition playerMapPixel = GameManager.Instance.PlayerGPS.CurrentMapPixel;
                if (anchorMapPixel.X == playerMapPixel.X && anchorMapPixel.Y == playerMapPixel.Y)
                {
                    GameManager.Instance.PlayerEnterExit.PlayerTeleportedIntoDungeon = true;
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Event Handlers

        private void PlayerEnterExit_OnRespawnerComplete()
        {
            // Must have a caster and it must be the player
            if (caster == null || caster != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Get peered SerializablePlayer and PlayerEnterExit if they haven't been cached yet
            if (!CacheReferences())
                return;

            // Get anchor position from player entity
            anchorPosition = GameManager.Instance.PlayerEntity.AnchorPosition;

            // Only the current network dungeon is authoritative for the destination
            // slot. TryCompleteNetworkDungeonConversion() has already placed the player
            // at dungeon.transform.position + savedDungeonLocalPosition, including X/Y/Z.
            // The vanilla RestorePosition() call here would UNDO that move by restoring
            // the old raw absolute position (the cause of the 0/-500/-800 mistakes).
            if (IsMultiplayerDungeonRecall())
            {
                PlayerEnterExit.OnRespawnerComplete -= PlayerEnterExit_OnRespawnerComplete;
                DaggerfallDungeon liveDungeon = playerEnterExit.Dungeon;
                if (!playerEnterExit.IsPlayerInsideDungeon || liveDungeon == null ||
                    !liveDungeon.isSet || !liveDungeon.IsNetworkDungeonInstance)
                {
                    // Dungeon construction/request failed. Leave the safe exterior
                    // fallback alone instead of teleporting the player into empty space.
                    // Preserve the Recall anchor so it can be attempted again.
                    Debug.LogWarning("[RecallMP] Destination network dungeon was not ready at respawn completion. Keeping saved Recall anchor and safe fallback position.");
                    End();
                    return;
                }

                RestoreDungeonRecallOrientation(anchorPosition);
                Debug.Log($"[RecallMP] Arrived in live dungeon. grid={liveDungeon.transform.position} local={GetDungeonRecallLocalPosition(anchorPosition)} final={playerEnterExit.transform.position}");
            }
            else
            {
                // Original SP and building teleport handling remains unchanged.
                serializablePlayer.RestorePosition(anchorPosition);
                PlayerEnterExit.OnRespawnerComplete -= PlayerEnterExit_OnRespawnerComplete;
            }

            // Set "teleported into dungeon" flag when anchor is inside a dungeon
            GameManager.Instance.PlayerEnterExit.PlayerTeleportedIntoDungeon = anchorPosition.insideDungeon;

            // Restore scene cache on arrival
            if (!playerEnterExit.IsPlayerInside)
                SaveLoadManager.RestoreCachedScene(GameManager.Instance.StreamingWorld.SceneName);      // Player is outside
            else if (playerEnterExit.IsPlayerInsideBuilding)
                SaveLoadManager.RestoreCachedScene(playerEnterExit.Interior.name);                      // Player inside a building

            GameManager.Instance.PlayerEntity.AnchorPosition = null;
            End();
        }

        private void EffectActionPrompt_OnButtonClick(DaggerfallMessageBox sender, DaggerfallMessageBox.MessageBoxButtons messageBoxButton)
        {
            sender.CloseWindow();

            if (messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Anchor)
            {
                SetAnchor();
            }
            else if (messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Teleport)
            {
                if (GameManager.Instance.PlayerEntity.AnchorPosition == null)
                {
                    DaggerfallMessageBox mb = new DaggerfallMessageBox(DaggerfallUI.Instance.UserInterfaceManager, DaggerfallUI.Instance.UserInterfaceManager.TopWindow);
                    mb.SetTextTokens(achorMustBeSet);
                    mb.ClickAnywhereToClose = true;
                    mb.Show();
                    return;
                }
                TeleportPlayer();
            }
        }

        #endregion

        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public bool anchorSet;
            public PlayerPositionData_v1 anchorPosition;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.anchorPosition = anchorPosition;

            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            anchorPosition = data.anchorPosition;

            // On resume migrate legacy anchor position to player entity then end effect
            if (anchorPosition != null)
            {
                GameManager.Instance.PlayerEntity.AnchorPosition = anchorPosition;
                End();
            }
        }

        #endregion
    }
}
