using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallConnect;
using DaggerfallWorkshop.Game;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.Serialization;

public class PositionMultiplayer : NetworkBehaviour
{
    public float hideDistance = 500;
    public float sendDistance = 50;

    // How often the local player checks PlayerGPS for normal movement/world-coordinate changes.
    // The old script only checked every 3.12s, which allowed stale DF X/Z after loading a save.
    public float coordinateCheckInterval = 0.1f;

    // Exterior terrain/map-pixel frame width in Unity units.
    // Daggerfall world map pixels are 32768 DF units wide and this project uses
    // 40 DF units per 1 Unity unit, so one terrain frame is 819.2 Unity units.
    // This is only used to wrap remote presentation into the local terrain frame;
    // it does not replace NetworkTransform smoothing or rewrite DF world coords.
    private const float ExteriorTerrainFrameUnity = 819.2f;
    private const float ExteriorTerrainHalfFrameUnity = ExteriorTerrainFrameUnity * 0.5f;
    private const float SeamCorrectionEpsilon = 0.01f;
    // Remote player seam wrapping must obey the same DF-world visibility range as the sprite cull.
    // Do not use a huge value here, or players in distant towns that happen to share similar
    // local Unity coordinates can be wrapped into view.
    private const float RemotePlayerSeamCorrectionWorldMargin = 25f;

    public GameObject visual;
    StreamingWorld world;
    Transform playerTransform;
    PlayerGPS gps;

    Coroutine loadCoordinateRefreshCoroutine;

    // Saved-dungeon conversion can complete while PlayerGPS still intentionally retains
    // the save's exterior world context. While this override is active, the normal
    // PositionMultiplayer publisher uses the host-authored dungeon entrance anchor instead
    // of alternating between that anchor and stale PlayerGPS coordinates.
    private bool networkDungeonCoordinateOverrideActive = false;
    private int networkDungeonCoordinateOverrideX = 0;
    private int networkDungeonCoordinateOverrideZ = 0;

    [SyncVar]
    public int x = 0, z = 0;

    public enum PartyLocationState
    {
        Unknown = 0,
        Wilderness = 1,
        ExteriorLocation = 2,
        BuildingInterior = 3,
        DungeonInterior = 4,
    }

    [Header("Party HUD Location")]
    [Tooltip("Independent of OptionsMultiplayer.sendLocation. Disabling this hides only the current-location line on the party HUD; it does not change shared map discovery.")]
    public bool shareCurrentLocationWithParty = true;

    [Tooltip("How often the local player checks whether the party-HUD location snapshot changed. A network update is sent only when the snapshot is different.")]
    public float partyLocationCheckInterval = 0.75f;

    // Current location display for the light party HUD. This is deliberately separate
    // from SendLocation()/cmdSendLocation()/rpcSendLocation(), which remain the existing
    // shared map-discovery feature.
    [SyncVar] public bool PartyLocationShared = false;
    [SyncVar] public string PartyRegionName = string.Empty;
    [SyncVar] public string PartyLocationName = string.Empty;
    [SyncVar] public PartyLocationState PartyCurrentLocationState = PartyLocationState.Unknown;

    // Numeric identity of the town currently containing this player. This is used only
    // for mandatory NPC building-map reveals and is deliberately independent of both
    // OptionsMultiplayer.sendLocation and shareCurrentLocationWithParty.
    // -1 means the player is not currently in a town exterior/building interior.
    [SyncVar] public int NpcBuildingRevealMapID = -1;

    // Exact identity of the building interior occupied by this player. Building keys are
    // only meaningful together with NpcBuildingRevealMapID. Zero means unknown/not inside
    // a building and must never be treated as a valid same-building match.
    [SyncVar] public int PartyBuildingKey = 0;

    // Safe exterior doorway anchor for party travel while this player is inside a building.
    // Keep this separate from x/z: normal MP visibility and enemy-distance logic must continue
    // using the existing player world coordinate path.
    [SyncVar] public bool PartyBuildingEntranceAnchorValid = false;
    [SyncVar] public int PartyBuildingEntranceAnchorX = 0;
    [SyncVar] public int PartyBuildingEntranceAnchorZ = 0;

    // Exact exterior Unity height for party rendezvous travel. Unlike x/z, this is not
    // a Daggerfall world coordinate: exterior players share the same Unity Y elevation,
    // while floating-origin shifts affect the horizontal frame. Never publish interior
    // or dungeon Y values because those spaces use artificial vertical offsets.
    [SyncVar] public bool PartyExteriorArrivalYValid = false;
    [SyncVar] public float PartyExteriorArrivalY = 0f;

    // Stable identity of the live network dungeon occupied by this player.
    // Party travel already uses x/z as the exact exterior dungeon entrance anchor.
    // This ID is only used after exterior fast travel to ensure the traveler enters
    // the same generated dungeon instance before snapping near the target player.
    [SyncVar] public string PartyDungeonInstanceId = string.Empty;

    private bool lastPartyLocationSnapshotInitialized = false;
    private bool lastPartyLocationShared = false;
    private string lastPartyRegionName = string.Empty;
    private string lastPartyLocationName = string.Empty;
    private PartyLocationState lastPartyLocationState = PartyLocationState.Unknown;
    private int lastNpcBuildingRevealMapID = -1;
    private int lastPartyBuildingKey = 0;
    private bool lastPartyBuildingEntranceAnchorValid = false;
    private int lastPartyBuildingEntranceAnchorX = 0;
    private int lastPartyBuildingEntranceAnchorZ = 0;
    private bool lastPartyExteriorArrivalYValid = false;
    private float lastPartyExteriorArrivalY = 0f;
    private string lastPartyDungeonInstanceId = string.Empty;

