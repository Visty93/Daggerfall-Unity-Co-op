using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Entity;
using Mirror;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Sets up enemy using demo components.
    /// Currently using this component to setup enemy entity.
    /// TODO: Revise enemy instantiation and entity assignment.
    /// </summary>
    [RequireComponent(typeof(EnemyMotor))]
    public class SetupDemoEnemy : NetworkBehaviour
    {
        [SyncVar] public MobileTypes EnemyType = MobileTypes.SkeletalWarrior;
        [SyncVar] public MobileReactions EnemyReaction = MobileReactions.Passive;
        [SyncVar] public MobileGender EnemyGender = MobileGender.Unspecified;
        [SyncVar] public bool AlliedToPlayer = false;
        [SyncVar] public byte ClassicSpawnDistanceType = 0;
				[SyncVar]
public MobileTeams Team;

// Live hostility sync + inspector/debug visibility
[SyncVar(hook = nameof(OnSyncedHostilityChanged))]
public bool SyncedMotorIsHostile = false;
public bool SpawnedMotorIsHostile = false;
public bool CurrentMotorIsHostile = false;
public bool LastAppliedMotorIsHostile = false;

private bool lastObservedIsHostile = false;
private bool suppressLocalHostilityReport = false;
private float nextAllowedHostilityReportTime = 0f;
private bool hasPendingOwnerHostilityBeforeInitialSettings = false;
private bool pendingOwnerHostilityBeforeInitialSettings = false;

[SyncVar(hook = nameof(OnSyncedYChanged))]
public float syncedInitialY;

// Server-authored spawn health baseline.
// This is set by the host before NetworkServer.Spawn() for networked enemies, especially CreateFoe wave spawns.
// Clients apply this as both CurrentHealth and MaxHealth as soon as the object starts, before UI/combat can keep a local roll.
[SyncVar(hook = nameof(OnSyncedSpawnHealthChanged))]
public int SyncedSpawnHealth = 0;

// Requester/player level used when rolling class/human enemy health.
// Monsters still use their own fixed mobile enemy level/health.
[SyncVar] public int SpawnScalingLevel = 0;

private static int activeDungeonSpawnScalingLevel = 0;

public static int PushDungeonSpawnScalingLevel(int level)
{
    int previous = activeDungeonSpawnScalingLevel;
    activeDungeonSpawnScalingLevel = level > 0 ? Mathf.Clamp(level, 1, 100) : 0;
    return previous;
}

public static void RestoreDungeonSpawnScalingLevel(int previous)
{
    activeDungeonSpawnScalingLevel = previous > 0 ? Mathf.Clamp(previous, 1, 100) : 0;
}

private int GetEffectiveSpawnScalingLevel()
{
    if (SpawnScalingLevel > 0)
        return Mathf.Clamp(SpawnScalingLevel, 1, 100);

    if (activeDungeonSpawnScalingLevel > 0)
        return Mathf.Clamp(activeDungeonSpawnScalingLevel, 1, 100);

    return 0;
}

private void OnSyncedYChanged(float oldY, float newY)
{
    transform.position = new Vector3(transform.position.x, newY, transform.position.z);

    EnemyMotor motor = GetComponent<EnemyMotor>();
    if (motor != null)
        motor.LastGroundedY = newY;

    Debug.Log($"[SetupDemoEnemy] Y corrected to syncedInitialY: {newY}");
}


private void OnSyncedSpawnHealthChanged(int oldValue, int newValue)
{
    if (newValue <= 0)
        return;

    QueueAuthoritativeSpawnHealthApply(newValue, isServer ? "syncvar-host" : "syncvar-client");
}

public void ServerCaptureAuthoritativeSpawnHealth()
{
    if (!isServer)
        return;

    if (!TryGetComponent(out DaggerfallEntityBehaviour entityBehaviour) || !(entityBehaviour.Entity is EnemyEntity enemyEntity))
    {
        Debug.LogWarning($"[SpawnHealthDbg][WaveServerCaptureMissingEntity] enemy='{gameObject.name}'");
        return;
    }

    int authoritativeHealth = enemyEntity.CurrentHealth;
    if (authoritativeHealth <= 0)
        authoritativeHealth = enemyEntity.MaxHealth;

    if (authoritativeHealth <= 0)
    {
        MobileEnemy me = enemyEntity.MobileEnemy;
        authoritativeHealth = Mathf.Max(me.MinHealth, me.MaxHealth);
    }

    if (authoritativeHealth <= 0)
    {
        Debug.LogWarning($"[SpawnHealthDbg][WaveServerCaptureInvalid] enemy='{gameObject.name}' cur={enemyEntity.CurrentHealth} max={enemyEntity.MaxHealth}");
        return;
    }

    SyncedSpawnHealth = authoritativeHealth;
    entityBehaviour.ApplyAuthoritativeSpawnHealthAndMax(authoritativeHealth);

    MobileEnemy after = enemyEntity.MobileEnemy;
    Debug.Log($"[SpawnHealthDbg][WaveServerCapture] enemy='{gameObject.name}' syncedSpawnHealth={SyncedSpawnHealth} cur={enemyEntity.CurrentHealth} min={after.MinHealth} max={after.MaxHealth}");
}

private void ApplySyncedSpawnHealthAsMax(int authoritativeHealth, string reason)
{
    if (authoritativeHealth <= 0)
        return;

    if (!TryGetComponent(out DaggerfallEntityBehaviour entityBehaviour) || !(entityBehaviour.Entity is EnemyEntity enemyEntity))
    {
        Debug.Log($"[SpawnHealthDbg][SyncedSpawnHealthDeferred] enemy='{gameObject.name}' reason={reason} hp={authoritativeHealth} entity=missing");
        return;
    }

    entityBehaviour.ApplyAuthoritativeSpawnHealthAndMax(authoritativeHealth);

    MobileEnemy after = enemyEntity.MobileEnemy;
    Debug.Log($"[SpawnHealthDbg][SyncedSpawnHealthApplied] enemy='{gameObject.name}' reason={reason} hp={authoritativeHealth} cur={enemyEntity.CurrentHealth} min={after.MinHealth} max={after.MaxHealth}");
}






private void OnSyncedHostilityChanged(bool oldValue, bool newValue)
{
    // The server already owns/applies its live EnemyMotor state. The hook is for
    // pure client copies consuming the server-published state; applying it again
    // on a listen host can incorrectly retarget a remote-owned enemy to the host.
    if (!isClient || isServer)
        return;

    Debug.Log($"[SetupDemoEnemy][HostilitySync] SyncVar changed on {(isServer ? "host" : "client")} enemy='{gameObject.name}' old={oldValue} new={newValue}");
    ApplyHostilityStateLocally(newValue, "syncvar");
}

private void ApplyHostilityStateLocally(bool makeHostile, string reason, bool assignLocalPlayerAsAttacker = true)
{
    EnemyMotor motor = GetComponent<EnemyMotor>();
    if (motor == null)
        return;

    suppressLocalHostilityReport = true;
    try
    {
        if (makeHostile)
        {
            var playerAttacker = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
            if (assignLocalPlayerAsAttacker && playerAttacker != null)
                motor.MakeEnemyHostileToAttacker(playerAttacker);
            else
                motor.IsHostile = true;
        }
        else
        {
            motor.IsHostile = false;
        }

        ApplyBackingHostilityReaction(makeHostile);

        LastAppliedMotorIsHostile = motor.IsHostile;
        CurrentMotorIsHostile = motor.IsHostile;
        Debug.Log($"[SetupDemoEnemy][HostilitySync] Applied locally enemy='{gameObject.name}' reason={reason} current={motor.IsHostile}");
    }
    finally
    {
        suppressLocalHostilityReport = false;
        lastObservedIsHostile = motor != null && motor.IsHostile;
        nextAllowedHostilityReportTime = Time.time + 0.15f;
    }
}

private void ApplyBackingHostilityReaction(bool makeHostile)
{
    MobileReactions reaction = makeHostile ? MobileReactions.Hostile : MobileReactions.Passive;

    // Publish the matching reaction for late join/settings repair only on the server.
    if (isServer)
        EnemyReaction = reaction;

    // EnemyMotor.Start() and later motor re-enables read MobileEnemy.Reactions.
    // Keep that backing value aligned so an accepted passive state cannot bounce
    // straight back to the spawn-time hostile reaction.
    DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
    if (behaviour != null && behaviour.Entity is EnemyEntity enemyEntity)
    {
        MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;
        mobileEnemy.Reactions = reaction;
        enemyEntity.SetMobileEnemy(mobileEnemy);
    }
}

private IEnumerator InitHostilitySyncState()
{
    float timeout = 2f;
    float elapsed = 0f;
    while (elapsed < timeout)
    {
        EnemyMotor motor = GetComponent<EnemyMotor>();
        if (motor != null)
        {
            CurrentMotorIsHostile = motor.IsHostile;
            lastObservedIsHostile = motor.IsHostile;

            if (isServer)
            {
                SpawnedMotorIsHostile = motor.IsHostile;
                SyncedMotorIsHostile = motor.IsHostile;
                LastAppliedMotorIsHostile = motor.IsHostile;
                Debug.Log($"[SetupDemoEnemy][HostilitySync] Init on server enemy='{gameObject.name}' spawned={motor.IsHostile}");
            }
            else
            {
                ApplyHostilityStateLocally(SyncedMotorIsHostile, "init");
            }
            yield break;
        }

        yield return null;
        elapsed += Time.deltaTime;
    }
}

[Command(requiresAuthority = false)]
private void CmdReportHostilityState(bool isHostile, NetworkConnectionToClient sender = null)
{
    if (!isServer) return;

    // Any player can legitimately trigger language-based pacification when this enemy
    // attacks them, even when another client currently owns movement simulation.
    // Keep this authority-free and accept the reporting client just like the original
    // multiplayer behaviour. The server still applies the state before publishing it.
    if (sender == null)
    {
        Debug.LogWarning($"[SetupDemoEnemy][HostilitySync] Rejected report without sender enemy='{gameObject.name}' requested={isHostile}");
        return;
    }

    // Apply the accepted state to the server motor and its backing reaction before
    // publishing the SyncVar. Otherwise Update() sees the old server motor value and
    // immediately mirrors that stale value back over the owner's report.
    ApplyHostilityStateLocally(isHostile, "client-report-server", false);

    if (SyncedMotorIsHostile != isHostile)
    {
        Debug.Log($"[SetupDemoEnemy][HostilitySync] Client reported change enemy='{gameObject.name}' old={SyncedMotorIsHostile} new={isHostile} sender={sender.connectionId}");
        SyncedMotorIsHostile = isHostile;
    }
}

public void ApplyInitialSyncedHostility(bool isHostile)
{
    // The full settings RPC carries a spawn-time snapshot which can already be stale
    // if language pacification happened while settings were in flight. The live SyncVar
    // is canonical unless the movement owner already observed a newer local transition
    // before the settings payload rebuilt the enemy entity.
    bool syncedState = SyncedMotorIsHostile;
    bool stateToApply = hasPendingOwnerHostilityBeforeInitialSettings
        ? pendingOwnerHostilityBeforeInitialSettings
        : syncedState;

    SpawnedMotorIsHostile = syncedState;
    ApplyHostilityStateLocally(
        stateToApply,
        hasPendingOwnerHostilityBeforeInitialSettings ? "initial-preserved-owner-change" : "initial-syncvar");

    if (hasPendingOwnerHostilityBeforeInitialSettings && isClientOnly && hasAuthority)
    {
        hasPendingOwnerHostilityBeforeInitialSettings = false;
        CmdReportHostilityState(stateToApply);
    }

    if (isHostile != stateToApply)
        Debug.Log($"[SetupDemoEnemy][HostilitySync] Ignored stale initial RPC hostility enemy='{gameObject.name}' rpc={isHostile} applied={stateToApply} syncVar={syncedState}");
}

        DaggerfallEntityBehaviour entityBehaviour;
        private bool receivedInitialServerSettings = false;
        private bool clientVisualSettingsApplied = false;
        private bool hasPendingAuthoritativeSpawnHealth = false;
        private int pendingAuthoritativeSpawnHealth = 0;
        private Coroutine pendingAuthoritativeHealthCoroutine = null;

        // One-shot host-authoritative health repair.
        // This is intentionally not a continuous sync. The server sends current/max once after spawn,
        // and the client applies/queues it to repair build-only cases where local enemy setup starts at 0 HP.
        private bool serverSentOneShotSpawnHealthRepair = false;
        private bool hasPendingOneShotAuthoritativeHealthRepair = false;
        private int pendingOneShotAuthoritativeCurrentHealth = 0;
        private int pendingOneShotAuthoritativeMaxHealth = 0;
        private float pendingOneShotAuthoritativeHealthValidUntil = 0f;
        private Coroutine pendingOneShotAuthoritativeHealthCoroutine = null;

        public GameObject LightAura;

        void Awake()
        {
            // Must have an entity behaviour
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            if (!entityBehaviour)
                gameObject.AddComponent<DaggerfallEntityBehaviour>();
        }
		
		
		
				
[SyncVar(hook = nameof(OnDungeonEnemyChanged))]
public bool isDungeonEnemy = false;

        public bool HasReceivedInitialServerSettings()
        {
            return receivedInitialServerSettings;
        }

        public void MarkInitialServerSettingsApplied()
        {
            receivedInitialServerSettings = true;
            clientVisualSettingsApplied = true;
        }

        // Called immediately before the full settings payload recreates the local
        // EnemyEntity. This captures language/pacify changes that happened during
        // spawn setup so the older payload cannot erase them before they are reported.
        public void CaptureOwnerHostilityBeforeInitialSettings()
        {
            if (!isClientOnly || !hasAuthority || receivedInitialServerSettings)
                return;

            EnemyMotor motor = GetComponent<EnemyMotor>();
            if (motor == null)
                return;

            if (motor.IsHostile == lastObservedIsHostile)
            {
                hasPendingOwnerHostilityBeforeInitialSettings = false;
                return;
            }

            hasPendingOwnerHostilityBeforeInitialSettings = true;
            pendingOwnerHostilityBeforeInitialSettings = motor.IsHostile;
            Debug.Log($"[SetupDemoEnemy][HostilitySync] Preserved pre-settings owner change enemy='{gameObject.name}' old={lastObservedIsHostile} new={motor.IsHostile}");
        }

        public void SetPendingAuthoritativeSpawnHealth(int value)
        {
            if (value <= 0)
                return;

            hasPendingAuthoritativeSpawnHealth = true;
            pendingAuthoritativeSpawnHealth = value;
        }

        private void QueueAuthoritativeSpawnHealthApply(int value, string reason)
        {
            if (value <= 0)
                return;

            SetPendingAuthoritativeSpawnHealth(value);

            if (pendingAuthoritativeHealthCoroutine == null && isActiveAndEnabled)
                pendingAuthoritativeHealthCoroutine = StartCoroutine(ApplyPendingAuthoritativeHealthWhenReady(reason));
        }

        private IEnumerator ApplyPendingAuthoritativeHealthWhenReady(string reason)
        {
            float timeout = 20f;
            float elapsed = 0f;

            while (elapsed < timeout && hasPendingAuthoritativeSpawnHealth)
            {
                if (entityBehaviour == null)
                    entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

                if (entityBehaviour != null && entityBehaviour.Entity is EnemyEntity)
                {
                    int hp = pendingAuthoritativeSpawnHealth;
                    ApplySyncedSpawnHealthAsMax(hp, reason + "-queued");
                    hasPendingAuthoritativeSpawnHealth = false;
                    pendingAuthoritativeSpawnHealth = 0;
                    pendingAuthoritativeHealthCoroutine = null;
                    yield break;
                }

                yield return null;
                elapsed += Time.deltaTime;
            }

            if (hasPendingAuthoritativeSpawnHealth)
            {
                Debug.LogWarning($"[SpawnHealthDbg][PendingHealthTimeout] enemy='{gameObject.name}' reason={reason} hp={pendingAuthoritativeSpawnHealth} entityReady={(entityBehaviour != null && entityBehaviour.Entity is EnemyEntity)}");
            }

            pendingAuthoritativeHealthCoroutine = null;
        }

private void OnDungeonEnemyChanged(bool oldValue, bool newValue)
{
    Debug.Log($"[SetupDemoEnemy] isDungeonEnemy changed: {oldValue} → {newValue}");

  /*  if (newValue)
        StartCoroutine(DelayedMoveToDungeon());*/
}

/*private IEnumerator DelayedMoveToDungeon()
{
    yield return new WaitForSeconds(0.5f); // Short delay to ensure setup
    MoveToDungeon();
}*/



private IEnumerator ServerSendOneShotSpawnHealthRepair()
{
    if (!isServer || serverSentOneShotSpawnHealthRepair)
        yield break;

    serverSentOneShotSpawnHealthRepair = true;

    // Let RDB/dungeon enemy setup, parent moves, and normal spawn SyncVars/RPCs run first.
    yield return new WaitForSeconds(1.0f);

    float elapsed = 0f;
    const float timeout = 2.0f;

    while (elapsed < timeout)
    {
        DaggerfallEntityBehaviour serverEntityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
        if (serverEntityBehaviour != null && serverEntityBehaviour.Entity is EnemyEntity enemyEntity)
        {
            MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;
            int authoritativeMax = Mathf.Max(enemyEntity.MaxHealth, mobileEnemy.MaxHealth, enemyEntity.CurrentHealth, serverEntityBehaviour.currentHealth);
            int authoritativeCurrent = Mathf.Max(enemyEntity.CurrentHealth, serverEntityBehaviour.currentHealth);

            if (authoritativeMax > 0)
            {
                // If the server side somehow has a max but no current during the spawn window, prefer max.
                // In the reported bug the host sees the correct non-zero current, so this should normally send 16/16, 12/12, etc.
                if (authoritativeCurrent <= 0)
                    authoritativeCurrent = authoritativeMax;

                authoritativeCurrent = Mathf.Clamp(authoritativeCurrent, 0, authoritativeMax);

                Debug.Log($"[SpawnHealthDbg][ServerOneShotRepairSend] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={authoritativeCurrent} max={authoritativeMax} isDungeon={isDungeonEnemy}");
                RpcOneShotAuthoritativeHealthRepair(authoritativeCurrent, authoritativeMax);
                yield break;
            }
        }

        yield return new WaitForSeconds(0.1f);
        elapsed += 0.1f;
    }

    Debug.LogWarning($"[SpawnHealthDbg][ServerOneShotRepairNoEntity] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity missingNi) ? missingNi.netId.ToString() : "none")} could not read authoritative health.");
}

[ClientRpc]
public void RpcOneShotAuthoritativeHealthRepair(int authoritativeCurrent, int authoritativeMax)
{
    if (authoritativeMax <= 0)
        return;

    authoritativeMax = Mathf.Max(authoritativeMax, authoritativeCurrent, 1);
    authoritativeCurrent = Mathf.Clamp(authoritativeCurrent, 0, authoritativeMax);

    // Host already owns the authoritative value; this repair is for remote clients.
    if (isServer)
        return;

    hasPendingOneShotAuthoritativeHealthRepair = true;
    pendingOneShotAuthoritativeCurrentHealth = authoritativeCurrent;
    pendingOneShotAuthoritativeMaxHealth = authoritativeMax;
    pendingOneShotAuthoritativeHealthValidUntil = Time.time + 5.0f;

    Debug.Log($"[SpawnHealthDbg][ClientOneShotRepairReceived] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={authoritativeCurrent} max={authoritativeMax}");

    if (!TryApplyPendingOneShotAuthoritativeHealthRepair("rpc-received"))
    {
        if (pendingOneShotAuthoritativeHealthCoroutine == null && isActiveAndEnabled)
            pendingOneShotAuthoritativeHealthCoroutine = StartCoroutine(ApplyPendingOneShotAuthoritativeHealthRepairWhenReady());
    }
}

private bool TryApplyPendingOneShotAuthoritativeHealthRepair(string reason)
{
    if (!hasPendingOneShotAuthoritativeHealthRepair)
        return true;

    if (Time.time > pendingOneShotAuthoritativeHealthValidUntil)
    {
        Debug.LogWarning($"[SpawnHealthDbg][ClientOneShotRepairExpired] enemy='{gameObject.name}' reason={reason} cur={pendingOneShotAuthoritativeCurrentHealth} max={pendingOneShotAuthoritativeMaxHealth}");
        hasPendingOneShotAuthoritativeHealthRepair = false;
        pendingOneShotAuthoritativeCurrentHealth = 0;
        pendingOneShotAuthoritativeMaxHealth = 0;
        return true;
    }

    if (entityBehaviour == null)
        entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

    if (entityBehaviour == null || !(entityBehaviour.Entity is EnemyEntity))
    {
        Debug.Log($"[SpawnHealthDbg][ClientOneShotRepairDeferred] enemy='{gameObject.name}' reason={reason} cur={pendingOneShotAuthoritativeCurrentHealth} max={pendingOneShotAuthoritativeMaxHealth} entityReady=false");
        return false;
    }

    int current = pendingOneShotAuthoritativeCurrentHealth;
    int max = Mathf.Max(pendingOneShotAuthoritativeMaxHealth, current, 1);
    current = Mathf.Clamp(current, 0, max);

    entityBehaviour.ApplyAuthoritativeHealthCurrentAndMax(current, max);

    if (entityBehaviour.Entity is EnemyEntity enemyAfter)
    {
        MobileEnemy mobileAfter = enemyAfter.MobileEnemy;
        Debug.Log($"[SpawnHealthDbg][ClientOneShotRepairApplied] enemy='{gameObject.name}' reason={reason} cur={enemyAfter.CurrentHealth} min={mobileAfter.MinHealth} max={mobileAfter.MaxHealth}");
    }

    // Consume the network repair. This is intentionally one-shot, not a permanent health override.
    hasPendingOneShotAuthoritativeHealthRepair = false;
    pendingOneShotAuthoritativeCurrentHealth = 0;
    pendingOneShotAuthoritativeMaxHealth = 0;
    return true;
}

private IEnumerator ApplyPendingOneShotAuthoritativeHealthRepairWhenReady()
{
    while (hasPendingOneShotAuthoritativeHealthRepair && Time.time <= pendingOneShotAuthoritativeHealthValidUntil)
    {
        if (TryApplyPendingOneShotAuthoritativeHealthRepair("queued"))
        {
            pendingOneShotAuthoritativeHealthCoroutine = null;
            yield break;
        }

        yield return null;
    }

    if (hasPendingOneShotAuthoritativeHealthRepair)
        TryApplyPendingOneShotAuthoritativeHealthRepair("queued-timeout");

    pendingOneShotAuthoritativeHealthCoroutine = null;
}



public override void OnStartServer()
{
    base.OnStartServer();

    // Late-join fix:
    // If this enemy was spawned by normal DFU/GameObjectHelper code, its DaggerfallEntity/MobileUnit
    // can already be correct while the SetupDemoEnemy SyncVars are still prefab/default values.
    // Mirror only sends SyncVars to late joiners, not your original local setup calls, so copy the
    // authoritative EnemyEntity data into SyncVars before clients spawn this object.
    DaggerfallEntityBehaviour serverEntityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
    if (serverEntityBehaviour != null && serverEntityBehaviour.Entity is EnemyEntity enemyEntity)
    {
        MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;

        if (mobileEnemy.ID < 0 || mobileEnemy.ID >= EnemyBasics.Enemies.Length)
        {
            Debug.LogError($"[SetupDemoEnemy] ERROR: Invalid enemy ID {mobileEnemy.ID}!");
            return;
        }

        mobileEnemy.Team = EnemyBasics.Enemies[mobileEnemy.ID].Team;
        enemyEntity.SetMobileEnemy(mobileEnemy);

        EnemyType = (MobileTypes)mobileEnemy.ID;
        EnemyGender = mobileEnemy.Gender;
        EnemyReaction = mobileEnemy.Reactions;
        Team = mobileEnemy.Team;

        Debug.Log($"[SetupDemoEnemy] (Server) Published enemy SyncVars: Type={EnemyType}, Gender={EnemyGender}, Reaction={EnemyReaction}, Team={Team}, ID={mobileEnemy.ID}");
    }

    // Safety net for any server spawn path that did not manually capture HP before NetworkServer.Spawn().
    // For correct initial spawn payloads, prefer calling ServerCaptureAuthoritativeSpawnHealth() before spawn.
    ServerCaptureAuthoritativeSpawnHealth();

    StartCoroutine(InitHostilitySyncState());
    StartCoroutine(ServerSendOneShotSpawnHealthRepair());
}


		
		
public override void OnStartClient()
{
    base.OnStartClient();
    Debug.Log($"[OnStartClient] SetupDemoEnemy started on client: {gameObject.name}");

    if (TryGetComponent(out DaggerfallEntityBehaviour startEntityBehaviour) && startEntityBehaviour.Entity is EnemyEntity startEnemyEntity)
    {
        MobileEnemy startMobileEnemy = startEnemyEntity.MobileEnemy;
        Debug.Log($"[SpawnHealthDbg][ClientOnStart] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={startEnemyEntity.CurrentHealth} min={startMobileEnemy.MinHealth} max={startMobileEnemy.MaxHealth} gender={EnemyGender} reaction={EnemyReaction}");
    }

    // Apply host-authored spawn health immediately on clients.
    // This removes the local random max/current roll before the normal full settings RPC arrives.
    if (!isServer && SyncedSpawnHealth > 0)
        QueueAuthoritativeSpawnHealthApply(SyncedSpawnHealth, "OnStartClient");

    if (isServer)
        return;

    StartCoroutine(ApplySpawnSyncVarsAsVisualFallback());
    StartCoroutine(InitHostilitySyncState());
    StartCoroutine(RequestEnemySettingsWithRetry());
    StartCoroutine(WaitForEnemyEntity());

    // ✅ Move enemy under "Dungeon" parent if necessary
   /* if (isDungeonEnemy)
        MoveToDungeon();*/
}





public bool IsClientEnemyVisualReadyForAnimation()
{
    if (!clientVisualSettingsApplied)
        return false;

    MobileUnit mobile = GetMobileBillboardChild();
    if (mobile == null || !mobile.IsSetup)
        return false;

    return entityBehaviour != null && entityBehaviour.Entity is EnemyEntity;
}

private IEnumerator ApplySpawnSyncVarsAsVisualFallback()
{
    // This runs for late joiners before the full server settings RPC returns.
    // It prevents DaggerfallMobileUnit from staying in its prefab/default state
    // (e.g. Rat with zero frames) long enough to crash on attack animation RPCs.
    float timeout = 20.0f;
    float elapsed = 0f;

    while (elapsed < timeout && !clientVisualSettingsApplied)
    {
        if (DaggerfallUnity.Instance != null && GameManager.Instance != null && GetMobileBillboardChild() != null)
        {
            if (SyncedSpawnHealth > 0)
                SetPendingAuthoritativeSpawnHealth(SyncedSpawnHealth);

            ApplyEnemySettings(EnemyType, EnemyReaction, EnemyGender, ClassicSpawnDistanceType, AlliedToPlayer, Team);
            clientVisualSettingsApplied = true;

            if (SyncedSpawnHealth > 0)
                ApplySyncedSpawnHealthAsMax(SyncedSpawnHealth, "spawn-syncvar-visual-fallback");

            Debug.Log($"[SetupDemoEnemy][LateJoinVisualFallback] Applied spawn SyncVars locally: enemy='{gameObject.name}' type={EnemyType} reaction={EnemyReaction} gender={EnemyGender} team={Team}");
            yield break;
        }

        yield return null;
        elapsed += Time.deltaTime;
    }

    if (!clientVisualSettingsApplied)
        Debug.LogWarning($"[SetupDemoEnemy][LateJoinVisualFallback] Could not apply visual setup before timeout for enemy='{gameObject.name}' type={EnemyType}");
}

private PlayerMultiplayer FindLocalPlayerMultiplayerForCommand()
{
    if (PlayerMultiplayer.localPlayer != null && PlayerMultiplayer.localPlayer.isLocalPlayer)
        return PlayerMultiplayer.localPlayer;

    PlayerMultiplayer[] players = FindObjectsOfType<PlayerMultiplayer>();
    for (int i = 0; i < players.Length; i++)
    {
        if (players[i] != null && players[i].isLocalPlayer)
            return players[i];
    }

    return null;
}

private IEnumerator RequestEnemySettingsWithRetry()
{
    NetworkIdentity enemyNetIdentity = GetComponent<NetworkIdentity>();
    if (enemyNetIdentity == null)
    {
        Debug.LogError("[RequestEnemySettingsWithRetry] Enemy NetworkIdentity is NULL!");
        yield break;
    }

    PlayerMultiplayer playerMultiplayer = null;
    float waitForLocalPlayerTimeout = 20f;
    float elapsed = 0f;

    while (elapsed < waitForLocalPlayerTimeout)
    {
        playerMultiplayer = FindLocalPlayerMultiplayerForCommand();
        if (playerMultiplayer != null && NetworkClient.ready && enemyNetIdentity.netId != 0)
            break;

        yield return null;
        elapsed += Time.deltaTime;
    }

    if (playerMultiplayer == null || !playerMultiplayer.isLocalPlayer)
    {
        Debug.LogError($"[RequestEnemySettingsWithRetry] No local PlayerMultiplayer found for enemy='{gameObject.name}'. Cannot send Command.");
        yield break;
    }

    int attempts = 0;
    while (!receivedInitialServerSettings)
    {
        if (enemyNetIdentity == null || enemyNetIdentity.netId == 0)
        {
            yield return null;
            continue;
        }

        attempts++;
        Debug.Log($"[RequestEnemySettingsWithRetry] Requesting full settings for enemy='{gameObject.name}' netId={enemyNetIdentity.netId} (Attempt {attempts})...");
        playerMultiplayer.CmdRequestApplyEnemySettings(enemyNetIdentity.netId);

        float delay = attempts < 60 ? 0.25f : 1.0f;
        yield return new WaitForSeconds(delay);

        if (attempts == 60 && !receivedInitialServerSettings)
        {
            Debug.LogWarning($"[RequestEnemySettingsWithRetry] Still waiting for full settings for enemy='{gameObject.name}' netId={enemyNetIdentity.netId}. Continuing slow retry instead of giving up.");
        }
    }

    Debug.Log($"[RequestEnemySettingsWithRetry] Received full settings for enemy='{gameObject.name}'.");
}

private IEnumerator WaitForEnemyEntity()
{
    float waitTime = 0f;
    float maxWait = 1.5f; // Maximum time to wait for enemy setup

    while (waitTime < maxWait)
    {
        yield return new WaitForSeconds(0.2f); // Check every 0.2 seconds
        waitTime += 0.2f;

        DaggerfallEntityBehaviour entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
        if (entityBehaviour != null && entityBehaviour.Entity is EnemyEntity)
        {
            EnemyEntity enemyEntity = entityBehaviour.Entity as EnemyEntity;
            Debug.Log($"[SetupDemoEnemy] (Client) Enemy spawned with Team: {enemyEntity.Team}");
            yield break; // ✅ Exit loop if enemyEntity is found
        }
    }

    Debug.LogError("[SetupDemoEnemy] (Client) Failed to retrieve EnemyEntity after multiple attempts!");
}

private IEnumerator ApplySettingsWithDelayTeam(SetupDemoEnemy setupEnemy, MobileTeams team)
{
    yield break;
}


/*	
private void MoveToDungeon()
{
    GameObject dungeonParent = GameObject.Find("Dungeon");

    if (dungeonParent && dungeonParent.activeInHierarchy)
    {
        transform.SetParent(dungeonParent.transform);
        Debug.Log($"[SetupDemoEnemy] Moved {name} under 'Dungeon' on client.");
    }
    else
    {
        StartCoroutine(WaitForDungeon());
    }
}*/

private IEnumerator WaitForDungeon()
{
    GameObject dungeonParent;

    // Wait until the Dungeon GameObject is active
    while ((dungeonParent = GameObject.Find("Dungeon")) == null || !dungeonParent.activeInHierarchy)
    {
        Debug.Log($"[SetupDemoEnemy] Waiting for 'Dungeon' to become active...");
        yield return new WaitForSeconds(0.5f); // Check every 0.5 seconds
    }

    // Once active, move the enemy
    transform.SetParent(dungeonParent.transform);
    Debug.Log($"[SetupDemoEnemy] Moved {name} under 'Dungeon' after activation.");
}
	


		

       /* void Start()
        {
            // Disable this game object if missing mobile setup
            MobileUnit dfMobile = GetMobileBillboardChild();
            if (dfMobile == null)
                this.gameObject.SetActive(false);
            if (!dfMobile.IsSetup)
                this.gameObject.SetActive(false);
        }*/


        enum ControllerJustification
        {
            TOP,
            CENTER,
            BOTTOM
        }

        static void AdjustControllerHeight(CharacterController controller, float newHeight, ControllerJustification justification)
        {
            Vector3 newCenter = controller.center;
            switch (justification)
            {
                case ControllerJustification.TOP:
                    newCenter.y -= (newHeight - controller.height) / 2;
                    break;

                case ControllerJustification.BOTTOM:
                    newCenter.y += (newHeight - controller.height) / 2;
                    break;

                case ControllerJustification.CENTER:
                    // do nothing, centered is normal CharacterController behavior
                    break;
            }
            controller.height = newHeight;
            controller.center = newCenter;
        }


        private void Update()
        {
            EnemyMotor motor = GetComponent<EnemyMotor>();
            if (motor == null)
                return;

            CurrentMotorIsHostile = motor.IsHostile;

            if (isServer)
            {
                if (SyncedMotorIsHostile != motor.IsHostile)
                {
                    Debug.Log($"[SetupDemoEnemy][HostilitySync] Server mirror enemy='{gameObject.name}' old={SyncedMotorIsHostile} new={motor.IsHostile}");
                    ApplyBackingHostilityReaction(motor.IsHostile);
                    SyncedMotorIsHostile = motor.IsHostile;
                }

                lastObservedIsHostile = motor.IsHostile;
            }
            else if (isClientOnly)
            {
                if (suppressLocalHostilityReport)
                    return;

                if (!receivedInitialServerSettings)
                {
                    // Before full settings arrive, only the movement owner has a valid
                    // gameplay motor. Non-owner copies can still show prefab/default
                    // hostility here, so never publish those setup values.
                    if (hasAuthority)
                    {
                        hasPendingOwnerHostilityBeforeInitialSettings = motor.IsHostile != lastObservedIsHostile;
                        if (hasPendingOwnerHostilityBeforeInitialSettings)
                            pendingOwnerHostilityBeforeInitialSettings = motor.IsHostile;
                    }
                    return;
                }

                if (motor.IsHostile != lastObservedIsHostile)
                {
                    if (Time.time < nextAllowedHostilityReportTime)
                        return;

                    Debug.Log($"[SetupDemoEnemy][HostilitySync] Client local change enemy='{gameObject.name}' old={lastObservedIsHostile} new={motor.IsHostile}");
                    lastObservedIsHostile = motor.IsHostile;
                    CmdReportHostilityState(motor.IsHostile);
                }
            }
        }

        /// <summary>
        /// Sets up enemy based on current settings.
        /// </summary>
        public void ApplyEnemySettings(MobileGender gender)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            Dictionary<int, MobileEnemy> enemyDict = GameObjectHelper.EnemyDict;

            if (!enemyDict.TryGetValue((int)EnemyType, out MobileEnemy mobileEnemy))
                return;

            if (AlliedToPlayer)
                mobileEnemy.Team = MobileTeams.PlayerAlly;

            // Find mobile unit in children
            MobileUnit dfMobile = GetMobileBillboardChild();
            if (dfMobile != null)
            {
                // Setup mobile billboard
                Vector2 size = Vector2.one;
                mobileEnemy.Gender = gender;
                mobileEnemy.Reactions = EnemyReaction;
                dfMobile.SetEnemy(dfUnity, mobileEnemy, EnemyReaction, ClassicSpawnDistanceType);
                clientVisualSettingsApplied = true;

                // Setup controller
                CharacterController controller = GetComponent<CharacterController>();
                if (controller)
                {
                    // Set base height from sprite
                    size = dfMobile.GetSize();
                    controller.height = size.y;

                    // Reduce height of flying creatures as their wing animation makes them taller than desired
                    // This helps them get through doors while aiming for player eye height
                    if (dfMobile.Enemy.Behaviour == MobileBehaviour.Flying)
                        // (in frame 0 wings are in high position, assume body is  the lower half)
                        AdjustControllerHeight(controller, controller.height / 2, ControllerJustification.BOTTOM);

                    // Limit minimum controller height
                    // Stops very short characters like rats from being walked upon
                    if (controller.height < 1.6f)
                        AdjustControllerHeight(controller, 1.6f, ControllerJustification.BOTTOM);

                    controller.gameObject.layer = LayerMask.NameToLayer("Enemies");
                }

                // Setup sounds
                EnemySounds enemySounds = GetComponent<Game.EnemySounds>();
                if (enemySounds)
                {
                    enemySounds.MoveSound = (SoundClips)dfMobile.Enemy.MoveSound;
                    enemySounds.BarkSound = (SoundClips)dfMobile.Enemy.BarkSound;
                    enemySounds.AttackSound = (SoundClips)dfMobile.Enemy.AttackSound;
                }

                MeshRenderer meshRenderer = dfMobile.GetComponent<MeshRenderer>();
                if (meshRenderer)
                {
                    if (dfMobile.Enemy.Behaviour == MobileBehaviour.Spectral)
                    {
                        meshRenderer.material.shader = Shader.Find(MaterialReader._DaggerfallGhostShaderName);
                        meshRenderer.material.SetFloat("_Cutoff", 0.1f);
                    }
                    if (dfMobile.Enemy.NoShadow)
                    {
                        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                    if (dfMobile.Enemy.GlowColor != null)
                    {
                        meshRenderer.receiveShadows = false;
                        GameObject enemyLightGameObject = Instantiate(LightAura);
                        enemyLightGameObject.transform.parent = dfMobile.transform;
                        enemyLightGameObject.transform.localPosition = new Vector3(0, 0.3f, 0.2f);
                        Light enemyLight = enemyLightGameObject.GetComponent<Light>();
                        enemyLight.color = (Color)dfMobile.Enemy.GlowColor;
                        enemyLight.shadows = DaggerfallUnity.Settings.DungeonLightShadows ? LightShadows.Soft : LightShadows.None;
                    }
                }

                // Setup entity
                int effectiveSpawnScalingLevel = GetEffectiveSpawnScalingLevel();
                if (NetworkServer.active && SpawnScalingLevel <= 0 && effectiveSpawnScalingLevel > 0)
                    SpawnScalingLevel = effectiveSpawnScalingLevel;

                if (entityBehaviour)
                {
                    EnemyEntity entity = new EnemyEntity(entityBehaviour);
                    entityBehaviour.Entity = entity;

                    // Enemies are initially added to same world context as player
                    entity.WorldContext = GameManager.Instance.PlayerEnterExit.WorldContext;

                    int enemyIndex = (int)EnemyType;
                    if (enemyIndex >= 0 && enemyIndex <= 42)
                    {
                        entityBehaviour.EntityType = EntityTypes.EnemyMonster;
                        entity.SetEnemyCareer(mobileEnemy, entityBehaviour.EntityType, effectiveSpawnScalingLevel);
                    }
                    else if (enemyIndex >= 128 && enemyIndex <= 146)
                    {
                        entityBehaviour.EntityType = EntityTypes.EnemyClass;
                        entity.SetEnemyCareer(mobileEnemy, entityBehaviour.EntityType, effectiveSpawnScalingLevel);
                    }
                    else if (DaggerfallEntity.GetCustomCareerTemplate(enemyIndex) != null)
                    {
                        if (DaggerfallEntity.IsClassEnemyId(enemyIndex))
                        {
                            entityBehaviour.EntityType = EntityTypes.EnemyClass;
                        }
                        else
                        {
                            entityBehaviour.EntityType = EntityTypes.EnemyMonster;
                        }
                        entity.SetEnemyCareer(mobileEnemy, entityBehaviour.EntityType, effectiveSpawnScalingLevel);
                    }
                    else
                    {
                        entityBehaviour.EntityType = EntityTypes.None;
                    }
                }


                if (entityBehaviour != null && entityBehaviour.Entity is EnemyEntity appliedEnemy && hasPendingAuthoritativeSpawnHealth)
                {
                    MobileEnemy authoritativeMobile = appliedEnemy.MobileEnemy;
                    authoritativeMobile.MaxHealth = pendingAuthoritativeSpawnHealth;
                    if (authoritativeMobile.MinHealth > authoritativeMobile.MaxHealth)
                        authoritativeMobile.MinHealth = authoritativeMobile.MaxHealth;
                    appliedEnemy.SetMobileEnemy(authoritativeMobile);
                    appliedEnemy.MaxHealth = pendingAuthoritativeSpawnHealth;
                    appliedEnemy.CurrentHealth = pendingAuthoritativeSpawnHealth;
                    entityBehaviour.ApplyAuthoritativeSpawnHealthAndMax(pendingAuthoritativeSpawnHealth);
                    hasPendingAuthoritativeSpawnHealth = false;
                    pendingAuthoritativeSpawnHealth = 0;
                }

                if (entityBehaviour != null && entityBehaviour.Entity is EnemyEntity appliedEnemyEntity)
                {
                    MobileEnemy appliedMobileEnemy = appliedEnemyEntity.MobileEnemy;
                    Debug.Log($"[SpawnHealthDbg][ApplyEnemySettings] enemy='{gameObject.name}' cur={appliedEnemyEntity.CurrentHealth} min={appliedMobileEnemy.MinHealth} max={appliedMobileEnemy.MaxHealth} gender={EnemyGender} reaction={EnemyReaction}");
                }

                // If the one-shot repair arrived before this ApplyEnemySettings call recreated Entity,
                // consume it here so late visual/entity setup cannot leave the client at 0/max HP.
                if (hasPendingOneShotAuthoritativeHealthRepair)
                    TryApplyPendingOneShotAuthoritativeHealthRepair("after-apply-settings");

                // Add special behaviour for Daedra Seducer mobiles
                if (dfMobile.Enemy.ID == (int)MobileTypes.DaedraSeducer)
                {
                    dfMobile.gameObject.AddComponent<DaedraSeducerMobileBehaviour>();
                }
            }
        }

        /// <summary>
        /// Change enemy settings and configure in a single call.
        /// </summary>
        /// <param name="enemyType">Enemy type.</param>
public void ApplyEnemySettings(MobileTypes enemyType, MobileReactions enemyReaction, MobileGender gender, byte classicSpawnDistanceType = 0, bool alliedToPlayer = false, MobileTeams team = MobileTeams.CityWatch, int spawnScalingLevel = 0)
{
    if (spawnScalingLevel > 0)
        SpawnScalingLevel = Mathf.Clamp(spawnScalingLevel, 1, 100);

    EnemyType = enemyType;
    EnemyReaction = enemyReaction;
    EnemyGender = gender;
    ClassicSpawnDistanceType = classicSpawnDistanceType;
    AlliedToPlayer = alliedToPlayer;

    // ✅ Apply the Team properly
    Team = team;

    Debug.Log($"[ApplyEnemySettings] Applied settings: Type={enemyType}, Reaction={enemyReaction}, Gender={gender}, AlliedToPlayer={alliedToPlayer}, Team={team}, SpawnScalingLevel={SpawnScalingLevel}");

    ApplyEnemySettings(gender);
}

        /// <summary>
        /// Change enemy settings and configure in a single call.
        /// </summary>
        public void ApplyEnemySettings(EntityTypes entityType, int careerIndex, MobileGender gender, bool isHostile = true, bool alliedToPlayer = false)
        {
            // Get mobile type based on entity type and career index
            MobileTypes mobileType;

            // For classic enemies, careerIndex is equal to enemyId for monsters, or enemyId - 128 for class enemies (ex: Mage, enemyId=128, careerIndex=0)
            // For custom enemies, we just always store the enemyId in careerIndex, even if class type
            if (careerIndex < 256)
            {                
                if (entityType == EntityTypes.EnemyMonster)
                    mobileType = (MobileTypes)careerIndex;
                else if (entityType == EntityTypes.EnemyClass)
                    mobileType = (MobileTypes)(careerIndex + 128);
                else
                    return;
            }
            else
            {
                mobileType = (MobileTypes)careerIndex;
            }

            MobileReactions enemyReaction = (isHostile) ? MobileReactions.Hostile : MobileReactions.Passive;
            MobileGender enemyGender = gender;

            ApplyEnemySettings(mobileType, enemyReaction, enemyGender, alliedToPlayer: alliedToPlayer);
        }

        public void AlignToGround()
        {
            CharacterController controller = GetComponent<CharacterController>();
            if (controller != null)
                GameObjectHelper.AlignControllerToGround(controller);
        }

        /// <summary>
        /// Finds mobile billboard or custom implementation in children.
        /// </summary>
        /// <returns>Mobile Unit component.</returns>
        public MobileUnit GetMobileBillboardChild()
        {
#if UNITY_EDITOR
            // Get component from prefab in edit mode
            if (!Application.isPlaying)
                return GetComponentInChildren<DaggerfallMobileUnit>();
#endif

            // Get default or custom implementation
            return GetComponent<DaggerfallEnemy>().MobileUnit;
        }
    }
}
