// Unofficial Come Sail Away multiplayer compatibility helper — v15l.
// For Come Sail Away by RedRoryOTheGlen:
// https://github.com/RedRoryOTheGlen/DFU-mods-released/tree/main/ComeSailAway
// Developed with reference to CSA's source; requires the original installed mod.
// Adds multiplayer boat, interaction, driving, and passenger synchronization.
// Requires Mirror weaving. Initializes automatically; no Inspector setup.
// Accesses CSA at runtime without a compile-time reference.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility;
using Mirror;
using UnityEngine;

public struct CSAMPSubscribe : NetworkMessage { public byte version; }
public struct CSAMPBoatState : NetworkMessage
{
    public uint owner, boat;
    public bool removed, driving, lights, sailing;
    public int hull, variant;
    public double worldX, worldZ;
    public float y;
    public Quaternion rotation, meshRotation;
    public Vector3 meshPosition;
    public Quaternion[] sailRotations, boomRotations;
    public bool[] stowed;
    public float rowX, rowZ, rowSpeed, turn;
}

// High-rate motion packet kept separate from the larger visual/animation state.
// This is adapter-owned data only; no Come Sail Away implementation is copied here.
public struct CSAMPBoatMotionState : NetworkMessage
{
    public uint owner, boat;
    public bool removed;
    public double worldX, worldZ;
    public float y;
    public Quaternion rotation, meshRotation;
    public Vector3 meshPosition;
}

public struct CSAMPDoorRequest : NetworkMessage
{
    public uint owner, boat;
    public ushort door;
    public bool opened;
}

public struct CSAMPDoorState : NetworkMessage
{
    public uint owner, boat;
    public ushort door;
    public bool opened;
}

public struct CSAMPDriveRequest : NetworkMessage
{
    public uint owner, boat;
    public bool release;
}

public struct CSAMPDriveState : NetworkMessage
{
    public uint owner, boat, driver;
    // 0 = authoritative lease state, 1 = not same party,
    // 2 = already controlled, 3 = unavailable.
    public byte result;
}

// Separate from ordinary player world-position updates; wire-compatible with v15i.
public struct CSAMPDeckPose : NetworkMessage
{
    public uint player, owner, boat;
    public bool attached;
    public Vector3 position;
    public Quaternion rotation;
}

[DefaultExecutionOrder(-32000)]
public sealed class ComeSailAwayMultiplayerCompatibility : MonoBehaviour
{
    const string Prefix = "[SailMP v15l] ";
    const float Units = 40f, VisibleRange = 1800f;
    const int BoatLimit = 16, PartLimit = 32;
    const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance;
    Type boatType, modType;
    FieldInfo allBoats, currentBoat, nativeDisembarking;
    MethodInfo spawnBoat, startSailing, stopSailing, stopSailingDelayed;
    Component mod;
    bool serverWasActive, clientWasActive, reportedBinding;
    float nextBind, nextSubscribe, nextMotionSample, nextVisualSample, nextPrune, nextWarning;
    uint sequence;
    readonly Dictionary<GameObject, LocalBoat> locals = new Dictionary<GameObject, LocalBoat>();
    readonly Dictionary<ulong, CachedBoat> cache = new Dictionary<ulong, CachedBoat>();
    readonly Dictionary<ulong, RemoteBoat> remotes = new Dictionary<ulong, RemoteBoat>();
    readonly Dictionary<int, NetworkConnection> viewers = new Dictionary<int, NetworkConnection>();
    readonly Dictionary<int, Budget> budgets = new Dictionary<int, Budget>();
    readonly Dictionary<ulong, Dictionary<ushort, bool>> serverDoorStates = new Dictionary<ulong, Dictionary<ushort, bool>>();
    readonly Dictionary<ulong, Dictionary<ushort, bool>> clientDoorStates = new Dictionary<ulong, Dictionary<ushort, bool>>();
    readonly Dictionary<ulong, uint> serverDrivers = new Dictionary<ulong, uint>();
    readonly Dictionary<ulong, uint> clientDrivers = new Dictionary<ulong, uint>();
    ulong activeRemoteDriveKey;
    RemoteBoat activeRemoteDrive;
    bool releasingRemoteDrive;
    ulong releasingRemoteDriveKey;

    // A remote driver is locally parented/re-seated to CSA's DrivePosition, but their
    // PlayerMultiplayer NetworkTransform is a separate network stream. Observers pin
    // the presentation proxy to the replicated boat's authored DrivePosition instead,
    // so the driver cannot wobble independently from the wheel.
    readonly Dictionary<uint, float> remoteDriverSavedMinDistance =
        new Dictionary<uint, float>();
    readonly HashSet<uint> remoteDriverPresentationActive =
        new HashSet<uint>();
    readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
    readonly HashSet<NetworkIdentity> scannedActors = new HashSet<NetworkIdentity>();
    FieldInfo nativeParentedObjects;
    MethodInfo nativeFixedUpdate;
    IList savedPhysicsBoats, emptyPhysicsBoats;
    Component physicsMod;
    bool reportedPhysicsIsolation;
    EventInfo swimSuppressionEvent;
    Delegate swimSuppressionHandler;

    // Non-owner passengers use DFU's normal moving-platform path. CSA raises this
    // camera/smooth-follower speed to 250 while the driver is attached to a real boat;
    // mirror that presentation-only setting for passengers on replicated boats.
    const float RemoteDeckFollowerSpeed = 250f;
    bool remoteDeckFollowerOverride;
    float remoteDeckFollowerSavedSpeed;
    float remoteDeckFollowerAppliedSpeed;

    // Pausing stops Unity FixedUpdate, so PlayerGroundMotor and PositionMultiplayer
    // stop advancing even though the remote ship owner can keep sailing. Only while
    // the local simulation is paused, preserve the passenger's deck-relative pose
    // from unscaled remote-boat updates. This path is never used during normal play.
    bool pausedDeckAttached;
    Transform pausedDeckPlatform;
    Vector3 pausedDeckLocalPosition;
    Quaternion pausedDeckPreviousRotation;

    // CSA normally parents grounded enemies to Boat.MeshObject. Mirror's NetworkTransform
    // in this project synchronizes localPosition/localRotation, so literal parenting on
    // only the authority owner would make observers interpret boat-relative coordinates
    // as root/world coordinates. Keep network enemies at scene root and reproduce the
    // moving-platform result on the player who owns the real/local CurrentBoat instead.
    const float PassengerClaimRetry = 0.75f;
    const float PassengerContextInterval = 0.10f;
    const int PassengerLeaveGraceFrames = 3;
    readonly Dictionary<uint, EnemyPassenger> enemyPassengers = new Dictionary<uint, EnemyPassenger>();

    sealed class EnemyPassenger
    {
        public NetworkIdentity identity;
        public DynamicEnemyAuthority authority;
        public EnemyWorldPosition world;
        public CharacterController controller;
        public bool claimRequested, claimedByShip;
        public float nextClaimRetry, nextContext;
        public int missedDeckFrames;
        public bool hasJoinAnchor, hasPreviousBoatPose;
        public Vector3 joinLocalPosition, previousBoatPosition;
        public Quaternion joinLocalRotation, previousBoatRotation;
    }

    sealed class LocalBoat
    {
        public uint id;
        public object native;
        public GameObject root;
        public bool hiddenForRemoteDriver;
        public bool wasActiveBeforeRemoteDriver;
        public float motionSent = -100f, visualSent = -100f;
        public CSAMPBoatMotionState lastMotion;
        public CSAMPBoatState lastVisual;
        public bool hasMotion;
    }
    sealed class CachedBoat
    {
        public CSAMPBoatState state;
        public CSAMPBoatMotionState motion;
        public bool hasMotion;
        public float received;
    }
    sealed class Budget { public float since, subscription; public int count; }
    sealed class RemoteBoat
    {
        public CSAMPBoatState state;
        public bool hasState;
        public CSAMPBoatMotionState previousMotion, latestMotion;
        public bool hasMotion, hasPreviousMotion;
        public float latestMotionReceived;
        public float motionInterval = 0.05f;
        public float motionReceivedRealtime, observedSpeed, nextCorrectionDiagnostic;
        public float received, retry;
        public object native;
        public GameObject root, mesh;
        public bool positioned, visualsApplied, driving, lights;
        public bool localDriving;
        public bool handoffWaitingForOwnerMotion;
        public float handoffWaitStarted;
    }

    sealed class RemoteDeckPose
    {
        public CSAMPDeckPose target;
        public Vector3 fromPosition;
        public Quaternion fromRotation;
        public float received, interval = 0.05f;
    }
    readonly Dictionary<uint, RemoteDeckPose> remoteDeckPoses =
        new Dictionary<uint, RemoteDeckPose>();
    readonly List<uint> expiredDeckPoses = new List<uint>();
    // Support is selected once before ANY replicated boat moves. Seam grace is
    // restricted to the same collider and two physics steps, never a broad tether.
    Transform supportedDeck;
    float supportSeenAt = -100f, nextSupportDiagnostic;
    int seamSupportSteps;
    float seamSupportUntil;
    float supportSeenRealtime = -100f, correctionSupportUntil;
    int correctionSupportSteps;

    // A failed CSA enumerator is captured by identity from its actual exception.
    // Never time out a healthy disembark coroutine (pause can legitimately delay it).
    readonly HashSet<ulong> loadReleasedLeases = new HashSet<ulong>();
    IEnumerator failedDisembark;
    bool recoveringSteering, boatLoadInProgress;
    bool BoatLoadSuspended
    {
        get { return boatLoadInProgress || (SaveLoadManager.HasInstance && SaveLoadManager.Instance.LoadInProgress); }
    }

    bool sentDeckAttachment;
    float nextDeckPose;