    void OnEnable()
    {
        SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
    }

    void OnDisable()
    {
        SaveLoadManager.OnLoad -= SaveLoadManager_OnLoad;
    }

    void Start()
    {
        // Raw Unity distance is not reliable across Daggerfall exterior terrain seams.
        // If a DistanceInterestManagement component was placed on the player proxy,
        // remove it so the host/client does not lose observers just because one side
        // is in the neighboring 0<->819.2 terrain frame.
        var dim = GetComponent<DistanceInterestManagement>();
        if (dim != null)
            Destroy(dim);

        if (isLocalPlayer)
        {
            CacheLocalReferences();
            StartCoroutine(SetCoordinates());
            StartCoroutine(SendLocation());          // Existing shared discovery path.
            StartCoroutine(SendPartyLocation());     // Independent current-location HUD path.
        }
        else
            StartCoroutine(Check());
    }

    void FixedUpdate()
    {
        // Only the local PlayerMultiplayer proxy is glued to PlayerAdvanced.
        // Remote proxies must remain driven by NetworkTransform plus the seam-frame
        // correction in LateUpdate(). Never let a remote proxy accidentally cache
        // GameManager.Instance.PlayerObject and snap onto the local player.
        if (isLocalPlayer && playerTransform != null)
        {
            transform.position = playerTransform.position;
            transform.rotation = playerTransform.rotation;
        }
    }

    void LateUpdate()
    {
        // Remote player seam fix: keep NetworkTransform as the source of movement,
        // but present the remote root in the nearest exterior terrain frame to the
        // local player. This avoids the 0 <-> 819.2 seam making a nearby player
        // appear one whole terrain frame away.
        //
        // Do not use DF x/z to continuously place the player, as that destroys
        // smoothing. We only add/subtract exact whole terrain-frame offsets from
        // the already-smoothed NetworkTransform position.
        if (!isLocalPlayer)
            WrapRemotePlayerIntoLocalTerrainFrame();
    }

    private void WrapRemotePlayerIntoLocalTerrainFrame()
    {
        if (!NetworkClient.active)
            return;

        // This must also run on the host/server. The host's local scene uses the host
        // terrain frame, while a remote client's NetworkTransform position is expressed
        // in the remote client's terrain frame. If the host does not wrap the remote
        // player root into the host frame, server-side enemy sensing/attack range sees
        // the client hundreds of Unity units away at the 0 <-> 819.2 seam.
        //
        // EnemyWorldPosition_ownerpublish_v9 prevents remote-client-owned enemies from
        // rebaking logical DF coordinates from this host-side wrapped/interpolated
        // player transform, so allowing host wrapping here should not reintroduce the
        // worldX/worldZ corruption seen earlier.

        if (GameManager.Instance == null ||
            GameManager.Instance.PlayerObject == null ||
            GameManager.Instance.PlayerGPS == null)
            return;

        // This is an exterior-only seam fix. Never wrap remote proxies while either
        // side is in an interior/dungeon coordinate space. Network dungeons can share
        // the same 0 <-> 819.2-looking X/Z values as exterior terrain seams, but their
        // Y slots and lifecycle logic depend on the raw dungeon-local network position.
        PlayerEnterExit localEnterExit = GameManager.Instance.PlayerEnterExit;
        if (localEnterExit != null && localEnterExit.IsPlayerInside)
            return;

        if (PartyCurrentLocationState == PartyLocationState.BuildingInterior ||
            PartyCurrentLocationState == PartyLocationState.DungeonInterior)
            return;

        // During dungeon-entry handoff the party-location SyncVar can lag behind the
        // NetworkTransform by a frame or two. Treat the large negative MP dungeon Y
        // slots as non-exterior too, so a stationary client near dungeon walls is not
        // rewrapped into an exterior terrain frame and considered gone from the dungeon.
        if (transform.position.y < -100f)
            return;

        Vector3 localPlayerPos = GameManager.Instance.PlayerObject.transform.position;
        Vector3 pos = transform.position;

        float rawDx = pos.x - localPlayerPos.x;
        float rawDz = pos.z - localPlayerPos.z;
        float wrappedDx = WrapDeltaToNearestTerrainFrame(rawDx);
        float wrappedDz = WrapDeltaToNearestTerrainFrame(rawDz);

        float correctionX = wrappedDx - rawDx;
        float correctionZ = wrappedDz - rawDz;

        if (Mathf.Abs(correctionX) <= SeamCorrectionEpsilon &&
            Mathf.Abs(correctionZ) <= SeamCorrectionEpsilon)
            return;

        transform.position = new Vector3(pos.x + correctionX, pos.y, pos.z + correctionZ);
    }

    private float WrapDeltaToNearestTerrainFrame(float delta)
    {
        if (Mathf.Abs(delta) <= ExteriorTerrainHalfFrameUnity)
            return delta;

        return delta - Mathf.Round(delta / ExteriorTerrainFrameUnity) * ExteriorTerrainFrameUnity;
    }

