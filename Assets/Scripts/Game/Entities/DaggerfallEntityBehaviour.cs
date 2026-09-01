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
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Questing;
using Mirror;
using System.Collections;

namespace DaggerfallWorkshop.Game.Entity
{
    /// <summary>
    /// Hosts DaggerfallEntity for living GameObjects.
    /// </summary>
    public class DaggerfallEntityBehaviour : NetworkBehaviour
    {
        #region Fields

        public EntityTypes EntityType = EntityTypes.None;
        EntityTypes lastEntityType = EntityTypes.None;
        DaggerfallEntity entity = null;
        DaggerfallLoot corpseLootContainer = null;

        // Multiplayer death cleanup guard.
        // In MP, death can be reached from SetHealth(), ReduceHealth(), Cmd_UpdateHealth(),
        // and Rpc_UpdateHealth(). Keep the normal DFU death/corpse event, but only run it once
        // per local object and only destroy the server network object once.
        bool multiplayerDeathEventRaised = false;
        bool multiplayerNetworkDestroyIssued = false;
        bool multiplayerNetworkDestroyScheduled = false;

        #endregion

        #region Properties

        /// <summary>
        /// Gets entity as PlayerEntity.
        /// </summary>
        public DaggerfallEntity Entity
        {
            get { return entity; }
            set { SetEntityValue(value); }
        }


    // Expose CurrentHealth in Inspector (Read-Only)
    [SerializeField] public int currentHealth;


        /// <summary>
        /// Gets or sets reference to loot container spawned at time of entity death.
        /// </summary>
        public DaggerfallLoot CorpseLootContainer
        {
            get { return corpseLootContainer; }
            set { corpseLootContainer = value; }
        }

        #endregion

        #region Unity

        private void Awake()
        {
            SetEntityType(EntityType);
        }