    void Awake()
    {
        FloatingOrigin.OnPositionUpdate += OnOriginShift;
        SaveLoadManager.OnStartLoad += OnBoatStartLoad;
        SaveLoadManager.OnLoad += OnBoatLoadComplete;
        Application.logMessageReceived += OnNativeSailingException;
        var cleanup = gameObject.AddComponent<CSAMPActorParentCleanup>();
        cleanup.Owner = this;
        NetworkServer.ReplaceHandler<CSAMPSubscribe>(Subscribe);
        NetworkServer.ReplaceHandler<CSAMPBoatState>(ReceiveOwnerState);
        NetworkServer.ReplaceHandler<CSAMPBoatMotionState>(ReceiveOwnerMotion);
        NetworkServer.ReplaceHandler<CSAMPDoorRequest>(ReceiveDoorRequest);
        NetworkServer.ReplaceHandler<CSAMPDriveRequest>(ReceiveDriveRequest);
        NetworkServer.ReplaceHandler<CSAMPDeckPose>(ReceiveDeckPoseFromPlayer);
        NetworkClient.ReplaceHandler<CSAMPBoatState>(ReceiveRemoteState);
        NetworkClient.ReplaceHandler<CSAMPBoatMotionState>(ReceiveRemoteMotion);
        NetworkClient.ReplaceHandler<CSAMPDoorState>(ReceiveDoorState);
        NetworkClient.ReplaceHandler<CSAMPDriveState>(ReceiveDriveState);
        NetworkClient.ReplaceHandler<CSAMPDeckPose>(ReceiveDeckPose);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindObjectOfType<ComeSailAwayMultiplayerCompatibility>() != null) return;
        var root = new GameObject("Come Sail Away MP Compatibility");
        DontDestroyOnLoad(root);
        root.AddComponent<ComeSailAwayMultiplayerCompatibility>();
    }

    void Update()
    {
        RestoreBoatPhysicsCollection();
        RemoveNetworkActorsFromBoatCleanup();
        // Mirror clears custom handlers on shutdown. Reinstall on each session transition.
        if (NetworkServer.active && !serverWasActive)
        {
            NetworkServer.ReplaceHandler<CSAMPSubscribe>(Subscribe);
            NetworkServer.ReplaceHandler<CSAMPBoatState>(ReceiveOwnerState);
            NetworkServer.ReplaceHandler<CSAMPBoatMotionState>(ReceiveOwnerMotion);
            NetworkServer.ReplaceHandler<CSAMPDoorRequest>(ReceiveDoorRequest);
            NetworkServer.ReplaceHandler<CSAMPDriveRequest>(ReceiveDriveRequest);
            NetworkServer.ReplaceHandler<CSAMPDeckPose>(ReceiveDeckPoseFromPlayer);
        }
        if (NetworkClient.active && !clientWasActive)
        {
            NetworkClient.ReplaceHandler<CSAMPBoatState>(ReceiveRemoteState);
            NetworkClient.ReplaceHandler<CSAMPBoatMotionState>(ReceiveRemoteMotion);
            NetworkClient.ReplaceHandler<CSAMPDoorState>(ReceiveDoorState);
            NetworkClient.ReplaceHandler<CSAMPDriveState>(ReceiveDriveState);
            NetworkClient.ReplaceHandler<CSAMPDeckPose>(ReceiveDeckPose);
        }
        if (!NetworkServer.active && serverWasActive) { cache.Clear(); viewers.Clear(); budgets.Clear(); serverDoorStates.Clear(); serverDrivers.Clear(); }
        if (!NetworkClient.active && clientWasActive) { loadReleasedLeases.Clear(); StopRemoteDriveLocal(false); RestoreAllHiddenOwnedBoats(); RestoreAllRemoteDriverPresentationOverrides(); releasingRemoteDrive = false; releasingRemoteDriveKey = 0; ClearPausedDeckAttachment(); RestoreRemoteDeckFollowerSpeed(); ClearRemotes(); remoteDeckPoses.Clear(); sentDeckAttachment = false; locals.Clear(); clientDoorStates.Clear(); clientDrivers.Clear(); ClearEnemyPassengerState(); }
        serverWasActive = NetworkServer.active;
        clientWasActive = NetworkClient.active;
        if (!NetworkClient.active && !NetworkServer.active) return;

        if (Time.unscaledTime >= nextBind)
        {
            nextBind = Time.unscaledTime + 2f;
            Bind();
            BindPassengerSwimming();
        }
        if (NetworkClient.ready && ModAvailable() && Time.unscaledTime >= nextSubscribe)
        {
            nextSubscribe = Time.unscaledTime + 5f;
            NetworkClient.Send(new CSAMPSubscribe { version = 5 });
        }

        RecoverFailedDisembark();
        if (BoatLoadSuspended) return;
        UpdateDriveLeaseClient();

        bool motionDue = NetworkClient.ready && Time.unscaledTime >= nextMotionSample;
        bool visualDue = NetworkClient.ready && Time.unscaledTime >= nextVisualSample;
        if (motionDue) nextMotionSample = Time.unscaledTime + 0.05f;   // 20 Hz compact motion
        if (visualDue) nextVisualSample = Time.unscaledTime + 0.20f;  // 5 Hz full visual state
        if (motionDue || visualDue)
        {
            try { Publish(motionDue, visualDue); }
            catch (Exception e) { Warn("Sampling boat: " + e.GetBaseException().Message); }
        }
        if (NetworkServer.active && Time.unscaledTime >= nextPrune)
        {
            nextPrune = Time.unscaledTime + 1f;
            PruneServer();
        }

        // Replicated boats are intentionally not advanced here. DFU's actual
        // moving-platform compensation runs in PlayerMotor.FixedUpdate(), so moving
        // the deck on Update() creates two clocks: a render-frame deck and a
        // physics-frame passenger. Advance remote boats in our early FixedUpdate()
        // instead so PlayerGroundMotor consumes the same platform step.

        UpdateRemoteDeckFollowerSpeed();
    }

    void LateUpdate()
    {
        if (BoatLoadSuspended) return;
        // CSA moves the locally-controlled real boat (and its rocking MeshObject)
        // during its normal Update(). Native Transform parenting therefore carries
        // an SP enemy every rendered frame, not only on physics ticks.
        //
        // Keep the network enemy at scene root, but reproduce that same result here
        // after all Update() calls have completed. EnemyMotor can still move during
        // FixedUpdate; CarryEnemyPassengersPerFrame() preserves that current relative
        // movement and adds only the boat-frame delta accumulated since last render.
        if (NetworkClient.active && enemyPassengers.Count != 0)
            CarryEnemyPassengersPerFrame();

        // FixedUpdate does not run while local time is paused. Keep only remote-ship
        // passengers attached during that condition, using unscaled snapshots. This
        // does not touch PlayerAdvanced during ordinary gameplay.
        UpdatePausedRemoteDeckPassenger();
    }

    bool ModAvailable()
    {
        var behaviour = mod as Behaviour;
        return behaviour != null && behaviour.isActiveAndEnabled && spawnBoat != null;
    }

    void FixedUpdate()
    {
        RestoreBoatPhysicsCollection();
        if (BoatLoadSuspended) return;

        // This helper has execution order -32000, so this runs before the normal
        // PlayerMotor.FixedUpdate(). Advance the replicated ship on the SAME physics
        // timeline used by PlayerGroundMotor. Carry a supported observer passenger
        // with that step before normal walking, avoiding a sweep against the moved hull.
        if (NetworkClient.active && remotes.Count != 0)
            UpdateRemoteBoatsPhysics();

        if (!NetworkServer.active && !NetworkClient.active) return;
        if (mod == null && Time.unscaledTime >= nextBind)
        {
            nextBind = Time.unscaledTime + 2f;
            Bind();
        }
        if (!ModAvailable())
        {
            ClearEnemyPassengerState();
            return;
        }

        var boats = allBoats.GetValue(mod) as IList;
        if (boats == null || boats.Count == 0) return;
        // Run before the installed mod's FixedUpdate. Network roots and the
        // enemy-model children used by player avatars both need protection.
        scannedActors.Clear();
        if (NetworkServer.active)
            foreach (var identity in NetworkServer.spawned.Values) GuardActor(identity);
        if (NetworkClient.active)
            foreach (var identity in NetworkClient.spawned.Values) GuardActor(identity);
        if (nativeFixedUpdate == null) return;
        // Suspend just the automatic MonoBehaviour physics dispatch. Its event
        // subscriptions still run, with the REAL boat list intact, so world-origin
        // updates cannot miss boats. The post-driver invokes its physics method once.
        physicsMod = mod;
        ((Behaviour)physicsMod).enabled = false;
        if (!reportedPhysicsIsolation)
        {
            reportedPhysicsIsolation = true;
            Debug.Log(Prefix + "Boat physics isolated; real boat list retained for world-origin events.");
        }
    }

    internal void FinishBoatPhysics()
    {
        if (physicsMod == null) { RestoreBoatPhysicsCollection(); return; }
        try
        {
            if (!NetworkServer.active && !NetworkClient.active) return;
            if (!physicsMod.gameObject.activeInHierarchy) return;
            savedPhysicsBoats = allBoats.GetValue(physicsMod) as IList;
            if (savedPhysicsBoats == null) return;
            if (emptyPhysicsBoats == null)
                emptyPhysicsBoats = (IList)Activator.CreateInstance(allBoats.FieldType);
            // No frame or other FixedUpdate executes between swap and restore.
            // Native wave/current/boat physics runs; native literal actor parenting is skipped.
            allBoats.SetValue(physicsMod, emptyPhysicsBoats);
            nativeFixedUpdate.Invoke(physicsMod, null);

            // Boarding/authority detection remains on the physics cadence, matching
            // CSA's own enemy scan. Actual passenger carrying is deliberately NOT done
            // here: CSA moves the boat in Update(), so carrying in FixedUpdate produces
            // a visibly stepped enemy on an otherwise frame-smooth ship.
            ScanEnemyPassengers();
        }
        catch (Exception e) { Warn("Boat physics: " + e.GetBaseException().Message); }
        finally { RestoreBoatPhysicsCollection(); }
    }

    internal void RestoreBoatPhysicsCollection()
    {
        try
        {
            if (savedPhysicsBoats != null && physicsMod != null && allBoats != null &&
                ReferenceEquals(allBoats.GetValue(physicsMod), emptyPhysicsBoats))
            {
                foreach (object boat in emptyPhysicsBoats)
                    if (!savedPhysicsBoats.Contains(boat)) savedPhysicsBoats.Add(boat);
                allBoats.SetValue(physicsMod, savedPhysicsBoats);
            }
        }
        finally
        {
            savedPhysicsBoats = null;
            if (emptyPhysicsBoats != null) emptyPhysicsBoats.Clear();
            var behaviour = physicsMod as Behaviour;
            physicsMod = null;
            if (behaviour != null) behaviour.enabled = true;
        }
    }

    void ScanEnemyPassengers()
    {
        if (!NetworkClient.active || !NetworkClient.ready || currentBoat == null || mod == null)
        {
            ClearEnemyPassengerState();
            return;
        }

        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer == null || !localPlayer.isLocalPlayer)
            return;

        object boat = currentBoat.GetValue(mod);
        if (boat == null)
        {
            ReleaseAllEnemyPassengers(localPlayer.netId);
            return;
        }

        GameObject meshObject = Read<GameObject>(boat, "MeshObject");
        MeshCollider meshCollider = Read<MeshCollider>(boat, "MeshCollider");
        if (meshObject == null || meshCollider == null ||
            !meshObject.activeInHierarchy || !meshCollider.enabled)
        {
            ReleaseAllEnemyPassengers(localPlayer.netId);
            return;
        }

        Transform boatFrame = meshObject.transform;
        uint localNetId = localPlayer.netId;

        // CSA moves the boat through Transform changes in Update(). Synchronize those
        // changes before the physics-cadence deck raycasts when auto-sync is disabled.
        Physics.SyncTransforms();

        // Snapshot because deaths/unspawns/authority changes can mutate Mirror collections.
        List<NetworkIdentity> identities = new List<NetworkIdentity>(NetworkClient.spawned.Values);
        HashSet<uint> visited = new HashSet<uint>();

        for (int i = 0; i < identities.Count; i++)
        {
            NetworkIdentity identity = identities[i];
            if (!IsEligibleEnemyPassenger(identity))
                continue;

            DynamicEnemyAuthority authority = identity.GetComponent<DynamicEnemyAuthority>();
            CharacterController controller = identity.GetComponent<CharacterController>();
            if (authority == null || controller == null || !controller.enabled)
                continue;

            uint netId = identity.netId;
            EnemyPassenger passenger;
            enemyPassengers.TryGetValue(netId, out passenger);

            bool onDeck = IsStandingOnCurrentBoat(identity.transform, controller, meshCollider);
            if (!onDeck && passenger == null)
                continue;

            visited.Add(netId);

            if (passenger == null)
            {
                passenger = new EnemyPassenger
                {
                    identity = identity,
                    authority = authority,
                    world = identity.GetComponent<EnemyWorldPosition>(),
                    controller = controller,
                };
                enemyPassengers.Add(netId, passenger);
                CapturePassengerJoinAnchor(passenger, boatFrame);

                Debug.Log(Prefix + "Enemy reached local boat deck: '" + identity.name +
                    "' net=" + netId + "; requesting temporary ship ownership.");
            }
            else
            {
                passenger.identity = identity;
                passenger.authority = authority;
                passenger.controller = controller;
                if (passenger.world == null)
                    passenger.world = identity.GetComponent<EnemyWorldPosition>();
            }

            passenger.missedDeckFrames = onDeck ? 0 : passenger.missedDeckFrames + 1;

            uint persistentOwner = authority.PersistentOwnerPlayerNetId;

            // Do not fight another player's bound actor.
            if (persistentOwner != 0 && persistentOwner != localNetId)
            {
                enemyPassengers.Remove(netId);
                continue;
            }

            if (persistentOwner == 0)
            {
                if (onDeck && Time.unscaledTime >= passenger.nextClaimRetry)
                {
                    passenger.claimRequested = true;
                    passenger.nextClaimRetry = Time.unscaledTime + PassengerClaimRetry;
                    authority.CmdSetPersistentOwner(true);
                }

                // If the enemy stepped off before the reliable claim reached the server,
                // send a matching release after it. Mirror command ordering guarantees
                // that a late successful claim cannot remain stuck after this cancellation.
                if (!onDeck && passenger.missedDeckFrames >= PassengerLeaveGraceFrames)
                {
                    if (passenger.claimRequested)
                        authority.CmdSetPersistentOwner(false);
                    enemyPassengers.Remove(netId);
                }
                continue;
            }

            // The server has granted this local player persistent authority. If this
            // helper requested it, remember that we are responsible for releasing it.
            if (passenger.claimRequested)
                passenger.claimedByShip = true;

            if (!authority.hasAuthority)
                continue;

            if (!passenger.hasPreviousBoatPose)
            {
                // Authority can arrive after several boat frames. Reconstruct where the
                // enemy would now be if CSA had parented it from the original deck contact.
                if (passenger.claimedByShip && passenger.hasJoinAnchor)
                {
                    Vector3 desiredPosition = boatFrame.TransformPoint(passenger.joinLocalPosition);
                    Quaternion desiredRotation = boatFrame.rotation * passenger.joinLocalRotation;
                    identity.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
                    Physics.SyncTransforms();
                }

                passenger.hasPreviousBoatPose = true;
                passenger.previousBoatPosition = boatFrame.position;
                passenger.previousBoatRotation = boatFrame.rotation;

                Debug.Log(Prefix + "Enemy passenger active: '" + identity.name +
                    "' net=" + netId + " owner=" + localNetId + ".");
            }

            PublishPassengerWorldContext(passenger);

            if (!onDeck && passenger.missedDeckFrames >= PassengerLeaveGraceFrames)
            {
                ReleaseEnemyPassenger(passenger, localNetId);
                enemyPassengers.Remove(netId);
            }
        }

        // Clean actors that died/unspawned/became ineligible between scans.
        List<uint> stale = null;
        foreach (KeyValuePair<uint, EnemyPassenger> pair in enemyPassengers)
        {
            if (visited.Contains(pair.Key))
                continue;

            EnemyPassenger passenger = pair.Value;
            if (passenger == null || passenger.identity == null || passenger.authority == null ||
                passenger.identity.netId == 0 || !NetworkClient.spawned.ContainsKey(pair.Key))
            {
                if (stale == null) stale = new List<uint>();
                stale.Add(pair.Key);
                continue;
            }

            passenger.missedDeckFrames++;
            if (passenger.missedDeckFrames >= PassengerLeaveGraceFrames)
            {
                ReleaseEnemyPassenger(passenger, localNetId);
                if (stale == null) stale = new List<uint>();
                stale.Add(pair.Key);
            }
        }

        if (stale != null)
            for (int i = 0; i < stale.Count; i++)
                enemyPassengers.Remove(stale[i]);
    }

    void CarryEnemyPassengersPerFrame()
    {
        if (!NetworkClient.ready || currentBoat == null || mod == null)
            return;

        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer == null || !localPlayer.isLocalPlayer)
            return;

        object boat = currentBoat.GetValue(mod);
        if (boat == null)
            return;

        GameObject meshObject = Read<GameObject>(boat, "MeshObject");
        if (meshObject == null || !meshObject.activeInHierarchy)
            return;

        Transform boatFrame = meshObject.transform;
        uint localNetId = localPlayer.netId;
        bool movedAny = false;

        foreach (EnemyPassenger passenger in enemyPassengers.Values)
        {
            if (passenger == null ||
                passenger.identity == null ||
                !passenger.identity.gameObject.activeInHierarchy ||
                passenger.authority == null ||
                !passenger.hasPreviousBoatPose)
                continue;

            // Only the actual temporary/local persistent owner drives the root enemy.
            // Observers receive the resulting ordinary NetworkTransform position.
            if (passenger.authority.PersistentOwnerPlayerNetId != localNetId ||
                !passenger.authority.hasAuthority)
                continue;

            CarryEnemyByBoatDelta(passenger, boatFrame);
            movedAny = true;
        }

        if (movedAny)
            Physics.SyncTransforms();
    }

    bool IsEligibleEnemyPassenger(NetworkIdentity identity)
    {
        if (identity == null || identity.netId == 0 || !identity.gameObject.activeInHierarchy)
            return false;

        // A real network enemy has its DaggerfallEnemy and DynamicEnemyAuthority on
        // the NetworkIdentity root. This excludes the enemy-model child used by MP
        // player avatars, even though GuardActor() still protects that visual child.
        if (identity.GetComponent<DaggerfallEnemy>() == null ||
            identity.GetComponent<DynamicEnemyAuthority>() == null)
            return false;

        // Root-space carrying is intentionally only for ordinary network enemies.
        // Companions and other systems that deliberately maintain their own hierarchy
        // keep their existing parent/ownership behavior.
        if (identity.transform.parent != null)
            return false;

        return true;
    }

    static bool IsStandingOnCurrentBoat(Transform enemy, CharacterController controller, MeshCollider boatMesh)
    {
        if (enemy == null || controller == null || boatMesh == null)
            return false;

        // Match Come Sail Away's own FixedUpdate rule:
        // raycast down by CharacterController.height, require the enemy to be grounded,
        // and require the hit collider to be this boat's MeshCollider.
        RaycastHit hit;
        if (!Physics.Raycast(new Ray(enemy.position, Vector3.down), out hit, controller.height))
            return false;

        return controller.isGrounded && hit.collider != null &&
            hit.collider.transform == boatMesh.transform;
    }

    static void CapturePassengerJoinAnchor(EnemyPassenger passenger, Transform boatFrame)
    {
        if (passenger == null || passenger.identity == null || boatFrame == null)
            return;

        passenger.joinLocalPosition = boatFrame.InverseTransformPoint(passenger.identity.transform.position);
        passenger.joinLocalRotation =
            Quaternion.Inverse(boatFrame.rotation) * passenger.identity.transform.rotation;
        passenger.hasJoinAnchor = true;
    }

    static void CarryEnemyByBoatDelta(EnemyPassenger passenger, Transform boatFrame)
    {
        if (passenger == null || passenger.identity == null || boatFrame == null ||
            !passenger.hasPreviousBoatPose)
            return;

        Transform enemy = passenger.identity.transform;

        // Express the enemy's CURRENT world pose in the previous rendered boat frame.
        // This preserves any EnemyMotor movement made during intervening FixedUpdates,
        // then reapplies that relative pose in the boat's new frame. Running this from
        // LateUpdate reproduces the every-render-frame motion native parenting gets.
        Vector3 relativePosition =
            Quaternion.Inverse(passenger.previousBoatRotation) *
            (enemy.position - passenger.previousBoatPosition);
        Quaternion boatRotationDelta =
            boatFrame.rotation * Quaternion.Inverse(passenger.previousBoatRotation);

        enemy.SetPositionAndRotation(
            boatFrame.position + boatFrame.rotation * relativePosition,
            boatRotationDelta * enemy.rotation);

        passenger.previousBoatPosition = boatFrame.position;
        passenger.previousBoatRotation = boatFrame.rotation;
    }

    static void PublishPassengerWorldContext(EnemyPassenger passenger)
    {
        if (passenger == null || passenger.authority == null || passenger.world == null ||
            !passenger.authority.hasAuthority || Time.unscaledTime < passenger.nextContext)
            return;

        passenger.nextContext = Time.unscaledTime + PassengerContextInterval;

        bool dungeon, interior;
        int baseX, baseZ, offsetX, offsetZ;
        if (!passenger.world.TryCaptureLocalActorContext(
            out dungeon, out interior, out baseX, out baseZ, out offsetX, out offsetZ))
            return;

        passenger.world.CmdPublishBoundActorContext(
            dungeon, interior, baseX, baseZ, offsetX, offsetZ);
    }

    void ReleaseAllEnemyPassengers(uint localNetId)
    {
        if (enemyPassengers.Count == 0)
            return;

        List<EnemyPassenger> states = new List<EnemyPassenger>(enemyPassengers.Values);
        enemyPassengers.Clear();
        for (int i = 0; i < states.Count; i++)
            ReleaseEnemyPassenger(states[i], localNetId);
    }

    void ClearEnemyPassengerState()
    {
        // Used during network/mod teardown where sending Commands is unsafe.
        enemyPassengers.Clear();
    }

    static void ReleaseEnemyPassenger(EnemyPassenger passenger, uint localNetId)
    {
        if (passenger == null || passenger.authority == null)
            return;

        DynamicEnemyAuthority authority = passenger.authority;

        // If this helper never requested the ownership, it belongs to another local
        // system and must not be released by the ship adapter.
        if (!passenger.claimRequested)
            return;

        // Cancel an in-flight reliable claim even if the SyncVar/authority grant has
        // not reached this client yet. The release command follows the claim in order.
        if (authority.PersistentOwnerPlayerNetId != localNetId)
        {
            authority.CmdSetPersistentOwner(false);
            return;
        }

        if (authority.hasAuthority && passenger.world != null)
        {
            bool dungeon, interior;
            int baseX, baseZ, offsetX, offsetZ;
            if (passenger.world.TryCaptureLocalActorContext(
                out dungeon, out interior, out baseX, out baseZ, out offsetX, out offsetZ))
            {
                authority.CmdReleasePersistentOwnerAtPose(
                    authority.transform.position, authority.transform.rotation,
                    dungeon, interior, baseX, baseZ, offsetX, offsetZ);

                Debug.Log(Prefix + "Enemy left local boat; released ship ownership: '" +
                    authority.name + "' net=" + authority.netId + ".");
                return;
            }
        }

        // Root-space pose is already valid; this fallback just releases the binding.
        authority.CmdSetPersistentOwner(false);
        Debug.Log(Prefix + "Enemy left local boat; released ship ownership with fallback: '" +
            authority.name + "' net=" + authority.netId + ".");
    }

    void GuardActor(NetworkIdentity identity)
    {
        if (identity == null || identity.netId == 0 || !scannedActors.Add(identity)) return;
        foreach (var enemy in identity.GetComponentsInChildren<DaggerfallEnemy>(true))
        {
            if (enemy == null) continue;
            var guard = enemy.GetComponent<CSAMPActorParentGuard>();
            if (guard == null) guard = enemy.gameObject.AddComponent<CSAMPActorParentGuard>();
            guard.Arm(this, modType);
        }
    }

    internal bool ProtectActorParents
    {
        get { return (NetworkServer.active || NetworkClient.active) && ModAvailable(); }
    }

    internal void RemoveNetworkActorsFromBoatCleanup()
    {
        if (!ProtectActorParents || nativeParentedObjects == null) return;
        var tracked = nativeParentedObjects.GetValue(mod) as IList;
        if (tracked == null) return;
        // The mod records an actor after SetParent returns. Even when the parent
        // change is immediately undone, that entry must not survive to its load
        // cleanup, which destroys tracked actors. Native SP actors are untouched.
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var actor = tracked[i] as GameObject;
            if (actor != null && actor.GetComponent<CSAMPActorParentGuard>() != null) tracked.RemoveAt(i);
        }
    }
    bool ExteriorReady()
    {
        return GameManager.HasInstance && GameManager.Instance.PlayerObject != null &&
            GameManager.Instance.PlayerGPS != null && GameManager.Instance.StreamingWorld != null &&
            GameManager.Instance.PlayerEnterExit != null &&
            !GameManager.Instance.PlayerEnterExit.IsPlayerInside;
    }
    void Bind()
    {
        if (mod != null) return;
        if (modType == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                modType = assembly.GetType("ComeSailAwayMod.ComeSailAway", false);
                if (modType != null) break;
            }
            if (modType == null) return;
            boatType = modType.Assembly.GetType("ComeSailAwayMod.Boat", false);
            if (boatType == null) return;
            allBoats = modType.GetField("AllBoats", Fields);
            currentBoat = modType.GetField("CurrentBoat", Fields);
            spawnBoat = modType.GetMethod("SpawnBoat", Fields, null, new[] { boatType }, null);
            startSailing = modType.GetMethod("StartSailing", Fields, null, new[] { boatType }, null);
            stopSailing = modType.GetMethod("StopSailing", Fields, null, Type.EmptyTypes, null);
            stopSailingDelayed = modType.GetMethod("StopSailingDelayed", Fields, null, Type.EmptyTypes, null);
            nativeDisembarking = modType.GetField(
                "disembarking",
                BindingFlags.Instance | BindingFlags.NonPublic);
            nativeFixedUpdate = modType.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            nativeParentedObjects = modType.GetField("parentedObjects", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (boatType == null || allBoats == null || currentBoat == null || spawnBoat == null || startSailing == null || stopSailing == null) return;
        mod = FindObjectOfType(modType) as Component;
        if (mod != null && !reportedBinding)
        {
            reportedBinding = true;
            Debug.Log(Prefix + "Installed mod found. Remote deck collision and native moving-platform passenger test enabled.");
        }
    }
    object Get(object boat, string name)
    {
        if (boat == null) return null;
        FieldInfo field;
        if (!fields.TryGetValue(name, out field))
        {
            field = boatType.GetField(name, Fields);
            fields[name] = field;
        }
        return field == null ? null : field.GetValue(boat);
    }
    T Read<T>(object boat, string name) { object value = Get(boat, name); return value is T ? (T)value : default(T); }
    static ulong Key(uint owner, uint boat) { return ((ulong)owner << 32) | boat; }

    void Publish(bool sendMotion, bool sendVisual)
    {
        var seen = new HashSet<GameObject>();
        float now = Time.unscaledTime;
        if (ModAvailable() && ExteriorReady())
        {
            var boats = allBoats.GetValue(mod) as IList;
            if (boats != null)
            {
                foreach (object native in boats)
                {
                    var root = Read<GameObject>(native, "GameObject");
                    if (root == null || Read<bool>(native, "inside"))
                        continue;

                    LocalBoat local;
                    bool knownLocal = locals.TryGetValue(root, out local);

                    // While another party member drives, the persistent owner's real
                    // CSA boat remains in AllBoats but is intentionally hidden. Keep
                    // its identity alive and do not publish/remove it from this client.
                    if (knownLocal && local.hiddenForRemoteDriver)
                    {
                        local.native = native;
                        local.root = root;
                        seen.Add(root);
                        continue;
                    }

                    if (!root.activeInHierarchy)
                        continue;
                    if (seen.Count >= BoatLimit)
                        break;

                    seen.Add(root);
                    if (!knownLocal)
                    {
                        local = new LocalBoat { id = ++sequence };
                        locals.Add(root, local);
                    }

                    local.native = native;
                    local.root = root;
                    ConfigureLocalDoorInteractions(local);
                    ApplyCachedDoorStatesToLocal(local);

                    if (sendVisual)
                    {
                        CSAMPBoatState state = Capture(native, root, local.id);
                        PlayerMultiplayer publisher = PlayerMultiplayer.localPlayer;
                        if (publisher != null) state.owner = publisher.netId;
                        if (now - local.visualSent >= 1f || ChangedVisual(local.lastVisual, state))
                        {
                            NetworkClient.Send(state);
                            local.lastVisual = state;
                            local.visualSent = now;
                        }
                    }
                    if (sendMotion)
                    {
                        CSAMPBoatMotionState motion = CaptureMotion(native, root, local.id);
                        PlayerMultiplayer publisher = PlayerMultiplayer.localPlayer;
                        if (publisher != null) motion.owner = publisher.netId;
                        bool driving = ReferenceEquals(currentBoat.GetValue(mod), native);
                        if (!local.hasMotion || driving || now - local.motionSent >= 0.5f || ChangedMotion(local.lastMotion, motion))
                        {
                            NetworkClient.Send(motion, Channels.Unreliable);
                            local.lastMotion = motion;
                            local.motionSent = now;
                            local.hasMotion = true;
                        }
                    }
                }
            }
        }
        PublishDrivenRemoteBoat(sendMotion, sendVisual, now);

        var gone = new List<GameObject>();
        foreach (var entry in locals)
            if (!seen.Contains(entry.Key))
            {
                NetworkClient.Send(new CSAMPBoatState { boat = entry.Value.id, removed = true });
                gone.Add(entry.Key);
            }
        foreach (var root in gone) locals.Remove(root);
    }

    // DFU advances GPS in Update and can defer it while terrain streams. Its
    // private worldX/worldZ and lastPlayerPos are a matching coordinate anchor.
    // Read that pair without changing DFU or using its live player + stale GPS.
    static readonly FieldInfo streamingWorldX = typeof(StreamingWorld).GetField(
        "worldX", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo streamingWorldZ = typeof(StreamingWorld).GetField(
        "worldZ", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo streamingLastPlayerPos = typeof(StreamingWorld).GetField(
        "lastPlayerPos", BindingFlags.Instance | BindingFlags.NonPublic);

    bool TryReadStreamingAnchor(out Vector3 sceneAnchor, out double worldAnchorX, out double worldAnchorZ)
    {
        sceneAnchor = Vector3.zero;
        worldAnchorX = worldAnchorZ = 0;
        if (!GameManager.HasInstance || GameManager.Instance.StreamingWorld == null)
            return false;
        if (streamingWorldX == null || streamingWorldZ == null || streamingLastPlayerPos == null)
        {
            Warn("Cannot read DFU streaming coordinate anchor; boat synchronization is suspended.");
            return false;
        }
        var streaming = GameManager.Instance.StreamingWorld;
        sceneAnchor = (Vector3)streamingLastPlayerPos.GetValue(streaming);
        worldAnchorX = (double)streamingWorldX.GetValue(streaming);
        worldAnchorZ = (double)streamingWorldZ.GetValue(streaming);
        return true;
    }

    void GetBoatWorldCoordinates(Vector3 position, out double x, out double z)
    {
        Vector3 sceneAnchor;
        double anchorX, anchorZ;
        if (!TryReadStreamingAnchor(out sceneAnchor, out anchorX, out anchorZ))
            throw new InvalidOperationException("DFU streaming coordinate anchor unavailable.");
        x = anchorX + ((double)position.x - sceneAnchor.x) * StreamingWorld.SceneMapRatio;
        z = anchorZ + ((double)position.z - sceneAnchor.z) * StreamingWorld.SceneMapRatio;
    }

    CSAMPBoatState Capture(object native, GameObject root, uint id)
    {
        var game = GameManager.Instance;
        double boatWorldX, boatWorldZ;
        GetBoatWorldCoordinates(root.transform.position, out boatWorldX, out boatWorldZ);
        var mesh = Read<GameObject>(native, "MeshObject");
        var sails = Get(native, "Sails") as IList;
        var state = new CSAMPBoatState {
            boat = id, hull = Read<int>(native, "hull"), variant = Read<int>(native, "variant"),
            driving = ReferenceEquals(currentBoat.GetValue(mod), native), lights = Read<bool>(native, "LightOn"),
            worldX = boatWorldX,
            worldZ = boatWorldZ,
            y = root.transform.position.y, rotation = root.transform.rotation,
            meshPosition = mesh != null ? mesh.transform.localPosition : Vector3.zero,
            meshRotation = mesh != null ? mesh.transform.localRotation : Quaternion.identity,
            sailRotations = Rotations(sails), boomRotations = Rotations(Get(native, "Booms") as IList),
            stowed = new bool[Math.Min(sails == null ? 0 : sails.Count, PartLimit)]
        };
        for (int i = 0; i < state.stowed.Length; i++)
        {
            var sail = sails[i] as Transform;
            var animator = sail == null ? null : sail.GetComponent<Animator>();
            state.stowed[i] = animator == null || animator.GetBool("Stowed");
        }
        var rudder = Read<Animator>(native, "RudderAnimator");
        if (rudder != null)
        {
            state.sailing = HasParam(rudder, "Sailing", AnimatorControllerParameterType.Bool) && rudder.GetBool("Sailing");
            state.rowX = AnimFloat(rudder, "RowX"); state.rowZ = AnimFloat(rudder, "RowZ");
            state.rowSpeed = AnimFloat(rudder, "RowSpeed"); state.turn = AnimFloat(rudder, "TurnAngle");
        }
        return state;
    }

    CSAMPBoatMotionState CaptureMotion(object native, GameObject root, uint id)
    {
        var game = GameManager.Instance;
        double boatWorldX, boatWorldZ;
        GetBoatWorldCoordinates(root.transform.position, out boatWorldX, out boatWorldZ);
        var mesh = Read<GameObject>(native, "MeshObject");
        return new CSAMPBoatMotionState {
            boat = id,
            worldX = boatWorldX,
            worldZ = boatWorldZ,
            y = root.transform.position.y,
            rotation = root.transform.rotation,
            meshPosition = mesh != null ? mesh.transform.localPosition : Vector3.zero,
            meshRotation = mesh != null ? mesh.transform.localRotation : Quaternion.identity,
        };
    }

    static CSAMPBoatMotionState MotionFromState(CSAMPBoatState state)
    {
        return new CSAMPBoatMotionState {
            owner = state.owner, boat = state.boat, removed = state.removed,
            worldX = state.worldX, worldZ = state.worldZ, y = state.y,
            rotation = state.rotation, meshPosition = state.meshPosition, meshRotation = state.meshRotation,
        };
    }

    static bool HasParam(Animator animator, string name, AnimatorControllerParameterType type)
    {
        foreach (var p in animator.parameters) if (p.name == name && p.type == type) return true;
        return false;
    }
    static float AnimFloat(Animator animator, string name)
    { return HasParam(animator, name, AnimatorControllerParameterType.Float) ? animator.GetFloat(name) : 0f; }
    static Quaternion[] Rotations(IList list)
    {
        var result = new Quaternion[Math.Min(list == null ? 0 : list.Count, PartLimit)];
        for (int i = 0; i < result.Length; i++)
        {
            var t = list[i] as Transform;
            result[i] = t == null ? Quaternion.identity : t.localRotation;
        }
        return result;
    }
    static bool ChangedVisual(CSAMPBoatState a, CSAMPBoatState b)
    {
        return b.driving || a.driving != b.driving || a.lights != b.lights ||
            a.sailing != b.sailing || a.hull != b.hull || a.variant != b.variant;
    }
    static bool ChangedMotion(CSAMPBoatMotionState a, CSAMPBoatMotionState b)
    {
        return Math.Abs(a.worldX - b.worldX) > 0.5 || Math.Abs(a.worldZ - b.worldZ) > 0.5 ||
            Mathf.Abs(a.y - b.y) > 0.01f || Quaternion.Angle(a.rotation, b.rotation) > 0.25f ||
            Vector3.SqrMagnitude(a.meshPosition - b.meshPosition) > 0.0001f ||
            Quaternion.Angle(a.meshRotation, b.meshRotation) > 0.25f;
    }

    Budget GetBudget(NetworkConnection connection)
    {
        Budget value;
        if (!budgets.TryGetValue(connection.connectionId, out value))
        { value = new Budget { subscription = -100 }; budgets.Add(connection.connectionId, value); }
        return value;
    }
    void Subscribe(NetworkConnection connection, CSAMPSubscribe message)
    {
        if (message.version != 5 || connection == null || connection.identity == null || !connection.isReady) return;
        var budget = GetBudget(connection);
        if (Time.unscaledTime - budget.subscription < 2f) return;
        budget.subscription = Time.unscaledTime;
        viewers[connection.connectionId] = connection;
        foreach (var record in cache.Values)
        {
            ulong recordKey = Key(record.state.owner, record.state.boat);
            if (GetServerSimulator(recordKey, record.state.owner) == connection.identity.netId)
                continue;
            connection.Send(record.state);
            if (record.hasMotion) connection.Send(record.motion, Channels.Unreliable);
        }

        foreach (var driverEntry in serverDrivers)
        {
            if (driverEntry.Value == 0)
                continue;
            connection.Send(new CSAMPDriveState {
                owner = (uint)(driverEntry.Key >> 32),
                boat = (uint)(driverEntry.Key & 0xffffffffUL),
                driver = driverEntry.Value,
                result = 0,
            });
        }

        foreach (var boatEntry in serverDoorStates)
        {
            uint owner = (uint)(boatEntry.Key >> 32);
            uint boat = (uint)(boatEntry.Key & 0xffffffffUL);
            foreach (var doorEntry in boatEntry.Value)
            {
                connection.Send(new CSAMPDoorState {
                    owner = owner,
                    boat = boat,
                    door = doorEntry.Key,
                    opened = doorEntry.Value,
                });
            }
        }
    }
    bool AcceptOwnerPacket(NetworkConnection connection)
    {
        if (connection == null || connection.identity == null || !connection.isReady) return false;
        var budget = GetBudget(connection);
        if (Time.unscaledTime - budget.since >= 1f) { budget.since = Time.unscaledTime; budget.count = 0; }
        return ++budget.count <= 200;
    }
    uint GetServerSimulator(ulong key, uint owner)
    {
        uint driver;
        return serverDrivers.TryGetValue(key, out driver) && driver != 0
            ? driver
            : owner;
    }

    bool CanSimulateBoat(NetworkConnection connection, uint owner, uint boat, out ulong key)
    {
        key = 0;
        if (connection == null || connection.identity == null || !connection.isReady ||
            owner == 0 || boat == 0)
            return false;

        key = Key(owner, boat);
        return connection.identity.netId == GetServerSimulator(key, owner);
    }

    void ReceiveOwnerState(NetworkConnection connection, CSAMPBoatState state)
    {
        if (state.boat == 0 || !AcceptOwnerPacket(connection))
            return;

        uint sender = connection.identity.netId;
        if (state.owner == 0)
            state.owner = sender;

        ulong key;
        if (state.removed)
        {
            // Only the persistent owner can remove the boat itself.
            if (sender != state.owner)
                return;

            key = Key(state.owner, state.boat);
            serverDoorStates.Remove(key);
            ReleaseServerDriver(key, state.owner, state.boat);
            if (cache.Remove(key))
                Relay(state);
            return;
        }

        if (!CanSimulateBoat(connection, state.owner, state.boat, out key) ||
            !Valid(state))
            return;

        CachedBoat record;
        if (!cache.TryGetValue(key, out record))
        {
            // A leased driver can never create a boat identity. The persistent owner
            // must have published it before somebody can request the rudder.
            if (sender != state.owner)
                return;

            int count = 0;
            foreach (var item in cache.Values)
                if (item.state.owner == state.owner)
                    count++;
            if (count >= BoatLimit)
                return;

            record = new CachedBoat();
            cache.Add(key, record);
        }

        record.state = state;
        record.received = Time.unscaledTime;
        if (!record.hasMotion)
        {
            record.motion = MotionFromState(state);
            record.hasMotion = true;
        }
        Relay(state);
    }

    void ReceiveOwnerMotion(NetworkConnection connection, CSAMPBoatMotionState motion)
    {
        if (motion.boat == 0 || !AcceptOwnerPacket(connection))
            return;

        uint sender = connection.identity.netId;
        if (motion.owner == 0)
            motion.owner = sender;

        ulong key;
        if (motion.removed)
        {
            // Only the persistent owner can remove the boat identity.
            if (sender != motion.owner)
                return;

            key = Key(motion.owner, motion.boat);
            serverDoorStates.Remove(key);
            ReleaseServerDriver(key, motion.owner, motion.boat);
            if (cache.Remove(key))
                Relay(motion);
            return;
        }

        if (!CanSimulateBoat(connection, motion.owner, motion.boat, out key) ||
            !Valid(motion))
            return;

        CachedBoat record;
        if (!cache.TryGetValue(key, out record))
            return;

        record.motion = motion;
        record.hasMotion = true;
        record.received = Time.unscaledTime;
        Relay(motion);
    }

    bool ServerSameParty(uint first, uint second)
    {
        ulong firstParty, secondParty;
        return MultiplayerPartyManager.ServerTryGetPartyId(first, out firstParty) &&
               MultiplayerPartyManager.ServerTryGetPartyId(second, out secondParty) &&
               firstParty != 0UL && firstParty == secondParty;
    }

    void SendDriveResult(NetworkConnection connection, uint owner, uint boat, uint driver, byte result)
    {
        if (connection == null || !connection.isReady)
            return;

        connection.Send(new CSAMPDriveState {
            owner = owner,
            boat = boat,
            driver = driver,
            result = result,
        });
    }

    void BroadcastDriveState(uint owner, uint boat, uint driver)
    {
        CSAMPDriveState state = new CSAMPDriveState {
            owner = owner,
            boat = boat,
            driver = driver,
            result = 0,
        };

        foreach (var connection in viewers.Values)
        {
            if (connection != null && connection.isReady && connection.identity != null)
                connection.Send(state);
        }
    }

    void ReleaseServerDriver(ulong key, uint owner, uint boat)
    {
        uint previous;
        if (!serverDrivers.TryGetValue(key, out previous) || previous == 0)
            return;

        serverDrivers.Remove(key);
        BroadcastDriveState(owner, boat, 0);
    }

    void ReceiveDriveRequest(NetworkConnection connection, CSAMPDriveRequest request)
    {
        if (connection == null || connection.identity == null || !connection.isReady ||
            request.owner == 0 || request.boat == 0 || !AcceptOwnerPacket(connection))
            return;

        uint requester = connection.identity.netId;
        ulong key = Key(request.owner, request.boat);

        CachedBoat record;
        if (!cache.TryGetValue(key, out record))
        {
            SendDriveResult(connection, request.owner, request.boat, 0, 3);
            return;
        }

        uint currentDriver;
        serverDrivers.TryGetValue(key, out currentDriver);

        if (request.release)
        {
            if (currentDriver == requester)
            {
                serverDrivers.Remove(key);
                BroadcastDriveState(request.owner, request.boat, 0);
            }
            return;
        }

        // The real owner keeps using native CSA controls and does not need a lease.
        // Remote driving is intentionally limited to another member of that owner's party.
        if (requester == request.owner || !ServerSameParty(request.owner, requester))
        {
            SendDriveResult(connection, request.owner, request.boat, currentDriver, 1);
            return;
        }

        // If the owner is currently at the helm, do not steal control out from under them.
        if (currentDriver == 0 && record.state.driving)
        {
            SendDriveResult(connection, request.owner, request.boat, request.owner, 2);
            return;
        }

        if (currentDriver != 0 && currentDriver != requester)
        {
            SendDriveResult(connection, request.owner, request.boat, currentDriver, 2);
            return;
        }

        serverDrivers[key] = requester;
        BroadcastDriveState(request.owner, request.boat, requester);
        Debug.Log(Prefix + "Party driver lease granted owner=" + request.owner +
            " boat=" + request.boat + " driver=" + requester + ".");
    }

    void ReceiveDriveState(NetworkConnection connection, CSAMPDriveState state)
    {
        if (BoatLoadSuspended)
        {
            if (state.driver != 0 && state.driver == PlayerMultiplayer.GetLocalPlayerNetIdSafe())
                NetworkClient.Send(new CSAMPDriveRequest { owner = state.owner, boat = state.boat, release = true });
            return;
        }
        if (state.owner == 0 || state.boat == 0)
            return;

        if (state.result != 0)
        {
            if (state.result == 1)
                DaggerfallUI.AddHUDText("Only party members can drive this boat.");
            else if (state.result == 2)
                DaggerfallUI.AddHUDText("Someone else is controlling this boat.");
            else
                DaggerfallUI.AddHUDText("This boat cannot be controlled right now.");
            return;
        }

        ulong key = Key(state.owner, state.boat);
        if (loadReleasedLeases.Contains(key))
        {
            if (state.driver != 0 && state.driver == PlayerMultiplayer.GetLocalPlayerNetIdSafe())
            {
                NetworkClient.Send(new CSAMPDriveRequest { owner = state.owner, boat = state.boat, release = true });
                return; // Ignore an old grant queued before the load/release.
            }
            loadReleasedLeases.Remove(key);
        }

        if (releasingRemoteDrive && releasingRemoteDriveKey == key &&
            state.driver != PlayerMultiplayer.GetLocalPlayerNetIdSafe())
        {
            releasingRemoteDrive = false;
            releasingRemoteDriveKey = 0;
        }

        uint previous = 0;
        clientDrivers.TryGetValue(key, out previous);

        if (state.driver == 0)
            clientDrivers.Remove(key);
        else
            clientDrivers[key] = state.driver;

        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        uint localNetId = local != null ? local.netId : 0U;

        if (activeRemoteDriveKey == key && state.driver != localNetId)
            StopRemoteDriveLocal(false);

        if (localNetId == state.owner)
        {
            if (state.driver != 0 && state.driver != state.owner)
                BeginOwnedBoatNetworkFollow(key, state.owner, state.boat);
            else
                EndOwnedBoatNetworkFollow(key, state.owner, state.boat);

            SetOwnedBoatDriveTriggerEnabled(state.boat,
                state.driver == 0 || state.driver == state.owner);
        }
    }

    void ReceiveDoorRequest(NetworkConnection connection, CSAMPDoorRequest request)
    {
        if (connection == null || connection.identity == null || !connection.isReady)
            return;
        if (request.owner == 0 || request.boat == 0 || request.door >= 64)
            return;
        if (!AcceptOwnerPacket(connection))
            return;

        ulong key = Key(request.owner, request.boat);
        if (!cache.ContainsKey(key))
            return;

        Dictionary<ushort, bool> doors;
        if (!serverDoorStates.TryGetValue(key, out doors))
        {
            doors = new Dictionary<ushort, bool>();
            serverDoorStates.Add(key, doors);
        }
        doors[request.door] = request.opened;

        CSAMPDoorState state = new CSAMPDoorState {
            owner = request.owner,
            boat = request.boat,
            door = request.door,
            opened = request.opened,
        };

        // Door interaction is shared presentation state. Send it to every subscribed
        // player including the actual ship owner, because a passenger may operate it.
        foreach (var viewer in viewers.Values)
        {
            if (viewer != null && viewer.isReady && viewer.identity != null)
                viewer.Send(state);
        }
    }

    void ReceiveDoorState(NetworkConnection connection, CSAMPDoorState state)
    {
        if (state.owner == 0 || state.boat == 0 || state.door >= 64)
            return;

        ulong key = Key(state.owner, state.boat);
        Dictionary<ushort, bool> doors;
        if (!clientDoorStates.TryGetValue(key, out doors))
        {
            doors = new Dictionary<ushort, bool>();
            clientDoorStates.Add(key, doors);
        }

        doors[state.door] = state.opened;
        ApplyDoorState(state.owner, state.boat, state.door, state.opened);
    }

    void Relay(CSAMPBoatState state)
    {
        ulong key = Key(state.owner, state.boat);
        uint simulator = GetServerSimulator(key, state.owner);
        foreach (var connection in viewers.Values)
            if (connection != null && connection.isReady && connection.identity != null &&
                connection.identity.netId != simulator)
                connection.Send(state);
    }

    void Relay(CSAMPBoatMotionState motion)
    {
        ulong key = Key(motion.owner, motion.boat);
        uint simulator = GetServerSimulator(key, motion.owner);
        foreach (var connection in viewers.Values)
            if (connection != null && connection.isReady && connection.identity != null &&
                connection.identity.netId != simulator)
                connection.Send(motion, Channels.Unreliable);
    }

    static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    static bool ValidVector(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z) && v.sqrMagnitude < 1e8f; }
    static bool ValidRotation(Quaternion q)
    {
        float length = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
        return Finite(length) && length > 0.9f && length < 1.1f;
    }
    static bool Valid(CSAMPBoatState s)
    {
        if (s.hull < 0 || s.hull > 3 || s.variant < 0 || s.variant > 64 ||
            !Finite(s.worldX) || !Finite(s.worldZ) || Math.Abs(s.worldX) > 1e9 || Math.Abs(s.worldZ) > 1e9 ||
            !Finite(s.y) || Math.Abs(s.y) > 100000 || !ValidVector(s.meshPosition) ||
            !ValidRotation(s.rotation) || !ValidRotation(s.meshRotation) ||
            !Finite(s.rowX) || !Finite(s.rowZ) || !Finite(s.rowSpeed) || !Finite(s.turn) ||
            s.sailRotations == null || s.boomRotations == null || s.stowed == null ||
            s.sailRotations.Length > PartLimit || s.boomRotations.Length > PartLimit ||
            s.stowed.Length != s.sailRotations.Length) return false;
        foreach (var q in s.sailRotations) if (!ValidRotation(q)) return false;
        foreach (var q in s.boomRotations) if (!ValidRotation(q)) return false;
        return true;
    }
    static bool Valid(CSAMPBoatMotionState s)
    {
        return !s.removed && Finite(s.worldX) && Finite(s.worldZ) &&
            Math.Abs(s.worldX) <= 1e9 && Math.Abs(s.worldZ) <= 1e9 &&
            Finite(s.y) && Math.Abs(s.y) <= 100000 && ValidVector(s.meshPosition) &&
            ValidRotation(s.rotation) && ValidRotation(s.meshRotation);
    }
    void PruneServer()
    {
        var gone = new List<ulong>();
        foreach (var entry in cache)
            if (!NetworkServer.spawned.ContainsKey(entry.Value.state.owner) || Time.unscaledTime - entry.Value.received > 10f)
            {
                var state = entry.Value.state; state.removed = true; Relay(state); gone.Add(entry.Key);
            }
        foreach (ulong key in gone)
        {
            uint owner = (uint)(key >> 32);
            uint boat = (uint)(key & 0xffffffffUL);
            ReleaseServerDriver(key, owner, boat);
            cache.Remove(key);
        }

        var invalidDrivers = new List<ulong>();
        foreach (var entry in serverDrivers)
        {
            uint owner = (uint)(entry.Key >> 32);
            uint driver = entry.Value;
            if (driver == 0 ||
                !cache.ContainsKey(entry.Key) ||
                !NetworkServer.spawned.ContainsKey(owner) ||
                !NetworkServer.spawned.ContainsKey(driver) ||
                !ServerSameParty(owner, driver))
                invalidDrivers.Add(entry.Key);
        }
        foreach (ulong key in invalidDrivers)
        {
            uint owner = (uint)(key >> 32);
            uint boat = (uint)(key & 0xffffffffUL);
            ReleaseServerDriver(key, owner, boat);
        }

        var departed = new List<int>();
        foreach (var entry in viewers)
            if (entry.Value == null || entry.Value.identity == null || !entry.Value.isReady) departed.Add(entry.Key);
        foreach (int id in departed) { viewers.Remove(id); budgets.Remove(id); }
    }
    void ReceiveRemoteState(NetworkConnection connection, CSAMPBoatState state)
    {
        ulong key = Key(state.owner, state.boat);
        RemoteBoat remote;
        if (state.removed)
        {
            if (remotes.TryGetValue(key, out remote)) DestroyRemote(remote);
            remotes.Remove(key);
            clientDoorStates.Remove(key);
            clientDrivers.Remove(key);
            return;
        }
        if (!ModAvailable() || !Valid(state)) return;
        if (!remotes.TryGetValue(key, out remote)) { remote = new RemoteBoat(); remotes.Add(key, remote); }
        if (remote.hasState &&
            (remote.state.hull != state.hull || remote.state.variant != state.variant))
            DestroyRemote(remote);
        remote.state = state;
        remote.hasState = true;
        remote.received = Time.unscaledTime;
        if (!remote.hasMotion)
            UpdateHiddenOwnedBoatMapPixel(state.owner, state.boat, state.worldX, state.worldZ);
        if (!remote.hasMotion) PushRemoteMotion(remote, MotionFromState(state));
    }
    void ReceiveRemoteMotion(NetworkConnection connection, CSAMPBoatMotionState motion)
    {
        ulong key = Key(motion.owner, motion.boat);
        RemoteBoat remote;
        if (motion.removed)
        {
            if (remotes.TryGetValue(key, out remote)) DestroyRemote(remote);
            remotes.Remove(key);
            clientDoorStates.Remove(key);
            clientDrivers.Remove(key);
            return;
        }
        if (!ModAvailable() || !Valid(motion)) return;
        if (!remotes.TryGetValue(key, out remote)) { remote = new RemoteBoat(); remotes.Add(key, remote); }
        remote.received = Time.unscaledTime;


        if (remote.handoffWaitingForOwnerMotion)
        {
            float now = Time.unscaledTime;
            remote.previousMotion = remote.latestMotion;
            remote.latestMotion = motion;
            remote.hasMotion = true;
            remote.hasPreviousMotion = true;
            remote.latestMotionReceived = now;
            remote.motionInterval = 0.12f;
            remote.handoffWaitingForOwnerMotion = false;
            remote.handoffWaitStarted = 0f;
            remote.motionReceivedRealtime = Time.realtimeSinceStartup;
            remote.observedSpeed = 0f;
        }
        else
        {
            PushRemoteMotion(remote, motion);
        }
        // Use the accepted snapshot, not a stale full-state coordinate.
        UpdateHiddenOwnedBoatMapPixel(motion.owner, motion.boat,
            remote.latestMotion.worldX, remote.latestMotion.worldZ);
    }
    void PushRemoteMotion(RemoteBoat remote, CSAMPBoatMotionState motion)
    {
        float now = Time.unscaledTime;
        if (!remote.hasMotion)
        {
            remote.previousMotion = motion; remote.latestMotion = motion;
            remote.hasMotion = true; remote.hasPreviousMotion = false;
            remote.latestMotionReceived = now; remote.motionInterval = 0.05f;
            remote.motionReceivedRealtime = Time.realtimeSinceStartup;
            remote.observedSpeed = 0f;
            return;
        }
        // Receipt time is real time even inside FixedUpdate. Distance alone is
        // not teleport evidence at CSA x30 or after terrain/network stalls.
        float realtime = Time.realtimeSinceStartup;
        float elapsed = Mathf.Max(0.001f, realtime - remote.motionReceivedRealtime);
        double dx = (motion.worldX - remote.latestMotion.worldX) / Units;
        double dz = (motion.worldZ - remote.latestMotion.worldZ) / Units;
        float distance = (float)Math.Sqrt(dx * dx + dz * dz);
        float allowance = 100f + remote.observedSpeed * Mathf.Min(elapsed, 2f) * 2f;
        bool snap = distance > allowance || Mathf.Abs(motion.y - remote.latestMotion.y) > 50f;
        if (distance > 100f || snap)
        {
            // Arm only for the observer boat that actually supported this player.
            // A packet cannot acquire support or rescue somebody already off deck.
            if (!remote.localDriving && remote.positioned && remote.root != null &&
                supportedDeck != null && supportedDeck.IsChildOf(remote.root.transform) &&
                realtime - supportSeenRealtime <= 0.5f && correctionSupportSteps == 0)
            {
                correctionSupportSteps = 2;
                correctionSupportUntil = realtime + 0.5f;
            }
            LogBoatCorrection(remote, motion, "accepted boat update; passenger carry pending physics support check; " +
                "gap=" + elapsed.ToString("F3") + " speed=" + remote.observedSpeed.ToString("F1") +
                " snap=" + snap);
        }
        // Do not learn a velocity from bunched packets or from a spatial outlier.
        if (!snap && elapsed >= 0.015f && elapsed <= 2f)
            remote.observedSpeed = Mathf.Lerp(remote.observedSpeed,
                Mathf.Min(distance / elapsed, 2000f), 0.25f);
        remote.motionReceivedRealtime = realtime;
        if (snap)
        {
            // Reset interpolation only. Physics still captures support BEFORE moving
            // this same boat and carries that passenger through the correction.
            remote.previousMotion = remote.latestMotion = motion;
            remote.hasPreviousMotion = false;
            remote.latestMotionReceived = now;
            return;
        }
        // Continue from the current interpolated pose. Restarting at the last
        // packet's target can jump the deck when the next packet arrives early.
        CSAMPBoatMotionState rendered = remote.latestMotion;
        SampleRemoteMotion(remote, out rendered.worldX, out rendered.worldZ,
            out rendered.y, out rendered.rotation, out rendered.meshPosition, out rendered.meshRotation);
        float observed = now - remote.latestMotionReceived;
        if (observed > 0.001f && observed < 0.5f)
            remote.motionInterval = Mathf.Lerp(remote.motionInterval, Mathf.Clamp(observed, 0.035f, 0.15f), 0.25f);
        remote.previousMotion = rendered;
        remote.latestMotion = motion;
        remote.hasPreviousMotion = true;
        remote.latestMotionReceived = now;
    }
    void LogBoatCorrection(RemoteBoat remote, CSAMPBoatMotionState incoming, string reason)
    {
        if (Time.realtimeSinceStartup < remote.nextCorrectionDiagnostic) return;
        remote.nextCorrectionDiagnostic = Time.realtimeSinceStartup + 1f;
        Debug.LogWarning(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
            " frame=" + Time.frameCount + " owner=" + incoming.owner + " boat=" + incoming.boat +
            " " + reason + " deltaMetres=(" +
            ((incoming.worldX - remote.latestMotion.worldX) / Units).ToString("F2") + "," +
            (incoming.y - remote.latestMotion.y).ToString("F2") + "," +
            ((incoming.worldZ - remote.latestMotion.worldZ) / Units).ToString("F2") + ")");
    }

    void SampleRemoteMotion(RemoteBoat remote, out double worldX, out double worldZ, out float y,
        out Quaternion rotation, out Vector3 meshPosition, out Quaternion meshRotation)
    {
        if (!remote.hasPreviousMotion)
        {
            worldX = remote.latestMotion.worldX; worldZ = remote.latestMotion.worldZ; y = remote.latestMotion.y;
            rotation = remote.latestMotion.rotation; meshPosition = remote.latestMotion.meshPosition;
            meshRotation = remote.latestMotion.meshRotation; return;
        }
        float interval = Mathf.Clamp(remote.motionInterval, 0.035f, 0.15f);
        float t = Mathf.Clamp01((Time.unscaledTime - remote.latestMotionReceived) / interval);
        worldX = remote.previousMotion.worldX + (remote.latestMotion.worldX - remote.previousMotion.worldX) * t;
        worldZ = remote.previousMotion.worldZ + (remote.latestMotion.worldZ - remote.previousMotion.worldZ) * t;
        y = Mathf.Lerp(remote.previousMotion.y, remote.latestMotion.y, t);
        rotation = Quaternion.Slerp(remote.previousMotion.rotation, remote.latestMotion.rotation, t);
        meshPosition = Vector3.Lerp(remote.previousMotion.meshPosition, remote.latestMotion.meshPosition, t);
        meshRotation = Quaternion.Slerp(remote.previousMotion.meshRotation, remote.latestMotion.meshRotation, t);
    }
    void UpdateRemoteBoatsPhysics(bool pausedUpdate = false)
    {
        Vector3 sceneAnchor;
        double anchorX, anchorZ;
        // Do not fall back to mixing live transforms with delayed GPS coordinates.
        if (!TryReadStreamingAnchor(out sceneAnchor, out anchorX, out anchorZ)) return;
        bool ready = NetworkClient.ready && ModAvailable() && ExteriorReady();
        var expired = new List<ulong>();
        bool movedAny = false;
        Transform passengerDeck = null;
        Vector3 passengerLocalPoint = Vector3.zero;
        Quaternion passengerDeckRotation = Quaternion.identity;
        if (!pausedUpdate && ready && SelectPassengerSupport(out passengerDeck))
        {
            passengerLocalPoint = passengerDeck.InverseTransformPoint(GameManager.Instance.PlayerObject.transform.position);
            passengerDeckRotation = passengerDeck.rotation;
        }
        foreach (var entry in remotes)
        {
            var remote = entry.Value;

            // The leased client is running CSA natively against this replicated Boat.
            // Never overwrite that simulation with its old observer interpolation.
            if (remote.localDriving)
            {
                remote.received = Time.unscaledTime;
                continue;
            }

            if (remote.handoffWaitingForOwnerMotion)
            {
                remote.received = Time.unscaledTime;
                if (remote.handoffWaitStarted > 0f &&
                    Time.unscaledTime - remote.handoffWaitStarted > 2f)
                {
                    remote.handoffWaitingForOwnerMotion = false;
                    remote.handoffWaitStarted = 0f;
                }
                else
                {
                    continue;
                }
            }

            if (Time.unscaledTime - remote.received > 12f) { DestroyRemote(remote); expired.Add(entry.Key); continue; }
            if (!ready || !remote.hasState || !remote.hasMotion) { HideRemote(remote); continue; }
            double worldX, worldZ; float y; Quaternion rotation, meshRotation; Vector3 meshPosition;
            SampleRemoteMotion(remote, out worldX, out worldZ, out y, out rotation, out meshPosition, out meshRotation);
            var game = GameManager.Instance;
            Vector3 boatPosition = new Vector3(
                (float)(sceneAnchor.x + (worldX - anchorX) / StreamingWorld.SceneMapRatio),
                y,
                (float)(sceneAnchor.z + (worldZ - anchorZ) / StreamingWorld.SceneMapRatio));
            Vector3 playerPosition = game.PlayerObject.transform.position;
            double dx = (double)boatPosition.x - playerPosition.x;
            double dz = (double)boatPosition.z - playerPosition.z;
            if (dx*dx + dz*dz > VisibleRange*VisibleRange) { HideRemote(remote); remote.positioned = false; continue; }
            try
            {
                if (remote.root == null && Time.unscaledTime >= remote.retry) Build(remote);
                if (remote.root == null) continue;
                remote.root.SetActive(true);
                // Capture support BEFORE advancing the collider. Sweeping the
                // controller toward a deck that has already moved can hit its rail
                // or cabin instead of inheriting the boat's displacement.
                bool carryPassenger = passengerDeck != null && !pausedUpdate &&
                    !remote.localDriving && remote.positioned &&
                    passengerDeck.IsChildOf(remote.root.transform) &&
                    !GameManager.Instance.PlayerObject.transform.IsChildOf(remote.root.transform);

                remote.root.transform.position = boatPosition;
                remote.root.transform.rotation = rotation;
                if (remote.mesh != null)
                {
                    remote.mesh.transform.localPosition = meshPosition;
                    remote.mesh.transform.localRotation = meshRotation;
                }
                if (carryPassenger && passengerDeck != null && passengerDeck.gameObject.activeInHierarchy)
                {
                    CarryRemoteDeckPassenger(passengerDeck, passengerLocalPoint, passengerDeckRotation);
                    passengerDeck = null; // Never carry twice when replicated hulls overlap.
                }

                remote.positioned = true;
                float visualBlend = 1f - Mathf.Exp(-10f * (pausedUpdate ? Time.unscaledDeltaTime : Time.fixedUnscaledDeltaTime));
                ApplyVisuals(remote, visualBlend);
                SyncHiddenOwnedBoatFromObserver(entry.Key, remote, worldX, worldZ);
                movedAny = true;
            }
            catch (Exception e)
            {
                DestroyRemote(remote); remote.retry = Time.unscaledTime + 10f;
                Warn("Remote boat: " + e.GetBaseException().Message);
            }
        }
        foreach (ulong key in expired) remotes.Remove(key);
        if (movedAny) Physics.SyncTransforms();
    }

    internal void EnforceHiddenOwnedBoatsAfterCSAUpdate()
    {
        bool changed = false;
        foreach (LocalBoat local in locals.Values)
        {
            if (local == null || !local.hiddenForRemoteDriver || local.root == null)
                continue;

            if (local.root.activeSelf)
            {
                local.root.SetActive(false);
                changed = true;
            }
        }

        if (changed)
            Physics.SyncTransforms();
    }

    void Build(RemoteBoat remote)
    {
        remote.retry = Time.unscaledTime + 10f;
        // Ask the installed mod to construct its own assets. Never enter AllBoats,
        // never call StartSailing, and never create/save a player-owned boat entry.
        remote.native = Activator.CreateInstance(boatType, new object[] { remote.state.hull, remote.state.variant });
        try { spawnBoat.Invoke(mod, new[] { remote.native }); }
        finally { remote.root = Read<GameObject>(remote.native, "GameObject"); }
        if (remote.root == null) throw new InvalidOperationException("Installed mod did not construct a boat.");
        remote.root.name = "MP boardable boat [" + remote.state.owner + ":" + remote.state.boat + "]";
        remote.root.transform.SetParent(null, true);
        remote.mesh = Read<GameObject>(remote.native, "MeshObject");
        remote.root.AddComponent<CSAMPRemoteDeck>();
        // Keep installed solid geometry. Start with all trigger colliders disabled,
        // then selectively restore only CSA's board (112401) and door (112403)
        // activators below. Driving/cargo/variant/status controls stay disabled.
        foreach (var collider in remote.root.GetComponentsInChildren<Collider>(true))
            if (collider.isTrigger || collider is CharacterController) collider.enabled = false;
        foreach (var body in remote.root.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = true; }
        foreach (var audio in remote.root.GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); audio.enabled = false; }
        foreach (var script in remote.root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (script == null) continue;
            if (script.GetType().Name == "CollisionParenter") { script.enabled = false; Destroy(script); }
            // Animation events can call disabled behaviours. Replace the installed
            // event forwarder so observer oars cannot operate the viewer's own boat.
            if (script.GetType().Name == "RudderAnimationEventListener")
            {
                script.enabled = false;
                if (script.GetComponent<CSAMPVisualOarEvents>() == null) script.gameObject.AddComponent<CSAMPVisualOarEvents>();
                Destroy(script);
            }
        }
        var cargo = Get(remote.native, "Cargo") as Component;
        if (cargo != null) { cargo.gameObject.SetActive(false); Destroy(cargo.gameObject); }
        // Controls that could make the observer own/modify the ship remain disabled.
        foreach (string name in new[] { "CargoTrigger", "StatusTrigger", "PositionTrigger", "VariantTrigger" })
        {
            var marker = Read<GameObject>(remote.native, name);
            if (marker != null) marker.SetActive(false);
        }

        // Boarding and doors are safe observer interactions and are handled entirely
        // by this adapter. The replicated boat never enters CSA's native AllBoats list.
        ConfigureRemoteInteractions(remote);
        ApplyCachedDoorStatesToRemote(remote);
        var variants = Read<GameObject>(remote.native, "VariantObject");
        if (variants != null)
            for (int i = 0; i < variants.transform.childCount; i++) variants.transform.GetChild(i).gameObject.SetActive(i == remote.state.variant);
        remote.visualsApplied = false;
        Debug.Log(Prefix + "Created boardable boat: owner=" + remote.state.owner + " boat=" + remote.state.boat + " hull=" + remote.state.hull);
    }
    static bool IsInteractionMarker(GameObject marker, uint modelId)
    {
        if (marker == null || string.IsNullOrEmpty(marker.name))
            return false;

        string expected = GameObjectHelper.GetGoModelName(modelId);
        return marker.name.StartsWith(expected, StringComparison.Ordinal);
    }

    List<GameObject> GetInteractionMarkers(object native, uint modelId)
    {
        var result = new List<GameObject>();
        var seen = new HashSet<GameObject>();

        // Current CSA source puts both 112401 board markers and 112403 door markers
        // into BoardTriggers. Also inspect DoorTriggers so this remains compatible if
        // that bookkeeping is corrected in a future CSA release.
        foreach (string fieldName in new[] { "BoardTriggers", "DoorTriggers" })
        {
            IList list = Get(native, fieldName) as IList;
            if (list == null)
                continue;

            foreach (object value in list)
            {
                GameObject marker = value as GameObject;
                if (marker == null || seen.Contains(marker) ||
                    !IsInteractionMarker(marker, modelId))
                    continue;

                seen.Add(marker);
                result.Add(marker);
            }
        }

        return result;
    }

    static void EnableInteractionMarker(GameObject marker)
    {
        if (marker == null)
            return;

        marker.SetActive(true);
        Collider[] colliders = marker.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (collider == null)
                continue;
            collider.enabled = true;
            collider.isTrigger = true;
        }
    }

    static Transform ResolveBoardPosition(GameObject marker)
    {
        if (marker == null || marker.transform.parent == null)
            return null;

        Transform t = marker.transform;
        int index = t.GetSiblingIndex();
        if (index <= 0)
            return null;

        Transform candidate = t.parent.GetChild(index - 1);
        return candidate != null && candidate.name == "BoardPosition"
            ? candidate
            : null;
    }

    static Animator ResolveDoorAnimator(GameObject marker)
    {
        if (marker == null || marker.transform.parent == null)
            return null;

        return marker.transform.parent.GetComponent<Animator>();
    }

    void AttachRemoteInteraction(
        GameObject marker,
        CSAMPBoatInteraction.Kind kind,
        uint owner,
        uint boat,
        ushort door,
        Transform boardPosition,
        Animator doorAnimator)
    {
        if (marker == null)
            return;

        EnableInteractionMarker(marker);

        Collider[] colliders = marker.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (collider == null)
                continue;

            CSAMPBoatInteraction interaction =
                collider.gameObject.GetComponent<CSAMPBoatInteraction>();
            if (interaction == null)
                interaction = collider.gameObject.AddComponent<CSAMPBoatInteraction>();

            interaction.Configure(
                this, kind, owner, boat, door, false,
                boardPosition, doorAnimator);
        }
    }

    LocalBoat FindLocalBoat(uint boatId)
    {
        foreach (LocalBoat local in locals.Values)
            if (local != null && local.id == boatId)
                return local;
        return null;
    }

    void SetOwnedBoatDriveTriggerEnabled(uint boatId, bool enabled)
    {
        LocalBoat local = FindLocalBoat(boatId);
        if (local == null || local.native == null)
            return;

        GameObject marker = Read<GameObject>(local.native, "DriveTrigger");
        if (marker == null)
            return;

        marker.SetActive(true);
        foreach (Collider collider in marker.GetComponentsInChildren<Collider>(true))
            if (collider != null)
                collider.enabled = enabled;
    }

    void UpdateHiddenOwnedBoatMapPixel(
        uint owner, uint boat, double worldX, double worldZ)
    {
        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer == null || localPlayer.netId != owner ||
            !Finite(worldX) || !Finite(worldZ))
            return;

        uint driver;
        if (!clientDrivers.TryGetValue(Key(owner, boat), out driver) ||
            driver == 0 || driver == owner)
            return;

        LocalBoat local = FindLocalBoat(boat);
        if (local == null || !local.hiddenForRemoteDriver || local.native == null)
            return;

        try
        {
            FieldInfo mapPixelField = boatType.GetField("MapPixel", Fields);
            if (mapPixelField == null)
                return;

            var mapPixel = DaggerfallConnect.Arena2.MapsFile.WorldCoordToMapPixel(
                (int)Math.Round(worldX),
                (int)Math.Round(worldZ));
            mapPixelField.SetValue(local.native, mapPixel);
        }
        catch (Exception e)
        {
            Warn("Updating hidden owner boat map pixel: " +
                e.GetBaseException().Message);
        }
    }

    static void CopyLocalRotations(IList source, IList destination)
    {
        if (source == null || destination == null)
            return;

        int count = Math.Min(source.Count, destination.Count);
        for (int i = 0; i < count; i++)
        {
            Transform src = source[i] as Transform;
            Transform dst = destination[i] as Transform;
            if (src != null && dst != null)
                dst.localRotation = src.localRotation;
        }
    }

    void CopyObserverStateToHiddenOwnedBoat(LocalBoat local, RemoteBoat remote)
    {
        if (local == null || remote == null ||
            local.native == null || remote.native == null)
            return;

        GameObject realMesh = Read<GameObject>(local.native, "MeshObject");
        if (realMesh != null && remote.mesh != null)
        {
            realMesh.transform.localPosition = remote.mesh.transform.localPosition;
            realMesh.transform.localRotation = remote.mesh.transform.localRotation;
        }

        CopyLocalRotations(
            Get(remote.native, "Booms") as IList,
            Get(local.native, "Booms") as IList);
        CopyLocalRotations(
            Get(remote.native, "Sails") as IList,
            Get(local.native, "Sails") as IList);

        IList observerSails = Get(remote.native, "Sails") as IList;
        IList ownerSails = Get(local.native, "Sails") as IList;
        if (observerSails != null && ownerSails != null)
        {
            int count = Math.Min(observerSails.Count, ownerSails.Count);
            for (int i = 0; i < count; i++)
            {
                Transform src = observerSails[i] as Transform;
                Transform dst = ownerSails[i] as Transform;
                Animator srcAnimator = src == null ? null : src.GetComponent<Animator>();
                Animator dstAnimator = dst == null ? null : dst.GetComponent<Animator>();
                if (srcAnimator == null || dstAnimator == null)
                    continue;

                bool stowed = srcAnimator.GetBool("Stowed");
                if (dstAnimator.GetBool("Stowed") != stowed)
                {
                    dstAnimator.SetBool("Stowed", stowed);
                    dstAnimator.CrossFade(stowed ? "Stowed" : "Unstowed", 0f);
                }
            }
        }

        FieldInfo lightOn = boatType.GetField("LightOn", Fields);
        if (lightOn != null)
            lightOn.SetValue(local.native, remote.state.lights);

        IList ownerLights = Get(local.native, "Lights") as IList;
        if (ownerLights != null)
        {
            foreach (object value in ownerLights)
            {
                Light light = value as Light;
                if (light != null)
                    light.enabled = remote.state.lights;
            }
        }

        GameObject active = Read<GameObject>(local.native, "ActiveObject");
        GameObject idle = Read<GameObject>(local.native, "IdleObject");
        if (active != null)
            active.SetActive(remote.state.driving);
        if (idle != null)
            idle.SetActive(!remote.state.driving);
    }

    void SyncHiddenOwnedBoatFromObserver(
        ulong key, RemoteBoat remote, double worldX, double worldZ)
    {
        if (remote == null || remote.root == null)
            return;

        uint owner = (uint)(key >> 32);
        uint boat = (uint)(key & 0xffffffffUL);

        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer == null || localPlayer.netId != owner)
            return;

        uint driver;
        if (!clientDrivers.TryGetValue(key, out driver) ||
            driver == 0 || driver == owner)
            return;

        LocalBoat local = FindLocalBoat(boat);
        if (local == null || !local.hiddenForRemoteDriver ||
            local.native == null || local.root == null)
            return;

        local.root.transform.position = remote.root.transform.position;
        local.root.transform.rotation = remote.root.transform.rotation;
        CopyObserverStateToHiddenOwnedBoat(local, remote);
        UpdateHiddenOwnedBoatMapPixel(owner, boat, worldX, worldZ);

        if (local.root.activeSelf)
            local.root.SetActive(false);
    }

    void BeginOwnedBoatNetworkFollow(ulong key, uint owner, uint boat)
    {
        LocalBoat local = FindLocalBoat(boat);
        if (local == null || local.native == null || local.root == null)
            return;

        if (local.hiddenForRemoteDriver)
            return;

        if (ReferenceEquals(currentBoat.GetValue(mod), local.native))
        {
            try { stopSailing.Invoke(mod, null); }
            catch (Exception e)
            {
                Warn("Stopping owner for party driver: " +
                    e.GetBaseException().Message);
            }
        }

        RemoteBoat remote;
        if (!remotes.TryGetValue(key, out remote))
        {
            remote = new RemoteBoat();
            remotes.Add(key, remote);
        }

        if (!remote.hasState)
        {
            remote.state = Capture(local.native, local.root, local.id);
            remote.state.owner = owner;
            remote.hasState = true;
        }

        if (!remote.hasMotion)
        {
            CSAMPBoatMotionState seed =
                CaptureMotion(local.native, local.root, local.id);
            seed.owner = owner;
            remote.previousMotion = remote.latestMotion = seed;
            remote.hasMotion = true;
            remote.hasPreviousMotion = false;
            remote.latestMotionReceived = Time.unscaledTime;
            remote.motionInterval = 0.05f;
        }

        remote.received = Time.unscaledTime;

        try
        {
            if (remote.root == null)
                Build(remote);
            if (remote.root == null)
                return;

            GameObject realMesh = Read<GameObject>(local.native, "MeshObject");

            remote.root.transform.position = local.root.transform.position;
            remote.root.transform.rotation = local.root.transform.rotation;
            if (remote.mesh != null && realMesh != null)
            {
                remote.mesh.transform.localPosition = realMesh.transform.localPosition;
                remote.mesh.transform.localRotation = realMesh.transform.localRotation;
            }

            ApplyVisuals(remote, 1f);
            remote.root.SetActive(true);

            ReleasePassenger(local.root.transform);

            local.wasActiveBeforeRemoteDriver = local.root.activeSelf;
            local.hiddenForRemoteDriver = true;
            local.root.SetActive(false);

            Physics.SyncTransforms();

            Debug.Log(Prefix +
                "Owner passenger switched to ordinary observer boat owner=" +
                owner + " boat=" + boat + ".");
        }
        catch (Exception e)
        {
            local.hiddenForRemoteDriver = false;
            if (local.root != null)
                local.root.SetActive(true);
            Warn("Building owner observer boat: " +
                e.GetBaseException().Message);
        }
    }

    void RestoreHiddenOwnedBoat(LocalBoat local, RemoteBoat remote)
    {
        if (local == null || !local.hiddenForRemoteDriver ||
            local.native == null || local.root == null)
            return;

        if (remote != null && remote.root != null)
        {
            local.root.transform.position = remote.root.transform.position;
            local.root.transform.rotation = remote.root.transform.rotation;
            CopyObserverStateToHiddenOwnedBoat(local, remote);
        }

        local.hiddenForRemoteDriver = false;
        local.root.SetActive(true);
        SetOwnedBoatDriveTriggerEnabled(local.id, true);
        local.motionSent = -100f;
        local.visualSent = -100f;
        local.hasMotion = false;

        Physics.SyncTransforms();
    }

    void EndOwnedBoatNetworkFollow(ulong key, uint owner, uint boat)
    {
        LocalBoat local = FindLocalBoat(boat);
        if (local == null || !local.hiddenForRemoteDriver)
            return;

        RemoteBoat remote;
        remotes.TryGetValue(key, out remote);

        if (remote != null && remote.root != null)
            ReleasePassenger(remote.root.transform);

        RestoreHiddenOwnedBoat(local, remote);

        if (remote != null)
        {
            if (remote.root != null)
            {
                remote.root.SetActive(false);
                Destroy(remote.root);
            }
            remote.root = null;
            remote.mesh = null;
            remote.native = null;
            remotes.Remove(key);
        }

        nextMotionSample = 0f;
        nextVisualSample = 0f;

        Debug.Log(Prefix +
            "Owner passenger restored persistent boat owner=" +
            owner + " boat=" + boat + ".");
    }

    void RestoreAllHiddenOwnedBoats()
    {
        foreach (LocalBoat local in locals.Values)
        {
            if (local == null || !local.hiddenForRemoteDriver)
                continue;

            RemoteBoat remote = null;
            PlayerMultiplayer player = PlayerMultiplayer.localPlayer;
            if (player != null)
                remotes.TryGetValue(Key(player.netId, local.id), out remote);

            RestoreHiddenOwnedBoat(local, remote);
        }
    }

    void PublishDrivenRemoteBoat(bool sendMotion, bool sendVisual, float now)
    {
        if (activeRemoteDriveKey == 0 || activeRemoteDrive == null ||
            !activeRemoteDrive.localDriving || activeRemoteDrive.native == null ||
            activeRemoteDrive.root == null || !activeRemoteDrive.root.activeInHierarchy)
            return;

        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer == null)
            return;

        uint driver;
        if (!clientDrivers.TryGetValue(activeRemoteDriveKey, out driver) ||
            driver != localPlayer.netId)
            return;

        uint owner = (uint)(activeRemoteDriveKey >> 32);
        uint boat = (uint)(activeRemoteDriveKey & 0xffffffffUL);

        if (sendVisual)
        {
            CSAMPBoatState state = Capture(
                activeRemoteDrive.native,
                activeRemoteDrive.root,
                boat);
            state.owner = owner;
            NetworkClient.Send(state);
        }

        if (sendMotion)
        {
            CSAMPBoatMotionState motion = CaptureMotion(
                activeRemoteDrive.native,
                activeRemoteDrive.root,
                boat);
            motion.owner = owner;
            NetworkClient.Send(motion, Channels.Unreliable);
        }
    }

    void OnNativeSailingException(string message, string stackTrace, LogType type)
    {
        if (recoveringSteering || (!NetworkClient.active && !NetworkServer.active) ||
            mod == null || nativeDisembarking == null || type != LogType.Exception) return;
        // Unity reports iterator failures outside the reflection Invoke caller.
        if ((message == null || message.IndexOf("StopSailingCoroutine", StringComparison.Ordinal) < 0) &&
            (stackTrace == null || stackTrace.IndexOf("StopSailingCoroutine", StringComparison.Ordinal) < 0)) return;
        try { failedDisembark = nativeDisembarking.GetValue(mod) as IEnumerator; }
        catch { /* Do not throw or log recursively inside Unity's log callback. */ }
    }

    void RecoverFailedDisembark()
    {
        if (failedDisembark == null || mod == null || nativeDisembarking == null || BoatLoadSuspended) return;
        IEnumerator failed = failedDisembark;
        failedDisembark = null;
        // A newer coroutine must never be cancelled because an older one failed.
        if (!ReferenceEquals(nativeDisembarking.GetValue(mod), failed)) return;
        ResetNativeSteering("aborted StopSailingCoroutine", false);
        // Normal lease completion publishes the final pose and returns authority.
        UpdateDriveLeaseClient();
    }

    void ResetNativeSteering(string reason, bool loading)
    {
        if (mod == null || currentBoat == null || recoveringSteering) return;
        recoveringSteering = true;
        try
        {
            object boat = currentBoat.GetValue(mod);
            var routine = nativeDisembarking != null ? nativeDisembarking.GetValue(mod) as IEnumerator : null;
            if (routine != null)
            {
                var behaviour = mod as MonoBehaviour;
                if (behaviour != null) behaviour.StopCoroutine(routine);
                nativeDisembarking.SetValue(mod, null);
            }
            failedDisembark = null;
            if (!GameManager.HasInstance || GameManager.Instance.PlayerObject == null) return;
            var game = GameManager.Instance;
            Transform player = game.PlayerObject.transform;
            Vector3 position = player.position;
            Quaternion rotation = player.rotation;
            GameObject root = Read<GameObject>(boat, "GameObject");
            bool attached = root != null && player.IsChildOf(root.transform);
            // A failed routine can have already cleared CurrentBoat. Recognize only
            // a CSA boat parent; never unparent an unrelated system's player object.
            if (!attached && allBoats != null)
            {
                var boats = allBoats.GetValue(mod) as IList;
                if (boats != null)
                    foreach (object candidate in boats)
                    {
                        GameObject candidateRoot = Read<GameObject>(candidate, "GameObject");
                        if (candidateRoot != null && player.IsChildOf(candidateRoot.transform))
                        { attached = true; break; }
                    }
            }
            if (!attached && activeRemoteDrive != null && activeRemoteDrive.root != null)
                attached = player.IsChildOf(activeRemoteDrive.root.transform);
            bool controlled = boat != null || attached || activeRemoteDriveKey != 0;
            bool stopped = false;
            if (boat != null)
            {
                try { stopSailing.Invoke(mod, null); stopped = true; }
                catch (Exception e)
                {
                    Debug.LogWarning(Prefix + "[SteeringRecovery] Native stop failed: " + e.GetBaseException().Message);
                }
            }
            // Clear stale references even when native cleanup touched a destroyed
            // particle/boat object. These writes affect CSA steering state only.
            currentBoat.SetValue(mod, null);
            if (controlled && !stopped)
            {
                SetNativeSteeringField("MoveVectorCurrent", Vector3.zero);
                SetNativeSteeringField("MoveVectorTarget", Vector3.zero);
                SetNativeSteeringField("TurnCurrent", 0f);
                SetNativeSteeringField("TurnTarget", 0f);
                SetNativeSteeringField("sailPosition", 0);
                GameObject active = Read<GameObject>(boat, "ActiveObject");
                GameObject idle = Read<GameObject>(boat, "IdleObject");
                if (active != null) active.SetActive(false);
                if (idle != null) idle.SetActive(true);
                var resetTime = modType.GetMethod("ResetTimeScale", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(bool) }, null);
                try { if (resetTime != null) resetTime.Invoke(mod, new object[] { false }); }
                catch (Exception e) { Debug.LogWarning(Prefix + "[SteeringRecovery] Time reset: " + e.GetBaseException().Message); }
            }
            if (controlled)
            {
                if (attached) player.SetParent(null, true);
                // Cleanup must not pull a player back to an old helm/save position.
                player.position = position;
                player.rotation = rotation;
                var ground = player.GetComponent<PlayerGroundMotor>();
                if (ground != null) ground.ClearActivePlatform();
                game.PlayerMotor.FreezeMotor = 0f;
                game.PlayerMotor.smoothFollowerLerpSpeed = 25f;
                var footsteps = player.GetComponent<PlayerFootsteps>();
                if (footsteps != null) footsteps.enabled = true;
                if (game.MainCamera != null && !stopped)
                    game.MainCamera.renderingPath = RenderingPath.DeferredShading;
                Physics.SyncTransforms();
                if (!stopped)
                {
                    // Let swimming/UI integrations observe the recovered stop, too.
                    var eventField = modType.GetField("OnUpdateSailing", BindingFlags.Instance | BindingFlags.NonPublic);
                    var callbacks = eventField != null ? eventField.GetValue(mod) as Action<bool> : null;
                    if (callbacks != null)
                        foreach (Action<bool> callback in callbacks.GetInvocationList())
                        {
                            try { callback(false); }
                            catch (Exception e) { Debug.LogWarning(Prefix + "[SteeringRecovery] Sailing listener: " + e.Message); }
                        }
                }
            }
            Debug.Log(Prefix + "[SteeringRecovery] " + reason + " controlled=" + controlled +
                " cancelledRoutine=" + (routine != null) + " loading=" + loading);
        }
        catch (Exception e) { Debug.LogWarning(Prefix + "[SteeringRecovery] " + e.GetBaseException().Message); }
        finally { recoveringSteering = false; }
    }

    void SetNativeSteeringField(string name, object value)
    {
        var field = modType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) field.SetValue(mod, value);
    }

    void OnBoatStartLoad(SaveData_v1 data)
    {
        // Never let adapter cleanup abort DFU's save restoration/event chain.
        try { BeginBoatLoad(); }
        catch (Exception e) { Debug.LogWarning(Prefix + "[SteeringRecovery] Load cleanup: " + e.GetBaseException().Message); }
    }

    void BeginBoatLoad()
    {
        if (!isActiveAndEnabled || (!NetworkClient.active && !NetworkServer.active)) return;
        RestoreBoatPhysicsCollection();
        Bind();
        if (mod == null) return;
        boatLoadInProgress = true;
        RemoveNetworkActorsFromBoatCleanup();
        ResetNativeSteering("save load start", true);
        // Release leases BEFORE removing replicas; don't invoke native stop again.
        if (activeRemoteDrive != null) activeRemoteDrive.localDriving = false;
        activeRemoteDrive = null;
        activeRemoteDriveKey = 0;
        uint localId = PlayerMultiplayer.GetLocalPlayerNetIdSafe();
        if (NetworkClient.ready)
        {
            foreach (var lease in clientDrivers)
                if (lease.Value == localId)
                {
                    loadReleasedLeases.Add(lease.Key);
                    NetworkClient.Send(new CSAMPDriveRequest { owner = (uint)(lease.Key >> 32),
                        boat = (uint)lease.Key, release = true });
                }
            foreach (var local in locals.Values)
                NetworkClient.Send(new CSAMPBoatState { boat = local.id, removed = true });
            if (sentDeckAttachment) NetworkClient.Send(new CSAMPDeckPose { attached = false });
        }
        RestoreAllHiddenOwnedBoats();
        ClearRemotes();
        locals.Clear();
        clientDrivers.Clear();
        clientDoorStates.Clear();
        remoteDeckPoses.Clear();
        sentDeckAttachment = false;
        releasingRemoteDrive = false;
        releasingRemoteDriveKey = 0;
        ClearSeamSupport("save load");
        ClearPausedDeckAttachment();
        RestoreRemoteDeckFollowerSpeed();
        RestoreAllRemoteDriverPresentationOverrides();
        ClearEnemyPassengerState();
    }

    void OnBoatLoadComplete(SaveData_v1 data)
    {
        if (!boatLoadInProgress) return;
        // CSA may legitimately restore a saved driver on a NEW native boat. Keep
        // that restored state; the old coroutine/leases were cleared before load.
        boatLoadInProgress = false;
        failedDisembark = null;
        nextSubscribe = nextMotionSample = nextVisualSample = 0f;
        if (GameManager.HasInstance && GameManager.Instance.PlayerEntity != null)
            Debug.Log(Prefix + "[SteeringRecovery] load complete; magicka=" +
                GameManager.Instance.PlayerEntity.CurrentMagicka + "/" + GameManager.Instance.PlayerEntity.MaxMagicka +
                " nativeDriver=" + (mod != null && currentBoat.GetValue(mod) != null));
    }

    bool IsNativeDriveDisembarking()
    {
        if (mod == null)
            return false;

        // Current CSA keeps this IEnumerator non-null from StopSailingDelayed()
        // until StopSailingCoroutine reaches OnUpdateSailing(false).
        if (nativeDisembarking != null)
        {
            try
            {
                if (nativeDisembarking.GetValue(mod) != null)
                    return true;
            }
            catch
            {
                // Fall through to the PlayerMotor guard below.
            }
        }

        // Compatibility fallback if CSA ever renames/removes its private coroutine.
        // During delayed disembark CurrentBoat is already null, but CSA continues
        // re-seating PlayerAdvanced at DrivePosition until FreezeMotor reaches zero.
        if (activeRemoteDriveKey != 0 &&
            currentBoat != null &&
            currentBoat.GetValue(mod) == null &&
            GameManager.HasInstance &&
            GameManager.Instance.PlayerMotor != null &&
            GameManager.Instance.PlayerMotor.FreezeMotor > 0f)
            return true;

        return false;
    }

    void UpdateDriveLeaseClient()
    {
        if (!NetworkClient.active || !NetworkClient.ready || !ModAvailable())
            return;

        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        if (local == null || local.netId == 0)
            return;

        // CSA handles C/Transport itself. Detect when it has finished StopSailing()
        // and release the network lease with one final authoritative snapshot.
        if (activeRemoteDriveKey != 0 && activeRemoteDrive != null)
        {
            object activeNative = currentBoat.GetValue(mod);

            // CSA sets CurrentBoat=null near the START of StopSailingCoroutine, then
            // spends roughly the remaining FreezeMotor interval keeping the player at
            // DrivePosition. Do not hand simulation back to the owner during that phase.
            // Releasing here was the remaining "leave wheel" jank in v15d.
            bool nativeDisembarking = IsNativeDriveDisembarking();

            if (!ReferenceEquals(activeNative, activeRemoteDrive.native) &&
                !nativeDisembarking &&
                !releasingRemoteDrive)
            {
                ulong key = activeRemoteDriveKey;
                uint owner = (uint)(key >> 32);
                uint boat = (uint)(key & 0xffffffffUL);
                float now = Time.unscaledTime;

                // Publish the driver's true final pose first.
                PublishDrivenRemoteBoat(true, true, now);

                // The server deliberately does not echo the driver's own packets while
                // they hold the lease, so this RemoteBoat still contains pre-drive
                // snapshots. Seed observer interpolation from the TRUE final local pose
                // before re-enabling it, otherwise the boat snaps back to the old pose.
                CSAMPBoatState finalState =
                    Capture(activeRemoteDrive.native, activeRemoteDrive.root, boat);
                finalState.owner = owner;
                CSAMPBoatMotionState finalMotion =
                    CaptureMotion(activeRemoteDrive.native, activeRemoteDrive.root, boat);
                finalMotion.owner = owner;

                activeRemoteDrive.state = finalState;
                activeRemoteDrive.hasState = true;
                activeRemoteDrive.previousMotion = finalMotion;
                activeRemoteDrive.latestMotion = finalMotion;
                activeRemoteDrive.hasMotion = true;
                activeRemoteDrive.hasPreviousMotion = false;
                activeRemoteDrive.received = now;
                activeRemoteDrive.latestMotionReceived = now;
                activeRemoteDrive.motionInterval = 0.05f;

                // Do not allow a stale moving-platform anchor to convert even a small
                // handoff correction into a huge CharacterController displacement.
                ReleasePassenger(activeRemoteDrive.root.transform);

                releasingRemoteDrive = true;
                releasingRemoteDriveKey = key;

                NetworkClient.Send(new CSAMPDriveRequest {
                    owner = owner,
                    boat = boat,
                    release = true,
                });

                activeRemoteDrive.handoffWaitingForOwnerMotion = true;
                activeRemoteDrive.handoffWaitStarted = now;
                activeRemoteDrive.localDriving = false;
                activeRemoteDriveKey = 0;
                activeRemoteDrive = null;
                return;
            }
        }

        // Re-apply the persistent owner's lease presentation every frame. This also
        // handles the lease packet arriving before Publish() has discovered/indexed
        // the owner's real CSA boat.
        foreach (var lease in clientDrivers)
        {
            uint owner = (uint)(lease.Key >> 32);
            uint boat = (uint)(lease.Key & 0xffffffffUL);
            if (owner != local.netId)
                continue;

            if (lease.Value != 0 && lease.Value != owner)
            {
                BeginOwnedBoatNetworkFollow(lease.Key, owner, boat);
                SetOwnedBoatDriveTriggerEnabled(boat, false);
            }
        }

        if (activeRemoteDriveKey != 0)
            return;

        foreach (var entry in clientDrivers)
        {
            if (entry.Value != local.netId)
                continue;
            if (releasingRemoteDrive && entry.Key == releasingRemoteDriveKey)
                continue;

            RemoteBoat remote;
            if (!remotes.TryGetValue(entry.Key, out remote) ||
                remote == null || remote.native == null || remote.root == null ||
                !remote.root.activeInHierarchy)
                continue;

            StartRemoteDriveLocal(entry.Key, remote);
            break;
        }
    }

    void StartRemoteDriveLocal(ulong key, RemoteBoat remote)
    {
        if (BoatLoadSuspended || IsNativeDriveDisembarking()) return;
        if (remote == null || remote.native == null || remote.root == null ||
            activeRemoteDriveKey != 0)
            return;

        object existing = currentBoat.GetValue(mod);
        if (existing != null && !ReferenceEquals(existing, remote.native))
        {
            DaggerfallUI.AddHUDText("Stop controlling your current boat first.");
            NetworkClient.Send(new CSAMPDriveRequest {
                owner = (uint)(key >> 32),
                boat = (uint)(key & 0xffffffffUL),
                release = true,
            });
            return;
        }

        // If we were previously a normal deck passenger, CSA now owns the camera
        // follower setting. Drop only our override bookkeeping without restoring 25.
        remoteDeckFollowerOverride = false;
        remoteDeckFollowerSavedSpeed = 0f;
        remoteDeckFollowerAppliedSpeed = 0f;
       
        ClearPausedDeckAttachment();

        remote.localDriving = true;
        activeRemoteDriveKey = key;
        activeRemoteDrive = remote;

        try
        {
            startSailing.Invoke(mod, new[] { remote.native });
            Debug.Log(Prefix + "Started party-member remote driving owner=" +
                ((uint)(key >> 32)) + " boat=" + ((uint)(key & 0xffffffffUL)) + ".");
        }
        catch (Exception e)
        {
            ResetNativeSteering("remote helm start failed", false);
            remote.localDriving = false;
            activeRemoteDriveKey = 0;
            activeRemoteDrive = null;
            NetworkClient.Send(new CSAMPDriveRequest {
                owner = (uint)(key >> 32),
                boat = (uint)(key & 0xffffffffUL),
                release = true,
            });
            Warn("Starting remote drive: " + e.GetBaseException().Message);
        }
    }

    void StopRemoteDriveLocal(bool requestRelease)
    {
        if (activeRemoteDriveKey == 0 || activeRemoteDrive == null)
            return;

        ulong key = activeRemoteDriveKey;
        RemoteBoat remote = activeRemoteDrive;

        try
        {
            if (mod != null && currentBoat != null &&
                ReferenceEquals(currentBoat.GetValue(mod), remote.native))
                stopSailing.Invoke(mod, null);
        }
        catch (Exception e)
        {
            Warn("Stopping remote drive: " + e.GetBaseException().Message);
            ResetNativeSteering("remote driver cleanup failed", false);
        }

        if (remote.native != null && remote.root != null)
        {
            uint owner = (uint)(key >> 32);
            uint boat = (uint)(key & 0xffffffffUL);
            CSAMPBoatState finalState = Capture(remote.native, remote.root, boat);
            finalState.owner = owner;
            CSAMPBoatMotionState finalMotion = CaptureMotion(remote.native, remote.root, boat);
            finalMotion.owner = owner;
            remote.state = finalState;
            remote.hasState = true;
            remote.previousMotion = remote.latestMotion = finalMotion;
            remote.hasMotion = true;
            remote.hasPreviousMotion = false;
            remote.received = remote.latestMotionReceived = Time.unscaledTime;
            ReleasePassenger(remote.root.transform);
        }

        if (requestRelease && NetworkClient.active && NetworkClient.ready)
        {
            remote.handoffWaitingForOwnerMotion = true;
            remote.handoffWaitStarted = Time.unscaledTime;
        }

        remote.localDriving = false;
        activeRemoteDriveKey = 0;
        activeRemoteDrive = null;

        if (requestRelease && NetworkClient.active && NetworkClient.ready)
        {
            releasingRemoteDrive = true;
            releasingRemoteDriveKey = key;
            NetworkClient.Send(new CSAMPDriveRequest {
                owner = (uint)(key >> 32),
                boat = (uint)(key & 0xffffffffUL),
                release = true,
            });
        }
    }

    internal void ActivateRemoteDrive(CSAMPBoatInteraction interaction, RaycastHit hit)
    {
        if (BoatLoadSuspended || interaction == null || interaction.BoatOwner == 0 || interaction.BoatId == 0 ||
            hit.distance > PlayerActivate.DefaultActivationDistance ||
            !NetworkClient.active || !NetworkClient.ready)
            return;

        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        if (local == null || local.netId == 0)
            return;

        ulong key = Key(interaction.BoatOwner, interaction.BoatId);

        if (activeRemoteDriveKey == key)
        {
            // Match native CSA: clicking the rudder again stops sailing.
            try
            {
                if (stopSailingDelayed != null)
                    stopSailingDelayed.Invoke(mod, null);
                else
                    StopRemoteDriveLocal(true);
            }
            catch (Exception e)
            {
                Warn("Stopping remote drive from rudder: " + e.GetBaseException().Message);
                StopRemoteDriveLocal(true);
            }
            return;
        }

        uint currentDriver;
        if (clientDrivers.TryGetValue(key, out currentDriver) &&
            currentDriver != 0 && currentDriver != local.netId)
        {
            DaggerfallUI.AddHUDText("Someone else is controlling this boat.");
            return;
        }

        // Party membership is decided by the authoritative server table. Do not
        // reject locally here because the client's party snapshot can briefly lag.
        NetworkClient.Send(new CSAMPDriveRequest {
            owner = interaction.BoatOwner,
            boat = interaction.BoatId,
            release = false,
        });
    }

    // This stream carries only a player's pose relative to their supporting boat.
    // Normal player networking continues; presentation is corrected after its updates.
    Transform BoatPresentationFrame(object native, GameObject root)
    {
        GameObject mesh = Read<GameObject>(native, "MeshObject");
        return mesh != null ? mesh.transform : root.transform;
    }

    bool TryCaptureDeckPose(out CSAMPDeckPose pose)
    {
        pose = new CSAMPDeckPose();
        if (!ModAvailable() || !ExteriorReady()) return false;
        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        if (local == null || !local.isLocalPlayer) return false;
        Transform player = GameManager.Instance.PlayerObject.transform;
        object driven = currentBoat.GetValue(mod);
        Transform support = null;
        CharacterController controller = player.GetComponent<CharacterController>();
        PlayerMotor motor = player.GetComponent<PlayerMotor>();
        if (controller != null && controller.enabled && controller.detectCollisions &&
            motor != null && !motor.IsLevitating &&
            !(motor.IsJumping && motor.MoveDirection.y > 0f))
        {
            Physics.SyncTransforms();
            Bounds bounds = controller.bounds;
            Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            RaycastHit hit;
            if (Physics.Raycast(foot + Vector3.up * 0.18f, Vector3.down, out hit, 0.40f,
                Physics.DefaultRaycastLayers & ~(1 << player.gameObject.layer),
                QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f)
                support = hit.collider.transform;
        }

        foreach (var entry in remotes)
        {
            RemoteBoat boat = entry.Value;
            if (boat.root == null || !boat.root.activeInHierarchy || boat.native == null) continue;
            bool driving = boat.localDriving && ReferenceEquals(driven, boat.native);
            if (!driving && (support == null || !support.IsChildOf(boat.root.transform))) continue;
            Transform frame = BoatPresentationFrame(boat.native, boat.root);
            pose = MakeDeckPose(local.netId, boat.state.owner, boat.state.boat, frame, player);
            return true;
        }
        foreach (LocalBoat boat in locals.Values)
        {
            if (boat.root == null || !boat.root.activeInHierarchy || boat.hiddenForRemoteDriver) continue;
            bool driving = ReferenceEquals(driven, boat.native);
            if (!driving && (support == null || !support.IsChildOf(boat.root.transform))) continue;
            Transform frame = BoatPresentationFrame(boat.native, boat.root);
            pose = MakeDeckPose(local.netId, local.netId, boat.id, frame, player);
            return true;
        }
        return false;
    }

    static CSAMPDeckPose MakeDeckPose(uint playerId, uint owner, uint boat,
        Transform frame, Transform player)
    {
        return new CSAMPDeckPose {
            player = playerId, owner = owner, boat = boat, attached = true,
            position = frame.InverseTransformPoint(player.position),
            rotation = Quaternion.Inverse(frame.rotation) * player.rotation,
        };
    }

    void ReceiveDeckPoseFromPlayer(NetworkConnection connection, CSAMPDeckPose pose)
    {
        if (!AcceptOwnerPacket(connection)) return;
        // Never trust a packet-supplied player ID, including on a listen host.
        pose.player = connection.identity.netId;
        if (pose.attached)
        {
            CachedBoat boat;
            if (!ValidVector(pose.position) || pose.position.sqrMagnitude > 40000f ||
                !ValidRotation(pose.rotation) ||
                !cache.TryGetValue(Key(pose.owner, pose.boat), out boat) || boat.state.removed)
                return;
        }
        foreach (NetworkConnection viewer in viewers.Values)
            if (viewer != null && viewer.isReady && viewer.identity != null &&
                viewer.identity.netId != pose.player)
                viewer.Send(pose);
    }

    void ReceiveDeckPose(NetworkConnection connection, CSAMPDeckPose pose)
    {
        if (pose.player == PlayerMultiplayer.GetLocalPlayerNetIdSafe()) return;
        if (!pose.attached)
        {
            remoteDeckPoses.Remove(pose.player);
            return;
        }
        if (!ValidVector(pose.position) || !ValidRotation(pose.rotation)) return;
        RemoteDeckPose state;
        float now = Time.unscaledTime;
        bool existing = remoteDeckPoses.TryGetValue(pose.player, out state);
        if (!existing)
        {
            state = new RemoteDeckPose();
            remoteDeckPoses.Add(pose.player, state);
        }
        bool sameBoat = existing && state.target.owner == pose.owner &&
            state.target.boat == pose.boat && now - state.received < 1f;
        if (sameBoat)
        {
            float t = Mathf.Clamp01((now - state.received) / state.interval);
            state.fromPosition = Vector3.Lerp(state.fromPosition, state.target.position, t);
            state.fromRotation = Quaternion.Slerp(state.fromRotation, state.target.rotation, t);
            state.interval = Mathf.Clamp(now - state.received, 0.035f, 0.15f);
        }
        else
        {
            state.fromPosition = pose.position;
            state.fromRotation = pose.rotation;
            state.interval = 0.05f;
        }
        state.target = pose;
        state.received = now;
    }

    bool TryResolveDeckFrame(CSAMPDeckPose pose, out Transform frame)
    {
        frame = null;
        RemoteBoat remote;
        if (remotes.TryGetValue(Key(pose.owner, pose.boat), out remote) &&
            remote.root != null && remote.root.activeInHierarchy && remote.native != null)
        {
            frame = BoatPresentationFrame(remote.native, remote.root);
            return true;
        }
        if (pose.owner == PlayerMultiplayer.GetLocalPlayerNetIdSafe())
        {
            LocalBoat local = FindLocalBoat(pose.boat);
            if (local != null && local.root != null && local.root.activeInHierarchy &&
                !local.hiddenForRemoteDriver)
            {
                frame = BoatPresentationFrame(local.native, local.root);
                return true;
            }
        }
        return false;
    }

    internal void UpdateDeckPresentationLate()
    {
        if (BoatLoadSuspended) return;
        if (!isActiveAndEnabled || !NetworkClient.active || !NetworkClient.ready)
        {
            remoteDeckPoses.Clear();
            sentDeckAttachment = false;
            return;
        }
        float now = Time.unscaledTime;
        // Run after CSA's LateUpdate: owner boats move there, after normal proxy copying.
        CSAMPDeckPose pose;
        bool attached = TryCaptureDeckPose(out pose);
        if (attached && (now >= nextDeckPose || !sentDeckAttachment))
        {
            NetworkClient.Send(pose);
            nextDeckPose = now + 0.05f;
        }
        else if (!attached && sentDeckAttachment)
            NetworkClient.Send(new CSAMPDeckPose { attached = false });
        sentDeckAttachment = attached;

        expiredDeckPoses.Clear();
        foreach (var entry in remoteDeckPoses)
        {
            RemoteDeckPose state = entry.Value;
            NetworkIdentity identity;
            if (now - state.received > 1f ||
                !NetworkClient.spawned.TryGetValue(entry.Key, out identity) || identity == null)
            {
                expiredDeckPoses.Add(entry.Key);
                continue;
            }
            if (!ExteriorReady()) continue;
            PlayerMultiplayer player = identity.GetComponent<PlayerMultiplayer>();
            if (player == null || player.isLocalPlayer) continue;
            Transform frame;
            if (!TryResolveDeckFrame(state.target, out frame)) continue;
            float t = Mathf.Clamp01((now - state.received) / state.interval);
            player.transform.position = frame.TransformPoint(
                Vector3.Lerp(state.fromPosition, state.target.position, t));
            Quaternion rotation = frame.rotation *
                Quaternion.Slerp(state.fromRotation, state.target.rotation, t);
            // Network avatars remain upright just like the actual character motor.
            player.transform.rotation = Quaternion.FromToRotation(rotation * Vector3.up,
                Vector3.up) * rotation;
        }
        foreach (uint id in expiredDeckPoses) remoteDeckPoses.Remove(id);
    }


    internal void UpdateRemoteDriverPresentationLate()
    {
        if (BoatLoadSuspended) return;
        remoteDriverPresentationActive.Clear();

        if (!NetworkClient.active || !NetworkClient.ready)
        {
            RestoreAllRemoteDriverPresentationOverrides();
            return;
        }

        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        uint localNetId = local != null ? local.netId : 0U;

        foreach (KeyValuePair<ulong, uint> lease in clientDrivers)
        {
            uint driverNetId = lease.Value;
            if (driverNetId == 0 || driverNetId == localNetId)
                continue;

            RemoteBoat remote;
            if (!remotes.TryGetValue(lease.Key, out remote) ||
                remote == null || remote.native == null || remote.root == null ||
                !remote.root.activeInHierarchy)
                continue;

            GameObject drivePosition = Read<GameObject>(remote.native, "DrivePosition");
            if (drivePosition == null)
                continue;

            NetworkIdentity playerIdentity;
            if (!NetworkClient.spawned.TryGetValue(driverNetId, out playerIdentity) ||
                playerIdentity == null)
                continue;

            PlayerMultiplayer player = playerIdentity.GetComponent<PlayerMultiplayer>();
            if (player == null || player.isLocalPlayer)
                continue;

            // The driver's real PlayerAdvanced is rigidly re-seated to this exact
            // transform by CSA every Update. Mirror the same relationship on observers
            // after NetworkTransform/LateUpdate rather than letting two independently
            // interpolated streams visibly fight at the helm.
            player.transform.position = drivePosition.transform.position;

            SpriteMultiplayer presentation =
                player.GetComponent<SpriteMultiplayer>();
            if (presentation != null)
            {
                float original;
                if (!remoteDriverSavedMinDistance.TryGetValue(driverNetId, out original))
                {
                    original = presentation.minDistance;
                    remoteDriverSavedMinDistance.Add(driverNetId, original);
                }

                // SpriteMultiplayer's movement detector uses WORLD-space displacement.
                // A perfectly stationary person on a moving boat therefore "walks".
                // While at the helm, movement animation must be relative to the boat.
                presentation.minDistance = float.MaxValue;

                if (presentation.sprite != null &&
                    !presentation.sprite.IsPlayingOneShot())
                {
                    presentation.sprite.ChangeEnemyState(
                        DaggerfallWorkshop.MobileStates.Idle);
                }
            }

            remoteDriverPresentationActive.Add(driverNetId);
        }

        if (remoteDriverSavedMinDistance.Count == 0)
            return;

        List<uint> restore = null;
        foreach (KeyValuePair<uint, float> entry in remoteDriverSavedMinDistance)
        {
            if (remoteDriverPresentationActive.Contains(entry.Key))
                continue;

            if (restore == null)
                restore = new List<uint>();
            restore.Add(entry.Key);
        }

        if (restore == null)
            return;

        for (int i = 0; i < restore.Count; i++)
            RestoreRemoteDriverPresentationOverride(restore[i]);
    }

    void RestoreRemoteDriverPresentationOverride(uint playerNetId)
    {
        float original;
        if (!remoteDriverSavedMinDistance.TryGetValue(playerNetId, out original))
            return;

        NetworkIdentity identity;
        if (NetworkClient.active &&
            NetworkClient.spawned.TryGetValue(playerNetId, out identity) &&
            identity != null)
        {
            SpriteMultiplayer presentation =
                identity.GetComponent<SpriteMultiplayer>();
            if (presentation != null)
                presentation.minDistance = original;
        }

        remoteDriverSavedMinDistance.Remove(playerNetId);
    }

    void RestoreAllRemoteDriverPresentationOverrides()
    {
        if (remoteDriverSavedMinDistance.Count == 0)
            return;

        List<uint> ids = new List<uint>(remoteDriverSavedMinDistance.Keys);
        for (int i = 0; i < ids.Count; i++)
            RestoreRemoteDriverPresentationOverride(ids[i]);

        remoteDriverPresentationActive.Clear();
    }

    void ConfigureRemoteInteractions(RemoteBoat remote)
    {
        if (remote == null || remote.native == null)
            return;

        GameObject driveMarker = Read<GameObject>(remote.native, "DriveTrigger");
        if (driveMarker != null)
        {
            AttachRemoteInteraction(
                driveMarker,
                CSAMPBoatInteraction.Kind.Drive,
                remote.state.owner,
                remote.state.boat,
                0,
                null,
                null);
        }

        List<GameObject> boardMarkers = GetInteractionMarkers(remote.native, 112401);
        foreach (GameObject marker in boardMarkers)
        {
            Transform boardPosition = ResolveBoardPosition(marker);
            if (boardPosition == null)
            {
                marker.SetActive(false);
                continue;
            }

            AttachRemoteInteraction(
                marker,
                CSAMPBoatInteraction.Kind.Board,
                remote.state.owner,
                remote.state.boat,
                0,
                boardPosition,
                null);
        }

        List<GameObject> doorMarkers = GetInteractionMarkers(remote.native, 112403);
        for (int i = 0; i < doorMarkers.Count && i < 64; i++)
        {
            GameObject marker = doorMarkers[i];
            Animator animator = ResolveDoorAnimator(marker);
            if (animator == null)
            {
                marker.SetActive(false);
                continue;
            }

            AttachRemoteInteraction(
                marker,
                CSAMPBoatInteraction.Kind.Door,
                remote.state.owner,
                remote.state.boat,
                (ushort)i,
                null,
                animator);
        }
    }

    void ConfigureLocalDoorInteractions(LocalBoat local)
    {
        if (local == null || local.native == null || local.id == 0 ||
            !NetworkClient.active || !NetworkClient.ready)
            return;

        PlayerMultiplayer player = PlayerMultiplayer.localPlayer;
        if (player == null || player.netId == 0)
            return;

        List<GameObject> doorMarkers = GetInteractionMarkers(local.native, 112403);
        for (int i = 0; i < doorMarkers.Count && i < 64; i++)
        {
            GameObject marker = doorMarkers[i];
            Animator animator = ResolveDoorAnimator(marker);
            if (animator == null)
                continue;

            // Do not modify native owner triggers. CSA still handles the owner's click.
            // IPlayerActivable runs afterwards and only publishes the resulting state.
            Collider[] colliders = marker.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in colliders)
            {
                if (collider == null)
                    continue;

                CSAMPBoatInteraction interaction =
                    collider.gameObject.GetComponent<CSAMPBoatInteraction>();
                if (interaction == null)
                    interaction = collider.gameObject.AddComponent<CSAMPBoatInteraction>();

                interaction.Configure(
                    this,
                    CSAMPBoatInteraction.Kind.Door,
                    player.netId,
                    local.id,
                    (ushort)i,
                    true,
                    null,
                    animator);
            }
        }
    }

    internal void ActivateRemoteBoarding(CSAMPBoatInteraction interaction, RaycastHit hit)
    {
        if (interaction == null || interaction.BoardPosition == null ||
            hit.distance > PlayerActivate.DefaultActivationDistance ||
            !NetworkClient.active || !NetworkClient.ready ||
            !GameManager.HasInstance || GameManager.Instance.PlayerObject == null)
            return;

        // Native owner boats keep CSA's normal boarding callback. This path is only for
        // a replicated observer copy that CSA intentionally cannot find in AllBoats.
        if (interaction.GetComponentInParent<CSAMPRemoteDeck>() == null)
            return;

        CharacterController controller =
            GameManager.Instance.PlayerObject.GetComponent<CharacterController>();
        if (controller == null)
            return;

        Physics.SyncTransforms();

        Transform target = interaction.BoardPosition;
        GameManager.Instance.PlayerObject.transform.position = target.position;
        GameManager.Instance.PlayerMouseLook.SetHorizontalFacing(target.forward);
        GameObjectHelper.AlignControllerToGround(controller);

        PlayerGroundMotor ground =
            GameManager.Instance.PlayerObject.GetComponent<PlayerGroundMotor>();
        if (ground != null)
            ground.ClearActivePlatform();

        Physics.SyncTransforms();
    }

    internal void ActivateSynchronizedDoor(CSAMPBoatInteraction interaction, RaycastHit hit)
    {
        if (interaction == null || interaction.DoorAnimator == null ||
            interaction.BoatOwner == 0 || interaction.BoatId == 0 ||
            hit.distance > PlayerActivate.DefaultActivationDistance ||
            !NetworkClient.active || !NetworkClient.ready)
            return;

        bool opened;
        if (interaction.NativeOwnerDoor)
        {
            // CSA's own 112403 callback ran first on the real locally-owned boat.
            opened = interaction.DoorAnimator.GetBool("Opened");
        }
        else
        {
            // CSA intentionally ignores the replicated copy because it is not in
            // AllBoats. Apply the requested state immediately for responsiveness;
            // the server echo keeps owner/observers/late joiners consistent.
            opened = !interaction.DoorAnimator.GetBool("Opened");
            interaction.DoorAnimator.SetBool("Opened", opened);
        }

        NetworkClient.Send(new CSAMPDoorRequest {
            owner = interaction.BoatOwner,
            boat = interaction.BoatId,
            door = interaction.DoorIndex,
            opened = opened,
        });
    }

    static void SetDoorStateOnNative(object native, ushort door, bool opened)
    {
        if (native == null)
            return;

        FieldInfo boardField = native.GetType().GetField("BoardTriggers", Fields);
        FieldInfo doorField = native.GetType().GetField("DoorTriggers", Fields);
        var markers = new List<GameObject>();
        var seen = new HashSet<GameObject>();

        foreach (FieldInfo field in new[] { boardField, doorField })
        {
            IList list = field == null ? null : field.GetValue(native) as IList;
            if (list == null)
                continue;

            foreach (object value in list)
            {
                GameObject marker = value as GameObject;
                if (marker == null || seen.Contains(marker) ||
                    !IsInteractionMarker(marker, 112403))
                    continue;

                seen.Add(marker);
                markers.Add(marker);
            }
        }

        if (door >= markers.Count)
            return;

        Animator animator = ResolveDoorAnimator(markers[door]);
        if (animator != null && animator.GetBool("Opened") != opened)
            animator.SetBool("Opened", opened);
    }

    void ApplyDoorState(uint owner, uint boat, ushort door, bool opened)
    {
        PlayerMultiplayer localPlayer = PlayerMultiplayer.localPlayer;
        if (localPlayer != null && localPlayer.netId == owner)
        {
            foreach (LocalBoat local in locals.Values)
            {
                if (local != null && local.id == boat)
                {
                    SetDoorStateOnNative(local.native, door, opened);
                    break;
                }
            }
        }

        RemoteBoat remote;
        if (remotes.TryGetValue(Key(owner, boat), out remote) &&
            remote != null && remote.native != null)
            SetDoorStateOnNative(remote.native, door, opened);
    }

    void ApplyCachedDoorStatesToRemote(RemoteBoat remote)
    {
        if (remote == null || remote.native == null)
            return;

        Dictionary<ushort, bool> doors;
        if (!clientDoorStates.TryGetValue(
            Key(remote.state.owner, remote.state.boat), out doors))
            return;

        foreach (var entry in doors)
            SetDoorStateOnNative(remote.native, entry.Key, entry.Value);
    }

    void ApplyCachedDoorStatesToLocal(LocalBoat local)
    {
        if (local == null || local.native == null)
            return;

        PlayerMultiplayer player = PlayerMultiplayer.localPlayer;
        if (player == null)
            return;

        Dictionary<ushort, bool> doors;
        if (!clientDoorStates.TryGetValue(Key(player.netId, local.id), out doors))
            return;

        foreach (var entry in doors)
            SetDoorStateOnNative(local.native, entry.Key, entry.Value);
    }

    void ApplyVisuals(RemoteBoat remote, float blend)
    {
        var s = remote.state;
        if (!remote.visualsApplied) blend = 1f;
        if (!remote.visualsApplied || remote.driving != s.driving || remote.lights != s.lights)
        {
            var animator = Read<Animator>(remote.native, "RudderAnimator");
            if (animator != null && (!remote.visualsApplied || remote.driving != s.driving))
            {
                string state = s.driving ? "Rowing" : "Disembarked";
                if (animator.HasState(0, Animator.StringToHash(state))) animator.CrossFade(state, 0.2f);
            }
            var active = Read<GameObject>(remote.native, "ActiveObject");
            var idle = Read<GameObject>(remote.native, "IdleObject");
            if (active != null) active.SetActive(s.driving);
            if (idle != null) idle.SetActive(!s.driving);
            var lights = Get(remote.native, "Lights") as IList;
            if (lights != null) foreach (var value in lights) { var light = value as Light; if (light != null) light.enabled = s.lights; }
            remote.driving = s.driving; remote.lights = s.lights; remote.visualsApplied = true;
        }
        ApplyRotations(Get(remote.native, "Booms") as IList, s.boomRotations, blend);
        var sails = Get(remote.native, "Sails") as IList;
        ApplyRotations(sails, s.sailRotations, blend);
        if (sails != null)
            for (int i = 0; i < Math.Min(sails.Count, s.stowed.Length); i++)
            {
                var t = sails[i] as Transform;
                var animator = t == null ? null : t.GetComponent<Animator>();
                if (animator != null && animator.GetBool("Stowed") != s.stowed[i])
                { animator.SetBool("Stowed", s.stowed[i]); animator.CrossFade(s.stowed[i] ? "Stowed" : "Unstowed", 0.2f); }
            }
        var rudder = Read<Animator>(remote.native, "RudderAnimator");
        if (rudder != null)
        {
            SetFloat(rudder, "RowX", s.rowX); SetFloat(rudder, "RowZ", s.rowZ);
            SetFloat(rudder, "RowSpeed", s.rowSpeed); SetFloat(rudder, "TurnAngle", s.turn);
            if (HasParam(rudder, "Sailing", AnimatorControllerParameterType.Bool)) rudder.SetBool("Sailing", s.sailing);
        }
    }
    static void SetFloat(Animator a, string name, float value)
    { if (HasParam(a, name, AnimatorControllerParameterType.Float)) a.SetFloat(name, value); }
    static void ApplyRotations(IList list, Quaternion[] values, float blend)
    {
        if (list == null || values == null) return;
        for (int i = 0; i < Math.Min(list.Count, values.Length); i++)
        { var t = list[i] as Transform; if (t != null) t.localRotation = Quaternion.Slerp(t.localRotation, values[i], blend); }
    }
    void HideRemote(RemoteBoat remote)
    {
        if (remote.root == null) return;
        if (supportedDeck != null && supportedDeck.IsChildOf(remote.root.transform))
            ClearSeamSupport("boat hidden");
        ReleasePassenger(remote.root.transform);
        remote.root.SetActive(false);
    }

    static void ReleasePassenger(Transform root)
    {
        if (!GameManager.HasInstance || GameManager.Instance.PlayerObject == null) return;
        var ground = GameManager.Instance.PlayerObject.GetComponent<PlayerGroundMotor>();
        if (ground != null && ground.ActivePlatform != null &&
            ground.ActivePlatform.IsChildOf(root)) ground.ClearActivePlatform();
    }

    void OnOriginShift(Vector3 offset)
    {
        // DFU has ALREADY shifted the player and its streaming anchor. Capture
        // against the old deck using player-offset, then shift only remote roots.
        Transform deck = supportedDeck;
        bool preserve = deck != null && deck.gameObject.activeInHierarchy &&
            Time.unscaledTime - supportSeenAt <= 0.10f && PassengerCanUseSupport();
        Vector3 localPoint = Vector3.zero;
        if (preserve)
            localPoint = deck.InverseTransformPoint(GameManager.Instance.PlayerObject.transform.position - offset);

        foreach (var remote in remotes.Values)
        {
            if (remote.root == null) continue;
            remote.root.transform.position += offset;
        }
        foreach (var passenger in enemyPassengers.Values)
            if (passenger != null && passenger.hasPreviousBoatPose)
                passenger.previousBoatPosition += offset;
        if (remotes.Count != 0) Physics.SyncTransforms();

        if (preserve)
        {
            // Do not snap back to last tick's pose: that would undo walking. The
            // event-time point includes movement made since the previous carry.
            Vector3 residual = deck.TransformPoint(localPoint) - GameManager.Instance.PlayerObject.transform.position;
            seamSupportSteps = residual.sqrMagnitude < 0.01f ? 2 : 0;
            seamSupportUntil = Time.unscaledTime + 0.15f;
            Debug.Log(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
                " frame=" + Time.frameCount + " origin shift=" + offset +
                " support=" + deck.name + " residual=" + residual + " graceSteps=" + seamSupportSteps);
        }
        else
        {
            ClearSeamSupport("origin shift without recent support");
            if (remotes.Count != 0)
                Debug.Log(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
                    " frame=" + Time.frameCount + " origin shift=" + offset + " support=none");
        }
    }

    bool PassengerCanUseSupport()
    {
        string reason;
        return PassengerCanUseSupport(out reason);
    }

    bool PassengerCanUseSupport(out string reason)
    {
        reason = null;
        if (!isActiveAndEnabled || !NetworkClient.ready || !ExteriorReady() ||
            !GameManager.HasInstance || GameManager.Instance.PlayerObject == null)
        { reason = "player/world not ready"; return false; }
        var player = GameManager.Instance.PlayerObject;
        var controller = player.GetComponent<CharacterController>();
        var motor = player.GetComponent<PlayerMotor>();
        if (controller == null || !controller.enabled || !controller.detectCollisions)
        { reason = "controller missing/disabled or collisions disabled"; return false; }
        if (motor == null) { reason = "motor missing"; return false; }
        if (motor.IsLevitating) { reason = "levitating"; return false; }
        if (motor.IsJumping && motor.MoveDirection.y > 0)
        { reason = "ascending jump"; return false; }
        return true;
    }

    bool IsObserverSupport(Transform deck)
    {
        foreach (var remote in remotes.Values)
            if (!remote.localDriving && remote.positioned && remote.root != null &&
                deck.IsChildOf(remote.root.transform) &&
                !GameManager.Instance.PlayerObject.transform.IsChildOf(remote.root.transform))
                return true;
        return false;
    }

    bool SelectPassengerSupport(out Transform deck)
    {
        deck = null;
        string reason;
        if (!PassengerCanUseSupport(out reason))
        { ClearSeamSupport(reason); return false; }
        if (ProbePassengerFloor(0.18f, 0.40f, out deck, out reason))
        {
            if (!IsObserverSupport(deck))
            {
                ClearSeamSupport("native driver or unpositioned boat");
                deck = null;
                return false;
            }
            if (supportedDeck != deck)
            {
                correctionSupportSteps = 0;
                correctionSupportUntil = 0f;
            }
            supportedDeck = deck;
            supportSeenAt = Time.unscaledTime;
            supportSeenRealtime = Time.realtimeSinceStartup;
            seamSupportSteps = 0;
            // Keep the correction window across the actual movement this tick.
            if (Time.realtimeSinceStartup > correctionSupportUntil)
                correctionSupportSteps = 0;
            return true;
        }

        bool seamGrace = seamSupportSteps > 0 && Time.unscaledTime <= seamSupportUntil;
        bool correctionGrace = correctionSupportSteps > 0 &&
            Time.realtimeSinceStartup <= correctionSupportUntil &&
            Time.realtimeSinceStartup - supportSeenRealtime <= 0.5f;
        if ((seamGrace || correctionGrace) && supportedDeck != null &&
            supportedDeck.gameObject.activeInHierarchy && IsObserverSupport(supportedDeck))
        {
            if (seamGrace) --seamSupportSteps;
            if (correctionGrace) --correctionSupportSteps;
            string extendedReason;
            Transform floor;
            // Same current footprint, not the previous local position. A slightly
            // higher ray start also covers a shallow controller/deck penetration.
            // The FIRST solid hit must be the exact previously confirmed deck:
            // no carrying through encounter geometry or across an open deck edge.
            if (ProbePassengerFloor(0.30f, 0.77f, out floor, out extendedReason) &&
                floor == supportedDeck)
            {
                deck = supportedDeck;
                Debug.Log(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
                    " frame=" + Time.frameCount + " support bridged=" + deck.name +
                    " window=" + (correctionGrace ? "correction" : "origin") +
                    " shortProbe=" + reason);
                return true;
            }
            reason += "; extendedProbe=" + (extendedReason ?? "different deck: " + floor.name);
        }
        else reason += "; no eligible correction/origin grace";
        ClearSeamSupport(reason);
        deck = null;
        return false;
    }

    bool ProbePassengerFloor(float height, float distance, out Transform deck, out string reason)
    {
        deck = null;
        reason = null;
        var player = GameManager.Instance.PlayerObject;
        Physics.SyncTransforms();
        Bounds bounds = player.GetComponent<CharacterController>().bounds;
        Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        RaycastHit hit;
        if (!Physics.Raycast(foot + Vector3.up * height, Vector3.down, out hit, distance,
            Physics.DefaultRaycastLayers & ~(1 << player.layer), QueryTriggerInteraction.Ignore))
        { reason = "floor ray missed; foot=" + foot; return false; }
        if (hit.normal.y <= 0.5f || hit.collider.GetComponentInParent<CSAMPRemoteDeck>() == null)
        {
            reason = "floor ray blocked by " + hit.collider.name + " normalY=" +
                hit.normal.y.ToString("F3") + " gap=" + (hit.distance - height).ToString("F3");
            return false;
        }
        deck = hit.collider.transform;
        return true;
    }

    void ClearSeamSupport(string reason)
    {
        if (supportedDeck != null && Time.unscaledTime >= nextSupportDiagnostic)
        {
            nextSupportDiagnostic = Time.unscaledTime + 1f;
            Debug.Log(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
                " frame=" + Time.frameCount + " detach support=" + supportedDeck.name + " reason=" + reason);
        }
        supportedDeck = null;
        seamSupportSteps = 0;
        supportSeenAt = -100f;
        supportSeenRealtime = -100f;
        correctionSupportSteps = 0;
        correctionSupportUntil = 0f;
    }

    void BindPassengerSwimming()
    {
        if (swimSuppressionEvent != null || !ModAvailable()) return;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("DeepWaters.DeepWaterPlayer", false);
            if (type == null) continue;
            var hook = type.GetEvent("ShouldSuppressOutdoorSwimming", BindingFlags.Public | BindingFlags.Static);
            if (hook == null || hook.EventHandlerType != typeof(Func<bool>)) return;
            try
            {
                var callback = new Func<bool>(IsStandingOnRemoteDeck);
                hook.AddEventHandler(null, callback);
                swimSuppressionEvent = hook;
                swimSuppressionHandler = callback;
            }
            catch (Exception e) { Warn("Passenger water hook: " + e.GetBaseException().Message); }
            return;
        }
    }

    void UnbindPassengerSwimming()
    {
        if (swimSuppressionEvent == null) return;
        try { swimSuppressionEvent.RemoveEventHandler(null, swimSuppressionHandler); }
        catch (Exception e) { Warn("Passenger water cleanup: " + e.GetBaseException().Message); }
        finally { swimSuppressionEvent = null; swimSuppressionHandler = null; }
    }

    // Only the deck's rigid movement is inherited here. PlayerMotor still runs
    // afterwards and applies walking, jumping, gravity and ordinary collisions.
    void CarryRemoteDeckPassenger(Transform deck, Vector3 localPoint, Quaternion oldRotation)
    {
        Transform player = GameManager.Instance.PlayerObject.transform;
        PlayerGroundMotor ground = player.GetComponent<PlayerGroundMotor>();
        if (ground != null)
            ground.ClearActivePlatform(); // Prevent a second application by DFU.

        Vector3 oldPosition = player.position;
        player.position = deck.TransformPoint(localPoint);
        Quaternion rotationDiff = deck.rotation * Quaternion.Inverse(oldRotation);
        rotationDiff = Quaternion.FromToRotation(rotationDiff * player.up, player.up) * rotationDiff;
        player.rotation = rotationDiff * player.rotation;
        Physics.SyncTransforms();
        Vector3 carryDelta = player.position - oldPosition;
        if (carryDelta.sqrMagnitude > 100f * 100f)
            Debug.Log(Prefix + "[DeckSync] realtime=" + Time.realtimeSinceStartup.ToString("F3") +
                " frame=" + Time.frameCount + " passenger carried=" + deck.name + " delta=" + carryDelta);
    }

    bool TryGetRemoteDeckUnderPlayer(out Transform platform, bool requireFootContact = false)
    {
        platform = null;
        if (!isActiveAndEnabled || !NetworkClient.ready || !ExteriorReady()) return false;

        GameObject player = GameManager.Instance.PlayerObject;
        CharacterController controller = player.GetComponent<CharacterController>();
        PlayerMotor motor = player.GetComponent<PlayerMotor>();
        if (controller == null || !controller.enabled || !controller.detectCollisions ||
            motor == null || motor.IsLevitating || (motor.IsJumping && motor.MoveDirection.y > 0))
            return false;

        // Prefer the exact platform already selected by PlayerMotor. This is especially
        // useful on the first paused frame, before another controller collision can occur.
        PlayerGroundMotor ground = player.GetComponent<PlayerGroundMotor>();
        if (!requireFootContact && ground != null && ground.ActivePlatform != null &&
            ground.ActivePlatform.GetComponentInParent<CSAMPRemoteDeck>() != null)
        {
            platform = ground.ActivePlatform;
            return true;
        }

        // Fallback to a short foot ray. A nearby/overhead hull must not qualify.
        Physics.SyncTransforms();
        Bounds bounds = controller.bounds;
        Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        RaycastHit hit;
        if (!Physics.Raycast(
            foot + Vector3.up * 0.18f,
            Vector3.down,
            out hit,
            0.40f,
            Physics.DefaultRaycastLayers & ~(1 << player.layer),
            QueryTriggerInteraction.Ignore))
            return false;

        if (hit.normal.y <= 0.5f ||
            hit.collider.GetComponentInParent<CSAMPRemoteDeck>() == null)
            return false;

        platform = hit.collider.transform;
        return true;
    }

    bool IsStandingOnRemoteDeck()
    {
        Transform platform;
        return TryGetRemoteDeckUnderPlayer(out platform);
    }

    void UpdateRemoteDeckFollowerSpeed()
    {
        if (!GameManager.HasInstance || GameManager.Instance.PlayerObject == null)
        {
            RestoreRemoteDeckFollowerSpeed();
            return;
        }

        PlayerMotor motor = GameManager.Instance.PlayerObject.GetComponent<PlayerMotor>();
        if (motor == null)
        {
            RestoreRemoteDeckFollowerSpeed();
            return;
        }

        Transform platform;
        bool onRemoteDeck = TryGetRemoteDeckUnderPlayer(out platform);

        if (onRemoteDeck)
        {
            if (!remoteDeckFollowerOverride)
            {
                remoteDeckFollowerSavedSpeed = motor.smoothFollowerLerpSpeed;
                remoteDeckFollowerAppliedSpeed =
                    Mathf.Max(remoteDeckFollowerSavedSpeed, RemoteDeckFollowerSpeed);
                remoteDeckFollowerOverride = true;
            }

            // Match CSA's StartSailing() value for a locally-controlled boat.
            motor.smoothFollowerLerpSpeed = remoteDeckFollowerAppliedSpeed;
        }
        else
        {
            RestoreRemoteDeckFollowerSpeed();
        }
    }

    void RestoreRemoteDeckFollowerSpeed()
    {
        if (!remoteDeckFollowerOverride)
            return;

        if (GameManager.HasInstance && GameManager.Instance.PlayerObject != null)
        {
            PlayerMotor motor = GameManager.Instance.PlayerObject.GetComponent<PlayerMotor>();
            if (motor != null &&
                Mathf.Abs(motor.smoothFollowerLerpSpeed - remoteDeckFollowerAppliedSpeed) < 0.01f)
            {
                // Only restore if nobody else changed the value while our override was active.
                motor.smoothFollowerLerpSpeed = remoteDeckFollowerSavedSpeed;
            }
        }

        remoteDeckFollowerOverride = false;
        remoteDeckFollowerSavedSpeed = 0f;
        remoteDeckFollowerAppliedSpeed = 0f;
    }

    bool IsLocalSimulationPaused()
    {
        if (Time.timeScale <= 0.0001f)
            return true;

        if (GameManager.HasInstance && GameManager.IsGamePaused)
            return true;

        try
        {
            if (InputManager.Instance != null && InputManager.Instance.IsPaused)
                return true;
        }
        catch { }

        return false;
    }

    void UpdatePausedRemoteDeckPassenger()
    {
        if (!NetworkClient.active || !NetworkClient.ready || remotes.Count == 0)
        {
            ClearPausedDeckAttachment();
            return;
        }

        bool paused = IsLocalSimulationPaused();
        if (!paused)
        {
            ClearPausedDeckAttachment();
            return;
        }

        // Capture the supporting deck BEFORE moving observer boats to their newest
        // unscaled network pose. Once captured, keep the relation even if the normal
        // collision callback cannot run while FixedUpdate is stopped.
        if (!pausedDeckAttached)
        {
            Transform platform;
            if (TryGetRemoteDeckUnderPlayer(out platform))
            {
                pausedDeckPlatform = platform;
                pausedDeckLocalPosition =
                    platform.InverseTransformPoint(GameManager.Instance.PlayerObject.transform.position);
                pausedDeckPreviousRotation = platform.rotation;
                pausedDeckAttached = true;

                // Its cached platform anchor belongs to the stopped physics timeline.
                // Do not let that stale anchor be applied again when FixedUpdate resumes.
                PlayerGroundMotor ground =
                    GameManager.Instance.PlayerObject.GetComponent<PlayerGroundMotor>();
                if (ground != null)
                    ground.ClearActivePlatform();
            }
        }

        // While paused there is no FixedUpdate, so this is the only time the replicated
        // boats are advanced outside the physics loop.
        UpdateRemoteBoatsPhysics(true);

        if (!pausedDeckAttached || pausedDeckPlatform == null ||
            !pausedDeckPlatform.gameObject.activeInHierarchy ||
            GameManager.Instance.PlayerObject == null)
            return;

        Transform player = GameManager.Instance.PlayerObject.transform;

        // Hold the exact deck-local position captured when pause began.
        player.position = pausedDeckPlatform.TransformPoint(pausedDeckLocalPosition);

        // Match PlayerGroundMotor's rotation rule: inherit platform yaw while keeping
        // the player's local up axis upright instead of following ship roll/pitch.
        Quaternion currentPlatformRotation = pausedDeckPlatform.rotation;
        Quaternion rotationDiff =
            currentPlatformRotation * Quaternion.Inverse(pausedDeckPreviousRotation);
        rotationDiff =
            Quaternion.FromToRotation(rotationDiff * player.up, player.up) * rotationDiff;
        player.rotation = rotationDiff * player.rotation;
        pausedDeckPreviousRotation = currentPlatformRotation;

        Physics.SyncTransforms();

        // PositionMultiplayer normally copies PlayerAdvanced only from FixedUpdate.
        // That loop is stopped during pause, so keep the locally-owned proxy current
        // here. This lets the host continue seeing the paused passenger on the ship.
        PlayerMultiplayer local = PlayerMultiplayer.localPlayer;
        if (local != null && local.isLocalPlayer)
        {
            local.transform.position = player.position;
            local.transform.rotation = player.rotation;
        }
    }

    void ClearPausedDeckAttachment()
    {
        pausedDeckAttached = false;
        pausedDeckPlatform = null;
        pausedDeckLocalPosition = Vector3.zero;
        pausedDeckPreviousRotation = Quaternion.identity;
    }

    void DestroyRemote(RemoteBoat remote)
    {
        if (remote == null)
            return;

        if (remote.localDriving && ReferenceEquals(activeRemoteDrive, remote))
            StopRemoteDriveLocal(true);

        if (remote.hasState)
        {
            PlayerMultiplayer player = PlayerMultiplayer.localPlayer;
            if (player != null && player.netId == remote.state.owner)
            {
                LocalBoat local = FindLocalBoat(remote.state.boat);
                if (local != null && local.hiddenForRemoteDriver)
                    RestoreHiddenOwnedBoat(local, remote);
            }
        }

        if (remote.root != null)
        {
            HideRemote(remote);
            Destroy(remote.root);
        }

        remote.localDriving = false;
        remote.handoffWaitingForOwnerMotion = false;
        remote.handoffWaitStarted = 0f;
        remote.root = null;
        remote.mesh = null;
        remote.native = null;
        remote.positioned = false;
    }
    void ClearRemotes() { foreach (var remote in remotes.Values) DestroyRemote(remote); remotes.Clear(); }
    void OnDisable()
    {
        ClearSeamSupport("helper disabled");
        if (NetworkClient.active && NetworkClient.ready && sentDeckAttachment)
            NetworkClient.Send(new CSAMPDeckPose { attached = false });
        sentDeckAttachment = false;
        remoteDeckPoses.Clear();
        StopRemoteDriveLocal(true);
        RestoreAllHiddenOwnedBoats();
        RestoreAllRemoteDriverPresentationOverrides();
        RestoreBoatPhysicsCollection();
        foreach (var remote in remotes.Values) HideRemote(remote);
        ClearPausedDeckAttachment();
       
        RestoreRemoteDeckFollowerSpeed();
        ClearEnemyPassengerState();
        UnbindPassengerSwimming();
    }
    void OnDestroy()
    {
        SaveLoadManager.OnStartLoad -= OnBoatStartLoad;
        SaveLoadManager.OnLoad -= OnBoatLoadComplete;
        Application.logMessageReceived -= OnNativeSailingException;
        ClearSeamSupport("helper destroyed");
        StopRemoteDriveLocal(false);
        RestoreAllHiddenOwnedBoats();
        RestoreAllRemoteDriverPresentationOverrides();
        FloatingOrigin.OnPositionUpdate -= OnOriginShift;
        ClearPausedDeckAttachment();
       
        RestoreRemoteDeckFollowerSpeed();
        UnbindPassengerSwimming();
        RestoreBoatPhysicsCollection(); ClearRemotes(); ClearEnemyPassengerState();
    }
    void Warn(string message)
    {
        if (Time.unscaledTime < nextWarning) return;
        nextWarning = Time.unscaledTime + 10f;
        Debug.LogWarning(Prefix + message);
    }
}

