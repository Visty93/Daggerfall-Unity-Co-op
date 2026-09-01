using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;

/// <summary>
/// Smooth/lightweight multiplayer sync for wandering city MobilePersonNPC civilians.
///
/// v10c fixed compact-motion changes:
/// - Preserves the smooth v10c 0.40 second cadence, yaw updates, prediction, smoothing,
///   and reliable stop acknowledgement path.
/// - Reduces every walking entry from 36 bytes to a fixed 18 bytes. Position and target are
///   centimetre-scale offsets from an exact batch origin, while yaw/state/idle remain present.
/// - Uses only normally generated Mirror serializers, so MotionBatchEntry arrays cannot silently
///   fall back to the old 36-byte layout when the custom array serializer is not selected.
/// - Carries up to 32 civilians in one unreliable batch to reduce Mirror/transport envelopes.
/// - Caches remote ghost renderers/colliders once instead of allocating two component arrays for
///   every remote NPC every frame. This removes a potential periodic garbage-collection hitch.
/// - Removes redundant motion owner/time fields and disables full periodic spawn refreshes
///   by default. Explicit load/location and missing-spawn recovery remain enabled.
/// - Predicts walking from smoothed velocity measured between consecutive owner positions rather
///   than from the current navigation waypoint, avoiding waypoint-change velocity dips.
/// - Holds ordinary walking disagreement inside a small receiver-only dead zone and corrects only
///   the excess slowly. The authoritative idle state remains immediate.
/// - Compensates remote stop requests for the measured distance that the owner is ahead of the
///   rendered ghost, while keeping the owner's stop state authoritative.
/// - Keeps an acknowledged remote stop latched until the player's real stop conditions change,
///   preventing an idle NPC from restarting while its ghost settles to the owner position.
/// - Caps the visible correction on the idle frame, holds the remaining receiver-only position
///   offset while stopped, and drains it gradually only after walking resumes.
///
/// v10c changes:
/// - Compacts ordinary motion entries by sending a 16-bit yaw and byte-sized state/flags instead of a full quaternion/int/bool set.
/// - Uses a 0.40 second default motion cadence; reliable stop acknowledgements remain immediate.
///
/// v10b changes:
/// - Explicitly tears down and rebuilds civilian sync state when any save is loaded, including a save in the same town.
/// - Batches changed civilian motion into one unreliable message instead of one RPC per NPC.
/// - Sends civilian spawn/motion only while another player is in the same town and routes those packets only to that town.
/// - Keeps the v10a exterior/load handshake/location filtering behaviour.
/// - Adds a very cheap realtime owner heartbeat while outside. This keeps remote ghosts alive during menus/time-stop
///   even when normal Time.time-based spawn/motion refreshes stop.
/// - Heartbeat can also temporarily freeze remote ghost prediction while the owner is paused, avoiding drift/snap after long menus.
///
/// Steam motion smoothing patch:
/// - Uses receiver-local packet age instead of comparing NetworkTime values from two clients.
/// - Uses velocity-preserving SmoothDamp for remote transforms and caps extrapolation across packet stalls.
/// - Leads approaching-NPC stop requests by Steam transit time; non-owned ghosts keep walking until the owner's idle state arrives.
/// - Applies owner idle immediately on receipt and returns a dedicated reliable stop-position acknowledgment.
/// - Sends pause-state edges immediately without bursting every NPC snapshot on resume.
/// - Rate-limits full reliable spawn refreshes and prevents reciprocal all-NPC burst feedback loops.
/// - Publishes an always-on exterior MapID used to divide the normal town population across same-town players.
/// - Does not change the original stop predicate, stop distance, owner idle lease, or owner motor control.
///
/// Attach this to the multiplayer player prefab next to PlayerMultiplayer/EntityCatcher-style scripts.
/// Requires MobilePersonNPC_NPCSoftSync_v10a.cs because it adds ApplySyncedPerson() and GetPersonFaceVariant().
/// </summary>
public class MobileNpcSync : NetworkBehaviour
{
    [SyncVar]
    public bool npcPopulationExteriorActive;

    [SyncVar]
    public int npcPopulationLocationMapId = -1;

    #region Settings

    [Header("NPC soft sync")]
    public bool enableMobileNpcSync = true;

    [Tooltip("How often this player's local civilians send transform/path snapshots.")]
    public float sendInterval = 0.40f;

    [Tooltip("Maximum changed civilians carried by one unreliable compact motion batch. Runtime clamps this to 1-32.")]
    public int compactMotionBatchSize = 32;

    [Tooltip("Fallback interval for resending full reliable identity/spawn packets. Runtime enforces at least 30 seconds; explicit load/join refreshes and targeted missing-spawn requests handle late arrivals.")]
    public float spawnRefreshInterval = 45.0f;

    [Tooltip("Optional fallback that resends every NPC's full reliable identity packet every 30+ seconds. Disabled by default because reliable spawn, load/location refresh, and targeted missing-spawn recovery already cover normal cases.")]
    public bool periodicSpawnRefresh = false;

    [Tooltip("Minimum position delta before a motion snapshot is sent.")]
    public float sendDistanceThreshold = 0.20f;

    [Tooltip("Minimum yaw delta before a motion snapshot is sent.")]
    public float sendRotationThreshold = 8f;

    [Tooltip("Remote smoothing strength. Higher follows owner faster, lower is softer.")]
    public float remoteSmoothing = 14f;

    [Tooltip("SmoothDamp response time used only for remote civilian position display over the network.")]
    public float steamPositionSmoothTime = 0.14f;

    [Tooltip("Shorter visual settling time after an authoritative idle snapshot. This does not change NPC stop requests or owner motor control.")]
    public float steamStopSmoothTime = 0.04f;

    [Tooltip("Maximum distance a rendered ghost is pulled toward the owner's exact position when authoritative idle first arrives. The rest is held as a visual-only offset while stopped.")]
    public float steamStopMaxVisualCorrection = 0.12f;

    [Tooltip("Maximum stop-position offset that can be carried into resumed walking instead of jumping immediately to the owner path.")]
    public float steamResumeVisualOffsetMaxCarry = 1.0f;

    [Tooltip("World-space speed at which a held stop-position offset is removed after walking resumes.")]
    public float steamResumeVisualOffsetCorrectionSpeed = 0.15f;

    [Tooltip("Response strength used to smooth velocity measured from consecutive owner positions. Higher follows turns faster; lower filters packet-position noise more strongly.")]
    public float steamVelocitySmoothing = 3.0f;

    [Tooltip("Maximum accepted measured walking speed as a multiplier of Predicted Move Speed. Rejects teleport-like packet deltas from the velocity estimate.")]
    public float steamMeasuredVelocityMaxMultiplier = 1.75f;

    [Tooltip("Walking position disagreement held without changing visual velocity. Idle/stop packets bypass this and remain exact.")]
    public float steamWalkingCorrectionDeadZone = 0.06f;

    [Tooltip("Maximum walking disagreement carried across a new packet. This bounds any later authoritative stop settle.")]
    public float steamWalkingCorrectionMaxCarry = 0.10f;

    [Tooltip("World-space speed used to remove only walking disagreement beyond the dead zone.")]
    public float steamWalkingCorrectionSpeed = 0.08f;

    [Tooltip("Maximum receiver-local seconds to predict a remote civilian along its reported path after the newest motion packet.")]
    public float steamMaxPredictionSeconds = 0.50f;

    [Tooltip("If a remote ghost error is bigger than this, it snaps instead of lerping.")]
    public float snapDistance = 8f;

    [Tooltip("Default MobilePersonMotor movement speed. Vanilla is 1.3f.")]
    public float predictedMoveSpeed = 1.3f;

    [Tooltip("Remote ghosts keep their colliders/entity components active so they can be talked to and killed. Turn this on only for visual debugging.")]
    public bool remoteGhostsAreVisualOnly = false;

    [Tooltip("If true, a remote ghost killed on this client is reported through the server so the owner and other clients kill the same civilian.")]
    public bool syncRemoteGhostDeaths = true;

    [Header("Remote idle behaviour")]
    [Tooltip("Requests the owner to pause this civilian when a remote player is close, instead of locally stopping the ghost and causing snapback.")]
    public bool localIdleOverrideForRemoteGhosts = true;

    [Tooltip("Same distance used by vanilla MobilePersonMotor for civilians stopping near the player.")]
    public float localIdleDistance = 2.5f;

    [Tooltip("Steam request lead time. The owner is asked to stop an approaching civilian this many seconds early; the non-owned ghost keeps moving until the owner's idle packet arrives.")]
    public float steamStopRequestLeadSeconds = 0.75f;

    [Tooltip("Maximum extra stop-request distance contributed by how far the estimated owner NPC is ahead of this rendered ghost.")]
    public float steamStopOwnerAheadCompensationMax = 0.50f;

    [Tooltip("Game-time interval for renewing an active remote stop request. The first stop and final release are still sent immediately.")]
    public float remoteIdleRenewInterval = 1.5f;

    [Tooltip("Game-time safety lifetime of one player's stop request. Requests are tracked separately so another player cannot cancel this lease.")]
    public float remoteIdleLeaseSeconds = 5.0f;

    [Tooltip("Ignore late unreliable motion briefly after a reliable NPC disable, preventing quota culls from requesting full spawn refreshes.")]
    public float disabledGhostMotionIgnoreSeconds = 3.0f;

    [Tooltip("How long to retry creating a remote ghost while the receiving client is still loading the same exterior.")]
    public float ghostCreateRetrySeconds = 8f;

    [Tooltip("Drop remote NPC spawn packets immediately when this client is not in the same exterior location. This prevents hundreds of useless TryCreateRemoteGhost coroutines when players are in different towns.")]
    public bool strictRemoteLocationFiltering = true;

    [Tooltip("When entering/loading/changing an exterior location, ask other players in the same town to resend their already-active civilian spawn packets.")]
    public bool requestSpawnsOnExteriorLocationChange = true;

    [Tooltip("When this client accepts another player's civilians, send a short burst of this client's own local civilians back. This helps late host loads see client-owned NPCs immediately.")]
    public bool reciprocalSpawnBurstOnAcceptedRemoteSpawn = true;

    [Tooltip("Minimum seconds between automatic all-local civilian spawn bursts.")]
    public float reciprocalSpawnBurstMinInterval = 4.0f;

    [Header("Owner cleanup")]
    [Tooltip("If the player object that owns a remote ghost is gone for this long, remove that ghost. This is the main disconnect/leave cleanup.")]
    public float ownerMissingCleanupDelay = 1.5f;

    [Tooltip("Fallback cleanup if no spawn/motion/heartbeat packet was received from a ghost owner for this long. With heartbeat enabled, code enforces a safe minimum so long menus do not erase ghosts.")]
    public float ownerPacketTimeout = 15f;

    [Header("Pause heartbeat")]
    [Tooltip("Sends a tiny realtime heartbeat while this player is outside with owned civilians, so remote ghosts are not deleted during menus/time-stop.")]
    public bool sendOwnerHeartbeat = true;

    [Tooltip("Realtime seconds between owner heartbeat packets. This is per player/location, not per NPC.")]
    public float ownerHeartbeatInterval = 1.5f;

    [Tooltip("When an owner heartbeat says the owner is paused, freeze remote ghost prediction until normal packets resume.")]
    public bool freezeRemoteGhostsWhenOwnerPaused = true;

    [Tooltip("Extra grace after the expected heartbeat time before remote ghosts resume prediction.")]
    public float ownerPausedFreezeGrace = 2.25f;

    [Header("Exterior transition cleanup")]
    [Tooltip("When the local player leaves exterior for an interior/dungeon, clear this player's exported civilians on other clients and remove received ghosts locally.")]
    public bool clearGhostsWhenLeavingExterior = true;

    [Tooltip("How often to check whether the local player has left exterior. Low value keeps cleanup responsive without checking every frame.")]
    public float exteriorStateCheckInterval = 0.25f;

    [Header("Debug")]
    public bool verboseLogging = false;

    [Tooltip("Print one local-only remote-motion diagnostic summary every five seconds. Adds no network traffic.")]
    public bool enableVelocityMotionDiagnostics = false;

    [Tooltip("Seconds accumulated by each local-only remote-motion diagnostic summary.")]
    public float motionDiagnosticsInterval = 5.0f;

    #endregion

    #region Records

    class LocalRecord
    {
        public MobilePersonNPC npc;
        public int npcId;
        public string locationKey;
        public Vector3 lastSentPosition;
        public Quaternion lastSentRotation;
        public Vector3 lastSentTarget;
        public bool lastSentIdle;
        public int lastSentState;
        public float nextSpawnRefreshTime;
        public bool deathSent;
        // True once at least one reliable spawn packet for this record was exported.
        // Terminal removal packets use this instead of a possibly stale same-town observer cache.
        public bool wasExported;
        public readonly Dictionary<string, float> remoteIdleUntilByRequester = new Dictionary<string, float>();
        public bool motorDisabledByRemoteIdle;
        public bool remoteIdleAppliedAckSent;
        public byte nextMotionSequence;
    }

    class RemoteRecord
    {
        public string ownerPlayerId;
        public string locationKey;
        public int npcId;
        public GameObject gameObject;
        public MobilePersonNPC npc;
        public MobilePersonMotor motor;
        public Vector3 lastNetworkPosition;
        public Quaternion lastNetworkRotation;
        public Vector3 targetPosition;
        public bool networkIdle;
        public int state;
        public float packetTime;
        public float lastSeenRealtime;
        public float ownerMissingSinceRealtime;
        public float nextOwnerPresenceCheckRealtime;
        public bool ownerPlayerObjectPresent;
        public bool disabled;
        public bool deathSent;
        public bool localIdleOverride;
        public float ownerPausedFreezeUntilRealtime;
        public bool lastIdleRequestSent;
        public float nextIdleRequestTime;
        public Vector3 positionSmoothVelocity;
        public Vector3 predictionCorrectionOffset;
        public Vector3 networkVelocity;
        public bool hasNetworkVelocity;
        public Vector3 idleVisualPosition;
        public bool hasIdleVisualPosition;
        public Vector3 resumeVisualOffset;
        public float resumeVisualOffsetStartRealtime;
        public bool hasResumeVisualOffset;
        public float lastMotionReceiveRealtime;
        public bool wasLocallyPaused;
        public bool hasAcceptedMotionSequence;
        public byte lastAcceptedMotionSequence;
        public bool preemptiveIdleRequestLatched;
        public Renderer[] cachedRenderers;
        public Collider[] cachedColliders;
        public bool diagnosticsHasVisualSample;
        public Vector3 diagnosticsLastVisualPosition;
        public float diagnosticsLastVisualRealtime;
        public float diagnosticsLastVisualSpeed;
        public int diagnosticsLastMotionFrame;
    }

    class SpawnPacket
    {
        public string ownerPlayerId;
        public string locationKey;
        public int npcId;
        public Vector3 position;
        public Quaternion rotation;
        public int race;
        public int gender;
        public int outfitVariant;
        public bool isGuard;
        public int faceVariant;
        public int faceRecordId;
        public string npcName;
        public int direction;
        public bool idle;
        public Vector3 targetPosition;
    }

    // Fixed 18-byte wire layout. Mirror writes these primitive fields directly without CLR padding.
    // A Vector3 batch origin is sent once per batch, not once per NPC.
    public struct MotionBatchEntry
    {
        public ushort npcId;
        public short positionX;
        public short positionY;
        public short positionZ;
        public ushort yaw;
        public short targetOffsetX;
        public short targetOffsetY;
        public short targetOffsetZ;
        public byte stateAndFlags;
        public byte motionSequence;
    }

    struct PendingMotionEntry
    {
        public int npcId;
        public Vector3 position;
        public ushort yaw;
        public Vector3 targetPosition;
        public byte state;
        public bool idle;
        public byte motionSequence;
    }

    const byte MotionStateMask = 0x7f;
    const byte MotionFlagIdle = 0x80;
    const float MotionPositionScale = 32f;
    const float MotionTargetScale = 32f;