        void FixedUpdate()
        {
            Entity.FixedUpdate();
        }
		
		
		
[Command(requiresAuthority = false)]
public void Cmd_UpdateHealth(int newHealth, uint enemyNetId)
{
    if (!NetworkIdentity.spawned.ContainsKey(enemyNetId))
        return;

    GameObject enemy = NetworkIdentity.spawned[enemyNetId].gameObject;
    DaggerfallEntityBehaviour enemyBehaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
    if (enemyBehaviour == null || enemyBehaviour.Entity == null)
        return;

    int hostHealth = enemyBehaviour.Entity.CurrentHealth;
    int maxHealth = Mathf.Max(1, enemyBehaviour.Entity.MaxHealth);
    int acceptedHealth = Mathf.Clamp(newHealth, 0, maxHealth);

    // Client damage reports are absolute health values. Never let a stale/older client
    // report raise enemy HP on the host, and never let a positive report revive an enemy
    // whose death has already been accepted/scheduled. This keeps fast client attacks from
    // being overwritten by late higher-health packets.
    if ((hostHealth <= 0 || enemyBehaviour.multiplayerDeathEventRaised) && acceptedHealth > 0)
    {
        Debug.Log($"[HealthSync][ServerIgnoredReviveReport] enemy='{enemy.name}' netId={enemyNetId} host={hostHealth} reported={acceptedHealth}");
        return;
    }

    if (acceptedHealth > hostHealth)
    {
        Debug.Log($"[HealthSync][ServerIgnoredStaleHigherHealth] enemy='{enemy.name}' netId={enemyNetId} host={hostHealth} reported={acceptedHealth}");
        return;
    }

    // Update health on the host side. ApplyHealthLocally() can already route death
    // through CheckMulti(), so the explicit death call below is guarded.
    enemyBehaviour.ApplyHealthLocally(acceptedHealth);

    // Propagate the accepted host value to clients before the server destroys the NetworkIdentity.
    Rpc_UpdateHealth(acceptedHealth, enemyNetId);

    if (acceptedHealth <= 0)
        enemyBehaviour.SomethingMultiDeath();
}

[ClientRpc]
public void Rpc_UpdateHealth(int newHealth, uint enemyNetId)
{
    if (NetworkIdentity.spawned.ContainsKey(enemyNetId))
    {
        GameObject enemy = NetworkIdentity.spawned[enemyNetId].gameObject;
        DaggerfallEntityBehaviour enemyBehaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
        if (enemyBehaviour != null)
        {
            // Update health on the client side. ApplyHealthLocally() can already route death
            // through CheckMulti(), so the explicit death call below is guarded.
            enemyBehaviour.ApplyHealthLocally(newHealth, suppressClientOutgoing: true);

            if (newHealth <= 0)
            {
                // On pure clients, raise the normal DFU death/corpse event immediately inside
                // the death RPC. Otherwise NetworkServer.Destroy can arrive before the old
                // delayed coroutine fires, and the client may lose its local corpse marker.
                // Host/server keeps the existing delayed path.
                if (enemyBehaviour.isClientOnly)
                    enemyBehaviour.RaiseDeathEventOnceAndDestroyNetworkEnemy();
                else
                    enemyBehaviour.SomethingMultiDeath();
            }
        }
    }
}

public int ReduceHealth(int amount)
{
    int baseHealth = Entity != null ? Entity.CurrentHealth : currentHealth;

    // Build clients can briefly see networked enemies before the authoritative setup RPC has completed.
    // Never let that half-initialized local entity publish a fake 0 HP / death back to the host.
    SetupDemoEnemy setupDemoEnemy = GetComponent<SetupDemoEnemy>();
    if (isClientOnly && setupDemoEnemy != null && !setupDemoEnemy.HasReceivedInitialServerSettings())
    {
        Debug.LogWarning(
            $"[HealthSync][BlockedDamageBeforeServerSettings] enemy='{gameObject.name}' " +
            $"baseHealth={baseHealth} amount={amount} syncedSpawnHealth={setupDemoEnemy.SyncedSpawnHealth}");
        return currentHealth;
    }

    Debug.Log($"ReduceHealth called. Amount: {amount}, Current Health: {baseHealth}");

    int targetHealth = baseHealth - amount;
    if (targetHealth <= 0)
    {
        targetHealth = 0;
        SomethingMultiDeath();
    }

    if (isServer)
    {
        ApplyHealthLocally(targetHealth);
    }
    else if (isClientOnly)
    {
        Cmd_UpdateHealth(targetHealth, (uint)GetComponent<NetworkIdentity>().netId);
    }

    return currentHealth;
}

public void CheckMulti()
{
    if (NetworkClient.active || NetworkServer.active)
    {
        // Multiplayer keeps the existing 0.1s death delay, but the actual network enemy
        // will be destroyed by the server after the normal DFU death/corpse event runs.
        SomethingMultiDeath();
    }
    else // Single-player mode
    {
        Entity.RaiseOnDeathEvent(); // Directly trigger the death event in single-player
    }
}

public void SomethingMultiDeath()
{
    // Death can be scheduled from several paths in MP. Only schedule it once per local object.
    if (multiplayerDeathEventRaised)
        return;

    StartCoroutine(DelayedDeathEvent());
}

private IEnumerator DelayedDeathEvent()
{
    yield return new WaitForSeconds(0.1f);  // Keep the existing MP death delay.

    RaiseDeathEventOnceAndDestroyNetworkEnemy();
}

private void RaiseDeathEventOnceAndDestroyNetworkEnemy()
{
    if (multiplayerDeathEventRaised)
        return;

    multiplayerDeathEventRaised = true;

    // Keep the normal DFU death path untouched. This is what creates the corpse marker,
    // loot, quest kill tracking, death sounds, etc.
    if (Entity != null)
        Entity.RaiseOnDeathEvent();

    // Only the server removes the actual network enemy. Do not SetActive(false) as the final
    // state for MP enemies, because these enemies live at scene root and are not cleaned up
    // by the normal dungeon/interior/exterior parent unload path.
    TryDestroyNetworkEnemyAfterDeathEvent();
}

private void TryDestroyNetworkEnemyAfterDeathEvent()
{
    if (multiplayerNetworkDestroyIssued || multiplayerNetworkDestroyScheduled)
        return;

    if (!NetworkServer.active)
        return;

    NetworkIdentity networkIdentity = GetComponent<NetworkIdentity>();
    if (networkIdentity == null)
        return;

    // Do not destroy non-enemy network objects through this path.
    if (GetComponent<SetupDemoEnemy>() == null &&
        EntityType != EntityTypes.EnemyClass &&
        EntityType != EntityTypes.EnemyMonster)
        return;

    multiplayerNetworkDestroyScheduled = true;

    // Do not call NetworkServer.Destroy() in the exact same call stack as RaiseOnDeathEvent().
    // DFU corpse creation can be finalized by death handlers/coroutines after the event is raised.
    // If the network enemy is destroyed immediately, the enemy disappears but the corpse marker can be lost.
    // Run the destroy coroutine from GameManager so it survives even if the enemy object is disabled by normal death logic.
    if (GameManager.Instance != null)
        GameManager.Instance.StartCoroutine(DestroyNetworkEnemyAfterCorpseGrace(gameObject, networkIdentity.netId));
    else
        StartCoroutine(DestroyNetworkEnemyAfterCorpseGrace(gameObject, networkIdentity.netId));
}

private static IEnumerator DestroyNetworkEnemyAfterCorpseGrace(GameObject enemyObject, uint netId)
{
    // Minimum one frame delay so death handlers get a chance to finish their corpse work.
    yield return null;

    float startTime = Time.time;
    const float maxWait = 0.5f;

    while (enemyObject != null && Time.time - startTime < maxWait)
    {
        DaggerfallEntityBehaviour entityBehaviour = enemyObject.GetComponent<DaggerfallEntityBehaviour>();

        // If the normal DFU death path has already assigned a corpse container, we can destroy now.
        if (entityBehaviour != null && entityBehaviour.CorpseLootContainer != null)
            break;

        yield return null;
    }

    if (enemyObject == null)
        yield break;

    if (!NetworkServer.active)
        yield break;

    NetworkIdentity networkIdentity = enemyObject.GetComponent<NetworkIdentity>();
    if (networkIdentity == null)
        yield break;

    DaggerfallEntityBehaviour behaviour = enemyObject.GetComponent<DaggerfallEntityBehaviour>();
    if (behaviour != null)
    {
        // Publish authoritative initial corpse contents before destroying the network enemy.
        // Local corpse markers are registered by GameObjectHelper using this enemy's netId,
        // so no position matching is needed.
        if (behaviour.CorpseLootContainer != null)
            global::LootCatcher.ServerPublishCorpseLootFromEnemy(enemyObject, behaviour.CorpseLootContainer);

        behaviour.multiplayerNetworkDestroyIssued = true;
    }

    Debug.Log($"[EnemyNetworkDestroy] Destroying dead network enemy '{enemyObject.name}' netId={networkIdentity.netId} after corpse grace. corpse={(behaviour != null && behaviour.CorpseLootContainer != null)}");
    NetworkServer.Destroy(enemyObject);
}

		
		
private int lastHealth = 0; // Properly initialize to match actual health
private float suppressClientHealthSyncUntil = -1f;

// Client-only: if a local hit happens inside the short RPC echo-suppression window,
// keep the lowest reported HP and send it as soon as the window ends instead of
// silently consuming the health change by advancing lastHealth. Death bypasses the
// window and is sent immediately.
private bool hasPendingClientHealthReport = false;
private int pendingClientHealthReport = 0;

private string HealthSyncSide
{
    get
    {
        if (isServer) return "host";
        if (isClientOnly) return "client";
        return "single";
    }
}

private void ApplyHealthLocally(int newHealth, bool suppressClientOutgoing = false)
{
    if (Entity == null) return;

    Entity.SetHealth(newHealth);
    currentHealth = newHealth;
    lastHealth = newHealth;

    if (suppressClientOutgoing)
    {
        suppressClientHealthSyncUntil = Time.time + 0.35f;

        // If the host already accepted the same/lower health than our queued client
        // report, the pending report is obsolete. If the host value is still higher,
        // keep the pending lower value so it can be sent after suppression ends.
        if (isClientOnly && hasPendingClientHealthReport && newHealth <= pendingClientHealthReport)
            ClearPendingClientHealthReport();
    }
}

private void QueuePendingClientHealthReport(int newHealth, string reason)
{
    newHealth = Mathf.Max(0, newHealth);

    if (!hasPendingClientHealthReport || newHealth < pendingClientHealthReport)
    {
        pendingClientHealthReport = newHealth;
        hasPendingClientHealthReport = true;
    }

    Debug.Log($"[HealthSync][ClientQueuedDuringSuppression] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} queued={pendingClientHealthReport} reason={reason} until={suppressClientHealthSyncUntil:F2} now={Time.time:F2}");
}

private void ClearPendingClientHealthReport()
{
    hasPendingClientHealthReport = false;
    pendingClientHealthReport = 0;
}

private bool TrySendClientHealthReport(int reportHealth, string reason)
{
    if (!isClientOnly)
        return false;

    NetworkIdentity ni = GetComponent<NetworkIdentity>();
    if (ni == null)
        return false;

    reportHealth = Mathf.Max(0, reportHealth);
    Debug.Log($"[HealthSync][ClientSend] enemy='{gameObject.name}' netId={ni.netId} hp={reportHealth} reason={reason}");
    Cmd_UpdateHealth(reportHealth, (uint)ni.netId);
    return true;
}

public void ApplyAuthoritativeSpawnHealth(int newHealth)
{
    ApplyHealthLocally(newHealth, suppressClientOutgoing: true);
    Debug.Log($"[SpawnHealthDbg][Baseline] side={HealthSyncSide} enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={currentHealth}");
}

public void ApplyAuthoritativeSpawnHealthAndMax(int newHealth)
{
    if (Entity == null) return;

    if (Entity is EnemyEntity enemyEntity)
    {
        MobileEnemy me = enemyEntity.MobileEnemy;
        me.MaxHealth = newHealth;
        if (me.MinHealth > me.MaxHealth)
            me.MinHealth = me.MaxHealth;
        enemyEntity.SetMobileEnemy(me);
        enemyEntity.MaxHealth = newHealth;
        enemyEntity.CurrentHealth = newHealth;
    }

    ApplyHealthLocally(newHealth, suppressClientOutgoing: true);
    Debug.Log($"[SpawnHealthDbg][BaselineMax] side={HealthSyncSide} enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={currentHealth}");
}

public void ApplyAuthoritativeHealthCurrentAndMax(int authoritativeCurrent, int authoritativeMax)
{
    if (Entity == null) return;

    authoritativeMax = Mathf.Max(authoritativeMax, authoritativeCurrent, 1);
    authoritativeCurrent = Mathf.Clamp(authoritativeCurrent, 0, authoritativeMax);

    if (Entity is EnemyEntity enemyEntity)
    {
        MobileEnemy me = enemyEntity.MobileEnemy;
        me.MaxHealth = authoritativeMax;
        if (me.MinHealth > me.MaxHealth)
            me.MinHealth = me.MaxHealth;
        enemyEntity.SetMobileEnemy(me);
        enemyEntity.MaxHealth = authoritativeMax;
        enemyEntity.CurrentHealth = authoritativeCurrent;
    }

    ApplyHealthLocally(authoritativeCurrent, suppressClientOutgoing: true);
    Debug.Log($"[SpawnHealthDbg][BaselineCurrentMax] side={HealthSyncSide} enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity ni) ? ni.netId.ToString() : "none")} cur={currentHealth} max={authoritativeMax}");
}

void Start()
{
    if (Entity != null)
    {
        currentHealth = Entity.CurrentHealth;
        lastHealth = Entity.CurrentHealth;
    }
}
		
		
		