    private bool IsRemoteWithinDfVisibilityDistance(float margin = 0f)
    {
        // Saved-dungeon conversion can bind the live network dungeon before raw
        // PlayerGPS has settled to its exact entrance coordinate. Compare against
        // the same dungeon-aware local anchor used by coordinate publication, or
        // only the remote visual can be hidden while its collider remains present.
        int localWorldX;
        int localWorldZ;
        if (!TryGetCurrentGpsCoordinates(out localWorldX, out localWorldZ))
            return true;

        // This is the original player visibility rule, still based on Daggerfall
        // world coordinates. Unity transform distance is deliberately not used here.
        float worldDistance = Vector2.Distance(
            new Vector2(x, z),
            new Vector2(localWorldX, localWorldZ));

        return worldDistance < hideDistance + Mathf.Max(0f, margin);
    }

    // Read-only visibility source for separately-rendered remote objects such as the
    // multiplayer downed corpse. This deliberately does not enable or disable the normal
    // player visual, so it cannot interrupt SpriteMultiplayer's animation lifecycle.
    public bool ShouldShowRemoteVisualForCurrentCoordinates()
    {
        if (isLocalPlayer)
            return true;

        return IsRemoteWithinDfVisibilityDistance();
    }

    private void SaveLoadManager_OnLoad(SaveData_v1 saveData)
    {
        if (!isLocalPlayer)
            return;

        if (loadCoordinateRefreshCoroutine != null)
            StopCoroutine(loadCoordinateRefreshCoroutine);

        // Force only the independent party-location snapshot to republish after load.
        // The original discovery coroutine below is left unchanged.
        lastPartyLocationSnapshotInitialized = false;
        loadCoordinateRefreshCoroutine = StartCoroutine(ForceSendCoordinatesAfterLoad());
    }

    private void CacheLocalReferences()
    {
        if (world == null)
        {
            GameObject streamingWorld = GameObject.Find("StreamingWorld");
            if (streamingWorld != null)
                world = streamingWorld.GetComponent<StreamingWorld>();
        }

        if (GameManager.Instance != null)
        {
            if (isLocalPlayer && GameManager.Instance.PlayerObject != null)
                playerTransform = GameManager.Instance.PlayerObject.transform;

            if (GameManager.Instance.PlayerGPS != null)
                gps = GameManager.Instance.PlayerGPS;
        }
    }

