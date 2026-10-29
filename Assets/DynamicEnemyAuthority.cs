using System.Collections;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallConnect;
using DaggerfallWorkshop.Game.MagicAndEffects;

[RequireComponent(typeof(NetworkIdentity))]
public class DynamicEnemyAuthority : NetworkBehaviour
{
    [System.NonSerialized] public bool KeepForDungeonGridTest;
    [System.NonSerialized] public bool PassiveDungeonGridTest;

    // Test-only local simulator override. Mirror ownership remains genuine.
    public bool IsDungeonGridObserverTest => NetworkServer.active && PassiveDungeonGridTest;

    public void ConfigureDungeonGridTest(bool observer)
    {
        if (!NetworkServer.active) return;
        KeepForDungeonGridTest = true;
        PassiveDungeonGridTest = observer;
        CancelDestroyCountdown();
        if (observer)
        {
            var identity = GetComponent<NetworkIdentity>();
            if (identity != null && identity.netId != 0 && identity.connectionToClient != null)
                identity.RemoveClientAuthority();
            SetAuthorityDeactivated(false);
        }
        ApplyAuthorityMotionGate();
        RefreshVisualAndAudioState();
        RefreshLocalCollisionState();
    }

            Coroutine _ntResetCo = null;
// One-shot resnap for CreateFoe wave spawns when this client gains authority.
    private bool _didCreateFoeAuthorityResnap = false;
    private bool _didCreateFoeObserverSnap = false;
    private bool _didServerCreateFoeSpawnSettle = false;
    private Coroutine _createFoeAuthorityResnapCo = null;
    private Coroutine _serverCreateFoeSpawnSettleCo = null;
    private float _nextRemotePassiveHoldLogTime = 0f;

    public override void OnStartClient()
    {
        base.OnStartClient();
        // CreateFoe: watch briefly and resnap when data arrives.
        StartCoroutine(CoLateResnapWatcher());
        StartCoroutine(CoLateFixedSpawnObserverSnap());
    }


// ---- WARMUP SETTINGS (pump only; NT stays enabled) ----
    // Sends reliable poses for a short window to "kick" NT after authority flips.
    const float WARMUP_DURATION = 1.25f;   // how long to pump reliable poses
    const float PUMP_STEP       = 0.05f;   // cadence of reliable pose pump (50 ms)

    [Header("Authority Settings")]
    [Tooltip("Interval between authority checks.")]
    public float authorityCheckInterval = 0.2f;

    [Tooltip("Max Daggerfall X/Z distance for switching to another player (DF units).")]
    public float switchDistance = 100f;

    [Tooltip("Max Daggerfall/XZ+UnityY distance before host-owned enemy motor/visuals are deactivated (DF units converted by unityPerDF).")]
    public float deactivateDistance = 100f;

    [Tooltip("Max Daggerfall X/Z distance before enemy is destroyed entirely (DF units).")]
    public float destroyDistance = 300f;

    // Optional exterior retention policy. Zero preserves the normal DF+Y checks.
    // Set by compatibility adapters BEFORE network spawn, in raw DF world units.
    [System.NonSerialized] public float HorizontalRetentionDistanceDF;
    public bool IsAuthorityDeactivatedForRetention => authorityDeactivated;

    private NetworkConnection initialExteriorOwner;
    private float initialExteriorOwnerUntil;

    // Opt-in startup grace for retained exterior spawns. Ownership is assigned by
    // Spawn itself; this only prevents the first proximity tick immediately undoing
    // it while client setup and network position updates are still arriving.
    public void PreserveInitialExteriorOwner(NetworkConnection owner)
    {
        initialExteriorOwner = owner;
        initialExteriorOwnerUntil = Time.unscaledTime + 2f;
    }

    private bool KeepInitialExteriorOwner()
    {
        if (initialExteriorOwner == null || Time.unscaledTime >= initialExteriorOwnerUntil)
        {
            initialExteriorOwner = null;
            return false;
        }
        if (HorizontalRetentionDistanceDF <= 0f || worldPosition == null ||
            !worldPosition.PreserveDistantExteriorCoordinates || worldPosition.isInteriorSpawn || worldPosition.isDungeonSpawn ||
            netIdentity.connectionToClient != initialExteriorOwner || !initialExteriorOwner.isReady ||
            initialExteriorOwner.identity == null) return false;
        var position = initialExteriorOwner.identity.GetComponent<PositionMultiplayer>();
        if (position == null || position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.BuildingInterior ||
            position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.DungeonInterior) return false;
        double dx = ((double)position.x - worldPosition.worldX) * unityPerDF;
        double dz = ((double)position.z - worldPosition.worldZ) * unityPerDF;
        if (dx * dx + dz * dz > 125.0 * 125.0 ||
            Mathf.Abs(initialExteriorOwner.identity.transform.position.y - transform.position.y) > maxYDistance) return false;
        SetAuthorityDeactivated(false);
        return true;
    }

    [Tooltip("Max Unity Y distance difference before deactivation/destroy checks (Unity units).")]
    public float maxYDistance = 100f;

    [Tooltip("Grace period (seconds) before enemy can be destroyed.")]
    public float destroyGracePeriod = 10f;

    // Generic extension points: callers own local membership policy.
    public static event System.Action<DynamicEnemyAuthority> LocalActorUpdate;
    public bool HasLocalOwnershipClaim { get; set; }
    [SyncVar] private uint persistentOwnerPlayerNetId;
    public uint PersistentOwnerPlayerNetId => persistentOwnerPlayerNetId;
    private NetworkConnectionToClient persistentOwnerConnection;