        void Update()
        {
            // Change entity type
            if (EntityType != lastEntityType)
            {
                SetEntityType(EntityType);
                lastEntityType = EntityType;
            }

            // Exit when no entity set
            if (Entity == null)
                return;

            // Update entity
            Entity.Update(this);
			
			
			 // Check if the DaggerfallEntityBehaviour has the SetupDemoEnemy script
    SetupDemoEnemy setupDemoEnemy = GetComponent<SetupDemoEnemy>();
    if (setupDemoEnemy == null)
        return; // Exit if it's not an enemy with SetupDemoEnemy script

    // Get the latest health value
    int newHealth = Entity.CurrentHealth;

    // Update currentHealth so it shows up in the editor
    currentHealth = newHealth;

    // If a client hit was queued during the RPC suppression window, flush it as soon
    // as suppression expires even if lastHealth was already advanced locally. This is
    // the important part that prevents fast client attacks from being swallowed.
    if (isClientOnly && hasPendingClientHealthReport && Time.time >= suppressClientHealthSyncUntil)
    {
        TrySendClientHealthReport(pendingClientHealthReport, "flush-pending-after-suppression");
        ClearPendingClientHealthReport();
    }

    // Sync only if health has changed
    if (newHealth != lastHealth)
    {
        Debug.Log($"Enemy Health Changed [{HealthSyncSide}] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity healthNi) ? healthNi.netId.ToString() : "none")}: {lastHealth} → {newHealth}");

        if (isServer)
        {
            Rpc_UpdateHealth(newHealth, (uint)GetComponent<NetworkIdentity>().netId);
        }
        else if (isClientOnly)
        {
            // Do not let a locally half-initialized enemy report 0 HP to the host.
            // This is especially important for build clients, where spawn/SyncVar/RPC timing can differ from the Editor.
            if (!setupDemoEnemy.HasReceivedInitialServerSettings())
            {
                Debug.LogWarning(
                    $"[HealthSync][BlockedBeforeServerSettings] enemy='{gameObject.name}' " +
                    $"netId={(TryGetComponent(out NetworkIdentity preInitNi) ? preInitNi.netId.ToString() : "none")} " +
                    $"localHealth={newHealth} syncedSpawnHealth={setupDemoEnemy.SyncedSpawnHealth}");

                lastHealth = newHealth;
                return;
            }

            // Death should never be delayed by the echo-suppression window.
            if (newHealth <= 0)
            {
                TrySendClientHealthReport(0, "death-immediate");
                ClearPendingClientHealthReport();
            }
            else if (Time.time < suppressClientHealthSyncUntil)
            {
                // The old code only logged this and then advanced lastHealth, which could
                // permanently swallow rapid client-side damage. Queue only downward HP
                // changes; upward changes during suppression are usually host echoes/repairs.
                if (newHealth < lastHealth)
                    QueuePendingClientHealthReport(newHealth, "local-damage-inside-suppression");
                else
                    Debug.Log($"[SpawnHealthDbg][ClientHealthSyncSuppressedNonDamage] enemy='{gameObject.name}' netId={(TryGetComponent(out NetworkIdentity suppressNi) ? suppressNi.netId.ToString() : "none")} cur={newHealth} last={lastHealth} until={suppressClientHealthSyncUntil:F2} now={Time.time:F2}");
            }
            else
            {
                TrySendClientHealthReport(newHealth, "health-changed");
            }
        }

        lastHealth = newHealth;
    }
			
			
        }
		
		
		
// Directly apply the change
public void SyncHealth(int newHealth)
{
    ApplyHealthLocally(newHealth);
}
		
		