    private bool TryGetCurrentGpsCoordinates(out int gpsX, out int gpsZ)
    {
        CacheLocalReferences();

        gpsX = 0;
        gpsZ = 0;

        if (networkDungeonCoordinateOverrideActive)
        {
            gpsX = networkDungeonCoordinateOverrideX;
            gpsZ = networkDungeonCoordinateOverrideZ;
            return true;
        }

        if (gps == null)
            return false;

        // MP network dungeon rule:
        // While inside a network dungeon, publish the dungeon's stable DF anchor, not raw
        // PlayerGPS.WorldX/Z. TeleportPc can put PlayerGPS on the dungeon map-pixel/border
        // coordinate, while a later exit/re-entry can change it to the actual entrance.
        // EnemyWorldPosition uses the dungeon anchor too, so players and dungeon enemies
        // must compare against the same stable coordinate.
        bool multiplayerActive = NetworkServer.active || NetworkClient.active;
        if (multiplayerActive &&
            GameManager.Instance != null &&
            GameManager.Instance.PlayerEnterExit != null &&
            GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
        {
            DaggerfallDungeon currentDungeon = GameManager.Instance.PlayerEnterExit.Dungeon;
            if (currentDungeon != null && currentDungeon.HasDungeonWorldAnchor)
            {
                gpsX = currentDungeon.DungeonAnchorWorldX;
                gpsZ = currentDungeon.DungeonAnchorWorldZ;
                return true;
            }
        }

        gpsX = gps.WorldX;
        gpsZ = gps.WorldZ;
        return true;
    }

    public void SetNetworkDungeonCoordinateOverride(
        bool active,
        int dungeonWorldX,
        int dungeonWorldZ,
        string reason = "saved-dungeon")
    {
        if (!isLocalPlayer)
            return;

        bool changed =
            networkDungeonCoordinateOverrideActive != active ||
            (active &&
             (networkDungeonCoordinateOverrideX != dungeonWorldX ||
              networkDungeonCoordinateOverrideZ != dungeonWorldZ));

        networkDungeonCoordinateOverrideActive = active;
        networkDungeonCoordinateOverrideX = active ? dungeonWorldX : 0;
        networkDungeonCoordinateOverrideZ = active ? dungeonWorldZ : 0;

        if (!changed)
            return;

        if (active)
            SendCurrentCoordinates(true, reason + "-override-enabled");
        else
            SendCurrentCoordinates(true, reason + "-override-released");

        Debug.Log($"[PositionMultiplayer][DungeonOverride] active={active} x={networkDungeonCoordinateOverrideX} z={networkDungeonCoordinateOverrideZ} reason={reason}");
    }

    public void ForceSendCurrentCoordinatesNow(string reason = "manual")
    {
        SendCurrentCoordinates(true, reason);
    }

    private bool SendCurrentCoordinates(bool force, string reason)
    {
        int gpsX, gpsZ;
        if (!TryGetCurrentGpsCoordinates(out gpsX, out gpsZ))
            return false;

        // Keep the PlayerMultiplayer/PositionMultiplayer transform glued to PlayerAdvanced immediately.
        // This avoids one FixedUpdate of stale Unity position after load/teleport.
        if (isLocalPlayer && playerTransform != null)
        {
            transform.position = playerTransform.position;
            transform.rotation = playerTransform.rotation;
        }

        float dist = Vector2.Distance(new Vector2(x, z), new Vector2(gpsX, gpsZ));
        if (!force && dist <= sendDistance)
            return false;

        // Update local copy immediately too. On pure clients the server will echo the same SyncVar
        // back shortly after the command arrives, but this avoids local stale reads in the meantime.
        x = gpsX;
        z = gpsZ;

        // Pure client: send to host immediately. Mirror command ordering should make this arrive
        // before later dungeon-entry commands issued by the same client after load.
        if (NetworkClient.active && !isServer)
            cmdSendCoordinates(gpsX, gpsZ);
        else if (!NetworkClient.active && !isServer)
        {
            // Defensive fallback for odd non-network cases.
            x = gpsX;
            z = gpsZ;
        }

        if (reason != "poll")
            Debug.Log($"[PositionMultiplayer] Sent coordinates reason={reason} x={gpsX} z={gpsZ} force={force} dist={dist:F1} isServer={isServer} isClient={NetworkClient.active}");

        return true;
    }

    private IEnumerator ForceSendCoordinatesAfterLoad()
    {
        // SaveLoadManager.OnLoad fires after player position has been restored. Send immediately,
        // then repeat a few times over the next half second in case PlayerGPS/StreamingWorld settles
        // across one or two frames.
        yield return null;
        SendCurrentCoordinates(true, "load-immediate");

        yield return new WaitForEndOfFrame();
        SendCurrentCoordinates(true, "load-end-of-frame");

        yield return new WaitForSeconds(0.1f);
        SendCurrentCoordinates(true, "load-0.1s");

        yield return new WaitForSeconds(0.25f);
        SendCurrentCoordinates(true, "load-0.35s");

        yield return new WaitForSeconds(0.5f);
        SendCurrentCoordinates(true, "load-0.85s");

        loadCoordinateRefreshCoroutine = null;
    }

    IEnumerator Check()
    {
        yield return new WaitForSeconds(2f);
        gps = GameManager.Instance.PlayerGPS;
        while (true)
        {
            // Keep the original essential rule: remote player visibility is based on
            // Daggerfall world X/Z distance, not Unity transform distance.
            // The seam-wrap code above is only allowed to run when this same DF-world
            // visibility test says the remote player is actually nearby.
            if (visual != null)
                visual.SetActive(IsRemoteWithinDfVisibilityDistance());

            yield return new WaitForSeconds(3.15f);
        }
    }

    IEnumerator SetCoordinates()
    {
        // Do not wait a full second before the first coordinate send. This matters after load,
        // teleport, and entering/leaving MP while the player may immediately request a dungeon.
        yield return null;
        SendCurrentCoordinates(true, "startup");

        while (true)
        {
            SendCurrentCoordinates(false, "poll");
            yield return new WaitForSeconds(Mathf.Max(0.05f, coordinateCheckInterval));
        }
    }

    IEnumerator SendLocation()
    {
        yield return new WaitForSeconds(1.5f);
        CacheLocalReferences();

        if (gps == null)
            yield break;

        DFLocation location = gps.CurrentLocation;
        while (true)
        {
            if (OptionsMultiplayer.sendLocation && location.Name != gps.CurrentLocation.Name)
            {
                location = gps.CurrentLocation;
                cmdSendLocation(location.RegionName, location.Name);
            }
            yield return new WaitForSeconds(5.56f);
        }
    }

    [Command]
    void cmdSendLocation(string region, string location)
    {
        rpcSendLocation(region, location);
    }

    [ClientRpc]
    void rpcSendLocation(string region, string location)
    {
        if (!isLocalPlayer)
        {
            try
            {
                if (DaggerfallUnity.Instance.ContentReader.GetLocation(region, location, out DFLocation loc))
                {
                    gps.DiscoverLocation(region, location);
                    Debug.Log($"[Multiplayer] Discovered location {region}/{location}.");
                }
                else
                {
                    Debug.LogWarning($"[Multiplayer] Location not found: {region}/{location}. Skipping DiscoverLocation.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Multiplayer] Exception in DiscoverLocation: {region}/{location} - {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Independent party-HUD current-location sync.
    //
    // Do not merge this into SendLocation(). That existing method is specifically
    // for sharing map discoveries with the other players.
    // -------------------------------------------------------------------------

    IEnumerator SendPartyLocation()
    {
        yield return new WaitForSecondsRealtime(0.75f);
        CacheLocalReferences();

        while (true)
        {
            bool loadInProgress = false;
            try
            {
                loadInProgress = SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
            }
            catch { }

            if (!loadInProgress)
                SendPartyLocationSnapshotIfChanged();

            yield return new WaitForSecondsRealtime(Mathf.Max(0.25f, partyLocationCheckInterval));
        }
    }

    private void SendPartyLocationSnapshotIfChanged()
    {
        bool shared;
        string regionName;
        string locationName;
        PartyLocationState state;
        int npcBuildingRevealMapID;
        int buildingKey;
        bool buildingEntranceAnchorValid;
        int buildingEntranceAnchorX;
        int buildingEntranceAnchorZ;
        bool exteriorArrivalYValid;
        float exteriorArrivalY;
        string dungeonInstanceId;

        if (!TryBuildPartyLocationSnapshot(
            out shared,
            out regionName,
            out locationName,
            out state,
            out npcBuildingRevealMapID,
            out buildingKey,
            out buildingEntranceAnchorValid,
            out buildingEntranceAnchorX,
            out buildingEntranceAnchorZ,
            out exteriorArrivalYValid,
            out exteriorArrivalY,
            out dungeonInstanceId))
            return;

        bool changed = !lastPartyLocationSnapshotInitialized ||
            shared != lastPartyLocationShared ||
            state != lastPartyLocationState ||
            npcBuildingRevealMapID != lastNpcBuildingRevealMapID ||
            buildingKey != lastPartyBuildingKey ||
            buildingEntranceAnchorValid != lastPartyBuildingEntranceAnchorValid ||
            buildingEntranceAnchorX != lastPartyBuildingEntranceAnchorX ||
            buildingEntranceAnchorZ != lastPartyBuildingEntranceAnchorZ ||
            exteriorArrivalYValid != lastPartyExteriorArrivalYValid ||
            (exteriorArrivalYValid && Mathf.Abs(exteriorArrivalY - lastPartyExteriorArrivalY) > 0.05f) ||
            !string.Equals(dungeonInstanceId, lastPartyDungeonInstanceId, System.StringComparison.Ordinal) ||
            !string.Equals(regionName, lastPartyRegionName, System.StringComparison.Ordinal) ||
            !string.Equals(locationName, lastPartyLocationName, System.StringComparison.Ordinal);

        if (!changed)
            return;

        lastPartyLocationSnapshotInitialized = true;
        lastPartyLocationShared = shared;
        lastPartyRegionName = regionName;
        lastPartyLocationName = locationName;
        lastPartyLocationState = state;
        lastNpcBuildingRevealMapID = npcBuildingRevealMapID;
        lastPartyBuildingKey = buildingKey;
        lastPartyBuildingEntranceAnchorValid = buildingEntranceAnchorValid;
        lastPartyBuildingEntranceAnchorX = buildingEntranceAnchorX;
        lastPartyBuildingEntranceAnchorZ = buildingEntranceAnchorZ;
        lastPartyExteriorArrivalYValid = exteriorArrivalYValid;
        lastPartyExteriorArrivalY = exteriorArrivalY;
        lastPartyDungeonInstanceId = dungeonInstanceId;

        if (isServer)
        {
            ServerSetPartyLocation(
                shared,
                regionName,
                locationName,
                state,
                npcBuildingRevealMapID,
                buildingKey,
                buildingEntranceAnchorValid,
                buildingEntranceAnchorX,
                buildingEntranceAnchorZ,
                exteriorArrivalYValid,
                exteriorArrivalY,
                dungeonInstanceId);
        }
        else
        {
            CmdSetPartyLocation(
                shared,
                regionName,
                locationName,
                state,
                npcBuildingRevealMapID,
                buildingKey,
                buildingEntranceAnchorValid,
                buildingEntranceAnchorX,
                buildingEntranceAnchorZ,
                exteriorArrivalYValid,
                exteriorArrivalY,
                dungeonInstanceId);
        }
    }

    private bool TryBuildPartyLocationSnapshot(
        out bool shared,
        out string regionName,
        out string locationName,
        out PartyLocationState state,
        out int npcBuildingRevealMapID,
        out int buildingKey,
        out bool buildingEntranceAnchorValid,
        out int buildingEntranceAnchorX,
        out int buildingEntranceAnchorZ,
        out bool exteriorArrivalYValid,
        out float exteriorArrivalY,
        out string dungeonInstanceId)
    {
        CacheLocalReferences();

        shared = shareCurrentLocationWithParty;
        regionName = string.Empty;
        locationName = string.Empty;
        state = PartyLocationState.Unknown;
        npcBuildingRevealMapID = -1;
        buildingKey = 0;
        buildingEntranceAnchorValid = false;
        buildingEntranceAnchorX = 0;
        buildingEntranceAnchorZ = 0;
        exteriorArrivalYValid = false;
        exteriorArrivalY = 0f;
        dungeonInstanceId = string.Empty;

        // The HUD text can be hidden, but town identity must still be calculated for
        // mandatory NPC building-map reveals. OptionsMultiplayer.sendLocation is also
        // intentionally not consulted here.
        if (gps == null)
            return false;

        try
        {
            regionName = gps.CurrentLocalizedRegionName ?? string.Empty;
        }
        catch { }

        if (string.IsNullOrEmpty(regionName))
        {
            try { regionName = gps.CurrentRegionName ?? string.Empty; }
            catch { }
        }

        bool hasLocation = false;
        bool inLocationRect = false;
        try
        {
            hasLocation = gps.HasCurrentLocation;
            inLocationRect = gps.IsPlayerInLocationRect;
        }
        catch { }

        if (hasLocation)
        {
            try { locationName = gps.CurrentLocalizedLocationName ?? string.Empty; }
            catch { }

            if (string.IsNullOrEmpty(locationName))
            {
                try { locationName = gps.CurrentLocation.Name ?? string.Empty; }
                catch { }
            }
        }

        PlayerEnterExit enterExit = null;
        if (GameManager.Instance != null)
            enterExit = GameManager.Instance.PlayerEnterExit;

        bool insideDungeon = enterExit != null && enterExit.IsPlayerInsideDungeon;
        bool insideBuilding = enterExit != null && enterExit.IsPlayerInsideBuilding;

        if (insideDungeon)
        {
            state = PartyLocationState.DungeonInterior;

            // x/z is already published from this dungeon's exact exterior world anchor.
            // Carry only the stable dungeon instance identity for the later interior handoff.
            DaggerfallDungeon currentDungeon = enterExit.Dungeon;
            if (currentDungeon != null &&
                currentDungeon.IsNetworkDungeonInstance &&
                !string.IsNullOrEmpty(currentDungeon.DungeonInstanceId))
            {
                dungeonInstanceId = currentDungeon.DungeonInstanceId;
            }
        }
        else if (insideBuilding)
        {
            state = PartyLocationState.BuildingInterior;

            // BuildingDiscoveryData is the authoritative saved identity for the current
            // interior. Older saves can expose buildingKey == 0 until the player re-enters,
            // so fall back to the live interior entry door when available. Zero remains
            // "unknown" and is never accepted as a same-building match.
            try { buildingKey = enterExit.BuildingDiscoveryData.buildingKey; }
            catch { buildingKey = 0; }

            if (buildingKey == 0)
            {
                try
                {
                    if (enterExit.Interior != null)
                        buildingKey = enterExit.Interior.EntryDoor.buildingKey;
                }
                catch { buildingKey = 0; }
            }

            // The normal building-exit code retains the exterior door list and can
            // calculate the same safe point immediately outside the door. Publish that
            // point separately for party travel instead of reusing interior PlayerGPS x/z.
            buildingEntranceAnchorValid =
                enterExit.TryGetCurrentBuildingExteriorArrivalWorldCoordinates(
                    out buildingEntranceAnchorX,
                    out buildingEntranceAnchorZ);
        }
        else if (hasLocation && inLocationRect)
            state = PartyLocationState.ExteriorLocation;
        else
            state = PartyLocationState.Wilderness;

        // Building keys are only unique inside their parent location, so the network
        // reveal path always pairs them with this numeric map ID. Publish a valid ID only
        // while actually in a town exterior or one of that town's building interiors.
        if (state == PartyLocationState.ExteriorLocation ||
            state == PartyLocationState.BuildingInterior)
        {
            try
            {
                int currentMapID = gps.CurrentMapID;
                if (currentMapID >= 0)
                    npcBuildingRevealMapID = currentMapID;
            }
            catch { }
        }

        // Exterior terrain, town platforms, castle stairs, and rooftops use the same
        // Unity Y elevation on every peer. Publish the PlayerAdvanced controller-centre
        // height only for exterior states. Interior and dungeon Y values are deliberately
        // excluded because those spaces use temporary multiplayer vertical offsets.
        if ((state == PartyLocationState.ExteriorLocation || state == PartyLocationState.Wilderness) &&
            playerTransform != null)
        {
            float currentY = playerTransform.position.y;
            if (!float.IsNaN(currentY) && !float.IsInfinity(currentY))
            {
                exteriorArrivalYValid = true;
                exteriorArrivalY = currentY;
            }
        }

        // A map pixel can contain a named location while the player is outside its
        // actual rect. In that case the party HUD should say regional wilderness,
        // not imply that the player is already inside the nearby town/dungeon.
        if (state == PartyLocationState.Wilderness)
            locationName = string.Empty;

        return true;
    }

    [Command]
    private void CmdSetPartyLocation(
        bool shared,
        string regionName,
        string locationName,
        PartyLocationState state,
        int npcBuildingRevealMapID,
        int buildingKey,
        bool buildingEntranceAnchorValid,
        int buildingEntranceAnchorX,
        int buildingEntranceAnchorZ,
        bool exteriorArrivalYValid,
        float exteriorArrivalY,
        string dungeonInstanceId)
    {
        ServerSetPartyLocation(
            shared,
            regionName,
            locationName,
            state,
            npcBuildingRevealMapID,
            buildingKey,
            buildingEntranceAnchorValid,
            buildingEntranceAnchorX,
            buildingEntranceAnchorZ,
            exteriorArrivalYValid,
            exteriorArrivalY,
            dungeonInstanceId);
    }

    [Server]
    private void ServerSetPartyLocation(
        bool shared,
        string regionName,
        string locationName,
        PartyLocationState state,
        int npcBuildingRevealMapID,
        int buildingKey,
        bool buildingEntranceAnchorValid,
        int buildingEntranceAnchorX,
        int buildingEntranceAnchorZ,
        bool exteriorArrivalYValid,
        float exteriorArrivalY,
        string dungeonInstanceId)
    {
        if ((int)state < (int)PartyLocationState.Unknown ||
            (int)state > (int)PartyLocationState.DungeonInterior)
            state = PartyLocationState.Unknown;

        PartyLocationShared = shared;
        PartyRegionName = shared ? SanitizePartyLocationText(regionName) : string.Empty;
        PartyLocationName = shared ? SanitizePartyLocationText(locationName) : string.Empty;
        PartyCurrentLocationState = shared ? state : PartyLocationState.Unknown;

        // This identity remains active even when the party-HUD location line is hidden.
        // It is not a user preference and is not connected to sendLocation.
        bool acceptNpcBuildingRevealMapID =
            (state == PartyLocationState.ExteriorLocation ||
             state == PartyLocationState.BuildingInterior) &&
            npcBuildingRevealMapID >= 0;
        NpcBuildingRevealMapID = acceptNpcBuildingRevealMapID ? npcBuildingRevealMapID : -1;

        bool acceptBuildingKey =
            shared &&
            state == PartyLocationState.BuildingInterior &&
            npcBuildingRevealMapID >= 0 &&
            buildingKey != 0;
        PartyBuildingKey = acceptBuildingKey ? buildingKey : 0;

        bool acceptBuildingAnchor =
            shared &&
            state == PartyLocationState.BuildingInterior &&
            buildingEntranceAnchorValid &&
            buildingEntranceAnchorX > 0 &&
            buildingEntranceAnchorZ > 0;

        PartyBuildingEntranceAnchorValid = acceptBuildingAnchor;
        PartyBuildingEntranceAnchorX = acceptBuildingAnchor ? buildingEntranceAnchorX : 0;
        PartyBuildingEntranceAnchorZ = acceptBuildingAnchor ? buildingEntranceAnchorZ : 0;

        bool acceptExteriorArrivalY =
            shared &&
            (state == PartyLocationState.ExteriorLocation || state == PartyLocationState.Wilderness) &&
            exteriorArrivalYValid &&
            !float.IsNaN(exteriorArrivalY) &&
            !float.IsInfinity(exteriorArrivalY) &&
            exteriorArrivalY > -10000f &&
            exteriorArrivalY < 10000f;

        PartyExteriorArrivalYValid = acceptExteriorArrivalY;
        PartyExteriorArrivalY = acceptExteriorArrivalY ? exteriorArrivalY : 0f;

        bool acceptDungeonInstanceId =
            shared &&
            state == PartyLocationState.DungeonInterior &&
            !string.IsNullOrEmpty(dungeonInstanceId);

        PartyDungeonInstanceId = acceptDungeonInstanceId
            ? SanitizePartyDungeonInstanceId(dungeonInstanceId)
            : string.Empty;
    }


    /// <summary>
    /// Returns true when this remote player occupies the exact automap space currently
    /// loaded by the local player. This is intentionally an eligibility test only: exact
    /// marker XYZ/rotation still come from the remote NetworkTransform.
    /// </summary>
    public bool IsInSameAutomapSpaceAsLocalPlayer()
    {
        if (isLocalPlayer || !PartyLocationShared)
            return false;

        if (GameManager.Instance == null)
            return false;

        PlayerGPS localGps = GameManager.Instance.PlayerGPS;
        PlayerEnterExit localEnterExit = GameManager.Instance.PlayerEnterExit;
        if (localGps == null || localEnterExit == null)
            return false;

        if (localEnterExit.IsPlayerInsideDungeon)
        {
            if (PartyCurrentLocationState != PartyLocationState.DungeonInterior ||
                string.IsNullOrEmpty(PartyDungeonInstanceId))
                return false;

            DaggerfallDungeon localDungeon = localEnterExit.Dungeon;
            return localDungeon != null &&
                   localDungeon.IsNetworkDungeonInstance &&
                   !string.IsNullOrEmpty(localDungeon.DungeonInstanceId) &&
                   string.Equals(
                       PartyDungeonInstanceId,
                       localDungeon.DungeonInstanceId,
                       System.StringComparison.Ordinal);
        }

        if (localEnterExit.IsPlayerInsideBuilding)
        {
            if (PartyCurrentLocationState != PartyLocationState.BuildingInterior ||
                PartyBuildingKey == 0 ||
                NpcBuildingRevealMapID < 0)
                return false;

            int localMapID;
            try { localMapID = localGps.CurrentMapID; }
            catch { return false; }

            if (localMapID < 0 || localMapID != NpcBuildingRevealMapID)
                return false;

            int localBuildingKey = 0;
            try { localBuildingKey = localEnterExit.BuildingDiscoveryData.buildingKey; }
            catch { localBuildingKey = 0; }

            if (localBuildingKey == 0)
            {
                try
                {
                    if (localEnterExit.Interior != null)
                        localBuildingKey = localEnterExit.Interior.EntryDoor.buildingKey;
                }
                catch { localBuildingKey = 0; }
            }

            return localBuildingKey != 0 && localBuildingKey == PartyBuildingKey;
        }

        // The exterior automap represents a named location, not arbitrary wilderness.
        bool localInLocationRect = false;
        try { localInLocationRect = localGps.HasCurrentLocation && localGps.IsPlayerInLocationRect; }
        catch { }

        if (!localInLocationRect || PartyCurrentLocationState != PartyLocationState.ExteriorLocation)
            return false;

        try
        {
            return localGps.CurrentMapID >= 0 && localGps.CurrentMapID == NpcBuildingRevealMapID;
        }
        catch
        {
            return false;
        }
    }


    // -------------------------------------------------------------------------
    // Mandatory NPC building-map reveal sync.
    //
    // This is intentionally separate from SendLocation()/OptionsMultiplayer.sendLocation.
    // Only TalkManager's genuine "mark it on your map" result calls this path.
    // -------------------------------------------------------------------------

    public static void ShareNpcMarkedBuildingWithPlayersInTown(int mapID, int buildingKey)
    {
        // Preserve normal single-player behavior without requiring TalkManager to know
        // whether this component or a multiplayer session currently exists.
        if ((!NetworkClient.active && !NetworkServer.active) || mapID < 0 || buildingKey == 0)
            return;

        PositionMultiplayer[] players = FindObjectsOfType<PositionMultiplayer>();
        for (int i = 0; i < players.Length; i++)
        {
            PositionMultiplayer player = players[i];
            if (player != null && player.isLocalPlayer)
            {
                player.RequestShareNpcMarkedBuilding(mapID, buildingKey);
                return;
            }
        }
    }

    private void RequestShareNpcMarkedBuilding(int mapID, int buildingKey)
    {
        if (!isLocalPlayer || mapID < 0 || buildingKey == 0)
            return;

        // Publish the sender's latest town identity first. On a remote client, Mirror's
        // reliable command ordering ensures this update reaches the server before the
        // reveal request sent immediately after it.
        SendPartyLocationSnapshotIfChanged();

        int currentTownMapID;
        if (!TryGetEligibleNpcBuildingRevealTownMapID(out currentTownMapID) ||
            currentTownMapID != mapID)
        {
            Debug.LogWarning(string.Format(
                "[Multiplayer] Skipped NPC building reveal {0}/{1}: local player is no longer in that town.",
                mapID,
                buildingKey));
            return;
        }

        if (isServer)
            ServerShareNpcMarkedBuilding(mapID, buildingKey);
        else
            CmdShareNpcMarkedBuilding(mapID, buildingKey);
    }

    private bool TryGetEligibleNpcBuildingRevealTownMapID(out int mapID)
    {
        mapID = -1;
        CacheLocalReferences();

        if (gps == null)
            return false;

        PlayerEnterExit enterExit = null;
        if (GameManager.Instance != null)
            enterExit = GameManager.Instance.PlayerEnterExit;

        if (enterExit != null && enterExit.IsPlayerInsideDungeon)
            return false;

        bool insideBuilding = enterExit != null && enterExit.IsPlayerInsideBuilding;
        bool hasLocation = false;
        bool inLocationRect = false;

        try
        {
            hasLocation = gps.HasCurrentLocation;
            inLocationRect = gps.IsPlayerInLocationRect;
        }
        catch { }

        if (!insideBuilding && !(hasLocation && inLocationRect))
            return false;

        try
        {
            mapID = gps.CurrentMapID;
            return mapID >= 0;
        }
        catch
        {
            mapID = -1;
            return false;
        }
    }

    [Command]
    private void CmdShareNpcMarkedBuilding(int mapID, int buildingKey)
    {
        ServerShareNpcMarkedBuilding(mapID, buildingKey);
    }

    [Server]
    private void ServerShareNpcMarkedBuilding(int mapID, int buildingKey)
    {
        if (mapID < 0 || buildingKey == 0)
            return;

        // The sender must currently publish the same eligible town ID. The field is -1
        // in wilderness and dungeons, so those states cannot relay a town reveal.
        if (NpcBuildingRevealMapID != mapID)
        {
            Debug.LogWarning(string.Format(
                "[Multiplayer] Rejected NPC building reveal {0}/{1}: sender town ID is {2}.",
                mapID,
                buildingKey,
                NpcBuildingRevealMapID));
            return;
        }

        PositionMultiplayer[] players = FindObjectsOfType<PositionMultiplayer>();
        for (int i = 0; i < players.Length; i++)
        {
            PositionMultiplayer recipient = players[i];
            if (recipient == null || recipient == this)
                continue;

            // This test is evaluated only at reveal time. Nothing is queued for players
            // who enter the town later.
            if (recipient.NpcBuildingRevealMapID != mapID ||
                recipient.connectionToClient == null)
                continue;

            recipient.TargetReceiveNpcMarkedBuilding(
                recipient.connectionToClient,
                mapID,
                buildingKey);
        }
    }

    [TargetRpc]
    private void TargetReceiveNpcMarkedBuilding(
        NetworkConnection target,
        int mapID,
        int buildingKey)
    {
        if (!isLocalPlayer || mapID < 0 || buildingKey == 0)
            return;

        // Recheck locally in case this player left the town after the server selected
        // recipients but before the targeted RPC arrived.
        int currentTownMapID;
        if (!TryGetEligibleNpcBuildingRevealTownMapID(out currentTownMapID) ||
            currentTownMapID != mapID)
        {
            Debug.Log(string.Format(
                "[Multiplayer] Ignored NPC building reveal {0}/{1}: recipient changed location.",
                mapID,
                buildingKey));
            return;
        }

        try
        {
            gps.DiscoverBuilding(buildingKey);
            Debug.Log(string.Format(
                "[Multiplayer] Received NPC building reveal {0}/{1} from another player in town.",
                mapID,
                buildingKey));
        }
        catch (System.Exception ex)
        {
            Debug.LogError(string.Format(
                "[Multiplayer] Failed to apply NPC building reveal {0}/{1}: {2}",
                mapID,
                buildingKey,
                ex.Message));
        }
    }

    private static string SanitizePartyLocationText(string value)
    {
        value = (value ?? string.Empty).Trim();
        const int maxLength = 96;
        if (value.Length > maxLength)
            value = value.Substring(0, maxLength);

        return value;
    }

    private static string SanitizePartyDungeonInstanceId(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        value = value.Trim();
        if (value.Length > 64)
            value = value.Substring(0, 64);

        // Current dungeon IDs are Guid strings in "N" format. Keep this deliberately
        // strict so a client cannot use the party snapshot as an arbitrary string channel.
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool hexadecimal =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');

            if (!hexadecimal)
                return string.Empty;
        }

        return value;
    }

    [Command]
    public void cmdSendCoordinates(int _x, int _z)
    {
        x = _x;
        z = _z;
    }
}
