using System.Collections;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallConnect;

[RequireComponent(typeof(NetworkIdentity))]
public class DynamicEnemyAuthority : NetworkBehaviour
{
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

    [Tooltip("Max Unity Y distance difference before deactivation/destroy checks (Unity units).")]
    public float maxYDistance = 100f;

    [Tooltip("Grace period (seconds) before enemy can be destroyed.")]
    public float destroyGracePeriod = 10f;

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
    // Important: never disable the enemy CharacterController/colliders globally here.
    // We only use pairwise Physics.IgnoreCollision(localPlayerCollider, enemyCollider).
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

    void Start()
    {
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
            StartCoroutine(AuthorityCheckRoutine());
    }

    void LateUpdate()
    {
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

    private void NormalizeExteriorTerrainFrameNearLocalPlayer()
    {
        if (!NetworkClient.active)
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
            float expectedDx = NormalizeTerrainFrameDelta((worldPosition.worldX - localWorldX) * unityPerDF);
            float expectedDz = NormalizeTerrainFrameDelta((worldPosition.worldZ - localWorldZ) * unityPerDF);

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
        Vector3 correction = corrected - pos;
        correction.y = 0f;

        if (correction.sqrMagnitude < TerrainFrameCorrectionThresholdUnity * TerrainFrameCorrectionThresholdUnity)
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
    }

    void UpdateAuthority()
    {
        if (!NetworkServer.active || !netIdentity || worldPosition == null || !worldPosition.isActiveAndEnabled || !worldPosition.initialized)
            return;

        bool inGrace = (Time.time - spawnTime < destroyGracePeriod);

        currentOwner = netIdentity.connectionToClient as NetworkConnectionToClient;

        // Track for destroy logic: is there anyone within destroyDistance (DF→Unity) AND maxYDistance (Unity)?
        bool anyPlayerWithinDestroy = false;

        // Track for deactivate logic: if nobody is within this smaller radius, the
        // enemy falls back to host ownership and then turns off motor/gravity/visuals
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
            if (distU < destroyDistanceU && absYU < maxYDistance)
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

                netIdentity.AssignClientAuthority(newOwner);
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

        // No valid candidate in range -> hand to host. Do not remove/reassign/reset every check if
        // the host already owns it, otherwise NetworkTransform can appear to tick on/off constantly.
        NetworkConnectionToClient hostConnection = NetworkServer.localConnection as NetworkConnectionToClient;
        if (hostConnection != null)
        {
            if (currentOwner == hostConnection)
            {
                SetAuthorityDeactivated(!anyPlayerWithinDeactivate);
                ShowVisuals();
                return;
            }

            if (currentOwner != null)
            {
                netIdentity.RemoveClientAuthority();
                currentOwner = null;
            }

            netIdentity.AssignClientAuthority(hostConnection);
            currentOwner = hostConnection;
            SetAuthorityDeactivated(!anyPlayerWithinDeactivate);
            ApplyAuthorityMotionGate();
            ResetNetworkTransformBuffers();
            ShowVisuals();
        }
    }


    // ======== LOCAL PER-PLAYER VISIBILITY/COLLISION CULLING ========

    private void UpdateLocalPerPlayerCulling()
    {
        if (!NetworkClient.active)
            return;

        if (Time.time < nextLocalPerPlayerCullCheckTime)
            return;

        nextLocalPerPlayerCullCheckTime = Time.time + LocalPerPlayerCullInterval;

        bool shouldCull = ShouldCullForLocalPlayer();
        SetLocalPerPlayerCulled(shouldCull);
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
        float absY = Mathf.Abs(localPlayerPos.y - transform.position.y);

        bool useDungeonSlotRules = IsActualNetworkDungeonEnemyForLocalRules();

        // Real network dungeon enemies live far below the exterior and their DF X/Z is
        // only the dungeon entrance anchor. For them, local Unity/Y distance is the only
        // useful per-player visibility rule.
        if (useDungeonSlotRules)
            return Vector3.Distance(localPlayerPos, transform.position) > LocalPerPlayerCullDistanceUnity;

        // Exterior and building/interior enemies need DF X/Z. This fixes the case where
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

        // Last-resort fallback only when DF metadata is unavailable after the short grace.
        // This will not solve cross-world same-Unity-coordinate cases, but it is safer than
        // making a visible nearby enemy pass-through because metadata is missing.
        return Vector3.Distance(localPlayerPos, transform.position) > LocalPerPlayerCullDistanceUnity;
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
        bool visible = !authorityDeactivated && !localPerPlayerCulled;

        // Keep the old direct visual toggle, but also toggle all child renderers.
        // Some enemy prefabs/visuals do not use a direct child named MobileUnitBillboard,
        // so renderer toggling makes local culling actually hide the sprite/model too.
        if (visual != null)
            visual.SetActive(visible);

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

    private void RefreshLocalCollisionState()
    {
        // If the enemy is globally deactivated or locally culled, the local player should
        // be able to pass through it. This is pairwise only; enemy colliders/controllers
        // stay enabled for server/owner simulation.
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

                // Never disable the enemy CharacterController/collider itself. Pair-ignore
                // is local to this physics scene and does not make EnemyMotor's controller inactive.
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
            rb.isKinematic = clientOwned;
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
        if (destroyCoroutine == null)
        {
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

        if (!IsAnyPlayerStillOutOfRange()) // double check before destroy
        {
            Debug.Log($"[DynamicEnemyAuthority] Still no players near. Destroying enemy '{name}' (NetID: {netIdentity.netId})");
            NetworkServer.Destroy(gameObject);
        }

        destroyCoroutine = null;
    }

    // NOTE: This keeps your original predicate: returns TRUE if any player is in range (DF + Y).
    // Caller uses !IsAnyPlayerStillOutOfRange() to decide destroy. Leaving as-is to avoid changing behavior.
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

            if (daggerfallDistance < destroyDistance && yDistance < maxYDistance)
                return true;
        }

        return false;
    }

    private bool ShouldRunCreateFoeAuthorityResnap()
    {
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

    private void RestoreSettledMotorState(EnemyMotor settleMotor, bool motorWasEnabled, bool motorWasHostile, string source)
    {
        if (settleMotor == null)
            return;

        // Preserve the quest/passive hostility state across the temporary motor disable.
        // EnemyMotor.Start() reads MobileEnemy.Reactions when the component first becomes enabled,
        // so update the underlying entity reaction before re-enabling the component.
        ForceMotorHostilityState(settleMotor, motorWasHostile, source + ":before-enable");

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

        Debug.Log($"[DynamicEnemyAuthority][MotorSettleRestore] {source} enemy='{name}' capturedEnabled={motorWasEnabled} restoredEnabled={shouldEnableAfterSettle} hostile={motorWasHostile} authorityDeactivated={authorityDeactivated}");

        // If Unity runs EnemyMotor.Start() after this enable, it can still overwrite IsHostile.
        // Re-apply once on the next frame as a safety net.
        if (shouldEnableAfterSettle)
            StartCoroutine(CoRestoreMotorHostilityNextFrame(settleMotor, motorWasHostile, source));
    }

    private IEnumerator CoRestoreMotorHostilityNextFrame(EnemyMotor settleMotor, bool shouldBeHostile, string source)
    {
        yield return null;

        if (settleMotor != null && !settleMotor.enabled && !authorityDeactivated)
        {
            settleMotor.enabled = true;
            Debug.Log($"[DynamicEnemyAuthority][MotorSettleRestore] {source}:next-frame re-enabled EnemyMotor on '{name}'");
        }

        ForceMotorHostilityState(settleMotor, shouldBeHostile, source + ":next-frame");
    }

    private void ForceMotorHostilityState(EnemyMotor motor, bool shouldBeHostile, string source)
    {
        if (motor == null)
            return;

        DaggerfallEntityBehaviour entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
        if (entityBehaviour != null && entityBehaviour.Entity is EnemyEntity enemyEntity)
        {
            var mobileEnemy = enemyEntity.MobileEnemy;
            mobileEnemy.Reactions = shouldBeHostile ? MobileReactions.Hostile : MobileReactions.Passive;
            enemyEntity.SetMobileEnemy(mobileEnemy);
        }

        motor.IsHostile = shouldBeHostile;

        if (!shouldBeHostile)
        {
            EnemySenses senses = GetComponent<EnemySenses>();
            if (senses != null)
            {
                senses.Target = null;
                senses.SecondaryTarget = null;
                senses.DetectedTarget = false;
                senses.LastKnownTargetPos = transform.position;
                senses.OldLastKnownTargetPos = transform.position;
                senses.PredictedTargetPos = transform.position;
            }
        }

        Debug.Log($"[DynamicEnemyAuthority][MotorSettleHostility] {source} enemy='{name}' shouldBeHostile={shouldBeHostile}");
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

        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;
        bool motorWasHostile = settleMotor != null && settleMotor.IsHostile;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Capture IsHostile first so passive quest foes do not become hostile when the
        // motor is re-enabled and EnemyMotor.Start() runs late.
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
            RestoreSettledMotorState(settleMotor, motorWasEnabled, motorWasHostile, "create-foe-settle");

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
        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;
        bool motorWasHostile = settleMotor != null && settleMotor.IsHostile;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Capture IsHostile first so passive quest foes do not become hostile when the
        // motor is re-enabled and EnemyMotor.Start() runs late.
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
            RestoreSettledMotorState(settleMotor, motorWasEnabled, motorWasHostile, "create-foe-settle");

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

        bool ccWasEnabled = cc && cc.enabled;
        bool rbHad = rb != null;
        bool rbWasKinematic = rbHad && rb.isKinematic;
        bool rbHadGravity = rbHad && rb.useGravity;
        EnemyMotor settleMotor = GetComponent<EnemyMotor>();
        bool motorWasEnabled = settleMotor != null && settleMotor.enabled;
        bool motorWasHostile = settleMotor != null && settleMotor.IsHostile;

        // While the CharacterController is disabled for spawn settling, also disable
        // EnemyMotor. Otherwise EnemyMotor.FixedUpdate can call Move/SimpleMove on an
        // inactive CharacterController for enemies stamped as create-foe/wave spawns.
        // Capture IsHostile first so passive quest foes do not become hostile when the
        // motor is re-enabled and EnemyMotor.Start() runs late.
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
            RestoreSettledMotorState(settleMotor, motorWasEnabled, motorWasHostile, "create-foe-settle");
    }


}