        #endregion

        #region Special Damage Methods

        /// <summary>
        /// Cause fatigue damage to entity with additional logic.
        /// </summary>
        /// <param name="sourceEffect">Source effect.</param>
        /// <param name="amount">Amount to damage fatigue.</param>
        /// <param name="assignMultiplier">Optionally assign fatigue multiplier.</param>
        public void DamageFatigueFromSource(IEntityEffect sourceEffect, int amount, bool assignMultiplier = false)
        {
            // Skip fatigue damage from effects if this is a non-hostile enemy
            // This is a hack to support N0B00Y08 otherwise warrior will aggro if player casts Sleep on them
            // Warrior does not aggro in classic and it seems impossible to cast this class of spell on non-hostiles in classic
            // Would prefer a better system such as a quest action to whitelist certain spells on a Foe resource
            // But this will get job done in this case and we can expand/improve later
            if (!IsHostileEnemy() && !(Entity is PlayerEntity))
                return;

            DamageFatigueFromSource(sourceEffect.Caster, amount, assignMultiplier);
        }

        /// <summary>
        /// Check if this entity is a hostile enemy.
        /// Currently only used to block damage and aggro from Sleep spell in N0B00Y08.
        /// </summary>
        /// <returns>True if this entity is a hostile enemy.</returns>
        bool IsHostileEnemy()
        {
            EnemyMotor enemyMotor = transform.GetComponent<EnemyMotor>();
            return enemyMotor && enemyMotor.IsHostile;
        }

