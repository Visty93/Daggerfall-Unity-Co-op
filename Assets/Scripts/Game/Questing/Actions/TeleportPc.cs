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

using System;
using UnityEngine;
using System.Text.RegularExpressions;
using FullSerializer;
using DaggerfallConnect;
using DaggerfallConnect.Utility;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Utility;
using Mirror;

namespace DaggerfallWorkshop.Game.Questing
{
    /// <summary>
    /// Partial implementation.
    /// Teleport player to a dungeon for dungeon traps, or as part of main quest.
    /// Does not exactly emulate classic for "transfer pc inside" variant. This is only used in Sx016.
    /// </summary>
    public class TeleportPc : ActionTemplate
    {
        Symbol targetPlace;
        int targetMarker = -1;

        bool resumePending = false;
        Vector3 resumePosition = Vector3.zero;

        // Multiplayer network-dungeon teleport state.
        // TeleportPc is a classic/SP quest action, but MP dungeons live at a host-authored
        // vertical slot (DaggerfallDungeon.PositionY). In MP we must enter/request the
        // network dungeon first, then apply the quest marker position relative to the
        // actual spawned dungeon transform.
        bool networkDungeonResumePending = false;
        string networkDungeonRegionName = string.Empty;
        string networkDungeonLocationName = string.Empty;
        float networkDungeonResumeStartedAt = 0f;
        const float NetworkDungeonResumeTimeout = 20f;

        public override string Pattern
        {
            get { return @"teleport pc to (?<aPlace>[a-zA-Z0-9_.-]+)|" +
                         @"transfer pc inside (?<aPlace>[a-zA-Z0-9_.-]+) marker (?<marker>\d+)"; }
        }

        public TeleportPc(Quest parentQuest)
            : base(parentQuest)
        {
        }

        public override IQuestAction CreateNew(string source, Quest parentQuest)
        {
            // Source must match pattern
            Match match = Test(source);
            if (!match.Success)
                return null;

            // Factory new action
            TeleportPc action = new TeleportPc(parentQuest);
            action.targetPlace = new Symbol(match.Groups["aPlace"].Value);
            if (match.Groups["marker"].Success)
                action.targetMarker = Parser.ParseInt(match.Groups["marker"].Value);

            return action;
        }

        public override void RearmAction()
        {
            base.RearmAction();

            // If the action is disabled then rearmed, then teleport called before resumePending is finished, then player can desync from world
            // Lower the resumePending flag as this will be a new instance of teleport
            resumePending = false;
            networkDungeonResumePending = false;
            networkDungeonRegionName = string.Empty;
            networkDungeonLocationName = string.Empty;
            networkDungeonResumeStartedAt = 0f;
        }

        public override void Update(Task caller)
        {
            base.Update(caller);

            // Do nothing while player respawning
            if (GameManager.Instance.PlayerEnterExit.IsRespawning)
                return;

            // Multiplayer dungeon teleport: wait until the network-aware dungeon entry
            // path has created/synced/entered the actual dungeon, then perform the final
            // quest-marker snap relative to that dungeon's transform/Y slot.
            if (networkDungeonResumePending)
            {
                if (TryFinishNetworkDungeonTeleport())
                {
                    networkDungeonResumePending = false;
                    SetComplete();
                }
                return;
            }

            // Handle resume on next tick of action after respawn process complete
            if (resumePending)
            {
                GameObject player = GameManager.Instance.PlayerObject;
                player.transform.position = resumePosition;
                resumePending = false;
                SetComplete();
                return;
            }

            // Create SiteLink if not already present
            if (!QuestMachine.HasSiteLink(ParentQuest, targetPlace))
                QuestMachine.CreateSiteLink(ParentQuest, targetPlace);

            // Attempt to get Place resource
            Place place = ParentQuest.GetPlace(targetPlace);
            if (place == null)
                return;

            // Get selected spawn QuestMarker for this Place
            bool usingMarker = false;
            QuestMarker marker = new QuestMarker();
            if (targetMarker >= 0 && targetMarker < place.SiteDetails.questSpawnMarkers.Length)
            {
                marker = place.SiteDetails.questSpawnMarkers[targetMarker];
                usingMarker = true;
            }

            // Attempt to get location data - using GetLocation(regionName, locationName) as it can support all locations
            DFLocation location;
            if (!DaggerfallUnity.Instance.ContentReader.GetLocation(place.SiteDetails.regionName, place.SiteDetails.locationName, out location))
                return;

            // Spawn inside dungeon at this world position
            DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel((int)location.MapTableData.Longitude, location.MapTableData.Latitude);
            DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);