    [Command(requiresAuthority = true)]
    public void CmdReleasePersistentOwnerAtPose(Vector3 position, Quaternion rotation, bool dungeon, bool interior,
        int baseX, int baseZ, int offsetX, int offsetZ, NetworkConnectionToClient sender = null)
    {
        if (sender == null || sender.identity == null || persistentOwnerConnection != sender ||
            persistentOwnerPlayerNetId != sender.identity.netId) return;
        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
            float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z)) return;
        EnemyWorldPosition ewp = GetComponent<EnemyWorldPosition>();
        if (ewp == null) return;

        // One server command commits pose/context BEFORE dropping the binding.
        transform.SetParent(null, true);
        transform.SetPositionAndRotation(position, rotation);
        if (!ewp.ApplyBoundActorContext(dungeon, interior, baseX, baseZ, offsetX, offsetZ, sender)) return;
        ResetNetworkTransformBuffers();
        RpcSnapAll(position, rotation);
        uint previousOwner = persistentOwnerPlayerNetId;
        persistentOwnerPlayerNetId = 0;
        persistentOwnerConnection = null;
        Debug.Log($"[MPBoundActorRelease] enemy='{name}' net={netId} previousOwner={previousOwner} root pose={position} world={ewp.worldX}/{ewp.worldZ}; proximity authority resumed.", this);
        UpdateAuthority();
    }

    [Command(requiresAuthority = false)]
    public void CmdSetPersistentOwner(bool member, NetworkConnectionToClient sender = null)
    {
        SetPersistentOwner(member, sender);
    }

    [Server]
    public void SetPersistentOwner(bool member, NetworkConnectionToClient sender)
    {
        if (sender == null || !sender.isReady || sender.identity == null ||
            sender.identity.GetComponent<PlayerMultiplayer>() == null)
            return;

        if (netIdentity == null)
            netIdentity = GetComponent<NetworkIdentity>();
        if (netIdentity == null)
            return;

        if (!member)
        {
            // A non-owner's hierarchy must never release somebody else's bound actor.
            if (persistentOwnerConnection != sender || persistentOwnerPlayerNetId != sender.identity.netId)
                return;

            uint previousOwner = persistentOwnerPlayerNetId;
            persistentOwnerPlayerNetId = 0;
            persistentOwnerConnection = null;
            Debug.Log($"[MPBoundActorAuthority][Released] enemy='{name}' net={netId} player={previousOwner}; proximity authority resumed.", this);
            UpdateAuthority();
            return;
        }

        DaggerfallEntityBehaviour entity = GetComponent<DaggerfallEntityBehaviour>();
        if (entity == null || !(entity.Entity is EnemyEntity))
            return;

        // Parent trees are local and cannot be verified on the server. Treat this as
        // a co-op client's membership report; first claim wins until that owner releases.
        if (persistentOwnerPlayerNetId != 0 &&
            (persistentOwnerConnection != sender || persistentOwnerPlayerNetId != sender.identity.netId))
        {
            Debug.LogWarning($"[MPBoundActorAuthority][Rejected] enemy='{name}' net={netId} requestedPlayer={sender.identity.netId} boundPlayer={persistentOwnerPlayerNetId}", this);
            return;
        }

        bool newBinding = persistentOwnerPlayerNetId == 0;
        persistentOwnerConnection = sender;
        persistentOwnerPlayerNetId = sender.identity.netId;
        MaintainPersistentAuthority();
        if (newBinding)
            Debug.Log($"[MPBoundActorAuthority][Bound] enemy='{name}' net={netId} player={persistentOwnerPlayerNetId} connection={sender.connectionId}", this);
    }

    // Called before proximity, deactivation, and distance-destruction checks, even
    // while EnemyWorldPosition is initializing. No stale transition distance can
    // transfer a bound actor back to the host.
    private bool MaintainPersistentAuthority()
    {
        if (persistentOwnerPlayerNetId == 0)
            return false;

        NetworkConnectionToClient registeredConnection;
        if (persistentOwnerConnection == null || persistentOwnerConnection.identity == null ||
            persistentOwnerConnection.identity.netId != persistentOwnerPlayerNetId ||
            !NetworkServer.connections.TryGetValue(persistentOwnerConnection.connectionId, out registeredConnection) ||
            registeredConnection != persistentOwnerConnection)
        {
            Debug.Log($"[MPBoundActorAuthority][OwnerGone] Destroying bound actor '{name}' net={netId} player={persistentOwnerPlayerNetId}", this);
            NetworkServer.Destroy(gameObject);
            return true;
        }

        CancelDestroyCountdown();
        SetAuthorityDeactivated(false);

        // Not-ready is not a dismissal or disconnect. Wait for that same owner.
        if (!persistentOwnerConnection.isReady)
            return true;

        if (netIdentity.connectionToClient != persistentOwnerConnection)
        {
            if (netIdentity.connectionToClient != null)
                netIdentity.RemoveClientAuthority();
            { EnemyInterestManagement.PrepareOwner(netIdentity, persistentOwnerConnection); netIdentity.AssignClientAuthority(persistentOwnerConnection); }
            currentOwner = persistentOwnerConnection;
            ApplyAuthorityMotionGate();
            ResetNetworkTransformBuffers();
        }
        ShowVisuals(); // still respects each observer's local culling
        return true;
    }

    [Header("Unit Conversion")]
    [Tooltip("Unity meters per 1 Daggerfall unit. Default assumes 40 DF units = 1 Unity meter.")]
    public float unityPerDF = 1f / 40f; // 0.025

    // Multiplayer large-dungeon optimization:
    // With 100+ enemies, one AuthorityCheckRoutine per enemy can otherwise wake on the
    // same frames and each call FindObjectsOfType<PlayerMultiplayer>(). Cache players
    // globally and stagger authority checks so dungeon spawns do not create periodic
    // 50-100 ms spikes on the host.
    private const float MinEffectiveAuthorityCheckInterval = 0.75f;
    private const float PlayerCacheInterval = 0.50f;
    private const int AuthorityStaggerSlots = 64;

    private static readonly System.Collections.Generic.List<PlayerMultiplayer> cachedPlayers =
        new System.Collections.Generic.List<PlayerMultiplayer>();
    private static float lastPlayerCacheTime = -999f;
    private static int nextAuthoritySlot = 0;

    private int authoritySlot = 0;

    private NetworkIdentity netIdentity;
    private Transform enemyTransform;
    private float nextAuthorityCheckTime = 0f;
    private NetworkConnectionToClient currentOwner;
    private GameObject visual;
    private EnemyWorldPosition worldPosition;
    private float spawnTime;
    private Coroutine destroyCoroutine;

    // --- warmup pump state (NT is never disabled) ---
    Coroutine warmupPumpCo;

    // Server-controlled inactive state for enemies that are no longer close enough
    // for authority simulation, but are not far enough to destroy yet. This is the
    // old "deactivate distance" behaviour reintroduced for MP-root enemies.
    [SyncVar(hook = nameof(OnAuthorityDeactivatedChanged))]
    private bool authorityDeactivated = false;

    private bool deactivateSavedRbStateValid = false;
    private bool deactivateSavedRbKinematic = false;
    private bool deactivateSavedRbUseGravity = false;

    // Local-only per-player culling.
    //
    // This is deliberately separate from authorityDeactivated:
    // - authorityDeactivated is a server/global state used when no player is near enough.
    // - localPerPlayerCulled is only for THIS client/host-client view when this specific
    //   local player is far away in DF space/Y slot while somebody else might still be near.
    //
    // Presentation relevance also gates LOCAL observer collision suspension.
    // The simulation owner retains its physics; ownership selection is unchanged.
    private const float LocalPerPlayerCullInterval = 0.25f;
    // Local visibility/ghost distance for this specific player only.
    // 200 Unity units ~= 8000 DF units with unityPerDF=0.025.
    private const float LocalPerPlayerCullDistanceUnity = 200f;
    // Short startup grace only to avoid one-frame metadata/default-coordinate mistakes.
    private const float LocalPerPlayerCullSpawnGrace = 1.5f;
    // Only enemies in the actual underground network dungeon band should use dungeon/Y-slot rules.
    // Some exterior/interior spawn paths can accidentally mark SetupDemoEnemy.isDungeonEnemy while
    // a Dungeon object exists in scene; do not let that force Y-only culling/targeting above ground.
    private const float NetworkDungeonModeYThreshold = -300f;
    private bool localPerPlayerCulled = false;
    public bool IsLocalPresentationSuspended => authorityDeactivated || localPerPlayerCulled ||
        (netId != 0 && NetworkServer.active && !NetworkClient.active);
    // Suspend Unity ticks only for hidden observers; networking stays alive.
    private EnemySenses observerSenses;
    private EnemyAttack observerAttack;
    private bool observerComponentsCached;
    private bool suspendedMotor, suspendedSenses, suspendedAttack;

    private static void SetObserverTickSuspended(Behaviour component, bool suspend, ref bool changed)
    {
        if (component == null) { changed = false; return; }
        if (suspend)
        {
            // Remember only components this feature actually disabled.
            if (component.enabled) { changed = true; component.enabled = false; }
        }
        else if (changed)
        {
            changed = false;
            component.enabled = true;
        }
    }

    private void RefreshHiddenObserverTicks(bool visible)
    {
        // Leave time for all native Start methods and spawn setup to complete.
        bool suspend = retainedRuntimeStarted && netId != 0 &&
            (NetworkServer.active || NetworkClient.active) && !visible &&
            Time.time - spawnTime >= LocalPerPlayerCullSpawnGrace &&
            presentationMotor != null && !presentationMotor.ShouldSimulateMotor();
        if (!observerComponentsCached)
        {
            if (!suspend) return;
            observerSenses = GetComponent<EnemySenses>();
            observerAttack = GetComponent<EnemyAttack>();
            observerComponentsCached = true;
        }
        bool wasSuspended = suspendedMotor || suspendedSenses || suspendedAttack;
        if (observerAttack != null && (suspend != wasSuspended))
            observerAttack.RefreshObserverCombatOwnership();
        // Custom subclasses may have their own lifecycle or observer work.
        if (presentationMotor != null && presentationMotor.GetType() == typeof(EnemyMotor))
            SetObserverTickSuspended(presentationMotor, suspend, ref suspendedMotor);
        if (observerSenses != null && observerSenses.GetType() == typeof(EnemySenses))
            SetObserverTickSuspended(observerSenses, suspend, ref suspendedSenses);
        if (observerAttack != null && observerAttack.GetType() == typeof(EnemyAttack))
            SetObserverTickSuspended(observerAttack, suspend, ref suspendedAttack);
    }

    private void RestoreHiddenObserverTicks()
    {
        if (observerAttack != null && (suspendedMotor || suspendedSenses || suspendedAttack))
            observerAttack.RefreshObserverCombatOwnership();
        SetObserverTickSuspended(presentationMotor, false, ref suspendedMotor);
        SetObserverTickSuspended(observerSenses, false, ref suspendedSenses);
        SetObserverTickSuspended(observerAttack, false, ref suspendedAttack);
    }

    private void OnDisable()
    {
        RestoreObserverRuntime();
        RestoreObserverCollisions();
        RestoreHiddenObserverTicks();
    }

    // Coordinated observer dormancy. Root GameObject, network identity, SyncVars,
    // RPCs, quest resources, death handlers and transform synchronization stay alive.
    private static bool observerRuntimeEnabled = true;
    private static bool observerRuntimeCommandRegistered;
    [SerializeField] private bool observerRuntimeDormant;
    public bool IsObserverRuntimeDormant => observerRuntimeDormant;
    private float observerRuntimeWakeUntil;
    private bool observerRuntimeCached;
    private DaggerfallEntityBehaviour dormantEntity;
    private SetupDemoEnemy dormantSetup;
    private EntityEffectManager dormantEffects;
    private bool dormantEntityChanged, dormantSetupChanged, dormantEffectsChanged;
    private Behaviour[] dormantCosmetics;
    private bool[] dormantCosmeticChanged;

    // Explicit wake keeps existing network messages and effect/death entry points live.
    public void WakeObserverRuntime()
    {
        observerRuntimeWakeUntil = Time.unscaledTime + 1f;
        RestoreObserverRuntime();
    }

    private bool CanUseObserverRuntime(bool visible)
    {
        if (!observerRuntimeEnabled || observerRuntimeFaulted || !isActiveAndEnabled || !retainedRuntimeStarted || netId == 0 ||
            (!NetworkServer.active && !NetworkClient.active) || visible ||
            Time.time - spawnTime < LocalPerPlayerCullSpawnGrace ||
            Time.unscaledTime < observerRuntimeWakeUntil || observerPhysicsSettleDepth != 0 ||
            HasLocalOwnershipClaim || persistentOwnerPlayerNetId != 0 ||
            presentationMotor == null || presentationMotor.GetType() != typeof(EnemyMotor) ||
            presentationMotor.ShouldSimulateMotor()) return false;
        var saves = DaggerfallWorkshop.Game.Serialization.SaveLoadManager.Instance;
        if (saves != null && saves.LoadInProgress) return false;
        return true;
    }

    private void CacheObserverRuntime()
    {
        if (observerRuntimeCached) return;
        observerRuntimeCached = true;
        dormantEntity = GetComponent<DaggerfallEntityBehaviour>();
        dormantSetup = GetComponent<SetupDemoEnemy>();
        dormantEffects = GetComponent<EntityEffectManager>();
        var cosmetics = new System.Collections.Generic.List<Behaviour>();
        foreach (Behaviour component in GetComponentsInChildren<Behaviour>(true))
        {
            if (component == null || component.GetComponentInParent<NetworkIdentity>() != netIdentity) continue;
            System.Type type = component.GetType();
            if (type == typeof(DaggerfallAudioSource) || type == typeof(EnemySounds) ||
                type == typeof(EntityConcealmentBehaviour)) cosmetics.Add(component);
        }
        dormantCosmetics = cosmetics.ToArray();
        dormantCosmeticChanged = new bool[dormantCosmetics.Length];
    }

    private void RefreshObserverRuntime(bool visible)
    {
        if (!CanUseObserverRuntime(visible))
        {
            RestoreObserverRuntime();
            return;
        }
        CacheObserverRuntime();
        // Respect custom subclasses, incomplete spawn setup, and external disabled states.
        if (dormantEntity == null || dormantEntity.GetType() != typeof(DaggerfallEntityBehaviour) ||
            dormantEntity.Entity == null || dormantEntity.Entity.GetType() != typeof(EnemyEntity) ||
            dormantEntity.Entity.CurrentHealth <= 0 ||
            dormantSetup == null || dormantSetup.GetType() != typeof(SetupDemoEnemy) ||
            dormantEffects == null || dormantEffects.GetType() != typeof(EntityEffectManager) ||
            !dormantEffects.CanSuspendNetworkObserverUpdates ||
            (isClientOnly && !dormantSetup.HasReceivedInitialServerSettings()) ||
            (!observerRuntimeDormant && (!dormantEntity.enabled || !dormantSetup.enabled || !dormantEffects.enabled)))
        {
            RestoreObserverRuntime();
            return;
        }
        if (observerRuntimeDormant) return;
        if (!dormantEffects.PrepareNetworkObserverDormancy() || !CanUseObserverRuntime(visible)) return;
        observerRuntimeDormant = true;
        SetObserverTickSuspended(dormantEffects, true, ref dormantEffectsChanged);
        SetObserverTickSuspended(dormantSetup, true, ref dormantSetupChanged);
        SetObserverTickSuspended(dormantEntity, true, ref dormantEntityChanged);
        for (int i = 0; i < dormantCosmetics.Length; i++)
            SetObserverTickSuspended(dormantCosmetics[i], true, ref dormantCosmeticChanged[i]);
        lastObserverServiceTime = Time.unscaledTime;
        EnqueueObserverService(lastObserverServiceTime + LocalPerPlayerCullInterval);
    }

    private void RestoreObserverRuntime()
    {
        RemoveObserverService();
        if (!observerRuntimeDormant) return;
        observerRuntimeDormant = false;
        // Entity first: effect code checks entityBehaviour.enabled.
        SetObserverTickSuspended(dormantEntity, false, ref dormantEntityChanged);
        SetObserverTickSuspended(dormantSetup, false, ref dormantSetupChanged);
        SetObserverTickSuspended(dormantEffects, false, ref dormantEffectsChanged);
        for (int i = 0; i < dormantCosmetics.Length; i++)
            SetObserverTickSuspended(dormantCosmetics[i], false, ref dormantCosmeticChanged[i]);
    }

    private void TickObserverRuntimeMaintenance()
    {
        // Serviced by the shared bounded queue. No new per-enemy coroutine.
        // Do not StopAllCoroutines: corpse and network initialization work must survive.
        if (!observerRuntimeDormant) return;
        UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.Maintenance");
        try
        {
            UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.SetupState");
            try { dormantSetup.TickDormantNetworkObserver(dormantEntity, presentationMotor); }
            finally { UnityEngine.Profiling.Profiler.EndSample(); }
            if (!observerRuntimeDormant || !gameObject.activeInHierarchy) return;
            UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.EffectState");
            try { dormantEffects.TickDormantNetworkObserver(); }
            finally { UnityEngine.Profiling.Profiler.EndSample(); }
            if (!observerRuntimeDormant || !gameObject.activeInHierarchy) return;
            UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.HealthState");
            try { dormantEntity.TickDormantNetworkObserver(dormantSetup); }
            finally { UnityEngine.Profiling.Profiler.EndSample(); }
        }
        finally
        {
            UnityEngine.Profiling.Profiler.EndSample();
        }
    }

    // Intrusive FIFO: O(1) registration/removal, no per-frame scene scan or allocation.
    private static DynamicEnemyAuthority observerServiceHead, observerServiceTail;
    private static int observerServiceCount;
    private static int observerServiceFrame = -1;
    private static int observerServiceLastCount, observerServiceErrors;
    private static float observerServiceLastMs, observerServicePeakMs;
    private const int ObserverServiceMaxPerFrame = 512;
    private const double ObserverServiceBudgetSeconds = 0.002;
    private DynamicEnemyAuthority observerServicePrevious, observerServiceNext;
    private bool observerServiceQueued, observerRuntimeFaulted;
    private float observerServiceDue, lastObserverServiceTime;

    private void EnqueueObserverService(float due)
    {
        if (observerServiceQueued || !observerRuntimeDormant || !isActiveAndEnabled) return;
        observerServiceDue = due;
        observerServicePrevious = observerServiceTail;
        observerServiceNext = null;
        if (!ReferenceEquals(observerServiceTail, null)) observerServiceTail.observerServiceNext = this;
        else observerServiceHead = this;
        observerServiceTail = this;
        observerServiceQueued = true;
        observerServiceCount++;
    }

    private void RemoveObserverService()
    {
        if (!observerServiceQueued) return;
        if (!ReferenceEquals(observerServicePrevious, null)) observerServicePrevious.observerServiceNext = observerServiceNext;
        else observerServiceHead = observerServiceNext;
        if (!ReferenceEquals(observerServiceNext, null)) observerServiceNext.observerServicePrevious = observerServicePrevious;
        else observerServiceTail = observerServicePrevious;
        observerServicePrevious = observerServiceNext = null;
        observerServiceQueued = false;
        observerServiceCount--;
    }

    private static void ProcessObserverServiceQueue()
    {
        if (observerServiceFrame == Time.frameCount) return;
        observerServiceFrame = Time.frameCount;
        observerServiceLastCount = 0;
        observerServiceLastMs = 0f;
        if (ReferenceEquals(observerServiceHead, null)) return;

        // Aim for one pass per 0.25s, but never replay a whole overdue population
        // after a stall. Under load, FIFO fairness wins over catch-up bursts.
        int limit = Mathf.Clamp(Mathf.CeilToInt(observerServiceCount *
            Mathf.Min(Time.unscaledDeltaTime, 0.05f) / LocalPerPlayerCullInterval),
            1, Mathf.Min(ObserverServiceMaxPerFrame, observerServiceCount));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double budgetTicks = ObserverServiceBudgetSeconds * System.Diagnostics.Stopwatch.Frequency;
        float now = Time.unscaledTime;
        UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.Scheduler");
        try
        {
            while (observerServiceLastCount < limit && !ReferenceEquals(observerServiceHead, null))
            {
                DynamicEnemyAuthority enemy = observerServiceHead;
                // Normally due order follows FIFO order. Newly sleeping enemies are
                // appended; their first pass can wait behind the oldest queued entry.
                if (enemy != null && enemy.observerServiceDue > now) break;
                enemy.RemoveObserverService();
                observerServiceLastCount++;
                if (enemy != null && enemy.isActiveAndEnabled && enemy.observerRuntimeDormant)
                {
                    try
                    {
                        enemy.RefreshObserverLocalState();
                        if (enemy != null && enemy.isActiveAndEnabled && enemy.observerRuntimeDormant)
                            enemy.TickObserverRuntimeMaintenance();
                        if (enemy != null && enemy.isActiveAndEnabled && enemy.observerRuntimeDormant)
                        {
                            enemy.lastObserverServiceTime = now;
                            enemy.EnqueueObserverService(now + LocalPerPlayerCullInterval);
                        }
                    }
                    catch (System.Exception exception)
                    {
                        // A broken mod/state must not stall every other enemy's queue.
                        observerServiceErrors++;
                        if (enemy != null)
                        {
                            enemy.observerRuntimeFaulted = true;
                            enemy.WakeObserverRuntime();
                        }
                        Debug.LogException(exception);
                    }
                }
                // Soft time budget: the current enemy operation is never interrupted.
                if (System.Diagnostics.Stopwatch.GetTimestamp() - started >= budgetTicks) break;
            }
        }
        finally
        {
            observerServiceLastMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                1000.0 / System.Diagnostics.Stopwatch.Frequency);
            observerServicePeakMs = Mathf.Max(observerServicePeakMs, observerServiceLastMs);
            UnityEngine.Profiling.Profiler.EndSample();
        }
    }

    private static void RegisterObserverRuntimeCommand()
    {
        if (observerRuntimeCommandRegistered) return;
        Wenzil.Console.ConsoleCommandsDatabase.RegisterCommand("enemyobserver",
            "Inspect or toggle hidden observer runtime suspension (local process only).",
            "enemyobserver status|on|off", ObserverRuntimeCommand);
        observerRuntimeCommandRegistered = true;
    }

    private static string ObserverRuntimeCommand(params string[] args)
    {
        string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        if (action != "status" && action != "on" && action != "off")
            return "Usage: enemyobserver status|on|off";
        // A diagnostic command may scan once; the runtime path never does this scan.
        var enemies = FindObjectsOfType<DynamicEnemyAuthority>();
        if (action != "status")
        {
            observerRuntimeEnabled = action == "on";
            foreach (var enemy in enemies)
                if (!observerRuntimeEnabled) enemy.RestoreObserverRuntime();
        }
        int networked = 0, dormant = 0, collisions = 0, busyEffects = 0;
        int entityTicksOff = 0, setupTicksOff = 0, effectTicksOff = 0;
        float oldestServiceAge = 0f;
        foreach (var enemy in enemies)
        {
            if (enemy.netId == 0) continue;
            networked++;
            if (enemy.observerCollisionsSuspended) collisions++;
            if (enemy.dormantEffects != null && !enemy.dormantEffects.CanSuspendNetworkObserverUpdates) busyEffects++;
            if (!enemy.observerRuntimeDormant) continue;
            dormant++;
            oldestServiceAge = Mathf.Max(oldestServiceAge, Time.unscaledTime - enemy.lastObserverServiceTime);
            if (enemy.dormantEntity != null && !enemy.dormantEntity.enabled) entityTicksOff++;
            if (enemy.dormantSetup != null && !enemy.dormantSetup.enabled) setupTicksOff++;
            if (enemy.dormantEffects != null && !enemy.dormantEffects.enabled) effectTicksOff++;
        }
        return $"Enemy observer runtime enabled={observerRuntimeEnabled}; networked={networked}; dormant={dormant}; " +
            $"entityTicksOff={entityTicksOff}; setupTicksOff={setupTicksOff}; effectTicksOff={effectTicksOff}; " +
            $"collisionSuspended={collisions}; cachedBusyEffects={busyEffects}; " +
            $"queue={observerServiceCount}; servicedLastFrame={observerServiceLastCount}; " +
            $"serviceLastMs={observerServiceLastMs:F2}; oldestServiceAge={oldestServiceAge:F2}s; " +
            $"servicePeakMs={observerServicePeakMs:F2}; maintenanceErrors={observerServiceErrors}. " +
            "Local process only; collision suspension and earlier motor/senses/attack gates are unchanged. " +
            "Allow 2 seconds after a toggle before measuring. Do not combine with bench scripts/physics.";
    }

    private EnemyMotor presentationMotor;
    private DaggerfallMobileUnit[] presentationMobiles;
    private EnemySounds[] presentationSounds;
    private Light[] presentationLights;
    private ParticleSystem[] presentationParticles;
    private bool[] presentationLightStates;
    private bool[] presentationParticleStates;
    private bool presentationStateApplied;
    private bool lastPresentationVisible;
    private bool lastPresentationSimulator;

    private float nextLocalPerPlayerCullCheckTime = 0f;
    private Collider[] cachedEnemyColliders = null;
    private Collider[] cachedLocalPlayerColliders = null;
    private AudioSource[] cachedAudioSources = null;
    private Renderer[] cachedRenderers = null;
    private bool localCollisionPairsIgnored = false;

    // Exterior terrain frame wrap support. Daggerfall exterior local Unity coordinates
    // wrap by one map-pixel tile (~32768 DF units / 40 = 819.2 Unity units).
    // Networked MP actors can be scene-root objects, so they do not always receive
    // the same local terrain-frame shift as DFU's normal StreamingTarget children.
    // Keep normal NetworkTransform/EnemyMotor movement, but remap only by whole
    // terrain-frame multiples so the object stays in the local player's nearest frame.
    private const float TerrainFrameUnitySize = 819.2f;
    private const float TerrainFrameHalfUnitySize = TerrainFrameUnitySize * 0.5f;
    private const float TerrainFrameCorrectionThresholdUnity = 300f;

    private static System.Collections.Generic.List<PlayerMultiplayer> GetCachedPlayers(bool forceRefresh = false)
    {
        // Use unscaled time here. DFU message boxes/menus can pause scaled time on the host,
        // but Mirror messages and authority handoff still need the current player list.
        float now = Time.unscaledTime;

        if (forceRefresh || now - lastPlayerCacheTime >= PlayerCacheInterval)
        {
            cachedPlayers.Clear();
            cachedPlayers.AddRange(FindObjectsOfType<PlayerMultiplayer>());
            lastPlayerCacheTime = now;
        }

        return cachedPlayers;
    }

    private float GetEffectiveAuthorityCheckInterval()
    {
        // Inspector can still be lower for testing, but large MP dungeons should not
        // authority-scan every 0.2s per enemy anymore. EnemyMotor is now authority-gated,
        // so we can be more relaxed without risking remote client falling/physics issues.
        return Mathf.Max(authorityCheckInterval, MinEffectiveAuthorityCheckInterval);
    }

    private bool retainedRuntimeStarted;
    private Coroutine retainedAuthorityRoutine;
    public void ResumeRetainedActorNetworking()
    {
        if (retainedAuthorityRoutine != null) StopCoroutine(retainedAuthorityRoutine);
        retainedAuthorityRoutine = null;
        retainedRuntimeStarted = false;
        Start();
    }

    public void ResetRetainedActorToLocal()
    {
        RestoreObserverRuntime();
        RestoreObserverCollisions();
        RestoreHiddenObserverTicks();
        StopAllCoroutines();
        observerPhysicsSettleDepth = 0;
        if (_ntResetCo != null)
        {
            var pendingTransform = GetComponent<NetworkTransform>();
            if (pendingTransform != null) pendingTransform.enabled = true;
        }
        _ntResetCo = null;
        destroyCoroutine = null;
        warmupPumpCo = null;
        retainedAuthorityRoutine = null;
        _createFoeAuthorityResnapCo = null;
        _serverCreateFoeSpawnSettleCo = null;
        persistentOwnerPlayerNetId = 0;
        persistentOwnerConnection = null;
        currentOwner = null;
        HasLocalOwnershipClaim = false;
        authorityDeactivated = false;
        localPerPlayerCulled = false;
        SetLocalPlayerCollisionIgnored(false);
        RefreshVisualAndAudioState();
    }

    void Start()
    {
        if (retainedRuntimeStarted) return;
        retainedRuntimeStarted = true;
        RegisterObserverRuntimeCommand();
        netIdentity = GetComponent<NetworkIdentity>();
        enemyTransform = transform;
        visual = transform.Find("MobileUnitBillboard")?.gameObject;
        worldPosition = GetComponent<EnemyWorldPosition>();
        spawnTime = Time.time; // keep scaled time for destroy/spawn grace; menus should not burn grace time
        authoritySlot = nextAuthoritySlot++;
        nextAuthorityCheckTime = Time.unscaledTime + (authoritySlot % AuthorityStaggerSlots) * (GetEffectiveAuthorityCheckInterval() / AuthorityStaggerSlots);

        if (visual == null)
            Debug.LogWarning($"[DynamicEnemyAuthority] Could not find MobileUnitBillboard on '{name}'.");

        // Raw Unity-distance interest management is unsafe for MP enemies around
        // exterior terrain-frame seams (0 <-> ~820). Authority/cull logic below
        // uses DF world coordinates instead, so do not let Mirror despawn an enemy
        // just because its raw Unity frame differs from this observer's frame.
        var dim = GetComponent<DistanceInterestManagement>();
        if (dim != null)
            Destroy(dim);

        if (NetworkServer.active)
            retainedAuthorityRoutine = StartCoroutine(AuthorityCheckRoutine());

        RefreshVisualAndAudioState();
        // Authority callbacks can precede Start, before visual was resolved.
        if (visual != null)
            visual.SetActive(lastPresentationVisible || lastPresentationSimulator);
    }

    void LateUpdate()
    {
        ProcessObserverServiceQueue();
        LocalActorUpdate?.Invoke(this);
        NormalizeExteriorTerrainFrameNearLocalPlayer();
        HoldRemotePassiveFixedSpawnOnServer();
        UpdateLocalPerPlayerCulling();
    }

    private void OnDestroy()
    {
        // Make sure pairwise ignores are undone if the enemy is destroyed while culled.
        SetLocalPlayerCollisionIgnored(false);
    }


    private float NormalizeTerrainFrameDelta(float delta)
    {
        while (delta > TerrainFrameHalfUnitySize)
            delta -= TerrainFrameUnitySize;

        while (delta < -TerrainFrameHalfUnitySize)
            delta += TerrainFrameUnitySize;

        return delta;
    }

    private bool IsLocalPlayerExteriorFrameAvailable(out Vector3 localPlayerPos, out int localWorldX, out int localWorldZ)
    {
        localPlayerPos = Vector3.zero;
        localWorldX = 0;
        localWorldZ = 0;

        if (GameManager.Instance == null ||
            GameManager.Instance.PlayerObject == null ||
            GameManager.Instance.PlayerGPS == null)
            return false;

        // Do not apply exterior terrain-frame wrapping in network dungeons.
        if (GameManager.Instance.PlayerEnterExit != null &&
            GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
            return false;

        localPlayerPos = GameManager.Instance.PlayerObject.transform.position;
        localWorldX = GameManager.Instance.PlayerGPS.WorldX;
        localWorldZ = GameManager.Instance.PlayerGPS.WorldZ;
        return true;
    }

    private bool IsUnderStreamingTarget()
    {
        Transform t = transform.parent;
        while (t != null)
        {
            if (t.name == "StreamingTarget")
                return true;

            t = t.parent;
        }

        return false;
    }

    private bool TryAlignRemoteOwnedEnemyCombatFrame()
    {
        if (!isServer || netIdentity == null || netIdentity.connectionToClient == null ||
            netIdentity.connectionToClient == NetworkServer.localConnection)
            return false;

        // This branch completely owns correction for remote-owned enemies. If their
        // context is not exterior, do not fall through to an exterior wrap on the host.
        if (worldPosition == null || worldPosition.isInteriorSpawn || worldPosition.isDungeonSpawn ||
            transform.position.y < -100f || IsUnderStreamingTarget())
            return true;
        if (GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null ||
            GameManager.Instance.PlayerEnterExit.IsPlayerInside)
            return true;

        // Player-target combat uses that player's frame; infighting/following falls
        // back to the simulation owner's frame. No dependency on the distant host's
        // nearest-frame choice for the enemy itself.
        PlayerMultiplayer reference = null;
        EnemySenses senses = GetComponent<EnemySenses>();
        if (senses != null && senses.Target != null)
            reference = senses.Target.GetComponentInParent<PlayerMultiplayer>();
        PositionMultiplayer referencePosition = reference != null ? reference.GetComponent<PositionMultiplayer>() : null;
        if (!IsNearbyExteriorCombatReference(referencePosition))
        {
            NetworkIdentity ownerIdentity = netIdentity.connectionToClient.identity;
            reference = ownerIdentity != null ? ownerIdentity.GetComponent<PlayerMultiplayer>() : null;
            referencePosition = reference != null ? reference.GetComponent<PositionMultiplayer>() : null;
        }
        if (!IsNearbyExteriorCombatReference(referencePosition))
            return true;

        // Ensure the reference has already wrapped this frame, regardless of LateUpdate order.
        if (!reference.isLocalPlayer && !referencePosition.EnsureBoundedExteriorFrameForCombat())
            return true;

        float expectedDx = (float)(((double)worldPosition.worldX - referencePosition.x) * unityPerDF);
        float expectedDz = (float)(((double)worldPosition.worldZ - referencePosition.z) * unityPerDF);
        Vector3 position = transform.position;
        Vector3 referencePose = reference.transform.position;
        float shiftX = Mathf.Round((referencePose.x + expectedDx - position.x) / TerrainFrameUnitySize) * TerrainFrameUnitySize;
        float shiftZ = Mathf.Round((referencePose.z + expectedDz - position.z) / TerrainFrameUnitySize) * TerrainFrameUnitySize;
        if (Mathf.Abs(shiftX) < 1f && Mathf.Abs(shiftZ) < 1f)
            return true;
        transform.position = new Vector3(position.x + shiftX, position.y, position.z + shiftZ);
        worldPosition.NoteExternalSeamFrameCorrection();
        return true;
    }

    private bool IsNearbyExteriorCombatReference(PositionMultiplayer reference)
    {
        if (reference == null || (worldPosition.worldX == 0 && worldPosition.worldZ == 0) ||
            (reference.x == 0 && reference.z == 0) || reference.transform.position.y < -100f ||
            reference.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.BuildingInterior ||
            reference.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.DungeonInterior)
            return false;
        double dx = ((double)worldPosition.worldX - reference.x) * unityPerDF;
        double dz = ((double)worldPosition.worldZ - reference.z) * unityPerDF;
        // This is a bounded local offset, NEVER full-world displacement from the host.
        // Do not modulo this test: genuinely distant players are not valid references.
        return dx * dx + dz * dz <= TerrainFrameHalfUnitySize * TerrainFrameHalfUnitySize;
    }

    private void NormalizeExteriorTerrainFrameNearLocalPlayer()
    {
        if (!NetworkClient.active)
            return;

        // Client-owned enemies and their player reference must use one bounded
        // combat frame on the host, even while the host is far away.
        if (TryAlignRemoteOwnedEnemyCombatFrame())
            return;

        if (IsActualNetworkDungeonEnemyForLocalRules() || IsUnderStreamingTarget())
            return;

        Vector3 localPlayerPos;
        int localWorldX;
        int localWorldZ;
        if (!IsLocalPlayerExteriorFrameAvailable(out localPlayerPos, out localWorldX, out localWorldZ))
            return;

        // Use DF-world metadata only as a safety gate: if this enemy is not logically
        // near the local player even after terrain-frame wrapping, leave it alone.
        // This avoids wrapping truly distant exterior enemies into view.
        if (worldPosition != null)
        {
            float expectedDx = (float)(((double)worldPosition.worldX - localWorldX) * unityPerDF);
            float expectedDz = (float)(((double)worldPosition.worldZ - localWorldZ) * unityPerDF);
            if (!worldPosition.PreserveDistantExteriorCoordinates)
            {
                expectedDx = NormalizeTerrainFrameDelta(expectedDx);
                expectedDz = NormalizeTerrainFrameDelta(expectedDz);
            }

            if (expectedDx * expectedDx + expectedDz * expectedDz >
                LocalPerPlayerCullDistanceUnity * LocalPerPlayerCullDistanceUnity)
                return;
        }

        Vector3 pos = transform.position;
        float dx = pos.x - localPlayerPos.x;
        float dz = pos.z - localPlayerPos.z;
        float ndx = NormalizeTerrainFrameDelta(dx);
        float ndz = NormalizeTerrainFrameDelta(dz);

        Vector3 corrected = new Vector3(localPlayerPos.x + ndx, pos.y, localPlayerPos.z + ndz);
        if (worldPosition != null && worldPosition.PreserveDistantExteriorCoordinates &&
            (!hasAuthority || authorityDeactivated))
        {
            corrected.x = localPlayerPos.x + (float)(((double)worldPosition.worldX - localWorldX) * unityPerDF);
            corrected.z = localPlayerPos.z + (float)(((double)worldPosition.worldZ - localWorldZ) * unityPerDF);
        }
        Vector3 correction = corrected - pos;
        correction.y = 0f;

        float correctionThreshold = worldPosition != null &&
            worldPosition.PreserveDistantExteriorCoordinates && authorityDeactivated
            ? 0.1f : TerrainFrameCorrectionThresholdUnity;
        if (correction.sqrMagnitude < correctionThreshold * correctionThreshold)
            return;

        transform.position = corrected;

        // Prevent EnemyWorldPosition from interpreting this terrain-frame wrap as
        // enemy movement. On server/host it will also rebake DF X/Z from the now
        // normalized player-relative offset.
        if (worldPosition != null)
            worldPosition.NoteExternalSeamFrameCorrection();
    }

    private void HoldRemotePassiveFixedSpawnOnServer()
    {
        if (persistentOwnerPlayerNetId != 0)
            return;

        // Server/host-side only. The CreateFoe marker itself means "run the finite
        // floor-settle/resnap", not "hold every passive enemy forever". Only an
        // explicitly restrained single-marker quest foe may use this ongoing hold.
        if (!NetworkServer.active)
            return;

        EnemyWorldPosition ewp = worldPosition != null ? worldPosition : GetComponent<EnemyWorldPosition>();
        if (ewp == null ||
            !ewp.isCreateFoeWaveSpawn ||
            !ewp.isFixedQuestFoeRestrained ||
            ewp.intendedSpawnPos == Vector3.zero)
            return;

        EnemyMotor motor = GetComponent<EnemyMotor>();

        // Restraint is a one-way spawn state. Once the quest foe is attacked or
        // otherwise made hostile, release it permanently. A later language/pacify
        // transition must not teleport it back to its original marker.
        if (motor != null && motor.IsHostile)
        {
            ewp.isFixedQuestFoeRestrained = false;
            Debug.Log($"[FixedSpawnPassiveHold] Released restrained quest foe '{name}' after it became hostile.");
            return;
        }

        NetworkIdentity ni = netIdentity != null ? netIdentity : GetComponent<NetworkIdentity>();
        if (ni == null || ni.connectionToClient == null)
            return;

        // Do not pin host-owned enemies. The host should simulate its own enemies normally.
        NetworkConnectionToClient hostConnection = NetworkServer.localConnection as NetworkConnectionToClient;
        if (hostConnection != null && ni.connectionToClient == hostConnection)
            return;

        CharacterController cc = GetComponent<CharacterController>();

        // intendedSpawnPos is a marker/world reference, not always the final transform center.
        // Convert it to the same settled controller-center position used by the owner resnap.
        Vector3 holdPos = ComputeSettledCreateFoePosition(ewp.intendedSpawnPos, cc);

        // Undo any server-side passive gravity/floor settling after EnemyMotor.FixedUpdate().
        // LateUpdate is deliberate: it runs after FixedUpdate movement has had a chance to pull the host copy down.
        if ((transform.position - holdPos).sqrMagnitude > 0.0001f)
        {
            transform.position = holdPos;

            if (motor != null)
                motor.LastGroundedY = holdPos.y;

            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (Time.time >= _nextRemotePassiveHoldLogTime)
            {
                Debug.Log($"[FixedSpawnPassiveHold] Holding explicitly restrained remote quest foe '{name}' at settledPos={holdPos} intended={ewp.intendedSpawnPos} ownerConn={ni.connectionToClient.connectionId}");
                _nextRemotePassiveHoldLogTime = Time.time + 1.0f;
            }
        }
    }

    IEnumerator AuthorityCheckRoutine()
    {
        // Stagger initial wakeup. When a large dungeon spawns 100 enemies, without this
        // all enemy authority coroutines wake together and produce periodic host spikes.
        //
        // IMPORTANT: use unscaled waits for authority handoff. DFU message boxes/menus can
        // pause scaled time on the listen-host, but KCP/Mirror can still receive the client
        // dungeon/quest spawn while the host is in a popup. If this coroutine waits on
        // scaled time, enemies stay host-owned until the popup closes; then client-side
        // copies can settle/fall before the requester receives authority.
        float interval = GetEffectiveAuthorityCheckInterval();
        float initialDelay = (authoritySlot % AuthorityStaggerSlots) * (interval / AuthorityStaggerSlots);
        if (initialDelay > 0f)
            yield return new WaitForSecondsRealtime(initialDelay);

        while (true)
        {
            UpdateAuthority();

            interval = GetEffectiveAuthorityCheckInterval();
            nextAuthorityCheckTime = Time.unscaledTime + interval;
            yield return new WaitForSecondsRealtime(interval);
        }
    }

    private void OnAuthorityDeactivatedChanged(bool oldValue, bool newValue)
    {
        ApplyAuthorityDeactivatedState(newValue);
    }

    [Server]
    private void SetAuthorityDeactivated(bool value)
    {
        if (authorityDeactivated == value)
            return;

        authorityDeactivated = value;
        ApplyAuthorityDeactivatedState(value);
    }

    private void ApplyAuthorityDeactivatedState(bool deactivated)
    {
        RefreshVisualAndAudioState();
        RefreshLocalCollisionState();

        EnemyMotor motor = GetComponent<EnemyMotor>();
        if (motor != null)
        {
            if (deactivated)
            {
                motor.CanAct = false;
                motor.LastGroundedY = transform.position.y;
            }

            motor.enabled = !deactivated;
        }

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (deactivated)
            {
                if (!deactivateSavedRbStateValid)
                {
                    deactivateSavedRbKinematic = rb.isKinematic;
                    deactivateSavedRbUseGravity = rb.useGravity;
                    deactivateSavedRbStateValid = true;
                }

                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.useGravity = false;
                rb.isKinematic = true;
            }
            else if (deactivateSavedRbStateValid)
            {
                rb.isKinematic = deactivateSavedRbKinematic;
                rb.useGravity = deactivateSavedRbUseGravity;
                deactivateSavedRbStateValid = false;

                // Authority might have changed while this enemy was deactivated. Re-apply
                // the normal server-side Rigidbody gate after restoring saved values.
                ApplyAuthorityMotionGate();
            }
        }
        RefreshVisualAndAudioState();
    }

    void UpdateAuthority()
    {
        if (!NetworkServer.active || !netIdentity)
            return;

        if (PassiveDungeonGridTest) return;

        if (MaintainPersistentAuthority())
            return;

        if (worldPosition == null || !worldPosition.isActiveAndEnabled || !worldPosition.initialized)
            return;

        if (KeepInitialExteriorOwner())
            return;

        bool inGrace = (Time.time - spawnTime < destroyGracePeriod);

        currentOwner = netIdentity.connectionToClient as NetworkConnectionToClient;

        // Track for destroy logic: is there anyone within destroyDistance (DF→Unity) AND maxYDistance (Unity)?
        bool anyPlayerWithinDestroy = false;

        // Track for deactivate logic: if nobody is within this smaller radius, the
        // enemy falls back to host/server ownership and then turns off motor/gravity/visuals
        // until a player returns. This prevents client-owned interior enemies from
        // simulating against missing local floors after the owner leaves.
        bool anyPlayerWithinDeactivate = false;

        // Best candidate within switch bounds (3D Unity distance)
        PlayerMultiplayer bestCandidate = null;
        float bestCandidateDistU = float.MaxValue;

        // Current owner’s distances (to decide if we must switch)
        float ownerDistU = float.MaxValue;
        float ownerAbsYU = float.MaxValue;

        float switchDistanceU = switchDistance * unityPerDF;
        float deactivateDistanceU = deactivateDistance * unityPerDF;
        float destroyDistanceU = destroyDistance * unityPerDF;

        var players = GetCachedPlayers();

        foreach (var player in players)
        {
            if (player == null)
                continue;

            var pos = player.GetComponent<PositionMultiplayer>();
            if (pos == null)
                continue;

            // Convert DF XZ deltas to Unity meters
            float dxU = (pos.x - worldPosition.worldX) * unityPerDF;
            float dzU = (pos.z - worldPosition.worldZ) * unityPerDF;
            float dyU = player.transform.position.y - transform.position.y;

            float distU = Mathf.Sqrt(dxU * dxU + dzU * dzU + dyU * dyU);
            float absYU = Mathf.Abs(dyU);

            // Destroy consideration: within 3D Unity radius AND Y close enough
            if (HorizontalRetentionDistanceDF > 0f
                ? IsWithinHorizontalRetention(pos)
                : distU < destroyDistanceU && absYU < maxYDistance)
                anyPlayerWithinDestroy = true;

            // Deactivate consideration: smaller middle range between active authority
            // switching and full destruction. This uses the same 3D distance math,
            // with Y included through distU and also limited by maxYDistance.
            if (distU <= deactivateDistanceU && absYU <= maxYDistance)
                anyPlayerWithinDeactivate = true;

            // Track current owner distances if this is them
            if (currentOwner != null && player.connectionToClient == currentOwner)
            {
                ownerDistU = distU;
                ownerAbsYU = absYU;
            }

            // Candidate must be in bounds:
            //  - Y near enough
            //  - 3D distance within switchDistanceU
            bool candidateInBounds = (absYU <= maxYDistance) && (distU <= switchDistanceU);

            if (candidateInBounds && distU < bestCandidateDistU)
            {
                bestCandidate = player;
                bestCandidateDistU = distU;
            }
        }

        // Destroy handling: only allow destroy once grace is over AND no-one is within destroyDistanceU/maxYDistance
        if (!anyPlayerWithinDestroy)
        {
            if (!inGrace) TryStartDestroyCountdown();
        }
        else
        {
            CancelDestroyCountdown();
        }

        // If a player has come back into the active/deactivate radius, wake the
        // enemy before preserving or assigning authority. The normal owner/candidate
        // logic below will pick the right simulator.
        if (anyPlayerWithinDeactivate)
            SetAuthorityDeactivated(false);

        // ---- OWNER PRESERVATION RULE (3D Unity + Y priority) ----
        // Keep current owner if:
        //  - we have an owner, AND
        //  - Y is close (priority), AND
        //  - full 3D distance in Unity is inside switchDistanceU.
        bool ownerInBounds =
            currentOwner != null &&
            ownerAbsYU <= maxYDistance &&
            ownerDistU  <= switchDistanceU;

        if (ownerInBounds)
        {
            ShowVisuals();
            return; // keep current owner; do not switch just because someone is a bit closer
        }

        // Owner is out of bounds OR no owner yet → pick best in-bounds candidate
        if (bestCandidate != null)
        {
            var newOwner = bestCandidate.connectionToClient as NetworkConnectionToClient;
            if (newOwner != null && newOwner != currentOwner && newOwner.isReady)
            {
                if (currentOwner != null)
                    netIdentity.RemoveClientAuthority();

                { EnemyInterestManagement.PrepareOwner(netIdentity, newOwner); netIdentity.AssignClientAuthority(newOwner); }
                currentOwner = newOwner;
                SetAuthorityDeactivated(false);

                // Server side: inert RB (if any) and clear NT buffer.
                ApplyAuthorityMotionGate();
                ResetNetworkTransformBuffers();

                // No NT disable here. The new owner will start the warmup pump in OnStartAuthority().
                ShowVisuals();
            }
            return;
        }

        // Listen hosts retain their existing fallback owner. A dedicated server has
        // no local connection: removing client authority makes the server the owner.
        // Do not repeatedly reset NetworkTransform while already on the fallback.
        NetworkConnectionToClient fallbackOwner = NetworkServer.localConnection as NetworkConnectionToClient;
        bool ownerChanged = currentOwner != fallbackOwner;
        if (ownerChanged)
        {
            if (currentOwner != null)
                netIdentity.RemoveClientAuthority();

            if (fallbackOwner != null)
                { EnemyInterestManagement.PrepareOwner(netIdentity, fallbackOwner); netIdentity.AssignClientAuthority(fallbackOwner); }

            currentOwner = fallbackOwner;
        }

        // Run even without a host player, and even if already server-owned.
        SetAuthorityDeactivated(!anyPlayerWithinDeactivate);
        ApplyAuthorityMotionGate();
        if (ownerChanged)
            ResetNetworkTransformBuffers();
        ShowVisuals();
    }


    // ======== LOCAL PER-PLAYER VISIBILITY/COLLISION CULLING ========

    private void UpdateLocalPerPlayerCulling()
    {
        if (!NetworkClient.active && !NetworkServer.active) return;
        // Dormant relevance checks and maintenance run together through the bounded queue.
        if (observerRuntimeDormant) return;
        float now = Time.unscaledTime;
        if (now < nextLocalPerPlayerCullCheckTime) return;
        // Anchor each non-dormant check to its own phase; now+interval would merge
        // their phases again after a long frame. Missed checks are not replayed.
        float phase = (authoritySlot % AuthorityStaggerSlots) *
            (LocalPerPlayerCullInterval / AuthorityStaggerSlots);
        nextLocalPerPlayerCullCheckTime =
            (Mathf.Floor((now - phase) / LocalPerPlayerCullInterval) + 1f) * LocalPerPlayerCullInterval + phase;
        RefreshObserverLocalState();
    }

    private void RefreshObserverLocalState()
    {
        bool shouldCull;
        UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.Relevance");
        try { shouldCull = NetworkClient.active && ShouldCullForLocalPlayer(); }
        finally { UnityEngine.Profiling.Profiler.EndSample(); }
        UnityEngine.Profiling.Profiler.BeginSample("EnemyObserver.StateRefresh");
        try
        {
            if (localPerPlayerCulled != shouldCull) SetLocalPerPlayerCulled(shouldCull);
            else RefreshVisualAndAudioState();
        }
        finally { UnityEngine.Profiling.Profiler.EndSample(); }
    }

    private bool ShouldCullForLocalPlayer()
    {
        // This is a purely local visibility/ghost test. It does NOT decide authority,
        // it does NOT disable the enemy object, and it does NOT stop the enemy from
        // attacking another player who is actually near it.
        if (Time.time - spawnTime < LocalPerPlayerCullSpawnGrace)
            return false;

        if (GameManager.Instance == null ||
            GameManager.Instance.PlayerObject == null)
            return false;

        Vector3 localPlayerPos = GameManager.Instance.PlayerObject.transform.position;

        // Decide location relevance by identity, never by spacing between dungeon roots.
        // Unknown identity deliberately stays visible until a trustworthy stamp arrives.
        if (worldPosition == null) worldPosition = GetComponent<EnemyWorldPosition>();
        if (worldPosition == null) return false;
        var enterExit = GameManager.Instance.PlayerEnterExit;
        if (enterExit == null) return false;
        string dungeonId = worldPosition.PresentationDungeonInstanceId;
        if (!string.IsNullOrEmpty(dungeonId))
        {
            if (!enterExit.IsPlayerInsideDungeon) return true;
            var localDungeon = enterExit.Dungeon;
            if (localDungeon == null || string.IsNullOrEmpty(localDungeon.DungeonInstanceId)) return false;
            return dungeonId != localDungeon.DungeonInstanceId;
        }
        if (worldPosition.isDungeonSpawn) return false;
        if (worldPosition.isInteriorSpawn)
        {
            if (worldPosition.PresentationLocationState != PositionMultiplayer.PartyLocationState.BuildingInterior ||
                worldPosition.PresentationBuildingKey == 0) return false;
            if (!enterExit.IsPlayerInsideBuilding || enterExit.IsPlayerInsideDungeon) return true;
            var localPlayer = PlayerMultiplayer.GetLocalPlayer();
            var localPosition = localPlayer != null ? localPlayer.GetComponent<PositionMultiplayer>() : null;
            if (localPosition == null || localPosition.PartyCurrentLocationState != PositionMultiplayer.PartyLocationState.BuildingInterior ||
                localPosition.PartyBuildingKey == 0) return false;
            return worldPosition.PresentationBuildingKey != localPosition.PartyBuildingKey ||
                worldPosition.PresentationRegionName != localPosition.PartyRegionName ||
                worldPosition.PresentationLocationName != localPosition.PartyLocationName;
        }
        if (enterExit.IsPlayerInsideDungeon || enterExit.IsPlayerInsideBuilding) return true;

        // Exterior enemies need DF X/Z. This fixes the case where
        // another player is in a totally different DF world area but happens to share
        // similar local Unity coordinates.
        // IMPORTANT: do not require EnemyWorldPosition.initialized here. That flag is not
        // a SyncVar and remains false on remote clients, while worldX/worldZ are SyncVars
        // and are exactly the data local culling needs.
        if (worldPosition != null && GameManager.Instance.PlayerGPS != null)
        {
            int localWorldX = GameManager.Instance.PlayerGPS.WorldX;
            int localWorldZ = GameManager.Instance.PlayerGPS.WorldZ;

            float dxU = (localWorldX - worldPosition.worldX) * unityPerDF;
            float dzU = (localWorldZ - worldPosition.worldZ) * unityPerDF;
            float dyU = localPlayerPos.y - transform.position.y;
            float distU = Mathf.Sqrt(dxU * dxU + dzU * dzU + dyU * dyU);

            return distU > LocalPerPlayerCullDistanceUnity;
        }

        // Keep unknown locations visible rather than guessing from Unity coordinates.
        return false;
    }

    private bool IsActualNetworkDungeonEnemyForLocalRules()
    {
        EnemyWorldPosition ewp = worldPosition != null ? worldPosition : GetComponent<EnemyWorldPosition>();
        if (ewp == null || !ewp.isDungeonSpawn)
            return false;

        // Network dungeons now start around -500 and below. If an enemy is above this
        // band, treat it as exterior/building even if a stale SetupDemoEnemy.isDungeonEnemy
        // flag promoted it to isDungeonSpawn.
        return transform.position.y <= NetworkDungeonModeYThreshold;
    }

    private void SetLocalPerPlayerCulled(bool culled)
    {
        if (localPerPlayerCulled == culled)
            return;

        localPerPlayerCulled = culled;

        RefreshVisualAndAudioState();
        RefreshLocalCollisionState();
    }

    private void RefreshVisualAndAudioState()
    {
        bool visible = !IsLocalPresentationSuspended;
        if (presentationMotor == null) presentationMotor = GetComponent<EnemyMotor>();
        bool simulator = presentationMotor != null && presentationMotor.ShouldSimulateCombat();
        // Restore physics before re-enabling a local simulator's native ticks.
        RefreshObserverCollisions();
        RefreshObserverRuntime(visible);
        RefreshHiddenObserverTicks(visible);
        if (presentationStateApplied && lastPresentationVisible == visible && lastPresentationSimulator == simulator)
            return;
        bool visibilityChanged = !presentationStateApplied || lastPresentationVisible != visible;
        presentationStateApplied = true;
        lastPresentationVisible = visible;
        lastPresentationSimulator = simulator;
        if (presentationMobiles == null) presentationMobiles = GetComponentsInChildren<DaggerfallMobileUnit>(true);
        if (presentationSounds == null) presentationSounds = GetComponentsInChildren<EnemySounds>(true);
        foreach (var mobile in presentationMobiles)
            if (mobile != null) mobile.SetNetworkPresentationSuspended(!visible, simulator);
        foreach (var sound in presentationSounds)
            if (sound != null) sound.PresentationSuspended = !visible;
        if (visibilityChanged && !visible) RefreshPresentationLightsAndParticles(false);

        // Keep the old direct visual toggle, but also toggle all child renderers.
        // Some enemy prefabs/visuals do not use a direct child named MobileUnitBillboard,
        // so renderer toggling makes local culling actually hide the sprite/model too.
        if (visual != null)
            visual.SetActive(visible || simulator);

        // Restore only after reactivating the billboard. On hidden ownership gain,
        // suppress any play-on-awake effects without replacing their saved states.
        if (visibilityChanged && visible) RefreshPresentationLightsAndParticles(true);
        if (!visible)
        {
            if (presentationLights != null)
                foreach (var light in presentationLights)
                    if (light != null) light.enabled = false;
            if (presentationParticles != null)
                foreach (var particles in presentationParticles)
                    if (particles != null && particles.isPlaying) particles.Pause(false);
        }

        Renderer[] renderers = GetCachedRenderers();
        if (renderers != null)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }
        }

        AudioSource[] audioSources = GetCachedAudioSources();
        if (audioSources != null)
        {
            for (int i = 0; i < audioSources.Length; i++)
            {
                if (audioSources[i] != null)
                    audioSources[i].enabled = visible;
            }
        }
    }

    private void RefreshPresentationLightsAndParticles(bool visible)
    {
        if (presentationLights == null)
        {
            presentationLights = GetComponentsInChildren<Light>(true);
            presentationLightStates = new bool[presentationLights.Length];
            for (int i = 0; i < presentationLights.Length; i++)
                presentationLightStates[i] = presentationLights[i] != null && presentationLights[i].enabled;
        }
        if (presentationParticles == null)
        {
            presentationParticles = GetComponentsInChildren<ParticleSystem>(true);
            presentationParticleStates = new bool[presentationParticles.Length];
        }
        for (int i = 0; i < presentationLights.Length; i++)
        {
            Light light = presentationLights[i];
            if (light == null) continue;
            if (!visible) { presentationLightStates[i] = light.enabled; light.enabled = false; }
            else light.enabled = presentationLightStates[i];
        }
        for (int i = 0; i < presentationParticles.Length; i++)
        {
            ParticleSystem particles = presentationParticles[i];
            if (particles == null) continue;
            if (!visible)
            {
                presentationParticleStates[i] = particles.isPlaying;
                if (particles.isPlaying) particles.Pause(false);
            }
            else if (presentationParticleStates[i]) particles.Play(false);
        }
    }

    private Renderer[] GetCachedRenderers()
    {
        if (cachedRenderers == null)
            cachedRenderers = GetComponentsInChildren<Renderer>(true);

        return cachedRenderers;
    }

    private AudioSource[] GetCachedAudioSources()
    {
        if (cachedAudioSources == null)
            cachedAudioSources = GetComponentsInChildren<AudioSource>(true);

        return cachedAudioSources;
    }

    private Collider[] GetCachedEnemyColliders()
    {
        if (cachedEnemyColliders == null)
            cachedEnemyColliders = GetComponentsInChildren<Collider>(true);

        return cachedEnemyColliders;
    }

    private Collider[] GetCachedLocalPlayerColliders()
    {
        if (cachedLocalPlayerColliders == null || cachedLocalPlayerColliders.Length == 0)
        {
            GameObject localPlayerObject = (GameManager.Instance != null) ? GameManager.Instance.PlayerObject : null;
            if (localPlayerObject != null)
                cachedLocalPlayerColliders = localPlayerObject.GetComponentsInChildren<Collider>(true);
        }

        return cachedLocalPlayerColliders;
    }

    // Local collision suspension: no GameObject, network identity, or owner changes.
    private bool observerCollisionsSuspended;
    private int observerPhysicsSettleDepth;
    private readonly System.Collections.Generic.List<Collider> observerDisabledColliders =
        new System.Collections.Generic.List<Collider>();

    private static void OnObserverPhysicsAuthorityChanged(NetworkConnection connection,
        NetworkIdentity identity, bool assigned)
    {
        // RemoveClientAuthority invokes this BEFORE clearing connectionToClient.
        // Restore unconditionally so a server fallback has physics immediately.
        // The normal local visibility refresh decides when to suspend again.
        if (identity == null) return;
        var authority = identity.GetComponent<DynamicEnemyAuthority>();
        if (authority != null)
        {
            authority.WakeObserverRuntime();
            authority.RestoreObserverCollisions();
        }
    }

    private void RefreshObserverCollisions()
    {
        bool suspend = isActiveAndEnabled && retainedRuntimeStarted && netId != 0 &&
            (NetworkServer.active || NetworkClient.active) && IsLocalPresentationSuspended &&
            Time.time - spawnTime >= LocalPerPlayerCullSpawnGrace &&
            observerPhysicsSettleDepth == 0 && presentationMotor != null &&
            presentationMotor.GetType() == typeof(EnemyMotor) &&
            !presentationMotor.ShouldSimulateMotor();
        if (!suspend)
        {
            RestoreObserverCollisions();
            return;
        }
        if (observerCollisionsSuspended) return;

        // Snapshot on each suspension transition, not every frame. Include inactive
        // descendants, but never another network actor parented beneath this enemy.
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled ||
                collider.GetComponentInParent<NetworkIdentity>() != netIdentity) continue;
            // Unknown mod-driven dynamic bodies still need collision support.
            Rigidbody body = collider.attachedRigidbody;
            if (body != null && !body.isKinematic) continue;
            observerDisabledColliders.Add(collider);
            collider.enabled = false;
        }
        observerCollisionsSuspended = true;
    }

    private void RestoreObserverCollisions()
    {
        if (!observerCollisionsSuspended) return;
        observerCollisionsSuspended = false;
        for (int i = 0; i < observerDisabledColliders.Count; i++)
        {
            Collider collider = observerDisabledColliders[i];
            if (collider != null) collider.enabled = true;
        }
        observerDisabledColliders.Clear();
        // Unity can discard pair-ignore state when a collider is disabled.
        if (localCollisionPairsIgnored)
        {
            localCollisionPairsIgnored = false;
            SetLocalPlayerCollisionIgnored(true);
        }
    }

    private void RefreshLocalCollisionState()
    {
        RefreshObserverCollisions();
        // Retain the existing local-player ghost rule, including for deactivated owners.
        SetLocalPlayerCollisionIgnored(authorityDeactivated || localPerPlayerCulled);
    }

    private void SetLocalPlayerCollisionIgnored(bool ignored)
    {
        if (localCollisionPairsIgnored == ignored)
            return;

        Collider[] playerColliders = GetCachedLocalPlayerColliders();
        Collider[] enemyColliders = GetCachedEnemyColliders();

        if (playerColliders == null || enemyColliders == null)
            return;

        for (int p = 0; p < playerColliders.Length; p++)
        {
            Collider pc = playerColliders[p];
            if (pc == null)
                continue;

            for (int e = 0; e < enemyColliders.Length; e++)
            {
                Collider ec = enemyColliders[e];
                if (ec == null || ec == pc)
                    continue;

                // Pair-ignore remains separate from hidden observer suspension.
                Physics.IgnoreCollision(pc, ec, ignored);
            }
        }

        localCollisionPairsIgnored = ignored;
    }

    // ======== MOTION GATE (no CharacterController toggles) & BUFFER RESET ========

    // Do NOT toggle CharacterController here. Only make RB inert when client-owned.
    void ApplyAuthorityMotionGate()
    {
        var id = netIdentity;
        var rb = GetComponent<Rigidbody>();

        bool server = NetworkServer.active;
        bool clientOwned = server && id != null && id.connectionToClient != null;

        if (server && rb)
            rb.isKinematic = PassiveDungeonGridTest || authorityDeactivated || clientOwned;
    }

    
    void ResetNetworkTransformBuffers()
    {
        var nt = GetComponent<NetworkTransform>();
        if (!nt) return;

        // Dedicated server: keep old quick toggle.
        if (isServer && !isClient)
        {
            nt.enabled = false;
            nt.enabled = true;
            return;
        }

        // Client-side: disable for a couple frames so stale snapshots can't immediately overwrite our snap/warmup RPC.
        if (!isClient) return;

        if (_ntResetCo != null)
            StopCoroutine(_ntResetCo);

        _ntResetCo = StartCoroutine(CoResetNetworkTransform(nt));
    }

    IEnumerator CoResetNetworkTransform(NetworkTransform nt)
    {
        if (!nt) yield break;

        nt.enabled = false;
        yield return null;
        yield return null;
        if (nt) nt.enabled = true;

        _ntResetCo = null;
    }