        /// <summary>
        /// Cause damage to entity health with additional logic.
        /// </summary>
        /// <param name="sourceEffect">Source effect.</param>
        /// <param name="amount">Amount to damage health.</param>
        /// <param name="showBlood">Show blood splash.</param>
        /// <param name="bloodPosition">Blood splash position.</param>
        public void DamageHealthFromSource(IEntityEffect sourceEffect, int amount, bool showBlood, Vector3 bloodPosition)
        {
            DamageHealthFromSource(sourceEffect.Caster, amount, showBlood, bloodPosition);
        }

        /// <summary>
        /// Cause spell point damage to entity with additional logic.
        /// </summary>
        /// <param name="sourceEffect">Source effect.</param>
        /// <param name="amount">Amount to damage spell points.</param>
        public void DamageMagickaFromSource(IEntityEffect sourceEffect, int amount)
        {
            DamageMagickaFromSource(sourceEffect.Caster, amount);
        }

        /// <summary>
        /// Cause fatigue damage to entity with additional logic.
        /// </summary>
        /// <param name="sourceEntityBehaviour">Source entity behaviour.</param>
        /// <param name="amount">Amount to damage fatigue.</param>
        /// <param name="assignMultiplier">Optionally assign fatigue multiplier.</param>
        public void DamageFatigueFromSource(DaggerfallEntityBehaviour sourceEntityBehaviour, int amount, bool assignMultiplier = false)
        {
            // Remove fatigue amount
            Entity.DecreaseFatigue(amount, assignMultiplier);

            // Post-attack logic on source
            HandleAttackFromSource(sourceEntityBehaviour);
        }