    #endregion

    #region Static State

    static MobileNpcSync localInstance;
    static readonly Dictionary<string, RemoteRecord> remoteGhosts = new Dictionary<string, RemoteRecord>();
    static readonly Dictionary<string, Coroutine> pendingGhostCreates = new Dictionary<string, Coroutine>();
    static readonly Dictionary<string, float> disabledGhostMotionUntil = new Dictionary<string, float>();

    public static void ApplySessionOption(bool enabled)
    {
        // OptionsMultiplayer is authoritative for the session; the parameter makes the call
        // explicit and lets this react immediately when the host option RPC reaches a client.
        if (localInstance != null)
            localInstance.ApplySessionOptionIfChanged(enabled);
    }

    #endregion

    #region Local State

    readonly Dictionary<MobilePersonNPC, LocalRecord> localRecordsByNpc = new Dictionary<MobilePersonNPC, LocalRecord>();
    readonly Dictionary<string, LocalRecord> localRecordsByNetKey = new Dictionary<string, LocalRecord>();
    readonly Dictionary<string, int> nextLocalNpcIdByLocation = new Dictionary<string, int>();

    bool hasExteriorState;
    bool lastWasInExterior;
    string lastExteriorLocationKey = string.Empty;
    float nextExteriorStateCheckRealtime;
    Coroutine queuedLocalSpawnBurstCoroutine;
    bool queuedLocalSpawnBurstForced;
    Coroutine ownerHeartbeatCoroutine;
    Coroutine saveLoadRecoveryCoroutine;
    bool saveLoadSubscribed;
    bool saveLoadRecoveryInProgress;
    float nextAllowedLocalSpawnBurstRealtime;
    readonly Dictionary<string, float> nextMissingSpawnRequestRealtimeByOwner = new Dictionary<string, float>();
    readonly HashSet<string> reciprocalSpawnBurstKeys = new HashSet<string>();
    bool hasObservedLocalPauseState;
    bool lastObservedLocalPauseState;
    bool hasPublishedNpcPopulationLocation;
    bool lastPublishedNpcPopulationExteriorActive;
    int lastPublishedNpcPopulationLocationMapId = -1;
    float nextDisabledGhostTombstoneCleanupRealtime;
    readonly List<PendingMotionEntry> pendingMotionBatch = new List<PendingMotionEntry>(32);
    float nextMotionBatchTime;
    float nextTownObserverCheckRealtime;
    bool cachedHasSameTownObserver;
    float motionDiagnosticsWindowStartRealtime;
    int motionDiagnosticsFrames;
    int motionDiagnosticsLongFrames;
    float motionDiagnosticsMaximumFrameSeconds;
    int motionDiagnosticsPackets;
    int motionDiagnosticsGapSamples;
    float motionDiagnosticsPacketGapTotal;
    float motionDiagnosticsMaximumPacketGap;
    int motionDiagnosticsLatePackets;
    int motionDiagnosticsOutOfOrderPackets;
    int motionDiagnosticsEstimatedMissingSnapshots;
    float motionDiagnosticsCorrectionTotal;
    float motionDiagnosticsMaximumCorrection;
    int motionDiagnosticsMovingSamples;
    int motionDiagnosticsSpeedDrops;
    int motionDiagnosticsSpeedDropsOnPacketFrame;
    int motionDiagnosticsSpeedDropsAtPredictionLimit;
    int motionDiagnosticsSpeedDropsOnLongFrame;
    int motionDiagnosticsIdleEdges;
    bool hasObservedSessionOption;
    bool lastObservedSessionSyncEnabled;

    #endregion

    #region Unity

    void Start()
    {
        if (!isLocalPlayer)
            return;

        localInstance = this;
        motionDiagnosticsWindowStartRealtime = Time.realtimeSinceStartup;
        PopulationManager.OnMobileNPCEnable += OnMobileNpcEnable;
        PopulationManager.OnMobileNPCDisable += OnMobileNpcDisable;
        SaveLoadManager.OnStartLoad += OnSaveLoadStarted;
        SaveLoadManager.OnLoad += OnSaveLoaded;
        saveLoadSubscribed = true;
        lastWasInExterior = IsLocalPlayerInExterior();
        lastExteriorLocationKey = lastWasInExterior ? GetCurrentExteriorLocationKey() : string.Empty;
        hasExteriorState = true;
        hasObservedSessionOption = true;
        lastObservedSessionSyncEnabled = IsSessionSyncEnabled();
        StartCoroutine(ScanExistingLocalMobilesLoop());
        ownerHeartbeatCoroutine = StartCoroutine(OwnerHeartbeatLoop());

        if (lastObservedSessionSyncEnabled && lastWasInExterior)
        {
            QueueAllLocalSpawnBurst(0.75f);
            RequestSameLocationSpawnRefresh(1.0f);
        }

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Local fixed18-measured-velocity instance started.");
    }

    void OnDisable()
    {
        CleanupForLocalSyncShutdown();
    }

    void OnDestroy()
    {
        CleanupForLocalSyncShutdown();
    }

    void CleanupForLocalSyncShutdown()
    {
        // Disconnect/local player teardown case. Remote ghosts are normal local GameObjects, not network-spawned
        // objects, so they must be explicitly removed before this sync script stops ticking.
        bool wasLocalSync = (localInstance == this) || isLocalPlayer;

        if (wasLocalSync)
        {
            PopulationManager.OnMobileNPCEnable -= OnMobileNpcEnable;
            PopulationManager.OnMobileNPCDisable -= OnMobileNpcDisable;
            if (saveLoadSubscribed)
            {
                SaveLoadManager.OnStartLoad -= OnSaveLoadStarted;
                SaveLoadManager.OnLoad -= OnSaveLoaded;
                saveLoadSubscribed = false;
            }
            RemoveAllRemoteGhosts();
            StopAllPendingGhostCreates();

            if (ownerHeartbeatCoroutine != null)
            {
                StopCoroutine(ownerHeartbeatCoroutine);
                ownerHeartbeatCoroutine = null;
            }

            if (saveLoadRecoveryCoroutine != null)
            {
                StopCoroutine(saveLoadRecoveryCoroutine);
                saveLoadRecoveryCoroutine = null;
            }
            saveLoadRecoveryInProgress = false;
        }

        if (localInstance == this)
            localInstance = null;
    }

    void Update()
    {
        // Only the local player's component should tick the static ghost dictionaries.
        // RPCs can still be received on remote-player component instances, but update work must not run multiple times per frame.
        if (!isLocalPlayer)
            return;

        ApplySessionOptionIfChanged(OptionsMultiplayer.mobileNpcSync);
        if (!IsSessionSyncEnabled())
            return;

        // SaveLoadManager.OnLoad can run before every exterior object has finished registering.
        // Keep the old town from being republished during that short rebuild window.
        if (saveLoadRecoveryInProgress)
            return;

        UpdateMotionDiagnosticsFrame();

        UpdateExteriorTransitionCleanup();
        PublishNpcPopulationLocationIfChanged();
        PruneDisabledGhostMotionTombstones();

        if (!IsLocalPlayerInExterior())
            return;

        HandleLocalPauseStateChange();
        UpdateLocalOwnerMobiles();
        UpdateRemoteGhostDeaths();
        UpdateRemoteGhosts();
        FlushMotionDiagnosticsIfDue();
    }

    bool IsSessionSyncEnabled()
    {
        // The inspector field remains a hard local fallback. In the normal prefab it is true,
        // and the host-synchronized OptionsMultiplayer value decides the live session policy.
        return enableMobileNpcSync && OptionsMultiplayer.mobileNpcSync;
    }

    void ApplySessionOptionIfChanged(bool hostOptionEnabled)
    {
        if (!isLocalPlayer)
            return;

        bool enabled = enableMobileNpcSync && hostOptionEnabled;
        if (hasObservedSessionOption && enabled == lastObservedSessionSyncEnabled)
            return;

        hasObservedSessionOption = true;
        lastObservedSessionSyncEnabled = enabled;

        if (!enabled)
        {
            // The server rejects civilian sync commands when the host policy is off. Locally,
            // remove any ghosts or pending work created during the short join/import window.
            ClearLocalGhostsAndOwnedRecordsForNonExterior(false);
            hasExteriorState = false;
            hasPublishedNpcPopulationLocation = false;
            lastPublishedNpcPopulationExteriorActive = false;
            lastPublishedNpcPopulationLocationMapId = -1;
            npcPopulationExteriorActive = false;
            npcPopulationLocationMapId = -1;

            if (isServer)
                ServerSetNpcPopulationLocation(false, -1);

            if (verboseLogging)
                Debug.Log("[MobileNpcSync] Disabled by host session option.");
            return;
        }

        bool isExterior = IsLocalPlayerInExterior();
        string locationKey = isExterior ? GetCurrentExteriorLocationKey() : string.Empty;
        hasExteriorState = true;
        lastWasInExterior = isExterior;
        lastExteriorLocationKey = locationKey;
        nextExteriorStateCheckRealtime = 0f;
        hasPublishedNpcPopulationLocation = false;

        PublishNpcPopulationLocationIfChanged();

        if (isExterior && !string.IsNullOrEmpty(locationKey))
        {
            ScanExistingLocalMobiles();
            QueueAllLocalSpawnBurst(0.10f, true);
            RequestSameLocationSpawnRefresh(0.20f);
        }

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Enabled by host session option.");
    }

    void PublishNpcPopulationLocationIfChanged()
    {
        bool exteriorActive = false;
        int mapId = -1;

        if (IsLocalPlayerInExterior() && GameManager.Instance && GameManager.Instance.PlayerGPS)
        {
            PlayerGPS gps = GameManager.Instance.PlayerGPS;
            if (gps.HasCurrentLocation && gps.IsPlayerInLocationRect)
            {
                mapId = gps.CurrentLocation.MapTableData.MapId;
                exteriorActive = mapId > 0;
            }
        }

        if (hasPublishedNpcPopulationLocation &&
            exteriorActive == lastPublishedNpcPopulationExteriorActive &&
            mapId == lastPublishedNpcPopulationLocationMapId)
            return;

        hasPublishedNpcPopulationLocation = true;
        lastPublishedNpcPopulationExteriorActive = exteriorActive;
        lastPublishedNpcPopulationLocationMapId = mapId;

        // Update the local copy immediately. The server-owned SyncVars then make this presence
        // available to every PopulationManager without depending on party-HUD privacy settings.
        npcPopulationExteriorActive = exteriorActive;
        npcPopulationLocationMapId = mapId;

        if (isServer)
            ServerSetNpcPopulationLocation(exteriorActive, mapId);
        else if (NetworkClient.isConnected)
            CmdSetNpcPopulationLocation(exteriorActive, mapId);
    }

    void OnSaveLoadStarted(SaveData_v1 saveData)
    {
        if (!isLocalPlayer)
            return;

        BeginSaveLoadNpcReset();
    }

    void OnSaveLoaded(SaveData_v1 saveData)
    {
        if (!isLocalPlayer)
            return;

        // OnStartLoad normally performed the teardown. Keep this fallback for alternate load
        // paths or other DFU versions/mods that invoke only the completion event.
        if (!saveLoadRecoveryInProgress)
            BeginSaveLoadNpcReset();

        if (saveLoadRecoveryCoroutine != null)
            StopCoroutine(saveLoadRecoveryCoroutine);
        saveLoadRecoveryCoroutine = StartCoroutine(RecoverNpcSyncAfterSaveLoad());
    }

    void BeginSaveLoadNpcReset()
    {
        if (saveLoadRecoveryInProgress)
            return;

        // A same-town save load does not change exterior state or locationKey, so transition
        // polling cannot detect it. Explicitly announce that every previously exported civilian
        // is gone, and also discard received ghosts whose local scene objects were just rebuilt.
        saveLoadRecoveryInProgress = true;
        ClearLocalGhostsAndOwnedRecordsForNonExterior(NetworkClient.isConnected);

        hasExteriorState = false;
        lastWasInExterior = false;
        lastExteriorLocationKey = string.Empty;
        nextExteriorStateCheckRealtime = 0f;
        hasPublishedNpcPopulationLocation = false;
        lastPublishedNpcPopulationExteriorActive = false;
        lastPublishedNpcPopulationLocationMapId = -1;
        npcPopulationExteriorActive = false;
        npcPopulationLocationMapId = -1;

        if (isServer)
            ServerSetNpcPopulationLocation(false, -1);
        else if (NetworkClient.isConnected)
            CmdSetNpcPopulationLocation(false, -1);
    }

    IEnumerator RecoverNpcSyncAfterSaveLoad()
    {
        // OnLoad is the authoritative DFU save event, but scene/population objects can finish
        // enabling over the following frames. Wait for the load flag and then one small realtime
        // settle window before rebuilding records and requesting peer-owned civilians.
        float timeout = Time.realtimeSinceStartup + 10f;
        while (SaveLoadManager.HasInstance && SaveLoadManager.Instance.LoadInProgress &&
               Time.realtimeSinceStartup < timeout)
        {
            yield return null;
        }

        yield return null;
        yield return new WaitForSecondsRealtime(0.25f);

        saveLoadRecoveryCoroutine = null;
        if (!isLocalPlayer || !IsSessionSyncEnabled())
        {
            saveLoadRecoveryInProgress = false;
            yield break;
        }

        saveLoadRecoveryInProgress = false;
        bool isExterior = IsLocalPlayerInExterior();
        string locationKey = isExterior ? GetCurrentExteriorLocationKey() : string.Empty;
        hasExteriorState = true;
        lastWasInExterior = isExterior;
        lastExteriorLocationKey = locationKey;
        nextExteriorStateCheckRealtime = 0f;

        PublishNpcPopulationLocationIfChanged();

        if (isExterior && !string.IsNullOrEmpty(locationKey))
        {
            ScanExistingLocalMobiles();
            QueueAllLocalSpawnBurst(0.10f, true);
            RequestSameLocationSpawnRefresh(0.20f);
        }

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Rebuilt civilian sync after save load. exterior=" + isExterior + " loc=" + locationKey);
    }

    #endregion

    #region Population Events

    void OnMobileNpcEnable(PopulationManager.PoolItem poolItem)
    {
        RegisterLocalNpc(poolItem, true);
    }

    IEnumerator ScanExistingLocalMobilesLoop()
    {
        yield return new WaitForSecondsRealtime(0.35f);

        while (isLocalPlayer)
        {
            if (IsSessionSyncEnabled())
                ScanExistingLocalMobiles();
            yield return new WaitForSecondsRealtime(1.0f);
        }
    }

    void ScanExistingLocalMobiles()
    {
        if (!CanUseLocalSync())
            return;

        PopulationManager[] managers = GameObject.FindObjectsOfType<PopulationManager>();
        for (int i = 0; i < managers.Length; i++)
        {
            List<PopulationManager.PoolItem> pool = managers[i].PopulationPool;
            for (int j = 0; j < pool.Count; j++)
            {
                PopulationManager.PoolItem item = pool[j];
                if (!item.active || item.scheduleRecycle || item.npc == null || item.npc.Motor == null)
                    continue;

                if (!item.npc.Motor.gameObject.activeInHierarchy)
                    continue;

                RegisterLocalNpc(item, false);
            }
        }
    }