// CSA's normal wave/current physics still runs, but its FixedUpdate enemy-parent
// scan is replaced by physics-cadence detection plus frame-cadence root-space carrying.
// This guard catches any
// additional CSA/CollisionParenter attempt to reparent an already-networked actor
// (including a player's enemy-model child). Parenting by other systems is accepted.
public sealed class CSAMPActorParentGuard : MonoBehaviour
{
    ComeSailAwayMultiplayerCompatibility owner;
    Type modType;
    Transform savedParent;
    bool armed, restoring, reported;

    internal void Arm(ComeSailAwayMultiplayerCompatibility helper, Type installedModType)
    {
        owner = helper;
        modType = installedModType;
        if (!armed) { savedParent = transform.parent; armed = true; }
    }

    void OnTransformParentChanged()
    {
        if (!armed || restoring) return;
        if (owner == null || !owner.ProtectActorParents || !IsBoatParentingCall())
        {
            savedParent = transform.parent;
            return;
        }
        restoring = true;
        try
        {
            transform.SetParent(savedParent, true);
            if (!reported)
            {
                reported = true;
                Debug.Log("[SailMP v15l] Blocked additional boat actor reparent: " + gameObject.name +
                    "; kept parent=" + (savedParent == null ? "scene root" : savedParent.name));
            }
        }
        finally { restoring = false; }
    }