        /// <summary>
        /// Cause damage to entity health with additional logic.
        /// </summary>
        /// <param name="sourceEntityBehaviour">Source entity behaviour.</param>
        /// <param name="amount">Amount to damage health.</param>
        /// <param name="showBlood">Show blood splash.</param>
        /// <param name="bloodPosition">Blood splash position.</param>
        public void DamageHealthFromSource(DaggerfallEntityBehaviour sourceEntityBehaviour, int amount, bool showBlood, Vector3 bloodPosition)
        {
            // Remove health amount
            Entity.DecreaseHealth(amount);

            // Post-attack logic on source
            HandleAttackFromSource(sourceEntityBehaviour);

            // Show blood
            if (showBlood)
            {
                EnemyBlood blood = transform.GetComponent<EnemyBlood>();
                if (blood)
                    blood.ShowBloodSplash(0, bloodPosition);
            }
        }

        /// <summary>
        /// Cause spell point damage to entity with additional logic.
        /// </summary>
        /// <param name="sourceEntityBehaviour">Source entity behaviour.</param>
        /// <param name="amount">Amount to damage spell points.</param>
        public void DamageMagickaFromSource(DaggerfallEntityBehaviour sourceEntityBehaviour, int amount)
        {
            // Remove fatigue amount
            Entity.DecreaseMagicka(amount);

            // Post-attack logic on source
            HandleAttackFromSource(sourceEntityBehaviour);
        }

