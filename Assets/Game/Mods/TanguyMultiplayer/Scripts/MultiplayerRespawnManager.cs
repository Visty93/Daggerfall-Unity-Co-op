using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Local-player-only multiplayer respawn helper.
    ///
    /// Full respawn clears ordinary classic diseases and poisons that could immediately
    /// kill the restored player again. It deliberately preserves vampirism/lycanthropy
    /// infections and curses, racial transformations, quest effects, and other bundles.
    /// </summary>
    public class MultiplayerRespawnManager : MonoBehaviour
    {
        public enum RespawnHealthMode
        {
            OneHP,
            TenPercent,
            HalfHealth,
            FullHealth,
        }

        private class RespawnMoveResult
        {
            public bool Moved;
        }

        [Header("Multiplayer Respawn")]
        public RespawnHealthMode HealthMode = RespawnHealthMode.FullHealth;

        [Tooltip("Default MP auto-respawn delay. SP death still uses vanilla PlayerDeath.TimeBeforeReset.")]
        public float RespawnDelaySeconds = 30f;

        [Tooltip("Disabled by default so death actually waits the configured MP delay.")]
        public bool AllowManualRespawnInput = true;

        [Tooltip("If manual input is enabled, ignore clicks before this many seconds have passed.")]
        public float ManualRespawnMinDelaySeconds = 2f;

        [Tooltip("Health percentage restored when another player revives this downed player before auto-respawn.")]
        public int ReviveHealthPercent = 30;

        public float exteriorSafeAnchorInterval = 0.50f;
        public float postRespawnExtraPositionSyncSeconds = 1.0f;
        public float transitionSettleSeconds = 0.35f;

        PlayerDeath playerDeath;
        PlayerEntity playerEntity;
        PlayerEnterExit playerEnterExit;
        PlayerGPS playerGPS;
        StreamingWorld streamingWorld;
        Transform playerTransform;

        bool respawnInProgress;
        Coroutine respawnCoroutine;
        Coroutine positionRefreshCoroutine;
        Coroutine locationRepositionCoroutine;
        Vector3 lastSafeExteriorPosition;
        bool hasLastSafeExteriorPosition;
        DFLocation lastKnownLocation;
        bool hasLastKnownLocation;
        float nextAnchorUpdateTime;
        int respawnSerial;

        public bool RespawnInProgress { get { return respawnInProgress; } }

        void Awake()
        {
            CacheReferences();
        }

        void OnEnable()
        {
            CacheReferences();
        }

        void Update()
        {
            if (!IsMultiplayerActive())
                return;

            CacheReferences();
            UpdateRespawnAnchors();
        }

        public static bool IsMultiplayerActive()
        {
            if (NetworkServer.active || NetworkClient.active)
                return true;

            try
            {
                return global::PlayerMultiplayer.state != 0;
            }
            catch
            {
                return false;
            }
        }

        public void NotifyLocalDeathStarted(string reason)
        {
            if (!IsMultiplayerActive())
                return;

            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.GetLocalPlayer();
                if (pm != null)
                    pm.ReportLocalLifeState(global::PlayerMultiplayer.MultiplayerLifeState.Downed, reason);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Failed to report local Downed state. reason=" + reason + " error=" + ex.Message);
            }
        }

        public void RequestRespawnNow(string reason)
        {
            if (!IsMultiplayerActive())
                return;

            if (respawnInProgress)
                return;

            if (!isActiveAndEnabled)
                return;

            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.GetLocalPlayer();
                if (pm != null)
                    pm.ReportLocalLifeState(global::PlayerMultiplayer.MultiplayerLifeState.Respawning, reason);
            }
            catch { }

            respawnCoroutine = StartCoroutine(RespawnRoutine(reason));
        }

        public bool ReviveLocalPlayerFromNetwork(int reviveHealthPercent, uint reviverNetId)
        {
            if (!IsMultiplayerActive())
                return false;

            CacheReferences();

            if (respawnCoroutine != null)
            {
                StopCoroutine(respawnCoroutine);
                respawnCoroutine = null;
            }

            respawnInProgress = false;
            int serial = ++respawnSerial;

            if (playerEntity == null)
            {
                Debug.LogWarning("[MPRespawn] Cannot revive: PlayerEntity is missing. reviver=" + reviverNetId);
                return false;
            }

            int maxHealth = Mathf.Max(1, playerEntity.MaxHealth);
            int percent = Mathf.Clamp(reviveHealthPercent, 1, 100);
            int restoreHealth = Mathf.Clamp(Mathf.CeilToInt(maxHealth * (percent / 100f)), 1, maxHealth);

            playerEntity.SetHealth(restoreHealth, true);

            if (playerDeath != null)
                playerDeath.ClearDeathAnimation();

            if (InputManager.Instance != null)
                InputManager.Instance.IsPaused = false;

            // Apply again after clearing death in case another system disturbed health during the same frame.
            playerEntity.SetHealth(restoreHealth, true);

            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.GetLocalPlayer();
                if (pm != null)
                {
                    pm.ReportLocalLifeState(global::PlayerMultiplayer.MultiplayerLifeState.Alive, "revived-by-" + reviverNetId);
                    pm.ForceSyncLocalPlayerHealthToMPNow("revived-by-" + reviverNetId);
                }
            }
            catch { }

            ForcePositionSync("mp-revive-" + reviverNetId);

            if (positionRefreshCoroutine != null)
                StopCoroutine(positionRefreshCoroutine);
            positionRefreshCoroutine = StartCoroutine(ForcePositionSyncForASecond(serial));

            Debug.Log("[MPRespawn] Local player revived by netId=" + reviverNetId + " health=" + restoreHealth + "/" + maxHealth + " percent=" + percent);
            return true;
        }

        private IEnumerator RespawnRoutine(string reason)
        {
            respawnInProgress = true;
            int serial = ++respawnSerial;

            // Let the death frame finish before moving the controller. This avoids fighting
            // the same-frame health/death event and gives fade/camera state a chance to settle.
            yield return null;

            CacheReferences();

            if (playerEntity == null)
            {
                Debug.LogWarning("[MPRespawn] Cannot respawn: PlayerEntity is missing.");
                respawnInProgress = false;
                respawnCoroutine = null;
                yield break;
            }

            bool diedInsideDungeon = playerEnterExit != null && playerEnterExit.IsPlayerInsideDungeon;
            bool diedInsideInterior = playerEnterExit != null && playerEnterExit.IsPlayerInside && !diedInsideDungeon;
            bool moved = false;
            DFLocation deathLocation;
            bool hasDeathLocation = TryGetBestDeathLocation(out deathLocation);

            // Remove repeatable lethal conditions before restoring health. Special disease
            // infections expose Diseases.None and are preserved by the selective manager API.
            // Teammate revive intentionally does not call this: a revive is not a free cure.
            ClearTemporaryLethalEffectsForFullRespawn(reason);

            int restoreHealth = GetRespawnHealth();

            // Restore health before moving/clearing death, otherwise the death state can retrigger.
            playerEntity.SetHealth(restoreHealth, true);

            if (diedInsideDungeon)
            {
                moved = TryRespawnInsideCurrentDungeon();
                if (!moved)
                    moved = TryRespawnOutsideCurrentDungeonFallback(hasDeathLocation ? deathLocation : default(DFLocation));
                if (moved)
                    yield return WaitForTransitionSettle();
            }

            if (!moved && diedInsideInterior && hasDeathLocation && IsTownOrCity(deathLocation))
            {
                // Interior death in a town/city: leave the death building first, then try to enter
                // a temple/cathedral. If no temple exists, try a tavern. If neither exists, use
                // the town's normal fast-travel/start marker.
                TryEnsureExterior("interior-town-respawn");
                yield return WaitForTransitionSettle();
                CacheReferences();

                moved = TryEnterPreferredTownRespawnBuilding(out string buildingLabel);
                if (moved)
                {
                    Debug.Log("[MPRespawn] Respawned inside town " + buildingLabel + ". reason=" + reason);
                    yield return WaitForTransitionSettle();
                    if (IsTavernRespawnLabel(buildingLabel))
                        yield return MoveRespawnedTavernPlayerToRestMarkerRoutine("interior-town-tavern");
                }
                else
                {
                    RespawnMoveResult fastTravelResult = new RespawnMoveResult();
                    yield return RespawnAtLocationFastTravelRoutine(deathLocation, "town-no-temple-or-tavern", fastTravelResult);
                    moved = fastTravelResult.Moved;
                    if (moved)
                        yield return WaitForTransitionSettle();
                }
            }

            if (!moved && !diedInsideInterior && hasDeathLocation && LocationHasDungeon(deathLocation))
            {
                // Exterior death near a dungeon location: prefer the location fast-travel/start-marker point, not the physical dungeon door.
                RespawnMoveResult fastTravelResult = new RespawnMoveResult();
                yield return RespawnAtLocationFastTravelRoutine(deathLocation, "exterior-dungeon-location-fast-travel", fastTravelResult);
                moved = fastTravelResult.Moved;
                if (moved)
                    yield return WaitForTransitionSettle();
            }

            if (!moved && !diedInsideInterior && hasDeathLocation && IsTownOrCity(deathLocation))
            {
                // City/town exterior death: same safe-town rule as interiors.
                moved = TryEnterPreferredTownRespawnBuilding(out string buildingLabel);
                if (moved)
                {
                    Debug.Log("[MPRespawn] Respawned inside town " + buildingLabel + " from exterior. reason=" + reason);
                    yield return WaitForTransitionSettle();
                    if (IsTavernRespawnLabel(buildingLabel))
                        yield return MoveRespawnedTavernPlayerToRestMarkerRoutine("exterior-town-tavern");
                }
                else
                {
                    RespawnMoveResult fastTravelResult = new RespawnMoveResult();
                    yield return RespawnAtLocationFastTravelRoutine(deathLocation, "town-exterior-fast-travel", fastTravelResult);
                    moved = fastTravelResult.Moved;
                    if (moved)
                        yield return WaitForTransitionSettle();
                }
            }

            if (!moved && hasDeathLocation)
            {
                // Generic current-location fallback: use the same kind of start marker fast travel uses.
                RespawnMoveResult fastTravelResult = new RespawnMoveResult();
                yield return RespawnAtLocationFastTravelRoutine(deathLocation, "generic-location-fast-travel", fastTravelResult);
                moved = fastTravelResult.Moved;
                if (moved)
                    yield return WaitForTransitionSettle();
            }

            if (!moved)
            {
                // Emergency wilderness fallback: do not use the exact same position if possible.
                moved = TryRespawnAtRandomOffsetFromLastSafeExterior();
            }

            if (!moved)
                Debug.LogWarning("[MPRespawn] No respawn movement path succeeded; restoring health in-place. reason=" + reason);

            CacheReferences();

            // Some transitions or collision correction can disturb the health value. Re-apply once.
            if (playerEntity != null)
                playerEntity.SetHealth(restoreHealth, true);

            if (playerDeath != null)
                playerDeath.ClearDeathAnimation();

            if (InputManager.Instance != null)
                InputManager.Instance.IsPaused = false;

            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.GetLocalPlayer();
                if (pm != null)
                {
                    pm.ReportLocalLifeState(global::PlayerMultiplayer.MultiplayerLifeState.Alive, "respawn-complete-" + reason);
                    pm.ForceSyncLocalPlayerHealthToMPNow("respawn-complete-" + reason);
                }
            }
            catch { }

            ForcePositionSync("mp-respawn-" + reason);

            if (positionRefreshCoroutine != null)
                StopCoroutine(positionRefreshCoroutine);
            positionRefreshCoroutine = StartCoroutine(ForcePositionSyncForASecond(serial));

            respawnInProgress = false;
            respawnCoroutine = null;
        }

        private void ClearTemporaryLethalEffectsForFullRespawn(string reason)
        {
            EntityEffectManager effectManager = null;
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                effectManager = GameManager.Instance.PlayerObject.GetComponent<EntityEffectManager>();

            if (effectManager == null)
            {
                Debug.LogWarning("[MPRespawn] Cannot clear ordinary disease/poison effects: PlayerEffectManager is missing.");
                return;
            }

            int removedClassicDiseases = effectManager.CureAllClassicDiseases(
                "mp-full-respawn-" + (reason ?? string.Empty));
            int removedPoisons = effectManager.PoisonCount;
            effectManager.CureAllPoisons();

            Debug.Log(
                "[MPRespawn] Cleared temporary lethal effects before full respawn. reason=" + reason +
                " classicDiseases=" + removedClassicDiseases +
                " poisons=" + removedPoisons +
                " specialInfectionsPreserved=true");
        }

        private IEnumerator WaitForTransitionSettle()
        {
            float wait = Mathf.Max(0.05f, transitionSettleSeconds);
            float endAt = Time.realtimeSinceStartup + wait;
            while (Time.realtimeSinceStartup < endAt)
                yield return null;
        }

        private int GetRespawnHealth()
        {
            int maxHealth = 1;
            if (playerEntity != null)
                maxHealth = Mathf.Max(1, playerEntity.MaxHealth);

            switch (HealthMode)
            {
                case RespawnHealthMode.OneHP:
                    return 1;
                case RespawnHealthMode.TenPercent:
                    return Mathf.Clamp(Mathf.CeilToInt(maxHealth * 0.10f), 1, maxHealth);
                case RespawnHealthMode.HalfHealth:
                    return Mathf.Clamp(Mathf.CeilToInt(maxHealth * 0.50f), 1, maxHealth);
                case RespawnHealthMode.FullHealth:
                default:
                    return maxHealth;
            }
        }

        private bool TryRespawnInsideCurrentDungeon()
        {
            CacheReferences();

            if (playerEnterExit == null || !playerEnterExit.IsPlayerInsideDungeon)
                return false;

            if (playerEnterExit.Dungeon == null)
                return false;

            try
            {
                playerEnterExit.MovePlayerToDungeonStart();
                Debug.Log("[MPRespawn] Respawned inside current live dungeon at StartMarker.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] MovePlayerToDungeonStart failed, will try exterior fallback. error=" + ex.Message);
                return false;
            }
        }

        private bool TryRespawnOutsideCurrentDungeonFallback(DFLocation fallbackLocation)
        {
            DaggerfallDungeon dungeon = null;
            if (playerEnterExit != null)
                dungeon = playerEnterExit.Dungeon;

            DFLocation dungeonLocation = fallbackLocation;
            bool hasLocation = dungeonLocation.Loaded;

            if (dungeon != null)
            {
                try
                {
                    if (dungeon.Summary.LocationData.Loaded)
                    {
                        dungeonLocation = dungeon.Summary.LocationData;
                        hasLocation = true;
                    }
                    else if (!string.IsNullOrEmpty(dungeon.Summary.RegionName) && !string.IsNullOrEmpty(dungeon.Summary.LocationName))
                    {
                        DFLocation loaded;
                        if (DaggerfallUnity.Instance.ContentReader.GetLocation(dungeon.Summary.RegionName, dungeon.Summary.LocationName, out loaded))
                        {
                            dungeonLocation = loaded;
                            hasLocation = true;
                        }
                    }
                }
                catch { }
            }

            // If the live dungeon is gone/invalid, leave to the dungeon location's normal
            // fast-travel/start-marker point rather than forcing the physical dungeon door.
            if (hasLocation && TryRespawnAtLocationFastTravel(dungeonLocation, "inside-dungeon-invalid-fallback-fast-travel"))
                return true;

            return TryEnsureExterior("inside-dungeon-no-location");
        }

        private bool TryRespawnAtDungeonExterior(DFLocation location, string reason)
        {
            // Kept as a compatibility helper for any older call-sites. For respawn, a dungeon
            // exterior should use the same coarse location fast-travel/start-marker placement
            // as the map travel point, not the exact physical dungeon entrance door.
            return TryRespawnAtLocationFastTravel(location, "dungeon-location-fast-travel-" + reason);
        }

        private bool TryRespawnAtLocationFastTravel(DFLocation location, string reason)
        {
            // Compatibility helper for old call-sites. The respawn routine should use
            // RespawnAtLocationFastTravelRoutine() so it can wait for the location object
            // before clearing death. This fallback only arms vanilla auto-reposition.
            if (!location.Loaded)
                return false;

            try
            {
                DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel((int)location.MapTableData.Longitude, location.MapTableData.Latitude);
                DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);

                if (streamingWorld != null)
                    streamingWorld.SetAutoReposition(StreamingWorld.RepositionMethods.RandomStartMarker, Vector3.zero);

                return TryRespawnPlayerWorldCoordinates(worldPos.X, worldPos.Y, false, true, reason);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Location fast-travel compatibility respawn failed. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private IEnumerator RespawnAtLocationFastTravelRoutine(DFLocation location, string reason, RespawnMoveResult result)
        {
            if (result != null)
                result.Moved = false;

            if (!location.Loaded)
                yield break;

            DFPosition mapPixel;
            try
            {
                mapPixel = MapsFile.LongitudeLatitudeToMapPixel((int)location.MapTableData.Longitude, location.MapTableData.Latitude);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Could not compute map pixel for location fast-travel respawn. reason=" + reason + " error=" + ex.Message);
                yield break;
            }

            // Important: do not call PlayerEnterExit.RespawnPlayer(... forceReposition=false) here.
            // In exterior/town fallback cases it can leave the player at local terrain 0/0 before
            // StreamingWorld has fully rebuilt the destination location. Instead, move the GPS/world
            // to the target map pixel only, then wait until terrain + location + markers are ready,
            // and finally apply the fast-travel placement ourselves.
            if (!TryTeleportStreamingWorldToMapPixel(mapPixel.X, mapPixel.Y, reason + "-map-pixel"))
                yield break;

            if (streamingWorld != null)
                streamingWorld.SetAutoReposition(StreamingWorld.RepositionMethods.None, Vector3.zero);

            bool placed = false;
            float timeoutAt = Time.realtimeSinceStartup + 8.0f;
            while (Time.realtimeSinceStartup < timeoutAt)
            {
                CacheReferences();

                if (playerEnterExit != null && playerEnterExit.IsPlayerInside)
                    break;

                if (streamingWorld != null && IsStreamingWorldAtMapPixel(mapPixel.X, mapPixel.Y))
                {
                    GameObject terrainObject = streamingWorld.GetTerrainFromPixel(mapPixel.X, mapPixel.Y);
                    DaggerfallLocation currentLocation = streamingWorld.GetPlayerLocationObject();

                    if (terrainObject != null && IsMatchingLocation(currentLocation, location))
                    {
                        if (TryPlacePlayerAtCurrentLocationFastTravel(currentLocation, mapPixel.X, mapPixel.Y, reason))
                        {
                            // RepositionPlayer can fail silently if terrain data is not ready yet.
                            // Validate the actual final position before accepting this respawn path.
                            yield return null;
                            CacheReferences();
                            if (!IsPlayerNearTerrainOrigin())
                            {
                                placed = true;
                                break;
                            }
                        }
                    }
                }

                yield return null;
            }

            if (placed)
            {
                Debug.Log("[MPRespawn] Completed validated location fast-travel respawn. reason=" + reason);
                if (result != null)
                    result.Moved = true;
            }
            else
            {
                Debug.LogWarning("[MPRespawn] Location fast-travel placement timed out or stayed at terrain origin; allowing later fallback. reason=" + reason);
            }
        }

        private bool TryTeleportStreamingWorldToMapPixel(int mapPixelX, int mapPixelY, string reason)
        {
            CacheReferences();
            if (streamingWorld == null)
                return false;

            try
            {
                streamingWorld.TeleportToCoordinates(mapPixelX, mapPixelY, StreamingWorld.RepositionMethods.None);
                streamingWorld.SetAutoReposition(StreamingWorld.RepositionMethods.None, Vector3.zero);
                Debug.Log("[MPRespawn] Teleported StreamingWorld to map pixel " + mapPixelX + "/" + mapPixelY + " without auto-origin reposition. reason=" + reason);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] StreamingWorld map-pixel teleport failed. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private bool TryPlacePlayerAtCurrentLocationFastTravel(DaggerfallLocation currentLocation, int mapPixelX, int mapPixelY, string reason)
        {
            if (currentLocation == null)
                return false;

            try
            {
                int width = currentLocation.Summary.BlockWidth;
                int height = currentLocation.Summary.BlockHeight;
                DFPosition tilePos = TerrainHelper.GetLocationTerrainTileOrigin(currentLocation.Summary.LegacyLocation);
                Vector3 origin = new Vector3(tilePos.X * RMBLayout.RMBTileSide, 2.0f * MeshReader.GlobalScale, tilePos.Y * RMBLayout.RMBTileSide);

                bool useNearestStartMarker =
                    currentLocation.Summary.LocationType == DFRegion.LocationTypes.TownCity ||
                    currentLocation.Summary.LocationType == DFRegion.LocationTypes.HomeYourShips;
                bool grounded = currentLocation.Summary.LocationType != DFRegion.LocationTypes.HomeYourShips;

                Vector3 targetPosition;
                if (TryChooseFastTravelPosition(currentLocation, origin, width, height, useNearestStartMarker, out targetPosition))
                {
                    if (TryInvokeStreamingWorldRepositionPlayer(mapPixelX, mapPixelY, targetPosition, grounded, reason))
                    {
                        Debug.Log("[MPRespawn] Applied manual fast-travel location placement. reason=" + reason + " target=" + targetPosition);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Manual fast-travel placement failed. reason=" + reason + " error=" + ex.Message);
            }

            return false;
        }

        private bool TryChooseFastTravelPosition(DaggerfallLocation dfLocation, Vector3 origin, int mapWidth, int mapHeight, bool useNearestStartMarker, out Vector3 targetPosition)
        {
            targetPosition = Vector3.zero;
            if (dfLocation == null)
                return false;

            float halfWidth = (float)mapWidth * 0.5f * RMBLayout.RMBSide;
            float halfHeight = (float)mapHeight * 0.5f * RMBLayout.RMBSide;
            Vector3 centre = origin + new Vector3(halfWidth, 0, halfHeight);
            float extraDistance = RMBLayout.RMBSide * 0.1f;

            // Pick a side like StreamingWorld.PositionPlayerToLocation() does, then optionally
            // snap to the nearest start marker for towns/cities. This reproduces the fast-travel
            // style placement without letting vanilla fall back to local terrain 0/0.
            int side = UnityEngine.Random.Range(0, 4);
            targetPosition = centre;

            PlayerMouseLook mouseLook = null;
            if (GameManager.Instance != null)
                mouseLook = GameManager.Instance.PlayerMouseLook;

            switch (side)
            {
                case 0: // North
                    targetPosition += new Vector3(0, 0, (halfHeight + extraDistance));
                    if (mouseLook != null) mouseLook.SetFacing(180, 0);
                    break;
                case 1: // South
                    targetPosition += new Vector3(0, 0, -(halfHeight + extraDistance));
                    if (mouseLook != null) mouseLook.SetFacing(0, 0);
                    break;
                case 2: // East
                    targetPosition += new Vector3((halfWidth + extraDistance), 0, 0);
                    if (mouseLook != null) mouseLook.SetFacing(270, 0);
                    break;
                case 3: // West
                default:
                    targetPosition += new Vector3(-(halfWidth + extraDistance), 0, 0);
                    if (mouseLook != null) mouseLook.SetFacing(90, 0);
                    break;
            }

            if (useNearestStartMarker && dfLocation.StartMarkers != null && dfLocation.StartMarkers.Length > 0)
            {
                float smallestDistance = float.MaxValue;
                int closestMarker = -1;
                GameObject[] startMarkers = dfLocation.StartMarkers;
                for (int i = 0; i < startMarkers.Length; i++)
                {
                    if (startMarkers[i] == null)
                        continue;

                    float distance = Vector3.Distance(targetPosition, startMarkers[i].transform.position);
                    if (distance < smallestDistance)
                    {
                        smallestDistance = distance;
                        closestMarker = i;
                    }
                }

                if (closestMarker != -1)
                    targetPosition = startMarkers[closestMarker].transform.position;
            }

            return true;
        }

        private bool TryInvokeStreamingWorldRepositionPlayer(int mapPixelX, int mapPixelY, Vector3 position, bool grounded, string reason)
        {
            if (streamingWorld == null)
                return false;

            try
            {
                MethodInfo method = typeof(StreamingWorld).GetMethod(
                    "RepositionPlayer",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(int), typeof(int), typeof(Vector3), typeof(bool) },
                    null);

                if (method == null)
                    return false;

                method.Invoke(streamingWorld, new object[] { mapPixelX, mapPixelY, position, grounded });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Could not invoke StreamingWorld.RepositionPlayer. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private bool TryInvokeStreamingWorldPositionPlayerToLocation(string reason)
        {
            if (streamingWorld == null)
                return false;

            try
            {
                MethodInfo method = typeof(StreamingWorld).GetMethod(
                    "PositionPlayerToLocation",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                if (method == null)
                    return false;

                method.Invoke(streamingWorld, null);
                Debug.Log("[MPRespawn] Applied StreamingWorld RandomStartMarker placement. reason=" + reason);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Could not apply StreamingWorld location placement. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private bool IsStreamingWorldAtMapPixel(int mapPixelX, int mapPixelY)
        {
            if (streamingWorld == null)
                return false;

            return streamingWorld.MapPixelX == mapPixelX && streamingWorld.MapPixelY == mapPixelY;
        }

        private bool IsMatchingLocation(DaggerfallLocation currentLocation, DFLocation expectedLocation)
        {
            if (currentLocation == null || !expectedLocation.Loaded)
                return false;

            try
            {
                // GetPlayerLocationObject() should already be the current location, but compare
                // names when possible so an old location object from the previous map pixel does
                // not prematurely trigger PositionPlayerToLocation().
                string currentName = currentLocation.Summary.LocationName;
                string expectedName = expectedLocation.Name;

                if (!string.IsNullOrEmpty(currentName) && !string.IsNullOrEmpty(expectedName))
                    return string.Equals(currentName, expectedName, StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            // If name fields are not available in this DFU version, accepting a non-null
            // player location object after the map pixel has switched is still safer than
            // applying placement immediately before locations have loaded.
            return true;
        }

        private bool IsPlayerNearTerrainOrigin()
        {
            CacheReferences();
            if (playerTransform == null)
                return true;

            Vector3 pos = playerTransform.position;
            return Mathf.Abs(pos.x) < 2f && Mathf.Abs(pos.z) < 2f;
        }

        private bool TryRespawnPlayerWorldCoordinates(int worldX, int worldZ, bool insideDungeon, bool forceReposition, string reason)
        {
            CacheReferences();
            if (playerEnterExit == null)
                return false;

            try
            {
                playerEnterExit.RespawnPlayer(worldX, worldZ, insideDungeon, forceReposition);
                Debug.Log("[MPRespawn] RespawnPlayer world=" + worldX + "/" + worldZ + " insideDungeon=" + insideDungeon + " forceReposition=" + forceReposition + " reason=" + reason);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] RespawnPlayer failed. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private bool TryEnsureExterior(string reason)
        {
            CacheReferences();

            if (playerEnterExit == null)
                return false;

            if (!playerEnterExit.IsPlayerInside)
                return true;

            try
            {
                playerEnterExit.TransitionExterior(true);
                Debug.Log("[MPRespawn] Transitioned to exterior. reason=" + reason);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] TransitionExterior failed. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private bool TryEnterPreferredTownRespawnBuilding(out string buildingLabel)
        {
            buildingLabel = string.Empty;
            CacheReferences();

            if (playerEnterExit == null || playerGPS == null || streamingWorld == null)
                return false;

            if (playerEnterExit.IsPlayerInside)
                return false;

            BuildingDirectory buildingDirectory = streamingWorld.GetCurrentBuildingDirectory();
            if (buildingDirectory == null || streamingWorld.currentPlayerLocationObject == null)
                return false;

            DaggerfallStaticDoors selectedCollection;
            StaticDoor selectedDoor;
            BuildingSummary selectedSummary;

            if (TryFindBuildingDoor(buildingDirectory, DFLocation.BuildingTypes.Temple, out selectedCollection, out selectedDoor, out selectedSummary))
            {
                buildingLabel = "temple/cathedral";
                return TryTransitionIntoBuilding(selectedCollection, selectedDoor, selectedSummary, buildingLabel);
            }

            if (TryFindBuildingDoor(buildingDirectory, DFLocation.BuildingTypes.Tavern, out selectedCollection, out selectedDoor, out selectedSummary))
            {
                buildingLabel = "tavern";
                return TryTransitionIntoBuilding(selectedCollection, selectedDoor, selectedSummary, buildingLabel);
            }

            return false;
        }

        private bool IsTavernRespawnLabel(string buildingLabel)
        {
            return !string.IsNullOrEmpty(buildingLabel) &&
                   buildingLabel.IndexOf("tavern", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private IEnumerator MoveRespawnedTavernPlayerToRestMarkerRoutine(string reason)
        {
            // Tavern room rentals do not teleport to a special hardcoded object. The tavern UI
            // stores a random InteriorMarkerTypes.Rest marker index as the allocated bed, then
            // the rest window moves PlayerMotor to that marker. For MP respawn, do the same
            // placement only, without creating any rented room or opening tavern UI.
            float timeoutAt = Time.realtimeSinceStartup + 3.0f;

            while (Time.realtimeSinceStartup < timeoutAt)
            {
                CacheReferences();

                if (playerEnterExit != null &&
                    playerEnterExit.IsPlayerInsideBuilding &&
                    playerEnterExit.Interior != null &&
                    (playerEnterExit.IsPlayerInsideTavern || playerEnterExit.BuildingType == DFLocation.BuildingTypes.Tavern))
                {
                    Vector3[] restMarkers = playerEnterExit.Interior.FindMarkers(DaggerfallInterior.InteriorMarkerTypes.Rest);
                    if (restMarkers != null && restMarkers.Length > 0)
                    {
                        int markerIndex = UnityEngine.Random.Range(0, restMarkers.Length);
                        Vector3 bedPosition = restMarkers[markerIndex];

                        if (TryMovePlayerTransform(bedPosition, "tavern-rest-marker-" + reason + " index=" + markerIndex))
                        {
                            try
                            {
                                PlayerMotor motor = GameManager.Instance != null ? GameManager.Instance.PlayerMotor : null;
                                if (motor != null)
                                    motor.FixStanding(0.4f, 0.4f);
                            }
                            catch { }

                            ForcePositionSync("mp-respawn-tavern-rest-marker");
                            Debug.Log("[MPRespawn] Moved tavern respawn to Rest marker index=" + markerIndex + " count=" + restMarkers.Length + " reason=" + reason);
                        }
                        else
                        {
                            Debug.LogWarning("[MPRespawn] Tavern Rest marker found but player move failed. reason=" + reason + " pos=" + bedPosition);
                        }

                        yield break;
                    }

                    Debug.LogWarning("[MPRespawn] Tavern interior loaded but has no Rest markers. Leaving player at tavern entrance. reason=" + reason);
                    yield break;
                }

                yield return null;
            }

            Debug.LogWarning("[MPRespawn] Timed out waiting for tavern interior before Rest marker placement. Leaving player at tavern entrance. reason=" + reason);
        }

        private bool TryFindBuildingDoor(
            BuildingDirectory buildingDirectory,
            DFLocation.BuildingTypes wantedType,
            out DaggerfallStaticDoors selectedCollection,
            out StaticDoor selectedDoor,
            out BuildingSummary selectedSummary)
        {
            selectedCollection = null;
            selectedDoor = default(StaticDoor);
            selectedSummary = default(BuildingSummary);

            if (streamingWorld == null || streamingWorld.currentPlayerLocationObject == null)
                return false;

            DaggerfallStaticDoors[] doorCollections = streamingWorld.currentPlayerLocationObject.GetComponentsInChildren<DaggerfallStaticDoors>(true);
            if (doorCollections == null || doorCollections.Length == 0)
                return false;

            float bestDistance = float.MaxValue;
            Vector3 currentPos = playerTransform != null ? playerTransform.position : Vector3.zero;

            for (int c = 0; c < doorCollections.Length; c++)
            {
                DaggerfallStaticDoors collection = doorCollections[c];
                if (collection == null || collection.Doors == null)
                    continue;

                for (int i = 0; i < collection.Doors.Length; i++)
                {
                    StaticDoor door = collection.Doors[i];
                    if (door.doorType != DoorTypes.Building)
                        continue;

                    BuildingSummary summary;
                    if (!buildingDirectory.GetBuildingSummary(door.buildingKey, out summary))
                        continue;

                    if (summary.BuildingType != wantedType)
                        continue;

                    Vector3 doorPosition = collection.GetDoorPosition(i);
                    float distance = Vector3.SqrMagnitude(doorPosition - currentPos);
                    if (selectedCollection == null || distance < bestDistance)
                    {
                        bestDistance = distance;
                        selectedCollection = collection;
                        selectedDoor = door;
                        selectedSummary = summary;
                    }
                }
            }

            return selectedCollection != null;
        }

        private bool TryTransitionIntoBuilding(DaggerfallStaticDoors collection, StaticDoor door, BuildingSummary summary, string label)
        {
            CacheReferences();
            if (collection == null || playerEnterExit == null || playerGPS == null)
                return false;

            try
            {
                playerGPS.DiscoverBuilding(door.buildingKey);

                PlayerGPS.DiscoveredBuilding discovery;
                if (playerGPS.GetDiscoveredBuilding(door.buildingKey, out discovery))
                    playerEnterExit.BuildingDiscoveryData = discovery;

                playerEnterExit.IsPlayerInsideOpenShop = false;
                playerEnterExit.IsPlayerInsideTavern = summary.BuildingType == DFLocation.BuildingTypes.Tavern;
                playerEnterExit.IsPlayerInsideResidence = false;

                playerEnterExit.TransitionInterior(collection.transform, door, true, false);
                Debug.Log("[MPRespawn] Transitioned into " + label + " buildingKey=" + door.buildingKey);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] Failed to transition into " + label + ". error=" + ex.Message);
                return false;
            }
        }

        private bool TryRespawnAtRandomOffsetFromLastSafeExterior()
        {
            if (!hasLastSafeExteriorPosition)
                return false;

            Vector3 candidate;
            if (TryFindRandomGroundedOffset(lastSafeExteriorPosition, out candidate))
                return TryMovePlayerTransform(candidate, "random-offset-last-safe-exterior");

            return TryMovePlayerTransform(lastSafeExteriorPosition, "last-safe-exterior-emergency");
        }

        private bool TryFindRandomGroundedOffset(Vector3 origin, out Vector3 result)
        {
            result = origin;
            CacheReferences();

            float controllerHeight = 1.8f;
            float controllerRadius = 0.4f;
            if (GameManager.Instance != null && GameManager.Instance.PlayerController != null)
            {
                controllerHeight = Mathf.Max(0.5f, GameManager.Instance.PlayerController.height);
                controllerRadius = Mathf.Max(0.1f, GameManager.Instance.PlayerController.radius);
            }

            for (int i = 0; i < 24; i++)
            {
                Vector2 circle = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(8f, 24f);
                Vector3 probe = origin + new Vector3(circle.x, 20f, circle.y);

                RaycastHit hit;
                if (!Physics.Raycast(probe, Vector3.down, out hit, 80f))
                    continue;

                Vector3 standing = hit.point + Vector3.up * ((controllerHeight * 0.5f) + 0.15f);
                Vector3 capsuleBottom = standing + Vector3.up * controllerRadius;
                Vector3 capsuleTop = standing + Vector3.up * (controllerHeight - controllerRadius);

                if (Physics.CheckCapsule(capsuleBottom, capsuleTop, controllerRadius, ~0, QueryTriggerInteraction.Ignore))
                    continue;

                result = standing;
                return true;
            }

            return false;
        }

        private bool TryMovePlayerTransform(Vector3 position, string reason)
        {
            CacheReferences();
            if (playerTransform == null)
                return false;

            CharacterController controller = playerTransform.GetComponent<CharacterController>();
            bool controllerWasEnabled = controller != null && controller.enabled;

            try
            {
                if (controller != null)
                    controller.enabled = false;

                playerTransform.position = position;

                if (controller != null)
                    controller.enabled = controllerWasEnabled;

                try
                {
                    if (GameManager.Instance != null && GameManager.Instance.AcrobatMotor != null)
                        GameManager.Instance.AcrobatMotor.ClearFallingDamage();
                }
                catch { }

                Debug.Log("[MPRespawn] Moved PlayerAdvanced transform. reason=" + reason + " pos=" + position);
                return true;
            }
            catch (Exception ex)
            {
                if (controller != null)
                    controller.enabled = controllerWasEnabled;

                Debug.LogWarning("[MPRespawn] Failed to move PlayerAdvanced transform. reason=" + reason + " error=" + ex.Message);
                return false;
            }
        }

        private void UpdateRespawnAnchors()
        {
            if (Time.realtimeSinceStartup < nextAnchorUpdateTime)
                return;

            nextAnchorUpdateTime = Time.realtimeSinceStartup + Mathf.Max(0.05f, exteriorSafeAnchorInterval);

            if (respawnInProgress)
                return;

            if (playerDeath != null && playerDeath.DeathInProgress)
                return;

            if (playerEntity == null || playerEntity.CurrentHealth <= 0)
                return;

            if (playerGPS != null && playerGPS.CurrentLocation.Loaded)
            {
                lastKnownLocation = playerGPS.CurrentLocation;
                hasLastKnownLocation = true;
            }

            if (playerEnterExit != null && playerEnterExit.IsPlayerInside)
                return;

            if (playerTransform == null)
                return;

            lastSafeExteriorPosition = playerTransform.position;
            hasLastSafeExteriorPosition = true;
        }

        private bool TryGetBestDeathLocation(out DFLocation location)
        {
            CacheReferences();

            if (playerGPS != null && playerGPS.CurrentLocation.Loaded)
            {
                location = playerGPS.CurrentLocation;
                return true;
            }

            if (hasLastKnownLocation && lastKnownLocation.Loaded)
            {
                location = lastKnownLocation;
                return true;
            }

            if (playerEnterExit != null && playerEnterExit.Dungeon != null)
            {
                try
                {
                    if (playerEnterExit.Dungeon.Summary.LocationData.Loaded)
                    {
                        location = playerEnterExit.Dungeon.Summary.LocationData;
                        return true;
                    }
                }
                catch { }
            }

            location = default(DFLocation);
            return false;
        }

        private bool LocationHasDungeon(DFLocation location)
        {
            try
            {
                return location.Loaded && location.HasDungeon;
            }
            catch
            {
                return false;
            }
        }

        private bool IsTownOrCity(DFLocation location)
        {
            if (!location.Loaded)
                return false;

            DFRegion.LocationTypes type = location.MapTableData.LocationType;
            return type == DFRegion.LocationTypes.TownCity ||
                   type == DFRegion.LocationTypes.TownHamlet ||
                   type == DFRegion.LocationTypes.TownVillage;
        }

        private IEnumerator ForcePositionSyncForASecond(int serial)
        {
            float endAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, postRespawnExtraPositionSyncSeconds);

            while (Time.realtimeSinceStartup < endAt && serial == respawnSerial)
            {
                ForcePositionSync("mp-respawn-refresh");
                yield return new WaitForSecondsRealtime(0.20f);
            }

            if (serial == respawnSerial)
                positionRefreshCoroutine = null;
        }

        private void ForcePositionSync(string reason)
        {
            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.GetLocalPlayer();
                if (pm != null)
                {
                    global::PositionMultiplayer positionMultiplayer = pm.GetComponent<global::PositionMultiplayer>();
                    if (positionMultiplayer != null)
                        positionMultiplayer.ForceSendCurrentCoordinatesNow(reason);

                    if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                    {
                        pm.transform.position = GameManager.Instance.PlayerObject.transform.position;
                        pm.transform.rotation = GameManager.Instance.PlayerObject.transform.rotation;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MPRespawn] ForcePositionSync failed: " + ex.Message);
            }
        }

        private void CacheReferences()
        {
            if (playerDeath == null)
                playerDeath = GetComponent<PlayerDeath>();

            if (GameManager.Instance != null)
            {
                playerEntity = GameManager.Instance.PlayerEntity;
                playerEnterExit = GameManager.Instance.PlayerEnterExit;
                playerGPS = GameManager.Instance.PlayerGPS;
                streamingWorld = GameManager.Instance.StreamingWorld;

                if (GameManager.Instance.PlayerObject != null)
                    playerTransform = GameManager.Instance.PlayerObject.transform;
            }
        }
    }
}