    bool IsBoatParentingCall()
    {
        var trace = new System.Diagnostics.StackTrace(false);
        for (int i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i).GetMethod();
            var type = method == null ? null : method.DeclaringType;
            if (type == modType || (type != null && modType != null &&
                type.Assembly == modType.Assembly && type.FullName == "ComeSailAwayMod.CollisionParenter")) return true;
        }
        return false;
    }
}

[DefaultExecutionOrder(32000)]
public sealed class CSAMPActorParentCleanup : MonoBehaviour
{
    internal ComeSailAwayMultiplayerCompatibility Owner;

    void Update()
    {
        if (Owner == null) return;
        Owner.EnforceHiddenOwnedBoatsAfterCSAUpdate();
    }

    void FixedUpdate()
    {
        if (Owner == null) return;
        Owner.FinishBoatPhysics();
        Owner.RemoveNetworkActorsFromBoatCleanup();
    }

    void LateUpdate()
    {
        if (Owner == null) return;
        Owner.UpdateRemoteDriverPresentationLate();
        Owner.UpdateDeckPresentationLate();
    }
}

// Consume visual animation events without forwarding them into the local mod singleton.
public sealed class CSAMPVisualOarEvents : MonoBehaviour
{
    public void OarEvent_In() { }
    public void OarEvent_Sweep() { }
    public void OarEvent_Out() { }
}