        /// <summary>
        /// Handle shared logic when player attacks entity.
        /// </summary>
        public void HandleAttackFromSource(DaggerfallEntityBehaviour sourceEntityBehaviour)
        {
            // Break "normal power" concealment effects on source
            if (sourceEntityBehaviour && sourceEntityBehaviour.Entity.IsMagicallyConcealedNormalPower)
                EntityEffectManager.BreakNormalPowerConcealmentEffects(sourceEntityBehaviour);

            // When source is player
            if (sourceEntityBehaviour == GameManager.Instance.PlayerEntityBehaviour)
            {
                PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                // Handle civilian NPC crime reporting
                if (EntityType == EntityTypes.CivilianNPC)
                {
                    MobilePersonNPC mobileNpc = transform.GetComponent<MobilePersonNPC>();
                    if (mobileNpc)
                    {
                        // Handle assault or murder
                        if (Entity.CurrentHealth > 0)
                        {
                            playerEntity.CrimeCommitted = PlayerEntity.Crimes.Assault;
                            playerEntity.SpawnCityGuards(true);
                        }
                        else
                        {
                            if (!mobileNpc.IsGuard)
                            {
                                playerEntity.TallyCrimeGuildRequirements(false, 5);
                                playerEntity.CrimeCommitted = PlayerEntity.Crimes.Murder;
                                playerEntity.SpawnCityGuards(true);
                            }
                            else
                            {
                                playerEntity.CrimeCommitted = PlayerEntity.Crimes.Assault;
                                playerEntity.SpawnCityGuard(mobileNpc.transform.position, mobileNpc.transform.forward);
                            }

                            // Disable when dead
                            mobileNpc.Motor.gameObject.SetActive(false);
                        }
                    }
                }

                // Handle equipped Azura's Star trapping slain enemy monsters
                // This is always successful if Azura's Star is empty and equipped
                if (EntityType == EntityTypes.EnemyMonster && playerEntity.IsAzurasStarEquipped && entity.CurrentHealth <= 0)
                {
                    EnemyEntity enemyEntity = entity as EnemyEntity;
                    if (SoulTrap.FillEmptyTrapItem((MobileTypes)enemyEntity.MobileEnemy.ID, true))
                    {
                        DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("trapSuccess"), 1.5f);
                    }
                }

                // Handle mobile enemy aggro
                if (EntityType == EntityTypes.EnemyClass || EntityType == EntityTypes.EnemyMonster)
                {
                    // Make enemy aggressive to player
                    EnemyMotor enemyMotor = transform.GetComponent<EnemyMotor>();
                    if (enemyMotor)
                    {
                        if (!enemyMotor.IsHostile)
                        {
                            GameManager.Instance.MakeEnemiesHostile();
                        }
                        enemyMotor.MakeEnemyHostileToAttacker(GameManager.Instance.PlayerEntityBehaviour);
                    }

                    // Handle killing guards
                    EnemyEntity enemyEntity = entity as EnemyEntity;
                    if (enemyEntity.MobileEnemy.ID == (int)MobileTypes.Knight_CityWatch && entity.CurrentHealth <= 0)
                    {
                        playerEntity.TallyCrimeGuildRequirements(false, 1);
                        playerEntity.CrimeCommitted = PlayerEntity.Crimes.Murder;
                    }
                }
            }
        }

        #endregion

        #region Private Methods

        void SetEntityType(EntityTypes type)
        {
            switch (type)
            {
                case EntityTypes.None:
                    Entity = null;
                    break;
                case EntityTypes.Player:
                    Entity = new PlayerEntity(this);
                    break;
                case EntityTypes.CivilianNPC:
                    Entity = new CivilianEntity(this);
                    break;
            }

            lastEntityType = type;

            if (Entity != null)
                Entity.SetEntityDefaults();
        }

        void SetEntityValue(DaggerfallEntity value)
        {
            RaiseOnSetEntityHandler(entity, value);
            entity = value;
        }

        #endregion

        #region Events

        public delegate void OnSetEntityHandler(DaggerfallEntity oldEntity, DaggerfallEntity newEntity);
        public event OnSetEntityHandler OnSetEntity;
        void RaiseOnSetEntityHandler(DaggerfallEntity oldEntity, DaggerfallEntity newEntity)
        {
            if (OnSetEntity != null)
                OnSetEntity(oldEntity, newEntity);
        }

        #endregion
    }
}