            // Determine target quest-marker position in dungeon-local space.
            if (!usingMarker)
                marker = place.SiteDetails.questSpawnMarkers[0];

            Vector3 dungeonBlockPosition = new Vector3(marker.dungeonX * RDBLayout.RDBSide, 0, marker.dungeonZ * RDBLayout.RDBSide);
            Vector3 markerLocalPosition = dungeonBlockPosition + marker.flatPosition;

            bool multiplayerActive = NetworkServer.active || NetworkClient.active;
            if (multiplayerActive)
            {
                StartNetworkDungeonTeleport(location, worldPos, markerLocalPosition);
                return;
            }

            // Original single-player behavior.
            GameManager.Instance.PlayerEnterExit.RespawnPlayer(
                worldPos.X,
                worldPos.Y,
                true,
                true);

            resumePosition = markerLocalPosition;
            resumePending = true;
        }

        void StartNetworkDungeonTeleport(DFLocation location, DFPosition worldPos, Vector3 markerLocalPosition)
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit == null)
                return;

            // TeleportPc originally uses the dungeon location map-pixel origin, which can be
            // hundreds of Unity units away from the actual exterior dungeon entrance. Normal
            // manual dungeon entry/exit uses StreamingWorld.PositionPlayerToDungeonExit(),
            // which resolves the real lowest DungeonEntrance door. Build that exact coordinate
            // now, before requesting/generating the network dungeon, so both host and clients
            // publish the same DF X/Z as manual dungeon entry.
            int exactEntranceWorldX;
            int exactEntranceWorldZ;
            Vector3 exactEntranceLocal;
            if (DaggerfallWorkshop.StreamingWorld.TryGetDungeonEntranceWorldCoordinates(location, out exactEntranceWorldX, out exactEntranceWorldZ, out exactEntranceLocal))
            {
                Debug.Log($"[TeleportPcMP][ExactEntrance] Replacing coarse teleport world={worldPos.X}/{worldPos.Y} with exact entrance world={exactEntranceWorldX}/{exactEntranceWorldZ} localEntrance={exactEntranceLocal}");
                worldPos = new DFPosition(exactEntranceWorldX, exactEntranceWorldZ);
            }
            else
            {
                Debug.LogWarning($"[TeleportPcMP][ExactEntrance] Could not calculate exact dungeon entrance for '{location.RegionName}/{location.Name}'. Falling back to coarse world={worldPos.X}/{worldPos.Y}.");
            }

            resumePosition = markerLocalPosition;
            networkDungeonRegionName = location.RegionName;
            networkDungeonLocationName = location.Name;
            networkDungeonResumeStartedAt = Time.realtimeSinceStartup;
            networkDungeonResumePending = true;

            // Pure clients must NOT run the local dungeon transition path here.
            // They need the same path as clicked dungeon entry:
            //   client Command -> host creates/sends dungeon data -> TargetRpc -> DelayedDungeonSpawn -> WaitForDungeonReady -> TransitionDungeonInterior.
            // The TargetRpc carries markerLocalPosition and registers it again immediately before the delayed spawn.
            bool pureClient = NetworkClient.active && !NetworkServer.active;
            if (pureClient)
            {
                // Register locally too as a fallback, but the important registration happens again in TargetEnterTeleportPcDungeon.
                playerEnterExit.RegisterMultiplayerQuestDungeonTeleportMarker(
                    location,
                    markerLocalPosition,
                    "TeleportPc-client-before-host-request");

                // Update the lightweight MP coordinate before the request, but do not run TransitionDungeonInterior locally.
                // The actual dungeon state flags are set later by the normal client enter path.
                playerEnterExit.PrepareMultiplayerQuestDungeonTeleportWorldPosition(
                    worldPos.X,
                    worldPos.Y,
                    "TeleportPc-client-before-host-request");
                ForceSendLocalCoordinates("teleportpc-client-before-host-request");

                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer == null)
                {
                    Debug.LogError("[TeleportPcMP] Pure client could not find local PlayerMultiplayer for TeleportPc dungeon request.");
                    return;
                }

                Debug.Log($"[TeleportPcMP] Pure client requesting host dungeon '{location.RegionName}/{location.Name}' world={worldPos.X}/{worldPos.Y} markerLocal={markerLocalPosition}");
                localPlayer.RequestTeleportPcDungeonFromHost(location, markerLocalPosition, worldPos.X, worldPos.Y, "TeleportPc-client");
                return;
            }

            // Host/server path: enter immediately through the existing network dungeon transition path.
            // This is the branch that already worked better for the host.
            playerEnterExit.PrepareMultiplayerQuestDungeonTeleportWorldPosition(
                worldPos.X,
                worldPos.Y,
                "TeleportPc-host-before-dungeon-request");

            playerEnterExit.RegisterMultiplayerQuestDungeonTeleportMarker(
                location,
                markerLocalPosition,
                "TeleportPc-host-before-dungeon-request");

            ForceSendLocalCoordinates("teleportpc-host-before-dungeon-request");

            StaticDoor dummyDoor = new StaticDoor();
            Debug.Log($"[TeleportPcMP] Host/server requesting/entering network dungeon '{location.RegionName}/{location.Name}' world={worldPos.X}/{worldPos.Y} markerLocal={markerLocalPosition}");
            playerEnterExit.TransitionDungeonInterior(null, dummyDoor, location, true);
        }

        bool TryFinishNetworkDungeonTeleport()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit == null || playerEnterExit.IsRespawning)
                return false;

            if (Time.realtimeSinceStartup - networkDungeonResumeStartedAt > NetworkDungeonResumeTimeout)
            {
                Debug.LogError($"[TeleportPcMP] Timed out waiting for network dungeon '{networkDungeonRegionName}/{networkDungeonLocationName}'. Completing action to avoid quest softlock.");
                return true;
            }

            DaggerfallDungeon dungeon = playerEnterExit.Dungeon;
            if (dungeon == null || !playerEnterExit.IsPlayerInsideDungeon || !dungeon.isSet)
                return false;

            if (!string.Equals(dungeon.Summary.RegionName, networkDungeonRegionName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(dungeon.Summary.LocationName, networkDungeonLocationName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!playerEnterExit.TryMovePlayerToDungeonQuestMarker(resumePosition, "TeleportPc-final-marker"))
                return false;

            ForceSendLocalCoordinates("teleportpc-dungeon-final-marker");

            // The destination dungeon is now fully active on this exact machine.
            // Prompt-choice item refresh can happen before a pure client's network dungeon
            // exists, leaving placed quest items absent only for the player who initiated
            // the teleport. Re-run only quest-item injection now that the real destination
            // is ready. AddQuestResourceObjects already de-duplicates existing resources.
            try
            {
                global::QuestNetSync.NotifyLocalQuestTeleportDestinationReady(
                    ParentQuest != null ? ParentQuest.UID : 0UL,
                    "teleportpc-dungeon-ready");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TeleportPcMP] Destination quest-item refresh failed: {ex.Message}");
            }

            Debug.Log($"[TeleportPcMP] Completed network dungeon teleport to '{networkDungeonRegionName}/{networkDungeonLocationName}' markerLocal={resumePosition}");
            return true;
        }

        void ForceSendLocalCoordinates(string reason)
        {
            try
            {
                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer == null)
                    return;

                GameObject player = GameManager.Instance.PlayerObject;
                if (player != null)
                    localPlayer.transform.position = player.transform.position;

                PositionMultiplayer positionMultiplayer = localPlayer.GetComponent<PositionMultiplayer>();
                if (positionMultiplayer != null)
                    positionMultiplayer.ForceSendCurrentCoordinatesNow(reason);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TeleportPcMP] Failed to force-send coordinates. reason={reason} error={ex.Message}");
            }
        }

        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public Symbol targetPlace;
            public int targetMarker;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.targetPlace = targetPlace;
            data.targetMarker = targetMarker;

            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            targetPlace = data.targetPlace;
            targetMarker = data.targetMarker;
        }

        #endregion
    }
}