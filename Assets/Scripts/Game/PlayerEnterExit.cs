// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors: Numidium
// 
// Notes:
//

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Utility.AssetInjection;
using Mirror;
using System.Linq;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Assist player controller to enter and exit building interiors and dungeons.
    /// Should be attached to player object with PlayerGPS for climate tracking.
    /// </summary>
    public class PlayerEnterExit : NetworkBehaviour
    {
        const HideFlags defaultHideFlags = HideFlags.None;

        // In multiplayer we keep building interiors vertically separated from the exterior
        // world while still using the normal non-networked/SP interior layout path.
        const float multiplayerInteriorYOffset = -250f;
        bool currentInteriorUsesMultiplayerYOffset = false;

        // Some interior-related mods can perform delayed placement after OnTransitionInterior.
        // MP building interiors are shifted far below exterior Y, so a placement search that uses
        // exterior-height coordinates can incorrectly choose an upper floor. Briefly protect a
        // positively validated entry landing, but relinquish that protection as soon as the player
        // materially moves away from the entrance. This keeps the guard transition-specific and
        // prevents normal interior traversal such as stairs or ladders from being treated as a bad snap.
        const float multiplayerInteriorLandingGuardDuration = 8f;
        const float multiplayerInteriorLandingGuardMinUpwardSnap = 1.5f;
        const float multiplayerInteriorLandingGuardMinInstantDistance = 2.5f;
        const float multiplayerInteriorLandingGuardMaxEntryHorizontalDistance = 0.75f;
        const float multiplayerInteriorLandingGuardMaxEntryVerticalDistance = 0.75f;
        Coroutine multiplayerInteriorLandingGuardCoroutine = null;

        // Set during load of a save made inside an MP-offset local building interior.
        // This allows the same non-networked interior to be reconstructed at its saved
        // temporary Y offset even when loading that save later in SP.
        bool pendingRecoveredMultiplayerInteriorSave = false;
        float pendingRecoveredMultiplayerInteriorYOffset = multiplayerInteriorYOffset;

        UnderwaterFog underwaterFog;
        DaggerfallUnity dfUnity;
        CharacterController controller;
        bool isCreatingDungeonObjects = false;
        bool isPlayerInside = false;
        bool isPlayerInsideDungeon = false;
        bool isPlayerInsideDungeonCastle = false;
        bool isPlayerInsideSpecialArea = false;
        bool isPlayerInsideOpenShop = false;
        bool isPlayerSwimming = false;
        bool isPlayerSubmerged = false;
        bool isPlayerInSunlight = false;
        bool isPlayerInHolyPlace = false;
        bool isRespawning = false;
        bool lastInteriorStartFlag;
        bool displayAfloatMessage = false;
        public DaggerfallInterior interior;
        DaggerfallDungeon dungeon;
        bool lastNetworkActiveState = false;
        bool emergencyDungeonExitInProgress = false;
        bool emergencyBuildingExitInProgress = false;
        bool interiorNetworkOffsetConversionInProgress = false;
        bool buildingQuestFoeReplayInProgress = false;
        const float BuildingQuestFoeReplayTimeout = 20f;

        // Save/load recovery for saves made inside MP/network dungeons.
        bool pendingRecoveredNetworkDungeonSave = false;
        float pendingRecoveredNetworkDungeonY = 0f;

        // Seamless local/save dungeon -> host-authoritative MP dungeon conversion.
        // While this is pending the source dungeon has been captured/removed, the HUD
        // remains black, and normal network safety must not emergency-exit mid-request.
        bool networkDungeonConversionInProgress = false;
        bool pendingNetworkDungeonConversionFromLoad = false;
        bool pendingNetworkDungeonUseStartMarker = false;
        string pendingNetworkDungeonRegionName = string.Empty;
        string pendingNetworkDungeonLocationName = string.Empty;
        Vector3 pendingNetworkDungeonLocalPosition = Vector3.zero;
        int pendingNetworkDungeonWorldX = 0;
        int pendingNetworkDungeonWorldZ = 0;
        int pendingNetworkDungeonRequesterLevel = 1;
        string pendingNetworkDungeonInitialActionState = string.Empty;
        float pendingNetworkDungeonConversionStartedAt = 0f;
        const float NetworkDungeonConversionTimeout = 30f;

        // Pure-client saved-dungeon loads complete their network conversion before
        // SaveLoadManager restores the remaining serialized object state. Keep one exact
        // authoritative snapshot so the final load phase can reassert the live dungeon Y
        // and player-local position after every ordinary restore callback has finished.
        bool pendingPureClientDungeonLoadFinalization = false;
        DaggerfallDungeon pendingPureClientDungeonLoadDungeon = null;
        string pendingPureClientDungeonLoadRegionName = string.Empty;
        string pendingPureClientDungeonLoadLocationName = string.Empty;
        Vector3 pendingPureClientDungeonLoadLocalPosition = Vector3.zero;
        float pendingPureClientDungeonLoadAuthoritativeY = 0f;
        int pendingPureClientDungeonLoadWorldX = 0;
        int pendingPureClientDungeonLoadWorldZ = 0;

        // TeleportPc MP dungeon trap support.
        // TeleportPc is not a normal clicked dungeon-door transition. It first uses
        // the network dungeon entry path, which moves the player to the normal entry
        // marker, then this pending local marker is applied immediately after that
        // entry move. This avoids relying on the quest action still ticking after
        // TargetEnterDungeon/WaitForDungeonReady completes.
        bool pendingTeleportPcDungeonMarker = false;
        string pendingTeleportPcDungeonRegionName = string.Empty;
        string pendingTeleportPcDungeonLocationName = string.Empty;
        Vector3 pendingTeleportPcDungeonLocalMarker = Vector3.zero;
        float pendingTeleportPcDungeonMarkerRegisteredAt = 0f;

        // Exact exterior dungeon entrance DF world coordinate for the pending/current TeleportPc dungeon.
        // This must be preserved after the player is moved to the quest marker. Do not rebuild it from
        // the dungeon location map pixel, because that is the old coarse coordinate that caused players
        // and enemies to publish different X/Z than manual dungeon entry.
        bool teleportPcDungeonWorldContextActive = false;
        string teleportPcDungeonWorldContextRegionName = string.Empty;
        string teleportPcDungeonWorldContextLocationName = string.Empty;
        int teleportPcDungeonWorldContextX = 0;
        int teleportPcDungeonWorldContextZ = 0;
        bool lastPreparedTeleportPcWorldContextValid = false;
        int lastPreparedTeleportPcWorldContextX = 0;
        int lastPreparedTeleportPcWorldContextZ = 0;

        const float PendingTeleportPcDungeonMarkerTimeout = 30f;

        // Kept only as a compatibility wrapper for older vampire test files.
        // True SP vampire wake-up must use the normal vanilla RespawnPlayer path.

        StreamingWorld world;
        PlayerGPS playerGPS;
        Entity.DaggerfallEntityBehaviour player;
        LevitateMotor levitateMotor;
		DaggerfallInteriorNetwork.InteriorNetworkData data;

        List<StaticDoor> exteriorDoors = new List<StaticDoor>();

        public GameObject ExteriorParent;
        public GameObject InteriorParent;
        public GameObject DungeonParent;
        public DaggerfallLocation OverrideLocation;
		public static Transform realSceneDoorOwner;

        int lastPlayerDungeonBlockIndex = -1;
        DFLocation.DungeonBlock playerDungeonBlockData = new DFLocation.DungeonBlock();

        // Host-side fallback for client-requested network dungeons: use the actual generated
        // water renderer height when classic block WaterLevel disagrees with the visual water.
        int cachedVisualWaterBlockIndex = int.MinValue;
        bool cachedVisualWaterLookupComplete = false;
        bool cachedVisualWaterSurfaceValid = false;
        float cachedVisualWaterSurfaceY = 0f;

        /// <summary>
        /// If different than <c>10000</c> this is the height level of water in current dungeon.
        /// Otherwise player is not inside a dungeon with water.
        /// </summary>
        public short blockWaterLevel = 10000;

        DFLocation.BuildingTypes buildingType = DFLocation.BuildingTypes.None;
        ushort factionID = 0;
        PlayerGPS.DiscoveredBuilding buildingDiscoveryData;

        DaggerfallLocation holidayTextLocation;
        bool holidayTextPrimed = false;
        float holidayTextTimer = 0f;

        /// <summary>
        /// Gets player world context.
        /// </summary>
        public WorldContext WorldContext
        {
            get { return GetWorldContext(); }
        }

        /// <summary>
        /// Gets start flag from most recent interior transition.
        /// Helps inform other systems if first-time load or enter/exit transition
        /// </summary>
        public bool LastInteriorStartFlag
        {
            get { return lastInteriorStartFlag; }
        }

        /// <summary>
        /// True when GameObjectHelper is creating the RDB Base Game Objects
        /// </summary>
        public bool IsCreatingDungeonObjects
        {
            get { return isCreatingDungeonObjects; }
            set { isCreatingDungeonObjects = value; }
        }

        /// <summary>
        /// True when player is inside any structure.
        /// </summary>
        public bool IsPlayerInside
        {
            get { return isPlayerInside; }
        }

        /// <summary>
        /// True only when player is inside a building.
        /// </summary>
        public bool IsPlayerInsideBuilding
        {
            get { return (IsPlayerInside && !IsPlayerInsideDungeon); }
        }

        /// <summary>
        /// True only when player is inside a dungeon.
        /// </summary>
        public bool IsPlayerInsideDungeon
        {
            get { return isPlayerInsideDungeon; }
        }

        /// <summary>
        /// True only when player inside castle blocks of a dungeon.
        /// For example, main hall in Daggerfall castle.
        /// </summary>
        public bool IsPlayerInsideDungeonCastle
        {
            get { return isPlayerInsideDungeonCastle; }
        }

        /// <summary>
        /// True only when player inside special blocks of a dungeon.
        /// For example, treasure room in Daggerfall castle.
        /// </summary>
        public bool IsPlayerInsideSpecialArea
        {
            get { return isPlayerInsideSpecialArea; }
        }

        /// <summary>
        /// True when player is inside an open shop.
        /// Set upon entry, so doesn't matter if shop 'closes' with player inside.
        /// </summary>
        public bool IsPlayerInsideOpenShop
        {
            get { return isPlayerInsideOpenShop; }
            set { isPlayerInsideOpenShop = value; }
        }

        /// <summary>
        /// True when player is inside a tavern.
        /// Set upon entry.
        /// </summary>
        public bool IsPlayerInsideTavern { get; set; }

        /// <summary>
        /// True when player is inside a residence.
        /// Set upon entry.
        /// </summary>
        public bool IsPlayerInsideResidence { get; set; }

        /// <summary>
        /// True when player is swimming in water.
        /// </summary>
        public bool IsPlayerSwimming
        {
            get { return isPlayerSwimming; }
            set { isPlayerSwimming = value; }
        }

        /// <summary>
        /// True when player is submerged in water.
        /// </summary>
        public bool IsPlayerSubmerged
        {
            get { return isPlayerSubmerged; }
        }

        /// <summary>
        /// True when player is in sunlight.
        /// </summary>
        public bool IsPlayerInSunlight
        {
            get { return isPlayerInSunlight; }
        }

        /// <summary>
        /// True when player is in darkness.
        /// Same as !IsPlayerInSunlight.
        /// </summary>
        public bool IsPlayerInDarkness
        {
            get { return !isPlayerInSunlight; }
        }

        /// <summary>
        /// True when player is in a holy place.
        /// Holy places include all Temples and guildhalls of the Fighter Trainers (faction #849)
        /// https://en.uesp.net/wiki/Daggerfall:ClassMaker#Special_Disadvantages
        /// Refreshed once per game minute.
        /// </summary>
        public bool IsPlayerInHolyPlace
        {
            get { return isPlayerInHolyPlace; }
        }

        /// <summary>
        /// True when a player respawn is in progress.
        /// e.g. After loading a game or teleporting back to a marked location.
        /// </summary>
        public bool IsRespawning
        {
            get { return isRespawning; }
        }

        /// <summary>
        /// True while a saved/local dungeon is waiting for the host-authored MP dungeon.
        /// </summary>
        public bool IsNetworkDungeonConversionInProgress
        {
            get { return networkDungeonConversionInProgress; }
        }

        /// <summary>
        /// True when player just teleported into a dungeon via Teleport spell, otherwise false.
        /// Flag is only raised by Teleport spell and is lowered any time player exits a dungeon or interior, or teleports to a non-dungeon anchor.
        /// </summary>
        public bool PlayerTeleportedIntoDungeon { get; set; }

        /// <summary>
        /// Gets current player dungeon.
        /// Only valid when player is inside a dungeon.
        /// </summary>
        public DaggerfallDungeon Dungeon
        {
            get { return dungeon; }
        }

        /// <summary>
        /// Gets information about current player dungeon block.
        /// Only valid when player is inside a dungeon.
        /// </summary>
        public DFLocation.DungeonBlock DungeonBlock
        {
            get { return playerDungeonBlockData; }
        }

        /// <summary>
        /// Gets current building interior.
        /// Only valid when player inside building.
        /// </summary>
        public DaggerfallInterior Interior
        {
            get { return interior; }
        }

        /// <summary>
        /// Gets current building type.
        /// Only valid when player inside building.
        /// </summary>
        public DFLocation.BuildingTypes BuildingType
        {
            get { return buildingType; }
        }

        /// <summary>
        /// Gets current building's faction ID.
        /// </summary>
        public uint FactionID
        {
            get { return factionID; }
        }

        /// <summary>
        /// Gets current building's discovery data.
        /// Only valid when player is inside a building.
        /// This is set every time player enters a building and is saved/loaded with each save game.
        /// Notes:
        ///  Older save games will not carry this data until player exits and enters building again.
        ///  When consuming this property, try to handle empty BuildingDiscoveryData (buildingKey == 0) if possible.
        /// </summary>
        public PlayerGPS.DiscoveredBuilding BuildingDiscoveryData
        {
            get { return buildingDiscoveryData; }
            set { buildingDiscoveryData = value; }
        }

        /// <summary>
        /// Gets or sets exterior doors of current interior.
        /// Returns empty array if player not inside.
        /// </summary>
        public StaticDoor[] ExteriorDoors
        {
            get { return exteriorDoors.ToArray(); }
            set { SetExteriorDoors(value); }
        }

        /// <summary>
        /// Gets the same safe exterior arrival point used by the normal building-exit path,
        /// converted into absolute Daggerfall world coordinates. This is read-only and does
        /// not transition or move the local player. Multiplayer party travel uses this helper
        /// to publish the actual entrance of the building occupied by this player.
        /// </summary>
        public bool TryGetCurrentBuildingExteriorArrivalWorldCoordinates(out int worldX, out int worldZ)
        {
            worldX = 0;
            worldZ = 0;

            if (!IsPlayerInsideBuilding || interior == null || exteriorDoors == null || exteriorDoors.Count == 0)
                return false;

            if (!ReferenceComponents() || world == null)
                return false;

            try
            {
                // Reuse the exact door selection and outward offset used by
                // BuildingTransitionExteriorLogic(). MP interiors are shifted down in Y,
                // so first convert the interior player position back to exterior height.
                StaticDoor closestDoor;
                Vector3 exteriorDoorSearchPosition =
                    GetExteriorDoorSearchPositionFromInteriorPosition(transform.position);
                Vector3 closestDoorPos = DaggerfallStaticDoors.FindClosestDoor(
                    exteriorDoorSearchPosition,
                    ExteriorDoors,
                    out closestDoor);
                Vector3 normal = DaggerfallStaticDoors.GetDoorNormal(closestDoor);

                float radius = controller != null ? controller.radius : 0.5f;
                Vector3 safeExteriorPosition = closestDoorPos + normal * (radius * 3f);

                // StreamingWorld derives PlayerGPS world coordinates from the current
                // map-pixel origin plus the player's scene position using SceneMapRatio.
                // Apply the same conversion to the exterior doorway position.
                DFPosition mapPixelOrigin = MapsFile.MapPixelToWorldCoord(world.MapPixelX, world.MapPixelY);
                worldX = mapPixelOrigin.X + Mathf.RoundToInt(
                    safeExteriorPosition.x * StreamingWorld.SceneMapRatio);
                worldZ = mapPixelOrigin.Y + Mathf.RoundToInt(
                    safeExteriorPosition.z * StreamingWorld.SceneMapRatio);

                return worldX > 0 && worldZ > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PartyFastTravel] Could not calculate current building exterior entrance anchor. error={ex.Message}");
                worldX = 0;
                worldZ = 0;
                return false;
            }
        }

        /// <summary>
        /// Gets instance of UnderwaterFog controlling fog when below the surface of dungeon water.
        /// </summary>
        public UnderwaterFog UnderwaterFog
        {
            get { return underwaterFog; }
        }

        void Awake()
        {
            dfUnity = DaggerfallUnity.Instance;
            playerGPS = GetComponent<PlayerGPS>();
            world = FindObjectOfType<StreamingWorld>();
            player = GameManager.Instance.PlayerEntityBehaviour;
        }

        void Start()
        {
            // Wire event for when player enters a new location
            PlayerGPS.OnEnterLocationRect += PlayerGPS_OnEnterLocationRect;
            EntityEffectBroker.OnNewMagicRound += EntityEffectBroker_OnNewMagicRound;
            levitateMotor = GetComponent<LevitateMotor>();
            underwaterFog = new UnderwaterFog();

            // Track network state locally so a player standing inside an incompatible local
            // interior/dungeon can be pushed back outside when the network state changes.
            lastNetworkActiveState = NetworkServer.active || NetworkClient.active;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (networkDungeonConversionInProgress)
                FailPendingNetworkDungeonConversion("OnStopClient-during-conversion");
            else
                EmergencyExitDungeonForNetworkChange("OnStopClient");
            lastNetworkActiveState = NetworkServer.active || NetworkClient.active;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (networkDungeonConversionInProgress)
                FailPendingNetworkDungeonConversion("OnStopServer-during-conversion");
            else
                EmergencyExitDungeonForNetworkChange("OnStopServer");
            lastNetworkActiveState = NetworkServer.active || NetworkClient.active;
        }

        void Update()
        {            
            // Pure SP should not run any MP/network dungeon safety code.
            // The old small SP PlayerEnterExit had no Mirror state checks here.
            // Only run this when Mirror is actually active, or just changed state.
            if (NetworkServer.active || NetworkClient.active || lastNetworkActiveState)
                HandleNetworkDungeonSafety();

            // Track which dungeon block player is inside of
            if (dungeon && isPlayerInsideDungeon)
            {
                int playerBlockIndex = dungeon.GetPlayerBlockIndex(transform.position);
                if (playerBlockIndex != lastPlayerDungeonBlockIndex)
                {
                    InvalidateVisualWaterSurfaceCache();
                    lastPlayerDungeonBlockIndex = playerBlockIndex;
                    if (playerBlockIndex != -1)
                    {
                        dungeon.GetBlockData(playerBlockIndex, out playerDungeonBlockData);
                        blockWaterLevel = playerDungeonBlockData.WaterLevel;
                        isPlayerInsideDungeonCastle = playerDungeonBlockData.CastleBlock;
                        SpecialAreaCheck();
                    }
                    else
                    {
                        blockWaterLevel = 10000;
                        isPlayerInsideDungeonCastle = false;
                        isPlayerInsideSpecialArea = false;
                    }
                    //Debug.Log(string.Format("Player is now inside block {0}", playerDungeonBlockData.BlockName));
                }
                // Do not update underwater fog directly from raw blockWaterLevel here.
                // Network dungeons can be placed at large negative Y offsets, and UnderwaterFog
                // assumes classic dungeon Y=0 when given blockWaterLevel directly. Fog is now
                // updated after the offset-aware submerged check below.
            }

            if (holidayTextPrimed && holidayTextLocation != GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject)
            {
                holidayTextTimer = 0;
                holidayTextPrimed = false;
            }

            // Count down holiday text display
            if (holidayTextTimer > 0)
                holidayTextTimer -= Time.deltaTime;
            if (holidayTextTimer <= 0 && holidayTextPrimed && GameManager.Instance.IsPlayerOnHUD)
            {
                holidayTextPrimed = false;
                ShowHolidayText();
            }

            // Player in sunlight or darkness
            isPlayerInSunlight = DaggerfallUnity.Instance.WorldTime.Now.IsDay && !IsPlayerInside && !GameManager.Instance.PlayerEntity.InPrison;

            // Do not process underwater logic if not playing game
            // This prevents player catching breath during load
            if (!GameManager.Instance.IsPlayingGame())
                return;

            // Underwater swimming logic should only be processed in dungeons at this time
            if (isPlayerInsideDungeon)
            {
                // NOTE: Player's y value in DF unity is 0.95 units off from classic, so subtracting it to get correct comparison
                float dungeonWaterSurfaceWorldY = GetDungeonWaterSurfaceWorldY();

                if (blockWaterLevel == 10000 || (player.transform.position.y + (50 * MeshReader.GlobalScale) - 0.95f) >= dungeonWaterSurfaceWorldY)
                {
                    isPlayerSwimming = false;
                    levitateMotor.IsSwimming = false;
                }
                else
                {
                    if (!isPlayerSwimming)
                        SendMessage("PlayLargeSplash", SendMessageOptions.DontRequireReceiver);
                    isPlayerSwimming = true;
                    levitateMotor.IsSwimming = true;
                }

                bool overEncumbered = (GameManager.Instance.PlayerEntity.CarriedWeight * 4 > 250);
                if ((overEncumbered && levitateMotor.IsSwimming) && !displayAfloatMessage && !GameManager.Instance.PlayerEntity.IsWaterWalking)
                {
                    DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("cannotFloat"), 1.75f);
                    displayAfloatMessage = true;
                }
                else if ((!overEncumbered || !levitateMotor.IsSwimming) && displayAfloatMessage)
                {
                    displayAfloatMessage = false;
                }

                // Check if player is submerged and needs to start holding breath
                if (blockWaterLevel == 10000 || (player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f) >= dungeonWaterSurfaceWorldY)
                {
                    isPlayerSubmerged = false;
                }
                else
                    isPlayerSubmerged = true;

                UpdateDungeonUnderwaterFogOffsetAware();
            }
            else
            {
                if (underwaterFog != null)
                    underwaterFog.UpdateFog(10000);

                // Clear flags when not in a dungeon
                // don't clear swimming if we're outside on a water tile - MeteoricDragon
                if (GameManager.Instance.StreamingWorld.PlayerTileMapIndex != 0)
                    isPlayerSwimming = false;
                isPlayerSubmerged = false;
                levitateMotor.IsSwimming = false;
            }
        }

        /// <summary>
        /// Clears dungeon water/swimming state so stale singleplayer dungeon water data cannot
        /// carry into a later networked dungeon placed at a different world Y offset.
        /// This only resets local player state; it does not move, create, destroy, or edit dungeons.
        /// </summary>
        private void ClearDungeonWaterState()
        {
            InvalidateVisualWaterSurfaceCache();
            blockWaterLevel = 10000;
            isPlayerSwimming = false;
            isPlayerSubmerged = false;
            displayAfloatMessage = false;

            if (levitateMotor != null)
                levitateMotor.IsSwimming = false;

            if (underwaterFog != null)
            {
                // If we are currently in an offset network dungeon, raw 10000 can still look
                // underwater because the camera is around -1200/-2400/etc. Convert the water
                // level to the coordinate space expected by UnderwaterFog.
                if (isPlayerInsideDungeon && dungeon != null)
                    underwaterFog.UpdateFog(GetDungeonWaterLevelForFog());
                else
                    underwaterFog.UpdateFog(10000);
            }
        }

        /// <summary>
        /// Updates underwater fog using the already offset-aware submerged state instead of
        /// letting UnderwaterFog compare the player against an unoffset classic dungeon water Y.
        /// This prevents network dungeons placed at -1200/-2400/etc. from looking fully underwater
        /// while still enabling the visual effect when the player's head is actually submerged.
        /// </summary>
        private void UpdateDungeonUnderwaterFogOffsetAware()
        {
            if (underwaterFog == null)
                return;

            // UnderwaterFog.UpdateFog() expects a classic dungeon water level where the dungeon
            // root is effectively at Y=0. Network dungeons are shifted down by their root Y,
            // so passing raw blockWaterLevel makes the whole dungeon look underwater. Convert
            // the level back into the coordinate space expected by UnderwaterFog instead.
            underwaterFog.UpdateFog(GetDungeonWaterLevelForFog());
        }

        /// <summary>
        /// Gets current dungeon water surface in world Y. In normal singleplayer dungeons
        /// dungeon.transform.position.y is usually 0, so this preserves vanilla behaviour.
        /// In networked dungeons placed at negative Y, this offsets the water surface down
        /// with the dungeon instead of comparing the player against a stale/global Y=0 level.
        /// </summary>
        private float GetDungeonWaterSurfaceWorldY()
        {
            float visualWaterSurfaceY;
            if (TryGetVisualDungeonWaterSurfaceY(out visualWaterSurfaceY))
                return visualWaterSurfaceY;

            float dungeonBaseY = GetCurrentDungeonBaseYForWater();
            return dungeonBaseY + (blockWaterLevel * -1 * MeshReader.GlobalScale);
        }

        /// <summary>
        /// Converts the current block water level into the waterLevel value expected by
        /// UnderwaterFog.UpdateFog(), while preserving the dungeon root Y offset.
        ///
        /// UnderwaterFog internally does: waterSurfaceY = -waterLevel * MeshReader.GlobalScale.
        /// We need: waterSurfaceY = dungeonBaseY + (-blockWaterLevel * MeshReader.GlobalScale).
        /// Therefore: waterLevel = blockWaterLevel - dungeonBaseY / MeshReader.GlobalScale.
        /// </summary>
        private float GetDungeonWaterLevelForFog()
        {
            float visualWaterSurfaceY;
            if (TryGetVisualDungeonWaterSurfaceY(out visualWaterSurfaceY))
                return -visualWaterSurfaceY / MeshReader.GlobalScale;

            float dungeonBaseY = GetCurrentDungeonBaseYForWater();
            return blockWaterLevel - (dungeonBaseY / MeshReader.GlobalScale);
        }

        /// <summary>
        /// Public offset-aware dungeon water surface helper for systems outside PlayerEnterExit
        /// such as PlayerFootsteps. Returns false when the player is not in a dungeon water block.
        /// </summary>
        public bool TryGetDungeonWaterSurfaceWorldY(out float surfaceY)
        {
            surfaceY = 0f;

            if (!isPlayerInsideDungeon || blockWaterLevel == 10000 || dungeon == null)
                return false;

            surfaceY = GetDungeonWaterSurfaceWorldY();
            return true;
        }

        /// <summary>
        /// True when the supplied world Y is below the current offset-aware dungeon water surface.
        /// </summary>
        public bool IsWorldYBelowDungeonWaterSurface(float worldY)
        {
            float surfaceY;
            if (!TryGetDungeonWaterSurfaceWorldY(out surfaceY))
                return false;

            return worldY < surfaceY;
        }

        /// <summary>
        /// True when footstep code should use shallow dungeon water sounds. This deliberately
        /// reuses PlayerEnterExit's offset-aware water surface instead of raw blockWaterLevel.
        /// </summary>
        public bool ShouldUseDungeonShallowFootstepSound(float feetWorldY)
        {
            if (!isPlayerInsideDungeon || blockWaterLevel == 10000 || isPlayerSwimming)
                return false;

            return IsWorldYBelowDungeonWaterSurface(feetWorldY);
        }

        /// <summary>
        /// True when footstep code should leave dungeon water sounds and return to normal stone.
        /// </summary>
        public bool ShouldResetDungeonWaterFootstepSound(float resetWorldY)
        {
            if (!isPlayerInsideDungeon || blockWaterLevel == 10000)
                return true;

            float surfaceY;
            if (!TryGetDungeonWaterSurfaceWorldY(out surfaceY))
                return true;

            return resetWorldY >= surfaceY;
        }


        private float GetCurrentDungeonBaseYForWater()
        {
            if (dungeon == null)
                return 0f;

            // Network dungeons carry an authoritative Y slot. Prefer it over a transform that
            // might be temporarily stale during spawn/rebind.
            if (Mathf.Abs(dungeon.PositionY) > 0.01f)
                return dungeon.PositionY;

            return dungeon.transform.position.y;
        }

        private void InvalidateVisualWaterSurfaceCache()
        {
            cachedVisualWaterBlockIndex = int.MinValue;
            cachedVisualWaterLookupComplete = false;
            cachedVisualWaterSurfaceValid = false;
            cachedVisualWaterSurfaceY = 0f;
        }

        private bool ShouldUseVisualDungeonWaterSurfaceFallback()
        {
            // Host-side network dungeons can have a small mismatch between the classic
            // blockWaterLevel-derived Y and the actual generated water renderer Y.
            // Use the visual water surface for all host-side network dungeons, regardless
            // of whether the host or a remote client requested the dungeon.
            return dungeon != null &&
                   isPlayerInsideDungeon &&
                   NetworkServer.active &&
                   dungeon.IsNetworkDungeonInstance &&
                   blockWaterLevel != 10000;
        }

        private bool TryGetVisualDungeonWaterSurfaceY(out float surfaceY)
        {
            surfaceY = 0f;

            if (!ShouldUseVisualDungeonWaterSurfaceFallback())
                return false;

            // Cache both successful and failed lookups for this block. Some large castle
            // dungeons have no renderer whose object/material name contains "water". Without
            // a negative cache, every caller repeated GetComponentsInChildren<Renderer>() and
            // scanned thousands of renderers every physics tick.
            if (cachedVisualWaterLookupComplete && cachedVisualWaterBlockIndex == lastPlayerDungeonBlockIndex)
            {
                if (cachedVisualWaterSurfaceValid)
                    surfaceY = cachedVisualWaterSurfaceY;

                return cachedVisualWaterSurfaceValid;
            }

            // Mark this block as searched before scanning. If no visual water renderer is
            // found, that failed result remains cached until the player changes dungeon block
            // or dungeon water state is explicitly invalidated.
            cachedVisualWaterBlockIndex = lastPlayerDungeonBlockIndex;
            cachedVisualWaterLookupComplete = true;
            cachedVisualWaterSurfaceValid = false;
            cachedVisualWaterSurfaceY = 0f;

            Renderer[] renderers = dungeon.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return false;

            Vector3 playerPos = transform.position;
            Renderer best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                    continue;

                string objectName = r.gameObject != null ? r.gameObject.name : string.Empty;
                string materialName = (r.sharedMaterial != null) ? r.sharedMaterial.name : string.Empty;

                if (objectName.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 &&
                    materialName.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Bounds b = r.bounds;

                float dx = 0f;
                if (playerPos.x < b.min.x)
                    dx = b.min.x - playerPos.x;
                else if (playerPos.x > b.max.x)
                    dx = playerPos.x - b.max.x;

                float dz = 0f;
                if (playerPos.z < b.min.z)
                    dz = b.min.z - playerPos.z;
                else if (playerPos.z > b.max.z)
                    dz = playerPos.z - b.max.z;

                float score = dx * dx + dz * dz;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }

            if (best == null)
                return false;

            // Water is a flat plane, so bounds center is the actual visual surface height.
            surfaceY = best.bounds.center.y;
            cachedVisualWaterBlockIndex = lastPlayerDungeonBlockIndex;
            cachedVisualWaterSurfaceY = surfaceY;
            cachedVisualWaterSurfaceValid = true;

            float classicSurfaceY = GetCurrentDungeonBaseYForWater() + (blockWaterLevel * -1 * MeshReader.GlobalScale);
            if (Mathf.Abs(classicSurfaceY - surfaceY) > 0.5f)
            {
                Debug.Log($"[DungeonWaterFix] Using visual water surface for network dungeon on host. block={lastPlayerDungeonBlockIndex} blockWaterLevel={blockWaterLevel} classicY={classicSurfaceY} visualY={surfaceY} dungeonY={GetCurrentDungeonBaseYForWater()} requester={dungeon.RequesterNetId} waterObject='{best.name}'");
            }

            return true;
        }


        private bool TryResolveSavedDungeonLocation(PlayerPositionData_v1 playerPosition, out DFLocation location)
        {
            location = default(DFLocation);
            if (playerPosition == null || dfUnity == null)
                return false;

            try
            {
                bool hasExplicitSavedIdentity =
                    !string.IsNullOrEmpty(playerPosition.savedDungeonRegionName) &&
                    !string.IsNullOrEmpty(playerPosition.savedDungeonLocationName);

                if (hasExplicitSavedIdentity)
                {
                    location = dfUnity.ContentReader.MapFileReader.GetLocation(
                        playerPosition.savedDungeonRegionName,
                        playerPosition.savedDungeonLocationName);

                    // New saves carry both the human-readable identity and numeric map ID.
                    // Never silently fall back to the pre-load exterior world coordinate if
                    // those fields disagree. Doing so can generate a different dungeon whose
                    // layout/textures no longer match the saved dungeon-local player position.
                    if (!location.Loaded || !location.HasDungeon)
                    {
                        Debug.LogError($"[NetworkDungeonConversion][Identity] Explicit saved dungeon could not be loaded: '{playerPosition.savedDungeonRegionName}/{playerPosition.savedDungeonLocationName}' mapId={playerPosition.savedDungeonMapId}.");
                        location = default(DFLocation);
                        return false;
                    }

                    if (playerPosition.savedDungeonMapId != 0 &&
                        location.MapTableData.MapId != playerPosition.savedDungeonMapId)
                    {
                        Debug.LogError($"[NetworkDungeonConversion][Identity] Refusing mismatched saved dungeon identity. saved='{playerPosition.savedDungeonRegionName}/{playerPosition.savedDungeonLocationName}' savedMapId={playerPosition.savedDungeonMapId} resolvedMapId={location.MapTableData.MapId}.");
                        location = default(DFLocation);
                        return false;
                    }

                    Debug.Log($"[NetworkDungeonConversion][Identity] Resolved explicit saved dungeon '{location.RegionName}/{location.Name}' mapId={location.MapTableData.MapId}.");
                    return true;
                }

                // Legacy saves made before explicit dungeon identity was serialized can
                // only be resolved from their exterior world coordinate.
                if (!location.Loaded)
                {
                    DFPosition mapPixel = MapsFile.WorldCoordToMapPixel(playerPosition.worldPosX, playerPosition.worldPosZ);
                    ContentReader.MapSummary summary;
                    if (dfUnity.ContentReader.HasLocation(mapPixel.X, mapPixel.Y, out summary))
                        dfUnity.ContentReader.GetLocation(summary.RegionIndex, summary.MapIndex, out location);
                }

                if (location.Loaded && location.HasDungeon)
                    Debug.LogWarning($"[NetworkDungeonConversion][Identity] Resolved legacy dungeon save from world coordinate {playerPosition.worldPosX}/{playerPosition.worldPosZ}: '{location.RegionName}/{location.Name}' mapId={location.MapTableData.MapId}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkDungeonConversion] Failed to resolve saved dungeon location: {ex.Message}");
                location = default(DFLocation);
            }

            return location.Loaded && location.HasDungeon;
        }

        private void CommitSavedDungeonWorldContext(
            DFLocation location,
            int worldX,
            int worldZ,
            string reason)
        {
            // Once TransitionDungeonInterior has marked the player inside, commit the exact
            // resolved destination once. At this point PlayerGPS_OnEnterLocationRect is
            // suppressed by isPlayerInside, so this updates CurrentLocation/CurrentMapID
            // without showing exterior dungeon flavour messages. Repeating this for the host
            // also repairs saves whose serialized world coordinate came from an older handoff.
            if ((!NetworkClient.active && !NetworkServer.active) ||
                !location.Loaded || !location.HasDungeon)
                return;

            try
            {
                bool pureClient = NetworkClient.active && !NetworkServer.active;
                Vector3 restorePosition = transform.position;
                bool restoreInside = isPlayerInside;
                bool restoreInsideDungeon = isPlayerInsideDungeon;
                bool restoreInsideCastle = isPlayerInsideDungeonCastle;
                bool restoreInsideSpecial = isPlayerInsideSpecialArea;
                bool restoreTeleportedIntoDungeon = PlayerTeleportedIntoDungeon;
                DaggerfallDungeon restoreDungeon = dungeon;

                if (playerGPS != null)
                {
                    playerGPS.WorldX = worldX;
                    playerGPS.WorldZ = worldZ;
                }

                if (world != null)
                {
                    DFPosition mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
                    world.MapPixelX = mapPixel.X;
                    world.MapPixelY = mapPixel.Y;

                    // This is the missing half of the old TeleportPc client-exit fix.
                    // Merely assigning MapPixelX/Y makes the exterior appear logically
                    // selected, but it does not build/reposition that exterior. The next
                    // DungeonEntrance auto-reposition then runs against an uninitialized
                    // exterior and leaves PlayerAdvanced at the negative dungeon Y.
                    //
                    // Rebuild only for a pure client after it is already marked inside,
                    // then restore the dungeon-space state/transform in the same frame.
                    // This is the same proven order used by the TeleportPc path below.
                    if (pureClient && restoreInsideDungeon)
                        world.TeleportToCoordinates(mapPixel.X, mapPixel.Y, StreamingWorld.RepositionMethods.None);
                }

                if (playerGPS != null)
                {
                    playerGPS.UpdateWorldInfo();

                    // UpdateWorldInfo can normalize coordinates while rebuilding location
                    // metadata. The exact dungeon entrance remains authoritative.
                    playerGPS.WorldX = worldX;
                    playerGPS.WorldZ = worldZ;
                }

                if (pureClient && restoreInsideDungeon)
                {
                    dungeon = restoreDungeon;
                    isPlayerInside = restoreInside;
                    isPlayerInsideDungeon = restoreInsideDungeon;
                    isPlayerInsideDungeonCastle = restoreInsideCastle;
                    isPlayerInsideSpecialArea = restoreInsideSpecial;
                    PlayerTeleportedIntoDungeon = restoreTeleportedIntoDungeon;

                    EnableDungeonParent();
                    transform.position = restorePosition;
                    SetStanding();

                    Debug.Log($"[NetworkDungeonConversion][ClientWorldContext] Rebuilt pure-client exterior and restored dungeon state. dungeon='{location.RegionName}/{location.Name}' anchor={worldX}/{worldZ} restoredPos={restorePosition} reason={reason}");
                }

                int currentMapId = playerGPS != null && playerGPS.CurrentLocation.Loaded
                    ? playerGPS.CurrentLocation.MapTableData.MapId
                    : -1;

                Debug.Log($"[NetworkDungeonConversion][ClientWorldContext] Committed dungeon='{location.RegionName}/{location.Name}' mapId={location.MapTableData.MapId} currentMapId={currentMapId} anchor={worldX}/{worldZ} reason={reason}");

                if (currentMapId >= 0 && currentMapId != location.MapTableData.MapId)
                {
                    Debug.LogError($"[NetworkDungeonConversion][ClientWorldContext] Current location mismatch after commit. expectedMapId={location.MapTableData.MapId} currentMapId={currentMapId} dungeon='{location.RegionName}/{location.Name}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkDungeonConversion][ClientWorldContext] Failed to commit dungeon world context. dungeon='{location.RegionName}/{location.Name}' anchor={worldX}/{worldZ} reason={reason} error={ex}");
            }
        }

        private Vector3 GetSavedDungeonLocalPosition(PlayerPositionData_v1 playerPosition)
        {
            if (playerPosition == null)
                return Vector3.zero;

            if (playerPosition.hasSavedDungeonLocalPosition)
                return playerPosition.savedDungeonLocalPosition;

            // Backward compatibility for saves made before explicit local position was added.
            // MP saves know their old dungeon Y; normal SP dungeons use the standard root at Y=0.
            if (playerPosition.savedInsideNetworkDungeon)
                return playerPosition.position - new Vector3(0f, playerPosition.savedNetworkDungeonY, 0f);

            return playerPosition.position;
        }

        private void ResolveSavedDungeonEntrance(
            DFLocation location,
            int savedWorldX,
            int savedWorldZ,
            out int resolvedWorldX,
            out int resolvedWorldZ)
        {
            resolvedWorldX = savedWorldX;
            resolvedWorldZ = savedWorldZ;

            // Match TeleportPc for both host and client. Saved world coordinates can be
            // stale when the save was made after an earlier dungeon conversion, so the
            // resolved dungeon identity must determine its exact exterior entrance.
            if ((!NetworkClient.active && !NetworkServer.active) || !location.Loaded)
                return;

            try
            {
                Vector3 exactEntranceLocal;
                int exactWorldX;
                int exactWorldZ;
                if (DaggerfallWorkshop.StreamingWorld.TryGetDungeonEntranceWorldCoordinates(
                    location,
                    out exactWorldX,
                    out exactWorldZ,
                    out exactEntranceLocal))
                {
                    resolvedWorldX = exactWorldX;
                    resolvedWorldZ = exactWorldZ;
                    Debug.Log($"[NetworkDungeonConversion][ExactEntrance] Replaced saved/pre-load world={savedWorldX}/{savedWorldZ} with exact dungeon entrance={resolvedWorldX}/{resolvedWorldZ} localEntrance={exactEntranceLocal} dungeon='{location.RegionName}/{location.Name}' host={NetworkServer.active}");
                }
                else
                {
                    Debug.LogWarning($"[NetworkDungeonConversion][ExactEntrance] Exact entrance lookup failed for '{location.RegionName}/{location.Name}'. Keeping saved fallback={savedWorldX}/{savedWorldZ}.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkDungeonConversion][ExactEntrance] Exact entrance lookup threw for '{location.RegionName}/{location.Name}'. Keeping saved fallback={savedWorldX}/{savedWorldZ}. error={ex.Message}");
            }
        }

        private bool ShouldUseDungeonStartMarkerForLoadedLayout(PlayerPositionData_v1 playerPosition, DFLocation location)
        {
            if (playerPosition == null)
                return false;

            bool savedSmallerDungeons = playerPosition.smallerDungeonsState == QuestSmallerDungeonsState.Enabled;
            if (savedSmallerDungeons == DaggerfallUnity.Settings.SmallerDungeons)
                return false;

            // Match vanilla load behavior: story dungeons always retain their full layout,
            // while other dungeons with a changed layout must start at the entrance.
            return !DaggerfallDungeon.IsMainStoryDungeon(location.MapTableData.MapId);
        }

        private bool IsFiniteDungeonLocalPosition(Vector3 position)
        {
            return !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
                   !float.IsNaN(position.y) && !float.IsInfinity(position.y) &&
                   !float.IsNaN(position.z) && !float.IsInfinity(position.z);
        }

        private void RemoveLocalDungeonCopiesForNetworkConversion(
            DFLocation location,
            DaggerfallDungeon sourceLocalDungeon,
            string reason)
        {
            if (!location.Loaded)
                return;

            int removed = 0;
            bool sourceHandled = false;
            DaggerfallDungeon[] allDungeons = FindObjectsOfType<DaggerfallDungeon>();

            for (int i = 0; i < allDungeons.Length; i++)
            {
                DaggerfallDungeon candidate = allDungeons[i];
                if (candidate == null || DungeonHasUsableNetworkIdentity(candidate))
                    continue;

                bool isSource = candidate == sourceLocalDungeon;
                bool sameLocation =
                    string.Equals(candidate.Summary.RegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Summary.LocationName, location.Name, StringComparison.OrdinalIgnoreCase);

                if (!isSource && !sameLocation)
                    continue;

                if (isSource)
                    sourceHandled = true;

                if (dungeon == candidate)
                    dungeon = null;

                candidate.gameObject.SetActive(false);
                Destroy(candidate.gameObject);
                removed++;

                Debug.Log($"[NetworkDungeonConversion][LocalCleanup] Removed non-network dungeon copy '{candidate.name}' reason={reason} sameLocation={sameLocation} source={isSource}");
            }

            // FindObjectsOfType() only returns active objects. If the explicitly supplied source
            // had already been disabled earlier in this frame, still make sure it is destroyed.
            if (sourceLocalDungeon != null && !sourceHandled && !DungeonHasUsableNetworkIdentity(sourceLocalDungeon))
            {
                if (dungeon == sourceLocalDungeon)
                    dungeon = null;

                sourceLocalDungeon.gameObject.SetActive(false);
                Destroy(sourceLocalDungeon.gameObject);
                removed++;
                Debug.Log($"[NetworkDungeonConversion][LocalCleanup] Removed disabled source non-network dungeon '{sourceLocalDungeon.name}' reason={reason}");
            }

            if (removed > 0)
                GameObjectHelper.DestroyNonNetworkedEnemiesForMultiplayerStart();
        }

        private bool BeginNetworkDungeonConversion(
            DFLocation location,
            Vector3 dungeonLocalPosition,
            int worldX,
            int worldZ,
            bool fromLoad,
            bool useStartMarker,
            DaggerfallDungeon sourceLocalDungeon,
            int requesterLevel,
            string initialSavedActionState,
            string reason)
        {
            if (networkDungeonConversionInProgress)
                return true;

            if ((!NetworkServer.active && !NetworkClient.active) || !location.Loaded || !location.HasDungeon)
                return false;

            if (!IsFiniteDungeonLocalPosition(dungeonLocalPosition))
            {
                Debug.LogWarning($"[NetworkDungeonConversion] Refusing invalid dungeon-local position {dungeonLocalPosition}. reason={reason}");
                return false;
            }

            networkDungeonConversionInProgress = true;
            pendingNetworkDungeonConversionFromLoad = fromLoad;
            pendingNetworkDungeonUseStartMarker = useStartMarker;
            pendingNetworkDungeonRegionName = location.RegionName;
            pendingNetworkDungeonLocationName = location.Name;
            pendingNetworkDungeonLocalPosition = dungeonLocalPosition;
            pendingNetworkDungeonWorldX = worldX;
            pendingNetworkDungeonWorldZ = worldZ;
            pendingNetworkDungeonRequesterLevel = Mathf.Clamp(
                requesterLevel > 0 ? requesterLevel : DaggerfallDungeon.GetLocalPlayerLevelFallback(),
                1,
                100);
            pendingNetworkDungeonInitialActionState = initialSavedActionState ?? string.Empty;
            pendingNetworkDungeonConversionStartedAt = Time.realtimeSinceStartup;
            lastNetworkActiveState = true;
            isRespawning = true;

            if (DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
                DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();

            // A pure client loading a dungeon save does not need to mutate its exterior
            // PlayerGPS/StreamingWorld context before the host dungeon arrives. The exact
            // anchor is carried in the request, applied to PositionMultiplayer, and returned
            // on the network dungeon data. Even a one-time PlayerGPS coordinate change here
            // makes DFU emit wilderness terrain-description messages inside the dungeon.
            bool skipPureClientLoadWorldContext =
                fromLoad && NetworkClient.active && !NetworkServer.active;

            if (!skipPureClientLoadWorldContext)
            {
                // Prepare the destination exterior/GPS context before the host imports
                // enemies or the local quest-resource pass checks CurrentMapID.
                if (playerGPS != null)
                {
                    playerGPS.WorldX = worldX;
                    playerGPS.WorldZ = worldZ;
                }

                if (world != null)
                {
                    DFPosition mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
                    world.MapPixelX = mapPixel.X;
                    world.MapPixelY = mapPixel.Y;

                    bool pureClient = NetworkClient.active && !NetworkServer.active;
                    if (!pureClient)
                    {
                        world.TeleportToCoordinates(mapPixel.X, mapPixel.Y, StreamingWorld.RepositionMethods.None);

                        if (playerGPS != null)
                        {
                            playerGPS.WorldX = worldX;
                            playerGPS.WorldZ = worldZ;
                            playerGPS.UpdateWorldInfo();
                        }
                    }
                }
            }
            else
            {
                Debug.Log($"[NetworkDungeonConversion][ClientEntrance] Preserved saved PlayerGPS/StreamingWorld context until network dungeon entry. anchor={worldX}/{worldZ}");
            }

            // A save load can begin with sourceLocalDungeon == null even though a local/SP
            // copy of this same dungeon is still alive in the scene. Remove every matching
            // non-network copy now so later transition code cannot accidentally bind the
            // player to a Y=0 local dungeon instead of the host-authored network instance.
            RemoveLocalDungeonCopiesForNetworkConversion(location, sourceLocalDungeon, reason);

            if (dungeon != null && !DungeonHasUsableNetworkIdentity(dungeon))
                dungeon = null;

            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();
            ClearDungeonWaterState();
            isPlayerInside = false;
            isPlayerInsideDungeon = false;
            isPlayerInsideDungeonCastle = false;
            isPlayerInsideSpecialArea = false;

            Debug.Log($"[NetworkDungeonConversion] Started. reason={reason} fromLoad={fromLoad} dungeon='{location.RegionName}/{location.Name}' world={worldX}/{worldZ} local={dungeonLocalPosition} useStartMarker={useStartMarker}");
            StartCoroutine(RequestNetworkDungeonConversionWhenReady(location, reason));
            return true;
        }

        private IEnumerator RequestNetworkDungeonConversionWhenReady(DFLocation location, string reason)
        {
            // Let deferred destruction remove the local SP dungeon before the host searches
            // by region/location, then wait for the local Mirror player/connection to exist.
            yield return new WaitForEndOfFrame();

            while (networkDungeonConversionInProgress)
            {
                if (!NetworkServer.active && !NetworkClient.active)
                {
                    FailPendingNetworkDungeonConversion("network-became-inactive-before-request");
                    yield break;
                }

                if (Time.realtimeSinceStartup - pendingNetworkDungeonConversionStartedAt > NetworkDungeonConversionTimeout)
                {
                    FailPendingNetworkDungeonConversion("timed-out-waiting-for-local-network-player");
                    yield break;
                }

                PlayerMultiplayer localNetPlayer = PlayerMultiplayer.GetLocalPlayerForCommand("network-dungeon-conversion");
                if (localNetPlayer != null)
                {
                    localNetPlayer.RequestSavedDungeonFromHost(
                        location,
                        pendingNetworkDungeonLocalPosition,
                        pendingNetworkDungeonWorldX,
                        pendingNetworkDungeonWorldZ,
                        reason,
                        pendingNetworkDungeonRequesterLevel,
                        pendingNetworkDungeonInitialActionState);
                    yield break;
                }

                yield return null;
            }
        }

        public bool HasPendingNetworkDungeonConversionFor(DFLocation location)
        {
            return networkDungeonConversionInProgress &&
                   location.Loaded &&
                   string.Equals(pendingNetworkDungeonRegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(pendingNetworkDungeonLocationName, location.Name, StringComparison.OrdinalIgnoreCase);
        }

        public bool PrepareSavedNetworkDungeonTransition(
            DaggerfallDungeon liveDungeon,
            DFLocation location,
            string reason)
        {
            if (!HasPendingNetworkDungeonConversionFor(location) ||
                liveDungeon == null ||
                !liveDungeon.isSet ||
                !DungeonHasUsableNetworkIdentity(liveDungeon))
                return false;

            if (!string.Equals(liveDungeon.Summary.RegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(liveDungeon.Summary.LocationName, location.Name, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[NetworkDungeonConversion][ExactBind] Refusing mismatched live dungeon. requested='{location.RegionName}/{location.Name}' live='{liveDungeon.Summary.RegionName}/{liveDungeon.Summary.LocationName}' reason={reason}");
                return false;
            }

            if (!ReferenceComponents())
                return false;

            // Saved-dungeon entry already knows the exact host-authored NetworkIdentity from
            // DungeonNetworkData. Do not call TransitionDungeonInterior() here: that generic
            // method searches by region/name and can select a stale local Y=0 dungeon when
            // both copies briefly coexist on a pure client during load.
            StaticDoor transitionDoor = new StaticDoor();
            ClearDungeonWaterState();
            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();

            RaiseOnPreTransitionEvent(TransitionType.ToDungeonInterior, transitionDoor);

            dungeon = liveDungeon;
            isPlayerInside = true;
            isPlayerInsideDungeon = true;
            isPlayerInsideDungeonCastle = false;
            isPlayerInsideSpecialArea = false;
            lastNetworkActiveState = NetworkServer.active || NetworkClient.active;

            if (liveDungeon.StartMarker == null)
            {
                Debug.LogError($"[NetworkDungeonConversion][ExactBind] Live network dungeon has no StartMarker. dungeon='{location.RegionName}/{location.Name}' netId={liveDungeon.netId} reason={reason}");
                return false;
            }

            // Preserve the normal dungeon-transition side effects, but use the exact live
            // dungeon's marker. TryCompleteNetworkDungeonConversion() immediately replaces
            // this with the saved dungeon-local position unless a smaller-dungeon layout
            // change intentionally requires the start marker.
            MovePlayerToMarker(liveDungeon.StartMarker);
            TryApplyPendingTeleportPcDungeonMarker(location, reason + "-after-entry-marker");

            StaticDoor[] doors = DaggerfallStaticDoors.FindDoorsInCollections(
                liveDungeon.StaticDoorCollections,
                DoorTypes.DungeonExit);
            if (doors != null && doors.Length > 0)
            {
                Vector3 doorPos;
                int doorIndex;
                if (DaggerfallStaticDoors.FindClosestDoorToPlayer(transform.position, doors, out doorPos, out doorIndex))
                {
                    PlayerMouseLook look = GameManager.Instance.PlayerMouseLook;
                    if (look)
                        look.SetFacing(DaggerfallStaticDoors.GetDoorNormal(doors[doorIndex]));
                }
            }

            EnableDungeonParent(false);

            // Saved-load quest resources are deliberately replayed only after LoadInProgress
            // clears in TryCompleteNetworkDungeonConversion(). Do not inject them here.
            if (!(networkDungeonConversionInProgress && pendingNetworkDungeonConversionFromLoad))
                GameObjectHelper.AddQuestResourceObjects(SiteTypes.Dungeon, liveDungeon.transform);
            else
                Debug.Log($"[NetworkDungeonConversion][QuestResources] Deferred exact-bind dungeon quest-resource injection until save load completes. dungeon='{location.RegionName}/{location.Name}'");

            RaiseOnTransitionDungeonInteriorEvent(transitionDoor, liveDungeon);
            Debug.Log($"[NetworkDungeonConversion][ExactBind] Prepared saved dungeon using exact network object. dungeon='{location.RegionName}/{location.Name}' netId={liveDungeon.netId} y={liveDungeon.PositionY} reason={reason}");
            return true;
        }

        public bool TryCompleteNetworkDungeonConversion(
            DaggerfallDungeon liveDungeon,
            DFLocation location,
            Vector3 requestedLocalPosition,
            string reason)
        {
            if (!HasPendingNetworkDungeonConversionFor(location) ||
                liveDungeon == null ||
                !liveDungeon.isSet ||
                !DungeonHasUsableNetworkIdentity(liveDungeon))
                return false;

            dungeon = liveDungeon;
            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();
            ClearDungeonWaterState();

            isPlayerInside = true;
            isPlayerInsideDungeon = true;
            isPlayerInsideDungeonCastle = false;
            isPlayerInsideSpecialArea = false;
            lastNetworkActiveState = true;

            // The earlier request deliberately preserved the pure client's old exterior
            // context to avoid notifications during the asynchronous handoff. Now that the
            // player is definitively inside the correct live dungeon, commit its entrance
            // context exactly once. Dungeon exit, quest SiteLink lookup, and future saves all
            // depend on this real PlayerGPS/StreamingWorld identity.
            if (pendingNetworkDungeonConversionFromLoad)
            {
                CommitSavedDungeonWorldContext(
                    location,
                    pendingNetworkDungeonWorldX,
                    pendingNetworkDungeonWorldZ,
                    reason + "-authoritative-entry");
            }

            EnableDungeonParent(false);

            Vector3 finalPosition = transform.position;
            if (!pendingNetworkDungeonUseStartMarker)
            {
                // Use the locally retained value as the final authority and treat the RPC
                // copy as a consistency check only.
                if ((requestedLocalPosition - pendingNetworkDungeonLocalPosition).sqrMagnitude > 0.0001f)
                    Debug.LogWarning($"[NetworkDungeonConversion] Target local position differed from pending value. target={requestedLocalPosition} pending={pendingNetworkDungeonLocalPosition}");

                finalPosition = liveDungeon.transform.position + pendingNetworkDungeonLocalPosition;
                ClearTransitionFallingDamage(reason + "-before-local-snap");
                transform.position = finalPosition;
            }

            SetStanding();
            ClearTransitionFallingDamageWindow(reason + "-after-local-snap");
            ForceSendMultiplayerCoordinatesNow(reason + "-complete");

            bool fromLoad = pendingNetworkDungeonConversionFromLoad;

            // On a pure client, SaveLoadManager is still inside LoadGame at this point.
            // It waits for IsRespawning to clear and only then restores SerializablePlayer,
            // scene cache, mod save data, and other state. The first-load-only Y=0 bug can
            // therefore happen after this otherwise-correct -Y snap. Preserve the exact
            // network dungeon and local offset and perform one deterministic final rebind
            // at the end of LoadGame instead of relying on a timed delay.
            if (fromLoad && NetworkClient.active && !NetworkServer.active)
            {
                pendingPureClientDungeonLoadFinalization = true;
                pendingPureClientDungeonLoadDungeon = liveDungeon;
                pendingPureClientDungeonLoadRegionName = location.RegionName ?? string.Empty;
                pendingPureClientDungeonLoadLocationName = location.Name ?? string.Empty;
                pendingPureClientDungeonLoadLocalPosition = transform.position - liveDungeon.transform.position;
                pendingPureClientDungeonLoadAuthoritativeY = liveDungeon.PositionY;
                pendingPureClientDungeonLoadWorldX = pendingNetworkDungeonWorldX;
                pendingPureClientDungeonLoadWorldZ = pendingNetworkDungeonWorldZ;

                Debug.Log($"[NetworkDungeonConversion][PostLoadY] Captured pure-client finalization. dungeon='{location.RegionName}/{location.Name}' netId={liveDungeon.netId} authoritativeY={pendingPureClientDungeonLoadAuthoritativeY} local={pendingPureClientDungeonLoadLocalPosition} world={pendingPureClientDungeonLoadWorldX}/{pendingPureClientDungeonLoadWorldZ}");
            }

            ClearPendingNetworkDungeonConversionState();
            isRespawning = false;

            if (!fromLoad && DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(0.7f);

            RaiseOnRespawnerCompleteEvent();
            if (fromLoad)
                StartCoroutine(AddSavedDungeonQuestResourcesAfterLoad(liveDungeon, location, reason));
            Debug.Log($"[NetworkDungeonConversion] Completed. reason={reason} dungeon='{location.RegionName}/{location.Name}' dungeonY={liveDungeon.transform.position.y} final={transform.position}");
            return true;
        }

        public bool FinalizePureClientNetworkDungeonLoadAfterRestore(string reason)
        {
            if (!pendingPureClientDungeonLoadFinalization)
                return false;

            // This safeguard is intentionally client-only. Host dungeon placement is already
            // authoritative and singleplayer must retain the vanilla/recovery paths.
            if (!NetworkClient.active || NetworkServer.active)
            {
                ClearPureClientDungeonLoadFinalization();
                return false;
            }

            DaggerfallDungeon liveDungeon = pendingPureClientDungeonLoadDungeon;
            if (liveDungeon == null ||
                !liveDungeon.isSet ||
                !DungeonHasUsableNetworkIdentity(liveDungeon))
            {
                Debug.LogWarning($"[NetworkDungeonConversion][PostLoadY] Could not finalize pure-client dungeon load because the authoritative dungeon is no longer usable. reason={reason}");
                ClearPureClientDungeonLoadFinalization();
                return false;
            }

            if (!string.Equals(liveDungeon.Summary.RegionName, pendingPureClientDungeonLoadRegionName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(liveDungeon.Summary.LocationName, pendingPureClientDungeonLoadLocationName, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[NetworkDungeonConversion][PostLoadY] Refusing final rebind to mismatched dungeon. expected='{pendingPureClientDungeonLoadRegionName}/{pendingPureClientDungeonLoadLocationName}' live='{liveDungeon.Summary.RegionName}/{liveDungeon.Summary.LocationName}' reason={reason}");
                ClearPureClientDungeonLoadFinalization();
                return false;
            }

            if (!ReferenceComponents())
            {
                ClearPureClientDungeonLoadFinalization();
                return false;
            }

            // Reassert the host-authored dungeon root itself first. This also covers the case
            // where some late pure-client restore touched the spawned dungeon transform rather
            // than only the player's transform.
            liveDungeon.PositionY = pendingPureClientDungeonLoadAuthoritativeY;
            liveDungeon.transform.position = new Vector3(0f, pendingPureClientDungeonLoadAuthoritativeY, 0f);

            dungeon = liveDungeon;
            isPlayerInside = true;
            isPlayerInsideDungeon = true;
            isPlayerInsideDungeonCastle = false;
            isPlayerInsideSpecialArea = false;
            lastNetworkActiveState = true;
            EnableDungeonParent(false);

            // Restore the exact dungeon exterior/GPS identity once more after serialized state
            // restoration. CommitSavedDungeonWorldContext temporarily preserves the current
            // transform for pure clients, and the authoritative local snap below is the final
            // position write for this load.
            try
            {
                DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(
                    pendingPureClientDungeonLoadRegionName,
                    pendingPureClientDungeonLoadLocationName);

                if (location.Loaded && location.HasDungeon)
                {
                    CommitSavedDungeonWorldContext(
                        location,
                        pendingPureClientDungeonLoadWorldX,
                        pendingPureClientDungeonLoadWorldZ,
                        reason + "-post-load-final");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkDungeonConversion][PostLoadY] Could not recommit saved dungeon world context. reason={reason} error={ex.Message}");
            }

            // CommitSavedDungeonWorldContext can rebuild StreamingWorld, so make the final
            // authoritative root + player write after it. No coroutine or arbitrary delay is
            // used: SaveLoadManager calls this after its complete restore phase.
            liveDungeon.PositionY = pendingPureClientDungeonLoadAuthoritativeY;
            liveDungeon.transform.position = new Vector3(0f, pendingPureClientDungeonLoadAuthoritativeY, 0f);
            transform.position = liveDungeon.transform.position + pendingPureClientDungeonLoadLocalPosition;
            SetStanding();
            ClearTransitionFallingDamageWindow(reason + "-post-load-final");
            ForceSendMultiplayerCoordinatesNow(reason + "-post-load-final");

            Debug.Log($"[NetworkDungeonConversion][PostLoadY] Finalized pure-client dungeon load after all save restoration. dungeon='{pendingPureClientDungeonLoadRegionName}/{pendingPureClientDungeonLoadLocationName}' netId={liveDungeon.netId} authoritativeY={liveDungeon.PositionY} local={pendingPureClientDungeonLoadLocalPosition} final={transform.position} reason={reason}");

            ClearPureClientDungeonLoadFinalization();
            return true;
        }

        private void ClearPureClientDungeonLoadFinalization()
        {
            pendingPureClientDungeonLoadFinalization = false;
            pendingPureClientDungeonLoadDungeon = null;
            pendingPureClientDungeonLoadRegionName = string.Empty;
            pendingPureClientDungeonLoadLocationName = string.Empty;
            pendingPureClientDungeonLoadLocalPosition = Vector3.zero;
            pendingPureClientDungeonLoadAuthoritativeY = 0f;
            pendingPureClientDungeonLoadWorldX = 0;
            pendingPureClientDungeonLoadWorldZ = 0;
        }

        private IEnumerator AddSavedDungeonQuestResourcesAfterLoad(
            DaggerfallDungeon liveDungeon,
            DFLocation location,
            string reason)
        {
            // AddQuestFoe deliberately refuses to spawn during SaveLoadManager.LoadInProgress.
            // A saved-dungeon conversion completes before that flag is lowered, so the normal
            // TransitionDungeonInterior injection pass cannot create this player's quest foe.
            // Repeat the idempotent resource pass once loading is complete. Existing resources
            // are filtered by GameObjectHelper; MP quest foes are filtered per requesterNetId.
            float started = Time.realtimeSinceStartup;
            while (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress)
            {
                if (Time.realtimeSinceStartup - started > 20f)
                {
                    Debug.LogWarning($"[NetworkDungeonConversion][QuestResources] Timed out waiting for load completion. dungeon='{location.RegionName}/{location.Name}' reason={reason}");
                    yield break;
                }

                yield return null;
            }

            yield return null;

            if (liveDungeon == null ||
                dungeon != liveDungeon ||
                !isPlayerInsideDungeon ||
                !liveDungeon.isSet)
                yield break;

            GameObjectHelper.AddQuestResourceObjects(SiteTypes.Dungeon, liveDungeon.transform);
            Debug.Log($"[NetworkDungeonConversion][QuestResources] Replayed post-load quest resource injection for requester. dungeon='{location.RegionName}/{location.Name}' reason={reason}");
        }

        public void FailPendingNetworkDungeonConversion(string reason)
        {
            if (!networkDungeonConversionInProgress)
                return;

            bool fromLoad = pendingNetworkDungeonConversionFromLoad;
            int worldX = pendingNetworkDungeonWorldX;
            int worldZ = pendingNetworkDungeonWorldZ;

            ClearPendingNetworkDungeonConversionState();
            Debug.LogWarning($"[NetworkDungeonConversion] Failed; using existing exterior safety fallback. reason={reason} world={worldX}/{worldZ}");

            RespawnPlayerDungeonExteriorForNetworkSafety(worldX, worldZ, "network-dungeon-conversion-" + reason);
            dungeon = null;

            if (!fromLoad)
                StartCoroutine(FadeAfterNetworkDungeonConversionFallback());
        }

        private IEnumerator FadeAfterNetworkDungeonConversionFallback()
        {
            while (isRespawning)
                yield return null;

            if (DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(1f);
        }

        private void ClearPendingNetworkDungeonConversionState()
        {
            networkDungeonConversionInProgress = false;
            pendingNetworkDungeonConversionFromLoad = false;
            pendingNetworkDungeonUseStartMarker = false;
            pendingNetworkDungeonRegionName = string.Empty;
            pendingNetworkDungeonLocationName = string.Empty;
            pendingNetworkDungeonLocalPosition = Vector3.zero;
            pendingNetworkDungeonWorldX = 0;
            pendingNetworkDungeonWorldZ = 0;
            pendingNetworkDungeonRequesterLevel = 1;
            pendingNetworkDungeonInitialActionState = string.Empty;
            pendingNetworkDungeonConversionStartedAt = 0f;
        }

        private bool TryBeginCurrentLocalDungeonConversion(string reason)
        {
            DaggerfallDungeon sourceDungeon = dungeon;
            if (sourceDungeon == null || DungeonHasUsableNetworkIdentity(sourceDungeon) || !sourceDungeon.Summary.LocationData.Loaded)
                return false;

            Vector3 localPosition = transform.position - sourceDungeon.transform.position;
            int worldX = playerGPS != null ? playerGPS.WorldX : 0;
            int worldZ = playerGPS != null ? playerGPS.WorldZ : 0;

            // Hosting/joining while already inside a live SP dungeon follows the same
            // first-creator rule as loading a save: capture the current pressed/open
            // state before the source dungeon is destroyed for MP conversion.
            string initialActionState = DaggerfallDungeon.SerializeInitialSavedActionState(
                SaveLoadManager.StateManager.GetActionDoorData(),
                SaveLoadManager.StateManager.GetActionObjectData(),
                sourceDungeon.transform.position);

            return BeginNetworkDungeonConversion(
                sourceDungeon.Summary.LocationData,
                localPosition,
                worldX,
                worldZ,
                false,
                false,
                sourceDungeon,
                DaggerfallDungeon.GetLocalPlayerLevelFallback(),
                initialActionState,
                reason);
        }


        private bool ShouldOffsetBuildingInteriorForMultiplayer()
        {
            return NetworkServer.active || NetworkClient.active || pendingRecoveredMultiplayerInteriorSave;
        }

        private float GetActiveInteriorYOffset()
        {
            return pendingRecoveredMultiplayerInteriorSave ? pendingRecoveredMultiplayerInteriorYOffset : multiplayerInteriorYOffset;
        }

        private Vector3 GetMultiplayerInteriorYOffsetVector(bool useOffset)
        {
            return useOffset ? new Vector3(0f, GetActiveInteriorYOffset(), 0f) : Vector3.zero;
        }

        private Vector3 GetInteriorDoorSearchPositionFromExteriorDoor(StaticDoor door, bool useOffset)
        {
            Vector3 checkPosition = DaggerfallStaticDoors.GetDoorPosition(door);
            if (useOffset)
                checkPosition.y += GetActiveInteriorYOffset();

            return checkPosition;
        }

        private Vector3 GetExteriorDoorSearchPositionFromInteriorPosition(Vector3 interiorPosition)
        {
            Vector3 searchPosition = interiorPosition;
            if (currentInteriorUsesMultiplayerYOffset)
                searchPosition.y -= GetActiveInteriorYOffset();

            return searchPosition;
        }

        /// <summary>
        /// Tries to reconstruct the normal SP root position of the current building interior
        /// from its own exterior door data. This is only a sanity/fallback check; the explicit
        /// currentInteriorUsesMultiplayerYOffset flag remains the primary authority.
        /// </summary>
        private bool TryGetCurrentBuildingInteriorSingleplayerRoot(out Vector3 rootPosition)
        {
            rootPosition = Vector3.zero;
            if (interior == null)
                return false;

            StaticDoor referenceDoor = interior.EntryDoor;
            if (exteriorDoors != null && exteriorDoors.Count > 0)
                referenceDoor = exteriorDoors[0];

            Vector3 matrixOffset = (Vector3)referenceDoor.buildingMatrix.GetColumn(3);
            rootPosition = referenceDoor.ownerPosition + matrixOffset;

            return !float.IsNaN(rootPosition.y) && !float.IsInfinity(rootPosition.y);
        }

        /// <summary>
        /// Determines whether the already-created local interior is at the MP offset. Never
        /// classifies from absolute world Y, because exterior terrain height varies widely.
        /// </summary>
        private bool IsCurrentBuildingInteriorAlreadyMultiplayerOffset()
        {
            if (currentInteriorUsesMultiplayerYOffset)
                return true;

            Vector3 singleplayerRoot;
            if (!TryGetCurrentBuildingInteriorSingleplayerRoot(out singleplayerRoot) || interior == null)
                return false;

            float currentY = interior.transform.position.y;
            float singleplayerError = Mathf.Abs(currentY - singleplayerRoot.y);
            float multiplayerError = Mathf.Abs(currentY - (singleplayerRoot.y + multiplayerInteriorYOffset));

            // Exact/near-exact match handles normal interiors. The relative comparison also
            // tolerates a small world-compensation or floating-point discrepancy.
            return multiplayerError <= 1f || multiplayerError + 25f < singleplayerError;
        }

        /// <summary>
        /// Moves active, detached local save objects that do not inherit the interior root
        /// transform. Normal imported enemies/loot beneath the interior move with the root.
        /// </summary>
        private int ShiftDetachedActiveInteriorSaveObjects(Vector3 delta)
        {
            if (interior == null)
                return 0;

            int moved = 0;
            HashSet<Transform> shifted = new HashSet<Transform>();

            SerializableEnemy[] enemies = FindObjectsOfType<SerializableEnemy>();
            for (int i = 0; i < enemies.Length; i++)
            {
                SerializableEnemy enemy = enemies[i];
                if (enemy == null || !enemy.gameObject.activeInHierarchy)
                    continue;

                Transform target = enemy.transform;
                if (target == null || target.IsChildOf(interior.transform))
                    continue;

                NetworkIdentity identity = target.GetComponent<NetworkIdentity>();
                if (identity != null && identity.netId != 0)
                    continue;

                if (shifted.Add(target))
                {
                    target.position += delta;
                    moved++;
                }
            }

            SerializableLootContainer[] lootContainers = FindObjectsOfType<SerializableLootContainer>();
            for (int i = 0; i < lootContainers.Length; i++)
            {
                SerializableLootContainer loot = lootContainers[i];
                if (loot == null || !loot.gameObject.activeInHierarchy)
                    continue;

                Transform target = loot.transform;
                if (target == null || target.IsChildOf(interior.transform))
                    continue;

                NetworkIdentity identity = target.GetComponent<NetworkIdentity>();
                if (identity != null && identity.netId != 0)
                    continue;

                if (shifted.Add(target))
                {
                    target.position += delta;
                    moved++;
                }
            }

            return moved;
        }

        /// <summary>
        /// Starts the same quest-foe injection pass used by normal building entry after
        /// multiplayer startup has removed the old local/SP enemy. The delay is required
        /// because DestroyNonNetworkedEnemiesForMultiplayerStart() uses deferred Destroy(),
        /// and AddQuestResourceObjects() must not see the old QuestResourceBehaviour.
        /// </summary>
        private void QueueBuildingQuestFoeReplayAfterNetworkStart(string reason)
        {
            if (buildingQuestFoeReplayInProgress || !IsPlayerInsideBuilding || interior == null)
                return;

            DaggerfallInterior targetInterior = interior;
            int buildingKey = targetInterior.EntryDoor.buildingKey;
            StartCoroutine(ReplayBuildingQuestFoesAfterNetworkStart(targetInterior, buildingKey, reason));
        }

        private IEnumerator ReplayBuildingQuestFoesAfterNetworkStart(
            DaggerfallInterior targetInterior,
            int buildingKey,
            string reason)
        {
            buildingQuestFoeReplayInProgress = true;
            float startedAt = Time.realtimeSinceStartup;

            try
            {
                // PlayerMultiplayer.OnStartLocalPlayer() owns the startup cleanup which
                // destroys old non-networked enemies. Wait until that local command owner
                // exists, and do not replay quest resources during save restoration.
                while (NetworkServer.active || NetworkClient.active)
                {
                    if (!IsPlayerInsideBuilding || interior != targetInterior || targetInterior == null)
                    {
                        Debug.Log($"[InteriorQuestFoeReplay] Cancelled because the player left or changed interiors. reason={reason}");
                        yield break;
                    }

                    bool loadInProgress =
                        SaveLoadManager.Instance != null &&
                        SaveLoadManager.Instance.LoadInProgress;

                    PlayerMultiplayer localNetworkPlayer = PlayerMultiplayer.GetLocalPlayer();
                    if (!loadInProgress &&
                        localNetworkPlayer != null &&
                        localNetworkPlayer.isLocalPlayer)
                    {
                        break;
                    }

                    if (Time.realtimeSinceStartup - startedAt > BuildingQuestFoeReplayTimeout)
                    {
                        Debug.LogWarning($"[InteriorQuestFoeReplay] Timed out waiting for multiplayer startup/save load. reason={reason} buildingKey={buildingKey}");
                        yield break;
                    }

                    yield return null;
                }

                if (!NetworkServer.active && !NetworkClient.active)
                    yield break;

                // The SP enemy cleanup calls Unity Destroy(), which is deferred. Let the
                // current frame finish and then allow one additional update so stale
                // QuestResourceBehaviour components cannot block the replacement.
                yield return new WaitForEndOfFrame();
                yield return null;

                while (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress)
                {
                    if (Time.realtimeSinceStartup - startedAt > BuildingQuestFoeReplayTimeout)
                    {
                        Debug.LogWarning($"[InteriorQuestFoeReplay] Timed out waiting for save load completion. reason={reason} buildingKey={buildingKey}");
                        yield break;
                    }

                    yield return null;
                }

                if ((!NetworkServer.active && !NetworkClient.active) ||
                    !IsPlayerInsideBuilding ||
                    interior != targetInterior ||
                    targetInterior == null)
                {
                    yield break;
                }

                // Replay only foes. Existing quest NPCs/items were not removed by the
                // multiplayer-start enemy cleanup and should not be touched or recreated.
                GameObjectHelper.AddQuestResourceObjects(
                    SiteTypes.Building,
                    targetInterior.transform,
                    buildingKey,
                    false,
                    true,
                    false);

                Debug.Log($"[InteriorQuestFoeReplay] Replayed building quest-foe injection after multiplayer startup. reason={reason} buildingKey={buildingKey} host={NetworkServer.active} client={NetworkClient.active}");
            }
            finally
            {
                buildingQuestFoeReplayInProgress = false;
            }
        }


        /// <summary>
        /// Converts an already-live local/SP-height building interior in-place when networking
        /// starts. The interior is still local/non-networked; only its vertical placement changes.
        /// </summary>
        private bool EnsureCurrentBuildingInteriorUsesMultiplayerOffset(string reason)
        {
            if (!IsPlayerInsideBuilding || interior == null)
                return false;

            if (IsCurrentBuildingInteriorAlreadyMultiplayerOffset())
            {
                currentInteriorUsesMultiplayerYOffset = true;
                Debug.Log($"[InteriorNetworkSafety] Kept existing MP-offset interior on network start. reason={reason} interior={interior.name} interiorY={interior.transform.position.y}");
                return true;
            }

            if (interiorNetworkOffsetConversionInProgress)
                return true;

            interiorNetworkOffsetConversionInProgress = true;
            try
            {
                Vector3 oldInteriorPosition = interior.transform.position;
                Vector3 oldPlayerPosition = transform.position;
                Vector3 delta = new Vector3(0f, multiplayerInteriorYOffset, 0f);

                if (float.IsNaN(oldInteriorPosition.y) || float.IsInfinity(oldInteriorPosition.y) ||
                    float.IsNaN(oldPlayerPosition.y) || float.IsInfinity(oldPlayerPosition.y))
                {
                    Debug.LogWarning($"[InteriorNetworkSafety] Refusing invalid in-place interior conversion. reason={reason} interiorPos={oldInteriorPosition} playerPos={oldPlayerPosition}");
                    return false;
                }

                ClearTransitionFallingDamage(reason + "-before-interior-offset");

                // Children of the DaggerfallInterior (normal layout, imported enemies, doors,
                // quest resources, etc.) inherit this move automatically.
                interior.transform.position = oldInteriorPosition + delta;

                // Some runtime save objects can be detached/parentless. Move those once as well
                // so they cannot remain at the old SP-height copy.
                int detachedObjectsMoved = ShiftDetachedActiveInteriorSaveObjects(delta);

                transform.position = oldPlayerPosition + delta;
                currentInteriorUsesMultiplayerYOffset = true;

                SetStanding();
                ClearTransitionFallingDamageWindow(reason + "-after-interior-offset");
                ForceSendMultiplayerCoordinatesNow(reason + "-interior-offset-complete");

                Debug.Log($"[InteriorNetworkSafety] Converted live SP-height interior to MP offset without exiting. reason={reason} interior={interior.name} oldRoot={oldInteriorPosition} newRoot={interior.transform.position} oldPlayer={oldPlayerPosition} newPlayer={transform.position} yOffset={multiplayerInteriorYOffset} detachedSaveObjectsMoved={detachedObjectsMoved}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[InteriorNetworkSafety] In-place interior MP-offset conversion failed. reason={reason} error={ex}");
                return false;
            }
            finally
            {
                interiorNetworkOffsetConversionInProgress = false;
            }
        }


        #region Public Methods

        /// <summary>
        /// Respawn player at the specified world coordinates, optionally inside dungeon.
        /// </summary>
        public void RespawnPlayer(
            int worldX,
            int worldZ,
            bool insideDungeon = false,
            bool importEnemies = true)
        {
            RespawnPlayer(worldX, worldZ, insideDungeon, false, null, false, importEnemies);
        }

        /// <summary>
        /// Compatibility wrapper for older vampire MP test files.
        /// In true SP this intentionally does exactly the same as vanilla vampire wake-up:
        /// RespawnPlayer(worldX, worldZ, insideDungeon: true, importEnemies: false).
        /// </summary>
        public void RespawnPlayerVampireCemetery(int worldX, int worldZ)
        {
            RespawnPlayer(worldX, worldZ, true, false);
        }

        /// <summary>
        /// Respawn player at the specified world coordinates, optionally inside dungeon or building.
        /// Player can be forced to respawn to closest start marker or origin.
        /// </summary>
        public void RespawnPlayer(
            int worldX,
            int worldZ,
            bool insideDungeon,
            bool insideBuilding,
            StaticDoor[] exteriorDoors = null,
            bool forceReposition = false,
            bool importEnemies = true,
            bool start = true)
        {
            // Mark any existing world data for destruction
            // In multiplayer DestroyIfMultiplayerSafe() intentionally leaves network dungeons alive,
            // but this player must not keep using a stale dungeon reference after loading/respawning
            // somewhere else. Otherwise the next dungeon entrance can reuse the previous dungeon object.
            if (dungeon)
            {
                PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
                dungeon = null;
            }
            if (interior)
            {
                Destroy(interior.gameObject);
                interior = null;
            }

            // Deregister all serializable objects
            SaveLoadManager.DeregisterAllSerializableGameObjects();

            // Start respawn process
            isRespawning = true;
            SetExteriorDoors(exteriorDoors);
            StartCoroutine(Respawner(worldX, worldZ, insideDungeon, insideBuilding, forceReposition, importEnemies, start));
        }

        IEnumerator Respawner(int worldX, int worldZ, bool insideDungeon, bool insideBuilding, bool forceReposition, bool importEnemies, bool start = true)
        {
            // Wait for end of frame so existing world data can be removed
            yield return new WaitForEndOfFrame();

            // Store if player was inside a dungeon or building before respawning
            bool playerWasInDungeon = IsPlayerInsideDungeon;
            bool playerWasInBuilding = IsPlayerInsideBuilding;

            // Reset dungeon block on new spawn
            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();

            // Reset inside state
            isPlayerInside = false;
            isPlayerInsideDungeon = false;
            isPlayerInsideDungeonCastle = false;
            if (NetworkServer.active || NetworkClient.active)
                ClearDungeonWaterState();
            else
                blockWaterLevel = 10000;

            // Respawning/loading outside of a dungeon must clear this player's current dungeon
            // pointer even when the old network dungeon GameObject remains alive in the scene.
            if (!insideDungeon)
                dungeon = null;
            if (!insideBuilding)
                interior = null;

            // Set player GPS coordinates
            playerGPS.WorldX = worldX;
            playerGPS.WorldZ = worldZ;

            // Set streaming world coordinates
            DFPosition pos = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
            world.MapPixelX = pos.X;
            world.MapPixelY = pos.Y;

            // Get location at this position
            ContentReader.MapSummary summary;
            bool hasLocation = dfUnity.ContentReader.HasLocation(pos.X, pos.Y, out summary);

            if (!insideDungeon && !insideBuilding)
            {
                // Start outside
                EnableExteriorParent();
                if (!forceReposition)
                {
                    // Teleport to explicit world coordinates
                    world.TeleportToWorldCoordinates(worldX, worldZ);
                }
                else
                {
                    // Force reposition to closest start marker if available
                    world.TeleportToCoordinates(pos.X, pos.Y, StreamingWorld.RepositionMethods.RandomStartMarker);
                }

                // Wait until world is ready
                while (world.IsInit)
                    yield return new WaitForEndOfFrame();

                // Raise transition exterior event if player was inside a dungeon or building
                // This helps inform other systems player has transitioned to exterior without clicking a door or reloading game
                if (playerWasInDungeon)
                    RaiseOnTransitionDungeonExteriorEvent();
                else if (playerWasInBuilding)
                    RaiseOnTransitionExteriorEvent();
            }
            else if (hasLocation && insideDungeon)
            {
                // Start in dungeon
                DFLocation location;
                world.TeleportToCoordinates(pos.X, pos.Y, StreamingWorld.RepositionMethods.None);
                dfUnity.ContentReader.GetLocation(summary.RegionIndex, summary.MapIndex, out location);
                StartDungeonInterior(location, true, importEnemies);
                world.suppressWorld = false;
            }
            else if (hasLocation && insideBuilding && exteriorDoors != null)
            {
                // Start in building
                DFLocation location;
                world.TeleportToCoordinates(pos.X, pos.Y, StreamingWorld.RepositionMethods.None);
                dfUnity.ContentReader.GetLocation(summary.RegionIndex, summary.MapIndex, out location);
                StartBuildingInterior(location, exteriorDoors[0], start);
                world.suppressWorld = false;
            }
            else
            {
                // All else fails teleport to map pixel
                DaggerfallUnity.LogMessage("Something went wrong! Teleporting to origin of nearest map pixel.");
                EnableExteriorParent();
                world.TeleportToCoordinates(pos.X, pos.Y);
            }

            // Lower respawn flag
            isRespawning = false;

            RaiseOnRespawnerCompleteEvent();
        }

        /// <summary>
        /// Shows UI message with text for current holiday, if any.
        /// </summary>
        public void ShowHolidayText()
        {
            const int holidaysStartID = 8349;

            uint minutes = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            int holidayId = Formulas.FormulaHelper.GetHolidayId(minutes, GameManager.Instance.PlayerGPS.CurrentRegionIndex);

            if (holidayId != 0)
            {
                DaggerfallMessageBox messageBox = new DaggerfallMessageBox(DaggerfallUI.UIManager);
                messageBox.SetTextTokens(holidaysStartID + holidayId);
                messageBox.ClickAnywhereToClose = true;
                messageBox.ParentPanel.BackgroundColor = Color.clear;
                messageBox.ScreenDimColor = new Color32(0, 0, 0, 0);
                messageBox.Show();
            }

            // Set holiday text timer to a somewhat large value so it doesn't show again and again if the player is repeatedly crossing the
            // border of a city.
            holidayTextTimer = 10f;
        }

        /// <summary>
        /// Helper to reposition player to anywhere in world either at load or during player.
        /// </summary>
        /// <param name="playerPosition">Player position data.</param>
        /// <param name="start">Use true if this is a load/start operation, otherwise false.</param>
        public void RestorePositionHelper(
            PlayerPositionData_v1 playerPosition,
            bool start,
            bool importEnemies,
            int savedPlayerLevel = 0,
            string initialSavedDungeonActionState = null)
        {
            // Raise reposition flag if terrain sampler changed
            // This is required as changing terrain samplers will invalidate serialized player coordinates
            // Make an exception for dungeons as exterior world does not matter
            bool repositionPlayer = false;
            if ((playerPosition.terrainSamplerName != DaggerfallUnity.Instance.TerrainSampler.ToString() ||
                playerPosition.terrainSamplerVersion != DaggerfallUnity.Instance.TerrainSampler.Version) &&
                !playerPosition.insideDungeon)
            {
                repositionPlayer = true;
                if (DaggerfallUI.Instance.DaggerfallHUD != null)
                    DaggerfallUI.Instance.DaggerfallHUD.PopupText.AddText("Terrain sampler changed. Repositioning player.");
            }

            // Check exterior doors are included in save, we need these to exit building
            bool hasExteriorDoors;
            if (playerPosition.exteriorDoors == null || playerPosition.exteriorDoors.Length == 0)
                hasExteriorDoors = false;
            else
                hasExteriorDoors = true;

            // Raise reposition flag if player is supposed to start indoors but building has no doors
            if (playerPosition.insideBuilding && !hasExteriorDoors)
            {
                repositionPlayer = true;
                if (DaggerfallUI.Instance.DaggerfallHUD != null)
                    DaggerfallUI.Instance.DaggerfallHUD.PopupText.AddText("Building has no exterior doors. Repositioning player.");
            }

            bool multiplayerActive = NetworkServer.active || NetworkClient.active;

            // Saves made inside MP-offset local building interiors need the same temporary
            // Y offset when loaded in SP, otherwise the interior is recreated at normal SP Y
            // while the saved player/enemy positions remain at the MP offset. In MP this is already
            // handled by the normal active-network offset path.
            if (playerPosition.savedInsideMultiplayerInterior)
            {
                pendingRecoveredMultiplayerInteriorSave = !multiplayerActive;
                pendingRecoveredMultiplayerInteriorYOffset = playerPosition.savedMultiplayerInteriorYOffset != 0f
                    ? playerPosition.savedMultiplayerInteriorYOffset
                    : multiplayerInteriorYOffset;

                Debug.Log($"[NetworkInteriorSave] Loading MP-offset interior save. networkActive={multiplayerActive} pendingOffset={pendingRecoveredMultiplayerInteriorSave} yOffset={pendingRecoveredMultiplayerInteriorYOffset}");
            }
            else
            {
                pendingRecoveredMultiplayerInteriorSave = false;
                pendingRecoveredMultiplayerInteriorYOffset = multiplayerInteriorYOffset;
            }

            // Any SP/MP dungeon save loaded while networking is active now enters through
            // the same host-authoritative dungeon request path. The saved instance id is
            // deliberately not used: region/location owns the live shared dungeon.
            if (multiplayerActive && playerPosition.insideDungeon)
            {
                DFLocation savedDungeonLocation;
                if (TryResolveSavedDungeonLocation(playerPosition, out savedDungeonLocation))
                {
                    Vector3 localPosition = GetSavedDungeonLocalPosition(playerPosition);
                    bool useStartMarker = ShouldUseDungeonStartMarkerForLoadedLayout(playerPosition, savedDungeonLocation);
                    int conversionWorldX;
                    int conversionWorldZ;
                    ResolveSavedDungeonEntrance(
                        savedDungeonLocation,
                        playerPosition.worldPosX,
                        playerPosition.worldPosZ,
                        out conversionWorldX,
                        out conversionWorldZ);

                    playerPosition.suppressNetworkDungeonPositionRestore = true;
                    if (BeginNetworkDungeonConversion(
                        savedDungeonLocation,
                        localPosition,
                        conversionWorldX,
                        conversionWorldZ,
                        true,
                        useStartMarker,
                        null,
                        savedPlayerLevel,
                        initialSavedDungeonActionState,
                        "active-mp-dungeon-save-load"))
                    {
                        return;
                    }
                }

                Debug.LogWarning("[NetworkDungeonSave] Could not resolve/start saved dungeon conversion. Restoring outside at dungeon entrance.");
                playerPosition.suppressNetworkDungeonPositionRestore = true;
                RespawnPlayerDungeonExteriorForNetworkSafety(playerPosition.worldPosX, playerPosition.worldPosZ, "networked-load-dungeon-conversion-start-failed");
                return;
            }

            // True SP recovery of a save made inside an MP dungeon keeps the old saved
            // Y slot so the serialized player/enemy positions still line up locally.
            if (playerPosition.savedInsideNetworkDungeon)
            {
                pendingRecoveredNetworkDungeonSave = true;
                pendingRecoveredNetworkDungeonY = playerPosition.savedNetworkDungeonY;

                Debug.Log($"[NetworkDungeonSave] Loading MP dungeon save in SP. Reconstructing local recovery dungeon at y={pendingRecoveredNetworkDungeonY}");
            }

            // Start the respawn process based on saved player location
            if (playerPosition.insideDungeon/* && !repositionPlayer*/) // Do not need to resposition outside for dungeons
            {
                // Start in dungeon
                RespawnPlayer(
                    playerPosition.worldPosX,
                    playerPosition.worldPosZ,
                    true,
                    importEnemies);
            }
            else if (playerPosition.insideBuilding && hasExteriorDoors && !repositionPlayer)
            {
                // Start in building
                RespawnPlayer(
                    playerPosition.worldPosX,
                    playerPosition.worldPosZ,
                    playerPosition.insideDungeon,
                    playerPosition.insideBuilding,
                    playerPosition.exteriorDoors,
                    false,
                    false,
                    start);
            }
            else
            {
                // Start outside
                RespawnPlayer(
                    playerPosition.worldPosX,
                    playerPosition.worldPosZ,
                    false,
                    false,
                    null,
                    repositionPlayer);
            }
        }

        #endregion

        #region Building Transitions


/*public void TeleportIntoNetworkedInterior(DaggerfallInteriorNetwork netComp, StaticDoor door, bool doFade = true)
{
    StartCoroutine(WaitAndTeleportPlayer(netComp, door, doFade));
}*/



/*
private IEnumerator DelayedTargetEnterInterior(DaggerfallInteriorNetwork netComp, DaggerfallInteriorNetwork.InteriorNetworkData data, NetworkConnection conn)
{
    yield return new WaitForSeconds(0.3f); // Ensure spawn reaches client
    if (netComp != null && conn != null)
    {
        Debug.Log("[InteriorNet] Calling TargetEnterInterior now.");
        netComp.TargetEnterInterior(conn, data);
    }
    else
    {
        Debug.LogWarning("[InteriorNet] Could not call TargetEnterInterior: netComp or conn is null.");
    }
}*/




public static DaggerfallInteriorNetwork SpawnNetworkedInterior(
    string region,
    string location,
    int buildingKey,
    StaticDoor door,
    ClimateBases climateBase = ClimateBases.Temperate,
    PlayerGPS.DiscoveredBuilding discovery = default,
    Transform doorOwner = null)
{
    var prefab = NetworkManager.singleton.spawnPrefabs
        .FirstOrDefault(p => p.GetComponent<DaggerfallInteriorNetwork>() != null);

    if (!prefab)
    {
        Debug.LogError("[InteriorNet] ❌ No interior prefab found in spawnPrefabs.");
        return null;
    }

    GameObject netInterior = GameObject.Instantiate(prefab);
    netInterior.transform.position = Vector3.zero;
    netInterior.transform.rotation = Quaternion.identity;

    var netComp = netInterior.GetComponent<DaggerfallInteriorNetwork>();

    netComp.SetInteriorData(
        region,
        location,
        buildingKey,
        door.ownerPosition.x,
        door.ownerPosition.y,
        door.ownerPosition.z,
        door,
        climateBase,
        discovery,
        doorOwner
    );

    DaggerfallInterior interior = netComp.GetComponent<DaggerfallInterior>();
    if (interior)
    {
        interior.DoLayout(doorOwner, door, climateBase, discovery);
        Debug.Log($"[InteriorNet] Host ran DoLayout for buildingKey={buildingKey}");
		if (interior.EntryDoor.buildingKey != 0)
{
    GameObjectHelper.AddQuestResourceObjects(SiteTypes.Building, interior.transform, interior.EntryDoor.buildingKey);
    Debug.Log($"[InteriorNet] ✅ Added quest resource objects (host) for buildingKey={interior.EntryDoor.buildingKey}");
}
    }
    else
    {
        Debug.LogError("[InteriorNet] ❌ No DaggerfallInterior found!");
    }

    Vector3 worldOffset = GameManager.Instance.StreamingWorld.WorldCompensation;
    Vector3 finalPos = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3) + worldOffset;
    finalPos.y += multiplayerInteriorYOffset;

    netInterior.transform.position = finalPos;
    netInterior.transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

    NetworkServer.Spawn(netInterior);

    Debug.Log($"[InteriorNet] ✅ Spawned interior key={buildingKey} at {finalPos}");
    return netComp;
}





private IEnumerator WaitAndTeleportPlayer(DaggerfallInteriorNetwork netComp, StaticDoor door)
{
    Debug.Log("[InteriorNet] Host: Starting WaitAndTeleportPlayer coroutine...");

    // Wait until interior is fully initialized
    while (!netComp.IsReady)
        yield return null;

    DaggerfallInterior interior = netComp.GetComponent<DaggerfallInterior>();
    if (!interior)
    {
        Debug.LogError("[InteriorNet] ❌ Could not find DaggerfallInterior in prefab.");
        yield break;
    }

    GameManager.Instance.PlayerEnterExit.interior = interior;

    // ✅ Patch door with real scene door owner (based on doorIndex, recordIndex, buildingKey)
    Transform realOwner = PlayerEnterExit.FindDoorOwnerInScene(door);
    if (realOwner != null)
    {
        door.ownerPosition = realOwner.position;
        door.ownerRotation = realOwner.rotation;
        Debug.Log("[InteriorNet] ✅ Patched door with real owner transform.");
    }
    else
    {
        Debug.LogWarning("[InteriorNet] ⚠ Failed to find real door owner. Teleport may be inaccurate.");
    }

    // ✅ Patch ExteriorDoors list with correct door owner info (needed for exit logic)
    DaggerfallStaticDoors exteriorStaticDoors = interior.ExteriorDoors;
    if (exteriorStaticDoors != null)
    {
        List<StaticDoor> buildingDoors = new List<StaticDoor>();

        foreach (StaticDoor d in exteriorStaticDoors.Doors)
        {
            if (d.recordIndex == door.recordIndex)
            {
                StaticDoor newDoor = d;
                newDoor.ownerPosition = door.ownerPosition;
                newDoor.ownerRotation = door.ownerRotation;
                buildingDoors.Add(newDoor);
            }
        }

        if (buildingDoors.Count > 0)
        {
            GameManager.Instance.PlayerEnterExit.SetExteriorDoors(buildingDoors.ToArray());
            Debug.Log($"[InteriorNet] ✅ Set {buildingDoors.Count} exterior doors using synced transform.");
        }
        else
        {
            Debug.LogWarning("[InteriorNet] ⚠ No matching doors found in ExteriorDoors.");
        }
    }
    else
    {
        Debug.LogWarning("[InteriorNet] ⚠ exteriorStaticDoors was null.");
    }

  /*  // ✅ Match exact interior door (recordIndex + doorIndex)
    StaticDoor? matchedDoor = null;
    StaticDoor[] allDoors = interior.ExteriorDoors?.Doors;

    if (allDoors != null)
    {
        foreach (StaticDoor d in allDoors)
        {
            if (d.recordIndex == door.recordIndex && d.doorIndex == door.doorIndex)
            {
                matchedDoor = d;
                break;
            }
        }
    }

    // ✅ Use the matched interior door (or fallback) for initial position
    Vector3 checkPosition;
    if (matchedDoor.HasValue)
    {
        checkPosition = DaggerfallStaticDoors.GetDoorPosition(matchedDoor.Value);
        Debug.Log($"[InteriorNet] ✅ Matched doorIndex={matchedDoor.Value.doorIndex} at position {checkPosition}");
    }
    else
    {
        checkPosition = DaggerfallStaticDoors.GetDoorPosition(door);
        Debug.LogWarning("[InteriorNet] ⚠ No exact match for doorIndex, using fallback door position.");
    }*/
// ✅ Use the clicked door position for entry. Networked interiors are placed at the
// same X/Z as exterior buildings but shifted down by multiplayerInteriorYOffset.
GameManager.Instance.PlayerEnterExit.currentInteriorUsesMultiplayerYOffset = true;
Vector3 checkPosition = GetInteriorDoorSearchPositionFromExteriorDoor(door, true);

// ✅ Refine using enter marker
if (interior.FindClosestEnterMarker(checkPosition, out Vector3 closestEnterMarkerPosition))
    checkPosition = closestEnterMarkerPosition;

// ✅ Extract correct doorIndex from interior doors (match by recordIndex + doorIndex)
StaticDoor[] allInteriorDoors = interior.ExteriorDoors?.Doors;
StaticDoor? matchedDoor = null;

if (allInteriorDoors != null)
{
    foreach (StaticDoor d in allInteriorDoors)
    {
        if (d.recordIndex == door.recordIndex && d.doorIndex == door.doorIndex)
        {
            matchedDoor = d;
            break;
        }
    }
}

if (matchedDoor.HasValue)
{
    Debug.Log($"[InteriorNet] ✅ Found correct doorIndex={matchedDoor.Value.doorIndex} in interior.ExteriorDoors.");
}
else
{
    Debug.LogWarning("[InteriorNet] ⚠ Could not find matching door in interior.ExteriorDoors.");
}

// ✅ Use refined checkPosition to find landing point
Vector3 landingPosition;
Vector3 foundDoorNormal;

if (interior.FindClosestInteriorDoor(checkPosition, out landingPosition, out foundDoorNormal))
{
    landingPosition += foundDoorNormal * (GameManager.Instance.PlayerController.radius + 0.4f);
}
else if (interior.FindClosestEnterMarker(checkPosition, out landingPosition))
{
    landingPosition += Vector3.up * (GameManager.Instance.PlayerController.height * 0.6f);
}
else
{
    Debug.LogWarning("[InteriorNet] ❌ Could not find valid landing point. Aborting teleport.");
    yield break;
}

// ✅ Finalize interior transition
GameManager.Instance.PlayerEnterExit.ClearTransitionFallingDamage("networked-interior-before-teleport");
GameManager.Instance.PlayerObject.transform.position = landingPosition;
GameManager.Instance.PlayerEnterExit.SetStanding();
GameManager.Instance.PlayerEnterExit.ClearTransitionFallingDamageWindow("networked-interior-after-teleport");
GameManager.Instance.PlayerEnterExit.EnableInteriorParent();

Debug.Log("[InteriorNet] ✅ Player teleported to interior.");


Debug.Log($"[InteriorNet] Host landed at {landingPosition}");

}










IEnumerator WaitForInteriorAndEnter(StaticDoor clickedDoor)
{
    DaggerfallInteriorNetwork targetInterior = null;
    float timeout = Time.time + 10f;

    while (Time.time < timeout)
    {
        targetInterior = FindObjectsOfType<DaggerfallInteriorNetwork>()
            .FirstOrDefault(i => i.buildingKey == clickedDoor.buildingKey && i.IsReady);

        if (targetInterior != null)
            break;

        yield return null;
    }

    if (targetInterior == null)
    {
        Debug.LogError("[InteriorNet] ❌ Timed out waiting for networked interior.");
        yield break;
    }

    Debug.Log("[InteriorNet] ✅ Found ready interior, transitioning...");

    // ✅ Use interior.ExteriorDoors to get the actual door
    StaticDoor[] allDoors = targetInterior.GetComponent<DaggerfallInterior>()?.ExteriorDoors?.Doors;
    StaticDoor replacementDoor = clickedDoor;

    if (allDoors != null)
    {
        foreach (StaticDoor door in allDoors)
        {
if (door.recordIndex == clickedDoor.recordIndex && door.doorIndex == clickedDoor.doorIndex)
{
    replacementDoor = door;
    break;
}
        }
    }

    // Teleport player using the corrected door data
// ✅ Try to find the real door owner block (scene block, not dummy)
Transform realOwner = PlayerEnterExit.FindDoorOwnerInScene(clickedDoor);
if (realOwner != null)
{
    var interior = targetInterior.GetComponent<DaggerfallInterior>();
    if (interior != null)
    {
        interior.OverrideDoorOwner(realOwner); // custom method we add in next step
        Debug.Log("[InteriorNet] ✅ Overwrote interior doorOwner with real scene block.");
    }
    else
    {
        Debug.LogWarning("[InteriorNet] ⚠ Interior was null when trying to overwrite door owner.");
    }
}
else
{
    Debug.LogWarning("[InteriorNet] ⚠ Could not find real door owner block to overwrite.");
}

// Continue with teleport
StartCoroutine(GameManager.Instance.PlayerEnterExit.WaitAndTeleportPlayer(targetInterior, replacementDoor));


  
}



public static Transform FindDoorOwnerInScene(StaticDoor door)
{
    foreach (var dsd in GameObject.FindObjectsOfType<DaggerfallStaticDoors>())
    {
        if (dsd.Doors == null)
            continue;

        foreach (var d in dsd.Doors)
        {
            if (d.buildingKey == door.buildingKey &&
                d.recordIndex == door.recordIndex &&
                d.doorIndex == door.doorIndex) // ✅ Match doorIndex too
            {
                return dsd.transform;
            }
        }
    }

    return null;
}





         /// <summary>
        /// Transition player through an exterior door into building interior.
        /// </summary>
        /// <param name="doorOwner">Parent transform owning door array..</param>
        /// <param name="door">Exterior door player clicked on.</param>
        public void TransitionInterior(Transform doorOwner, StaticDoor door, bool doFade = false, bool start = true, uint requestingNetId = 0)

        {
			Debug.Log($"[TransitionInterior] NetServer={NetworkServer.active}, NetClient={NetworkClient.active}");
            // Store start flag
            lastInteriorStartFlag = start;
			
			Vector3 closestEnterMarkerPosition;
Vector3 checkPosition;
Vector3 landingPosition = Vector3.zero;
Vector3 foundDoorNormal = Vector3.zero;

            // Ensure we have component references
            if (!ReferenceComponents())
                return;


            // Copy owner position to door
            // This ensures the door itself is all we need to reposition interior
            // Useful when loading a save and doorOwner is null (as outside world does not exist)
            if (doorOwner)
            {
                door.ownerPosition = doorOwner.position;
                door.ownerRotation = doorOwner.rotation;
            }

            if (!start)
            {
                // Update scene cache from serializable state for exterior->interior transition
                SaveLoadManager.CacheScene(world.SceneName);
                // Explicitly deregister all stateful objects since exterior isn't destroyed
                SaveLoadManager.DeregisterAllSerializableGameObjects(true);
                // Clear all stateful objects from world loose object tracking
                world.ClearStatefulLooseObjects();
            }

            // Ensure building variant checks use this location.
            WorldDataVariants.SetLastLocationKeyTo(playerGPS.CurrentRegionIndex, playerGPS.CurrentLocationIndex);

            // Raise event
            RaiseOnPreTransitionEvent(TransitionType.ToBuildingInterior, door);

            // Ensure expired rooms are removed
            GameManager.Instance.PlayerEntity.RemoveExpiredRentedRooms();

            // Get climate
            ClimateBases climateBase = ClimateBases.Temperate;
            if (OverrideLocation)
                climateBase = OverrideLocation.Summary.Climate;
            else if (playerGPS)
                climateBase = ClimateSwaps.FromAPIClimateBase(playerGPS.ClimateSettings.ClimateType);
			
			
			
/*
if (NetworkServer.active)
{
    Debug.Log("[InteriorNet] Host: Starting networked interior transition...");

    // Try to get from cache first
if (!PlayerMultiplayer.BuildingDiscoveryCache.TryGet(door.buildingKey, out PlayerGPS.DiscoveredBuilding discoveryNet))
{
    GameManager.Instance.PlayerGPS.GetDiscoveredBuilding(door.buildingKey, out discoveryNet);

    if (discoveryNet.buildingKey != 0)
    {
        PlayerMultiplayer.BuildingDiscoveryCache.AddOrUpdate(discoveryNet);
        Debug.Log($"[InteriorNet] Host: Added discovery for buildingKey={door.buildingKey} to cache.");
    }
    else
    {
        Debug.LogWarning($"[InteriorNet] ⚠️ No discovery found for buildingKey={door.buildingKey}, using dummy.");
        discoveryNet = new PlayerGPS.DiscoveredBuilding { buildingKey = door.buildingKey };
    }
}

// Try to find the real doorOwner in the scene
Transform realDoorOwner = FindDoorOwnerInScene(door);
if (realDoorOwner != null)
{
    Debug.Log("[InteriorNet] ✅ Found real doorOwner in scene, using that instead of fallback.");
    doorOwner = realDoorOwner;
}
else
{
    Debug.LogWarning("[InteriorNet] ⚠️ Using fallback doorOwner, real scene object not found.");
}
    var netComp = PlayerEnterExit.SpawnNetworkedInterior(
        GameManager.Instance.PlayerGPS.CurrentRegionName,
        GameManager.Instance.PlayerGPS.CurrentLocation.Name,
        door.buildingKey,
        door,
        climateBase,
        discoveryNet,
        doorOwner
    );

    if (netComp != null)
        StartCoroutine(GameManager.Instance.PlayerEnterExit.WaitAndTeleportPlayer(netComp, door, true));

    return;
}



else */  /*if (NetworkClient.active)
{
		            // Raise event
            RaiseOnTransitionInteriorEvent(door, interior);

            // Fade in from black
            if (doFade)
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack();
			
	    // ✅ STORE THE REAL DOOR OWNER NOW
    PlayerEnterExit.realSceneDoorOwner = FindDoorOwnerInScene(door);
    PlayerMultiplayer localNetPlayer = PlayerMultiplayer.localPlayer;
    if (!localNetPlayer)
        localNetPlayer = FindObjectsOfType<PlayerMultiplayer>().FirstOrDefault(p => p.isLocalPlayer);

    if (localNetPlayer != null)
    {
        if (!FindObjectsOfType<DaggerfallInteriorNetwork>().Any(i => i.buildingKey == door.buildingKey))
        {
            Debug.Log("[InteriorNet] Client: Sending full interior data request to host.");

            var gps = GameManager.Instance.PlayerGPS;

            // Use door owner or fallback
            string blockName;
            Transform fallbackDoorOwner = DaggerfallInteriorNetwork.FindExteriorDoorOwner(door.buildingKey, gps.CurrentLocation.Name);
            if (fallbackDoorOwner)
                blockName = fallbackDoorOwner.name;
            else
                blockName = "UNKNOWN_BLOCK";

GameManager.Instance.PlayerGPS.GetDiscoveredBuilding(door.buildingKey, out var discovered);

var data = new DaggerfallInteriorNetwork.InteriorNetworkData
{
    regionName = gps.CurrentRegionName,
    locationName = gps.CurrentLocation.Name,
    regionIndex = gps.CurrentRegionIndex,
    locationIndex = gps.CurrentLocationIndex,
    buildingKey = door.buildingKey,
    posX = door.ownerPosition.x,
    posY = door.ownerPosition.y,
    posZ = door.ownerPosition.z,
    recordIndex = door.recordIndex,
    doorIndex = door.doorIndex,
    blockIndex = door.blockIndex,
    doorPosition = door.centre,
    doorNormal = door.normal,
    doorOwnerPosition = door.ownerPosition,
    doorOwnerRotation = door.ownerRotation,
    buildingMatrix = door.buildingMatrix,
    climate = ClimateBases.Temperate,
    blockName = blockName,
    discoveredBuilding = discovered
};


            localNetPlayer.CmdRequestInteriorFromHostFull(data);
        }

        StartCoroutine(WaitForInteriorAndEnter(door));
		
    }
    else
    {
        Debug.LogError("[InteriorNet] ❌ Client could not find local player.");
    }
	

        

    return;
}*/








		
			
			
    // ---- SINGLEPLAYER FALLBACK (ONLY EXECUTED IF NOT HOSTING) ----
            // Layout interior
            // This needs to be done first so we know where the enter markers are
            GameObject newInterior = new GameObject(DaggerfallInterior.GetSceneName(playerGPS.CurrentLocation, door));
            newInterior.hideFlags = defaultHideFlags;
            interior = newInterior.AddComponent<DaggerfallInterior>();

            // Try to layout interior
            // If we fail for any reason, use that old chestnut "this house has nothing of value"
            try
            {
                interior.DoLayout(doorOwner, door, climateBase, buildingDiscoveryData);
            }
            catch (Exception e)
            {
                DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("thisHouseHasNothingOfValue"));
                Debug.LogException(e);
                Destroy(newInterior);
                RaiseOnFailedTransition(TransitionType.ToBuildingInterior);
                return;
            }


// Get the discovery data
PlayerGPS.DiscoveredBuilding discovery;
GameManager.Instance.PlayerGPS.GetDiscoveredBuilding(door.buildingKey, out discovery);




            // Position interior directly inside of exterior
            // This helps with finding closest enter/exit point relative to player position
            bool useMultiplayerInteriorYOffset = ShouldOffsetBuildingInteriorForMultiplayer();
            currentInteriorUsesMultiplayerYOffset = useMultiplayerInteriorYOffset;

            interior.transform.position = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3) + GetMultiplayerInteriorYOffsetVector(useMultiplayerInteriorYOffset);
            interior.transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

            if (useMultiplayerInteriorYOffset)
                Debug.Log($"[InteriorYOffset] Spawned offset building interior at {interior.transform.position} using yOffset={GetActiveInteriorYOffset()} pendingRecoveredMPInterior={pendingRecoveredMultiplayerInteriorSave}.");

            // Find closest enter marker to exterior door position within building interior
            // If a marker is found, it will be used as the new check position to find actual interior door
 
checkPosition = GetInteriorDoorSearchPositionFromExteriorDoor(door, useMultiplayerInteriorYOffset);
if (interior.FindClosestEnterMarker(checkPosition, out closestEnterMarkerPosition))
    checkPosition = closestEnterMarkerPosition;

            // Position player in front of closest interior door.
            // Normally DFU trusts the door normal to point toward the usable interior side. A malformed
            // or replacement interior can have that normal effectively reversed, placing the player in
            // empty space outside the room. Keep the normal DFU side whenever it has valid interior floor;
            // only flip to the opposite side when the normal side is unsafe and the opposite side is
            // positively validated as standing over floor belonging to this same interior.
            if (interior.FindClosestInteriorDoor(checkPosition, out landingPosition, out foundDoorNormal))
            {
                Vector3 interiorDoorPosition = landingPosition;
                float doorLandingOffset = GameManager.Instance.PlayerController.radius + 0.4f;
                Vector3 normalSideLanding = interiorDoorPosition + foundDoorNormal * doorLandingOffset;
                Vector3 oppositeSideLanding = interiorDoorPosition - foundDoorNormal * doorLandingOffset;

                landingPosition = normalSideLanding;

                string normalSideReason;
                if (!IsSafeInteriorDoorLandingCandidate(interior, normalSideLanding, out normalSideReason))
                {
                    string oppositeSideReason;
                    if (IsSafeInteriorDoorLandingCandidate(interior, oppositeSideLanding, out oppositeSideReason))
                    {
                        landingPosition = oppositeSideLanding;
                        Debug.LogWarning(
                            $"[InteriorDoorLandingSafety] Normal door side was unsafe, using verified-safe opposite side. " +
                            $"interior={interior.name} door={interiorDoorPosition} normal={foundDoorNormal} " +
                            $"normalLanding={normalSideLanding} normalReason={normalSideReason} " +
                            $"oppositeLanding={oppositeSideLanding}.");
                    }
                    else
                    {
                        Debug.Log(
                            $"[InteriorDoorLandingSafety] Normal door side was not positively safe, but opposite side was not safe either; " +
                            $"keeping normal DFU landing. interior={interior.name} door={interiorDoorPosition} " +
                            $"normalReason={normalSideReason} oppositeReason={oppositeSideReason}");
                    }
                }
            }
            else
            {
                // If no door found position player above closest enter marker
                if (interior.FindClosestEnterMarker(checkPosition, out landingPosition))
                {
                    landingPosition += Vector3.up * (controller.height * 0.6f);
                }
                else
                {
                    // Could not find an door or enter marker, probably not a valid interior
                    Destroy(newInterior);
                    RaiseOnFailedTransition(TransitionType.ToBuildingInterior);
                    return;
                }
            }

            // Enumerate all exterior doors belonging to this building
            DaggerfallStaticDoors exteriorStaticDoors = interior.ExteriorDoors;
            if (exteriorStaticDoors && doorOwner)
            {
                List<StaticDoor> buildingDoors = new List<StaticDoor>();
                for (int i = 0; i < exteriorStaticDoors.Doors.Length; i++)
                {
                    if (exteriorStaticDoors.Doors[i].recordIndex == door.recordIndex)
                    {
                        StaticDoor newDoor = exteriorStaticDoors.Doors[i];
                        newDoor.ownerPosition = doorOwner.position;
                        newDoor.ownerRotation = doorOwner.rotation;
                        buildingDoors.Add(newDoor);
                    }
                }
                SetExteriorDoors(buildingDoors.ToArray());
            }

            // Assign new interior to parent
            if (InteriorParent != null)
                newInterior.transform.parent = InteriorParent.transform;

            // Cache some information about this interior
            buildingType = interior.BuildingData.BuildingType;
            factionID = interior.BuildingData.FactionId;

            // Set player to landing position
            ClearTransitionFallingDamage("building-interior-before-teleport");
            transform.position = landingPosition;
            SetStanding();
            ClearTransitionFallingDamageWindow("building-interior-after-teleport");

            // Capture the final grounded position, not the raw doorway point. SetStanding() can
            // adjust Y. This is only a candidate authoritative position until we positively verify
            // that the completed interior setup has solid floor beneath the player.
            Vector3 resolvedMultiplayerInteriorLanding = transform.position;
            DaggerfallInterior resolvedMultiplayerInterior = interior;
            bool considerMultiplayerInteriorLandingGuard =
                useMultiplayerInteriorYOffset &&
                (NetworkServer.active || NetworkClient.active) &&
                doorOwner != null;

            EnableInteriorParent();

            // Add quest resources
            GameObjectHelper.AddQuestResourceObjects(SiteTypes.Building, interior.transform, interior.EntryDoor.buildingKey);

            // Update serializable state from scene cache for exterior->interior transition (unless new/load game)
            if (!start)
                SaveLoadManager.RestoreCachedScene(interior.name);

            // Only arm the compatibility guard when this transition has positively established
            // a safe doorway landing. If the player has no solid floor beneath them (the kind of
            // situation an external placement/rescue mod may legitimately be trying to fix), leave
            // the guard disabled and allow that mod to reposition the player without interference.
            bool guardMultiplayerInteriorLanding = false;
            if (considerMultiplayerInteriorLandingGuard)
            {
                string landingSafetyReason;
                guardMultiplayerInteriorLanding = IsSafeMultiplayerInteriorLanding(
                    resolvedMultiplayerInterior,
                    resolvedMultiplayerInteriorLanding,
                    landingPosition,
                    out landingSafetyReason);

                if (!guardMultiplayerInteriorLanding)
                    Debug.Log($"[InteriorLandingGuard] Not armed because initial MP landing was not positively validated as safe. reason={landingSafetyReason}");
            }

            // Raise event
            RaiseOnTransitionInteriorEvent(door, interior);

            // A resource/placement mod can start a delayed teleport from the transition event.
            // Do not wait a hard-coded number of seconds or lock the player at the doorway; just
            // watch for one teleport-like upward snap and restore this transition's verified-safe,
            // MP-aware landing.
            if (guardMultiplayerInteriorLanding)
                StartMultiplayerInteriorLandingGuard(resolvedMultiplayerInterior, resolvedMultiplayerInteriorLanding);

            // Fade in from black
            if (doFade)
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack();

            // Pending recovered MP-interior offset is only for this one load/transition.
            // currentInteriorUsesMultiplayerYOffset remains true until the player exits, so
            // exterior-door matching still compensates correctly.
            pendingRecoveredMultiplayerInteriorSave = false;
            pendingRecoveredMultiplayerInteriorYOffset = multiplayerInteriorYOffset;
        }
		
		
/*public void TryEnterNetworkedInterior(StaticDoor door)
{
    string region = GameManager.Instance.PlayerGPS.CurrentRegionName;
    string location = GameManager.Instance.PlayerGPS.CurrentLocation.Name;
    int buildingKey = door.buildingKey;

    // Check if interior already exists
    var interiors = FindObjectsOfType<DaggerfallInteriorNetwork>();
    foreach (var interior in interiors)
    {
        if (interior.buildingKey == buildingKey)
        {
            if (interior.IsReady)
            {
                Debug.Log($"[TryEnterNetworkedInterior] Interior already exists and is ready.");
                TeleportIntoNetworkedInterior(interior, door, true); // You teleport manually
                return;
            }
            else
            {
                Debug.Log($"[TryEnterNetworkedInterior] Interior exists but not ready yet — requesting from host.");
                // ✅ STILL send request to host!
                var netPlayer = PlayerMultiplayer.localPlayer;
                if (netPlayer)
                {
                    netPlayer.CmdRequestInteriorFromHost(region, location, buildingKey, door, netPlayer.netId);
                }
                else
                {
                    Debug.LogError("[TryEnterNetworkedInterior] Could not find PlayerMultiplayer.");
                }
                return;
            }
        }
    }

    // Interior does not exist, request from host
    var netPlayerFallback = PlayerMultiplayer.localPlayer;
    if (netPlayerFallback)
    {
        Debug.Log($"[TryEnterNetworkedInterior] No existing interior found — requesting from host for key {buildingKey}");
        netPlayerFallback.CmdRequestInteriorFromHost(region, location, buildingKey, door, netPlayerFallback.netId);
    }
    else
    {
        Debug.LogError("[TryEnterNetworkedInterior] Could not find PlayerMultiplayer.");
    }
}*/


		
		

        /// <summary>
        /// Transition player through an interior door to building exterior. Player must be inside.
        /// Interior stores information about exterior, no need for extra params.
        /// </summary>
        public void TransitionExterior(bool doFade = false)
        {
            // Exit if missing required components or not currently inside
            if (!ReferenceComponents() || !interior || !isPlayerInside)
                return;

            // Redirect to coroutine verion for fade support
            if (doFade)
            {
                StartCoroutine(FadedTransitionExterior());
                return;
            }

            // Perform transition
            BuildingTransitionExteriorLogic();
        }

        private IEnumerator FadedTransitionExterior()
        {
            // Smash to black
            DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();
            yield return new WaitForEndOfFrame();

            // Perform transition
            BuildingTransitionExteriorLogic();

            // Increase fade time if outside world not ready
            // This indicates a first-time transition on fresh load
            float fadeTime = 0.7f;
            if (!GameManager.Instance.StreamingWorld.IsInit)
                fadeTime = 1.5f;

            // Fade in from black
            DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(fadeTime);
        }

        private void BuildingTransitionExteriorLogic()
        {
            // Raise event
            RaiseOnPreTransitionEvent(TransitionType.ToBuildingExterior);

            // Update scene cache from serializable state for interior->exterior transition
            SaveLoadManager.CacheScene(interior.name);

            // Find closest exterior door and position player outside of it.
            // If the interior was shifted down for multiplayer, compare against the exterior
            // doors using the player's unshifted/original-height position.
            StaticDoor closestDoor;
            Vector3 exteriorDoorSearchPosition = GetExteriorDoorSearchPositionFromInteriorPosition(transform.position);
            Vector3 closestDoorPos = DaggerfallStaticDoors.FindClosestDoor(exteriorDoorSearchPosition, ExteriorDoors, out closestDoor);
            Vector3 normal = DaggerfallStaticDoors.GetDoorNormal(closestDoor);
            Vector3 position = closestDoorPos + normal * (controller.radius * 3f);
            world.SetAutoReposition(StreamingWorld.RepositionMethods.Offset, position);

            EnableExteriorParent();

            // Player is now outside building
            isPlayerInside = false;
            isPlayerInsideOpenShop = false;
            IsPlayerInsideTavern = false;
            PlayerTeleportedIntoDungeon = false;
            buildingType = DFLocation.BuildingTypes.None;
            factionID = 0;

            // Update serializable state from scene cache for interior->exterior transition
            SaveLoadManager.RestoreCachedScene(world.SceneName);

            // Fire event
            RaiseOnTransitionExteriorEvent();
        }


        #endregion

        #region Dungeon Transitions


public static void DestroyIfMultiplayerSafe(GameObject obj)
{
    if (!obj) return;

    if (Mirror.NetworkServer.active || Mirror.NetworkClient.active)
    {
        Debug.Log($"[Multiplayer] Prevented destruction of: {obj.name}");
        return;
    }

    UnityEngine.Object.Destroy(obj);
}


/// <summary>
/// Transition player through a dungeon entrance door into dungeon interior.
/// </summary>
/// <param name="doorOwner">Parent transform owning door array.</param>
/// <param name="door">Exterior door player clicked on.</param>
/// <param name="location">Dungeon location data.</param>
/// <param name="doFade">Whether to fade screen transition.</param>
public void TransitionDungeonInterior(Transform doorOwner, StaticDoor door, DFLocation location, bool doFade = false)
{
    if (!ReferenceComponents())
        return;

    // Clear any stale water/swimming state before entering a new dungeon.
    // This prevents SP dungeon water state from leaking into later MP dungeons at -Y.
    ClearDungeonWaterState();

    lastPlayerDungeonBlockIndex = -1;
    playerDungeonBlockData = new DFLocation.DungeonBlock();

    if (OverrideLocation != null)
    {
        DFLocation overrideLocation = dfUnity.ContentReader.MapFileReader.GetLocation(
            OverrideLocation.Summary.RegionName,
            OverrideLocation.Summary.LocationName);
        if (overrideLocation.Loaded)
            location = overrideLocation;
    }

    RaiseOnPreTransitionEvent(TransitionType.ToDungeonInterior, door);

    string dungeonSceneName = DaggerfallDungeon.GetSceneName(location);

    // Never carry a stale player dungeon reference into a new entrance transition.
    // Network dungeons can remain alive after load/respawn, so the field can still point
    // at the previous dungeon even when this requested location is different.
    dungeon = null;

    DaggerfallDungeon[] allDungeons = FindObjectsOfType<DaggerfallDungeon>();

    foreach (var d in allDungeons)
    {
        if (d.Summary.RegionName == location.RegionName && d.Summary.LocationName == location.Name)
        {
            dungeon = d;
            break;
        }
    }

    bool multiplayerActive = NetworkServer.active || NetworkClient.active;

    // Singleplayer must use the normal local dungeon path: parent under DungeonParent, Y=0, no NetworkServer.Spawn().
    // Networked dungeon placement at negative Y is MP-only.
    if (dungeon == null && !multiplayerActive)
    {
        Debug.Log($"[TransitionDungeonInterior] Dungeon '{dungeonSceneName}' not found. Generating local singleplayer dungeon.");

        GameObject newDungeon;
        dungeon = GameObjectHelper.CreateDaggerfallDungeonGameObject(location, DungeonParent != null ? DungeonParent.transform : null, out newDungeon);
        if (dungeon == null || newDungeon == null)
        {
            Debug.LogError("[TransitionDungeonInterior] ERROR: Could not create local singleplayer dungeon!");
            RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
            return;
        }

        // IMPORTANT: CreateDaggerfallDungeonGameObject(location, ..., out go) only creates the GameObject
        // and component. It does NOT lay out the dungeon. Without SetDungeon()/GenerateDungeon(),
        // StartMarker remains null and TransitionDungeonInterior fails with "No start marker found".
        dungeon.SetDungeon(location, importEnemies: true);

        newDungeon.hideFlags = defaultHideFlags;
    }

    // Host/server multiplayer path: create the networked dungeon prefab at a network dungeon Y slot.
    if (dungeon == null && NetworkServer.active)
    {
        Debug.Log($"[TransitionDungeonInterior] Dungeon '{dungeonSceneName}' not found. Host will generate networked dungeon.");

        GameObject prefab = NetworkManager.singleton.spawnPrefabs
            .FirstOrDefault(p => p.GetComponent<DaggerfallDungeon>() != null);

        if (prefab == null)
        {
            Debug.LogError("[TransitionDungeonInterior] ERROR: Could not find DaggerfallDungeon prefab!");
            RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
            return;
        }

        float assignedY = DaggerfallDungeon.GetNextAvailableDungeonY();

        GameObject obj = Instantiate(prefab);
        obj.name = dungeonSceneName;
        obj.transform.position = new Vector3(0, assignedY, 0);

        NetworkServer.Spawn(obj);

        dungeon = obj.GetComponent<DaggerfallDungeon>();
        dungeon.PositionY = assignedY;

        DFLocation dfLocation = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(
            location.RegionName, location.Name);

        // TeleportPc must use the exact exterior dungeon entrance coordinate before enemy import.
        // Do not call SetDungeonRequesterContext(0) in that case, because it reads the host/local
        // PositionMultiplayer and can reintroduce the stale/coarse map-pixel coordinate.
        if (!TryApplyTeleportPcDungeonAnchorToHostDungeon(dungeon, dfLocation, "TransitionDungeonInterior-host-before-generation"))
            dungeon.SetDungeonRequesterContext(0);

        bool vampireCemeteryWake = HasPendingMultiplayerDungeonStartMarkerSentinelFor(dfLocation);
        dungeon.GenerateDungeon(dfLocation, importEnemies: !vampireCemeteryWake, assignedY: assignedY);
        if (vampireCemeteryWake)
            Debug.Log($"[VampireMP] Host generated cemetery network dungeon without random enemies: '{dfLocation.RegionName}/{dfLocation.Name}'");
    }

if (dungeon == null)
{
    if (!NetworkServer.active && NetworkClient.active)
    {
        Debug.Log($"[TransitionDungeonInterior] Client requesting dungeon from host: {location.RegionName} - {location.Name}");

PlayerMultiplayer localNetPlayer = PlayerMultiplayer.localPlayer;
if (localNetPlayer == null)
{
    localNetPlayer = FindObjectsOfType<PlayerMultiplayer>()
        .FirstOrDefault(p => p.isLocalPlayer);
}

if (localNetPlayer != null)
{
    try
    {
        int requesterLevel = DaggerfallDungeon.GetLocalPlayerLevelFallback();
        int[] requesterTextureTable = DaggerfallDungeon.BuildLocationDungeonTextureTable(location);
        int monsterSeed = DaggerfallDungeon.BuildStableDungeonMonsterSeed(location);

        Debug.Log($"[TransitionDungeonInterior] Found local player via fallback. Sending requester-authoritative dungeon generation spec. level={requesterLevel}, seed={monsterSeed}, textures=[{requesterTextureTable[0]},{requesterTextureTable[1]},{requesterTextureTable[2]},{requesterTextureTable[3]},{requesterTextureTable[4]},{requesterTextureTable[5]}]. Transition is pending, not failed.");

        localNetPlayer.CmdRequestDungeonFromHostWithGenerationSpec(
            location.RegionName,
            location.Name,
            localNetPlayer.netId,
            requesterLevel,
            monsterSeed,
            requesterTextureTable[0],
            requesterTextureTable[1],
            requesterTextureTable[2],
            requesterTextureTable[3],
            requesterTextureTable[4],
            requesterTextureTable[5]);
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"[TransitionDungeonInterior] Failed to build requester dungeon generation spec. Falling back to legacy request. Exception={ex}");
        localNetPlayer.CmdRequestDungeonFromHost(location.RegionName, location.Name, localNetPlayer.netId);
    }

    return;
}
else
{
    Debug.LogError("[TransitionDungeonInterior] ERROR: Could not find local PlayerMultiplayer.");
    RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
    return;
}
    }

    Debug.LogError($"[TransitionDungeonInterior] ERROR: Dungeon '{dungeonSceneName}' was not found in scene and cannot be created.");
    RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
    return;
}


    if (!dungeon.StartMarker)
    {
        Debug.LogError($"[TransitionDungeonInterior] ERROR: No start marker found in {location.Name}.");
        RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
        return;
    }

    // ✅ Ensure host/client player is marked as inside.
    isPlayerInside = true;
    isPlayerInsideDungeon = true;

    // Keep the network-state tracker in sync with dungeon entry. PlayerEnterExit starts
    // before multiplayer is usually active, and TeleportPc clients enter through a TargetRpc
    // path rather than a normal clicked door. If this stays false, disconnect can leave the
    // local player stuck inside the old network dungeon instead of emergency-exiting.
    lastNetworkActiveState = NetworkServer.active || NetworkClient.active;

    MovePlayerToMarker(dungeon.StartMarker);
    Debug.Log($"[TransitionDungeonInterior] Player moved inside {location.Name}.");

    // TeleportPc dungeon traps should not remain at the normal dungeon entry.
    // Apply the quest-marker snap as part of the dungeon entry flow itself so it
    // runs after both host-created and TargetEnterDungeon client-created dungeons.
    TryApplyPendingTeleportPcDungeonMarker(location, "TransitionDungeonInterior-after-entry-marker");

    StaticDoor[] doors = DaggerfallStaticDoors.FindDoorsInCollections(dungeon.StaticDoorCollections, DoorTypes.DungeonExit);
    if (doors != null && doors.Length > 0)
    {
        if (DaggerfallStaticDoors.FindClosestDoorToPlayer(transform.position, doors, out var doorPos, out var doorIndex))
        {
            PlayerMouseLook look = GameManager.Instance.PlayerMouseLook;
            if (look)
                look.SetFacing(DaggerfallStaticDoors.GetDoorNormal(doors[doorIndex]));
        }
    }
EnableDungeonParent();

    // A saved-dungeon conversion runs while SaveLoadManager is still restoring the
    // character and QuestMachine state. Injecting quest resources here can observe a
    // half-restored SiteLink/Quest pair and throw after MovePlayerToMarker(), which leaves
    // the player at the dungeon start marker with isRespawning still true and the screen
    // black. TryCompleteNetworkDungeonConversion() schedules the same idempotent resource
    // pass after LoadInProgress clears, so skip only this premature saved-load pass.
    bool deferQuestResourcesForSavedDungeonLoad =
        networkDungeonConversionInProgress &&
        pendingNetworkDungeonConversionFromLoad;

    if (!deferQuestResourcesForSavedDungeonLoad)
    {
        GameObjectHelper.AddQuestResourceObjects(SiteTypes.Dungeon, dungeon.transform);
    }
    else
    {
        Debug.Log($"[NetworkDungeonConversion][QuestResources] Deferred normal dungeon quest-resource injection until save load completes. dungeon='{location.RegionName}/{location.Name}'");
    }

    RaiseOnTransitionDungeonInteriorEvent(door, dungeon);

    if (doFade)
        DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack();
}


/*
// Subscribe to the dungeon transition event when the dungeon is created
private void OnEnable()
{
    PlayerEnterExit.OnTransitionDungeonInterior += HandleDungeonTransition;
}

// Unsubscribe when the object is destroyed to avoid memory leaks
private void OnDisable()
{
    PlayerEnterExit.OnTransitionDungeonInterior -= HandleDungeonTransition;
}

// 🔹 This method is triggered when a dungeon transition occurs
private void HandleDungeonTransition(PlayerEnterExit.TransitionEventArgs args)
{
    Debug.Log($"[HandleDungeonTransition] Player transitioning to a dungeon.");

    // 🔹 Check transition type (without direct enum access)
    if (args.GetType().Name.Contains("ToDungeonInterior"))  // ✅ Workaround for private enum
    {
        Debug.Log($"[HandleDungeonTransition] Handling transition for a dungeon.");

        // 🔹 Find the correct dungeon in the scene
        DaggerfallDungeon dungeon = FindObjectOfType<DaggerfallDungeon>();
        if (dungeon == null)
        {
            Debug.LogError("[HandleDungeonTransition] ERROR: No DaggerfallDungeon found!");
            return;
        }

        // 🔹 Ensure the Dungeon parent is enabled
        GameObject dungeonParent = GameObject.Find("Dungeon");
        if (dungeonParent != null && !dungeonParent.activeSelf)
        {
            Debug.Log("[HandleDungeonTransition] Enabling Dungeon parent.");
            dungeonParent.SetActive(true);
        }

        // 🔹 Find the correct dungeon entry door
        StaticDoor? entryDoorNullable = DaggerfallDungeon.GetDungeonEntryDoor(dungeon.summary.LocationData);
        if (!entryDoorNullable.HasValue)
        {
            Debug.LogError($"[HandleDungeonTransition] ERROR: Could not find a valid entry door for {dungeon.summary.LocationName}.");
            return;
        }
        StaticDoor entryDoor = entryDoorNullable.Value;

        // 🔹 Move the player into the dungeon
        PlayerEnterExit playerEnterExit = FindObjectOfType<PlayerEnterExit>();
        if (playerEnterExit != null)
        {
            Debug.Log($"[HandleDungeonTransition] Moving player inside {dungeon.summary.LocationName}");
            playerEnterExit.TransitionDungeonInterior(null, entryDoor, dungeon.summary.LocationData, false);
        }
        else
        {
            Debug.LogError("[HandleDungeonTransition] ERROR: Could not find PlayerEnterExit!");
        }
    }
}*/



        /// <summary>
        /// Starts player inside dungeon with no exterior world.
        /// </summary>
        public void StartDungeonInterior(DFLocation location, bool preferEnterMarker = true, bool importEnemies = true)
        {
            // Ensure we have component references
            if (!ReferenceComponents())
                return;

            // True single-player should keep the original DFU dungeon-start flow.
            // This avoids TeleportPc/network-dungeon safety code affecting vampire cemetery wake-up in SP.
            if (!NetworkServer.active && !NetworkClient.active && !pendingRecoveredNetworkDungeonSave)
            {
                StartDungeonInteriorSingleplayerVanilla(location, preferEnterMarker, importEnemies);
                return;
            }

            // Raise event
            RaiseOnPreTransitionEvent(TransitionType.ToDungeonInterior);

            // Layout dungeon
            GameObject newDungeon;
            dungeon = GameObjectHelper.CreateDaggerfallDungeonGameObject(location, DungeonParent.transform, out newDungeon);

            if (pendingRecoveredNetworkDungeonSave && dungeon != null)
            {
                dungeon.ConfigureRecoveredNetworkDungeonSave(pendingRecoveredNetworkDungeonY, string.Empty);
            }

            dungeon.SetDungeon(location, importEnemies);
            newDungeon.hideFlags = defaultHideFlags;

            GameObject marker = null;
            if (preferEnterMarker && dungeon.EnterMarker != null)
                marker = dungeon.EnterMarker;
            else
                marker = dungeon.StartMarker;

            // Find start marker to position player
            if (!marker)
            {
                // Could not find marker
                DaggerfallUnity.LogMessage("No start or enter marker found for this dungeon. Aborting load.");
                Destroy(newDungeon);
                RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
                return;
            }

            EnableDungeonParent();

            // Add quest resources and selectively enable quest foes
            //  -Entering a dungeon normally will add quest foes always
            //  -Loading a game will not add quest foes as these are restored by save state
            //  -Teleporting into a dungeon will add quest foes like going through entrance normally
            GameObjectHelper.AddQuestResourceObjects(SiteTypes.Dungeon, dungeon.transform, 0, true, importEnemies, true);

            // Set to start position
            MovePlayerToMarker(marker);

            // Set player facing north
            PlayerMouseLook playerMouseLook = GameManager.Instance.PlayerMouseLook;
            if (playerMouseLook)
                playerMouseLook.SetFacing(Vector3.forward);

            // Raise event
            RaiseOnTransitionDungeonInteriorEvent(new StaticDoor(), dungeon);

            if (pendingRecoveredNetworkDungeonSave)
            {
                Debug.Log($"[NetworkDungeonSave] Finished SP recovery dungeon setup y={pendingRecoveredNetworkDungeonY}");
                pendingRecoveredNetworkDungeonSave = false;
                pendingRecoveredNetworkDungeonY = 0f;
            }
        }



        // Vanilla single-player dungeon entry helpers.
        // These are intentionally kept separate from the MP/network dungeon path so
        // vampire cemetery wake-up in true SP follows the same placement/standing flow
        // as the unmodified PlayerEnterExit.
        private void StartDungeonInteriorSingleplayerVanilla(DFLocation location, bool preferEnterMarker = true, bool importEnemies = true)
        {
            // Ensure we have component references
            if (!ReferenceComponents())
                return;

            // Raise event
            RaiseOnPreTransitionEvent(TransitionType.ToDungeonInterior);

            // Layout dungeon
            GameObject newDungeon;
            dungeon = GameObjectHelper.CreateDaggerfallDungeonGameObject(location, DungeonParent.transform, out newDungeon);
            dungeon.SetDungeon(location, importEnemies);
            newDungeon.hideFlags = defaultHideFlags;

            GameObject marker = null;
            if (preferEnterMarker && dungeon.EnterMarker != null)
                marker = dungeon.EnterMarker;
            else
                marker = dungeon.StartMarker;

            // Find start marker to position player
            if (!marker)
            {
                // Could not find marker
                DaggerfallUnity.LogMessage("No start or enter marker found for this dungeon. Aborting load.");
                Destroy(newDungeon);
                RaiseOnFailedTransition(TransitionType.ToDungeonInterior);
                return;
            }

            EnableDungeonParent();

            // Add quest resources and selectively enable quest foes
            //  -Entering a dungeon normally will add quest foes always
            //  -Loading a game will not add quest foes as these are restored by save state
            //  -Teleporting into a dungeon will add quest foes like going through entrance normally
            GameObjectHelper.AddQuestResourceObjects(SiteTypes.Dungeon, dungeon.transform, 0, true, importEnemies, true);

            // Set to start position
            MovePlayerToMarkerSingleplayerVanilla(marker);

            // Set player facing north
            PlayerMouseLook playerMouseLook = GameManager.Instance.PlayerMouseLook;
            if (playerMouseLook)
                playerMouseLook.SetFacing(Vector3.forward);

            // Raise event
            RaiseOnTransitionDungeonInteriorEvent(new StaticDoor(), dungeon);
        }


        private void MovePlayerToMarkerSingleplayerVanilla(GameObject marker)
        {
            if (!isPlayerInsideDungeon || !marker)
                return;

            // Set player to start position
            transform.position = marker.transform.position + Vector3.up * (controller.height * 0.6f);

            // Fix player standing using the exact old SP standing helper.
            // Do not route through the MP SetStanding() wrapper here.
            SetStandingSingleplayerVanilla();

            // Raise event
            RaiseOnMovePlayerToDungeonStartEvent();
        }

        private void SetStandingSingleplayerVanilla()
        {
            // Snap player to ground
            RaycastHit hit;
            Ray ray = new Ray(transform.position, Vector3.down);
            if (Physics.Raycast(ray, out hit, PlayerHeightChanger.controllerStandingHeight * 2f))
            {
                // Clear falling damage so player doesn't take damage if they transitioned into a dungeon while jumping
                GameManager.Instance.AcrobatMotor.ClearFallingDamage();
                // Position player at hit position plus just over half controller height up
                Vector3 pos = hit.point;
                pos.y += controller.height / 2f + 0.25f;
                transform.position = pos;
            }
        }

        /// <summary>
        /// Starts player inside building with no exterior world.
        /// </summary>
        public void StartBuildingInterior(DFLocation location, StaticDoor exteriorDoor, bool start = true)
        {
            // Store start flag
            lastInteriorStartFlag = start;

            // Ensure we have component references
            if (!ReferenceComponents())
                return;

            // Discover building
            GameManager.Instance.PlayerGPS.DiscoverBuilding(exteriorDoor.buildingKey);

            TransitionInterior(null, exteriorDoor, false, start);
        }

        public void DisableAllParents(bool cleanup = true)
        {
            if (!GameManager.Instance.IsReady)
                GameManager.Instance.GetProperties();

            if (cleanup)
            {
                if (dungeon) PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
                if (interior && !NetworkServer.active && !NetworkClient.active)
                    Destroy(interior.gameObject);
            }

            if (ExteriorParent != null) ExteriorParent.SetActive(false);
            if (InteriorParent != null) InteriorParent.SetActive(false);
            if (DungeonParent != null) DungeonParent.SetActive(false);
        }

        /// <summary>
        /// Enable ExteriorParent.
        /// </summary>
        public void EnableExteriorParent(bool cleanup = true)
        {
            if (cleanup)
            {
                if (dungeon) PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
                if (interior) Destroy(interior.gameObject);    // comment out if I want to keep the interior (networked version)
                SetExteriorDoors(null);
            }
            DisableAllParents(false);
            if (ExteriorParent != null) ExteriorParent.SetActive(true);

            world.suppressWorld = false;
            isPlayerInside = false;
            isPlayerInsideDungeon = false;
            currentInteriorUsesMultiplayerYOffset = false;

            GameManager.UpdateShadowDistance();
        }

        /// <summary>
        /// Enable InteriorParent.
        /// </summary>
        public void EnableInteriorParent(bool cleanup = true)
        {
            if (cleanup)
            {
                if (dungeon) PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
            }
            DisableAllParents(false);
            if (InteriorParent != null) InteriorParent.SetActive(true);

            isPlayerInside = true;
            isPlayerInsideDungeon = false;

            GameManager.UpdateShadowDistance();
        }

        /// <summary>
        /// Enable DungeonParent.
        /// </summary>
        public void EnableDungeonParent(bool cleanup = true)
        {
            if (cleanup)
            {
                if (interior)
                {
                    Destroy(interior.gameObject);
                    buildingType = DFLocation.BuildingTypes.None;
                    factionID = 0;
                    currentInteriorUsesMultiplayerYOffset = false;
                }
            }

            DisableAllParents(false);
            if (DungeonParent != null) DungeonParent.SetActive(true);

            isPlayerInside = true;
            isPlayerInsideOpenShop = false;
            IsPlayerInsideTavern = false;
            isPlayerInsideDungeon = true;

            GameManager.UpdateShadowDistance();
        }

        /// <summary>
        /// Moves player to a start marker inside current dungeon.
        /// </summary>
        /// <param name="marker">Marker gameobject. See <see cref="DaggerfallRDBBlock.StartMarkers"/>.</param>
        public void MovePlayerToMarker(GameObject marker)
        {
            if (!NetworkServer.active && !NetworkClient.active)
            {
                MovePlayerToMarkerSingleplayerVanilla(marker);
                return;
            }

            if (!isPlayerInsideDungeon || !marker)
                return;

            // Set player to start position
            ClearTransitionFallingDamage("dungeon-marker-before-teleport");
            transform.position = marker.transform.position + Vector3.up * (controller.height * 0.6f);

            // Fix player standing
            SetStanding();
            ClearTransitionFallingDamageWindow("dungeon-marker-after-teleport");

            // Raise event
            RaiseOnMovePlayerToDungeonStartEvent();
        }

        /// <summary>
        /// Moves player to main start marker inside current dungeon.
        /// </summary>
        public void MovePlayerToDungeonStart()
        {
            MovePlayerToMarker(dungeon.StartMarker);
        }

        /// <summary>
        /// Registers a pending TeleportPc marker. The actual marker snap is applied from
        /// TransitionDungeonInterior() after that method performs its normal entry-marker move.
        /// This is more reliable than waiting for TeleportPc.Update() to tick again after
        /// TargetEnterDungeon/WaitForDungeonReady.
        /// </summary>
        public void RegisterMultiplayerQuestDungeonTeleportMarker(DFLocation location, Vector3 dungeonLocalMarkerPosition, string reason)
        {
            pendingTeleportPcDungeonMarker = true;
            pendingTeleportPcDungeonRegionName = location.RegionName;
            pendingTeleportPcDungeonLocationName = location.Name;
            pendingTeleportPcDungeonLocalMarker = dungeonLocalMarkerPosition;
            pendingTeleportPcDungeonMarkerRegisteredAt = Time.realtimeSinceStartup;

            // If PrepareMultiplayerQuestDungeonTeleportWorldPosition() already ran, bind that exact
            // entrance coordinate to this TeleportPc dungeon now. Host path calls Prepare before Register;
            // pure client path can call Register before Prepare, so Prepare also binds back to pending marker.
            if (lastPreparedTeleportPcWorldContextValid)
            {
                teleportPcDungeonWorldContextActive = true;
                teleportPcDungeonWorldContextRegionName = location.RegionName;
                teleportPcDungeonWorldContextLocationName = location.Name;
                teleportPcDungeonWorldContextX = lastPreparedTeleportPcWorldContextX;
                teleportPcDungeonWorldContextZ = lastPreparedTeleportPcWorldContextZ;

                Debug.Log($"[TeleportPcMP][ExactEntrance] Bound exact world context to pending marker. reason={reason} dungeon='{teleportPcDungeonWorldContextRegionName}/{teleportPcDungeonWorldContextLocationName}' world={teleportPcDungeonWorldContextX}/{teleportPcDungeonWorldContextZ}");
            }

            Debug.Log($"[TeleportPcMP] Registered pending quest dungeon marker. reason={reason} dungeon='{pendingTeleportPcDungeonRegionName}/{pendingTeleportPcDungeonLocationName}' local={pendingTeleportPcDungeonLocalMarker}");
        }

        // Compatibility overload used by PlayerMultiplayer TargetEnterTeleportPcDungeon() builds
        // that pass the host-authored dungeon Y. PlayerEnterExit only needs the live dungeon.
        public void RegisterMultiplayerQuestDungeonTeleportMarker(DFLocation location, Vector3 dungeonLocalMarkerPosition, float expectedDungeonY, string reason)
        {
            RegisterMultiplayerQuestDungeonTeleportMarker(location, dungeonLocalMarkerPosition, reason);
        }

        private static bool IsMultiplayerDungeonStartMarkerSentinel(Vector3 marker)
        {
            return float.IsPositiveInfinity(marker.x) &&
                   float.IsPositiveInfinity(marker.y) &&
                   float.IsPositiveInfinity(marker.z);
        }

        private bool HasPendingMultiplayerDungeonStartMarkerSentinelFor(DFLocation location)
        {
            return HasPendingTeleportPcDungeonMarkerFor(location) &&
                   IsMultiplayerDungeonStartMarkerSentinel(pendingTeleportPcDungeonLocalMarker);
        }

        private bool TryGetTeleportPcDungeonWorldContextFor(DFLocation location, out int worldX, out int worldZ)
        {
            worldX = 0;
            worldZ = 0;

            if (!teleportPcDungeonWorldContextActive || !location.Loaded)
                return false;

            if (!string.Equals(teleportPcDungeonWorldContextRegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(teleportPcDungeonWorldContextLocationName, location.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            worldX = teleportPcDungeonWorldContextX;
            worldZ = teleportPcDungeonWorldContextZ;
            return true;
        }

        private bool TryApplyTeleportPcDungeonAnchorToHostDungeon(DaggerfallDungeon targetDungeon, DFLocation location, string reason)
        {
            if (targetDungeon == null)
                return false;

            int anchorWorldX;
            int anchorWorldZ;
            if (!TryGetTeleportPcDungeonWorldContextFor(location, out anchorWorldX, out anchorWorldZ))
                return false;

            targetDungeon.RequesterNetId = 0;
            targetDungeon.HasDungeonWorldAnchor = true;
            targetDungeon.DungeonAnchorWorldX = anchorWorldX;
            targetDungeon.DungeonAnchorWorldZ = anchorWorldZ;

            Debug.Log($"[TeleportPcMP][ExactEntrance] Applied exact TeleportPc anchor to host-generated dungeon before enemy import. reason={reason} dungeon='{location.RegionName}/{location.Name}' world={anchorWorldX}/{anchorWorldZ}");
            return true;
        }

        private void ClearTeleportPcDungeonWorldContext(string reason)
        {
            bool hadContext = pendingTeleportPcDungeonMarker ||
                              teleportPcDungeonWorldContextActive ||
                              lastPreparedTeleportPcWorldContextValid;

            pendingTeleportPcDungeonMarker = false;
            pendingTeleportPcDungeonRegionName = string.Empty;
            pendingTeleportPcDungeonLocationName = string.Empty;
            pendingTeleportPcDungeonLocalMarker = Vector3.zero;
            pendingTeleportPcDungeonMarkerRegisteredAt = 0f;

            teleportPcDungeonWorldContextActive = false;
            teleportPcDungeonWorldContextRegionName = string.Empty;
            teleportPcDungeonWorldContextLocationName = string.Empty;
            teleportPcDungeonWorldContextX = 0;
            teleportPcDungeonWorldContextZ = 0;

            lastPreparedTeleportPcWorldContextValid = false;
            lastPreparedTeleportPcWorldContextX = 0;
            lastPreparedTeleportPcWorldContextZ = 0;

            if (hadContext)
                Debug.Log($"[TeleportPcMP][ExactEntrance] Cleared TeleportPc dungeon context. reason={reason}");
        }

        private bool HasPendingTeleportPcDungeonMarkerFor(DFLocation location)
        {
            if (!pendingTeleportPcDungeonMarker)
                return false;

            if (Time.realtimeSinceStartup - pendingTeleportPcDungeonMarkerRegisteredAt > PendingTeleportPcDungeonMarkerTimeout)
            {
                if (IsMultiplayerDungeonStartMarkerSentinel(pendingTeleportPcDungeonLocalMarker))
                {
                    // A vampire wake-up can sit behind the post-video message box or a slow network dungeon
                    // spawn. Do not let the sentinel expire into a normal dungeon entry with enemies.
                    pendingTeleportPcDungeonMarkerRegisteredAt = Time.realtimeSinceStartup;
                    Debug.LogWarning($"[VampireMP] Pending cemetery wake marker waited longer than normal TeleportPc timeout; keeping it armed for '{pendingTeleportPcDungeonRegionName}/{pendingTeleportPcDungeonLocationName}'.");
                }
                else
                {
                    Debug.LogWarning($"[TeleportPcMP] Pending quest dungeon marker expired for '{pendingTeleportPcDungeonRegionName}/{pendingTeleportPcDungeonLocationName}'.");
                    pendingTeleportPcDungeonMarker = false;
                    return false;
                }
            }

            return string.Equals(pendingTeleportPcDungeonRegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(pendingTeleportPcDungeonLocationName, location.Name, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryApplyPendingTeleportPcDungeonMarker(DFLocation location, string reason)
        {
            if (!HasPendingTeleportPcDungeonMarkerFor(location))
                return false;

            Vector3 localMarker = pendingTeleportPcDungeonLocalMarker;

            // Vampire cemetery wake-up uses the TeleportPc network-dungeon request only to
            // create/sync the cemetery dungeon with no enemies. Once the live dungeon is entered,
            // snap to its real EnterMarker and refresh the exterior world context for later exits.
            if (IsMultiplayerDungeonStartMarkerSentinel(localMarker))
            {
                bool movedToWakeMarker = TryMovePlayerToDungeonEnterMarkerForMultiplayerWake(location, reason + "-vampire-wake");
                if (movedToWakeMarker)
                {
                    pendingTeleportPcDungeonMarker = false;
                    Debug.Log($"[VampireMP] Consumed cemetery wake sentinel after live EnterMarker snap. reason={reason} dungeon='{location.RegionName}/{location.Name}'");
                }
                else
                {
                    // Keep it armed; the VampirismInfection coroutine will retry if the marker/dungeon
                    // is not ready on this exact frame.
                    pendingTeleportPcDungeonMarkerRegisteredAt = Time.realtimeSinceStartup;
                    Debug.LogWarning($"[VampireMP] Cemetery wake marker not ready yet, leaving sentinel armed. reason={reason} dungeon='{location.RegionName}/{location.Name}'");
                }

                return movedToWakeMarker;
            }

            // Consume before moving so a later normal re-entry cannot reuse a stale quest marker.
            pendingTeleportPcDungeonMarker = false;

            bool moved = TryMovePlayerToDungeonQuestMarker(localMarker, reason);
            if (!moved)
            {
                // Re-arm for the next frame/tick if the dungeon was not fully ready yet.
                pendingTeleportPcDungeonMarker = true;
                pendingTeleportPcDungeonMarkerRegisteredAt = Time.realtimeSinceStartup;
            }

            return moved;
        }

        /// <summary>
        /// Multiplayer-only helper for quest teleports such as TeleportPc.
        /// Updates the player's DF world/map position before the network dungeon request is sent,
        /// so imported dungeon enemies use the destination dungeon as their world anchor instead
        /// of the tavern/house position where the teleport action fired.
        /// </summary>
        public void PrepareMultiplayerQuestDungeonTeleportWorldPosition(int worldX, int worldZ, string reason)
        {
            if (!ReferenceComponents())
                return;

            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();
            ClearDungeonWaterState();

            if (playerGPS != null)
            {
                playerGPS.WorldX = worldX;
                playerGPS.WorldZ = worldZ;
            }

            lastPreparedTeleportPcWorldContextValid = true;
            lastPreparedTeleportPcWorldContextX = worldX;
            lastPreparedTeleportPcWorldContextZ = worldZ;

            // Pure-client order is Register -> Prepare; host order is Prepare -> Register.
            // If a pending marker is already known, bind this exact coordinate to that dungeon now.
            if (pendingTeleportPcDungeonMarker)
            {
                teleportPcDungeonWorldContextActive = true;
                teleportPcDungeonWorldContextRegionName = pendingTeleportPcDungeonRegionName;
                teleportPcDungeonWorldContextLocationName = pendingTeleportPcDungeonLocationName;
                teleportPcDungeonWorldContextX = worldX;
                teleportPcDungeonWorldContextZ = worldZ;
                Debug.Log($"[TeleportPcMP][ExactEntrance] Preserved exact world context. reason={reason} dungeon='{teleportPcDungeonWorldContextRegionName}/{teleportPcDungeonWorldContextLocationName}' world={worldX}/{worldZ}");
            }

            if (world != null)
            {
                DFPosition mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
                world.MapPixelX = mapPixel.X;
                world.MapPixelY = mapPixel.Y;

                bool pureClient = NetworkClient.active && !NetworkServer.active;
                bool canRefreshStreamingWorldNow = !pureClient || isPlayerInsideDungeon;

                // Important for TeleportPc MP dungeon traps:
                // The player is not entering through an exterior dungeon door, so StreamingWorld
                // can still think the current exterior location is the tavern/house town where the
                // teleport action fired. That breaks DungeonExit later: it repositions to the old
                // town, shows "You are entering <old town>", and then dungeon enemies see the player
                // as far away.
                //
                // Pure clients must still skip this BEFORE the host TargetRpc/normal client dungeon
                // enter path completes, otherwise they can end up in the old exterior/interior hybrid
                // state. Once isPlayerInsideDungeon is true, it is safe to rebuild the exterior
                // StreamingWorld context and immediately restore the dungeon-space transform in the
                // same frame. This gives later dungeon exits/disconnect safety the correct exterior
                // dungeon entrance context without causing the visible one-frame exit jump.
                if (canRefreshStreamingWorldNow)
                {
                    Vector3 restorePosition = transform.position;
                    bool restoreInside = isPlayerInside;
                    bool restoreInsideDungeon = isPlayerInsideDungeon;
                    bool restoreInsideCastle = isPlayerInsideDungeonCastle;
                    bool restoreInsideSpecial = isPlayerInsideSpecialArea;
                    bool restoreTeleportedIntoDungeon = PlayerTeleportedIntoDungeon;
                    DaggerfallDungeon restoreDungeon = dungeon;

                    // This rebuilds the StreamingWorld map-pixel/location context for the destination,
                    // but vanilla TeleportToCoordinates(mapPixel, None) resets PlayerGPS.WorldX/Z to the
                    // map-pixel origin and moves the local player transform to zero. Immediately restore
                    // the exact dungeon entrance coordinate and, for pure clients already inside a dungeon,
                    // restore the dungeon-space transform/state.
                    world.TeleportToCoordinates(mapPixel.X, mapPixel.Y, StreamingWorld.RepositionMethods.None);

                    if (playerGPS != null)
                    {
                        playerGPS.WorldX = worldX;
                        playerGPS.WorldZ = worldZ;
                        playerGPS.UpdateWorldInfo();
                    }

                    if (pureClient && restoreInsideDungeon)
                    {
                        dungeon = restoreDungeon;
                        isPlayerInside = restoreInside;
                        isPlayerInsideDungeon = restoreInsideDungeon;
                        isPlayerInsideDungeonCastle = restoreInsideCastle;
                        isPlayerInsideSpecialArea = restoreInsideSpecial;
                        PlayerTeleportedIntoDungeon = restoreTeleportedIntoDungeon;

                        EnableDungeonParent();
                        transform.position = restorePosition;
                        SetStanding();

                        Debug.Log($"[TeleportPcMP][ExactEntrance] Refreshed pure-client StreamingWorld dungeon exterior context after entry. reason={reason} world={worldX}/{worldZ} mapPixel={mapPixel.X}/{mapPixel.Y} restoredPos={restorePosition}");
                    }
                }

                Debug.Log($"[TeleportPcMP] Prepared dungeon teleport world context. reason={reason} world={worldX}/{worldZ} mapPixel={mapPixel.X}/{mapPixel.Y} pureClient={pureClient} refreshedWorld={canRefreshStreamingWorldNow}");
            }
            else
            {
                Debug.Log($"[TeleportPcMP] Prepared dungeon teleport world position. reason={reason} world={worldX}/{worldZ} mapPixel=<no StreamingWorld>");
            }
        }

        private void PrepareMultiplayerQuestDungeonTeleportWorldPositionFromDungeon(string reason)
        {
            if (dungeon == null)
                return;

            try
            {
                DFLocation location = dungeon.summary.LocationData;

                int anchorWorldX;
                int anchorWorldZ;
                if (TryGetTeleportPcDungeonWorldContextFor(location, out anchorWorldX, out anchorWorldZ))
                {
                    PrepareMultiplayerQuestDungeonTeleportWorldPosition(anchorWorldX, anchorWorldZ, reason + "-exact-context");
                    return;
                }

                // Fallback only for old saves/rare cases without a TeleportPc exact context.
                // This is the old coarse map-pixel coordinate and should not be used for normal TeleportPc.
                DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel(
                    (int)location.MapTableData.Longitude,
                    location.MapTableData.Latitude);
                DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);

                Debug.LogWarning($"[TeleportPcMP][ExactEntrance] Missing exact world context for '{location.RegionName}/{location.Name}'. Falling back to coarse map-pixel world={worldPos.X}/{worldPos.Y}. reason={reason}");
                PrepareMultiplayerQuestDungeonTeleportWorldPosition(worldPos.X, worldPos.Y, reason + "-coarse-fallback");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TeleportPcMP] Could not refresh dungeon world context from current dungeon. reason={reason} error={ex.Message}");
            }
        }

        /// <summary>
        /// Multiplayer-only helper for TeleportPc. The supplied position is dungeon-local,
        /// so this applies it relative to the actual spawned dungeon transform/Y slot.
        /// </summary>
        public bool TryMovePlayerToDungeonQuestMarker(Vector3 dungeonLocalMarkerPosition, string reason)
        {
            if (!ReferenceComponents())
                return false;

            if (dungeon == null || !dungeon.isSet)
                return false;

            EnableDungeonParent();

            // Re-assert the exterior/world context after the network dungeon is actually bound.
            // The marker snap itself is a dungeon-space move, but the eventual DungeonExit must
            // still know which exterior dungeon location to return to.
            PrepareMultiplayerQuestDungeonTeleportWorldPositionFromDungeon(reason + "-dungeon-bound");

            Vector3 finalPosition = dungeon.transform.position + dungeonLocalMarkerPosition;

            ClearTransitionFallingDamage(reason + "-before");
            transform.position = finalPosition;
            SetStanding();
            ClearTransitionFallingDamageWindow(reason + "-after");

            PlayerTeleportedIntoDungeon = true;
            ForceSendMultiplayerCoordinatesNow(reason + "-after-marker");

            Debug.Log($"[TeleportPcMP] Moved player to quest dungeon marker. reason={reason} dungeon='{dungeon.name}' dungeonY={dungeon.transform.position.y} local={dungeonLocalMarkerPosition} final={finalPosition}");

            RaiseOnMovePlayerToDungeonStartEvent();
            return true;
        }

        /// <summary>
        /// Final handoff for party fast travel to a player already inside a network dungeon.
        /// The normal dungeon transition has already bound this PlayerEnterExit to the exact
        /// dungeon instance and moved to StartMarker. This only performs the final dungeon-space
        /// snap using the target player's existing NetworkTransform.
        /// </summary>
        public bool TryMovePlayerNearPartyMemberInDungeon(
            Transform targetPlayerTransform,
            string expectedDungeonInstanceId,
            string reason)
        {
            if (!ReferenceComponents() ||
                targetPlayerTransform == null ||
                string.IsNullOrEmpty(expectedDungeonInstanceId))
                return false;

            if (!isPlayerInsideDungeon ||
                dungeon == null ||
                !dungeon.isSet ||
                !dungeon.IsNetworkDungeonInstance ||
                !string.Equals(dungeon.DungeonInstanceId, expectedDungeonInstanceId, StringComparison.Ordinal))
                return false;

            PositionMultiplayer targetPosition =
                targetPlayerTransform.GetComponent<PositionMultiplayer>();
            if (targetPosition == null)
                targetPosition = targetPlayerTransform.GetComponentInParent<PositionMultiplayer>();

            if (targetPosition == null ||
                targetPosition.PartyCurrentLocationState != PositionMultiplayer.PartyLocationState.DungeonInterior ||
                !string.Equals(targetPosition.PartyDungeonInstanceId, expectedDungeonInstanceId, StringComparison.Ordinal))
                return false;

            EnableDungeonParent();

            // The direct/off-map party path enters the correct Unity dungeon instance but
            // deliberately skips exterior fast travel. That means PlayerGPS, StreamingWorld,
            // and PositionMultiplayer can still describe the dungeon the traveler came from.
            // Commit the verified destination dungeon anchor before the final transform snap,
            // just as the saved-dungeon and TeleportPc paths commit their destination context.
            CommitPartyDungeonRendezvousWorldContext(
                targetPosition,
                reason + "-destination-context");

            Vector3 finalPosition = targetPlayerTransform.position;

            ClearTransitionFallingDamage(reason + "-before");
            transform.position = finalPosition;
            SetStanding();
            ClearTransitionFallingDamageWindow(reason + "-after");

            ForceSendMultiplayerCoordinatesNow(reason + "-after-rendezvous");
            RaiseOnMovePlayerToDungeonStartEvent();

            Debug.Log($"[PartyFastTravel] Moved player to party member inside dungeon. instance='{expectedDungeonInstanceId}' final={finalPosition}");
            return true;
        }

        private void CommitPartyDungeonRendezvousWorldContext(
            PositionMultiplayer targetPosition,
            string reason)
        {
            if (dungeon == null || targetPosition == null)
                return;

            int destinationWorldX = targetPosition.x;
            int destinationWorldZ = targetPosition.z;
            string anchorSource = "target-player";

            // The live dungeon's server-authored anchor is the primary identity. The
            // target player's synced X/Z is an important fallback for special/off-map
            // dungeons whose generated instance did not receive an entrance-door anchor.
            if (dungeon.HasDungeonWorldAnchor)
            {
                destinationWorldX = dungeon.DungeonAnchorWorldX;
                destinationWorldZ = dungeon.DungeonAnchorWorldZ;
                anchorSource = "dungeon-anchor";
            }

            if (destinationWorldX <= 0 || destinationWorldZ <= 0)
            {
                Debug.LogWarning($"[PartyFastTravel][WorldContext] No valid destination anchor. dungeon='{dungeon.name}' target={targetPosition.x}/{targetPosition.z} reason={reason}");
                return;
            }

            DFLocation destinationLocation = dungeon.Summary.LocationData;
            DFPosition destinationMapPixel =
                MapsFile.WorldCoordToMapPixel(destinationWorldX, destinationWorldZ);
            bool streamableMapPixel =
                destinationMapPixel.X >= TerrainHelper.minMapPixelX &&
                destinationMapPixel.X <= TerrainHelper.maxMapPixelX &&
                destinationMapPixel.Y >= TerrainHelper.minMapPixelY &&
                destinationMapPixel.Y <= TerrainHelper.maxMapPixelY;

            if (destinationLocation.Loaded &&
                destinationLocation.HasDungeon &&
                streamableMapPixel)
            {
                // For ordinary mapped dungeons, also build/select the correct exterior.
                // This makes a later normal exit or disconnect safety exit return to the
                // destination dungeon rather than the traveler's previous entrance.
                CommitSavedDungeonWorldContext(
                    destinationLocation,
                    destinationWorldX,
                    destinationWorldZ,
                    reason + "-streamable");
            }
            else if (destinationLocation.Loaded && destinationLocation.HasDungeon)
            {
                // Some story/teleport-only dungeons (including Mantellan Crux) have a
                // genuine exterior dungeon entrance outside the normal travel-map terrain
                // bounds. The party popup cannot overland-travel to that pixel, but after
                // the player is safely inside the network dungeon we can use the proven
                // TeleportPc world-context path to build that hidden exterior, then the
                // caller immediately performs the final interior position snap.
                //
                // This is important for disconnect/emergency exit: DungeonEntrance must
                // operate on the destination's hidden exterior, not the exterior belonging
                // to the dungeon the traveler came from.
                PrepareMultiplayerQuestDungeonTeleportWorldPosition(
                    destinationWorldX,
                    destinationWorldZ,
                    reason + "-hidden-exterior");

                Debug.Log($"[PartyFastTravel][WorldContext] Built hidden off-map dungeon exterior through TeleportPc context path. dungeon='{destinationLocation.RegionName}/{destinationLocation.Name}' anchor={destinationWorldX}/{destinationWorldZ} mapPixel={destinationMapPixel.X}/{destinationMapPixel.Y} source={anchorSource} reason={reason}");
            }
            else
            {
                Debug.LogWarning($"[PartyFastTravel][WorldContext] Destination dungeon location data was not loaded; retaining anchor-only network correction. dungeon='{dungeon.name}' anchor={destinationWorldX}/{destinationWorldZ} mapPixel={destinationMapPixel.X}/{destinationMapPixel.Y} source={anchorSource} reason={reason}");
            }

            // Pure clients need the same short-lived publisher hold used by loaded-save
            // dungeon conversion. It prevents the next PlayerGPS polling tick from
            // re-publishing the pre-travel coordinate before the world context settles.
            PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
            if (localPlayer != null)
            {
                localPlayer.ActivatePureClientPartyDungeonAnchor(
                    destinationWorldX,
                    destinationWorldZ,
                    reason);
            }

            Debug.Log($"[PartyFastTravel][WorldContext] Destination committed. dungeon='{destinationLocation.RegionName}/{destinationLocation.Name}' anchor={destinationWorldX}/{destinationWorldZ} mapPixel={destinationMapPixel.X}/{destinationMapPixel.Y} streamable={streamableMapPixel} source={anchorSource} reason={reason}");
        }

        /// <summary>
        /// Multiplayer vampire cemetery wake-up helper.
        /// Uses the live network dungeon's own EnterMarker after the dungeon is bound,
        /// then refreshes the exterior world context so DungeonExit returns to the cemetery.
        /// </summary>
        public bool TryMovePlayerToDungeonEnterMarkerForMultiplayerWake(DFLocation location, string reason)
        {
            if (!ReferenceComponents())
                return false;

            if (dungeon == null || !isPlayerInsideDungeon || !dungeon.isSet)
                return false;

            if (location.Loaded)
            {
                if (!string.Equals(dungeon.Summary.RegionName, location.RegionName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(dungeon.Summary.LocationName, location.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Vampire wake-up is not the normal exterior entrance. Vanilla StartDungeonInterior()
            // prefers EnterMarker here, and cemetery crypts should have one. Do not silently
            // fall back to StartMarker while the network dungeon is still finishing setup,
            // because that creates the wrong "start of dungeon" wake-up placement.
            GameObject marker = dungeon.EnterMarker;
            if (marker == null)
            {
                Debug.LogWarning($"[VampireMP] Live cemetery EnterMarker is not ready yet; waiting. reason={reason} dungeon='{dungeon.name}' dungeonY={dungeon.transform.position.y} PositionY={dungeon.PositionY}");
                return false;
            }

            Vector3 markerPosition = marker.transform.position;
            if (Mathf.Abs(markerPosition.x) < 0.05f && Mathf.Abs(markerPosition.y) < 0.05f && Mathf.Abs(markerPosition.z) < 0.05f)
            {
                Debug.LogWarning($"[VampireMP] Live cemetery EnterMarker is still at world origin; waiting. reason={reason} marker='{marker.name}' dungeon='{dungeon.name}' dungeonY={dungeon.transform.position.y} PositionY={dungeon.PositionY}");
                return false;
            }

            EnableDungeonParent();

            // Same reason TeleportPc does this after the dungeon is bound: pure clients can still
            // have the old inn/town StreamingWorld context, which breaks leaving the dungeon later.
            PrepareMultiplayerQuestDungeonTeleportWorldPositionFromDungeon(reason + "-dungeon-bound");

            ClearTransitionFallingDamage(reason + "-before-enter-marker");
            MovePlayerToMarker(marker);
            SetStanding();
            ClearTransitionFallingDamageWindow(reason + "-after-enter-marker");

            PlayerTeleportedIntoDungeon = true;
            ForceSendMultiplayerCoordinatesNow(reason + "-after-enter-marker");

            Debug.Log($"[VampireMP] Moved vampire wake-up player to live cemetery EnterMarker. reason={reason} final={transform.position} dungeonY={dungeon.transform.position.y} PositionY={dungeon.PositionY}");
            return true;
        }

        /// <summary>
        /// Player is leaving dungeon, transition them back outside.
        /// </summary>
        /// <param name="doFade">Fade HUD after transition if true.</param>
        public void TransitionDungeonExterior(bool doFade = false)
        {
            if (!ReferenceComponents() || !dungeon || !isPlayerInsideDungeon)
                return;

            // Redirect to coroutine verion for fade support
            if (doFade)
            {
                StartCoroutine(FadedTransitionDungeonExterior());
                return;
            }

            // Perform transition
            DungeonTransitionExteriorLogic();
        }

private IEnumerator FadedTransitionDungeonExterior()
{
    DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();
    yield return new WaitForEndOfFrame();

    DungeonTransitionExteriorLogic(); // does NOT destroy the dungeon anymore

            float fadeTime = 0.7f;
            if (!GameManager.Instance.StreamingWorld.IsInit)
                fadeTime = 1.5f;

  /*  // ✅ Now destroy dungeon after fade
    if (dungeon != null)
    {
        PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
        dungeon = null;
    }*/

    DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(fadeTime);
}


        private void ForceSendMultiplayerCoordinatesNow(string reason)
        {
            try
            {
                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer == null)
                    return;

                GameObject playerObject = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
                if (playerObject != null)
                    localPlayer.transform.position = playerObject.transform.position;

                PositionMultiplayer positionMultiplayer = localPlayer.GetComponent<PositionMultiplayer>();
                if (positionMultiplayer != null)
                    positionMultiplayer.ForceSendCurrentCoordinatesNow(reason);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TeleportPcMP] Failed to force-send multiplayer coordinates. reason={reason} error={ex.Message}");
            }
        }

        private void DungeonTransitionExteriorLogic()
        {
            // Raise event
            RaiseOnPreTransitionEvent(TransitionType.ToDungeonExterior);

            // Do not force a StreamingWorld map-pixel teleport during dungeon exit.
            // TeleportPc exact entrance context is prepared before dungeon generation/entry now.
            // Calling world.TeleportToCoordinates() here affects every MP dungeon exit and causes
            // a visible one-frame snap to the exterior map-pixel/world origin before the normal
            // DungeonEntrance auto-reposition fixes the final placement.

            // Keep network dungeon objects alive. Disable the dungeon parent, but do not destroy
            // the current dungeon object during MP exits.
            if (NetworkServer.active || NetworkClient.active)
                EnableExteriorParent(false);
            else
                EnableExteriorParent();

            // Player is now outside dungeon
            isPlayerInside = false;
            isPlayerInsideDungeon = false;
            isPlayerInsideDungeonCastle = false;
            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();
            PlayerTeleportedIntoDungeon = false;
            ClearDungeonWaterState();

            // Position player to the dungeon entrance using the normal StreamingWorld path.
            world.SetAutoReposition(StreamingWorld.RepositionMethods.DungeonEntrance, Vector3.zero);

            // TeleportPc context is one-shot. Do not let it leak into later normal dungeon entries/exits.
            ClearTeleportPcDungeonWorldContext("dungeon-exit");

            // Raise event
            RaiseOnTransitionDungeonExteriorEvent();
        }

        /// <summary>
        /// Prepares for leaving dungeon, but do not perform transition logic. Reposition process is up to the caller.
        /// </summary>
        public void TransitionDungeonExteriorImmediate()
        {
            if (!ReferenceComponents() || !dungeon || !isPlayerInsideDungeon)
                return;

            RaiseOnPreTransitionEvent(PlayerEnterExit.TransitionType.ToDungeonExterior);
        }

        /// <summary>
        /// Local safety exit used when multiplayer starts/stops while the local player is inside a dungeon,
        /// or when a networked dungeon object disappears underneath the player. This does not move or edit
        /// dungeon/enemy placement; it only restores the local player to exterior state.
        /// </summary>
        public bool EmergencyExitDungeonForNetworkChange(string reason)
        {
            if (emergencyDungeonExitInProgress || !isPlayerInsideDungeon)
                return false;

            emergencyDungeonExitInProgress = true;
            try
            {
                if (!ReferenceComponents())
                    return false;

                Debug.LogWarning($"[DungeonNetworkSafety] Emergency dungeon exterior transition. reason={reason} dungeon={(dungeon ? dungeon.name : "null")} networkActive={(NetworkServer.active || NetworkClient.active)}");

                // Fire the same transition events as a normal dungeon exit where possible.
                RaiseOnPreTransitionEvent(TransitionType.ToDungeonExterior);

                DaggerfallDungeon oldDungeon = dungeon;

                // Enable exterior without running the normal cleanup path. The normal cleanup path uses
                // DestroyIfMultiplayerSafe(), which intentionally refuses to destroy networked objects while
                // networking is active. Mirror will clean up spawned dungeon objects on disconnect.
                EnableExteriorParent(false);

                isPlayerInside = false;
                isPlayerInsideDungeon = false;
                isPlayerInsideDungeonCastle = false;
                isPlayerInsideSpecialArea = false;
                lastPlayerDungeonBlockIndex = -1;
                playerDungeonBlockData = new DFLocation.DungeonBlock();
                PlayerTeleportedIntoDungeon = false;
                ClearDungeonWaterState();

                if (world != null)
                {
                    world.suppressWorld = false;
                    world.SetAutoReposition(StreamingWorld.RepositionMethods.DungeonEntrance, Vector3.zero);
                }

                // If this was a local/non-networked dungeon while networking is active, remove it so the
                // player cannot remain tied to stale singleplayer dungeon state. Do not destroy networked
                // dungeons here; Mirror/server ownership handles those.
                if (oldDungeon != null && !DungeonHasUsableNetworkIdentity(oldDungeon))
                    Destroy(oldDungeon.gameObject);

                dungeon = null;

                GameManager.UpdateShadowDistance();
                RaiseOnTransitionDungeonExteriorEvent();
                return true;
            }
            finally
            {
                emergencyDungeonExitInProgress = false;
            }
        }

        private void HandleNetworkDungeonSafety()
        {
            bool networkActive = NetworkServer.active || NetworkClient.active;

            if (networkDungeonConversionInProgress)
            {
                if (!networkActive)
                    FailPendingNetworkDungeonConversion("network-stopped-during-conversion");
                else if (Time.realtimeSinceStartup - pendingNetworkDungeonConversionStartedAt > NetworkDungeonConversionTimeout)
                    FailPendingNetworkDungeonConversion("host-response-timeout");

                // The source SP dungeon has intentionally been removed and the replacement
                // is asynchronous. Do not let the normal missing-dungeon safety race it.
                lastNetworkActiveState = networkActive;
                return;
            }

            if (networkActive != lastNetworkActiveState)
            {
                // Building interiors remain local/non-networked. When MP starts, preserve the
                // current interior instead of forcing the player outside. An existing MP-offset
                // interior is left untouched; an SP-height interior is shifted in-place together
                // with the player and any detached active interior save objects.
                if (networkActive && IsPlayerInsideBuilding)
                {
                    if (!EnsureCurrentBuildingInteriorUsesMultiplayerOffset("network-started-while-inside-building"))
                    {
                        EmergencyExitBuildingForNetworkChange("network-started-interior-offset-conversion-failed");
                    }
                    else
                    {
                        QueueBuildingQuestFoeReplayAfterNetworkStart("network-started-while-inside-building");
                    }
                }

                if (isPlayerInsideDungeon)
                {
                    if (networkActive && TryBeginCurrentLocalDungeonConversion("network-started-while-inside-local-dungeon"))
                    {
                        lastNetworkActiveState = networkActive;
                        return;
                    }

                    // Disconnect behavior remains unchanged in this phase: a player inside
                    // an MP dungeon is moved outside rather than reconstructing an SP copy.
                    EmergencyExitDungeonForNetworkChange(networkActive ? "network-started-while-inside-dungeon" : "network-stopped-while-inside-dungeon");
                }

                lastNetworkActiveState = networkActive;
            }

            if (!isPlayerInsideDungeon)
                return;

            // Extra safety: if the network is already inactive but this local player is still
            // bound to a spawned network dungeon object, force the normal emergency exterior
            // transition. This covers TeleportPc client entries where the regular OnStopClient
            // callback/state-change path can be missed.
            if (!networkActive && dungeon != null && DungeonHasUsableNetworkIdentity(dungeon))
            {
                EmergencyExitDungeonForNetworkChange("network-inactive-current-dungeon-still-networked");
                return;
            }

            // Some client disconnect paths can leave NetworkClient.active true for a short time while
            // the local PlayerMultiplayer object has already been destroyed/cleared. In that state the
            // old network dungeon can remain locally visible and the normal networkActive edge check is
            // missed. Treat "client active but no local multiplayer player" as a disconnect safety case.
            if (!NetworkServer.active && NetworkClient.active && dungeon != null && DungeonHasUsableNetworkIdentity(dungeon) && PlayerMultiplayer.GetLocalPlayer() == null)
            {
                EmergencyExitDungeonForNetworkChange("client-active-but-local-player-missing");
                return;
            }

            // If networking is active, the player should not be inside a non-networked/SP dungeon.
            if (networkActive && dungeon != null && !DungeonHasUsableNetworkIdentity(dungeon))
            {
                EmergencyExitDungeonForNetworkChange("network-active-current-dungeon-has-no-netid");
                return;
            }

            // If the local network dungeon was destroyed during disconnect/despawn while the player still
            // believes they are inside, immediately push them back to exterior before they fall into the void.
            if (dungeon == null)
                EmergencyExitDungeonForNetworkChange("inside-dungeon-but-dungeon-object-missing");
        }

        public bool EmergencyExitBuildingForNetworkChange(string reason)
        {
            if (emergencyBuildingExitInProgress || !IsPlayerInsideBuilding || interior == null)
                return false;

            emergencyBuildingExitInProgress = true;
            try
            {
                if (!ReferenceComponents())
                    return false;

                Debug.LogWarning($"[InteriorNetworkSafety] Emergency building exterior transition. reason={reason} interior={(interior ? interior.name : "null")} buildingKey={(interior ? interior.EntryDoor.buildingKey : 0)} networkActive={(NetworkServer.active || NetworkClient.active)}");

                // Use the normal building-exit path so exterior-door matching, scene cache,
                // currentInteriorUsesMultiplayerYOffset compensation, and transition events remain consistent.
                TransitionExterior(false);
                return true;
            }
            finally
            {
                emergencyBuildingExitInProgress = false;
            }
        }

        private bool DungeonHasUsableNetworkIdentity(DaggerfallDungeon dungeonToCheck)
        {
            if (dungeonToCheck == null)
                return false;

            NetworkIdentity identity = dungeonToCheck.GetComponent<NetworkIdentity>();
            if (identity == null)
                return false;

            // Dungeons are spawned network objects. Unlike the host player netId case, a dungeon netId of 0
            // means the dungeon is not currently a spawned network dungeon.
            return identity.netId != 0;
        }

        private void RespawnPlayerDungeonExteriorForNetworkSafety(int worldX, int worldZ, string reason)
        {
            if (dungeon)
                PlayerEnterExit.DestroyIfMultiplayerSafe(dungeon.gameObject);
            if (interior)
                Destroy(interior.gameObject);

            SaveLoadManager.DeregisterAllSerializableGameObjects();
            isRespawning = true;
            SetExteriorDoors(null);
            StartCoroutine(RespawnDungeonExteriorForNetworkSafety(worldX, worldZ, reason));
        }

        private IEnumerator RespawnDungeonExteriorForNetworkSafety(int worldX, int worldZ, string reason)
        {
            yield return new WaitForEndOfFrame();

            lastPlayerDungeonBlockIndex = -1;
            playerDungeonBlockData = new DFLocation.DungeonBlock();
            ClearDungeonWaterState();

            isPlayerInside = false;
            isPlayerInsideDungeon = false;
            isPlayerInsideDungeonCastle = false;
            isPlayerInsideSpecialArea = false;
            PlayerTeleportedIntoDungeon = false;

            playerGPS.WorldX = worldX;
            playerGPS.WorldZ = worldZ;

            DFPosition pos = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
            world.MapPixelX = pos.X;
            world.MapPixelY = pos.Y;

            Debug.LogWarning($"[DungeonNetworkSafety] Restoring outside dungeon entrance. reason={reason} world={worldX}/{worldZ} mapPixel={pos.X}/{pos.Y}");

            EnableExteriorParent(false);
            world.TeleportToCoordinates(pos.X, pos.Y, StreamingWorld.RepositionMethods.DungeonEntrance);

            while (world.IsInit)
                yield return new WaitForEndOfFrame();

            isRespawning = false;
            RaiseOnTransitionDungeonExteriorEvent();
            RaiseOnRespawnerCompleteEvent();
        }

        #endregion

        #region Private Methods

        private void SpecialAreaCheck()
        {
            if (!isPlayerInsideDungeon)
            {
                isPlayerInsideSpecialArea = false;
                return;
            }

            switch (playerDungeonBlockData.BlockName)
            {
                case "S0000161.RDB":    // Daggerfall treasure room
                    isPlayerInsideSpecialArea = true;
                    break;
                default:
                    isPlayerInsideSpecialArea = false;
                    break;
            }
        }

        public void ClearTransitionFallingDamage(string reason = null)
        {
            try
            {
                if (GameManager.Instance != null && GameManager.Instance.AcrobatMotor != null)
                    GameManager.Instance.AcrobatMotor.ClearFallingDamage();
            }
            catch { }
        }

        public void ClearTransitionFallingDamageWindow(string reason = null, int frames = 8)
        {
            ClearTransitionFallingDamage(reason);
            StartCoroutine(CoClearTransitionFallingDamageWindow(reason, frames));
        }

        private IEnumerator CoClearTransitionFallingDamageWindow(string reason, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                ClearTransitionFallingDamage(reason);
                yield return null;
            }
        }

        private void SetStanding()
        {
            if (!NetworkServer.active && !NetworkClient.active)
            {
                SetStandingSingleplayerVanilla();
                return;
            }

            // A door transition is a teleport, not a fall. Clear fall tracking before
            // and after snapping because MP interiors can be shifted far below exterior Y.
            ClearTransitionFallingDamage("SetStanding-before-raycast");

            // Snap player to ground
            RaycastHit hit;
            Ray ray = new Ray(transform.position, Vector3.down);
            if (Physics.Raycast(ray, out hit, PlayerHeightChanger.controllerStandingHeight * 2f))
            {
                // Position player at hit position plus just over half controller height up
                Vector3 pos = hit.point;
                pos.y += controller.height / 2f + 0.25f;
                transform.position = pos;
            }

            ClearTransitionFallingDamageWindow("SetStanding-after-snap");
        }

        /// <summary>
        /// Positively validates one side of an interior doorway without moving the player.
        /// This is intentionally conservative and generic: a candidate is considered safe only when
        /// a non-trigger, sufficiently horizontal collider belonging to this exact interior is directly
        /// beneath it. This lets malformed/replacement interiors recover from a reversed door normal
        /// without changing normal DFU door placement when the usual side is already valid.
        /// </summary>
        private bool IsSafeInteriorDoorLandingCandidate(
            DaggerfallInterior targetInterior,
            Vector3 candidatePosition,
            out string reason)
        {
            reason = string.Empty;

            if (targetInterior == null)
            {
                reason = "missing-interior";
                return false;
            }

            if (controller == null)
            {
                reason = "missing-character-controller";
                return false;
            }

            if (float.IsNaN(candidatePosition.x) || float.IsInfinity(candidatePosition.x) ||
                float.IsNaN(candidatePosition.y) || float.IsInfinity(candidatePosition.y) ||
                float.IsNaN(candidatePosition.z) || float.IsInfinity(candidatePosition.z))
            {
                reason = "invalid-candidate-position";
                return false;
            }

            // Match the scale of SetStanding(), but ignore triggers and require the hit to come from
            // this interior rather than exterior terrain/geometry that can overlap SP interiors in Y.
            float rayDistance = Mathf.Max(
                PlayerHeightChanger.controllerStandingHeight * 2f,
                controller.height * 0.5f + 1f);

            RaycastHit[] hits = Physics.RaycastAll(
                candidatePosition + Vector3.up * 0.05f,
                Vector3.down,
                rayDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)
            {
                reason = "no-floor-hit";
                return false;
            }

            // RaycastAll order is not guaranteed. Choose the nearest acceptable floor belonging
            // to this interior so unrelated exterior/world colliders cannot validate the wrong side.
            float nearestDistance = float.MaxValue;
            RaycastHit nearestHit = new RaycastHit();
            bool foundInteriorFloor = false;

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null)
                    continue;

                Transform hitTransform = hit.collider.transform;
                if (hitTransform == null ||
                    (hitTransform != targetInterior.transform && !hitTransform.IsChildOf(targetInterior.transform)))
                    continue;

                float upDot = Vector3.Dot(hit.normal, Vector3.up);
                if (upDot < 0.25f)
                    continue;

                if (hit.distance < nearestDistance)
                {
                    nearestDistance = hit.distance;
                    nearestHit = hit;
                    foundInteriorFloor = true;
                }
            }

            if (!foundInteriorFloor)
            {
                reason = "no-interior-floor-below";
                return false;
            }

            // The doorway position can be above the exact standing foot height. Keep this generous
            // enough to match SetStanding(), while still rejecting a floor on a different storey.
            if (nearestHit.distance > PlayerHeightChanger.controllerStandingHeight * 2f)
            {
                reason = $"interior-floor-too-far distance={nearestHit.distance:F3}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Positively validates the MP doorway landing before the delayed-teleport guard is armed.
        /// This keeps the compatibility guard out of the way when the normal transition has not
        /// actually produced a safe standing position and an external rescue/placement mod may be
        /// needed to move the player somewhere valid.
        /// </summary>
        private bool IsSafeMultiplayerInteriorLanding(
            DaggerfallInterior targetInterior,
            Vector3 authoritativeLandingPosition,
            Vector3 expectedDoorLandingPosition,
            out string reason)
        {
            reason = string.Empty;

            if (targetInterior == null || interior != targetInterior)
            {
                reason = "interior-changed";
                return false;
            }

            if (controller == null)
            {
                reason = "missing-character-controller";
                return false;
            }

            Vector3 currentPosition = transform.position;
            if (float.IsNaN(currentPosition.x) || float.IsInfinity(currentPosition.x) ||
                float.IsNaN(currentPosition.y) || float.IsInfinity(currentPosition.y) ||
                float.IsNaN(currentPosition.z) || float.IsInfinity(currentPosition.z))
            {
                reason = "invalid-player-position";
                return false;
            }

            // Internal setup after SetStanding() should not have moved the player away from the
            // resolved doorway landing before external transition subscribers even run.
            float setupDriftLimit = Mathf.Max(1f, controller.radius * 2f);
            if ((currentPosition - authoritativeLandingPosition).sqrMagnitude > setupDriftLimit * setupDriftLimit)
            {
                reason = $"setup-drift current={currentPosition} resolved={authoritativeLandingPosition}";
                return false;
            }

            // SetStanding() can legitimately adjust Y, but X/Z should still correspond to the
            // doorway landing chosen by the MP-offset-aware transition.
            Vector2 currentXZ = new Vector2(currentPosition.x, currentPosition.z);
            Vector2 expectedDoorXZ = new Vector2(expectedDoorLandingPosition.x, expectedDoorLandingPosition.z);
            float doorwayHorizontalLimit = Mathf.Max(1.25f, controller.radius * 2.5f);
            if ((currentXZ - expectedDoorXZ).sqrMagnitude > doorwayHorizontalLimit * doorwayHorizontalLimit)
            {
                reason = $"not-at-resolved-door current={currentPosition} doorLanding={expectedDoorLandingPosition}";
                return false;
            }

            // The guard is only allowed to override a later teleport when the original landing has
            // a real, non-trigger, sufficiently horizontal surface immediately beneath the player's
            // feet. This is the key distinction between an unnecessary placement override and a legitimate void rescue.
            float rayDistance = controller.height * 0.5f + 1f;
            RaycastHit hit;
            Ray ray = new Ray(currentPosition + Vector3.up * 0.05f, Vector3.down);
            if (!Physics.Raycast(
                ray,
                out hit,
                rayDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            {
                reason = "no-solid-floor-below";
                return false;
            }

            float feetY = currentPosition.y - controller.height * 0.5f;
            float floorGap = feetY - hit.point.y;
            if (floorGap < -0.1f || floorGap > 0.75f)
            {
                reason = $"floor-not-immediately-below gap={floorGap:F3} hit={hit.point}";
                return false;
            }

            float upDot = Vector3.Dot(hit.normal, Vector3.up);
            if (upDot < 0.25f)
            {
                reason = $"surface-too-vertical normal={hit.normal}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Briefly protects the just-resolved MP building entry from a delayed, implausible upward
        /// placement change. The guard only has authority while the player remains effectively at
        /// the entrance; once the player materially moves away, normal interior traversal owns the
        /// player position and the guard permanently ends for that transition.
        /// </summary>
        private void StartMultiplayerInteriorLandingGuard(DaggerfallInterior targetInterior, Vector3 authoritativeLandingPosition)
        {
            if (multiplayerInteriorLandingGuardCoroutine != null)
            {
                StopCoroutine(multiplayerInteriorLandingGuardCoroutine);
                multiplayerInteriorLandingGuardCoroutine = null;
            }

            multiplayerInteriorLandingGuardCoroutine = StartCoroutine(
                CoGuardMultiplayerInteriorLanding(targetInterior, authoritativeLandingPosition));
        }

        private IEnumerator CoGuardMultiplayerInteriorLanding(DaggerfallInterior targetInterior, Vector3 authoritativeLandingPosition)
        {
            float expiresAt = Time.realtimeSinceStartup + multiplayerInteriorLandingGuardDuration;
            Vector3 previousPosition = authoritativeLandingPosition;

            while (Time.realtimeSinceStartup < expiresAt)
            {
                // Stop as soon as this is no longer the same live MP-offset building transition.
                if ((!NetworkServer.active && !NetworkClient.active) ||
                    !currentInteriorUsesMultiplayerYOffset ||
                    !IsPlayerInsideBuilding ||
                    targetInterior == null ||
                    interior != targetInterior)
                {
                    break;
                }

                // This is an entry-transition guard, not a general teleport blocker. If the player
                // had already moved materially away from the resolved entrance on the previous frame,
                // relinquish ownership permanently before evaluating any later movement. Using the
                // previous frame is important: the unexpected snap itself must not count as the player
                // intentionally leaving the entrance area.
                if (!IsPositionWithinMultiplayerInteriorEntryGuard(previousPosition, authoritativeLandingPosition))
                {
                    Debug.Log($"[InteriorLandingGuard] Ended because player moved away from entry. interior={targetInterior.name} previous={previousPosition} entry={authoritativeLandingPosition}");
                    break;
                }

                Vector3 currentPosition = transform.position;
                Vector3 instantDelta = currentPosition - previousPosition;
                Vector3 landingDelta = currentPosition - authoritativeLandingPosition;

                // Detect only an implausible single-frame upward displacement while the player was
                // still at the entrance on the previous frame. Ordinary traversal that first moves
                // away from the entry area causes this guard to end instead of pulling the player back.
                bool jumpedUpFromPreviousFrame = instantDelta.y >= multiplayerInteriorLandingGuardMinUpwardSnap;
                bool isStillAboveResolvedLanding = landingDelta.y >= multiplayerInteriorLandingGuardMinUpwardSnap;
                bool movedTeleportDistance = instantDelta.sqrMagnitude >=
                    multiplayerInteriorLandingGuardMinInstantDistance * multiplayerInteriorLandingGuardMinInstantDistance;

                if (jumpedUpFromPreviousFrame && isStillAboveResolvedLanding && movedTeleportDistance)
                {
                    Debug.LogWarning(
                        $"[InteriorLandingGuard] Correcting delayed upward interior placement. " +
                        $"interior={targetInterior.name} entry={authoritativeLandingPosition} " +
                        $"previous={previousPosition} displaced={currentPosition} instantDelta={instantDelta}.");

                    // Restore the last pre-snap position rather than forcing the exact original doorway
                    // point. This preserves any small legitimate movement that occurred while the player
                    // was still inside the guarded entry area.
                    ClearTransitionFallingDamage("building-interior-landing-guard-before-restore");
                    transform.position = previousPosition;
                    SetStanding();
                    ClearTransitionFallingDamageWindow("building-interior-landing-guard-after-restore");
                    ForceSendMultiplayerCoordinatesNow("building-interior-landing-guard-restored");
                    break;
                }

                // If ordinary movement has now carried the player away from the entry area, end the
                // guard immediately. Do this only after the unexpected-snap test above so the snap itself
                // cannot cancel its own correction.
                if (!IsPositionWithinMultiplayerInteriorEntryGuard(currentPosition, authoritativeLandingPosition))
                {
                    Debug.Log($"[InteriorLandingGuard] Ended because player left entry area. interior={targetInterior.name} current={currentPosition} entry={authoritativeLandingPosition}");
                    break;
                }

                previousPosition = currentPosition;
                yield return null;
            }

            multiplayerInteriorLandingGuardCoroutine = null;
        }

        private bool IsPositionWithinMultiplayerInteriorEntryGuard(Vector3 position, Vector3 authoritativeLandingPosition)
        {
            Vector3 delta = position - authoritativeLandingPosition;
            float horizontalDistanceSqr = delta.x * delta.x + delta.z * delta.z;
            float horizontalLimit = Mathf.Max(
                multiplayerInteriorLandingGuardMaxEntryHorizontalDistance,
                controller != null ? controller.radius * 1.5f : multiplayerInteriorLandingGuardMaxEntryHorizontalDistance);

            if (horizontalDistanceSqr > horizontalLimit * horizontalLimit)
                return false;

            if (Mathf.Abs(delta.y) > multiplayerInteriorLandingGuardMaxEntryVerticalDistance)
                return false;

            return true;
        }

        private bool ReferenceComponents()
        {
            // Look for required components
            if (controller == null)
                controller = GetComponent<CharacterController>();
            
            // Fail if missing required components
            if (dfUnity == null || controller == null)
                return false;

            return true;
        }

        public void SetExteriorDoors(StaticDoor[] doors)
        {
            exteriorDoors.Clear();
            if (doors != null && doors.Length > 0)
                exteriorDoors.AddRange(doors);
        }

        private WorldContext GetWorldContext()
        {
            if (!IsPlayerInside)
                return WorldContext.Exterior;
            else if (IsPlayerInsideBuilding)
                return WorldContext.Interior;
            else if (isPlayerInsideDungeon)
                return WorldContext.Dungeon;
            else
                return WorldContext.Nothing;
        }

        #endregion

        #region Event Arguments

        /// <summary>
        /// Types of transition encountered by event system.
        /// </summary>
        public enum TransitionType
        {
            NotDefined,
            ToBuildingInterior,
            ToBuildingExterior,
            ToDungeonInterior,
            ToDungeonExterior,
        }

        /// <summary>
        /// Arguments for PlayerEnterExit events.
        /// All interior/exterior/dungeon transitions use these arguments.
        /// Valid members will depend on which transition event was fired.
        /// </summary>
        public class TransitionEventArgs : System.EventArgs
        {
            /// <summary>The type of transition.</summary>
            public TransitionType TransitionType { get; set; }

            /// <summary>The exterior StaticDoor clicked to initiate transition. For exterior to interior transitions only.</summary>
            public StaticDoor StaticDoor { get; set; }

            /// <summary>The newly instanced building interior. For building interior transitions only.</summary>
            public DaggerfallInterior DaggerfallInterior { get; set; }

            /// <summary>The newly instanced dungeon interior. For dungeon interior transitions only.</summary>
            public DaggerfallDungeon DaggerfallDungeon { get; set; }

            /// <summary>Constructor.</summary>
            public TransitionEventArgs()
            {
                TransitionType = PlayerEnterExit.TransitionType.NotDefined;
                StaticDoor = new StaticDoor();
                DaggerfallInterior = null;
                DaggerfallDungeon = null;
            }

            /// <summary>Constructor helper.</summary>
            public TransitionEventArgs(TransitionType transitionType)
                : base()
            {
                this.TransitionType = transitionType;
            }

            /// <summary>Constructor helper.</summary>
            public TransitionEventArgs(TransitionType transitionType, StaticDoor staticDoor, DaggerfallInterior daggerfallInterior = null, DaggerfallDungeon daggerfallDungeon = null)
                : base()
            {
                this.TransitionType = transitionType;
                this.StaticDoor = staticDoor;
                this.DaggerfallInterior = daggerfallInterior;
                this.DaggerfallDungeon = daggerfallDungeon;
            }
        }

        #endregion

        #region Event Handlers

        // Notify player when they enter location rect
        // For exterior towns, print out "You are entering %s".
        // For exterior dungeons, print out flavour text.
        private void PlayerGPS_OnEnterLocationRect(DFLocation location)
        {
            const int set1StartID = 500;
            const int set2StartID = 520;

            if (playerGPS && !isPlayerInside && !networkDungeonConversionInProgress)
            {
                if (location.MapTableData.LocationType == DFRegion.LocationTypes.DungeonLabyrinth ||
                    location.MapTableData.LocationType == DFRegion.LocationTypes.DungeonKeep ||
                    location.MapTableData.LocationType == DFRegion.LocationTypes.DungeonRuin ||
                    location.MapTableData.LocationType == DFRegion.LocationTypes.Graveyard)
                {
                    // Get text ID based on set start and dungeon type index
                    int dungeonTypeIndex = (int)location.MapTableData.DungeonType;
                    int set1ID = set1StartID + dungeonTypeIndex;
                    int set2ID = set2StartID + dungeonTypeIndex;

                    // Select two sets of flavour text based on dungeon type
                    string flavourText1 = DaggerfallUnity.Instance.TextProvider.GetRandomText(set1ID);
                    string flavourText2 = DaggerfallUnity.Instance.TextProvider.GetRandomText(set2ID);

                    // Show flavour text a bit longer than in classic
                    DaggerfallUI.AddHUDText(flavourText1, 3);
                    DaggerfallUI.AddHUDText(flavourText2, 3);
                }
                else if (location.MapTableData.LocationType != DFRegion.LocationTypes.Coven &&
                    location.MapTableData.LocationType != DFRegion.LocationTypes.HomeYourShips)
                {
                    // Show "You are entering %s"
                    string youAreEntering = TextManager.Instance.GetLocalizedText("youAreEntering");
                    youAreEntering = youAreEntering.Replace("%s", TextManager.Instance.GetLocalizedLocationName(location.MapTableData.MapId, location.Name));
                    DaggerfallUI.AddHUDText(youAreEntering, 2);

                    // Check room rentals in this location, and display how long any rooms are rented for
                    int mapId = playerGPS.CurrentLocation.MapTableData.MapId;
                    PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                    playerEntity.RemoveExpiredRentedRooms();
                    List<RoomRental_v1> rooms = playerEntity.GetRentedRooms(mapId);
                    if (rooms.Count > 0)
                    {
                        foreach (RoomRental_v1 room in rooms)
                        {
                            string remainingHours = PlayerEntity.GetRemainingHours(room).ToString();
                            DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("youHaveRentedRoom").Replace("%s", room.name).Replace("%d", remainingHours), 6);
                        }
                    }

                    if (holidayTextTimer <= 0 && !holidayTextPrimed)
                    {
                        holidayTextTimer = 2.5f; // Short delay to give save game fade-in time to finish
                        holidayTextPrimed = true;
                    }
                    holidayTextLocation = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;

                    // note Nystul: this next line is not enough to manage questor dictionary update since player might load a savegame in an interior -
                    // so this never gets triggered and questor list is rebuild always as a consequence
                    // a better thing is if talkmanager handles all this by itself without making changes to PlayerEnterExit necessary and use events/delegates
                    // -> so I will outcomment next line but leave it in so that original author stumbles across this comment
                    // fixed this in TalkManager class
                    // TalkManager.Instance.LastExteriorEntered = location.LocationIndex;
                }
            }
        }

        private void EntityEffectBroker_OnNewMagicRound()
        {
            // Player in holy place
            isPlayerInHolyPlace = false;
            if (WorldContext == WorldContext.Interior && interior != null)
            {
                if (interior.BuildingData.BuildingType == DFLocation.BuildingTypes.Temple ||
                    interior.BuildingData.FactionId == (int)FactionFile.FactionIDs.Fighter_Trainers)
                    isPlayerInHolyPlace = true;
            }
        }

        #endregion

        #region Events

        // OnPreTransition - Called PRIOR to any transition, other events called AFTER transition.
        public delegate void OnPreTransitionEventHandler(TransitionEventArgs args);
        /// <summary>
        /// Unlike other events in this class, this one is raised before the transition has been performed.
        /// It's always followed by <see cref="OnFailedTransition"/> or one of the other events for success.
        /// </summary>
        public static event OnPreTransitionEventHandler OnPreTransition;
        protected virtual void RaiseOnPreTransitionEvent(TransitionType transitionType)
        {
            TransitionEventArgs args = new TransitionEventArgs(transitionType);
            if (OnPreTransition != null)
                OnPreTransition(args);
        }
        protected virtual void RaiseOnPreTransitionEvent(TransitionType transitionType, StaticDoor staticDoor)
        {
            TransitionEventArgs args = new TransitionEventArgs(transitionType, staticDoor);
            if (OnPreTransition != null)
                OnPreTransition(args);
        }

        // OnTransitionInterior
        public delegate void OnTransitionInteriorEventHandler(TransitionEventArgs args);
        public static event OnTransitionInteriorEventHandler OnTransitionInterior;
        protected virtual void RaiseOnTransitionInteriorEvent(StaticDoor staticDoor, DaggerfallInterior daggerfallInterior)
        {
            TransitionEventArgs args = new TransitionEventArgs(TransitionType.ToBuildingInterior, staticDoor, daggerfallInterior);
            if (OnTransitionInterior != null)
                OnTransitionInterior(args);
        }

        // OnTransitionExterior
        public delegate void OnTransitionExteriorEventHandler(TransitionEventArgs args);
        public static event OnTransitionExteriorEventHandler OnTransitionExterior;
        protected virtual void RaiseOnTransitionExteriorEvent()
        {
            TransitionEventArgs args = new TransitionEventArgs(TransitionType.ToBuildingExterior);
            if (OnTransitionExterior != null)
                OnTransitionExterior(args);
        }

        // OnTransitionDungeonInterior
        public delegate void OnTransitionDungeonInteriorEventHandler(TransitionEventArgs args);
        public static event OnTransitionDungeonInteriorEventHandler OnTransitionDungeonInterior;
        public virtual void RaiseOnTransitionDungeonInteriorEvent(StaticDoor staticDoor, DaggerfallDungeon daggerfallDungeon)
        {
            TransitionEventArgs args = new TransitionEventArgs(TransitionType.ToDungeonInterior, staticDoor, null, daggerfallDungeon);
            if (OnTransitionDungeonInterior != null)
                OnTransitionDungeonInterior(args);
        }

        // OnTransitionDungeonExterior
        public delegate void OnTransitionDungeonExteriorEventHandler(TransitionEventArgs args);
        public static event OnTransitionDungeonExteriorEventHandler OnTransitionDungeonExterior;
        protected virtual void RaiseOnTransitionDungeonExteriorEvent()
        {
            TransitionEventArgs args = new TransitionEventArgs(TransitionType.ToDungeonExterior);
            if (OnTransitionDungeonExterior != null)
                OnTransitionDungeonExterior(args);
        }

        /// <summary>
        /// This event is raised when a transition has started being performed and <see cref="OnPreTransition"/>
        /// was fired but it couldn't be finished correctly due to an unexpected issue (i.e when 
        /// <c>"thisHouseHasNothingOfValue"</c> is also shown).
        /// </summary>
        public static event Action<TransitionEventArgs> OnFailedTransition;
        protected virtual void RaiseOnFailedTransition(TransitionType transitionType)
        {
            if (OnFailedTransition != null)
                OnFailedTransition(new TransitionEventArgs(transitionType));
        }

        // OnMovePlayerToDungeonStart
        public delegate void OnMovePlayerToDungeonStartEventHandler();
        public static event OnMovePlayerToDungeonStartEventHandler OnMovePlayerToDungeonStart;
        protected virtual void RaiseOnMovePlayerToDungeonStartEvent()
        {
            if (OnMovePlayerToDungeonStart != null)
                OnMovePlayerToDungeonStart();
        }

        // OnRespawnerComplete
        public delegate void OnRespawnerCompleteEventHandler();
        public static event OnRespawnerCompleteEventHandler OnRespawnerComplete;
        protected virtual void RaiseOnRespawnerCompleteEvent()
        {
            if (OnRespawnerComplete != null)
                OnRespawnerComplete();
        }

        #endregion
    }
}