// ======== SNAP LOGIC (INSTANT HANDOFF) + RELIABLE WARMUP PUMP ========

    [Command(requiresAuthority = true)]
    void CmdSnapTo(Vector3 pos, Quaternion rot)
    {
        float traceBeforeY = transform.position.y;
        if (Mathf.Abs(pos.y - traceBeforeY) >= 1.0f)
        {
            Debug.LogWarning(
                $"[EnemyDeathTrace][DEA][CmdSnapTo] enemy='{gameObject.name}' netId={(netIdentity != null ? netIdentity.netId : 0U)} " +
                $"oldY={traceBeforeY:0.000} newY={pos.y:0.000} deltaY={(pos.y - traceBeforeY):0.000} " +
                $"authority={hasAuthority} server={isServer} client={isClient}");
        }

        // Server: set world pose immediately
        transform.SetPositionAndRotation(pos, rot);

        // Clear server NT buffer so it doesn't lerp from stale state
        ResetNetworkTransformBuffers();

        // No NT disable here.

        // Reliable snap for all clients (including host client)
        RpcSnapAll(pos, rot);
    }

    [ClientRpc]
    void RpcSnapAll(Vector3 pos, Quaternion rot)
    {
        float traceBeforeY = transform.position.y;
        if (!hasAuthority && Mathf.Abs(pos.y - traceBeforeY) >= 1.0f)
        {
            Debug.LogWarning(
                $"[EnemyDeathTrace][DEA][RpcSnapAll] enemy='{gameObject.name}' netId={(netIdentity != null ? netIdentity.netId : 0U)} " +
                $"oldY={traceBeforeY:0.000} newY={pos.y:0.000} deltaY={(pos.y - traceBeforeY):0.000} " +
                $"authority={hasAuthority} server={isServer} client={isClient}");
        }

        if (!hasAuthority)
            transform.SetPositionAndRotation(pos, rot);

        // Clear client NT buffers so they don't smooth from old state
        ResetNetworkTransformBuffers();
    }

    // --- WARMUP POSE PUMP ---
    // Sends reliable pose updates for the warmup window so we don't wait for NT's potential "dead" period after authority flip.
    [Command(requiresAuthority = true)]
    void CmdWarmupPose(Vector3 pos, Quaternion rot)
    {
        float traceBeforeY = transform.position.y;
        if (Mathf.Abs(pos.y - traceBeforeY) >= 1.0f)
        {
            Debug.LogWarning(
                $"[EnemyDeathTrace][DEA][CmdWarmupPose] enemy='{gameObject.name}' netId={(netIdentity != null ? netIdentity.netId : 0U)} " +
                $"oldY={traceBeforeY:0.000} newY={pos.y:0.000} deltaY={(pos.y - traceBeforeY):0.000}");
        }

        // server adopts pose (reliable), then rebroadcasts to observers (reliable)
        transform.SetPositionAndRotation(pos, rot);
        RpcWarmupPose(pos, rot);
    }

    [ClientRpc]
    void RpcWarmupPose(Vector3 pos, Quaternion rot)
    {
        if (!hasAuthority)
            transform.SetPositionAndRotation(pos, rot);

        // Clear client NT buffers so they don't smooth from old state
        ResetNetworkTransformBuffers();
    }
    // owner only: push pose every PUMP_STEP for WARMUP_DURATION
    IEnumerator WarmupPosePump(float duration, float step)
    {
        float t = 0f;
        while (t < duration)
        {
            CmdWarmupPose(transform.position, transform.rotation);

            float w = 0f;
            while (w < step) { w += Time.unscaledDeltaTime; yield return null; }

            t += step;
        }
        warmupPumpCo = null;
    }

    // Optional: brief burst of higher send rate on owner right after authority begins
    IEnumerator BurstOwnerSendRate(float seconds = 0.5f, float interval = 0.02f)
    {
        var nt = GetComponent<NetworkTransform>();
        if (!nt) yield break;

        float old = nt.syncInterval; // NetworkBehaviour.syncInterval in older Mirror versions
        nt.syncInterval = interval;

        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        nt.syncInterval = old;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        netIdentity = GetComponent<NetworkIdentity>();
        EnemyInterestManagement.Install();
        // Use DF relevance, including prefabs previously set ForceShown to bypass Unity distance AOI.
        if (NetworkServer.aoi is EnemyInterestManagement && netIdentity.visible == Visibility.ForceShown)
            netIdentity.visible = Visibility.Default;
        // One static handler, not one subscriber per enemy. Mirror clears it on reset.
        NetworkIdentity.clientAuthorityCallback -= OnObserverPhysicsAuthorityChanged;
        NetworkIdentity.clientAuthorityCallback += OnObserverPhysicsAuthorityChanged;
        ApplyAuthorityMotionGate(); // RB inert if needed; CC untouched here

        // Server-side spawn settle closes the gap before the first client authority assignment.
        // Without this, gravity/CharacterController can drop a newly spawned indoor quest foe
        // through the floor before the requester receives authority and starts pumping poses.
        if (_serverCreateFoeSpawnSettleCo == null)
            _serverCreateFoeSpawnSettleCo = StartCoroutine(CoServerSettleCreateFoeSpawn());
    }

    public override void OnStartAuthority()
    {
        base.OnStartAuthority();
        WakeObserverRuntime();
        RestoreObserverCollisions();
        RefreshVisualAndAudioState();

        // Owner: clear local NT buffers immediately.
        ResetNetworkTransformBuffers();

        // CreateFoe / quest-spawned foes need to snap to their fixed marker first.
        // Do NOT start the warmup pump before this snap, otherwise we can reliably pump
        // the bad/fallen position to the server for the first authority window.
        if (ShouldRunCreateFoeAuthorityResnap())
        {
            StartCreateFoeAuthorityResnap();
        }
        else
        {
            CmdSnapTo(transform.position, transform.rotation);

            if (WARMUP_DURATION > 0f && warmupPumpCo == null)
                warmupPumpCo = StartCoroutine(WarmupPosePump(WARMUP_DURATION, PUMP_STEP));
        }

        // Optional short send-rate burst (harmless)
        StartCoroutine(BurstOwnerSendRate());
    }

    public override void OnStopAuthority()
    {
        base.OnStopAuthority();
        RefreshVisualAndAudioState();
        ResetNetworkTransformBuffers();
    }

    // ======== VISUAL HELPERS ========

    void HideVisuals()
    {
        if (visual != null)
            visual.SetActive(false);
    }

    void ShowVisuals()
    {
        RefreshVisualAndAudioState();
    }

    void ForcePositionResync()
    {
        var netTransform = GetComponent<NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.enabled = false;
            netTransform.enabled = true;
            Debug.Log("[DynamicEnemyAuthority] NetworkTransform toggled to resync");
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, deactivateDistance);

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, switchDistance);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, destroyDistance);
    }

    // ======== LIFETIME (DESTROY WHEN FAR) ========

    void TryStartDestroyCountdown()
    {
        if (KeepForDungeonGridTest) return;
        if (persistentOwnerPlayerNetId != 0)
            return;

        if (destroyCoroutine == null)
        {
            if (HorizontalRetentionDistanceDF > 0f && worldPosition != null)
                Debug.Log($"[ExteriorRetention] Starting cleanup countdown for '{name}' net={netId}, DF={worldPosition.worldX}/{worldPosition.worldZ}, horizontal radius={HorizontalRetentionDistanceDF * unityPerDF}m; no player in range (Y ignored).");
            destroyCoroutine = StartCoroutine(DestroyAfterDelay());
        }
    }

    void CancelDestroyCountdown()
    {
        if (destroyCoroutine != null)
        {
            StopCoroutine(destroyCoroutine);
            destroyCoroutine = null;
        }
    }

    IEnumerator DestroyAfterDelay()
    {
        yield return new WaitForSeconds(destroyGracePeriod);

        if (!KeepForDungeonGridTest && persistentOwnerPlayerNetId == 0 && !IsAnyPlayerStillOutOfRange()) // double check before destroy
        {
            Debug.Log($"[DynamicEnemyAuthority] Still no players near. Destroying enemy '{name}' (NetID: {netIdentity.netId})");
            NetworkServer.Destroy(gameObject);
        }

        destroyCoroutine = null;
    }

    // NOTE: This keeps your original predicate: returns TRUE if any player is in range (DF + Y).
    // Caller uses !IsAnyPlayerStillOutOfRange() to decide destroy. Leaving as-is to avoid changing behavior.
    bool IsWithinHorizontalRetention(PositionMultiplayer player)
    {
        // Never wrap by terrain size: different peers can share Unity coordinates
        // while being kilometres apart in the Daggerfall world.
        double dx = (double)worldPosition.worldX - player.x;
        double dz = (double)worldPosition.worldZ - player.z;
        double radius = HorizontalRetentionDistanceDF;
        return dx * dx + dz * dz <= radius * radius;
    }

    bool IsAnyPlayerStillOutOfRange()
    {
        var players = GetCachedPlayers(true);

        foreach (var player in players)
        {
            if (player == null)
                continue;

            var pos = player.GetComponent<PositionMultiplayer>();
            if (pos == null)
                continue;

            float daggerfallDistance = Vector2.Distance(
                new Vector2(worldPosition.worldX, worldPosition.worldZ),
                new Vector2(pos.x, pos.z)
            );

            float yDistance = Mathf.Abs(transform.position.y - player.transform.position.y);

            if (HorizontalRetentionDistanceDF > 0f
                ? IsWithinHorizontalRetention(pos)
                : daggerfallDistance < destroyDistance && yDistance < maxYDistance)
                return true;
        }

        return false;
    }

    private bool ShouldRunCreateFoeAuthorityResnap()
    {
        if (persistentOwnerPlayerNetId != 0 || HasLocalOwnershipClaim)
            return false;

        if (_didCreateFoeAuthorityResnap || !hasAuthority)
            return false;

        // Host-client special case:
        // In host mode this object is both the server object and the local authoritative
        // client object. OnStartServer() already runs CoServerSettleCreateFoeSpawn().
        // If OnStartAuthority() also runs CoResnapCreateFoeOnAuthority() at the same
        // time, both coroutines temporarily disable EnemyMotor and then restore the
        // captured enabled state. Depending on order, one coroutine can capture
        // motorWasEnabled=false while the other settle coroutine has it disabled, then
        // restore it to false permanently. The result is exactly what we see: the enemy
        // keeps correct hostility and can attack from place, but does not walk.
        //
        // Remote/client-owned enemies still need the authority resnap. Host-owned
        // enemies do not; the server settle path is enough.
        if (isServer && isClient)
            return false;

        var ewp = GetComponent<EnemyWorldPosition>();
        if (ewp == null || !ewp.isCreateFoeWaveSpawn || ewp.intendedSpawnPos == Vector3.zero)
            return false;

        // Only the requester should resnap this fixed quest/wave spawn.
        var lp = PlayerMultiplayer.localPlayer;
        if (lp != null && ewp.requesterNetId != 0 && lp.netId != ewp.requesterNetId)
            return false;

        return true;
    }

    private void StartCreateFoeAuthorityResnap()
    {
        if (_createFoeAuthorityResnapCo != null || _didCreateFoeAuthorityResnap)
            return;

        _createFoeAuthorityResnapCo = StartCoroutine(CoResnapCreateFoeOnAuthority());
    }

    private void RestoreSettledMotorState(EnemyMotor settleMotor, bool motorWasEnabled, string source)
    {
        if (settleMotor == null)
            return;

        // Settling owns positioning and motor enablement, not combat state.
        // Preserve the live value, including changes received while settling.
        // Prevent a deferred first Start() from replacing it with spawn reaction.
        settleMotor.PreserveCurrentHostilityOnStart();

        // Do NOT blindly restore motorWasEnabled here.
        //
        // For these fixed marker spawns, this script is the thing that temporarily disables
        // EnemyMotor during the floor-settle window. In host mode the motor can already be
        // disabled when this coroutine samples motorWasEnabled (for example because Start()
        // has not completed yet, or because another settle/helper path disabled it first).
        // Restoring that sampled false leaves the quest foe permanently unable to walk.
        //
        // The only time the motor should remain disabled after settling is when the global
        // authority-deactivation system has intentionally deactivated the enemy.
        bool shouldEnableAfterSettle = !authorityDeactivated;
        settleMotor.enabled = shouldEnableAfterSettle;

        Debug.Log($"[DynamicEnemyAuthority][MotorSettleRestore] {source} enemy='{name}' capturedEnabled={motorWasEnabled} restoredEnabled={shouldEnableAfterSettle} hostile={settleMotor.IsHostile} authorityDeactivated={authorityDeactivated}");

        // Retain the next-frame enable safety net without replaying combat state.
        if (shouldEnableAfterSettle)
            StartCoroutine(CoRestoreMotorEnabledNextFrame(settleMotor, source));
    }

    private IEnumerator CoRestoreMotorEnabledNextFrame(EnemyMotor settleMotor, string source)
    {
        yield return null;

        if (settleMotor != null && !settleMotor.enabled && !authorityDeactivated)
        {
            settleMotor.PreserveCurrentHostilityOnStart();
            settleMotor.enabled = true;
            Debug.Log($"[DynamicEnemyAuthority][MotorSettleRestore] {source}:next-frame re-enabled EnemyMotor on '{name}'");
        }

    }

    private IEnumerator CoServerSettleCreateFoeSpawn()
    {
        // Server-only safety net. Wait briefly for SyncVars/metadata set before NetworkServer.Spawn().
        if (!isServer) yield break;

        EnemyWorldPosition ewp = null;
        for (int i = 0; i < 30; i++)
        {
            ewp = GetComponent<EnemyWorldPosition>();
            if (ewp != null && ewp.isCreateFoeWaveSpawn && ewp.intendedSpawnPos != Vector3.zero)
                break;
            yield return null;
        }

        if (_didServerCreateFoeSpawnSettle) yield break;
        if (ewp == null || !ewp.isCreateFoeWaveSpawn || ewp.intendedSpawnPos == Vector3.zero) yield break;

        _didServerCreateFoeSpawnSettle = true;

        var cc = GetComponent<CharacterController>();
        var rb = GetComponent<Rigidbody>();

        // Do not capture a controller disabled by observer culling as its normal state.
        WakeObserverRuntime();
        RestoreObserverCollisions();
        observerPhysicsSettleDepth++;
        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Combat state stays live while disabled and is preserved on re-enable.
        if (settleMotor != null)
            settleMotor.enabled = false;

        if (cc) cc.enabled = false;
        if (rbHad)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        Vector3 intended = ewp.intendedSpawnPos;
        Vector3 target = intended;

        // Hold the fixed marker XZ for several frames and settle Y to the highest nearby floor.
        for (int settleFrame = 0; settleFrame < 20; settleFrame++)
        {
            target = ComputeSettledCreateFoePosition(intended, cc);
            transform.position = target;
            yield return null;
        }

        ResetNetworkTransformBuffers();
        RpcSnapAll(transform.position, transform.rotation);

        if (rbHad)
        {
            rb.isKinematic = rbWasKinematic;
            rb.useGravity = rbHadGravity;
        }
        if (cc && ccWasEnabled) cc.enabled = true;
        if (settleMotor != null)
            RestoreSettledMotorState(settleMotor, motorWasEnabled, "create-foe-settle");
        observerPhysicsSettleDepth--;

        _serverCreateFoeSpawnSettleCo = null;
    }

    private Vector3 ComputeSettledCreateFoePosition(Vector3 intended, CharacterController cc)
    {
        float ccRadius = (cc != null) ? cc.radius : 0.4f;
        float ccHeight = (cc != null) ? cc.height : 1.8f;
        float ccCenterY = (cc != null) ? cc.center.y : (ccHeight * 0.5f);

        float footLift = (ccHeight * 0.5f - ccCenterY) + 0.05f;
        float probeStartUp = Mathf.Max(0.75f, footLift + 0.25f);
        float probeDistance = 3.0f;
        float probeOffset = Mathf.Max(0.05f, ccRadius * 0.8f);

        bool got = false;
        Vector3 best = new Vector3(intended.x, intended.y + footLift, intended.z);
        float bestScore = float.MaxValue;

        Vector3 baseXZ = new Vector3(intended.x, intended.y, intended.z);

        Vector3[] offsets = new Vector3[]
        {
            Vector3.zero,
            new Vector3( probeOffset, 0f, 0f),
            new Vector3(-probeOffset, 0f, 0f),
            new Vector3(0f, 0f,  probeOffset),
            new Vector3(0f, 0f, -probeOffset),
        };

        for (int oi = 0; oi < offsets.Length; oi++)
        {
            Vector3 start = baseXZ + offsets[oi] + Vector3.up * probeStartUp;
            RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, probeDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
                continue;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int hi = 0; hi < hits.Length; hi++)
            {
                RaycastHit hit = hits[hi];
                if (hit.collider == null)
                    continue;

                Transform tr = hit.collider.transform;
                if (tr.GetComponentInParent<DaggerfallEnemy>() != null)
                    continue;
                if (tr.GetComponentInParent<PlayerMultiplayer>() != null)
                    continue;
                if (tr.GetComponentInParent<QuestResourceBehaviour>() != null)
                    continue;

                if (hit.normal.y < 0.7f)
                    continue;

                Vector3 candidate = new Vector3(intended.x, hit.point.y + footLift, intended.z);

                // Multi-storey interiors often have overlapping floors at the same XZ.
                // Choose the floor closest to the original marker Y instead of the highest hit.
                float score = Mathf.Abs(candidate.y - intended.y);
                if (!got || score < bestScore)
                {
                    got = true;
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private IEnumerator CoResnapCreateFoeOnAuthority()
    {
        // Wait a couple frames for NetworkTransform / controllers to initialize.
        yield return null;
        yield return null;

        if (_didCreateFoeAuthorityResnap) yield break;
        if (!hasAuthority) yield break;

        var ewp = GetComponent<EnemyWorldPosition>();
        if (ewp == null || !ewp.isCreateFoeWaveSpawn) yield break;

        // Only requester should resnap (prevents other clients fighting over placement)
        var lp = PlayerMultiplayer.localPlayer;
        if (lp != null && ewp.requesterNetId != 0 && lp.netId != ewp.requesterNetId)
            yield break;

        Vector3 intended = ewp.intendedSpawnPos;
        if (intended == Vector3.zero) yield break;

        var cc = GetComponent<CharacterController>();
        var rb = GetComponent<Rigidbody>();

        // Temporarily disable physics resolution while we settle.
        // Do not capture a controller disabled by observer culling as its normal state.
        WakeObserverRuntime();
        RestoreObserverCollisions();
        observerPhysicsSettleDepth++;
        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Combat state stays live while disabled and is preserved on re-enable.
        if (settleMotor != null)
            settleMotor.enabled = false;

        if (cc) cc.enabled = false;
        if (rbHad)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Multi-frame settle: keep the intended XZ and choose the floor closest to intended Y.
        for (int settleFrame = 0; settleFrame < 20; settleFrame++)
        {
            transform.position = ComputeSettledCreateFoePosition(intended, cc);
            yield return null;
        }

        // Kick the reliable warmup pump AFTER we have settled onto the correct floor.
        ResetNetworkTransformBuffers();
        CmdSnapTo(transform.position, transform.rotation);
        if (WARMUP_DURATION > 0f)
        {
            if (warmupPumpCo != null) StopCoroutine(warmupPumpCo);
            warmupPumpCo = StartCoroutine(WarmupPosePump(WARMUP_DURATION, PUMP_STEP));
        }

        // Restore components
        if (rbHad)
        {
            rb.isKinematic = rbWasKinematic;
            rb.useGravity = rbHadGravity;
        }
        if (cc && ccWasEnabled) cc.enabled = true;
        if (settleMotor != null)
            RestoreSettledMotorState(settleMotor, motorWasEnabled, "create-foe-settle");
        observerPhysicsSettleDepth--;

        _didCreateFoeAuthorityResnap = true;
        _createFoeAuthorityResnapCo = null;
    }



    private IEnumerator CoLateResnapWatcher()
    {
        // Wait up to ~1 second (60 frames) for SyncVars to arrive and/or authority to be ready.
        for (int i = 0; i < 60; i++)
        {
            if (_didCreateFoeAuthorityResnap)
                yield break;

            if (hasAuthority)
            {
                var ewp = GetComponent<EnemyWorldPosition>();
                if (ewp != null && ewp.isCreateFoeWaveSpawn && ewp.intendedSpawnPos != Vector3.zero)
                {
                    var lp = PlayerMultiplayer.localPlayer;
                    if (lp == null || ewp.requesterNetId == 0 || lp.netId == ewp.requesterNetId)
                    {
                        StartCreateFoeAuthorityResnap();
                        yield break;
                    }
                }
            }

            yield return null;
        }
    }


    private IEnumerator CoLateFixedSpawnObserverSnap()
    {
        // Non-owner observers (especially host observing a client-owned indoor foe) can receive
        // an early/fallen pose and then receive no further NetworkTransform update while the owner
        // is standing still. This visually pins the observer to the fixed intended spawn pose once
        // the SyncVars arrive. The owner still handles authoritative resnap/pump separately.
        for (int i = 0; i < 120; i++)
        {
            if (_didCreateFoeObserverSnap)
                yield break;

            if (!hasAuthority)
            {
                var ewp = GetComponent<EnemyWorldPosition>();
                if (ewp != null && ewp.isCreateFoeWaveSpawn && ewp.intendedSpawnPos != Vector3.zero)
                {
                    yield return StartCoroutine(CoObserverSnapFixedSpawn(ewp.intendedSpawnPos));
                    yield break;
                }
            }

            yield return null;
        }
    }

    private IEnumerator CoObserverSnapFixedSpawn(Vector3 intended)
    {
        if (_didCreateFoeObserverSnap)
            yield break;

        _didCreateFoeObserverSnap = true;

        var cc = GetComponent<CharacterController>();
        var rb = GetComponent<Rigidbody>();

        // Do not capture a controller disabled by observer culling as its normal state.
        WakeObserverRuntime();
        RestoreObserverCollisions();
        observerPhysicsSettleDepth++;
        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Combat state stays live while disabled and is preserved on re-enable.
        if (settleMotor != null)
            settleMotor.enabled = false;

        if (cc) cc.enabled = false;
        if (rbHad)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        ResetNetworkTransformBuffers();

        // Hold the settled transform-center pose briefly. Raw intendedSpawnPos can be a
        // marker/floor reference and may place the capsule halfway through stacked floors.
        for (int i = 0; i < 90; i++)
        {
            if (hasAuthority)
                break;

            transform.position = ComputeSettledCreateFoePosition(intended, cc);
            yield return null;
        }

        ResetNetworkTransformBuffers();

        if (rbHad)
        {
            rb.isKinematic = rbWasKinematic;
            rb.useGravity = rbHadGravity;
        }
        if (cc && ccWasEnabled) cc.enabled = true;
        if (settleMotor != null)
            RestoreSettledMotorState(settleMotor, motorWasEnabled, "create-foe-settle");
        observerPhysicsSettleDepth--;
    }


}

