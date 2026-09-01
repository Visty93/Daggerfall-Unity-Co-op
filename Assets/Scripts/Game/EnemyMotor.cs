// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    Allofich
// 
// Notes:
//

using UnityEngine;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using System.Collections.Generic;
using DaggerfallWorkshop.Utility;
using System.Linq;
using System.Collections;
using Mirror;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Enemy motor and AI combat decision-making logic.
    /// </summary>
    [RequireComponent(typeof(EnemySenses))]
    [RequireComponent(typeof(EnemyAttack))]
    [RequireComponent(typeof(EnemyBlood))]
    [RequireComponent(typeof(EnemySounds))]
    [RequireComponent(typeof(CharacterController))]
    public class EnemyMotor : NetworkBehaviour
    {
        private static readonly bool DEBUG_ENEMY_MOTOR = false;
        private static readonly bool DEBUG_MP_TOUCH_SPELL_FORWARDING = false;
        private const float CLIENT_GRAVITY_SPAWN_GRACE = 5.0f;
        private const float CLIENT_GRAVITY_AUTHORITY_GRACE = 3.0f;
        private float clientGravitySuppressedUntil = 0f;
        private float nextClientRangedAttackRequestTime = 0f;
        private const float CLIENT_RANGED_REQUEST_COOLDOWN = 0.35f;

        // Aquatic enemies are not parented under dungeon blocks in MP, so their classic
        // water clamp cannot use PlayerEnterExit.blockWaterLevel directly. Cache the
        // actual dungeon/block water surface for this enemy and refresh it occasionally.
        private const float AQUATIC_WATER_REFRESH_INTERVAL = 0.25f;
        private const float AQUATIC_WATER_SURFACE_PADDING = 0.35f;
        private const float AQUATIC_HARD_CLAMP_TOLERANCE = 0.05f;
        private float nextAquaticWaterRefreshTime = 0f;
        private bool cachedAquaticWaterValid = false;
        private float cachedAquaticWaterSurfaceY = 0f;

        // Networked visual-only hurt animation sync.
        // MobileUnit.ChangeEnemyState() is local only, so without this only the enemy owner
        // sees MobileStates.Hurt when enemy health/knockback changes locally.
        private const float NETWORK_HURT_ANIM_MIN_INTERVAL = 0.25f;
        private int lastObservedHealthForNetworkHurt = int.MinValue;
        private float nextAllowedNetworkHurtAnimTime = 0f;

        // Diagnostic-only sampling. Does not modify transform, health, gravity, or fall state.
        private bool mpTraceYReady = false;
        private float mpTracePreviousY = 0f;

        // Cached MP hot-path state. These avoid per-physics-tick component searches,
        // collider-array/list allocations, and rebuilding enemy spell candidate lists.
        private NetworkIdentity cachedNetworkIdentity;
        private readonly Collider[] projectileOverlapBuffer = new Collider[64];
        private readonly RaycastHit[] projectileCastBuffer = new RaycastHit[64];

        private EffectBundleSettings[] cachedRangedSpellSettings = new EffectBundleSettings[0];
        private EffectBundleSettings[] cachedClassicTouchSpellSettings = new EffectBundleSettings[0];
        private EffectBundleSettings[] cachedEnhancedTouchSpellSettings = new EffectBundleSettings[0];
        private bool spellSettingsCacheReady = false;

        private const float MP_PROJECTILE_PATH_CACHE_INTERVAL = 0.10f;
        private const float MP_SPELL_ELIGIBILITY_CACHE_INTERVAL = 0.10f;

        private float nextBowPathCheckTime = 0f;
        private bool cachedBowPathClear = false;
        private DaggerfallEntityBehaviour cachedBowPathTarget;

        private float nextRangedSpellEligibilityTime = 0f;
        private bool cachedRangedSpellEligibility = false;
        private DaggerfallEntityBehaviour cachedRangedSpellTarget;
        private bool cachedRangedSpellEnhancedAI = false;

        private float nextTouchSpellEligibilityTime = 0f;
        private bool cachedTouchSpellEligibility = false;
        private DaggerfallEntityBehaviour cachedTouchSpellTarget;
        private bool cachedTouchSpellEnhancedAI = false;

        #region Member Variables

        public float OpenDoorDistance = 2f;         // Maximum distance to open door
        const float attackSpeedDivisor = 2f;        // How much to slow down during attack animations
        float stopDistance = 1.7f;                  // Used to prevent orbiting
        const float doorCrouchingHeight = 1.65f;    // How low enemies dive to pass thru doors
        bool flies;                                 // The enemy can fly
        bool swims;                                 // The enemy can swim
        bool pausePursuit;                          // pause to wait for the player to come closer to ground
        float moveInForAttackTimer;                 // Time until next pursue/retreat decision
        bool moveInForAttack;                       // False = retreat. True = pursue.
        float retreatDistanceMultiplier;            // How far to back off while retreating
        float changeStateTimer;                     // Time until next change in behavior. Padding to prevent instant reflexes.
        bool doStrafe;
        float strafeTimer;
        bool pursuing;                              // Is pursuing
        bool retreating;                            // Is retreating
        bool backingUp;                             // Is backing up
        bool fallDetected;                          // Detected a fall in front of us, so don't move there
        bool foundUpwardSlope;
        bool foundDoor;
        Vector3 lastPosition;                       // Used to track whether we have moved or not
        Vector3 lastDirection;                      // Used to track whether we have rotated or not
        bool rotating;                              // Used to track whether we have rotated or not
        float avoidObstaclesTimer;
        bool checkingClockwise;
        float checkingClockwiseTimer;
        bool didClockwiseCheck;
        float lastTimeWasStuck;
        public bool hasBowAttack;
        float realHeight;
        float centerChange;
        bool resetHeight;
        float heightChangeTimer;
        bool strafeLeft;
        float strafeAngle;
        int searchMult;
        int ignoreMaskForShooting;
        int ignoreMaskForObstacles;
        bool flyerFalls;
        float originalHeight;

        EnemySenses senses;
        Vector3 destination;
        Vector3 detourDestination;
        CharacterController controller;
        MobileUnit mobile;
        Collider myCollider;
        DaggerfallEntityBehaviour entityBehaviour;
        EnemyBlood entityBlood;
        EntityEffectManager entityEffectManager;
        EnemyAttack attack;
        EnemyEntity entity;
        #endregion

        #region Auto Properties

        public bool IsLevitating { get; set; }      // Is this enemy levitating
        public bool IsHostile { get; set; }         // Is this enemy hostile to the player
        public float KnockbackSpeed { get; set; }   // While non-zero, this enemy will be knocked back at this speed
        public Vector3 KnockbackDirection { get; set; } // Direction to travel while being knocked back
        public bool Bashing { get; private set; }   // Is this enemy bashing a door
        public int GiveUpTimer { get; set; }        // Timer for enemy giving up pursuit of target
        public bool ObstacleDetected { get; private set; }
        public EntityEffectBundle SelectedSpell { get; set; }
        public bool CanAct { get; set; }
        public float LastGroundedY { get; set; }    // Used for fall damage
        public bool Falls { get; private set; }

        //============Delegates to allow mods to extend motor behaviour.
        //==When setting a new handler, it may be desired to store the original and call it before/after your own logic.
        public delegate void TakeActionCallback();
        public TakeActionCallback TakeActionHandler { get; set; }

        public delegate bool CanCastRangedSpellCallback();
        public CanCastRangedSpellCallback CanCastRangedSpellHandler { get; set; }

        public delegate bool CanCastTouchSpellCallback();
        public CanCastTouchSpellCallback CanCastTouchSpellHandler { get; set; }

        #endregion

        private bool IsNetworkActive()
        {
            return NetworkServer.active || NetworkClient.active;
        }

        private void EnsureSpellSettingsCache()
        {
            if (spellSettingsCacheReady || entity == null)
                return;

            EffectBundleSettings[] spells = entity.GetSpells();
            if (spells == null || spells.Length == 0)
            {
                cachedRangedSpellSettings = new EffectBundleSettings[0];
                cachedClassicTouchSpellSettings = new EffectBundleSettings[0];
                cachedEnhancedTouchSpellSettings = new EffectBundleSettings[0];
                spellSettingsCacheReady = true;
                return;
            }

            List<EffectBundleSettings> ranged = new List<EffectBundleSettings>();
            List<EffectBundleSettings> classicTouch = new List<EffectBundleSettings>();
            List<EffectBundleSettings> enhancedTouch = new List<EffectBundleSettings>();

            for (int i = 0; i < spells.Length; i++)
            {
                EffectBundleSettings spell = spells[i];

                if (spell.TargetType == TargetTypes.SingleTargetAtRange ||
                    spell.TargetType == TargetTypes.AreaAtRange)
                {
                    ranged.Add(spell);
                }

                if (spell.TargetType == TargetTypes.ByTouch ||
                    spell.TargetType == TargetTypes.CasterOnly)
                {
                    classicTouch.Add(spell);
                }

                if (spell.TargetType == TargetTypes.ByTouch ||
                    spell.TargetType == TargetTypes.AreaAroundCaster)
                {
                    enhancedTouch.Add(spell);
                }
            }

            cachedRangedSpellSettings = ranged.ToArray();
            cachedClassicTouchSpellSettings = classicTouch.ToArray();
            cachedEnhancedTouchSpellSettings = enhancedTouch.ToArray();
            spellSettingsCacheReady = true;
        }

        private void InvalidateRangedSpellEligibility()
        {
            nextRangedSpellEligibilityTime = 0f;
            cachedRangedSpellEligibility = false;
            cachedRangedSpellTarget = null;
            SelectedSpell = null;
        }

        private void InvalidateTouchSpellEligibility()
        {
            nextTouchSpellEligibilityTime = 0f;
            cachedTouchSpellEligibility = false;
            cachedTouchSpellTarget = null;
            SelectedSpell = null;
        }

        private bool IsSelfCollider(Collider collider)
        {
            if (collider == null)
                return false;

            Transform colliderTransform = collider.transform;
            return colliderTransform == transform || colliderTransform.IsChildOf(transform);
        }


        private bool ShouldSimulateMotor()
        {
            // Singleplayer: normal DFU behaviour.
            if (!IsNetworkActive())
                return true;

            // Host/server must still simulate host-owned or server-owned enemies.
            // When authority belongs to a remote client, the host copy is observer-only.
            if (isServer)
            {
                NetworkIdentity ni = cachedNetworkIdentity;
                if (ni == null)
                {
                    ni = GetComponent<NetworkIdentity>();
                    cachedNetworkIdentity = ni;
                }

                if (ni != null && ni.connectionToClient != null)
                {
                    NetworkConnectionToClient hostConnection =
                        NetworkServer.localConnection as NetworkConnectionToClient;

                    if (hostConnection != null && ni.connectionToClient != hostConnection)
                        return false;
                }

                return true;
            }

            return hasAuthority;
        }

        private void SuppressClientGravityFor(float seconds)
        {
            if (NetworkClient.active && !isServer)
            {
                clientGravitySuppressedUntil = Mathf.Max(clientGravitySuppressedUntil, Time.time + seconds);
                LastGroundedY = transform.position.y;
                Falls = false;
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            SuppressClientGravityFor(CLIENT_GRAVITY_SPAWN_GRACE);
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            SuppressClientGravityFor(CLIENT_GRAVITY_AUTHORITY_GRACE);
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();
            SuppressClientGravityFor(CLIENT_GRAVITY_AUTHORITY_GRACE);
        }

        #region Unity Methods

        void Start()
        {
            senses = GetComponent<EnemySenses>();
            controller = GetComponent<CharacterController>();
            mobile = GetComponentInChildren<MobileUnit>();
            myCollider = gameObject.GetComponent<Collider>();
            cachedNetworkIdentity = GetComponent<NetworkIdentity>();

            // Stagger the first MP path/spell refresh so a whole dungeon does not refresh
            // every ranged enemy on the same physics tick.
            float mpHotPathStagger = (Mathf.Abs(GetInstanceID()) % 16) * 0.00625f;
            nextBowPathCheckTime = Time.time + mpHotPathStagger;
            nextRangedSpellEligibilityTime = Time.time + mpHotPathStagger;
            nextTouchSpellEligibilityTime = Time.time + mpHotPathStagger;
            if (mobile == null)
            {
                Debug.LogError($"EnemyMotor: mobile is null on {gameObject.name}");
            }
            else if (!mobile.IsSetup)
            {
                // Do not use Enemy.ID == 0 as an uninitialized test. ID 0 is a valid monster ID
                // in Daggerfall Unity data, and this commonly maps to Rat.
                Debug.LogWarning($"EnemyMotor: mobile is not setup yet on {gameObject.name} (current ID={mobile.Enemy.ID}).");
            }
            else if (mobile.Enemy.ID == 0)
            {
                // Rat can legitimately be ID 0. This is informational only, not an error.
                if (DEBUG_ENEMY_MOTOR) Debug.Log($"EnemyMotor: mobile.Enemy.ID is 0 on {gameObject.name}; treating as valid setup enemy, not uninitialized.");
            }

            if (mobile != null)
            {
                IsHostile = mobile.Enemy.Reactions == MobileReactions.Hostile;
                flies = CanFly();
                swims = mobile.Enemy.Behaviour == MobileBehaviour.Aquatic;
            }
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            entityBlood = GetComponent<EnemyBlood>();
            entityEffectManager = GetComponent<EntityEffectManager>();
            entity = entityBehaviour.Entity as EnemyEntity;
            attack = GetComponent<EnemyAttack>();
            if (entity != null)
                EnsureSpellSettingsCache();
			
			  // Start a coroutine to ensure entity is set
    StartCoroutine(EnsureEnemyEntityIsSet());

            // Add things AI should ignore when checking for a clear path to shoot.
            ignoreMaskForShooting = ~(1 << LayerMask.NameToLayer("SpellMissiles") | 1 << LayerMask.NameToLayer("Ignore Raycast"));

            // Also ignore arrows and "Ignore Raycast" layer for obstacles
            ignoreMaskForObstacles = ~(1 << LayerMask.NameToLayer("SpellMissiles") | 1 << LayerMask.NameToLayer("Ignore Raycast"));

            LastGroundedY = transform.position.y;

            // On remote clients, dungeon enemies can spawn before the client has the dungeon
            // colliders fully ready after reconnect/observer rebuild. Suppress only client-side
            // gravity briefly so visuals do not fall into the void before NetworkTransform/scene
            // placement settles. Server/host gravity is unchanged.
            SuppressClientGravityFor(CLIENT_GRAVITY_SPAWN_GRACE);

            // Get original height, before any height adjustments
            originalHeight = controller.height;

            TakeActionHandler = TakeAction;
            CanCastRangedSpellHandler = CanCastRangedSpell;
            CanCastTouchSpellHandler = CanCastTouchSpell;
        }
		
		
		// Coroutine to ensure `entity` is assigned before usage
public IEnumerator EnsureEnemyEntityIsSet()
{
    float maxWait = 5.0f; // Maximum wait time
    float elapsed = 0f;

    while (elapsed < maxWait)
    {
        if (entityBehaviour != null)
            entity = entityBehaviour.Entity as EnemyEntity;

        if (entity != null)
        {
            if (DEBUG_ENEMY_MOTOR) Debug.Log($"[EnemyMotor] SUCCESS: entity is now assigned for {gameObject.name} (ID: {entity.MobileEnemy.ID})");

            // 🔹 Now that enemy data is available, set bow attack ability
			            // Only need to check for ability to shoot bow once.
            // A mobile has a bow attack if:
            //   - it has RangedAttack1 and does not cast magic (ex: Mage, Healer, ...), or 
            //   - it has both RangedAttack1 and RangedAttack2 (ex: Nightblade)
            // If a mobile only has RangedAttack1 and casts magic, then its ranged attack is only shooting spells, not shooting a bow
            hasBowAttack = (entity.MobileEnemy.HasRangedAttack1 && (!entity.MobileEnemy.CastsMagic || entity.MobileEnemy.HasRangedAttack2));
            EnsureSpellSettingsCache();

            if (DEBUG_ENEMY_MOTOR) Debug.Log($"[EnsureEnemyEntityIsSet] hasBowAttack={hasBowAttack} | HasRangedAttack1={entity.MobileEnemy.HasRangedAttack1} | HasRangedAttack2={entity.MobileEnemy.HasRangedAttack2} | CastsMagic={entity.MobileEnemy.CastsMagic} | isServer={isServer} | isClient={isClient}");
			if (DEBUG_ENEMY_MOTOR) Debug.Log($"[EnsureEnemyEntityIsSet] FINAL VALUES: {gameObject.name} | hasBowAttack={hasBowAttack} | " +
          $"HasRangedAttack1={entity.MobileEnemy.HasRangedAttack1} | " +
          $"HasRangedAttack2={entity.MobileEnemy.HasRangedAttack2} | " +
          $"CastsMagic={entity.MobileEnemy.CastsMagic} | " +
          $"isServer={isServer} | isClient={isClient}");
            yield break; // Exit loop when assigned
        }

        yield return new WaitForSeconds(0.2f);
        elapsed += 0.2f;
    }

    Debug.LogError($"[EnemyMotor] FAILED: entity is STILL NULL after waiting! ({gameObject.name})");
}
		

        void FixedUpdate()
        {
            if (GameManager.Instance.DisableAI)
                return;

            TraceMpVerticalStep();

            if (!ShouldSimulateMotor())
            {
                // Important for reconnect/late observer rebuild: non-authoritative copies
                // must not run gravity, fall damage, AI movement, or door logic. They
                // should follow the authoritative NetworkTransform instead.
                //
                // Do still update the local idle/move animation from the replicated
                // transform delta. Without this, a remote-owned enemy moves correctly
                // over the network but non-owner machines keep the sprite in whatever
                // idle/move state it had before simulation was gated.
                CanAct = false;
                Falls = false;
                LastGroundedY = transform.position.y;
                UpdateToIdleOrMoveAnim();
                return;
            }

            flies = CanFly();
            CanAct = true;
            flyerFalls = false;
            Falls = false;

            HandleParalysis();
            KnockbackMovement();
            ApplyGravity();
            HandleNoAction();
            HandleBashing();
            UpdateTimers();
            if (CanAct)
                TakeActionHandler();
            ApplyFallDamage();
            UpdateToIdleOrMoveAnim();
            OpenDoors();
            HeightAdjust();

            // Do not perform an aquatic water-surface lookup from every FixedUpdate.
            // Castle-sized dungeons can contain thousands of transforms, and the MP water
            // fallback may scan the full dungeon hierarchy while resolving a slaughterfish's
            // water plane. WaterMove() already applies the offset-aware clamp after actual
            // aquatic movement, which preserves the MP water fix without waking this expensive
            // scene-wide lookup for idle aquatic enemies on every physics tick.
        }

        private void TraceMpVerticalStep()
        {
            if (!IsNetworkActive())
                return;

            float currentY = transform.position.y;
            if (!mpTraceYReady)
            {
                mpTraceYReady = true;
                mpTracePreviousY = currentY;
                return;
            }

            float deltaY = currentY - mpTracePreviousY;
            if (Mathf.Abs(deltaY) >= 1.0f)
            {
                NetworkIdentity traceNi = cachedNetworkIdentity != null ? cachedNetworkIdentity : GetComponent<NetworkIdentity>();
                uint traceNetId = traceNi != null ? traceNi.netId : 0U;
                Debug.LogWarning(
                    $"[EnemyDeathTrace][VerticalStep] enemy='{gameObject.name}' netId={traceNetId} " +
                    $"oldY={mpTracePreviousY:0.000} newY={currentY:0.000} deltaY={deltaY:0.000} " +
                    $"lastGroundedY={LastGroundedY:0.000} grounded={(controller != null && controller.isGrounded)} " +
                    $"falls={Falls} server={isServer} client={isClient} authority={hasAuthority}");
            }

            mpTracePreviousY = currentY;
        }

        void Update()
        {
            if (!IsNetworkActive())
                return;

            SyncHurtAnimationFromHealthDrop();
        }

        private void SyncHurtAnimationFromHealthDrop()
        {
            if (entityBehaviour == null)
                entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            if (mobile == null)
                mobile = GetComponentInChildren<MobileUnit>();

            if (entityBehaviour == null || entityBehaviour.Entity == null || mobile == null || !mobile.IsSetup)
                return;

            int currentHealth = entityBehaviour.Entity.CurrentHealth;

            if (lastObservedHealthForNetworkHurt == int.MinValue)
            {
                lastObservedHealthForNetworkHurt = currentHealth;
                return;
            }

            // Only real health loss should trigger a hurt animation. Health repair/spawn initialization
            // must not flash hurt, and dead enemies should be left to death/corpse handling.
            if (currentHealth < lastObservedHealthForNetworkHurt)
            {
                NetworkIdentity traceNi = cachedNetworkIdentity != null ? cachedNetworkIdentity : GetComponent<NetworkIdentity>();
                uint traceNetId = traceNi != null ? traceNi.netId : 0U;
                Debug.LogWarning(
                    $"[EnemyDeathTrace][HealthDrop] enemy='{gameObject.name}' netId={traceNetId} " +
                    $"oldHp={lastObservedHealthForNetworkHurt} newHp={currentHealth} y={transform.position.y:0.000} " +
                    $"lastGroundedY={LastGroundedY:0.000} grounded={(controller != null && controller.isGrounded)} " +
                    $"falls={Falls} server={isServer} client={isClient} authority={hasAuthority}");
            }

            if (currentHealth < lastObservedHealthForNetworkHurt && currentHealth > 0)
                BroadcastNetworkHurtAnimation();

            lastObservedHealthForNetworkHurt = currentHealth;
        }

        private void BroadcastNetworkHurtAnimation()
        {
            // Avoid duplicate flashes when a client-owned enemy plays locally, then receives
            // the server RPC shortly after, or when health sync arrives just after the RPC.
            if (Time.time < nextAllowedNetworkHurtAnimTime)
                return;

            // Singleplayer fallback.
            if (!NetworkClient.active && !NetworkServer.active)
            {
                PlayNetworkHurtAnimationLocal();
                return;
            }

            // Server/host-authoritative enemy: play locally and tell clients.
            if (isServer)
            {
                PlayNetworkHurtAnimationLocal();
                RpcPlayNetworkHurtAnimation();
                return;
            }

            // Client-authoritative enemy: play immediately for the owner, then ask the server
            // to replay it on host and non-owning observers.
            if (hasAuthority && NetworkClient.active)
            {
                PlayNetworkHurtAnimationLocal();
                CmdRequestNetworkHurtAnimation();
            }
        }

        [Command(requiresAuthority = true)]
        private void CmdRequestNetworkHurtAnimation()
        {
            if (!isServer)
                return;

            PlayNetworkHurtAnimationLocal();
            RpcPlayNetworkHurtAnimation();
        }

        [ClientRpc]
        private void RpcPlayNetworkHurtAnimation()
        {
            // Host/server already played it before sending the RPC.
            if (isServer)
                return;

            PlayNetworkHurtAnimationLocal();
        }

        private void PlayNetworkHurtAnimationLocal()
        {
            if (entityBehaviour == null)
                entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            if (mobile == null)
                mobile = GetComponentInChildren<MobileUnit>();

            if (entityBehaviour == null || entityBehaviour.Entity == null || entityBehaviour.Entity.CurrentHealth <= 0)
                return;
            if (mobile == null || !mobile.IsSetup || mobile.FreezeAnims)
                return;

            if (Time.time < nextAllowedNetworkHurtAnimTime)
                return;

            nextAllowedNetworkHurtAnimTime = Time.time + NETWORK_HURT_ANIM_MIN_INTERVAL;

            // Restart Hurt if another hit arrives while already in the hurt state.
            if (mobile.EnemyState == MobileStates.Hurt)
                mobile.ChangeEnemyState(MobileStates.Idle);

            mobile.ChangeEnemyState(MobileStates.Hurt);
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Immediately become hostile towards attacker and know attacker's location.
        /// </summary>
        /// <param name="attacker">Attacker to become hostile towards</param>
        public void MakeEnemyHostileToAttacker(DaggerfallEntityBehaviour attacker)
        {
            if (!senses)
                senses = GetComponent<EnemySenses>();
            if (!entityBehaviour)
                entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

    if (attacker)
    {
        if (DEBUG_ENEMY_MOTOR) Debug.Log($"[MakeEnemyHostileToAttacker] {gameObject.name} targeting {attacker.name} (NetID: {attacker.GetComponent<NetworkIdentity>()?.netId})");
    }
    else
    {
        Debug.LogWarning($"[MakeEnemyHostileToAttacker] {gameObject.name} received NULL attacker!");
    }


            // Assign target if don't already have target, or original target isn't seen or adjacent
            if (attacker && senses && (senses.Target == null || !senses.TargetInSight || senses.DistanceToTarget > 2f))
            {
                senses.Target = attacker;
                senses.SecondaryTarget = senses.Target;
                senses.OldLastKnownTargetPos = attacker.transform.position;
                senses.LastKnownTargetPos = attacker.transform.position;
                senses.PredictedTargetPos = attacker.transform.position;
                GiveUpTimer = 200;
				        if (DEBUG_ENEMY_MOTOR) Debug.Log($"[MakeEnemyHostileToAttacker] {gameObject.name} Set Target: {senses.Target.name}");
            }

            if (attacker == GameManager.Instance.PlayerEntityBehaviour)
            {
                IsHostile = true;
                // Reset former ally's team
                if (entityBehaviour.Entity.Team == MobileTeams.PlayerAlly)
                {
                    int id = (entityBehaviour.Entity as EnemyEntity).MobileEnemy.ID;
                    entityBehaviour.Entity.Team = EnemyBasics.Enemies.First(x => x.ID == id).Team;
                }
            }
        }

        /// <summary>
        /// Attempts to find the ground position below enemy, even if player is flying/falling
        /// </summary>
        /// <param name="distance">Distance to fire ray.</param>
        /// <returns>Hit point on surface below enemy, or enemy position if hit not found in distance.</returns>
        public Vector3 FindGroundPosition(float distance = 16)
        {
            RaycastHit hit;
            Ray ray = new Ray(transform.position, Vector3.down);
            if (Physics.Raycast(ray, out hit, distance))
                return hit.point;

            return transform.position;
        }

        /// <summary>
        /// Call this when floating origin ticks on Y to ensure enemy doesn't die from large "grounded" difference
        /// </summary>
        /// <param name="y">Amount to increment to fallstart</param>
        public void AdjustLastGrounded(float y)
        {
            LastGroundedY += y;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handle paralysis halting movement and animation.
        /// </summary>
        void HandleParalysis()
        {
            // Cancel movement and animations if paralyzed, but still allow gravity to take effect
            // This will have the (intentional for now) side-effect of making paralyzed flying enemies fall out of the air
            // Paralyzed swimming enemies will just freeze in place
            // Freezing anims also prevents the attack from triggering until paralysis cleared
            if (entityBehaviour.Entity.IsParalyzed)
            {
                mobile.FreezeAnims = true;
                CanAct = false;
                flyerFalls = true;
            }
            mobile.FreezeAnims = false;
        }

        /// <summary>
        /// Handles movement if the enemy has been knocked back.
        /// </summary>
        void KnockbackMovement()
        {
            // Prevent stunlocking transforming Seducers
            if (mobile.EnemyState == MobileStates.SeducerTransform1 || mobile.EnemyState == MobileStates.SeducerTransform2)
                return;

            // If hit, get knocked back
            if (KnockbackSpeed > 0)
            {
                // Limit KnockbackSpeed. This can be higher than what is actually used for the speed of motion,
                // making it last longer and do more damage if the enemy collides with something (TODO).
                if (KnockbackSpeed > (40 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)))
                    KnockbackSpeed = (40 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10));

                if (KnockbackSpeed > (5 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)) &&
                    mobile.EnemyState != MobileStates.PrimaryAttack)
                {
                    mobile.ChangeEnemyState(MobileStates.Hurt);
                }

                // Actual speed of motion is limited
                Vector3 motion;
                if (KnockbackSpeed <= (25 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)))
                    motion = KnockbackDirection * KnockbackSpeed;
                else
                    motion = KnockbackDirection * (25 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10));

                // Move in direction of knockback
                if (swims)
                    WaterMove(motion);
                else if (flies || IsLevitating)
                    controller.Move(motion * Time.deltaTime);
                else
                    controller.SimpleMove(motion);

                // Remove remaining knockback and restore animation
                if (GameManager.ClassicUpdate)
                {
                    KnockbackSpeed -= (5 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10));
                    if (KnockbackSpeed <= (5 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10))
                        && mobile.EnemyState != MobileStates.PrimaryAttack)
                    {
                        mobile.ChangeEnemyState(MobileStates.Move);
                    }
                }

                // If a decent hit got in, reconsider whether to continue current tactic
                if (KnockbackSpeed > (10 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)))
                {
                    EvaluateMoveInForAttack();
                }

                CanAct = false;
                flyerFalls = true;
            }
        }

        /// <summary>
        /// Apply gravity to ground-based enemies and paralyzed flyers.
        /// </summary>
        void ApplyGravity()
        {
            if (NetworkClient.active && !isServer && Time.time < clientGravitySuppressedUntil)
            {
                Falls = false;
                LastGroundedY = transform.position.y;
                return;
            }

            // Apply gravity
            if (entity.IsSlowFalling && !flies && !swims && !controller.isGrounded && !IsLevitating)
            {
                Vector3 velocity = controller.velocity * 0.97f; //gradually slow x/z movement
                velocity.y = -1; //slow downward fall
                Vector3 move = velocity * Time.deltaTime;
                controller.Move(move);
            }
            else if (!flies && !swims && !IsLevitating && !controller.isGrounded)
            {
                controller.SimpleMove(Vector3.zero);
                Falls = true;

                // Only cancel movement if actually falling. Sometimes mobiles can get stuck where they are !isGrounded but SimpleMove(Vector3.zero) doesn't help.
                // Allowing them to continue and attempt a Move() frees them, but we don't want to allow that if we can avoid it so they aren't moving
                // while falling, which can also accelerate the fall due to anti-bounce downward movement in Move().
                if (lastPosition != transform.position)
                    CanAct = false;
            }

            if (flyerFalls && flies && !IsLevitating && !entity.IsSlowFalling)
            {
                controller.SimpleMove(Vector3.zero);
                Falls = true;
            }
        }

        /// <summary>
        /// Do nothing if no target or after giving up finding the target or if target position hasn't been acquired yet.
        /// </summary>
        void HandleNoAction()
        {
            if (senses.Target == null || GiveUpTimer <= 0 || senses.PredictedTargetPos == EnemySenses.ResetPlayerPos)
            {
                SetChangeStateTimer();
                searchMult = 0;

                CanAct = false;
            }
        }

        /// <summary>
        /// Handle bashing doors.
        /// </summary>
        void HandleBashing()
        {
            if (Bashing)
            {
                int speed = entity.Stats.LiveSpeed;
                if (GameManager.ClassicUpdate && DFRandom.rand() % speed >= (speed >> 3) + 6 && attack.MeleeTimer == 0)
                {
                    mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
                    attack.ResetMeleeTimer();
                }

                CanAct = false;
            }
        }

        /// <summary>
        /// Updates timers used in this class.
        /// </summary>
        void UpdateTimers()
        {
            if (moveInForAttackTimer > 0)
                moveInForAttackTimer -= Time.deltaTime;

            if (avoidObstaclesTimer > 0)
                avoidObstaclesTimer -= Time.deltaTime;

            // Set avoidObstaclesTimer to 0 if got close enough to detourDestination. Only bother checking if possible to move.
            if (avoidObstaclesTimer > 0 && CanAct)
            {
                Vector3 detourDestination2D = detourDestination;
                detourDestination2D.y = transform.position.y;
                if ((detourDestination2D - transform.position).magnitude <= 0.3f)
                {
                    avoidObstaclesTimer = 0;
                }
            }

            if (checkingClockwiseTimer > 0)
                checkingClockwiseTimer -= Time.deltaTime;

            if (changeStateTimer > 0)
                changeStateTimer -= Time.deltaTime;

            if (strafeTimer > 0)
                strafeTimer -= Time.deltaTime;

            // As long as the target is detected,
            // giveUpTimer is reset to full
            if (senses.DetectedTarget)
                GiveUpTimer = 200;

            // GiveUpTimer value is from classic, so decrease at the speed of classic's update loop
            if (GameManager.ClassicUpdate && !senses.DetectedTarget && GiveUpTimer > 0)
                GiveUpTimer--;
        }

        /// <summary>
        /// Make decision about what action to take.
        /// </summary>
        void TakeAction()
        {
            // Monster speed of movement follows the same formula as for when the player walks
            float moveSpeed = (entity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale;

            // Get isPlayingOneShot for use below
            bool isPlayingOneShot = mobile.IsPlayingOneShot();

            // Reduced speed if playing a one-shot animation with enhanced AI
            if (isPlayingOneShot && DaggerfallUnity.Settings.EnhancedCombatAI)
                moveSpeed /= attackSpeedDivisor;

            // Classic AI moves only as close as melee range. It uses a different range for the player and for other AI.
            if (!DaggerfallUnity.Settings.EnhancedCombatAI)
            {
                if (senses.Target == GameManager.Instance.PlayerEntityBehaviour)
                    stopDistance = attack.MeleeDistance;
                else
                    stopDistance = attack.ClassicMeleeDistanceVsAI;
            }

            // Get location to move towards.
            GetDestination();

            // Get direction & distance to destination.
            Vector3 direction = (destination - transform.position).normalized;

            float distance;
            // If enemy sees the target, use the distance value from EnemySenses, as this is also used for the melee attack decision and we need to be consistent with that.
            if (avoidObstaclesTimer <= 0 && senses.TargetInSight)
                distance = senses.DistanceToTarget;
            else
                distance = (destination - transform.position).magnitude;

            // Do not change action if currently playing oneshot wants to stop actions
            if (isPlayingOneShot && mobile.OneShotPauseActionsWhilePlaying())
                return;

            // Ranged attacks
            if (DoRangedAttack(direction, moveSpeed, distance, isPlayingOneShot))
                return;

            // Touch spells
            if (DoTouchSpell())
                return;

            // Update advance/retreat decision
            if (moveInForAttackTimer <= 0 && avoidObstaclesTimer <= 0)
                EvaluateMoveInForAttack();

            // If detouring, always attempt to move
            if (avoidObstaclesTimer > 0)
            {
                AttemptMove(direction, moveSpeed);
            }
            // Otherwise, if not still executing a retreat, approach target until close enough to be on-guard.
            // If decided to move in for attack, continue until within melee range. Classic always moves in for attack.
            else if ((!retreating && distance >= (stopDistance * 2.75)) || (distance > stopDistance && moveInForAttack))
            {
                // If state change timer is done, or we are continuing an already started pursuit, we can move immediately
                if (changeStateTimer <= 0 || pursuing)
                    AttemptMove(direction, moveSpeed);
                // Otherwise, look at target until timer finishes
                else if (!senses.TargetIsWithinYawAngle(22.5f, destination))
                    TurnToTarget(direction);
            }
            else if (DaggerfallUnity.Settings.EnhancedCombatAI && strafeTimer <= 0)
            {
                StrafeDecision();
            }
            else if (doStrafe && strafeTimer > 0 && (distance >= stopDistance * .8f))
            {
                AttemptMove(direction, moveSpeed / 4, false, true, distance);
            }
            // Back away from combat target if right next to it, or if decided to retreat and enemy is too close.
            // Classic AI never backs away.
            else if (DaggerfallUnity.Settings.EnhancedCombatAI && senses.TargetInSight && (distance < stopDistance * .8f ||
                !moveInForAttack && distance < stopDistance * retreatDistanceMultiplier && (changeStateTimer <= 0 || retreating)))
            {
                // If state change timer is done, or we are already executing a retreat, we can move immediately
                if (changeStateTimer <= 0 || retreating)
                    AttemptMove(direction, moveSpeed / 2, true);
            }
            // Not moving, just look at target
            else if (!senses.TargetIsWithinYawAngle(22.5f, destination))
            {
                TurnToTarget(direction);
            }
            else // Not moving, and no need to turn
            {
                SetChangeStateTimer();
                pursuing = false;
                retreating = false;
            }

        }

        /// <summary>
        /// Get the destination to move towards.
        /// </summary>
        void GetDestination()
        {
            CharacterController targetController = senses.Target.GetComponent<CharacterController>();
            // If detouring around an obstacle or fall, use the detour position
            if (avoidObstaclesTimer > 0)
            {
                destination = detourDestination;
            }
            // Otherwise, try to get to the combat target if there is a clear path to it
            else if (ClearPathToPosition(senses.PredictedTargetPos, (destination - transform.position).magnitude) || (senses.TargetInSight && (hasBowAttack || entity.CurrentMagicka > 0)))
            {
                destination = senses.PredictedTargetPos;
                // Flying enemies and slaughterfish aim for target face.
                // For aquatic enemies, clamp this adjusted destination back below the
                // actual dungeon water surface so a fish chasing a player/NPC above the
                // water does not intentionally steer out of the water in MP dungeons.
                if (flies || IsLevitating || (swims && mobile.Enemy.ID == (int)MonsterCareers.Slaughterfish))
                    destination.y += targetController.height * 0.5f;

                if (swims)
                {
                    float waterSurfaceY;
                    if (TryGetAquaticWaterSurfaceY(out waterSurfaceY))
                    {
                        float maxAquaticCenterY = GetAquaticMaxCenterY(waterSurfaceY);
                        if (destination.y > maxAquaticCenterY)
                            destination.y = maxAquaticCenterY;
                    }
                }

                searchMult = 0;
            }
            // Otherwise, search for target based on its last known position and direction
            else
            {
                Vector3 searchPosition = senses.LastKnownTargetPos + (senses.LastPositionDiff.normalized * searchMult);
                if (searchMult <= 10 && (searchPosition - transform.position).magnitude <= stopDistance)
                    searchMult++;

                destination = searchPosition;
            }

            if (avoidObstaclesTimer <= 0 && !flies && !IsLevitating && !swims && senses.Target)
            {
                // Ground enemies target at their own height
                // Otherwise, short enemies' vector can aim up towards the target, which could interfere with distance-to-target calculations.
                float deltaHeight = (targetController.height - originalHeight) / 2;
                destination.y -= deltaHeight;
            }
        }

        /// <summary>
        /// Handles ranged attacks with bows and spells.
        /// </summary>
        bool DoRangedAttack(
            Vector3 direction,
            float moveSpeed,
            float distance,
            bool isPlayingOneShot)
        {
            if (!IsNetworkActive())
            {
                return DoRangedAttackSinglePlayer(
                    direction,
                    moveSpeed,
                    distance,
                    isPlayingOneShot);
            }

            bool inRange =
                senses.DistanceToTarget > EnemyAttack.minRangedDistance &&
                senses.DistanceToTarget < EnemyAttack.maxRangedDistance;

            if (!inRange ||
                !senses.TargetInSight ||
                !senses.DetectedTarget)
            {
                return false;
            }

            // Evaluate these once. The old MP path could call both helpers more than once
            // in a single FixedUpdate, rebuilding spell bundles and repeating physics tests.
            bool canShootBow = CanShootBow();
            bool canCastRangedSpell =
                !canShootBow && CanCastRangedSpellHandler();

            if (!canShootBow && !canCastRangedSpell)
                return false;

            if (DaggerfallUnity.Settings.EnhancedCombatAI &&
                senses.TargetIsWithinYawAngle(22.5f, destination) &&
                strafeTimer <= 0)
            {
                StrafeDecision();
            }

            if (doStrafe && strafeTimer > 0)
                AttemptMove(direction, moveSpeed / 4, false, true, distance);

            if (GameManager.ClassicUpdate &&
                senses.TargetIsWithinYawAngle(22.5f, destination))
            {
                if (!isPlayingOneShot)
                {
                    // Preserve the existing priority: enemies with a bow use the bow
                    // branch even if they also have magic.
                    if (hasBowAttack)
                    {
                        if (Random.value < 1f / 32f)
                        {
                            if (isServer)
                            {
                                PlayBowAttackAnimationLocal();
                                RpcPlayBowAttackAnimation();
                            }
                            else if (hasAuthority &&
                                     Time.time >=
                                         nextClientRangedAttackRequestTime)
                            {
                                nextClientRangedAttackRequestTime =
                                    Time.time +
                                    CLIENT_RANGED_REQUEST_COOLDOWN;
                                CmdRequestBowAttack(
                                    GetCurrentTargetNetId());
                            }
                        }
                    }
                    else if (Random.value < 1f / 40f)
                    {
                        if (isServer)
                        {
                            EntityEffectBundle spellToCast = SelectedSpell;
                            bool ready =
                                spellToCast != null &&
                                entityEffectManager.SetReadySpell(
                                    spellToCast);

                            InvalidateRangedSpellEligibility();

                            if (ready)
                            {
                                mobile.ChangeEnemyState(
                                    MobileStates.Spell);
                                RpcPlaySpellCastAnimation();
                            }
                        }
                        else if (hasAuthority &&
                                 Time.time >=
                                     nextClientRangedAttackRequestTime)
                        {
                            nextClientRangedAttackRequestTime =
                                Time.time +
                                CLIENT_RANGED_REQUEST_COOLDOWN;
                            CmdRequestSpellCast(
                                GetCurrentTargetNetId());
                            InvalidateRangedSpellEligibility();
                        }
                    }
                }
            }
            else
            {
                TurnToTarget(direction);
            }

            return true;
        }

        // Preserve the original DFU ranged-combat path in pure single-player.
        // None of the Mirror animation/command routing is needed without an active network.
        bool DoRangedAttackSinglePlayer(Vector3 direction, float moveSpeed, float distance, bool isPlayingOneShot)
        {
            bool inRange = senses.DistanceToTarget > EnemyAttack.minRangedDistance && senses.DistanceToTarget < EnemyAttack.maxRangedDistance;
            if (inRange && senses.TargetInSight && senses.DetectedTarget && (CanShootBow() || CanCastRangedSpellHandler()))
            {
                if (DaggerfallUnity.Settings.EnhancedCombatAI && senses.TargetIsWithinYawAngle(22.5f, destination) && strafeTimer <= 0)
                    StrafeDecision();

                if (doStrafe && strafeTimer > 0)
                    AttemptMove(direction, moveSpeed / 4, false, true, distance);

                if (GameManager.ClassicUpdate && senses.TargetIsWithinYawAngle(22.5f, destination))
                {
                    if (!isPlayingOneShot)
                    {
                        if (hasBowAttack)
                        {
                            if (Random.value < 1 / 32f)
                            {
                                if (mobile.Enemy.HasRangedAttack1 && !mobile.Enemy.HasRangedAttack2)
                                    mobile.ChangeEnemyState(MobileStates.RangedAttack1);
                                else if (mobile.Enemy.HasRangedAttack2)
                                    mobile.ChangeEnemyState(MobileStates.RangedAttack2);
                            }
                        }
                        else if (Random.value < 1 / 40f && entityEffectManager.SetReadySpell(SelectedSpell))
                        {
                            mobile.ChangeEnemyState(MobileStates.Spell);
                        }
                    }
                }
                else
                {
                    TurnToTarget(direction);
                }

                return true;
            }

            return false;
        }

		
		

        private uint GetCurrentTargetNetId()
        {
            if (senses == null || senses.Target == null)
                return GetLocalPlayerMultiplayerNetId();

            NetworkIdentity targetIdentity = senses.Target.GetComponent<NetworkIdentity>();
            if (targetIdentity != null && targetIdentity.netId != 0)
                return targetIdentity.netId;

            // In this MP mod the local physical player is PlayerAdvanced and has no netId.
            // Client-owned enemies can target that object locally, but the server/RPC side
            // needs the matching PlayerMultiplayer netId to know which client should receive
            // the real local-player spell payload. Without this, the spell request reaches
            // the server with targetNetId=0 and is often rejected or sent back as visual-only.
            return GetLocalPlayerMultiplayerNetId();
        }

        private uint GetLocalPlayerMultiplayerNetId()
        {
            try
            {
                global::PlayerMultiplayer pm = global::PlayerMultiplayer.localPlayer;
                if (pm == null || !pm.isLocalPlayer)
                {
                    global::PlayerMultiplayer[] players = FindObjectsOfType<global::PlayerMultiplayer>();
                    for (int i = 0; i < players.Length; i++)
                    {
                        if (players[i] != null && players[i].isLocalPlayer)
                        {
                            pm = players[i];
                            global::PlayerMultiplayer.localPlayer = pm;
                            break;
                        }
                    }
                }

                if (pm != null)
                {
                    NetworkIdentity ni = pm.GetComponent<NetworkIdentity>();
                    if (ni != null)
                        return ni.netId;
                }
            }
            catch { }

            return 0;
        }


        private bool TryGetMobileForNetworkAnimation(out MobileUnit resolvedMobile, string reason)
        {
            resolvedMobile = mobile;

            if (resolvedMobile == null)
            {
                resolvedMobile = GetComponentInChildren<MobileUnit>(true);
                if (resolvedMobile != null)
                    mobile = resolvedMobile;
            }

            if (resolvedMobile == null)
            {
                if (DEBUG_ENEMY_MOTOR)
                    Debug.LogWarning($"[{reason}] MobileUnit is not ready on '{name}'. Skipping network animation instead of throwing inside RPC.");
                return false;
            }

            if (!resolvedMobile.IsSetup)
            {
                if (DEBUG_ENEMY_MOTOR)
                    Debug.LogWarning($"[{reason}] MobileUnit is not setup yet on '{name}'. Skipping network animation instead of throwing inside RPC.");
                return false;
            }

            return true;
        }

        private IEnumerator CoDelayedNetworkSpellCastAnimation()
        {
            // RPCs can arrive on observers before MobileUnit has finished setup, especially
            // around network dungeon spawn / quest popup timing. Retrying for a few frames is
            // visual-only and prevents Mirror from disconnecting the client for an RPC exception.
            for (int i = 0; i < 20; i++)
            {
                yield return null;

                MobileUnit resolvedMobile;
                if (!TryGetMobileForNetworkAnimation(out resolvedMobile, "DelayedRpcPlaySpellCastAnimation"))
                    continue;

                if (resolvedMobile.EnemyState != MobileStates.Spell)
                    resolvedMobile.ChangeEnemyState(MobileStates.Spell);
                yield break;
            }
        }

        private IEnumerator CoDelayedNetworkBowAttackAnimation()
        {
            for (int i = 0; i < 20; i++)
            {
                yield return null;

                MobileUnit resolvedMobile;
                if (!TryGetMobileForNetworkAnimation(out resolvedMobile, "DelayedRpcPlayBowAttackAnimation"))
                    continue;

                if (resolvedMobile.Enemy.HasRangedAttack1 && !resolvedMobile.Enemy.HasRangedAttack2)
                    resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack1);
                else if (resolvedMobile.Enemy.HasRangedAttack2)
                    resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack2);

                yield break;
            }
        }

        private void PlayBowAttackAnimationLocal()
        {
            MobileUnit resolvedMobile;
            if (!TryGetMobileForNetworkAnimation(out resolvedMobile, "PlayBowAttackAnimationLocal"))
                return;

            if (resolvedMobile.Enemy.HasRangedAttack1 && !resolvedMobile.Enemy.HasRangedAttack2)
                resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack1);
            else if (resolvedMobile.Enemy.HasRangedAttack2)
                resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack2);
        }

        [Command(requiresAuthority = true)]
        private void CmdRequestBowAttack(uint targetNetId)
        {
            if (!isServer)
                return;

            if (mobile == null)
                mobile = GetComponentInChildren<MobileUnit>();
            if (senses == null)
                senses = GetComponent<EnemySenses>();

            DaggerfallEntityBehaviour explicitTarget = ResolveRequestedTargetOrOwner(targetNetId);
            if (explicitTarget != null && senses != null)
            {
                senses.Target = explicitTarget;
                senses.LastKnownTargetPos = explicitTarget.transform.position;
                senses.PredictedTargetPos = explicitTarget.transform.position;
                senses.DetectedTarget = true;
            }

            if (mobile == null || senses == null || senses.Target == null || !hasBowAttack)
                return;

            // Server sanity check. The owner decides when to request the shot, but the
            // server still requires a plausible ranged distance before starting the state.
            float distance = Vector3.Distance(transform.position, senses.Target.transform.position);
            if (distance < EnemyAttack.minRangedDistance * 0.5f || distance > EnemyAttack.maxRangedDistance + 8f)
                return;

            if (!CanShootBow())
                return;

            PlayBowAttackAnimationLocal();
            RpcPlayBowAttackAnimation();
        }

        [Command(requiresAuthority = true)]
        private void CmdRequestSpellCast(uint targetNetId)
        {
            if (!isServer)
                return;

            if (mobile == null)
                mobile = GetComponentInChildren<MobileUnit>();
            if (senses == null)
                senses = GetComponent<EnemySenses>();
            if (entityEffectManager == null)
                entityEffectManager = GetComponent<EntityEffectManager>();

            DaggerfallEntityBehaviour explicitTarget = ResolveRequestedTargetOrOwner(targetNetId);
            if (explicitTarget != null && senses != null)
            {
                senses.Target = explicitTarget;
                senses.LastKnownTargetPos = explicitTarget.transform.position;
                senses.PredictedTargetPos = explicitTarget.transform.position;
                senses.DetectedTarget = true;
            }

            if (mobile == null || senses == null || senses.Target == null || entityEffectManager == null)
                return;

            float distance = Vector3.Distance(transform.position, senses.Target.transform.position);
            if (distance > EnemyAttack.maxRangedDistance + 64f)
                return;

            if (entity == null && entityBehaviour != null)
                entity = entityBehaviour.Entity as EnemyEntity;

            if (entity == null || entity.CurrentMagicka <= 0)
            {
                RpcSyncEnemyMagicka(entity != null ? entity.CurrentMagicka : 0);
                return;
            }

            EntityEffectBundle serverSpell = SelectServerRangedSpellForCommand();
            if (serverSpell == null || !entityEffectManager.SetReadySpell(serverSpell))
            {
                RpcSyncEnemyMagicka(entity.CurrentMagicka);
                return;
            }

            mobile.ChangeEnemyState(MobileStates.Spell);
            RpcPlaySpellCastAnimation();
        }

        [Command(requiresAuthority = true)]
        private void CmdRequestTouchSpellCast(uint targetNetId)
        {
            if (!isServer)
                return;

            if (mobile == null)
                mobile = GetComponentInChildren<MobileUnit>();
            if (senses == null)
                senses = GetComponent<EnemySenses>();
            if (entityEffectManager == null)
                entityEffectManager = GetComponent<EntityEffectManager>();
            if (attack == null)
                attack = GetComponent<EnemyAttack>();

            DaggerfallEntityBehaviour explicitTarget = ResolveRequestedTargetOrOwner(targetNetId);
            if (explicitTarget != null && senses != null)
            {
                senses.Target = explicitTarget;
                senses.LastKnownTargetPos = explicitTarget.transform.position;
                senses.PredictedTargetPos = explicitTarget.transform.position;
                senses.DetectedTarget = true;
            }

            if (mobile == null || senses == null || senses.Target == null || entityEffectManager == null || attack == null)
                return;

            float distance = Vector3.Distance(transform.position, senses.Target.transform.position);
            if (distance > attack.MeleeDistance + senses.TargetRateOfApproach + 3.0f)
                return;

            if (entity == null && entityBehaviour != null)
                entity = entityBehaviour.Entity as EnemyEntity;

            if (entity == null || entity.CurrentMagicka <= 0)
            {
                RpcSyncEnemyMagicka(entity != null ? entity.CurrentMagicka : 0);
                return;
            }

            EntityEffectBundle serverSpell = SelectServerTouchSpellForCommand();
            if (serverSpell == null || !entityEffectManager.SetReadySpell(serverSpell))
            {
                RpcSyncEnemyMagicka(entity.CurrentMagicka);
                return;
            }

            if (mobile.EnemyState != MobileStates.Spell)
                mobile.ChangeEnemyState(MobileStates.Spell);

            RpcPlaySpellCastAnimation();
            TryForwardTouchSpellPayloadToRemotePlayer(serverSpell, "client-owned-command-touch");
            attack.ResetMeleeTimer();
        }

        [ClientRpc]
        private void RpcSyncEnemyMagicka(int currentMagicka)
        {
            if (isServer)
                return;

            if (entityBehaviour == null)
                entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

            if (entityBehaviour != null && entityBehaviour.Entity != null)
            {
                entityBehaviour.Entity.CurrentMagicka = Mathf.Max(0, currentMagicka);
                entity = entityBehaviour.Entity as EnemyEntity;
            }
        }

        [Server]
        private DaggerfallEntityBehaviour TryResolveNetworkTarget(uint targetNetId)
        {
            if (targetNetId != 0)
            {
                NetworkIdentity targetIdentity = null;
                if (NetworkServer.spawned.TryGetValue(targetNetId, out targetIdentity) && targetIdentity != null)
                    return targetIdentity.GetComponent<DaggerfallEntityBehaviour>();

                // Host player can be netId 0 in this project, and some edge cases may not be
                // present in spawned by the time the request is processed. Fall back to scan.
                NetworkIdentity[] identities = FindObjectsOfType<NetworkIdentity>();
                for (int i = 0; i < identities.Length; i++)
                {
                    NetworkIdentity identity = identities[i];
                    if (identity != null && identity.netId == targetNetId)
                        return identity.GetComponent<DaggerfallEntityBehaviour>();
                }
            }

            return null;
        }

        [Server]
        private DaggerfallEntityBehaviour ResolveRequestedTargetOrOwner(uint targetNetId)
        {
            DaggerfallEntityBehaviour target = TryResolveNetworkTarget(targetNetId);
            if (target != null)
                return target;

            // If a client-owned enemy was targeting the local PlayerAdvanced, the client may
            // have had no NetworkIdentity on the target at request time. The owner connection's
            // PlayerMultiplayer is the correct server-side stand-in for aiming/RPC target id.
            try
            {
                if (connectionToClient != null && connectionToClient.identity != null)
                    return connectionToClient.identity.GetComponent<DaggerfallEntityBehaviour>();
            }
            catch { }

            return null;
        }

        [Server]
        private EntityEffectBundle SelectServerRangedSpellForCommand()
        {
            if (entity == null && entityBehaviour != null)
                entity = entityBehaviour.Entity as EnemyEntity;

            if (entity == null || entityBehaviour == null ||
                entity.CurrentMagicka <= 0)
            {
                return null;
            }

            EnsureSpellSettingsCache();
            if (cachedRangedSpellSettings.Length == 0)
                return null;

            for (int attempt = 0;
                 attempt < cachedRangedSpellSettings.Length;
                 attempt++)
            {
                EffectBundleSettings selected =
                    cachedRangedSpellSettings[
                        Random.Range(0, cachedRangedSpellSettings.Length)];

                EntityEffectBundle bundle =
                    new EntityEffectBundle(selected, entityBehaviour);

                if (!EffectsAlreadyOnTarget(bundle))
                    return bundle;
            }

            return new EntityEffectBundle(
                cachedRangedSpellSettings[
                    Random.Range(0, cachedRangedSpellSettings.Length)],
                entityBehaviour);
        }

        [Server]
        private EntityEffectBundle SelectServerTouchSpellForCommand()
        {
            if (entity == null && entityBehaviour != null)
                entity = entityBehaviour.Entity as EnemyEntity;

            if (entity == null || entityBehaviour == null ||
                entity.CurrentMagicka <= 0)
            {
                return null;
            }

            EnsureSpellSettingsCache();

            EffectBundleSettings[] touchSettings =
                DaggerfallUnity.Settings.EnhancedCombatAI
                    ? cachedEnhancedTouchSpellSettings
                    : cachedClassicTouchSpellSettings;

            if (touchSettings.Length == 0)
                return null;

            for (int attempt = 0; attempt < touchSettings.Length; attempt++)
            {
                EffectBundleSettings selected =
                    touchSettings[Random.Range(0, touchSettings.Length)];

                EntityEffectBundle bundle =
                    new EntityEffectBundle(selected, entityBehaviour);

                if (!EffectsAlreadyOnTarget(bundle))
                    return bundle;
            }

            return new EntityEffectBundle(
                touchSettings[Random.Range(0, touchSettings.Length)],
                entityBehaviour);
        }

	[ClientRpc]