    void RegisterLocalNpc(PopulationManager.PoolItem poolItem, bool fromEnableEvent)
    {
        if (!CanUseLocalSync())
            return;

        MobilePersonNPC npc = poolItem.npc;
        if (!npc || !npc.Motor || !npc.Asset || !npc.Motor.cityNavigation)
            return;

        if (localRecordsByNpc.ContainsKey(npc))
            return;

        if (IsNpcDead(npc))
            return;

        PopulationManager manager = npc.Motor.cityNavigation.GetComponent<PopulationManager>();
        if (!manager)
            return;

        string locationKey = BuildLocationKey(manager);
        int npcId = GetNextNpcId(locationKey);

        LocalRecord record = new LocalRecord();
        record.npc = npc;
        record.npcId = npcId;
        record.locationKey = locationKey;
        record.lastSentPosition = npc.Motor.transform.position;
        record.lastSentRotation = npc.Motor.transform.rotation;
        record.lastSentTarget = npc.Motor.TargetScenePosition;
        record.lastSentIdle = npc.Asset.IsIdle;
        record.lastSentState = (int)npc.Motor.CurrentState;
        record.nextSpawnRefreshTime = Time.time + GetSpawnRefreshDelay();

        localRecordsByNpc[npc] = record;
        localRecordsByNetKey[MakeLocalNetKey(locationKey, npcId)] = record;

        ForceLocalNpcVisible(npc);
        SendSpawn(record);

        if (verboseLogging && !fromEnableEvent)
            Debug.Log("[MobileNpcSync] Registered already-active local mobile npcId=" + npcId + " loc=" + locationKey);
    }

    void OnMobileNpcDisable(PopulationManager.PoolItem poolItem)
    {
        if (!CanUseLocalSync())
            return;

        MobilePersonNPC npc = poolItem.npc;
        if (!npc)
            return;

        LocalRecord record;
        if (!localRecordsByNpc.TryGetValue(npc, out record))
            return;

        // A record that was exported must always get a matching terminal packet.
        // Do not gate this on the cached same-town observer result: that cache can be stale
        // during the exact frame a guard shell is converted or a civilian is killed.
        bool alreadyTerminal = record.deathSent;
        record.deathSent = true;
        if (!alreadyTerminal && record.wasExported)
            CmdMobileNpcDisable(LocalPlayerId(), record.locationKey, record.npcId);

        localRecordsByNpc.Remove(npc);
        localRecordsByNetKey.Remove(MakeLocalNetKey(record.locationKey, record.npcId));
    }

    #endregion

    #region Local Owner Updates

    void UpdateLocalOwnerMobiles()
    {
        if (!CanUseLocalSync())
            return;

        List<MobilePersonNPC> remove = null;
        pendingMotionBatch.Clear();

        bool hasSameTownObserver = HasRemoteNpcObserverInSameTown();
        bool sendMotionThisTick = hasSameTownObserver && Time.time >= nextMotionBatchTime;
        if (sendMotionThisTick)
            nextMotionBatchTime = Time.time + Mathf.Max(0.10f, sendInterval);

        string motionLocationKey = sendMotionThisTick ? GetCurrentExteriorLocationKey() : string.Empty;

        foreach (KeyValuePair<MobilePersonNPC, LocalRecord> pair in localRecordsByNpc)
        {
            LocalRecord record = pair.Value;
            MobilePersonNPC npc = record.npc;

            // WeaponManager and guard conversion code can deactivate a Mobile NPC directly.
            // Check health before the active-state cleanup so a just-killed NPC is not silently
            // discarded as merely inactive before its death packet is exported.
            DaggerfallEntityBehaviour entityBehaviour = npc ? npc.GetComponent<DaggerfallEntityBehaviour>() : null;
            if (!record.deathSent && entityBehaviour && entityBehaviour.Entity != null && entityBehaviour.Entity.CurrentHealth <= 0)
            {
                record.deathSent = true;
                if (record.wasExported)
                    CmdMobileNpcDeath(LocalPlayerId(), record.locationKey, record.npcId);
                DeactivateLocalNpcAfterDeath(record);
                if (remove == null)
                    remove = new List<MobilePersonNPC>();
                remove.Add(pair.Key);
                continue;
            }

            if (!npc || !npc.Motor || !npc.Asset || !npc.Motor.gameObject.activeInHierarchy)
            {
                // Direct SetActive(false) does not necessarily raise PopulationManager's disable
                // event. This fallback is what removes converted guard shells on other clients.
                if (!record.deathSent && record.wasExported)
                {
                    record.deathSent = true;
                    CmdMobileNpcDisable(LocalPlayerId(), record.locationKey, record.npcId);
                }

                if (remove == null)
                    remove = new List<MobilePersonNPC>();
                remove.Add(pair.Key);
                continue;
            }

            bool forcedRemoteIdle = HasActiveRemoteIdleRequest(record);
            ApplyRemoteIdleToOwnedNpc(record, forcedRemoteIdle);
            if (!forcedRemoteIdle)
                record.remoteIdleAppliedAckSent = false;

            ForceLocalNpcVisible(npc);

            if (periodicSpawnRefresh && Time.time >= record.nextSpawnRefreshTime)
            {
                record.nextSpawnRefreshTime = Time.time + GetSpawnRefreshDelay();
                if (hasSameTownObserver)
                    SendSpawn(record);
            }

            if (!sendMotionThisTick || record.locationKey != motionLocationKey)
                continue;

            Vector3 pos = npc.Motor.transform.position;
            Quaternion rot = npc.Motor.transform.rotation;
            Vector3 target = forcedRemoteIdle ? pos : npc.Motor.TargetScenePosition;
            bool idle = forcedRemoteIdle || npc.Asset.IsIdle;
            int state = forcedRemoteIdle ? (int)MobilePersonMotor.MobileStates.Idle : (int)npc.Motor.CurrentState;

            bool moved = Vector3.Distance(record.lastSentPosition, pos) >= sendDistanceThreshold;
            bool rotated = Mathf.Abs(Mathf.DeltaAngle(
                record.lastSentRotation.eulerAngles.y,
                rot.eulerAngles.y)) >= sendRotationThreshold;
            bool targetChanged = Vector3.Distance(record.lastSentTarget, target) >= 0.1f;
            bool idleChanged = record.lastSentIdle != idle;
            bool stateChanged = record.lastSentState != state;

            // Preserve the smooth v10c packet schedule. Full movement state is cheap enough in
            // the fixed entry, so every emitted snapshot is self-contained after packet loss.
            if (!moved && !rotated && !targetChanged && !idleChanged && !stateChanged)
                continue;

            record.lastSentPosition = pos;
            record.lastSentRotation = rot;
            record.lastSentTarget = target;
            record.lastSentIdle = idle;
            record.lastSentState = state;
            record.nextMotionSequence = NextMotionSequence(record.nextMotionSequence);

            PendingMotionEntry entry = new PendingMotionEntry();
            entry.npcId = record.npcId;
            entry.position = pos;
            entry.yaw = PackYaw(rot);
            entry.targetPosition = target;
            entry.state = (byte)Mathf.Clamp(state, 0, MotionStateMask);
            entry.idle = idle;
            entry.motionSequence = record.nextMotionSequence;

            pendingMotionBatch.Add(entry);
        }

        if (pendingMotionBatch.Count > 0 && !string.IsNullOrEmpty(motionLocationKey))
            SendPendingMotionBatches(motionLocationKey);

        if (remove != null)
        {
            for (int i = 0; i < remove.Count; i++)
            {
                LocalRecord record;
                if (localRecordsByNpc.TryGetValue(remove[i], out record))
                    localRecordsByNetKey.Remove(MakeLocalNetKey(record.locationKey, record.npcId));
                localRecordsByNpc.Remove(remove[i]);
            }
        }
    }

    void SendPendingMotionBatches(string locationKey)
    {
        int maxBatch = Mathf.Clamp(compactMotionBatchSize, 1, 32);

        for (int offset = 0; offset < pendingMotionBatch.Count; offset += maxBatch)
        {
            int count = Mathf.Min(maxBatch, pendingMotionBatch.Count - offset);
            Vector3 minimum = pendingMotionBatch[offset].position;
            Vector3 maximum = minimum;
            for (int i = 0; i < count; i++)
            {
                PendingMotionEntry pending = pendingMotionBatch[offset + i];
                minimum = Vector3.Min(minimum, Vector3.Min(pending.position, pending.targetPosition));
                maximum = Vector3.Max(maximum, Vector3.Max(pending.position, pending.targetPosition));
            }

            // Centering the origin maximizes signed-short range in every direction.
            Vector3 batchOrigin = (minimum + maximum) * 0.5f;
            MotionBatchEntry[] entries = new MotionBatchEntry[count];
            for (int i = 0; i < count; i++)
            {
                PendingMotionEntry pending = pendingMotionBatch[offset + i];
                Vector3 positionOffset = pending.position - batchOrigin;
                Vector3 targetOffset = pending.targetPosition - batchOrigin;

                MotionBatchEntry entry = new MotionBatchEntry();
                entry.npcId = (ushort)Mathf.Clamp(pending.npcId, 1, ushort.MaxValue);
                entry.positionX = PackMotionOffset(positionOffset.x, MotionPositionScale);
                entry.positionY = PackMotionOffset(positionOffset.y, MotionPositionScale);
                entry.positionZ = PackMotionOffset(positionOffset.z, MotionPositionScale);
                entry.yaw = pending.yaw;
                entry.targetOffsetX = PackMotionOffset(targetOffset.x, MotionTargetScale);
                entry.targetOffsetY = PackMotionOffset(targetOffset.y, MotionTargetScale);
                entry.targetOffsetZ = PackMotionOffset(targetOffset.z, MotionTargetScale);
                entry.stateAndFlags = (byte)(pending.state | (pending.idle ? MotionFlagIdle : 0));
                entry.motionSequence = pending.motionSequence;
                entries[i] = entry;
            }

            CmdMobileNpcMotionBatch(locationKey, batchOrigin, entries);
        }
    }

    bool HasRemoteNpcObserverInSameTown()
    {
        if (!NetworkClient.isConnected || !IsLocalPlayerInExterior())
            return false;

        float now = Time.realtimeSinceStartup;
        if (now < nextTownObserverCheckRealtime)
            return cachedHasSameTownObserver;

        nextTownObserverCheckRealtime = now + 0.50f;
        cachedHasSameTownObserver = false;

        int localMapId = -1;
        if (GameManager.Instance && GameManager.Instance.PlayerGPS)
        {
            PlayerGPS gps = GameManager.Instance.PlayerGPS;
            if (gps.HasCurrentLocation && gps.IsPlayerInLocationRect)
                localMapId = gps.CurrentLocation.MapTableData.MapId;
        }

        if (localMapId <= 0)
            return false;

        MobileNpcSync[] players = GameObject.FindObjectsOfType<MobileNpcSync>();
        for (int i = 0; i < players.Length; i++)
        {
            MobileNpcSync other = players[i];
            if (!other || other == this || other.isLocalPlayer)
                continue;

            if (other.npcPopulationExteriorActive && other.npcPopulationLocationMapId == localMapId)
            {
                cachedHasSameTownObserver = true;
                break;
            }
        }

        return cachedHasSameTownObserver;
    }

    void SendSpawn(LocalRecord record, bool force = false)
    {
        if (record == null || !record.npc || !record.npc.Motor || !record.npc.Asset)
            return;

        // When no peer can see this town, identity refreshes and motion have no consumer. A peer
        // arriving later explicitly requests a forced spawn pass after publishing its MapID.
        if (!force && !HasRemoteNpcObserverInSameTown())
            return;

        MobilePersonNPC npc = record.npc;
        CmdMobileNpcSpawn(
            LocalPlayerId(),
            record.locationKey,
            record.npcId,
            npc.Motor.transform.position,
            npc.Motor.transform.rotation,
            (int)npc.Race,
            (int)npc.Gender,
            npc.PersonOutfitVariant,
            npc.IsGuard,
            npc.GetPersonFaceVariant(),
            npc.PersonFaceRecordId,
            npc.NameNPC ?? string.Empty,
            (int)npc.Motor.CurrentDirection,
            npc.Asset.IsIdle,
            npc.Motor.TargetScenePosition);

        record.wasExported = true;
    }

    float GetSpawnRefreshDelay()
    {
        float interval = Mathf.Max(30f, spawnRefreshInterval);
        return interval + Random.Range(0f, Mathf.Min(5f, interval * 0.5f));
    }

    void QueueAllLocalSpawnBurst(float delay, bool force = false)
    {
        if (!isLocalPlayer || !IsSessionSyncEnabled())
            return;

        float now = Time.realtimeSinceStartup;
        if (queuedLocalSpawnBurstCoroutine != null)
        {
            if (force)
                queuedLocalSpawnBurstForced = true;
            return;
        }

        if (now < nextAllowedLocalSpawnBurstRealtime)
            return;

        nextAllowedLocalSpawnBurstRealtime = now + Mathf.Max(0.5f, reciprocalSpawnBurstMinInterval);
        queuedLocalSpawnBurstForced = force;
        queuedLocalSpawnBurstCoroutine = StartCoroutine(SendAllLocalSpawnsBurstAfterDelay(Mathf.Max(0f, delay), force));
    }

    IEnumerator SendAllLocalSpawnsBurstAfterDelay(float delay, bool force)
    {
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);

        if (!CanUseLocalSync())
        {
            queuedLocalSpawnBurstCoroutine = null;
            queuedLocalSpawnBurstForced = false;
            yield break;
        }

        ScanExistingLocalMobiles();

        List<LocalRecord> records = new List<LocalRecord>(localRecordsByNpc.Values);
        if (records.Count == 0)
        {
            queuedLocalSpawnBurstCoroutine = null;
            queuedLocalSpawnBurstForced = false;
            yield break;
        }

        // One reliable pass is sufficient once the receiver explicitly announces that its
        // exterior is ready. Repeating the entire list only increases head-of-line pressure.
        for (int pass = 0; pass < 1; pass++)
        {
            int sentThisFrame = 0;
            for (int i = 0; i < records.Count; i++)
            {
                LocalRecord record = records[i];
                if (record != null && record.npc && localRecordsByNpc.ContainsKey(record.npc))
                {
                    SendSpawn(record, force || queuedLocalSpawnBurstForced);
                    sentThisFrame++;

                    // Keep large cities from submitting the entire reliable identity list to
                    // Mirror in one frame. This limits both host work and transport queue spikes.
                    if (sentThisFrame >= 16)
                    {
                        sentThisFrame = 0;
                        yield return null;
                    }
                }
            }

            yield return new WaitForSecondsRealtime(0.15f);
        }