public sealed class CSAMPBoatInteraction : MonoBehaviour, IPlayerActivable
{
    public enum Kind : byte { Board = 1, Door = 2, Drive = 3 }

    ComeSailAwayMultiplayerCompatibility owner;
    Kind kind;
    uint boatOwner, boatId;
    ushort doorIndex;
    bool nativeOwnerDoor;
    Transform boardPosition;
    Animator doorAnimator;

    internal uint BoatOwner { get { return boatOwner; } }
    internal uint BoatId { get { return boatId; } }
    internal ushort DoorIndex { get { return doorIndex; } }
    internal bool NativeOwnerDoor { get { return nativeOwnerDoor; } }
    internal Transform BoardPosition { get { return boardPosition; } }
    internal Animator DoorAnimator { get { return doorAnimator; } }

    internal void Configure(
        ComeSailAwayMultiplayerCompatibility helper,
        Kind interactionKind,
        uint ownerNetId,
        uint boatNetId,
        ushort door,
        bool isNativeOwnerDoor,
        Transform boardingPosition,
        Animator animator)
    {
        owner = helper;
        kind = interactionKind;
        boatOwner = ownerNetId;
        boatId = boatNetId;
        doorIndex = door;
        nativeOwnerDoor = isNativeOwnerDoor;
        boardPosition = boardingPosition;
        doorAnimator = animator;
    }

    public void Activate(RaycastHit hit)
    {
        if (owner == null)
            return;

        if (kind == Kind.Board)
            owner.ActivateRemoteBoarding(this, hit);
        else if (kind == Kind.Door)
            owner.ActivateSynchronizedDoor(this, hit);
        else if (kind == Kind.Drive)
            owner.ActivateRemoteDrive(this, hit);
    }
}

// Identity only: no parenting, ownership or AI behavior.
public sealed class CSAMPRemoteDeck : MonoBehaviour { }