void RpcPlaySpellCastAnimation()
{
    if (isServer) return; // 🔹 **Prevent host from playing twice**

    if (DEBUG_ENEMY_MOTOR) Debug.Log($"[RpcPlaySpellCastAnimation] Syncing spell cast animation on client.");

    MobileUnit resolvedMobile;
    if (!TryGetMobileForNetworkAnimation(out resolvedMobile, "RpcPlaySpellCastAnimation"))
    {
        StartCoroutine(CoDelayedNetworkSpellCastAnimation());
        return;
    }

    if (resolvedMobile.EnemyState != MobileStates.Spell)
        resolvedMobile.ChangeEnemyState(MobileStates.Spell);
}

[ClientRpc]
void RpcPlayBowAttackAnimation()
{
    if (isServer) return; // 🔹 **Prevent host from playing twice**

    if (DEBUG_ENEMY_MOTOR) Debug.Log($"[RpcPlayBowAttackAnimation] Syncing bow attack animation on client.");

    MobileUnit resolvedMobile;
    if (!TryGetMobileForNetworkAnimation(out resolvedMobile, "RpcPlayBowAttackAnimation"))
    {
        StartCoroutine(CoDelayedNetworkBowAttackAnimation());
        return;
    }
    
    if (resolvedMobile.Enemy.HasRangedAttack1 && !resolvedMobile.Enemy.HasRangedAttack2)
        resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack1);
    else if (resolvedMobile.Enemy.HasRangedAttack2)
        resolvedMobile.ChangeEnemyState(MobileStates.RangedAttack2);
}	
		
		

        private void DebugMpTouchSpell(string phase, string detail)
        {
            if (!DEBUG_MP_TOUCH_SPELL_FORWARDING)
                return;

            try
            {
                string targetName = senses != null && senses.Target != null ? senses.Target.name : "null";
                NetworkIdentity targetIdentity = senses != null && senses.Target != null ? senses.Target.GetComponent<NetworkIdentity>() : null;
                uint targetNetId = targetIdentity != null ? targetIdentity.netId : 0;
                Debug.Log($"[MPTouchSpellForward][{phase}] enemy='{name}' netId={netId} server={isServer} client={isClient} hasAuth={hasAuthority} target='{targetName}' targetNetId={targetNetId} {detail}");
            }
            catch
            {
                Debug.Log($"[MPTouchSpellForward][{phase}] enemy='{name}' {detail}");
            }
        }

        [Server]
        private bool TryForwardTouchSpellPayloadToRemotePlayer(EntityEffectBundle spell, string reason)
        {
            if (!isServer || spell == null || senses == null || senses.Target == null)
                return false;

            if (spell.Settings.TargetType != TargetTypes.ByTouch && spell.Settings.TargetType != TargetTypes.AreaAroundCaster)
                return false;

            PlayerMultiplayer targetPlayer = senses.Target.GetComponent<PlayerMultiplayer>();
            if (targetPlayer == null)
                targetPlayer = senses.Target.GetComponentInParent<PlayerMultiplayer>();

            if (targetPlayer == null)
            {
                DebugMpTouchSpell("ForwardSkip", $"reason={reason} no PlayerMultiplayer on target targetType={spell.Settings.TargetType}");
                return false;
            }

            NetworkConnection targetConnection = targetPlayer.connectionToClient;
            if (targetConnection == null)
            {
                DebugMpTouchSpell("ForwardSkip", $"reason={reason} target PlayerMultiplayer has no connection targetNetId={targetPlayer.netId}");
                return false;
            }

            // Host/local player already receives the touch spell through the normal server-side PlayerAdvanced path.
            if (NetworkServer.localConnection != null && targetConnection == NetworkServer.localConnection)
            {
                DebugMpTouchSpell("ForwardSkip", $"reason={reason} target is host/local connection targetNetId={targetPlayer.netId}");
                return false;
            }

            NetworkIdentity enemyIdentity = GetComponent<NetworkIdentity>();
            uint enemyNetId = enemyIdentity != null ? enemyIdentity.netId : 0;
            int spellIndex = GetEnemySpellIndexForForwarding(spell);
            string spellData = JsonUtility.ToJson(spell.Settings);
            int effectCount = spell.Settings.Effects != null ? spell.Settings.Effects.Length : -1;

            DebugMpTouchSpell("ForwardSend", $"reason={reason} enemyNetId={enemyNetId} targetNetId={targetPlayer.netId} conn={targetConnection.connectionId} spellIndex={spellIndex} targetType={spell.Settings.TargetType} effects={effectCount}");
            targetPlayer.TargetApplyEnemyTouchSpellPayload(targetConnection, enemyNetId, spellIndex, spellData);
            return true;
        }

        private int GetEnemySpellIndexForForwarding(EntityEffectBundle spell)
        {
            if (spell == null || entity == null)
                return -1;

            EffectBundleSettings[] spells = entity.GetSpells();
            if (spells == null)
                return -1;

            for (int i = 0; i < spells.Length; i++)
            {
                if (SpellSettingsMatchForForwarding(spells[i], spell.Settings))
                    return i;
            }

            return -1;
        }
        private bool IsCurrentTouchTargetLocalPlayer(out global::PlayerMultiplayer localTargetPlayer)
        {
            localTargetPlayer = null;

            if (senses == null || senses.Target == null)
                return false;

            // Normal MP case: enemies target the PlayerMultiplayer shell.
            localTargetPlayer = senses.Target.GetComponent<global::PlayerMultiplayer>();
            if (localTargetPlayer == null)
                localTargetPlayer = senses.Target.GetComponentInParent<global::PlayerMultiplayer>();

            if (localTargetPlayer != null)
                return localTargetPlayer.isLocalPlayer;

            // Safety fallback for old/stale target states: if this client-owned enemy still
            // has the local PlayerAdvanced as target, this is also the local player.
            try
            {
                return GameManager.Instance != null &&
                       GameManager.Instance.PlayerEntityBehaviour != null &&
                       senses.Target == GameManager.Instance.PlayerEntityBehaviour;
            }
            catch { }

            return false;
        }

        private bool TryApplyClientOwnedTouchSpellLocally(uint targetNetId)
        {
            global::PlayerMultiplayer localTargetPlayer;
            if (!IsCurrentTouchTargetLocalPlayer(out localTargetPlayer))
                return false;

            bool canCast = CanCastTouchSpellHandler();
            DebugMpTouchSpell("ClientLocalTouch", $"targetNetId={targetNetId} canCast={canCast} selectedType={(SelectedSpell != null ? SelectedSpell.Settings.TargetType.ToString() : "null")}");

            if (!canCast || SelectedSpell == null)
                return false;

            DaggerfallEntityBehaviour localPlayerBehaviour = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
            if (localPlayerBehaviour == null)
            {
                DebugMpTouchSpell("ClientLocalTouchFail", "local PlayerAdvanced behaviour is null");
                return false;
            }

            EntityEffectManager localEffectManager = localPlayerBehaviour.GetComponent<EntityEffectManager>();
            if (localEffectManager == null)
            {
                DebugMpTouchSpell("ClientLocalTouchFail", "local PlayerAdvanced has no EntityEffectManager");
                return false;
            }

            if (mobile != null && mobile.EnemyState != MobileStates.Spell)
                mobile.ChangeEnemyState(MobileStates.Spell);

            int spellIndex = GetEnemySpellIndexForForwarding(SelectedSpell);
            int effectCount = SelectedSpell.Settings.Effects != null ? SelectedSpell.Settings.Effects.Length : -1;
            DebugMpTouchSpell("ClientLocalAssign", $"targetNetId={targetNetId} spellIndex={spellIndex} targetType={SelectedSpell.Settings.TargetType} effects={effectCount}");

            // Apply the real touch payload to the real local player body. PlayerMultiplayer is
            // only the network target shell and has no EntityEffectManager.
            localEffectManager.AssignBundle(SelectedSpell, AssignBundleFlags.ShowNonPlayerFailures);

            // Replay the visible effect on this player's PlayerMultiplayer shell for host and
            // other observers. The target client already received the real local effect above.
            try
            {
                uint enemyNetId = 0;
                NetworkIdentity enemyIdentity = GetComponent<NetworkIdentity>();
                if (enemyIdentity != null)
                    enemyNetId = enemyIdentity.netId;

                global::PlayerMultiplayer reporter = localTargetPlayer != null ? localTargetPlayer : global::PlayerMultiplayer.GetLocalPlayer();
                if (reporter != null)
                {
                    DebugMpTouchSpell("ClientLocalCosmeticsReport", $"enemyNetId={enemyNetId} reporterNetId={reporter.netId}");
                    reporter.CmdReportLocalPlayerSpellEffectCosmetics(enemyNetId);
                }
                else
                {
                    DebugMpTouchSpell("ClientLocalCosmeticsSkip", "no local PlayerMultiplayer reporter found");
                }
            }
            catch (System.Exception ex)
            {
                DebugMpTouchSpell("ClientLocalCosmeticsError", ex.Message);
            }

            return true;
        }


        private bool SpellSettingsMatchForForwarding(EffectBundleSettings a, EffectBundleSettings b)
        {
            if (a.TargetType != b.TargetType || a.ElementType != b.ElementType || a.BundleType != b.BundleType)
                return false;

            int ac = a.Effects != null ? a.Effects.Length : 0;
            int bc = b.Effects != null ? b.Effects.Length : 0;
            if (ac != bc)
                return false;

            for (int i = 0; i < ac; i++)
            {
                if (a.Effects[i].Key != b.Effects[i].Key)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Handles touch-range spells.
        /// </summary>
        bool DoTouchSpell()
        {
            if (!IsNetworkActive())
                return DoTouchSpellSinglePlayer();

            if (!senses.TargetInSight ||
                !senses.DetectedTarget ||
                attack.MeleeTimer != 0 ||
                senses.DistanceToTarget >
                    attack.MeleeDistance +
                    senses.TargetRateOfApproach)
            {
                if (DEBUG_MP_TOUCH_SPELL_FORWARDING &&
                    NetworkServer.active &&
                    senses != null &&
                    senses.Target != null &&
                    senses.DistanceToTarget <=
                        attack.MeleeDistance +
                        senses.TargetRateOfApproach +
                        1.5f)
                {
                    DebugMpTouchSpell(
                        "GateFail",
                        $"targetInSight={senses.TargetInSight} " +
                        $"detected={senses.DetectedTarget} " +
                        $"meleeTimer={attack.MeleeTimer:F2} " +
                        $"dist={senses.DistanceToTarget:F2} " +
                        $"allowed={(attack.MeleeDistance + senses.TargetRateOfApproach):F2}");
                }

                return false;
            }

            if (isServer)
            {
                bool canCast = CanCastTouchSpellHandler();
                EntityEffectBundle spellToCast =
                    canCast ? SelectedSpell : null;

                if (DEBUG_MP_TOUCH_SPELL_FORWARDING)
                {
                    DebugMpTouchSpell(
                        "TryServerTouch",
                        $"canCast={canCast} " +
                        $"selectedType={(spellToCast != null ? spellToCast.Settings.TargetType.ToString() : "null")}");
                }

                bool ready =
                    spellToCast != null &&
                    entityEffectManager.SetReadySpell(spellToCast);

                if (ready)
                {
                    if (mobile.EnemyState != MobileStates.Spell)
                        mobile.ChangeEnemyState(MobileStates.Spell);

                    RpcPlaySpellCastAnimation();

                    TryForwardTouchSpellPayloadToRemotePlayer(
                        spellToCast,
                        "server-touch");

                    InvalidateTouchSpellEligibility();
                    attack.ResetMeleeTimer();
                    return true;
                }

                if (canCast)
                    InvalidateTouchSpellEligibility();

                if (DEBUG_MP_TOUCH_SPELL_FORWARDING)
                {
                    DebugMpTouchSpell(
                        "ServerTouchFailed",
                        $"canCast={canCast} selectedNull={spellToCast == null}");
                }
            }
            else if (hasAuthority &&
                     Time.time >=
                         nextClientRangedAttackRequestTime)
            {
                uint targetNetId = GetCurrentTargetNetId();

                if (TryApplyClientOwnedTouchSpellLocally(targetNetId))
                {
                    nextClientRangedAttackRequestTime =
                        Time.time +
                        CLIENT_RANGED_REQUEST_COOLDOWN;
                    InvalidateTouchSpellEligibility();
                    attack.ResetMeleeTimer();
                    return true;
                }

                if (DEBUG_MP_TOUCH_SPELL_FORWARDING)
                {
                    DebugMpTouchSpell(
                        "ClientRequestTouch",
                        $"targetNetId={targetNetId}");
                }

                nextClientRangedAttackRequestTime =
                    Time.time +
                    CLIENT_RANGED_REQUEST_COOLDOWN;
                CmdRequestTouchSpellCast(targetNetId);
                InvalidateTouchSpellEligibility();
                attack.ResetMeleeTimer();
                return true;
            }

            return false;
        }

        // Preserve original DFU touch-spell behavior and avoid constructing MP debug/network
        // payload strings in single-player even when the MP debug flag is disabled.
        bool DoTouchSpellSinglePlayer()
        {
            if (senses.TargetInSight && senses.DetectedTarget && attack.MeleeTimer == 0
                && senses.DistanceToTarget <= attack.MeleeDistance + senses.TargetRateOfApproach
                && CanCastTouchSpellHandler() && entityEffectManager.SetReadySpell(SelectedSpell))
            {
                if (mobile.EnemyState != MobileStates.Spell)
                    mobile.ChangeEnemyState(MobileStates.Spell);

                attack.ResetMeleeTimer();
                return true;
            }

            return false;
        }



        /// <summary>
        /// Decide whether to strafe, and get direction to strafe to.
        /// </summary>
        void StrafeDecision()
        {
            doStrafe = Random.Range(0, 4) == 0;
            strafeTimer = Random.Range(1f, 2f);
            if (doStrafe)
            {
                if (Random.Range(0, 2) == 0)
                    strafeLeft = true;
                else
                    strafeLeft = false;

                Vector3 north = destination;
                north.z++; // Adding 1 to z so this Vector3 will be north of the destination Vector3.

                // Get angle between vector from destination to the north of it, and vector from destination to this enemy's position
                strafeAngle = Vector3.SignedAngle(destination - north, destination - transform.position, Vector3.up);
                if (strafeAngle < 0)
                    strafeAngle = 360 + strafeAngle;

                // Convert to radians
                strafeAngle *= Mathf.PI / 180;
            }
        }

        /// <summary>
        /// Returns whether there is a clear path to move the given distance from the current location towards the given location. True if clear
        /// or if combat target is the first obstacle hit.
        /// </summary>
        bool ClearPathToPosition(Vector3 location, float dist = 30)
        {
            Vector3 sphereCastDir = (location - transform.position).normalized;
            Vector3 sphereCastDir2d = sphereCastDir;
            sphereCastDir2d.y = 0;
            ObstacleCheck(sphereCastDir2d);
            FallCheck(sphereCastDir2d);

            if (ObstacleDetected || fallDetected)
                return false;

            RaycastHit hit;
            if (Physics.SphereCast(transform.position, controller.radius / 2, sphereCastDir, out hit, dist, ignoreMaskForShooting))
            {
                DaggerfallEntityBehaviour hitTarget = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
                if (hitTarget == senses.Target)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsSameSpellTarget(DaggerfallEntityBehaviour hitTarget, DaggerfallEntityBehaviour expectedTarget)
        {
            if (hitTarget == null || expectedTarget == null)
                return false;

            if (hitTarget == expectedTarget)
                return true;

            try
            {
                NetworkIdentity hitNi = hitTarget.GetComponent<NetworkIdentity>();
                NetworkIdentity expectedNi = expectedTarget.GetComponent<NetworkIdentity>();
                if (hitNi != null && expectedNi != null && hitNi.netId != 0 && hitNi.netId == expectedNi.netId)
                    return true;
            }
            catch { }

            try
            {
                if (NetworkClient.active || NetworkServer.active)
                {
                    DaggerfallEntityBehaviour localPlayer = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
                    if (localPlayer != null && (hitTarget == localPlayer || expectedTarget == localPlayer))
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Returns true if can shoot projectile at target.
        /// </summary>
        public bool HasClearPathToShootProjectile(float speed, float originDistance, float radius)
        {
            if (!IsNetworkActive())
                return HasClearPathToShootProjectileSinglePlayer(speed, originDistance, radius);

            Vector3 predictedTargetPosition = senses.PredictNextTargetPos(speed);
            if (predictedTargetPosition == EnemySenses.ResetPlayerPos)
                return false;

            Vector3 toTarget = predictedTargetPosition - transform.position;
            float sphereCastDist = toTarget.magnitude;
            if (sphereCastDist <= 0.001f)
                return false;

            Vector3 sphereCastDir = toTarget / sphereCastDist;
            Vector3 shootOrigin = transform.position + sphereCastDir * originDistance;

            // Non-alloc muzzle-space check. Ignore this enemy's own root/child colliders
            // without disabling and re-enabling physics shapes every FixedUpdate.
            int overlapCount = Physics.OverlapSphereNonAlloc(
                shootOrigin,
                radius,
                projectileOverlapBuffer,
                ignoreMaskForShooting,
                QueryTriggerInteraction.UseGlobal);

            for (int i = 0; i < overlapCount; i++)
            {
                Collider overlap = projectileOverlapBuffer[i];
                projectileOverlapBuffer[i] = null;

                if (overlap == null || IsSelfCollider(overlap))
                    continue;

                return false;
            }

            // SphereCastNonAlloc results are not guaranteed to be ordered. Select the
            // nearest non-self hit so behaviour matches the normal first-hit SphereCast.
            int hitCount = Physics.SphereCastNonAlloc(
                shootOrigin,
                radius,
                sphereCastDir,
                projectileCastBuffer,
                sphereCastDist,
                ignoreMaskForShooting,
                QueryTriggerInteraction.UseGlobal);

            RaycastHit nearestHit = new RaycastHit();
            bool foundNonSelfHit = false;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit candidate = projectileCastBuffer[i];
                projectileCastBuffer[i] = new RaycastHit();

                if (candidate.collider == null || IsSelfCollider(candidate.collider))
                    continue;

                if (!foundNonSelfHit || candidate.distance < nearestDistance)
                {
                    foundNonSelfHit = true;
                    nearestDistance = candidate.distance;
                    nearestHit = candidate;
                }
            }

            if (!foundNonSelfHit)
                return true;

            DaggerfallEntityBehaviour hitTarget =
                nearestHit.transform != null
                    ? nearestHit.transform.GetComponentInParent<DaggerfallEntityBehaviour>()
                    : null;

            return IsSameSpellTarget(hitTarget, senses.Target);
        }

        private bool HasClearPathToShootProjectileSinglePlayer(float speed, float originDistance, float radius)
        {
            Vector3 sphereCastDir = senses.PredictNextTargetPos(speed);
            if (sphereCastDir == EnemySenses.ResetPlayerPos)
                return false;

            float sphereCastDist = (sphereCastDir - transform.position).magnitude;
            sphereCastDir = (sphereCastDir - transform.position).normalized;

            bool myColliderWasEnabled = false;
            if (myCollider)
            {
                myColliderWasEnabled = myCollider.enabled;
                myCollider.enabled = false;
            }

            Vector3 shootOrigin = transform.position + sphereCastDir * originDistance;
            bool isSpaceInsufficient = Physics.CheckSphere(shootOrigin, radius, ignoreMaskForShooting);

            if (myCollider)
                myCollider.enabled = myColliderWasEnabled;

            if (isSpaceInsufficient)
                return false;

            RaycastHit hit;
            if (Physics.SphereCast(shootOrigin, radius, sphereCastDir, out hit, sphereCastDist, ignoreMaskForShooting))
            {
                DaggerfallEntityBehaviour hitTarget = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
                return hitTarget == senses.Target;
            }

            return true;
        }


        /// <summary>
        /// Returns true if can shoot bow at target.
        /// </summary>
        bool CanShootBow()
        {
            if (!hasBowAttack)
                return false;

            if (!IsNetworkActive())
                return HasClearPathToShootProjectile(35f, 0f, 0.15f);

            DaggerfallEntityBehaviour currentTarget = senses != null ? senses.Target : null;
            if (currentTarget == null)
                return false;

            if (currentTarget != cachedBowPathTarget || Time.time >= nextBowPathCheckTime)
            {
                cachedBowPathTarget = currentTarget;
                cachedBowPathClear = HasClearPathToShootProjectile(35f, 0f, 0.15f);
                nextBowPathCheckTime = Time.time + MP_PROJECTILE_PATH_CACHE_INTERVAL;
            }

            return cachedBowPathClear;
        }

        /// <summary>
        /// Selects a ranged spell from this enemy's list and returns true if it can be cast.
        /// </summary>
        bool CanCastRangedSpell()
        {
            if (entity == null || entity.CurrentMagicka <= 0)
                return false;

            // Keep the original DFU selection behaviour in pure SP.
            if (!IsNetworkActive())
            {
                EffectBundleSettings[] spells = entity.GetSpells();
                List<EffectBundleSettings> rangeSpells = new List<EffectBundleSettings>();
                int count = 0;

                foreach (EffectBundleSettings spell in spells)
                {
                    if (spell.TargetType == TargetTypes.SingleTargetAtRange ||
                        spell.TargetType == TargetTypes.AreaAtRange)
                    {
                        rangeSpells.Add(spell);
                        count++;
                    }
                }

                if (count == 0)
                    return false;

                EffectBundleSettings selectedSpellSettings =
                    rangeSpells[Random.Range(0, count)];
                SelectedSpell =
                    new EntityEffectBundle(selectedSpellSettings, entityBehaviour);

                if (EffectsAlreadyOnTarget(SelectedSpell))
                    return false;

                return HasClearPathToShootProjectile(
                    25f,
                    DaggerfallMissile.ArmLength,
                    0.45f);
            }

            EnsureSpellSettingsCache();
            if (cachedRangedSpellSettings.Length == 0)
                return false;

            DaggerfallEntityBehaviour currentTarget = senses != null ? senses.Target : null;
            if (currentTarget == null)
                return false;

            bool enhancedAI = DaggerfallUnity.Settings.EnhancedCombatAI;
            if (currentTarget == cachedRangedSpellTarget &&
                enhancedAI == cachedRangedSpellEnhancedAI &&
                Time.time < nextRangedSpellEligibilityTime)
            {
                return cachedRangedSpellEligibility;
            }

            cachedRangedSpellTarget = currentTarget;
            cachedRangedSpellEnhancedAI = enhancedAI;
            nextRangedSpellEligibilityTime =
                Time.time + MP_SPELL_ELIGIBILITY_CACHE_INTERVAL;
            cachedRangedSpellEligibility = false;
            SelectedSpell = null;

            EffectBundleSettings selectedSettings =
                cachedRangedSpellSettings[
                    Random.Range(0, cachedRangedSpellSettings.Length)];

            EntityEffectBundle selectedBundle =
                new EntityEffectBundle(selectedSettings, entityBehaviour);

            if (EffectsAlreadyOnTarget(selectedBundle))
                return false;

            if (!HasClearPathToShootProjectile(
                    25f,
                    DaggerfallMissile.ArmLength,
                    0.45f))
            {
                return false;
            }

            SelectedSpell = selectedBundle;
            cachedRangedSpellEligibility = true;
            return true;
        }

        /// <summary>
        /// Selects a touch spell from this enemy's list and returns true if it can be cast.
        /// </summary>
        bool CanCastTouchSpell()
        {
            if (entity == null || entity.CurrentMagicka <= 0)
                return false;

            // Keep the original DFU selection behaviour in pure SP.
            if (!IsNetworkActive())
            {
                EffectBundleSettings[] spells = entity.GetSpells();
                List<EffectBundleSettings> rangeSpells =
                    new List<EffectBundleSettings>();
                int count = 0;

                foreach (EffectBundleSettings spell in spells)
                {
                    if (!DaggerfallUnity.Settings.EnhancedCombatAI)
                    {
                        if (spell.TargetType == TargetTypes.ByTouch ||
                            spell.TargetType == TargetTypes.CasterOnly)
                        {
                            rangeSpells.Add(spell);
                            count++;
                        }
                    }
                    else
                    {
                        if (spell.TargetType == TargetTypes.ByTouch ||
                            spell.TargetType == TargetTypes.AreaAroundCaster)
                        {
                            rangeSpells.Add(spell);
                            count++;
                        }
                    }
                }

                if (count == 0)
                    return false;

                EffectBundleSettings selectedSpellSettings =
                    rangeSpells[Random.Range(0, count)];
                SelectedSpell =
                    new EntityEffectBundle(selectedSpellSettings, entityBehaviour);

                if (EffectsAlreadyOnTarget(SelectedSpell))
                    return false;

                return true;
            }

            EnsureSpellSettingsCache();

            bool enhancedAI = DaggerfallUnity.Settings.EnhancedCombatAI;
            EffectBundleSettings[] touchSettings =
                enhancedAI
                    ? cachedEnhancedTouchSpellSettings
                    : cachedClassicTouchSpellSettings;

            if (touchSettings.Length == 0)
                return false;

            DaggerfallEntityBehaviour currentTarget = senses != null ? senses.Target : null;
            if (currentTarget == null)
                return false;

            if (currentTarget == cachedTouchSpellTarget &&
                enhancedAI == cachedTouchSpellEnhancedAI &&
                Time.time < nextTouchSpellEligibilityTime)
            {
                return cachedTouchSpellEligibility;
            }

            cachedTouchSpellTarget = currentTarget;
            cachedTouchSpellEnhancedAI = enhancedAI;
            nextTouchSpellEligibilityTime =
                Time.time + MP_SPELL_ELIGIBILITY_CACHE_INTERVAL;
            cachedTouchSpellEligibility = false;
            SelectedSpell = null;

            EffectBundleSettings selectedSettings =
                touchSettings[Random.Range(0, touchSettings.Length)];

            EntityEffectBundle selectedBundle =
                new EntityEffectBundle(selectedSettings, entityBehaviour);

            if (EffectsAlreadyOnTarget(selectedBundle))
                return false;

            SelectedSpell = selectedBundle;
            cachedTouchSpellEligibility = true;
            return true;
        }

        /// <summary>
        /// Checks if enemy can fly based on behaviour.
        /// This can change in the case of a transformed Seducer.
        /// </summary>
        /// <returns>True if enemy can fly.</returns>
        bool CanFly()
        {
            return mobile.Enemy.Behaviour == MobileBehaviour.Flying || mobile.Enemy.Behaviour == MobileBehaviour.Spectral;
        }

        /// <summary>
        /// Checks whether the target already is affected by all of the effects of the given spell.
        /// </summary>
public bool EffectsAlreadyOnTarget(EntityEffectBundle spell)
{
    if (spell == null || senses == null || senses.Target == null)
        return false;

    DaggerfallEntityBehaviour currentTarget = senses.Target;
    PlayerMultiplayer targetMultiplayer =
        currentTarget.GetComponent<PlayerMultiplayer>();

    if (targetMultiplayer == null)
        targetMultiplayer =
            currentTarget.GetComponentInParent<PlayerMultiplayer>();

    EntityEffectManager targetEffectManager = null;

    if (targetMultiplayer != null)
    {
        // Only this machine's local PlayerAdvanced has the authoritative effect manager.
        // A remote PlayerMultiplayer shell normally has none; that is expected and must not
        // emit an error every spell eligibility check.
        if (targetMultiplayer.isLocalPlayer)
        {
            DaggerfallEntityBehaviour localPlayer =
                GameManager.Instance != null
                    ? GameManager.Instance.PlayerEntityBehaviour
                    : null;

            if (localPlayer != null)
                targetEffectManager =
                    localPlayer.GetComponent<EntityEffectManager>();
        }
        else
        {
            return false;
        }
    }
    else
    {
        targetEffectManager =
            currentTarget.GetComponent<EntityEffectManager>();
    }

    if (targetEffectManager == null)
        return false;

    LiveEffectBundle[] bundles = targetEffectManager.EffectBundles;
    for (int i = 0; i < spell.Settings.Effects.Length; i++)
    {
        bool foundEffect = false;
        IEntityEffect effectTemplate =
            GameManager.Instance.EntityEffectBroker.GetEffectTemplate(
                spell.Settings.Effects[i].Key);

        if (effectTemplate == null)
            return false;

        for (int j = 0; j < bundles.Length && !foundEffect; j++)
        {
            for (int k = 0;
                 k < bundles[j].liveEffects.Count && !foundEffect;
                 k++)
            {
                if (bundles[j].liveEffects[k].GetType() ==
                    effectTemplate.GetType())
                {
                    foundEffect = true;
                }
            }
        }

        if (!foundEffect)
            return false;
    }

    return true;
}

        /// <summary>
        /// Try to move in given direction.
        /// </summary>
        void AttemptMove(Vector3 direction, float moveSpeed, bool backAway = false, bool strafe = false, float strafeDist = 0)
        {
            // Set whether pursuing or retreating, for bypassing changeStateTimer delay when continuing these actions
            if (!backAway && !strafe)
            {
                pursuing = true;
                retreating = false;
            }
            else
            {
                retreating = true;
                pursuing = false;
            }

            if (!senses.TargetIsWithinYawAngle(5.625f, destination))
            {
                TurnToTarget(direction);
                // Classic always turns in place. Enhanced only does so if enemy is not in sight,
                // for more natural-looking movement while pursuing.
                if (!DaggerfallUnity.Settings.EnhancedCombatAI || !senses.TargetInSight)
                    return;
            }

            if (backAway)
                direction *= -1;

            if (strafe)
            {
                Vector3 strafeDest = new Vector3(destination.x + (Mathf.Sin(strafeAngle) * strafeDist), transform.position.y, destination.z + (Mathf.Cos(strafeAngle) * strafeDist));
                direction = (strafeDest - transform.position).normalized;

                if ((strafeDest - transform.position).magnitude <= 0.2f)
                {
                    if (strafeLeft)
                        strafeAngle++;
                    else
                        strafeAngle--;
                }
            }

            // Move downward some to eliminate bouncing down inclines
            if (!flies && !swims && !IsLevitating && controller.isGrounded)
                direction.y = -2f;

            // Stop fliers from moving too near the floor during combat
            if (flies && avoidObstaclesTimer <= 0 && direction.y < 0 && FindGroundPosition((originalHeight / 2) + 1f) != transform.position)
                direction.y = 0.1f;

            Vector3 motion = direction * moveSpeed;

            // If using enhanced combat, avoid moving directly below targets
            if (!backAway && DaggerfallUnity.Settings.EnhancedCombatAI && avoidObstaclesTimer <= 0)
            {
                bool withinPitch = senses.TargetIsWithinPitchAngle(45.0f);
                if (!pausePursuit && !withinPitch)
                {
                    if (flies || IsLevitating || swims)
                    {
                        if (!senses.TargetIsAbove())
                            motion = -transform.up * moveSpeed / 2;
                        else
                            motion = transform.up * moveSpeed;
                    }
                    // Causes a random delay after being out of pitch range
                    else if (senses.TargetIsAbove() && changeStateTimer <= 0)
                    {
                        SetChangeStateTimer();
                        pausePursuit = true;
                    }
                }
                else if (withinPitch)
                {
                    pausePursuit = false;
                    backingUp = false;
                }

                if (pausePursuit)
                {
                    if (senses.TargetIsAbove() && !senses.TargetIsWithinPitchAngle(55.0f) && (changeStateTimer <= 0 || backingUp))
                    {
                        // Back away from target
                        motion = -transform.forward * moveSpeed * 0.75f;
                        backingUp = true;
                    }
                    else
                    {
                        // Stop moving
                        backingUp = false;
                        return;
                    }
                }
            }

            SetChangeStateTimer();

            // Check if there is something to collide with directly in movement direction, such as upward sloping ground.
            Vector3 direction2d = direction;
            if (!flies && !swims && !IsLevitating)
                direction2d.y = 0;
            ObstacleCheck(direction2d);
            FallCheck(direction2d);

            if (fallDetected || ObstacleDetected)
            {
                if (!strafe && !backAway)
                    FindDetour(direction2d);
            }
            else
            // Clear to move
            {
                if (swims)
                    WaterMove(motion);
                else
                    controller.Move(motion * Time.deltaTime);
            }
        }

        /// <summary>
        /// Try to find a way around an obstacle or fall.
        /// </summary>
        void FindDetour(Vector3 direction2d)
        {
            float angle;
            Vector3 testMove = Vector3.zero;
            bool foundUpDown = false;

            // Try up/down first
            if (flies || swims || IsLevitating)
            {
                float multiplier = 0.3f;
                if (Random.Range(0, 2) == 0)
                    multiplier = -0.3f;

                Vector3 upOrDown = new Vector3(0, 1, 0);
                upOrDown.y *= multiplier;

                testMove = (direction2d + upOrDown).normalized;

                ObstacleCheck(testMove);
                if (ObstacleDetected)
                {
                    upOrDown.y *= -1;
                    testMove = (direction2d + upOrDown).normalized;
                    ObstacleCheck(testMove);
                }
                if (!ObstacleDetected)
                    foundUpDown = true;
            }

            // Reset clockwise check if we've been clear of obstacles/falls for a while
            if (!foundUpDown && Time.time - lastTimeWasStuck > 2f)
            {
                checkingClockwiseTimer = 0;
                didClockwiseCheck = false;
            }

            if (!foundUpDown && checkingClockwiseTimer <= 0)
            {
                if (!didClockwiseCheck)
                {
                    // Check 45 degrees in both ways first
                    // Pick first direction to check randomly
                    if (Random.Range(0, 2) == 0)
                        angle = 45;
                    else
                        angle = -45;

                    testMove = Quaternion.AngleAxis(angle, Vector3.up) * direction2d;
                    ObstacleCheck(testMove);
                    FallCheck(testMove);

                    if (!ObstacleDetected && !fallDetected)
                    {
                        // First direction was clear, use that way
                        if (angle == 45)
                        {
                            checkingClockwise = true;
                        }
                        else
                            checkingClockwise = false;
                    }
                    else
                    {
                        // Tested 45 degrees in the clockwise/counter-clockwise direction we chose,
                        // but hit something, so try other one.
                        angle *= -1;
                        testMove = Quaternion.AngleAxis(angle, Vector3.up) * direction2d;
                        ObstacleCheck(testMove);
                        FallCheck(testMove);

                        if (!ObstacleDetected && !fallDetected)
                        {
                            if (angle == 45)
                            {
                                checkingClockwise = true;
                            }
                            else
                                checkingClockwise = false;
                        }
                        else
                        {
                            // Both 45 degrees checks failed, pick clockwise/counterclockwise based on angle to target
                            Vector3 toTarget = destination - transform.position;
                            Vector3 directionToTarget = toTarget.normalized;
                            angle = Vector3.SignedAngle(directionToTarget, direction2d, Vector3.up);

                            if (angle > 0)
                            {
                                checkingClockwise = true;
                            }
                            else
                                checkingClockwise = false;
                        }
                    }
                    checkingClockwiseTimer = 5;
                    didClockwiseCheck = true;
                }
                else
                {
                    didClockwiseCheck = false;
                    checkingClockwise = !checkingClockwise;
                    checkingClockwiseTimer = 5;
                }
            }

            angle = 0;
            int count = 0;

            if (!foundUpDown)
            {
                do
                {
                    if (checkingClockwise)
                        angle += 45;
                    else
                        angle -= 45;

                    testMove = Quaternion.AngleAxis(angle, Vector3.up) * direction2d;
                    ObstacleCheck(testMove);
                    FallCheck(testMove);

                    // Break out of loop if can't find anywhere to go
                    count++;
                    if (count > 7)
                    {
                        break;
                    }
                }
                while (ObstacleDetected || fallDetected);
            }

            detourDestination = transform.position + testMove * 2;

            if (avoidObstaclesTimer <= 0)
                avoidObstaclesTimer = 0.75f;
            lastTimeWasStuck = Time.time;
        }

        void ObstacleCheck(Vector3 direction)
        {
            ObstacleDetected = false;
            // Rationale: follow walls at 45° incidence; is that optimal? At least it seems very good
            float checkDistance = controller.radius / Mathf.Sqrt(2f);
            foundUpwardSlope = false;
            foundDoor = false;

            RaycastHit hit;
            // Climbable/not climbable step for the player seems to be at around a height of 0.65f. The player is 1.8f tall.
            // Using the same ratio to height as these values, set the capsule for the enemy. 
            Vector3 p1 = transform.position + (Vector3.up * -originalHeight * 0.1388F);
            Vector3 p2 = p1 + (Vector3.up * Mathf.Min(originalHeight, doorCrouchingHeight) / 2);

            if (Physics.CapsuleCast(p1, p2, controller.radius / 2, direction, out hit, checkDistance, ignoreMaskForObstacles))
            {
                // Debug.DrawRay(transform.position, direction, Color.red, 2.0f);
                ObstacleDetected = true;
                DaggerfallEntityBehaviour entityBehaviour2 = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
                DaggerfallActionDoor door = hit.transform.GetComponent<DaggerfallActionDoor>();
                DaggerfallLoot loot = hit.transform.GetComponent<DaggerfallLoot>();

                if (entityBehaviour2)
                {
                    if (entityBehaviour2 == senses.Target)
                        ObstacleDetected = false;
                }
                else if (door)
                {
                    ObstacleDetected = false;
                    foundDoor = true;
                    if (senses.TargetIsWithinYawAngle(22.5f, door.transform.position))
                    {
                        senses.LastKnownDoor = door;
                        senses.DistanceToDoor = Vector3.Distance(transform.position, door.transform.position);
                    }
                }
                else if (loot)
                {
                    ObstacleDetected = false;
                }
                else if (!swims && !flies && !IsLevitating)
                {
                    // If an obstacle was hit, check for a climbable upward slope
                    Vector3 checkUp = transform.position + direction;
                    checkUp.y++;

                    direction = (checkUp - transform.position).normalized;
                    p1 = transform.position + (Vector3.up * -originalHeight * 0.25f);
                    p2 = p1 + (Vector3.up * originalHeight * 0.75f);

                    if (!Physics.CapsuleCast(p1, p2, controller.radius / 2, direction, checkDistance))
                    {
                        ObstacleDetected = false;
                        foundUpwardSlope = true;
                    }
                }
            }
            else
            {
                // Debug.DrawRay(transform.position, direction, Color.green, 2.0f);
            }
        }

        void FallCheck(Vector3 direction)
        {
            if (flies || IsLevitating || swims || ObstacleDetected || foundUpwardSlope || foundDoor)
            {
                fallDetected = false;
                return;
            }

            int checkDistance = 1;
            Vector3 rayOrigin = transform.position;

            direction *= checkDistance;
            Ray ray = new Ray(rayOrigin + direction, Vector3.down);
            RaycastHit hit;

            fallDetected = !Physics.Raycast(ray, out hit, (originalHeight * 0.5f) + 1.5f);
        }

        /// <summary>
        /// Decide whether or not to pursue enemy, based on perceived combat odds.
        /// </summary>
        void EvaluateMoveInForAttack()
        {
            // Classic always attacks
            if (!DaggerfallUnity.Settings.EnhancedCombatAI)
            {
                moveInForAttack = true;
                return;
            }

            // No retreat from unseen opponent
            if (!senses.TargetInSight)
            {
                moveInForAttack = true;
                return;
            }
			

            // No retreat if enemy is paralyzed
if (senses.Target != null)
{
    // 🔹 Default behavior (Singleplayer & AI logic)
    EntityEffectManager targetEffectManager = senses.Target.GetComponent<EntityEffectManager>();

    // 🔹 **If the target is PlayerMultiplayer, get the real PlayerAdvance**
    PlayerMultiplayer targetMultiplayer = senses.Target.GetComponent<PlayerMultiplayer>();
    if (targetMultiplayer != null && targetMultiplayer.isLocalPlayer)
    {
        if (DEBUG_ENEMY_MOTOR) Debug.Log($"[EvaluateMoveInForAttack] Redirecting EntityEffectManager checks to PlayerAdvance for {senses.Target.name} (NetID: {targetMultiplayer.netId})");
        targetEffectManager = GameManager.Instance.PlayerEntityBehaviour.GetComponent<EntityEffectManager>();
    }

    if (targetEffectManager != null && targetEffectManager.FindIncumbentEffect<MagicAndEffects.MagicEffects.Paralyze>() != null)
    {
        if (DEBUG_ENEMY_MOTOR) Debug.Log($"[EvaluateMoveInForAttack] Target {senses.Target.name} is paralyzed. Moving in for attack.");
        moveInForAttack = true;
        return;
    }

                // No retreat if enemy is player with bow or weapon not out
                if (senses.Target == GameManager.Instance.PlayerEntityBehaviour
                    && GameManager.Instance.WeaponManager.ScreenWeapon
                    && (GameManager.Instance.WeaponManager.ScreenWeapon.WeaponType == WeaponTypes.Bow
                    || !GameManager.Instance.WeaponManager.ScreenWeapon.ShowWeapon))
                {
                    moveInForAttack = true;
                    return;
                }
            }
            else
            {
                return;
            }

            const float retreatDistanceBaseMult = 2.25f;

            // Level difference affects likelihood of backing away.
            moveInForAttackTimer = Random.Range(1, 3);
            int levelMod = (entity.Level - senses.Target.Entity.Level) / 2;
            if (levelMod > 4)
                levelMod = 4;
            if (levelMod < -4)
                levelMod = -4;

            int roll = Random.Range(0 + levelMod, 10 + levelMod);

            moveInForAttack = roll > 4;

            // Chose to retreat
            if (!moveInForAttack)
            {
                retreatDistanceMultiplier = (float)(retreatDistanceBaseMult + (retreatDistanceBaseMult * (0.25 * (2 - roll))));

                if (!DaggerfallUnity.Settings.EnhancedCombatAI)
                    return;

                if (Random.Range(0, 2) == 0)
                    strafeLeft = true;
                else
                    strafeLeft = false;

                Vector3 north = destination;
                north.z++; // Adding 1 to z so this Vector3 will be north of the destination Vector3.

                // Get angle between vector from destination to the north of it, and vector from destination to this enemy's position
                strafeAngle = Vector3.SignedAngle(destination - north, destination - transform.position, Vector3.up);
                if (strafeAngle < 0)
                    strafeAngle = 360 + strafeAngle;

                // Convert to radians
                strafeAngle *= Mathf.PI / 180;
            }
        }

        /// <summary>
        /// Set timer for padding between state changes, for non-perfect reflexes.
        /// </summary>
        void SetChangeStateTimer()
        {
            // No timer without enhanced AI
            if (!DaggerfallUnity.Settings.EnhancedCombatAI)
                return;

            if (changeStateTimer <= 0)
                changeStateTimer = Random.Range(0.2f, .8f);
        }

        private bool TryGetAquaticWaterSurfaceY(out float waterSurfaceY)
        {
            waterSurfaceY = 0f;

            if (Time.time < nextAquaticWaterRefreshTime)
            {
                if (cachedAquaticWaterValid)
                {
                    waterSurfaceY = cachedAquaticWaterSurfaceY;
                    return true;
                }

                return false;
            }

            nextAquaticWaterRefreshTime = Time.time + AQUATIC_WATER_REFRESH_INTERVAL;
            cachedAquaticWaterValid = false;
            cachedAquaticWaterSurfaceY = 0f;

            DaggerfallWorkshop.DaggerfallDungeon currentDungeon = FindCurrentDungeonForAquaticWater();
            if (currentDungeon != null)
            {
                int blockIndex = currentDungeon.GetPlayerBlockIndex(transform.position);
                if (blockIndex >= 0)
                {
                    float visualWaterY;
                    if (TryGetVisualDungeonWaterSurfaceY(currentDungeon, blockIndex, out visualWaterY))
                    {
                        cachedAquaticWaterValid = true;
                        cachedAquaticWaterSurfaceY = visualWaterY;
                        waterSurfaceY = visualWaterY;
                        return true;
                    }

                    DaggerfallConnect.DFLocation.DungeonBlock blockData;
                    if (currentDungeon.GetBlockData(blockIndex, out blockData) && blockData.WaterLevel != 10000)
                    {
                        waterSurfaceY = GetDungeonBaseYForAquaticWater(currentDungeon) + (blockData.WaterLevel * -1 * MeshReader.GlobalScale);
                        cachedAquaticWaterValid = true;
                        cachedAquaticWaterSurfaceY = waterSurfaceY;
                        return true;
                    }
                }

                // If block matching failed, still prefer the nearest actual DungeonWater object
                // in this dungeon. A fish that has already climbed slightly out of the water can
                // fail block-sensitive lookup, but the real water plane is still the safest clamp.
                float nearestVisualWaterY;
                if (TryGetNearestVisualDungeonWaterSurfaceY(currentDungeon, out nearestVisualWaterY))
                {
                    cachedAquaticWaterValid = true;
                    cachedAquaticWaterSurfaceY = nearestVisualWaterY;
                    waterSurfaceY = nearestVisualWaterY;
                    return true;
                }
            }

            // Final MP fallback: scan all live network/SP dungeon water planes and pick the
            // closest one by Y and XZ. This catches already-escaped aquatic enemies whose
            // current position no longer maps cleanly to a dungeon block.
            float globalVisualWaterY;
            if (TryGetNearestVisualDungeonWaterSurfaceY(null, out globalVisualWaterY))
            {
                cachedAquaticWaterValid = true;
                cachedAquaticWaterSurfaceY = globalVisualWaterY;
                waterSurfaceY = globalVisualWaterY;
                return true;
            }

            // Fallback for normal singleplayer-style cases. This is intentionally last because
            // PlayerEnterExit.blockWaterLevel describes the local player's current block, not
            // necessarily this enemy's block once MP enemies are moved to the scene root.
            PlayerEnterExit enterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (enterExit != null && enterExit.blockWaterLevel != 10000)
            {
                float baseY = enterExit.Dungeon != null ? GetDungeonBaseYForAquaticWater(enterExit.Dungeon) : 0f;
                waterSurfaceY = baseY + (enterExit.blockWaterLevel * -1 * MeshReader.GlobalScale);
                cachedAquaticWaterValid = true;
                cachedAquaticWaterSurfaceY = waterSurfaceY;
                return true;
            }

            return false;
        }

        private DaggerfallWorkshop.DaggerfallDungeon FindCurrentDungeonForAquaticWater()
        {
            DaggerfallWorkshop.DaggerfallDungeon bestDungeon = null;
            float bestYDistance = float.MaxValue;

            DaggerfallWorkshop.DaggerfallDungeon[] dungeons = FindObjectsOfType<DaggerfallWorkshop.DaggerfallDungeon>();
            for (int i = 0; i < dungeons.Length; i++)
            {
                DaggerfallWorkshop.DaggerfallDungeon candidate = dungeons[i];
                if (candidate == null)
                    continue;

                int blockIndex = candidate.GetPlayerBlockIndex(transform.position);
                if (blockIndex < 0)
                    continue;

                float yDistance = Mathf.Abs(transform.position.y - GetDungeonBaseYForAquaticWater(candidate));
                if (yDistance < bestYDistance)
                {
                    bestYDistance = yDistance;
                    bestDungeon = candidate;
                }
            }

            return bestDungeon;
        }

        private float GetDungeonBaseYForAquaticWater(DaggerfallWorkshop.DaggerfallDungeon dungeon)
        {
            if (dungeon == null)
                return 0f;

            if (Mathf.Abs(dungeon.PositionY) > 0.01f)
                return dungeon.PositionY;

            return dungeon.transform.position.y;
        }

        private bool TryGetVisualDungeonWaterSurfaceY(DaggerfallWorkshop.DaggerfallDungeon dungeon, int blockIndex, out float waterSurfaceY)
        {
            waterSurfaceY = 0f;

            if (dungeon == null || blockIndex < 0)
                return false;

            DaggerfallConnect.DFLocation.DungeonBlock blockData;
            if (!dungeon.GetBlockData(blockIndex, out blockData) || blockData.WaterLevel == 10000)
                return false;

            float xMin = dungeon.transform.position.x + blockData.X * RDBLayout.RDBSide;
            float xMax = xMin + RDBLayout.RDBSide;
            float zMin = dungeon.transform.position.z + blockData.Z * RDBLayout.RDBSide;
            float zMax = zMin + RDBLayout.RDBSide;

            Transform[] children = dungeon.GetComponentsInChildren<Transform>(true);
            Transform bestWater = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child.name.IndexOf("DungeonWater", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Vector3 pos = child.position;
                if (pos.x < xMin - 1f || pos.x > xMax + 1f || pos.z < zMin - 1f || pos.z > zMax + 1f)
                    continue;

                Vector2 flatDelta = new Vector2(pos.x - transform.position.x, pos.z - transform.position.z);
                float distance = flatDelta.sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestWater = child;
                }
            }

            if (bestWater == null)
                return false;

            waterSurfaceY = bestWater.position.y;
            return true;
        }

        private bool TryGetNearestVisualDungeonWaterSurfaceY(DaggerfallWorkshop.DaggerfallDungeon preferredDungeon, out float waterSurfaceY)
        {
            waterSurfaceY = 0f;

            DaggerfallWorkshop.DaggerfallDungeon[] dungeons;
            if (preferredDungeon != null)
                dungeons = new DaggerfallWorkshop.DaggerfallDungeon[] { preferredDungeon };
            else
                dungeons = FindObjectsOfType<DaggerfallWorkshop.DaggerfallDungeon>();

            Transform bestWater = null;
            float bestScore = float.MaxValue;

            for (int d = 0; d < dungeons.Length; d++)
            {
                DaggerfallWorkshop.DaggerfallDungeon dungeon = dungeons[d];
                if (dungeon == null)
                    continue;

                Transform[] children = dungeon.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    Transform child = children[i];
                    if (child == null || child.name.IndexOf("DungeonWater", System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    Vector3 pos = child.position;
                    Vector2 flatDelta = new Vector2(pos.x - transform.position.x, pos.z - transform.position.z);

                    // Prefer the same Y-slot very strongly so stacked network dungeons at the same
                    // X/Z do not steal each other's water plane. X/Z then chooses the nearest block.
                    float yDelta = Mathf.Abs(pos.y - transform.position.y);
                    float score = (yDelta * 10000f) + flatDelta.sqrMagnitude;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestWater = child;
                    }
                }
            }

            if (bestWater == null)
                return false;

            waterSurfaceY = bestWater.position.y;
            return true;
        }

        private float GetAquaticMaxCenterY(float waterSurfaceY)
        {
            // Keep aquatic enemies just under the actual water surface.
            // Do not use the old 100 * MeshReader.GlobalScale probe as a center clamp here;
            // that pushes slaughterfish too deep into slopes/floors and can stop movement.
            return waterSurfaceY - AQUATIC_WATER_SURFACE_PADDING;
        }

        private void ForceSetAquaticPosition(Vector3 position)
        {
            if (controller != null)
            {
                bool wasEnabled = controller.enabled;
                controller.enabled = false;
                transform.position = position;
                controller.enabled = wasEnabled;
            }
            else
            {
                transform.position = position;
            }
        }

        private void ClampAquaticEnemyToWaterSurface(Vector3 fallbackPosition)
        {
            if (!swims)
                return;

            float waterSurfaceY;
            if (!TryGetAquaticWaterSurfaceY(out waterSurfaceY))
                return;

            float maxAquaticCenterY = GetAquaticMaxCenterY(waterSurfaceY);
            if (transform.position.y <= maxAquaticCenterY + AQUATIC_HARD_CLAMP_TOLERANCE)
                return;

            // First try a normal CharacterController correction. If the fish is only slightly
            // above the water this preserves collision behaviour.
            if (controller != null && controller.enabled)
            {
                controller.Move(new Vector3(0f, maxAquaticCenterY - transform.position.y, 0f));
                if (transform.position.y <= maxAquaticCenterY + AQUATIC_HARD_CLAMP_TOLERANCE)
                    return;
            }

            // If a ramp/ledge collision prevents moving back down, restore the previous valid
            // water position when possible. Otherwise hard snap vertically below the surface.
            if (fallbackPosition.y <= maxAquaticCenterY + AQUATIC_HARD_CLAMP_TOLERANCE)
                ForceSetAquaticPosition(new Vector3(fallbackPosition.x, Mathf.Min(fallbackPosition.y, maxAquaticCenterY), fallbackPosition.z));
            else
                ForceSetAquaticPosition(new Vector3(transform.position.x, maxAquaticCenterY, transform.position.z));

            LastGroundedY = transform.position.y;
            Falls = false;
        }

        /// <summary>
        /// Movement for water enemies.
        /// </summary>
        void WaterMove(Vector3 motion)
        {
            if (!IsNetworkActive())
            {
                if (GameManager.Instance.PlayerEnterExit.blockWaterLevel != 10000
                    && controller.transform.position.y < GameManager.Instance.PlayerEnterExit.blockWaterLevel * -1 * MeshReader.GlobalScale)
                {
                    if (motion.y > 0 && controller.transform.position.y + (100 * MeshReader.GlobalScale)
                        >= GameManager.Instance.PlayerEnterExit.blockWaterLevel * -1 * MeshReader.GlobalScale)
                    {
                        motion.y = 0;
                    }

                    controller.Move(motion * Time.deltaTime);
                }

                return;
            }

            float waterSurfaceY;
            if (!TryGetAquaticWaterSurfaceY(out waterSurfaceY))
                return;

            Vector3 beforeMove = transform.position;

            // Original DFU used blockWaterLevel directly, which only works when the dungeon
            // is at classic Y=0 and the enemy is in the same block as PlayerEnterExit. In MP,
            // aquatic enemies are root network objects inside offset dungeons, so compare
            // against the actual water surface for this enemy's current dungeon/block.
            float maxAquaticCenterY = GetAquaticMaxCenterY(waterSurfaceY);
            float surfaceProbeHeight = waterSurfaceY - maxAquaticCenterY;
            float deltaTime = Time.deltaTime > 0f ? Time.deltaTime : 0.02f;

            // Do not allow upward movement to cross the water surface.
            if (motion.y > 0f)
            {
                float nextProbeY = controller.transform.position.y + motion.y * deltaTime + surfaceProbeHeight;
                if (nextProbeY >= waterSurfaceY)
                    motion.y = Mathf.Min(motion.y, (maxAquaticCenterY - controller.transform.position.y) / deltaTime);
            }

            // If already above the allowed aquatic center height, actively push back down.
            // Swimming enemies do not use gravity, so otherwise a previous wrong water clamp
            // or a target above water can leave them floating/flying permanently.
            if (controller.transform.position.y > maxAquaticCenterY)
                motion.y = Mathf.Min(motion.y, -Mathf.Max(4f, Mathf.Abs(motion.y)));

            controller.Move(motion * deltaTime);
            ClampAquaticEnemyToWaterSurface(beforeMove);
        }

        /// <summary>
        /// Rotate toward target.
        /// </summary>
        void TurnToTarget(Vector3 targetDirection)
        {
            const float turnSpeed = 20f;
            //Classic speed is 11.25f, too slow for Daggerfall Unity's agile player movement

            if (GameManager.ClassicUpdate)
            {
                transform.forward = Vector3.RotateTowards(transform.forward, targetDirection, turnSpeed * Mathf.Deg2Rad, 0.0f);
            }
        }

        /// <summary>
        /// Set to either idle or move animation depending on whether the enemy has moved or rotated.
        /// </summary>
        void UpdateToIdleOrMoveAnim()
        {
            if (!mobile.IsPlayingOneShot())
            {
                // Rotation is done at classic update rate, so check at classic update rate
                if (GameManager.ClassicUpdate)
                {
                    Vector3 currentDirection = transform.forward;
                    currentDirection.y = 0;
                    rotating = lastDirection != currentDirection;
                    lastDirection = currentDirection;
                }
                // Movement is done at regular update rate, so check position at regular update rate
                if (!rotating && lastPosition == transform.position)
                    mobile.ChangeEnemyState(MobileStates.Idle);
                else
                    mobile.ChangeEnemyState(MobileStates.Move);
            }

            lastPosition = transform.position;
        }

        void ApplyFallDamage()
        {
            if (NetworkClient.active && !isServer && (!hasAuthority || Time.time < clientGravitySuppressedUntil))
            {
                Falls = false;
                LastGroundedY = transform.position.y;
                return;
            }

            // Assuming the same formula is used for the player and enemies
            const float fallingDamageThreshold = 5.0f;
            const float HPPerMetre = 5f;

            if (controller.isGrounded)
            {
                // did enemy just land?
                if (Falls)
                {
                    float fallDistance = LastGroundedY - transform.position.y;
                    if (fallDistance > fallingDamageThreshold)
                    {
                        int damage = (int)(HPPerMetre * (fallDistance - fallingDamageThreshold));

                        EnemyEntity enemyEntity = entityBehaviour.Entity as EnemyEntity;
                        int traceHpBefore = enemyEntity != null ? enemyEntity.CurrentHealth : -999;
                        NetworkIdentity traceNi = cachedNetworkIdentity != null ? cachedNetworkIdentity : GetComponent<NetworkIdentity>();
                        uint traceNetId = traceNi != null ? traceNi.netId : 0U;

                        Debug.LogWarning(
                            $"[EnemyDeathTrace][FallDamageBefore] enemy='{gameObject.name}' netId={traceNetId} " +
                            $"hp={traceHpBefore} damage={damage} fallDistance={fallDistance:0.000} " +
                            $"lastGroundedY={LastGroundedY:0.000} currentY={transform.position.y:0.000} " +
                            $"server={isServer} client={isClient} authority={hasAuthority}");

                        enemyEntity.DecreaseHealth(damage);

                        Debug.LogWarning(
                            $"[EnemyDeathTrace][FallDamageAfter] enemy='{gameObject.name}' netId={traceNetId} " +
                            $"hpBefore={traceHpBefore} hpAfter={(enemyEntity != null ? enemyEntity.CurrentHealth : -999)} " +
                            $"damage={damage} fallDistance={fallDistance:0.000}");

                        if (entityBlood)
                        {
                            // Like in classic, falling enemies bleed at the center. It must hurt the center of mass ;)
                            entityBlood.ShowBloodSplash(0, transform.position);
                        }

                        DaggerfallUI.Instance.DaggerfallAudioSource.PlayClipAtPoint((int)SoundClips.FallDamage, FindGroundPosition());
                    }
                }

                LastGroundedY = transform.position.y;
            }
            // For flying enemies, "lastGroundedY" is really "lastAltitudeControlY"
            else if ((flies && !flyerFalls) || IsLevitating || entity.IsSlowFalling)
                LastGroundedY = transform.position.y;

        }


        /// <summary>
        /// Open doors that are in the way.
        /// </summary>
        void OpenDoors()
        {
            // Try to open doors blocking way
            if (mobile.Enemy.CanOpenDoors)
            {
                if (senses.LastKnownDoor != null
                    && senses.DistanceToDoor < OpenDoorDistance && !senses.LastKnownDoor.IsOpen
                    && !senses.LastKnownDoor.IsLocked)
                {
                    senses.LastKnownDoor.ToggleDoor();
                    return;
                }

                // If door didn't open, and we are trying to get to the target, bash
                Bashing = DaggerfallUnity.Settings.EnhancedCombatAI && !senses.TargetInSight && moveInForAttack
                    && senses.LastKnownDoor != null && senses.DistanceToDoor <= attack.MeleeDistance && senses.LastKnownDoor.IsLocked;
            }
        }

        /// <summary>
        /// Limits maximum controller height.
        /// Tall sprites require this hack to get through doors.
        /// </summary>
        void HeightAdjust()
        {
            // If enemy bumps into something, temporarily reduce their height to 1.65, which should be short enough to fit through most if not all doorways.
            // Unfortunately, while the enemy is shortened, projectiles will not collide with the top of the enemy for the difference in height.
            if (!resetHeight && controller && ((controller.collisionFlags & CollisionFlags.CollidedSides) != 0) && originalHeight > doorCrouchingHeight)
            {
                // Adjust the center of the controller so that sprite doesn't sink into the ground
                centerChange = (doorCrouchingHeight - controller.height) / 2;
                Vector3 newCenter = controller.center;
                newCenter.y += centerChange;
                controller.center = newCenter;
                // Adjust the height
                controller.height = doorCrouchingHeight;
                resetHeight = true;
                heightChangeTimer = 0.5f;
            }
            else if (resetHeight && heightChangeTimer <= 0)
            {
                // Restore the original center
                Vector3 newCenter = controller.center;
                newCenter.y -= centerChange;
                controller.center = newCenter;
                // Restore the original height
                controller.height = originalHeight;
                resetHeight = false;
            }

            if (resetHeight && heightChangeTimer > 0)
            {
                heightChangeTimer -= Time.deltaTime;
            }
        }
        #endregion
    }
}