        queuedLocalSpawnBurstCoroutine = null;
        queuedLocalSpawnBurstForced = false;
    }

    void HandleLocalPauseStateChange()
    {
        bool paused = GameManager.IsGamePaused;
        if (hasObservedLocalPauseState && paused == lastObservedLocalPauseState)
            return;

        hasObservedLocalPauseState = true;
        lastObservedLocalPauseState = paused;

        if (sendOwnerHeartbeat && NetworkClient.isConnected && localRecordsByNpc.Count > 0 &&
            HasRemoteNpcObserverInSameTown())
        {
            string locationKey = GetCurrentExteriorLocationKey();
            if (!string.IsNullOrEmpty(locationKey))
                CmdMobileNpcHeartbeat(LocalPlayerId(), locationKey, paused);
        }
    }

    IEnumerator OwnerHeartbeatLoop()
    {
        yield return new WaitForSecondsRealtime(0.75f);

        while (isLocalPlayer)
        {
            if (IsSessionSyncEnabled() && sendOwnerHeartbeat && NetworkClient.isConnected && IsLocalPlayerInExterior() &&
                localRecordsByNpc.Count > 0 && HasRemoteNpcObserverInSameTown())
            {
                string locationKey = GetCurrentExteriorLocationKey();
                if (!string.IsNullOrEmpty(locationKey))
                    CmdMobileNpcHeartbeat(LocalPlayerId(), locationKey, GameManager.IsGamePaused);
            }

            yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, ownerHeartbeatInterval));
        }
    }

    void RequestSameLocationSpawnRefresh(float delay)
    {
        if (!requestSpawnsOnExteriorLocationChange || !isLocalPlayer || !NetworkClient.isConnected)
            return;

        string locationKey = GetCurrentExteriorLocationKey();
        if (string.IsNullOrEmpty(locationKey))
            return;

        StartCoroutine(RequestSameLocationSpawnRefreshAfterDelay(locationKey, delay));
    }

    IEnumerator RequestSameLocationSpawnRefreshAfterDelay(string locationKey, float delay)
    {
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);

        if (!CanUseLocalSync())
            yield break;

        if (GetCurrentExteriorLocationKey() != locationKey)
            yield break;

        CmdMobileNpcRequestSpawnRefresh(string.Empty, locationKey, LocalPlayerId());
    }

    void RequestMissingOwnerSpawnRefresh(string ownerPlayerId, string locationKey, int npcId)
    {
        if (string.IsNullOrEmpty(ownerPlayerId) || string.IsNullOrEmpty(locationKey) || npcId <= 0 || !NetworkClient.isConnected)
            return;

        string key = ownerPlayerId + "#" + locationKey;
        float now = Time.realtimeSinceStartup;
        float next;
        if (nextMissingSpawnRequestRealtimeByOwner.TryGetValue(key, out next) && now < next)
            return;

        nextMissingSpawnRequestRealtimeByOwner[key] = now + 2.0f;
        CmdMobileNpcRequestSingleSpawn(ownerPlayerId, locationKey, npcId, LocalPlayerId());
    }

    void UpdateRemoteGhostDeaths()
    {
        if (remoteGhostsAreVisualOnly || !syncRemoteGhostDeaths || !CanUseLocalSync())
            return;

        List<string> killedKeys = null;
        foreach (KeyValuePair<string, RemoteRecord> pair in remoteGhosts)
        {
            RemoteRecord record = pair.Value;
            if (record == null || record.deathSent || record.disabled || !record.npc)
                continue;

            DaggerfallEntityBehaviour entityBehaviour = record.npc.GetComponent<DaggerfallEntityBehaviour>();
            if (entityBehaviour && entityBehaviour.Entity != null && entityBehaviour.Entity.CurrentHealth <= 0)
            {
                record.deathSent = true;
                CmdMobileNpcRemoteDeath(record.ownerPlayerId, record.locationKey, record.npcId);
                if (killedKeys == null)
                    killedKeys = new List<string>();
                killedKeys.Add(pair.Key);
            }
        }

        if (killedKeys != null)
        {
            for (int i = 0; i < killedKeys.Count; i++)
            {
                RemoteRecord record;
                if (remoteGhosts.TryGetValue(killedKeys[i], out record) && record != null)
                    RemoveRemoteGhost(record.ownerPlayerId, record.locationKey, record.npcId);
            }
        }
    }

    void UpdateMotionDiagnosticsFrame()
    {
        if (!enableVelocityMotionDiagnostics)
            return;

        float frameSeconds = Mathf.Max(0f, Time.unscaledDeltaTime);
        motionDiagnosticsFrames++;
        motionDiagnosticsMaximumFrameSeconds = Mathf.Max(motionDiagnosticsMaximumFrameSeconds, frameSeconds);
        if (frameSeconds >= 0.05f)
            motionDiagnosticsLongFrames++;
    }

    void RecordRemoteVisualDiagnostics(
        RemoteRecord record,
        Vector3 visualPosition,
        bool expectedMoving,
        float realNow)
    {
        if (!enableVelocityMotionDiagnostics || record == null)
            return;

        if (!record.diagnosticsHasVisualSample)
        {
            record.diagnosticsHasVisualSample = true;
            record.diagnosticsLastVisualPosition = visualPosition;
            record.diagnosticsLastVisualRealtime = realNow;
            record.diagnosticsLastVisualSpeed = 0f;
            return;
        }

        float sampleSeconds = realNow - record.diagnosticsLastVisualRealtime;
        if (sampleSeconds <= 0.0001f)
            return;

        float visualSpeed = Vector3.Distance(record.diagnosticsLastVisualPosition, visualPosition) / sampleSeconds;
        if (expectedMoving)
        {
            motionDiagnosticsMovingSamples++;
            bool speedDrop = record.diagnosticsLastVisualSpeed >= 0.45f && visualSpeed <= 0.15f;
            if (speedDrop)
            {
                motionDiagnosticsSpeedDrops++;
                if (Time.frameCount - record.diagnosticsLastMotionFrame <= 1)
                    motionDiagnosticsSpeedDropsOnPacketFrame++;

                float packetAge = Mathf.Max(0f, realNow - record.lastMotionReceiveRealtime);
                if (packetAge >= Mathf.Max(0f, steamMaxPredictionSeconds) - 0.01f)
                    motionDiagnosticsSpeedDropsAtPredictionLimit++;
                if (sampleSeconds >= 0.05f)
                    motionDiagnosticsSpeedDropsOnLongFrame++;
            }
        }

        record.diagnosticsLastVisualPosition = visualPosition;
        record.diagnosticsLastVisualRealtime = realNow;
        record.diagnosticsLastVisualSpeed = visualSpeed;
    }

    void FlushMotionDiagnosticsIfDue()
    {
        if (!enableVelocityMotionDiagnostics)
            return;

        float now = Time.realtimeSinceStartup;
        if (motionDiagnosticsWindowStartRealtime <= 0f)
            motionDiagnosticsWindowStartRealtime = now;

        float interval = Mathf.Max(2f, motionDiagnosticsInterval);
        float windowSeconds = now - motionDiagnosticsWindowStartRealtime;
        if (windowSeconds < interval)
            return;

        float averageGapMilliseconds = motionDiagnosticsGapSamples > 0
            ? motionDiagnosticsPacketGapTotal * 1000f / motionDiagnosticsGapSamples
            : 0f;
        float averageCorrectionCentimetres = motionDiagnosticsGapSamples > 0
            ? motionDiagnosticsCorrectionTotal * 100f / motionDiagnosticsGapSamples
            : 0f;

        Debug.Log(
            "[MobileNpcSync MotionDiag] " + windowSeconds.ToString("F1") + "s" +
            ": remote=" + remoteGhosts.Count +
            ", frames=" + motionDiagnosticsFrames +
            ", frameMax=" + (motionDiagnosticsMaximumFrameSeconds * 1000f).ToString("F1") + "ms" +
            ", frame>=50ms=" + motionDiagnosticsLongFrames +
            ", packets=" + motionDiagnosticsPackets +
            ", gapAvg=" + averageGapMilliseconds.ToString("F1") + "ms" +
            ", gapMax=" + (motionDiagnosticsMaximumPacketGap * 1000f).ToString("F1") + "ms" +
            ", gap>limit=" + motionDiagnosticsLatePackets +
            ", outOfOrder=" + motionDiagnosticsOutOfOrderPackets +
            ", estimatedLost=" + motionDiagnosticsEstimatedMissingSnapshots +
            ", correctionAvg=" + averageCorrectionCentimetres.ToString("F1") + "cm" +
            ", correctionMax=" + (motionDiagnosticsMaximumCorrection * 100f).ToString("F1") + "cm" +
            ", speedDrops=" + motionDiagnosticsSpeedDrops + "/" + motionDiagnosticsMovingSamples +
            " (packet=" + motionDiagnosticsSpeedDropsOnPacketFrame +
            ", limit=" + motionDiagnosticsSpeedDropsAtPredictionLimit +
            ", longFrame=" + motionDiagnosticsSpeedDropsOnLongFrame + ")" +
            ", idleEdges=" + motionDiagnosticsIdleEdges + ".");

        motionDiagnosticsWindowStartRealtime = now;
        motionDiagnosticsFrames = 0;
        motionDiagnosticsLongFrames = 0;
        motionDiagnosticsMaximumFrameSeconds = 0f;
        motionDiagnosticsPackets = 0;
        motionDiagnosticsGapSamples = 0;
        motionDiagnosticsPacketGapTotal = 0f;
        motionDiagnosticsMaximumPacketGap = 0f;
        motionDiagnosticsLatePackets = 0;
        motionDiagnosticsOutOfOrderPackets = 0;
        motionDiagnosticsEstimatedMissingSnapshots = 0;
        motionDiagnosticsCorrectionTotal = 0f;
        motionDiagnosticsMaximumCorrection = 0f;
        motionDiagnosticsMovingSamples = 0;
        motionDiagnosticsSpeedDrops = 0;
        motionDiagnosticsSpeedDropsOnPacketFrame = 0;
        motionDiagnosticsSpeedDropsAtPredictionLimit = 0;
        motionDiagnosticsSpeedDropsOnLongFrame = 0;
        motionDiagnosticsIdleEdges = 0;
    }

    #endregion

    #region Remote Ghost Updates

    void UpdateRemoteGhosts()
    {
        if (remoteGhosts.Count == 0)
            return;

        float realNow = Time.realtimeSinceStartup;
        List<string> removeKeys = null;

        foreach (KeyValuePair<string, RemoteRecord> pair in remoteGhosts)
        {
            RemoteRecord record = pair.Value;
            if (record == null || record.disabled || !record.gameObject)
            {
                if (removeKeys == null)
                    removeKeys = new List<string>();
                removeKeys.Add(pair.Key);
                continue;
            }

            if (ShouldRemoveGhostForMissingOwner(record, realNow))
            {
                if (removeKeys == null)
                    removeKeys = new List<string>();
                removeKeys.Add(pair.Key);
                continue;
            }

            if (IsNpcDead(record.npc))
                continue;

            ForceRemoteGhostVisible(record);

            bool localIdleWanted = ShouldRemoteGhostIdleForLocalPlayer(record);
            bool ownerIdleRequestWanted = ShouldRequestRemoteIdleWithSteamLead(record, localIdleWanted);
            MaybeSendRemoteIdleRequest(record, ownerIdleRequestWanted);
            record.localIdleOverride = localIdleWanted;

            bool ownerPausedFreeze = freezeRemoteGhostsWhenOwnerPaused && realNow < record.ownerPausedFreezeUntilRealtime;
            bool authoritativeIdle = record.networkIdle || record.state == (int)MobilePersonMotor.MobileStates.Idle;
            bool desiredIdleAnim = ownerPausedFreeze || authoritativeIdle;
            if (record.npc && record.npc.Asset && record.npc.Asset.IsIdle != desiredIdleAnim)
                record.npc.Asset.IsIdle = desiredIdleAnim;

            Transform t = record.gameObject.transform;

            // Do not count time spent in this client's pause towards remote prediction. This is
            // presentation-only state and is deliberately separate from the existing owner pause/idle logic.
            if (GameManager.IsGamePaused)
            {
                record.wasLocallyPaused = true;
                record.positionSmoothVelocity = Vector3.zero;
                record.predictionCorrectionOffset = Vector3.zero;
                if (record.hasResumeVisualOffset)
                    record.resumeVisualOffsetStartRealtime += Mathf.Max(0f, Time.unscaledDeltaTime);
            }
            else if (record.wasLocallyPaused)
            {
                record.wasLocallyPaused = false;
                record.lastMotionReceiveRealtime = realNow;
                record.positionSmoothVelocity = Vector3.zero;
                record.predictionCorrectionOffset = Vector3.zero;
            }

            if (!ownerPausedFreeze)
            {
                Vector3 desired = PredictRemotePosition(record, realNow);
                float error = Vector3.Distance(t.position, desired);

                if (error > snapDistance)
                {
                    t.position = desired;
                    record.positionSmoothVelocity = Vector3.zero;
                }
                else
                {
                    t.position = Vector3.SmoothDamp(
                        t.position,
                        desired,
                        ref record.positionSmoothVelocity,
                        Mathf.Max(0.01f, authoritativeIdle ? steamStopSmoothTime : steamPositionSmoothTime),
                        Mathf.Infinity,
                        Time.deltaTime);
                }

                t.rotation = Quaternion.Slerp(t.rotation, record.lastNetworkRotation, Mathf.Clamp01(Time.deltaTime * remoteSmoothing));
            }
            else
            {
                // Owner pause is authoritative presentation state. A local proximity request does
                // not freeze this ghost; it keeps walking until the owner's idle packet arrives.
                record.positionSmoothVelocity = Vector3.zero;
                if (record.hasResumeVisualOffset && !GameManager.IsGamePaused)
                    record.resumeVisualOffsetStartRealtime += Mathf.Max(0f, Time.unscaledDeltaTime);
            }

            bool expectedMoving = !ownerPausedFreeze && !authoritativeIdle && !GameManager.IsGamePaused;
            RecordRemoteVisualDiagnostics(record, t.position, expectedMoving, realNow);
        }

        if (removeKeys != null)
        {
            for (int i = 0; i < removeKeys.Count; i++)
            {
                RemoteRecord record;
                if (remoteGhosts.TryGetValue(removeKeys[i], out record) && record != null)
                    RemoveRemoteGhost(record.ownerPlayerId, record.locationKey, record.npcId);
                else
                    remoteGhosts.Remove(removeKeys[i]);
            }
        }
    }

    bool ShouldRemoveGhostForMissingOwner(RemoteRecord record, float realNow)
    {
        if (record == null)
            return true;

        // Fast path for disconnect/leave: if the player object that owns this civilian is no longer present,
        // this ghost has no valid source and will only walk in place forever. Remove it after a short grace.
        // Do not run FindObjectsOfType() for every ghost every frame; cache the presence check briefly.
        if (realNow >= record.nextOwnerPresenceCheckRealtime)
        {
            record.ownerPlayerObjectPresent = IsOwnerPlayerObjectPresent(record.ownerPlayerId);
            record.nextOwnerPresenceCheckRealtime = realNow + 0.5f;
        }

        if (!record.ownerPlayerObjectPresent)
        {
            if (record.ownerMissingSinceRealtime <= 0f)
                record.ownerMissingSinceRealtime = realNow;

            return realNow - record.ownerMissingSinceRealtime >= Mathf.Max(0.1f, ownerMissingCleanupDelay);
        }

        record.ownerMissingSinceRealtime = -1f;

        // Fallback: if the owner object still exists but packets stopped, cleanup eventually.
        // This covers missed disable messages or odd network states without using aggressive timeouts.
        float effectiveTimeout = ownerPacketTimeout;
        if (sendOwnerHeartbeat)
            effectiveTimeout = Mathf.Max(effectiveTimeout, Mathf.Max(10f, ownerHeartbeatInterval * 6f));

        if (record.lastSeenRealtime > 0f && effectiveTimeout > 0f && realNow - record.lastSeenRealtime >= effectiveTimeout)
            return true;

        return false;
    }

    bool IsOwnerPlayerObjectPresent(string ownerPlayerId)
    {
        if (string.IsNullOrEmpty(ownerPlayerId))
            return false;

        MobileNpcSync[] syncs = GameObject.FindObjectsOfType<MobileNpcSync>();
        for (int i = 0; i < syncs.Length; i++)
        {
            MobileNpcSync sync = syncs[i];
            if (!sync)
                continue;

            if (sync.netId != 0 && sync.netId.ToString() == ownerPlayerId)
                return true;
        }

        return false;
    }

    bool ShouldRequestRemoteIdleWithSteamLead(RemoteRecord record, bool localIdleWanted)
    {
        if (!localIdleOverrideForRemoteGhosts || record == null || !record.gameObject)
            return false;

        // The original distance/interaction predicate remains authoritative for the local
        // visual hold. Once it is true, keep the owner request renewed exactly as before.
        if (localIdleWanted)
        {
            record.preemptiveIdleRequestLatched = true;
            return true;
        }

        if (!GameManager.Instance || !GameManager.Instance.PlayerMotor || GameManager.Instance.PlayerEntity == null || !GameManager.Instance.WeaponManager)
        {
            record.preemptiveIdleRequestLatched = false;
            return false;
        }

        // Do not release an already-sent early request merely because this client opened a menu.
        if (GameManager.IsGamePaused)
            return record.preemptiveIdleRequestLatched;

        bool playerStandingStill = GameManager.Instance.PlayerMotor.IsStandingStill;
        bool sheathed = GameManager.Instance.WeaponManager.Sheathed;
        bool invisible = GameManager.Instance.PlayerEntity.IsInvisible;
        bool inBeastForm = GameManager.Instance.PlayerEntity.IsInBeastForm;
        bool wantsToStop = playerStandingStill && sheathed && !invisible && !inBeastForm;
        if (!wantsToStop || GameManager.Instance.AreEnemiesNearby())
        {
            record.preemptiveIdleRequestLatched = false;
            return false;
        }

        float distanceToPlayer = GameManager.Instance.PlayerMotor.DistanceToPlayer(record.gameObject.transform.position);
        float leadSeconds = Mathf.Max(0f, steamStopRequestLeadSeconds);
        Vector3 measuredVelocity = GetRemotePredictionVelocity(record);
        measuredVelocity.y = 0f;
        float maximumOwnerAhead = Mathf.Max(0f, steamStopOwnerAheadCompensationMax);
        // Keep the full walking-speed allowance after an idle acknowledgment clears measured
        // velocity. Otherwise an early stop near the outer edge can be released for one frame,
        // making the owner restart and stop a second time a few seconds later.
        float maximumLeadDistance = Mathf.Max(
            measuredVelocity.magnitude,
            Mathf.Max(0f, predictedMoveSpeed)) * leadSeconds + maximumOwnerAhead;

        // If the early request has already stopped the owner just outside the exact local
        // threshold, keep renewing it while the player remains in the normal stop state.
        if (record.preemptiveIdleRequestLatched)
        {
            // Once the owner has acknowledged idle, distance must not release the request. The
            // rendered ghost can be temporarily farther away while it settles to the owner's
            // authoritative stop point. Releasing here restarts the owner immediately, while this
            // client continues showing the idle pose until the next motion batch: a long idle slide.
            // The wantsToStop checks above still release normally when the player moves, draws a
            // weapon, becomes invisible/beast-form, or an enemy is nearby.
            if (record.networkIdle || record.state == (int)MobilePersonMotor.MobileStates.Idle)
                return true;

            if (distanceToPlayer <= localIdleDistance + maximumLeadDistance + 0.35f)
                return true;

            record.preemptiveIdleRequestLatched = false;
        }

        if (leadSeconds <= 0f || record.networkIdle || record.state == (int)MobilePersonMotor.MobileStates.Idle)
            return false;

        Vector3 directionToPlayer = GameManager.Instance.PlayerMotor.transform.position - record.gameObject.transform.position;
        directionToPlayer.y = 0f;
        if (measuredVelocity.sqrMagnitude < 0.0025f || directionToPlayer.sqrMagnitude < 0.0025f)
            return false;

        float closingSpeed = Vector3.Dot(measuredVelocity, directionToPlayer.normalized);
        if (closingSpeed <= 0f)
            return false;

        // Motion batches carry no cross-client clock value, so estimate the authoritative NPC's
        // current position from its most recent position and measured velocity. Add only the part
        // by which that estimate is ahead of the displayed ghost, with a hard cap. This asks the
        // owner early enough without locally freezing or moving the ghost to a guessed stop point.
        float packetAge = Mathf.Clamp(
            Time.realtimeSinceStartup - record.lastMotionReceiveRealtime,
            0f,
            Mathf.Max(0f, steamMaxPredictionSeconds));
        Vector3 estimatedOwnerPosition = record.lastNetworkPosition + measuredVelocity * packetAge;
        Vector3 ownerAhead = estimatedOwnerPosition - record.gameObject.transform.position;
        ownerAhead.y = 0f;
        float ownerAheadDistance = Mathf.Clamp(
            Vector3.Dot(ownerAhead, measuredVelocity.normalized),
            0f,
            maximumOwnerAhead);

        float preemptiveRequestDistance = localIdleDistance + closingSpeed * leadSeconds + ownerAheadDistance;
        if (distanceToPlayer >= preemptiveRequestDistance)
            return false;

        record.preemptiveIdleRequestLatched = true;
        return true;
    }

    void MaybeSendRemoteIdleRequest(RemoteRecord record, bool wantsIdle)
    {
        if (!localIdleOverrideForRemoteGhosts || record == null || record.disabled)
            return;

        // A false state is only a release edge. Distant observers that never requested a stop
        // must not send one reliable false Command per NPC forever.
        if (!wantsIdle && !record.lastIdleRequestSent)
            return;

        if (wantsIdle && record.lastIdleRequestSent && Time.time < record.nextIdleRequestTime)
            return;

        record.lastIdleRequestSent = wantsIdle;
        record.nextIdleRequestTime = wantsIdle
            ? Time.time + Mathf.Max(0.5f, remoteIdleRenewInterval)
            : 0f;
        CmdMobileNpcIdleRequest(record.ownerPlayerId, record.locationKey, record.npcId, LocalPlayerId(), wantsIdle);
    }

    Vector3 GetRemotePredictionVelocity(RemoteRecord record)
    {
        if (record == null)
            return Vector3.zero;

        float maximumSpeed = Mathf.Max(0.1f, predictedMoveSpeed) *
            Mathf.Max(1f, steamMeasuredVelocityMaxMultiplier);
        // A measured zero is meaningful (the owner really did not advance during that snapshot),
        // so do not replace it with path-target speed. The target fallback is only for the first
        // packet, before any position-derived velocity exists.
        if (record.hasNetworkVelocity)
            return Vector3.ClampMagnitude(record.networkVelocity, maximumSpeed);

        // The first moving packet has no earlier position from which to measure velocity. Use
        // its path direction only for this initial estimate; subsequent prediction is independent
        // of navigation-waypoint changes.
        Vector3 initialDirection = record.targetPosition - record.lastNetworkPosition;
        if (initialDirection.sqrMagnitude < 0.0025f)
            return Vector3.zero;

        return initialDirection.normalized * Mathf.Max(0f, predictedMoveSpeed);
    }

    void CaptureRemoteIdleVisualPosition(RemoteRecord record, Vector3 authoritativePosition)
    {
        if (record == null)
            return;

        // A motion-idle packet can arrive just before the reliable stop acknowledgment. Preserve
        // the first anchor so the acknowledgment cannot apply the same correction a second time.
        if (!record.hasIdleVisualPosition)
        {
            Vector3 displayedPosition = record.gameObject
                ? record.gameObject.transform.position
                : authoritativePosition;
            record.idleVisualPosition = Vector3.MoveTowards(
                displayedPosition,
                authoritativePosition,
                Mathf.Max(0f, steamStopMaxVisualCorrection));
            record.hasIdleVisualPosition = true;
        }

        record.resumeVisualOffset = Vector3.zero;
        record.resumeVisualOffsetStartRealtime = 0f;
        record.hasResumeVisualOffset = false;
    }

    void BeginRemoteResumeVisualOffset(
        RemoteRecord record,
        Vector3 authoritativePosition,
        float realNow)
    {
        if (record == null)
            return;

        Vector3 displayedStopPosition = record.hasIdleVisualPosition
            ? record.idleVisualPosition
            : (record.gameObject ? record.gameObject.transform.position : authoritativePosition);
        record.resumeVisualOffset = Vector3.ClampMagnitude(
            displayedStopPosition - authoritativePosition,
            Mathf.Max(0f, steamResumeVisualOffsetMaxCarry));
        record.resumeVisualOffsetStartRealtime = realNow;
        record.hasResumeVisualOffset = record.resumeVisualOffset.sqrMagnitude > 0.000001f;
        record.hasIdleVisualPosition = false;
    }

    Vector3 GetRemoteResumeVisualOffset(RemoteRecord record, float realNow)
    {
        if (record == null || !record.hasResumeVisualOffset)
            return Vector3.zero;

        float elapsed = Mathf.Max(0f, realNow - record.resumeVisualOffsetStartRealtime);
        float correctionDistance = Mathf.Max(0f, steamResumeVisualOffsetCorrectionSpeed) * elapsed;
        return Vector3.MoveTowards(record.resumeVisualOffset, Vector3.zero, correctionDistance);
    }

    Vector3 PredictRemotePosition(RemoteRecord record, float realNow)
    {
        if (record.networkIdle || record.state == (int)MobilePersonMotor.MobileStates.Idle)
            return record.hasIdleVisualPosition
                ? record.idleVisualPosition
                : record.lastNetworkPosition;

        // NetworkTime offsets can correct differently on two Steam clients. Measure only how
        // long this receiver has held the current snapshot, then cap stalls to avoid runaway drift.
        float elapsed = Mathf.Max(0f, realNow - record.lastMotionReceiveRealtime);
        elapsed = Mathf.Min(elapsed, Mathf.Max(0f, steamMaxPredictionSeconds));
        Vector3 predicted = record.lastNetworkPosition + GetRemotePredictionVelocity(record) * elapsed;
        predicted += GetRemoteResumeVisualOffset(record, realNow);

        // Do not force every normal 3-4 cm snapshot disagreement back to zero. That periodic
        // deceleration was visible as a one- or two-frame micro-stop. Keep a small bounded carry
        // while walking and remove only its excess at a slow world-space rate. An authoritative
        // idle packet clears this ordinary carry; its bounded visual stop anchor is handled above.
        Vector3 correction = Vector3.ClampMagnitude(
            record.predictionCorrectionOffset,
            Mathf.Max(0f, steamWalkingCorrectionMaxCarry));
        float correctionMagnitude = correction.magnitude;
        if (correctionMagnitude > 0.0001f)
        {
            float deadZone = Mathf.Min(
                correctionMagnitude,
                Mathf.Max(0f, steamWalkingCorrectionDeadZone));
            float excess = correctionMagnitude - deadZone;
            float remainingMagnitude = deadZone + Mathf.Max(
                0f,
                excess - Mathf.Max(0f, steamWalkingCorrectionSpeed) * elapsed);
            predicted += correction * (remainingMagnitude / correctionMagnitude);
        }

        return predicted;
    }

    bool ShouldRemoteGhostIdleForLocalPlayer(RemoteRecord record)
    {
        if (!localIdleOverrideForRemoteGhosts || record == null || !record.gameObject)
            return false;

        if (!GameManager.Instance || !GameManager.Instance.PlayerMotor || GameManager.Instance.PlayerEntity == null || !GameManager.Instance.WeaponManager)
            return false;

        if (GameManager.IsGamePaused)
            return record.localIdleOverride;

        float distanceToPlayer = GameManager.Instance.PlayerMotor.DistanceToPlayer(record.gameObject.transform.position);
        if (distanceToPlayer >= localIdleDistance)
            return false;

        bool playerStandingStill = GameManager.Instance.PlayerMotor.IsStandingStill;
        bool sheathed = GameManager.Instance.WeaponManager.Sheathed;
        bool invisible = GameManager.Instance.PlayerEntity.IsInvisible;
        bool inBeastForm = GameManager.Instance.PlayerEntity.IsInBeastForm;

        bool wantsToStop = playerStandingStill && sheathed && !invisible && !inBeastForm;
        if (!wantsToStop)
            return false;

        return !GameManager.Instance.AreEnemiesNearby();
    }

    #endregion

    #region Commands / RPCs

    [Command]
    void CmdSetNpcPopulationLocation(bool exteriorActive, int mapId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        ServerSetNpcPopulationLocation(exteriorActive, mapId);
    }

    [Server]
    void ServerSetNpcPopulationLocation(bool exteriorActive, int mapId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
        {
            npcPopulationExteriorActive = false;
            npcPopulationLocationMapId = -1;
            return;
        }

        npcPopulationExteriorActive = exteriorActive && mapId > 0;
        npcPopulationLocationMapId = npcPopulationExteriorActive ? mapId : -1;
    }

    [Command]
    void CmdMobileNpcSpawn(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        Vector3 position,
        Quaternion rotation,
        int race,
        int gender,
        int outfitVariant,
        bool isGuard,
        int faceVariant,
        int faceRecordId,
        string npcName,
        int direction,
        bool idle,
        Vector3 targetPosition)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        float sentTime = (float)NetworkTime.time;
        foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
        {
            if (!ServerConnectionSharesNpcTown(connection))
                continue;

            TargetMobileNpcSpawn(connection, ownerPlayerId, locationKey, npcId, position, rotation,
                race, gender, outfitVariant, isGuard, faceVariant, faceRecordId, npcName,
                direction, idle, targetPosition, sentTime);
        }
    }

    [Command(channel = Channels.Unreliable)]
    void CmdMobileNpcMotionBatch(
        string locationKey,
        Vector3 batchOrigin,
        MotionBatchEntry[] entries)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        if (entries == null || entries.Length == 0 || entries.Length > 32)
            return;

        foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
        {
            if (!ServerConnectionSharesNpcTown(connection))
                continue;

            TargetMobileNpcMotionBatch(connection, locationKey, batchOrigin, entries);
        }
    }

    [Server]
    bool ServerConnectionSharesNpcTown(NetworkConnectionToClient connection)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return false;

        if (connection == null || connection == connectionToClient || connection.identity == null)
            return false;

        MobileNpcSync receiver = connection.identity.GetComponent<MobileNpcSync>();
        if (!receiver || !npcPopulationExteriorActive || !receiver.npcPopulationExteriorActive)
            return false;

        return npcPopulationLocationMapId > 0 &&
               receiver.npcPopulationLocationMapId == npcPopulationLocationMapId;
    }

    [Command]
    void CmdMobileNpcDisable(string ownerPlayerId, string locationKey, int npcId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcDisable(ownerPlayerId, locationKey, npcId);
    }

    [Command]
    void CmdMobileNpcDeath(string ownerPlayerId, string locationKey, int npcId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcDeath(ownerPlayerId, locationKey, npcId);
    }

    [Command]
    void CmdMobileNpcRemoteDeath(string ownerPlayerId, string locationKey, int npcId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        // A non-owner killed their remote ghost. These are intentionally not authority-owned NetworkIdentities.
        RpcMobileNpcDeath(ownerPlayerId, locationKey, npcId);
    }

    [Command]
    void CmdMobileNpcWeaponRemoval(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        bool markDead,
        bool showBlood,
        Vector3 impactOffset,
        string reporterPlayerId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcWeaponRemoval(
            ownerPlayerId,
            locationKey,
            npcId,
            markDead,
            showBlood,
            impactOffset,
            reporterPlayerId);
    }

    [Command]
    void CmdMobileNpcIdleRequest(string ownerPlayerId, string locationKey, int npcId, string requesterPlayerId, bool wantsIdle)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcIdleRequest(ownerPlayerId, locationKey, npcId, requesterPlayerId, wantsIdle);
    }

    [Command]
    void CmdMobileNpcIdleApplied(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        Vector3 position,
        Quaternion rotation,
        byte motionSequence)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcIdleApplied(ownerPlayerId, locationKey, npcId, position, rotation, motionSequence);
    }

    [Command]
    void CmdMobileNpcOwnerLeftExterior(string ownerPlayerId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcOwnerLeftExterior(ownerPlayerId);
    }

    [Command]
    void CmdMobileNpcRequestSpawnRefresh(string ownerPlayerId, string locationKey, string requesterPlayerId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcRequestSpawnRefresh(ownerPlayerId, locationKey, requesterPlayerId);
    }

    [Command]
    void CmdMobileNpcRequestSingleSpawn(string ownerPlayerId, string locationKey, int npcId, string requesterPlayerId)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcRequestSingleSpawn(ownerPlayerId, locationKey, npcId, requesterPlayerId);
    }


    [Command]
    void CmdMobileNpcHeartbeat(string ownerPlayerId, string locationKey, bool ownerPaused)
    {
        if (!OptionsMultiplayer.mobileNpcSync)
            return;
        RpcMobileNpcHeartbeat(ownerPlayerId, locationKey, ownerPaused);
    }

    [ClientRpc]
    void RpcMobileNpcHeartbeat(string ownerPlayerId, string locationKey, bool ownerPaused)
    {
        if (ownerPlayerId == LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance.ShouldAcceptRemoteLocation(locationKey))
            return;

        float realNow = Time.realtimeSinceStartup;
        foreach (KeyValuePair<string, RemoteRecord> pair in remoteGhosts)
        {
            RemoteRecord record = pair.Value;
            if (record == null || record.disabled)
                continue;

            if (record.ownerPlayerId != ownerPlayerId || record.locationKey != locationKey)
                continue;

            record.lastSeenRealtime = realNow;
            record.ownerMissingSinceRealtime = -1f;
            record.ownerPlayerObjectPresent = true;
            record.nextOwnerPresenceCheckRealtime = realNow + 0.5f;

            if (ownerPaused && instance.freezeRemoteGhostsWhenOwnerPaused)
            {
                record.ownerPausedFreezeUntilRealtime = realNow + Mathf.Max(instance.ownerHeartbeatInterval + 0.25f, instance.ownerPausedFreezeGrace);
                record.predictionCorrectionOffset = Vector3.zero;
            }
            else
                record.ownerPausedFreezeUntilRealtime = 0f;
        }
    }

    [TargetRpc]
    void TargetMobileNpcSpawn(
        NetworkConnection target,
        string ownerPlayerId,
        string locationKey,
        int npcId,
        Vector3 position,
        Quaternion rotation,
        int race,
        int gender,
        int outfitVariant,
        bool isGuard,
        int faceVariant,
        int faceRecordId,
        string npcName,
        int direction,
        bool idle,
        Vector3 targetPosition,
        float sentTime)
    {
        if (ownerPlayerId == LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance.ShouldAcceptRemoteLocation(locationKey))
            return;

        if (instance.reciprocalSpawnBurstOnAcceptedRemoteSpawn)
        {
            // Respond once per remote owner/location, not once per NPC and not once per periodic
            // refresh. The old behaviour let two clients trigger complete spawn lists forever.
            string reciprocalKey = ownerPlayerId + "#" + locationKey;
            if (instance.reciprocalSpawnBurstKeys.Add(reciprocalKey))
                instance.QueueAllLocalSpawnBurst(0.25f, true);
        }

        SpawnPacket packet = new SpawnPacket();
        packet.ownerPlayerId = ownerPlayerId;
        packet.locationKey = locationKey;
        packet.npcId = npcId;
        packet.position = position;
        packet.rotation = rotation;
        packet.race = race;
        packet.gender = gender;
        packet.outfitVariant = outfitVariant;
        packet.isGuard = isGuard;
        packet.faceVariant = faceVariant;
        packet.faceRecordId = faceRecordId;
        packet.npcName = npcName;
        packet.direction = direction;
        packet.idle = idle;
        packet.targetPosition = targetPosition;

        string key = MakeGhostKey(ownerPlayerId, locationKey, npcId);
        if (instance.ShouldIgnoreRecentlyDisabledGhost(key))
            return;

        if (pendingGhostCreates.ContainsKey(key))
            return;

        pendingGhostCreates[key] = instance.StartCoroutine(instance.TryCreateRemoteGhost(packet, sentTime));
    }

    [TargetRpc(channel = Channels.Unreliable)]
    void TargetMobileNpcMotionBatch(
        NetworkConnection target,
        string locationKey,
        Vector3 batchOrigin,
        MotionBatchEntry[] entries)
    {
        string ownerPlayerId = netId != 0 ? netId.ToString() : string.Empty;
        if (string.IsNullOrEmpty(ownerPlayerId))
            return;

        if (ownerPlayerId == LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance.ShouldAcceptRemoteLocation(locationKey) || entries == null)
            return;

        int count = Mathf.Min(entries.Length, 32);
        for (int i = 0; i < count; i++)
            instance.ApplyRemoteMotionEntry(ownerPlayerId, locationKey, batchOrigin, entries[i]);
    }

    void ApplyRemoteMotionEntry(
        string ownerPlayerId,
        string locationKey,
        Vector3 batchOrigin,
        MotionBatchEntry entry)
    {
        string key = MakeGhostKey(ownerPlayerId, locationKey, entry.npcId);
        if (ShouldIgnoreRecentlyDisabledGhost(key))
            return;

        RemoteRecord record;
        if (!remoteGhosts.TryGetValue(key, out record) || record == null || !record.gameObject || record.disabled)
        {
            RequestMissingOwnerSpawnRefresh(ownerPlayerId, locationKey, entry.npcId);
            return;
        }

        if (IsNpcDead(record.npc))
            return;

        // Unreliable batches can arrive out of order. Each NPC keeps its own sequence so one
        // delayed batch cannot rewind a civilian while unrelated entries continue normally.
        if (record.hasAcceptedMotionSequence &&
            !IsNewerMotionSequence(entry.motionSequence, record.lastAcceptedMotionSequence))
        {
            if (enableVelocityMotionDiagnostics)
                motionDiagnosticsOutOfOrderPackets++;
            return;
        }

        Vector3 position = batchOrigin + new Vector3(
            UnpackMotionOffset(entry.positionX, MotionPositionScale),
            UnpackMotionOffset(entry.positionY, MotionPositionScale),
            UnpackMotionOffset(entry.positionZ, MotionPositionScale));
        Vector3 targetPosition = batchOrigin + new Vector3(
            UnpackMotionOffset(entry.targetOffsetX, MotionTargetScale),
            UnpackMotionOffset(entry.targetOffsetY, MotionTargetScale),
            UnpackMotionOffset(entry.targetOffsetZ, MotionTargetScale));
        int state = entry.stateAndFlags & MotionStateMask;
        bool idle = (entry.stateAndFlags & MotionFlagIdle) != 0;

        float receiveNow = Time.realtimeSinceStartup;
        bool hadAcceptedMotion = record.hasAcceptedMotionSequence;
        float packetGap = hadAcceptedMotion
            ? Mathf.Max(0.01f, receiveNow - record.lastMotionReceiveRealtime)
            : 0f;
        bool previousAuthoritativeIdle = record.networkIdle ||
            record.state == (int)MobilePersonMotor.MobileStates.Idle;
        bool authoritativeIdle = idle || state == (int)MobilePersonMotor.MobileStates.Idle;
        Vector3 previousPredictedPosition = hadAcceptedMotion
            ? PredictRemotePosition(record, receiveNow)
            : position;

        if (authoritativeIdle)
        {
            if (!previousAuthoritativeIdle)
                record.hasIdleVisualPosition = false;
            CaptureRemoteIdleVisualPosition(record, position);
        }
        else if (previousAuthoritativeIdle)
        {
            BeginRemoteResumeVisualOffset(record, position, receiveNow);
        }

        Vector3 updatedNetworkVelocity = record.networkVelocity;
        bool hasUpdatedNetworkVelocity = record.hasNetworkVelocity;
        if (authoritativeIdle)
        {
            updatedNetworkVelocity = Vector3.zero;
            hasUpdatedNetworkVelocity = false;
        }
        else if (hadAcceptedMotion)
        {
            Vector3 measuredVelocity = (position - record.lastNetworkPosition) / packetGap;
            float maximumMeasuredSpeed = Mathf.Max(0.1f, predictedMoveSpeed) *
                Mathf.Max(1f, steamMeasuredVelocityMaxMultiplier);
            measuredVelocity = Vector3.ClampMagnitude(measuredVelocity, maximumMeasuredSpeed);

            if (!hasUpdatedNetworkVelocity || previousAuthoritativeIdle)
            {
                updatedNetworkVelocity = measuredVelocity;
                hasUpdatedNetworkVelocity = true;
            }
            else
            {
                float velocityBlend = 1f - Mathf.Exp(
                    -Mathf.Max(0.01f, steamVelocitySmoothing) * packetGap);
                updatedNetworkVelocity = Vector3.Lerp(
                    updatedNetworkVelocity,
                    measuredVelocity,
                    velocityBlend);
                hasUpdatedNetworkVelocity = true;
            }
        }
        else
        {
            Vector3 initialDirection = targetPosition - position;
            if (initialDirection.sqrMagnitude >= 0.0025f)
            {
                updatedNetworkVelocity = initialDirection.normalized * Mathf.Max(0f, predictedMoveSpeed);
                hasUpdatedNetworkVelocity = true;
            }
        }

        if (enableVelocityMotionDiagnostics)
        {
            motionDiagnosticsPackets++;
            if (hadAcceptedMotion)
            {
                motionDiagnosticsGapSamples++;
                motionDiagnosticsPacketGapTotal += packetGap;
                motionDiagnosticsMaximumPacketGap = Mathf.Max(motionDiagnosticsMaximumPacketGap, packetGap);
                if (packetGap > Mathf.Max(0f, steamMaxPredictionSeconds))
                    motionDiagnosticsLatePackets++;

                int sequenceAdvance = unchecked((byte)(entry.motionSequence - record.lastAcceptedMotionSequence));
                if (sequenceAdvance > 1 && sequenceAdvance < 128)
                    motionDiagnosticsEstimatedMissingSnapshots += sequenceAdvance - 1;

                float correctionDistance = Vector3.Distance(previousPredictedPosition, position);
                motionDiagnosticsCorrectionTotal += correctionDistance;
                motionDiagnosticsMaximumCorrection = Mathf.Max(
                    motionDiagnosticsMaximumCorrection,
                    correctionDistance);
            }
        }

        record.hasAcceptedMotionSequence = true;
        record.lastAcceptedMotionSequence = entry.motionSequence;
        record.lastNetworkPosition = position;
        record.lastNetworkRotation = UnpackYaw(entry.yaw);
        record.targetPosition = targetPosition;
        record.state = state;
        record.networkIdle = idle;
        record.networkVelocity = updatedNetworkVelocity;
        record.hasNetworkVelocity = hasUpdatedNetworkVelocity;

        if (enableVelocityMotionDiagnostics && hadAcceptedMotion && authoritativeIdle != previousAuthoritativeIdle)
            motionDiagnosticsIdleEdges++;
        if (authoritativeIdle)
        {
            record.positionSmoothVelocity = Vector3.zero;
            record.predictionCorrectionOffset = Vector3.zero;
        }
        else if (hadAcceptedMotion)
        {
            // Preserve only a tightly bounded part of the old predicted position. PredictRemotePosition
            // holds the normal disagreement without slowing the NPC and removes only the excess.
            // A stop-position offset carried into resumed walking is handled separately, so remove
            // it before calculating this ordinary packet-continuity correction.
            Vector3 resumeVisualOffset = GetRemoteResumeVisualOffset(record, receiveNow);
            record.predictionCorrectionOffset = Vector3.ClampMagnitude(
                previousPredictedPosition - position - resumeVisualOffset,
                Mathf.Max(0f, steamWalkingCorrectionMaxCarry));
        }
        else
        {
            record.predictionCorrectionOffset = Vector3.zero;
        }

        record.lastMotionReceiveRealtime = receiveNow;
        record.lastSeenRealtime = receiveNow;
        record.ownerMissingSinceRealtime = -1f;
        record.ownerPlayerObjectPresent = true;
        record.nextOwnerPresenceCheckRealtime = receiveNow + 0.5f;
        record.ownerPausedFreezeUntilRealtime = 0f;
        record.disabled = false;
        record.diagnosticsLastMotionFrame = Time.frameCount;

        ForceRemoteGhostVisible(record);
    }

    [ClientRpc]
    void RpcMobileNpcDisable(string ownerPlayerId, string locationKey, int npcId)
    {
        if (ownerPlayerId == LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        string key = MakeGhostKey(ownerPlayerId, locationKey, npcId);
        instance.PrepareRemoteGhostTerminalRemoval(key);
        instance.RemoveRemoteGhost(ownerPlayerId, locationKey, npcId);
    }

    [ClientRpc]
    void RpcMobileNpcDeath(string ownerPlayerId, string locationKey, int npcId)
    {
        MobileNpcSync instance = localInstance ? localInstance : this;
        instance.ApplyTerminalMobileNpcRemoval(
            ownerPlayerId,
            locationKey,
            npcId,
            true,
            false,
            Vector3.zero,
            string.Empty);
    }

    [ClientRpc]
    void RpcMobileNpcWeaponRemoval(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        bool markDead,
        bool showBlood,
        Vector3 impactOffset,
        string reporterPlayerId)
    {
        MobileNpcSync instance = localInstance ? localInstance : this;
        instance.ApplyTerminalMobileNpcRemoval(
            ownerPlayerId,
            locationKey,
            npcId,
            markDead,
            showBlood,
            impactOffset,
            reporterPlayerId);
    }

    [ClientRpc]
    void RpcMobileNpcIdleRequest(string ownerPlayerId, string locationKey, int npcId, string requesterPlayerId, bool wantsIdle)
    {
        // This RPC is sent from the requester player's object, not the owner object's object.
        // Therefore ownership must be resolved by the explicit ownerPlayerId/local player id comparison.
        if (ownerPlayerId != LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance || !instance.IsSessionSyncEnabled())
            return;

        LocalRecord localRecord;
        if (!instance.localRecordsByNetKey.TryGetValue(MakeLocalNetKey(locationKey, npcId), out localRecord) || localRecord == null)
            return;

        if (string.IsNullOrEmpty(requesterPlayerId))
            return;

        if (wantsIdle)
        {
            float renewInterval = Mathf.Max(0.5f, instance.remoteIdleRenewInterval);
            float leaseSeconds = Mathf.Max(renewInterval + 0.5f, instance.remoteIdleLeaseSeconds);
            localRecord.remoteIdleUntilByRequester[requesterPlayerId] = Time.time + leaseSeconds;
        }
        else
        {
            // A requester can release only its own lease. Another player standing farther away
            // cannot restart an NPC stopped for the player beside it.
            localRecord.remoteIdleUntilByRequester.Remove(requesterPlayerId);
        }

        bool forcedRemoteIdle = instance.HasActiveRemoteIdleRequest(localRecord);
        instance.ApplyRemoteIdleToOwnedNpc(localRecord, forcedRemoteIdle);

        if (forcedRemoteIdle)
        {
            if (!localRecord.remoteIdleAppliedAckSent)
            {
                localRecord.remoteIdleAppliedAckSent = true;
                instance.SendImmediateOwnedNpcIdleApplied(localRecord);
            }
        }
        else
        {
            localRecord.remoteIdleAppliedAckSent = false;
        }
    }

    [ClientRpc]
    void RpcMobileNpcIdleApplied(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        Vector3 position,
        Quaternion rotation,
        byte motionSequence)
    {
        if (ownerPlayerId == LocalPlayerId())
            return;

        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance.ShouldAcceptRemoteLocation(locationKey))
        {
            RemoveRemoteGhost(ownerPlayerId, locationKey, npcId);
            return;
        }

        string key = MakeGhostKey(ownerPlayerId, locationKey, npcId);
        if (instance.ShouldIgnoreRecentlyDisabledGhost(key))
            return;

        RemoteRecord record;
        if (!remoteGhosts.TryGetValue(key, out record) || record == null || !record.gameObject || record.disabled)
        {
            instance.RequestMissingOwnerSpawnRefresh(ownerPlayerId, locationKey, npcId);
            return;
        }

        if (IsNpcDead(record.npc))
            return;

        // The acknowledgment owns a unique sequence number. Reject it if a newer owner
        // motion state already arrived, and reject older in-flight motion after accepting it.
        if (record.hasAcceptedMotionSequence &&
            !IsNewerMotionSequence(motionSequence, record.lastAcceptedMotionSequence))
            return;

        bool wasAuthoritativeIdle = record.networkIdle ||
            record.state == (int)MobilePersonMotor.MobileStates.Idle;
        if (!wasAuthoritativeIdle)
            record.hasIdleVisualPosition = false;
        instance.CaptureRemoteIdleVisualPosition(record, position);

        record.hasAcceptedMotionSequence = true;
        record.lastAcceptedMotionSequence = motionSequence;
        record.lastNetworkPosition = position;
        record.lastNetworkRotation = rotation;
        record.targetPosition = position;
        record.state = (int)MobilePersonMotor.MobileStates.Idle;
        record.networkIdle = true;
        record.positionSmoothVelocity = Vector3.zero;
        record.predictionCorrectionOffset = Vector3.zero;
        record.networkVelocity = Vector3.zero;
        record.hasNetworkVelocity = false;
        record.lastMotionReceiveRealtime = Time.realtimeSinceStartup;
        record.lastSeenRealtime = Time.realtimeSinceStartup;
        record.ownerMissingSinceRealtime = -1f;
        record.ownerPlayerObjectPresent = true;
        record.nextOwnerPresenceCheckRealtime = Time.realtimeSinceStartup + 0.5f;
        record.ownerPausedFreezeUntilRealtime = 0f;
        record.disabled = false;

        ForceRemoteGhostVisible(record);
    }

    [ClientRpc]
    void RpcMobileNpcOwnerLeftExterior(string ownerPlayerId)
    {
        if (string.IsNullOrEmpty(ownerPlayerId))
            return;

        // Sender already cleared its own exported records and received ghosts locally.
        if (ownerPlayerId == LocalPlayerId())
            return;

        RemoveRemoteGhostsForOwner(ownerPlayerId);
    }

    [ClientRpc]
    void RpcMobileNpcRequestSpawnRefresh(string ownerPlayerId, string locationKey, string requesterPlayerId)
    {
        // This RPC is sent on the requester player's network object. On receivers that object is not
        // their local player object, so route all checks/work through localInstance.
        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance || !instance.isLocalPlayer)
            return;

        if (requesterPlayerId == instance.LocalPlayerId())
            return;

        if (!instance.ShouldAcceptRemoteLocation(locationKey))
            return;

        // Empty owner id means: every player currently in this location should resend their own civilians.
        // Specific owner id means: only that owner should resend.
        if (!string.IsNullOrEmpty(ownerPlayerId) && ownerPlayerId != instance.LocalPlayerId())
            return;

        instance.QueueAllLocalSpawnBurst(0.10f, true);
    }

    [ClientRpc]
    void RpcMobileNpcRequestSingleSpawn(string ownerPlayerId, string locationKey, int npcId, string requesterPlayerId)
    {
        MobileNpcSync instance = localInstance ? localInstance : this;
        if (!instance || !instance.isLocalPlayer)
            return;

        if (requesterPlayerId == instance.LocalPlayerId())
            return;

        if (ownerPlayerId != instance.LocalPlayerId() || !instance.ShouldAcceptRemoteLocation(locationKey))
            return;

        LocalRecord record;
        if (instance.localRecordsByNetKey.TryGetValue(MakeLocalNetKey(locationKey, npcId), out record) &&
            record != null && record.npc)
        {
            instance.SendSpawn(record, true);
        }
    }

    #endregion

    #region Remote Ghost Creation

    IEnumerator TryCreateRemoteGhost(SpawnPacket packet, float sentTime)
    {
        string key = MakeGhostKey(packet.ownerPlayerId, packet.locationKey, packet.npcId);

        RemoteRecord existing;
        if (remoteGhosts.TryGetValue(key, out existing) && existing != null && existing.gameObject)
        {
            ApplySpawnToRemoteRecord(existing, packet, sentTime);
            pendingGhostCreates.Remove(key);
            yield break;
        }

        float endTime = Time.realtimeSinceStartup + Mathf.Max(1f, ghostCreateRetrySeconds);
        while (Time.realtimeSinceStartup < endTime)
        {
            // Stop retrying if the receiver changes location while this same-town targeted spawn
            // is still waiting for its local PopulationManager to finish loading.
            if (!ShouldAcceptRemoteLocation(packet.locationKey))
            {
                pendingGhostCreates.Remove(key);
                yield break;
            }

            if (remoteGhosts.TryGetValue(key, out existing) && existing != null && existing.gameObject)
            {
                ApplySpawnToRemoteRecord(existing, packet, sentTime);
                pendingGhostCreates.Remove(key);
                yield break;
            }

            PopulationManager manager = FindPopulationManager(packet.locationKey);
            if (manager)
            {
                CreateRemoteGhost(manager, packet, sentTime);
                pendingGhostCreates.Remove(key);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        pendingGhostCreates.Remove(key);

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Dropped remote ghost after retry timeout: " + key);
    }

    void CreateRemoteGhost(PopulationManager manager, SpawnPacket packet, float sentTime)
    {
        if (!DaggerfallUnity.Instance || !DaggerfallUnity.Instance.Option_MobileNPCPrefab)
            return;

        string key = MakeGhostKey(packet.ownerPlayerId, packet.locationKey, packet.npcId);
        RemoteRecord existing;
        if (remoteGhosts.TryGetValue(key, out existing) && existing != null && existing.gameObject)
        {
            ApplySpawnToRemoteRecord(existing, packet, sentTime);
            return;
        }

        GameObject go = GameObjectHelper.InstantiatePrefab(
            DaggerfallUnity.Instance.Option_MobileNPCPrefab.gameObject,
            "MobileNPC_RemoteSoftSync",
            manager.transform,
            packet.position);

        if (!go)
            return;

        go.transform.position = packet.position;
        go.transform.rotation = packet.rotation;
        go.SetActive(true);

        MobilePersonNPC npc = go.GetComponent<MobilePersonNPC>();
        MobilePersonMotor motor = go.GetComponent<MobilePersonMotor>();
        if (!npc || !motor)
        {
            Destroy(go);
            return;
        }

        motor.cityNavigation = manager.GetComponent<CityNavigation>();
        npc.Motor = motor;
        npc.Asset = motor.MobileAsset;

        // Remote ghosts are driven by this script. Do not let local player-distance/path logic move them differently.
        motor.enabled = false;

        npc.ApplySyncedPerson(
            (Races)packet.race,
            (Genders)packet.gender,
            packet.outfitVariant,
            packet.isGuard,
            packet.faceVariant,
            packet.faceRecordId,
            packet.npcName);

        if (npc.Asset)
        {
            npc.Asset.gameObject.SetActive(true);
            if (npc.Asset.IsIdle != packet.idle)
                npc.Asset.IsIdle = packet.idle;
            Vector2 size = npc.Asset.GetSize();
            if (Mathf.Abs(size.y - 2f) > 0.1f)
                npc.Asset.transform.localPosition = new Vector3(0, (size.y - 2f) * 0.52f, 0);
        }

        RemoteRecord record = new RemoteRecord();
        record.ownerPlayerId = packet.ownerPlayerId;
        record.locationKey = packet.locationKey;
        record.npcId = packet.npcId;
        record.gameObject = go;
        record.npc = npc;
        record.motor = motor;
        record.lastNetworkPosition = packet.position;
        record.lastNetworkRotation = packet.rotation;
        record.targetPosition = packet.targetPosition;
        record.networkIdle = packet.idle;
        record.state = packet.idle ? (int)MobilePersonMotor.MobileStates.Idle : (int)MobilePersonMotor.MobileStates.MovingForward;
        if (packet.idle)
        {
            record.idleVisualPosition = packet.position;
            record.hasIdleVisualPosition = true;
        }
        else
        {
            Vector3 initialDirection = packet.targetPosition - packet.position;
            if (initialDirection.sqrMagnitude >= 0.0025f)
            {
                record.networkVelocity = initialDirection.normalized * Mathf.Max(0f, predictedMoveSpeed);
                record.hasNetworkVelocity = true;
            }
        }
        record.packetTime = sentTime;
        record.lastMotionReceiveRealtime = Time.realtimeSinceStartup;
        record.lastSeenRealtime = Time.realtimeSinceStartup;
        record.ownerMissingSinceRealtime = -1f;
        record.ownerPlayerObjectPresent = true;
        record.nextOwnerPresenceCheckRealtime = Time.realtimeSinceStartup + 0.5f;

        CacheRemoteGhostComponents(record);
        remoteGhosts[key] = record;
        ForceRemoteGhostVisible(record);

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Created remote ghost " + key + " name=" + packet.npcName);
    }

    void ApplySpawnToRemoteRecord(RemoteRecord record, SpawnPacket packet, float sentTime)
    {
        // v4: Spawn refreshes are reliability hints only. Do not snap the existing ghost back to
        // the owner position and do not re-apply SetPerson(), because both reset visible motion/animation.
        if (record.disabled)
            record.disabled = false;

        record.lastSeenRealtime = Time.realtimeSinceStartup;
        record.ownerMissingSinceRealtime = -1f;
        record.ownerPlayerObjectPresent = true;
        record.nextOwnerPresenceCheckRealtime = Time.realtimeSinceStartup + 0.5f;

        // Do not update lastNetworkPosition/target/packetTime here. Motion packets own movement state.
        // Updating time from a spawn refresh without updating position caused visible pause/snap cycles.
        record.lastNetworkRotation = packet.rotation;
        record.networkIdle = packet.idle;
        if (packet.idle)
        {
            CaptureRemoteIdleVisualPosition(record, record.lastNetworkPosition);
            record.positionSmoothVelocity = Vector3.zero;
            record.predictionCorrectionOffset = Vector3.zero;
            record.networkVelocity = Vector3.zero;
            record.hasNetworkVelocity = false;
        }

        if (record.gameObject && !record.gameObject.activeSelf)
            record.gameObject.SetActive(true);

        if (record.npc && record.npc.Asset && record.npc.Asset.IsIdle != packet.idle)
            record.npc.Asset.IsIdle = packet.idle;

        ForceRemoteGhostVisible(record);
    }

    void RemoveRemoteGhost(string ownerPlayerId, string locationKey, int npcId, float delay = 0f)
    {
        string key = MakeGhostKey(ownerPlayerId, locationKey, npcId);
        RemoteRecord record;
        if (!remoteGhosts.TryGetValue(key, out record) || record == null)
            return;

        record.disabled = true;
        if (record.gameObject)
        {
            if (delay > 0f)
                Destroy(record.gameObject, delay);
            else
                Destroy(record.gameObject);
        }

        remoteGhosts.Remove(key);
    }

    static void RemoveAllRemoteGhosts()
    {
        disabledGhostMotionUntil.Clear();

        if (remoteGhosts.Count == 0)
            return;

        List<RemoteRecord> records = new List<RemoteRecord>(remoteGhosts.Values);
        remoteGhosts.Clear();

        for (int i = 0; i < records.Count; i++)
        {
            RemoteRecord record = records[i];
            if (record == null)
                continue;

            record.disabled = true;
            if (record.gameObject)
                Destroy(record.gameObject);
        }
    }

    static void RemoveRemoteGhostsForOwner(string ownerPlayerId)
    {
        if (string.IsNullOrEmpty(ownerPlayerId) || remoteGhosts.Count == 0)
            return;

        List<string> removeKeys = null;
        foreach (KeyValuePair<string, RemoteRecord> pair in remoteGhosts)
        {
            RemoteRecord record = pair.Value;
            if (record != null && record.ownerPlayerId == ownerPlayerId)
            {
                if (removeKeys == null)
                    removeKeys = new List<string>();
                removeKeys.Add(pair.Key);
            }
        }

        if (removeKeys == null)
            return;

        for (int i = 0; i < removeKeys.Count; i++)
        {
            RemoteRecord record;
            if (!remoteGhosts.TryGetValue(removeKeys[i], out record) || record == null)
                continue;

            record.disabled = true;
            if (record.gameObject)
                Destroy(record.gameObject);

            remoteGhosts.Remove(removeKeys[i]);
        }
    }

    #endregion

    #region Exterior Transition Cleanup

    void UpdateExteriorTransitionCleanup()
    {
        if (!clearGhostsWhenLeavingExterior)
            return;

        float now = Time.realtimeSinceStartup;
        if (now < nextExteriorStateCheckRealtime)
            return;

        nextExteriorStateCheckRealtime = now + Mathf.Max(0.05f, exteriorStateCheckInterval);

        bool isExterior = IsLocalPlayerInExterior();
        string currentLocationKey = isExterior ? GetCurrentExteriorLocationKey() : string.Empty;

        if (!hasExteriorState)
        {
            hasExteriorState = true;
            lastWasInExterior = isExterior;
            lastExteriorLocationKey = currentLocationKey;
            if (!isExterior)
                ClearLocalGhostsAndOwnedRecordsForNonExterior(false);
            else
            {
                QueueAllLocalSpawnBurst(0.75f);
                RequestSameLocationSpawnRefresh(1.0f);
            }
            return;
        }

        if (lastWasInExterior && !isExterior)
        {
            ClearLocalGhostsAndOwnedRecordsForNonExterior(true);
        }
        else if (!lastWasInExterior && isExterior)
        {
            // Returned from an interior/dungeon into an exterior. Announce fresh civilians and ask
            // already-present players in this same town to resend theirs.
            QueueAllLocalSpawnBurst(0.75f);
            RequestSameLocationSpawnRefresh(1.0f);
        }
        else if (isExterior && currentLocationKey != lastExteriorLocationKey)
        {
            // Exterior-to-exterior load/fast travel/save load. v8 only checked exterior vs non-exterior,
            // so stale records could survive and no same-town spawn request was sent.
            ClearLocalGhostsAndOwnedRecordsForNonExterior(true);
            QueueAllLocalSpawnBurst(0.75f);
            RequestSameLocationSpawnRefresh(1.0f);
        }
        else if (!isExterior)
        {
            // Safety net if the transition was missed because objects were disabled/re-enabled in an unusual order.
            // If we still have owned records while not in exterior, broadcast one clear before dropping them.
            if (localRecordsByNpc.Count > 0)
                ClearLocalGhostsAndOwnedRecordsForNonExterior(true);
            else if (remoteGhosts.Count > 0)
                RemoveAllRemoteGhosts();
        }

        lastWasInExterior = isExterior;
        lastExteriorLocationKey = currentLocationKey;
    }

    void ClearLocalGhostsAndOwnedRecordsForNonExterior(bool broadcastOwnerClear)
    {
        if (broadcastOwnerClear && NetworkClient.isConnected)
            CmdMobileNpcOwnerLeftExterior(LocalPlayerId());

        if (localRecordsByNpc.Count > 0)
        {
            foreach (KeyValuePair<MobilePersonNPC, LocalRecord> pair in localRecordsByNpc)
            {
                LocalRecord record = pair.Value;
                if (record != null && record.motorDisabledByRemoteIdle && record.npc && record.npc.Motor)
                    record.npc.Motor.enabled = true;
            }

            localRecordsByNpc.Clear();
            localRecordsByNetKey.Clear();
        }

        RemoveAllRemoteGhosts();
        StopAllPendingGhostCreates();
        nextMissingSpawnRequestRealtimeByOwner.Clear();
        reciprocalSpawnBurstKeys.Clear();

        if (queuedLocalSpawnBurstCoroutine != null)
        {
            StopCoroutine(queuedLocalSpawnBurstCoroutine);
            queuedLocalSpawnBurstCoroutine = null;
        }
        queuedLocalSpawnBurstForced = false;
        nextAllowedLocalSpawnBurstRealtime = 0f;
        nextMotionBatchTime = 0f;
        pendingMotionBatch.Clear();
        nextTownObserverCheckRealtime = 0f;
        cachedHasSameTownObserver = false;

        if (verboseLogging)
            Debug.Log("[MobileNpcSync] Cleared mobile NPC soft-sync state after leaving exterior. broadcast=" + broadcastOwnerClear);
    }

    void StopAllPendingGhostCreates()
    {
        if (pendingGhostCreates.Count == 0)
            return;

        foreach (KeyValuePair<string, Coroutine> pair in pendingGhostCreates)
        {
            if (pair.Value != null)
                StopCoroutine(pair.Value);
        }

        pendingGhostCreates.Clear();
    }

    bool IsLocalPlayerInExterior()
    {
        PlayerEnterExit playerEnterExit = null;

        if (PlayerMultiplayer.playerObject)
            playerEnterExit = PlayerMultiplayer.playerObject.GetComponent<PlayerEnterExit>();

        if (!playerEnterExit)
            playerEnterExit = GameObject.FindObjectOfType<PlayerEnterExit>();

        if (!playerEnterExit)
            return false;

        bool exteriorActive = playerEnterExit.ExteriorParent && playerEnterExit.ExteriorParent.activeSelf;
        bool interiorActive = playerEnterExit.InteriorParent && playerEnterExit.InteriorParent.activeSelf;
        bool dungeonActive = playerEnterExit.DungeonParent && playerEnterExit.DungeonParent.activeSelf;

        return exteriorActive && !interiorActive && !dungeonActive;
    }

    bool ShouldAcceptRemoteLocation(string locationKey)
    {
        if (!IsSessionSyncEnabled())
            return false;

        if (!strictRemoteLocationFiltering)
            return true;

        if (string.IsNullOrEmpty(locationKey))
            return false;

        if (!IsLocalPlayerInExterior())
            return false;

        string currentLocationKey = GetCurrentExteriorLocationKey();
        if (string.IsNullOrEmpty(currentLocationKey))
            return false;

        return currentLocationKey == locationKey;
    }

    string GetCurrentExteriorLocationKey()
    {
        if (!GameManager.Instance || !GameManager.Instance.StreamingWorld)
            return string.Empty;

        DaggerfallLocation location = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
        if (!location)
            return string.Empty;

        PopulationManager manager = location.GetComponent<PopulationManager>();
        if (manager)
            return BuildLocationKey(manager);

        int region = -1;
        if (GameManager.Instance.PlayerGPS)
            region = GameManager.Instance.PlayerGPS.CurrentRegionIndex;

        return region + "|" + location.gameObject.name;
    }

    #endregion

    #region Local NPC Control Helpers

    bool HasActiveRemoteIdleRequest(LocalRecord record)
    {
        if (record == null || record.remoteIdleUntilByRequester.Count == 0)
            return false;

        float now = Time.time;
        List<string> expiredRequesters = null;

        foreach (KeyValuePair<string, float> pair in record.remoteIdleUntilByRequester)
        {
            if (pair.Value > now)
                continue;

            if (expiredRequesters == null)
                expiredRequesters = new List<string>();
            expiredRequesters.Add(pair.Key);
        }

        if (expiredRequesters != null)
        {
            for (int i = 0; i < expiredRequesters.Count; i++)
                record.remoteIdleUntilByRequester.Remove(expiredRequesters[i]);
        }

        return record.remoteIdleUntilByRequester.Count > 0;
    }

    bool ShouldIgnoreRecentlyDisabledGhost(string key)
    {
        float until;
        if (!disabledGhostMotionUntil.TryGetValue(key, out until))
            return false;

        if (Time.realtimeSinceStartup < until)
            return true;

        disabledGhostMotionUntil.Remove(key);
        return false;
    }

    void PruneDisabledGhostMotionTombstones()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextDisabledGhostTombstoneCleanupRealtime)
            return;

        nextDisabledGhostTombstoneCleanupRealtime = now + 5f;
        if (disabledGhostMotionUntil.Count == 0)
            return;

        List<string> expired = null;
        foreach (KeyValuePair<string, float> pair in disabledGhostMotionUntil)
        {
            if (pair.Value > now)
                continue;

            if (expired == null)
                expired = new List<string>();
            expired.Add(pair.Key);
        }

        if (expired != null)
        {
            for (int i = 0; i < expired.Count; i++)
                disabledGhostMotionUntil.Remove(expired[i]);
        }
    }

    void SendImmediateOwnedNpcIdleApplied(LocalRecord record)
    {
        if (record == null || !record.npc || !record.npc.Motor || !record.npc.Asset)
            return;

        Vector3 position = record.npc.Motor.transform.position;
        Quaternion rotation = record.npc.Motor.transform.rotation;

        // Give the reliable acknowledgment its own ordering barrier so any unreliable walking
        // snapshots already in transit cannot make the ghost resume after this stop is accepted.
        record.nextMotionSequence = NextMotionSequence(record.nextMotionSequence);
        record.lastSentPosition = position;
        record.lastSentRotation = rotation;
        record.lastSentTarget = position;
        record.lastSentIdle = true;
        record.lastSentState = (int)MobilePersonMotor.MobileStates.Idle;

        CmdMobileNpcIdleApplied(
            LocalPlayerId(),
            record.locationKey,
            record.npcId,
            position,
            rotation,
            record.nextMotionSequence);
    }

    void ApplyRemoteIdleToOwnedNpc(LocalRecord record, bool forcedIdle)
    {
        if (record == null || !record.npc || !record.npc.Motor || !record.npc.Asset)
            return;

        if (forcedIdle)
        {
            if (record.npc.Motor.enabled)
            {
                record.npc.Motor.enabled = false;
                record.motorDisabledByRemoteIdle = true;
            }

            if (!record.npc.Asset.IsIdle)
                record.npc.Asset.IsIdle = true;
        }
        else if (record.motorDisabledByRemoteIdle)
        {
            record.npc.Motor.enabled = true;
            record.motorDisabledByRemoteIdle = false;
        }
    }

    /// <summary>
    /// Reports a weapon-driven terminal removal of either an owned local Mobile NPC or a
    /// non-owned remote ghost. Used for civilian death and guard-shell conversion.
    /// Returns false outside active multiplayer or when the NPC is not managed by this sync layer.
    /// </summary>
    public static bool TryReportWeaponMobileNpcRemoval(
        MobilePersonNPC npc,
        Vector3 impactPosition,
        bool markDead,
        bool showBloodOnReporter)
    {
        MobileNpcSync instance = localInstance;
        if (!instance || !instance.CanUseLocalSync() || !npc)
            return false;

        string ownerPlayerId = string.Empty;
        string locationKey = string.Empty;
        int npcId = 0;
        bool shouldSend = false;
        Vector3 impactOffset = impactPosition - npc.transform.position;

        LocalRecord localRecord;
        if (instance.localRecordsByNpc.TryGetValue(npc, out localRecord) && localRecord != null)
        {
            ownerPlayerId = instance.LocalPlayerId();
            locationKey = localRecord.locationKey;
            npcId = localRecord.npcId;
            shouldSend = localRecord.wasExported;

            if (showBloodOnReporter)
                ShowMobileNpcBlood(npc, impactPosition);

            localRecord.deathSent = true;
            DeactivateLocalNpc(localRecord, markDead);
            instance.localRecordsByNpc.Remove(npc);
            instance.localRecordsByNetKey.Remove(MakeLocalNetKey(locationKey, npcId));
        }
        else
        {
            string remoteKey = null;
            RemoteRecord remoteRecord = null;
            foreach (KeyValuePair<string, RemoteRecord> pair in remoteGhosts)
            {
                RemoteRecord candidate = pair.Value;
                if (candidate != null && candidate.npc == npc)
                {
                    remoteKey = pair.Key;
                    remoteRecord = candidate;
                    break;
                }
            }

            if (remoteRecord == null)
                return false;

            ownerPlayerId = remoteRecord.ownerPlayerId;
            locationKey = remoteRecord.locationKey;
            npcId = remoteRecord.npcId;
            shouldSend = true;

            if (showBloodOnReporter)
                ShowMobileNpcBlood(npc, impactPosition);

            remoteRecord.deathSent = true;
            instance.PrepareRemoteGhostTerminalRemoval(remoteKey);
            instance.RemoveRemoteGhost(ownerPlayerId, locationKey, npcId);
        }

        if (shouldSend)
        {
            instance.CmdMobileNpcWeaponRemoval(
                ownerPlayerId,
                locationKey,
                npcId,
                markDead,
                true,
                impactOffset,
                instance.LocalPlayerId());
        }

        return true;
    }

    public static void ShowMobileNpcBlood(MobilePersonNPC npc, Vector3 impactPosition)
    {
        if (!npc)
            return;

        GameObject go = npc.Motor ? npc.Motor.gameObject : npc.gameObject;
        if (!go)
            return;

        EnemyBlood blood = go.GetComponent<EnemyBlood>();
        if (!blood)
            blood = go.GetComponentInChildren<EnemyBlood>(true);
        if (!blood)
            blood = go.AddComponent<EnemyBlood>();

        blood.ShowBloodSplash(0, impactPosition);
    }

    void ApplyTerminalMobileNpcRemoval(
        string ownerPlayerId,
        string locationKey,
        int npcId,
        bool markDead,
        bool showBlood,
        Vector3 impactOffset,
        string reporterPlayerId)
    {
        bool isReporter = !string.IsNullOrEmpty(reporterPlayerId) &&
                          reporterPlayerId == LocalPlayerId();

        if (ownerPlayerId == LocalPlayerId())
        {
            LocalRecord localRecord;
            string localKey = MakeLocalNetKey(locationKey, npcId);
            if (localRecordsByNetKey.TryGetValue(localKey, out localRecord) && localRecord != null && localRecord.npc)
            {
                if (showBlood && !isReporter)
                    ShowMobileNpcBlood(localRecord.npc, localRecord.npc.transform.position + impactOffset);

                localRecord.deathSent = true;
                MobilePersonNPC npc = localRecord.npc;
                DeactivateLocalNpc(localRecord, markDead);
                localRecordsByNpc.Remove(npc);
                localRecordsByNetKey.Remove(localKey);
            }
            return;
        }

        string key = MakeGhostKey(ownerPlayerId, locationKey, npcId);
        PrepareRemoteGhostTerminalRemoval(key);

        RemoteRecord record;
        if (!remoteGhosts.TryGetValue(key, out record) || record == null)
            return;

        if (showBlood && !isReporter && record.npc)
            ShowMobileNpcBlood(record.npc, record.npc.transform.position + impactOffset);

        record.deathSent = true;
        RemoveRemoteGhost(ownerPlayerId, locationKey, npcId);
    }

    void PrepareRemoteGhostTerminalRemoval(string key)
    {
        if (string.IsNullOrEmpty(key))
            return;

        disabledGhostMotionUntil[key] =
            Time.realtimeSinceStartup + Mathf.Max(0.5f, disabledGhostMotionIgnoreSeconds);

        Coroutine pendingCreate;
        if (pendingGhostCreates.TryGetValue(key, out pendingCreate))
        {
            if (pendingCreate != null)
                StopCoroutine(pendingCreate);
            pendingGhostCreates.Remove(key);
        }
    }

    static void DeactivateLocalNpcAfterDeath(LocalRecord record)
    {
        DeactivateLocalNpc(record, true);
    }

    static void DeactivateLocalNpc(LocalRecord record, bool markDead)
    {
        if (record == null || !record.npc)
            return;

        if (markDead)
        {
            DaggerfallEntityBehaviour eb = record.npc.GetComponent<DaggerfallEntityBehaviour>();
            if (eb && eb.Entity != null)
                eb.Entity.SetHealth(-1);
        }

        GameObject go = record.npc.Motor ? record.npc.Motor.gameObject : record.npc.gameObject;
        if (!go)
            return;

        Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            colliders[i].enabled = false;

        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].enabled = false;

        go.SetActive(false);
    }

    #endregion

    #region Helpers

    bool CanUseLocalSync()
    {
        return IsSessionSyncEnabled() && isLocalPlayer && !saveLoadRecoveryInProgress &&
               NetworkClient.isConnected && IsLocalPlayerInExterior();
    }

    string LocalPlayerId()
    {
        // Use the local player NetworkIdentity id for stable cross-client keys.
        // When this method runs on a remote player object because of an RPC, localInstance still points
        // at the actual local player object, so this returns the receiver's local id rather than this object's id.
        if (localInstance && localInstance.netId != 0)
            return localInstance.netId.ToString();

        if (isLocalPlayer && netId != 0)
            return netId.ToString();

        return PlayerMultiplayer.id ?? string.Empty;
    }

    int GetNextNpcId(string locationKey)
    {
        int next;
        if (!nextLocalNpcIdByLocation.TryGetValue(locationKey, out next))
            next = 1;

        nextLocalNpcIdByLocation[locationKey] = next + 1;
        return next;
    }

    static ushort PackYaw(Quaternion rotation)
    {
        float normalized = Mathf.Repeat(rotation.eulerAngles.y, 360f) / 360f;
        return (ushort)Mathf.RoundToInt(normalized * ushort.MaxValue);
    }

    static byte NextMotionSequence(byte sequence)
    {
        return unchecked((byte)(sequence + 1));
    }

    static bool IsNewerMotionSequence(byte sequence, byte previous)
    {
        // At the 0.40 second cadence, half the byte range spans 51 seconds. Any packet delayed
        // longer than that is obsolete, so byte ordering safely saves one byte per NPC forever.
        byte forwardDistance = unchecked((byte)(sequence - previous));
        return forwardDistance != 0 && forwardDistance < 128;
    }

    static short PackMotionOffset(float offset, float scale)
    {
        int scaled = Mathf.RoundToInt(offset * scale);
        return (short)Mathf.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    static float UnpackMotionOffset(short packedOffset, float scale)
    {
        return packedOffset / scale;
    }

    static Quaternion UnpackYaw(ushort packedYaw)
    {
        float yaw = packedYaw * (360f / ushort.MaxValue);
        return Quaternion.Euler(0f, yaw, 0f);
    }

    static string MakeLocalNetKey(string locationKey, int npcId)
    {
        return locationKey + "#" + npcId;
    }

    static string MakeGhostKey(string ownerPlayerId, string locationKey, int npcId)
    {
        return ownerPlayerId + "#" + locationKey + "#" + npcId;
    }

    static string BuildLocationKey(PopulationManager manager)
    {
        if (!manager)
            return string.Empty;

        int region = -1;
        if (GameManager.Instance && GameManager.Instance.PlayerGPS)
            region = GameManager.Instance.PlayerGPS.CurrentRegionIndex;

        return region + "|" + manager.gameObject.name;
    }

    static PopulationManager FindPopulationManager(string locationKey)
    {
        PopulationManager[] managers = GameObject.FindObjectsOfType<PopulationManager>();
        for (int i = 0; i < managers.Length; i++)
        {
            if (BuildLocationKey(managers[i]) == locationKey)
                return managers[i];
        }

        return null;
    }

    static bool IsNpcDead(MobilePersonNPC npc)
    {
        if (!npc)
            return true;

        DaggerfallEntityBehaviour entityBehaviour = npc.GetComponent<DaggerfallEntityBehaviour>();
        return entityBehaviour && entityBehaviour.Entity != null && entityBehaviour.Entity.CurrentHealth <= 0;
    }

    static void ForceLocalNpcVisible(MobilePersonNPC npc)
    {
        if (!npc || !npc.Asset)
            return;

        npc.Asset.gameObject.SetActive(true);
    }

    static void ForceRemoteGhostVisible(RemoteRecord record)
    {
        if (record == null || !record.gameObject)
            return;

        if (!record.gameObject.activeSelf)
            record.gameObject.SetActive(true);

        if (record.npc && record.npc.Asset)
        {
            if (!record.npc.Asset.gameObject.activeSelf)
                record.npc.Asset.gameObject.SetActive(true);
        }

        CacheRemoteGhostComponents(record);

        Renderer[] renderers = record.cachedRenderers;
        for (int i = 0; renderers != null && i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer && !renderer.enabled)
                renderer.enabled = true;
        }

        SetRemoteGhostInteraction(record, !localInstance || !localInstance.remoteGhostsAreVisualOnly);
    }

    static void CacheRemoteGhostComponents(RemoteRecord record)
    {
        if (record == null || !record.gameObject)
            return;

        // GetComponentsInChildren<T>() allocates a new array. Do it once per ghost, never from
        // the per-frame update path after the cache has been populated.
        if (record.cachedRenderers == null)
            record.cachedRenderers = record.gameObject.GetComponentsInChildren<Renderer>(true);
        if (record.cachedColliders == null)
            record.cachedColliders = record.gameObject.GetComponentsInChildren<Collider>(true);
    }

    static void SetRemoteGhostInteraction(RemoteRecord record, bool enabled)
    {
        if (record == null)
            return;

        CacheRemoteGhostComponents(record);
        Collider[] colliders = record.cachedColliders;
        for (int i = 0; colliders != null && i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider && collider.enabled != enabled)
                collider.enabled = enabled;
        }
    }

    #endregion
}
