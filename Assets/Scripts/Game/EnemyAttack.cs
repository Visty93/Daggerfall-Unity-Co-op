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
using DaggerfallConnect;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Utility;
using Mirror;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Temporary enemy attack.
    /// </summary>
    [RequireComponent(typeof(EnemySenses))]
    public class EnemyAttack : NetworkBehaviour
    {
        public const float minRangedDistance = 240 * MeshReader.GlobalScale; // 6m
        public const float maxRangedDistance = 2048 * MeshReader.GlobalScale; // 51.2m
        public float MeleeDistance = 2.25f;                // Maximum distance for melee attack
        public float ClassicMeleeDistanceVsAI = 1.5f;      // Maximum distance for melee attack vs other AI in classic AI mode
        public float MeleeTimer = 0;                       // Must be 0 for a melee attack or touch spell to be done
        public DaggerfallMissile ArrowMissilePrefab;

        EnemyMotor motor;
        EnemySenses senses;
        EnemySounds sounds;
        MobileUnit mobile;
        DaggerfallEntityBehaviour entityBehaviour;
        int damage = 0;

        // Server resolves each hit ONCE from the current simulator, then forwards
        // player damage to the victim's own real PlayerEntity. Observer animation
        // frames never run their own stale EnemySenses hit detection.
        private NetworkIdentity cachedAttackIdentity;
        private DynamicEnemyAuthority cachedAttackAuthority;
        private float lastServerOwnerHitAt = -999f;
        private float lastServerMeleeAnimationAt = -999f;
        private uint nextOwnerSwingId = 1;
        private uint activeOwnerSwingId;
        private uint lastReportedOwnerSwingId;
        private uint activeOwnerTargetNetId;
        private DaggerfallEntityBehaviour activeOwnerSwingTarget;
        private bool ownerHasActiveTarget;
        private float ownerSwingStartedAt;
        private uint lastServerSwingId;
        private NetworkConnection lastServerSwingConnection;
        private uint serverApprovedSwingId;
        private uint serverApprovedSwingTargetId;
        private float serverApprovedSwingAt = -999f;
        private const float MinServerMeleeHitInterval = 0.25f;

        private bool IsSpawnedNetworkEnemy()
        {
            if (!NetworkClient.active && !NetworkServer.active)
                return false;
            if (cachedAttackIdentity == null)
                cachedAttackIdentity = GetComponent<NetworkIdentity>();
            return cachedAttackIdentity != null && cachedAttackIdentity.netId != 0;
        }

        private bool IsOwnerSimulator()
        {
            if (!IsSpawnedNetworkEnemy())
                return true;
            if (motor == null)
                motor = GetComponent<EnemyMotor>();
            if (motor != null && !motor.ShouldSimulateMotor())
                return false;
            if (cachedAttackAuthority == null)
                cachedAttackAuthority = GetComponent<DynamicEnemyAuthority>();
            return cachedAttackAuthority == null || !cachedAttackAuthority.IsAuthorityDeactivatedForRetention;
        }

        private uint nextOwnerArrowId = 1;
        private bool ownerBowAnimationPending;

        public void PrepareOwnerBowAttack()
        {
            TrackCombatOwnership();
            ownerBowAnimationPending = IsOwnerSimulator();
        }
        private int ownerArrowEpoch;
        private bool combatOwnerKnown;
        private bool previousCombatOwner;
        private sealed class OwnerArrowRecord
        {
            public NetworkConnectionToClient Owner;
            public float Expires;
        }
        private readonly System.Collections.Generic.Dictionary<uint, OwnerArrowRecord> ownerArrows =
            new System.Collections.Generic.Dictionary<uint, OwnerArrowRecord>();

        // DynamicEnemyAuthority calls this at tick suspension/restoration boundaries.
        // Record ownership loss even when Update will no longer run on this observer.
        public void RefreshObserverCombatOwnership()
        {
            if (IsSpawnedNetworkEnemy()) TrackCombatOwnership();
            if (mobile != null)
            {
                mobile.DoMeleeDamage = false;
                mobile.ShootArrow = false;
            }
        }

        private void TrackCombatOwnership()
        {
            bool owner = IsOwnerSimulator();
            if (!combatOwnerKnown || owner != previousCombatOwner)
            {
                combatOwnerKnown = true;
                previousCombatOwner = owner;
                ownerArrowEpoch++;
                ownerHasActiveTarget = false;
                ownerBowAnimationPending = false;
                activeOwnerSwingId = 0;
                if (mobile != null)
                {
                    mobile.DoMeleeDamage = false;
                    mobile.ShootArrow = false;
                }
            }
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();
            ownerArrowEpoch++;
            ownerBowAnimationPending = false;
            ownerHasActiveTarget = false;
            activeOwnerSwingId = 0;
        }

        private void ShootOwnerArrow()
        {
            if (!IsOwnerSimulator() || !ownerBowAnimationPending || entityBehaviour == null || ArrowMissilePrefab == null) return;
            ownerBowAnimationPending = false;
            uint shot = nextOwnerArrowId++;
            if (nextOwnerArrowId == 0) nextOwnerArrowId = 1;
            int epoch = ownerArrowEpoch;
            DaggerfallMissile arrow = Instantiate(ArrowMissilePrefab);
            arrow.Caster = entityBehaviour;
            arrow.TargetType = TargetTypes.SingleTargetAtRange;
            arrow.ElementType = ElementTypes.None;
            arrow.IsArrow = true;
            Vector3 origin, aim;
            arrow.GetOwnerLaunchAim(out origin, out aim);
            arrow.CustomAimPosition = origin;
            arrow.CustomAimDirection = aim;
            arrow.OwnerDrivenEnemyProjectile = true;
            arrow.OwnerSimulationValid = () => this != null && epoch == ownerArrowEpoch &&
                IsSpawnedNetworkEnemy() && IsOwnerSimulator() && entityBehaviour != null && entityBehaviour.Entity != null && entityBehaviour.Entity.CurrentHealth > 0;
            arrow.OwnerImpact = victim => ReportOwnerArrowImpact(shot, victim);
            if (isServer) ServerOwnerArrowLaunch(shot, origin - transform.position, aim, null);
            else CmdOwnerArrowLaunch(shot, origin - transform.position, aim);
        }

        [Command]
        private void CmdOwnerArrowLaunch(uint shot, Vector3 offset, Vector3 aim,
            NetworkConnectionToClient sender = null)
        {
            ServerOwnerArrowLaunch(shot, offset, aim, sender);
        }

        private bool IsCurrentArrowSender(NetworkConnectionToClient sender)
        {
            return isServer && (sender == null ? IsOwnerSimulator() : netIdentity.connectionToClient == sender);
        }

        [Server]
        private void ServerOwnerArrowLaunch(uint shot, Vector3 offset, Vector3 aim, NetworkConnectionToClient sender)
        {
            if (!IsCurrentArrowSender(sender) || shot == 0 ||
                float.IsNaN(offset.sqrMagnitude) || float.IsInfinity(offset.sqrMagnitude) ||
                offset.sqrMagnitude > 100f || float.IsNaN(aim.sqrMagnitude) ||
                float.IsInfinity(aim.sqrMagnitude) || aim.sqrMagnitude < 0.001f) return;
            var expired = new System.Collections.Generic.List<uint>();
            foreach (var pair in ownerArrows)
                if (pair.Value.Expires < Time.unscaledTime || pair.Value.Owner != sender)
                    expired.Add(pair.Key);
            foreach (uint id in expired) ownerArrows.Remove(id);
            if (ownerArrows.ContainsKey(shot) || ownerArrows.Count >= 64) return;
            ownerArrows.Add(shot, new OwnerArrowRecord { Owner = sender, Expires = Time.unscaledTime + 12f });
            RpcOwnerArrowVisual(offset, aim.normalized);
        }

        [ClientRpc]
        private void RpcOwnerArrowVisual(Vector3 offset, Vector3 aim)
        {
            if (IsOwnerSimulator() || ArrowMissilePrefab == null) return;
            var presentation = GetComponent<DynamicEnemyAuthority>();
            if (presentation != null && presentation.IsLocalPresentationSuspended) return;
            DaggerfallMissile arrow = Instantiate(ArrowMissilePrefab);
            arrow.Caster = GetComponent<DaggerfallEntityBehaviour>();
            arrow.TargetType = TargetTypes.SingleTargetAtRange;
            arrow.ElementType = ElementTypes.None;
            arrow.IsArrow = true;
            arrow.VisualOnly = true;
            arrow.OwnerDrivenEnemyVisual = true;
            arrow.CustomAimPosition = transform.position + offset;
            arrow.CustomAimDirection = aim;
            DaggerfallAudioSource source = GetComponent<DaggerfallAudioSource>();
            if (source) source.PlayOneShot((int)SoundClips.ArrowShoot, 1, 1.0f);
        }

        private void ReportOwnerArrowImpact(uint shot, DaggerfallEntityBehaviour victim)
        {
            if (!IsOwnerSimulator()) return;
            victim = DaggerfallMissile.ResolveEnemyCombatVictim(victim);
            NetworkIdentity identity = victim != null ? victim.GetComponent<NetworkIdentity>() : null;
            if (identity == null || (identity.netId == 0 && victim.GetComponent<PlayerMultiplayer>() == null)) return;
            if (isServer) ServerOwnerArrowImpact(shot, identity.netId, null);
            else CmdOwnerArrowImpact(shot, identity.netId);
        }

        [Command]
        private void CmdOwnerArrowImpact(uint shot, uint victimId, NetworkConnectionToClient sender = null)
        {
            ServerOwnerArrowImpact(shot, victimId, sender);
        }

        [Server]
        private void ServerOwnerArrowImpact(uint shot, uint victimId, NetworkConnectionToClient sender)
        {
            OwnerArrowRecord record;
            if (!IsCurrentArrowSender(sender) || !ownerArrows.TryGetValue(shot, out record) ||
                record.Owner != sender || record.Expires < Time.unscaledTime) return;
            ownerArrows.Remove(shot); // One collision result per launched arrow.
            DaggerfallEntityBehaviour victim = ServerResolveCombatTarget(victimId);
            ServerResolveOwnerHit(victim, true, shot, sender);
        }

        void Start()
        {
            motor = GetComponent<EnemyMotor>();
            senses = GetComponent<EnemySenses>();
            sounds = GetComponent<EnemySounds>();
            mobile = GetComponent<DaggerfallEnemy>().MobileUnit;
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
        }

        void FixedUpdate()
        {
            if (IsSpawnedNetworkEnemy()) TrackCombatOwnership();
            const int speedFloor = 8;

            // Unable to attack if AI disabled or paralyzed
            if (GameManager.Instance.DisableAI || entityBehaviour.Entity.IsParalyzed)
                return;

            // Unable to attack when playing certain oneshot anims
            if (mobile && mobile.IsPlayingOneShot() && mobile.OneShotPauseActionsWhilePlaying())
                return;

            // Countdown to next melee attack
            MeleeTimer -= Time.deltaTime;

            if (MeleeTimer < 0)
                MeleeTimer = 0;

            // Get entity speed and enforce a lower limit so Drain Speed does not prevent attack ever firing
            EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
            int speed = entity.Stats.LiveSpeed;
            if (speed < speedFloor)
                speed = speedFloor;

            // Slow down enemy frame rate based on floored speed value
            // If enemy is still at maximum speed then divisor is 1 and will experience no change to frame rate
            mobile.FrameSpeedDivisor = entity.Stats.PermanentSpeed / speed;

            // The enemy owner (including a pure client) initiates melee.
            // Never run attack decisions on a remote observer or on the server
            // copy of an enemy owned by a client.
            if (!IsOwnerSimulator())
                return;

            // Note: Speed comparison here is reversed from classic. Classic's way makes fewer attack
            // attempts at higher speeds, so it seems backwards.
            if (GameManager.ClassicUpdate && (DFRandom.rand() % speed >= (speed >> 3) + 6 && MeleeTimer == 0))
            {
                if (!MeleeAnimation())
                    return;

                ResetMeleeTimer();
            }
        }

        void Update()
        {
            if (IsSpawnedNetworkEnemy()) TrackCombatOwnership();
            // Unable to attack if paralyzed
            if (entityBehaviour.Entity.IsParalyzed)
                return;

            // If a melee attack has reached the damage frame we can run a melee attempt
            if (mobile.DoMeleeDamage)
            {
                // Animation callbacks fire on all observers. Only the owning
                // simulator may turn one into an authoritative hit request.
                if (IsOwnerSimulator())
                    MeleeDamage();
                mobile.DoMeleeDamage = false;
            }
            // If a bow attack has reached the shoot frame we can shoot an arrow
            else if (mobile.ShootArrow)
            {
                bool mayShoot = !IsSpawnedNetworkEnemy() || (IsOwnerSimulator() && ownerBowAnimationPending);
                if (mayShoot) ShootBow();
                mobile.ShootArrow = false;
                if (!mayShoot) return;

                DaggerfallAudioSource dfAudioSource = GetComponent<DaggerfallAudioSource>();
                if (dfAudioSource)
                    dfAudioSource.PlayOneShot((int)SoundClips.ArrowShoot, 1, 1.0f);
            }
        }

        public void ResetMeleeTimer()
        {
            MeleeTimer = Random.Range(1500, 3000 + 1);
            MeleeTimer -= 50 * (GameManager.Instance.PlayerEntity.Level - 10);

            // Note: In classic, what happens here is
            // meleeTimer += 450 * (enemydata[130] - 2);
            // Looks like this was meant to reference the game reflexes setting,
            // which is stored in playerentitydata[130].
            // Instead enemydata[130] seems to instead always be 0, the equivalent of
            // "very high" reflexes, regardless of what the game reflexes are.
            // Here, we use the reflexes data as was intended.
            MeleeTimer += 450 * ((int)GameManager.Instance.PlayerEntity.Reflexes - 2);

            if (MeleeTimer < 0)
                MeleeTimer = 0;

            MeleeTimer /= 980; // Approximates classic frame update
        }

       public void BowDamage(Vector3 direction)
{
    if (senses == null || senses.Target == null)
        return;

    if (IsSpawnedNetworkEnemy())
    {
        if (!IsOwnerSimulator()) return;
        PlayerMultiplayer victimPlayer = senses.Target.GetComponent<PlayerMultiplayer>();
        if (victimPlayer != null)
        {
            NetworkIdentity targetIdentity = victimPlayer.GetComponent<NetworkIdentity>();
            if (targetIdentity == null) return;
            if (isServer)
                ServerResolveOwnerHit(senses.Target, true, 0, null);
            else if (hasAuthority)
                CmdReportOwnerBowPlayerHit(targetIdentity.netId);
            return;
        }
        // The existing server-authoritative enemy-vs-enemy bow Command
        // remains intact below; observers must not run any hit path.
    }

    EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
    PlayerMultiplayer targetMultiplayer = senses.Target.GetComponent<PlayerMultiplayer>();
    DaggerfallEntityBehaviour targetEntity = senses.Target.GetComponent<DaggerfallEntityBehaviour>();

    // 🔹 Single-Player Case: Directly check if attacking the local player
    if (senses.Target == GameManager.Instance.PlayerEntityBehaviour)
    {
        Debug.Log($"[BowDamage] Single-player: Enemy is attacking the player.");
        damage = ApplyDamageToPlayer(entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand));
    }
    // 🔹 Multiplayer Case: Redirect to PlayerMultiplayer but only apply damage locally
    else if (targetMultiplayer != null)
    {
        Debug.Log($"[BowDamage] Redirecting arrow damage from PlayerMultiplayer to actual player entity.");
        targetEntity = targetMultiplayer.GetComponent<DaggerfallEntityBehaviour>(); // Ensure we get the real entity

        if (targetMultiplayer.isLocalPlayer)
        {
            Debug.Log($"[BowDamage] Applying damage to Player (NetID: {targetMultiplayer?.netId})");
            damage = ApplyDamageToPlayer(entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand));
        }
        else
        {
            Debug.Log($"[BowDamage] Enemy arrow hit PlayerMultiplayer {targetMultiplayer.netId}, but this is NOT my local player. Ignoring.");
        }
    }
    // 🔹 Apply damage to non-player entities (NPCs, enemies)
    else
    {
        Items.DaggerfallUnityItem weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
        NetworkIdentity targetIdentity = targetEntity != null ? targetEntity.GetComponent<NetworkIdentity>() : null;

        // Server/SP can calculate and apply bow damage directly.
        if (isServer || !NetworkClient.active)
        {
            Debug.Log($"[BowDamage] Applying server/SP bow damage to non-player entity: {senses.Target.name}");
            damage = ApplyDamageToExplicitNonPlayerTarget(targetEntity, weapon, direction, true, -1, true);

            // In MP, tell observing clients to play the same hit/miss effects against the explicit target.
            if (isServer && NetworkServer.active && targetIdentity != null)
                RpcApplyEnemyHitEffectsExplicit(targetIdentity.netId, damage, true);
        }
        // Client-owned enemy bow hits are not authoritative. Ask the server to validate and apply damage.
        else if (hasAuthority && NetworkClient.active && targetIdentity != null)
        {
            Debug.Log($"[BowDamage] Client-owned enemy '{entityBehaviour.name}' bow-hit enemy '{senses.Target.name}'. Requesting server ranged infighting damage.");
            CmdApplyEnemyBowInfightingDamage(targetIdentity.netId, direction);
        }
        else
        {
            Debug.Log($"[BowDamage] Non-authority client observed bow hit on '{senses.Target.name}', ignoring local damage.");
        }
    }

    // 🔹 Arrow item logic remains unchanged
    Items.DaggerfallUnityItem arrow = Items.ItemBuilder.CreateWeapon(Items.Weapons.Arrow, Items.WeaponMaterialTypes.None);
    arrow.stackCount = 1;
    senses.Target.Entity.Items.AddItem(arrow);
}


        #region Private Methods

     private bool MeleeAnimation()
     {
         if (!IsOwnerSimulator())
             return false;
         // ChangeEnemyState does nothing when already in this state or frozen.
         // Do not overwrite an active swing ID or spend another cooldown then.
         if (IsSpawnedNetworkEnemy() && (mobile == null || mobile.FreezeAnims || mobile.IsPlayingOneShot()))
             return false;

         if (!senses.TargetInSight || !senses.TargetIsWithinYawAngle(22.5f, senses.LastKnownTargetPos))
             return false;

         float distance = MeleeDistance;
         if (!DaggerfallUnity.Settings.EnhancedCombatAI &&
             senses.Target != GameManager.Instance.PlayerEntityBehaviour)
             distance = ClassicMeleeDistanceVsAI;
         if (senses.DistanceToTarget > distance + senses.TargetRateOfApproach)
             return false;

         global::EnemySwingDiagnostics.RecordAcceptedMelee(this);
         if (sounds)
             sounds.PlayAttackSound();

         if (IsSpawnedNetworkEnemy())
         {
             NetworkIdentity targetIdentity = senses.Target != null
                 ? senses.Target.GetComponent<NetworkIdentity>() : null;
             ownerHasActiveTarget = targetIdentity != null;
             activeOwnerSwingTarget = ownerHasActiveTarget ? senses.Target : null;
             activeOwnerTargetNetId = ownerHasActiveTarget ? targetIdentity.netId : 0;
             activeOwnerSwingId = nextOwnerSwingId++;
             if (nextOwnerSwingId == 0) nextOwnerSwingId = 1;
             ownerSwingStartedAt = Time.unscaledTime;

             // The player shell can have netId 0 on the listen host; that is
             // still a valid explicit player target for the server resolver.
             if (isServer)
             {
                 lastServerMeleeAnimationAt = Time.unscaledTime;
                 serverApprovedSwingId = activeOwnerSwingId;
                 serverApprovedSwingTargetId = activeOwnerTargetNetId;
                 serverApprovedSwingAt = Time.unscaledTime;
                 mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
                 global::EnemySwingDiagnostics.RecordAttackRpc(this, "SERVER_SEND");
                 RpcPlayAttackAnimation();
             }
             else if (hasAuthority)
             {
                 // Immediate owner animation for responsiveness. The server
                 // relays it only to observers after validating ownership.
                 mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
                 CmdRelayOwnerMeleeAnimation(activeOwnerTargetNetId, activeOwnerSwingId);
             }
         }
         else
         {
             // Original vanilla/console-local path, no network event required.
             mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
         }
         return true;
     }

     [Command(requiresAuthority = true)]
     private void CmdRelayOwnerMeleeAnimation(uint targetNetId, uint swingId,
         NetworkConnectionToClient sender = null)
     {
         if (!isServer || sender == null || swingId == 0 ||
             GetComponent<NetworkIdentity>().connectionToClient != sender ||
             Time.unscaledTime - lastServerMeleeAnimationAt < 0.25f)
             return;
         lastServerMeleeAnimationAt = Time.unscaledTime;
         serverApprovedSwingId = swingId;
         serverApprovedSwingTargetId = targetNetId;
         serverApprovedSwingAt = Time.unscaledTime;
         // Server records the event; listen-host presentation runs in the RPC.
         // Dedicated servers do not need an attack animation to validate a hit.
         global::EnemySwingDiagnostics.RecordAttackRpc(this, "SERVER_SEND");
         RpcPlayAttackAnimation();
     }

     [ClientRpc]
     private void RpcPlayAttackAnimation()
     {
         if (IsOwnerSimulator())
             return; // Owner started it locally; non-owner host is an observer too.
         if (mobile == null) mobile = GetComponentInChildren<MobileUnit>();
         if (mobile == null || !mobile.IsSetup) return;
         global::EnemySwingDiagnostics.RecordAttackRpc(this, "CLIENT_RECEIVE");
         mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
     }


private void MeleeDamage()
{
    if (IsSpawnedNetworkEnemy())
    {
        ReportOwnerMeleeHit();
        return;
    }

    // Unspawned/console/SP actors preserve their original local damage path.
    if (entityBehaviour == null)
    {
        Debug.LogWarning($"[EnemyAttack] MeleeDamage() called but entityBehaviour is NULL! ({gameObject.name})");
        return;
    }

    EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
    if (entity == null)
    {
        Debug.LogWarning($"[EnemyAttack] {entityBehaviour.name} has no valid EnemyEntity.");
        return;
    }

    if (senses == null)
    {
        Debug.LogWarning($"[EnemyAttack] {entityBehaviour.name} has NULL senses component!");
        return;
    }

    if (senses.Target == null)
    {
        Debug.LogWarning($"[EnemyAttack] {entityBehaviour.name} tried to attack but target is NULL!");
        return;
    }

    PlayerMultiplayer targetMultiplayer = senses.Target.GetComponent<PlayerMultiplayer>();
    DaggerfallEntityBehaviour targetEntity = senses.Target.GetComponent<DaggerfallEntityBehaviour>();

    Debug.Log($"[EnemyAttack] {entityBehaviour.name} is attempting a melee attack on {senses.Target.name}");

    // Determine the weapon used
    Items.DaggerfallUnityItem weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
    if (weapon != null && targetEntity != null && targetEntity.Entity is EnemyEntity targetEnemyEntity &&
        targetEnemyEntity.MobileEnemy.MinMetalToHit > (Items.WeaponMaterialTypes)weapon.NativeMaterialValue)
    {
        Debug.Log($"[EnemyAttack] {entityBehaviour.name} switching to hand-to-hand (weapon ineffective)");
        weapon = null; // Switch to hand-to-hand if weapon is ineffective
    }

    damage = 0; // Reset damage each attack cycle

    // Melee hit detection logic
    bool isWithinMeleeRange = senses.DistanceToTarget <= 0.25f ||
        (senses.DistanceToTarget <= MeleeDistance && senses.TargetIsWithinYawAngle(35.156f, senses.Target.transform.position));

    if (senses.TargetInSight && isWithinMeleeRange)
    {
        if (senses.Target == GameManager.Instance.PlayerEntityBehaviour)
        {
            // **Singleplayer & Multiplayer Mode: Apply damage normally to the local player**
            Debug.Log($"[EnemyAttack] {entityBehaviour.name} is attacking the local player.");
            damage = ApplyDamageToPlayer(weapon);
        }
        else if (targetMultiplayer != null)
        {
            // **Multiplayer Mode: Only apply damage if this is MY PlayerMultiplayer**
            if (targetMultiplayer.isLocalPlayer)
            {
                Debug.Log($"[EnemyAttack] {entityBehaviour.name} detected my PlayerMultiplayer (NetID: {targetMultiplayer.netId}), applying damage locally.");
                damage = ApplyDamageToPlayer(weapon);
            }
            else
            {
                Debug.Log($"[EnemyAttack] {entityBehaviour.name} is attacking PlayerMultiplayer {targetMultiplayer.netId}, but this is NOT my local player. Ignoring.");
            }
        }

else
{
    // Enemy infighting.
    //
    // Server / SP can apply damage directly.
    // Client-owned enemies can play the attack locally, but their local damage is not authoritative.
    // In that case, ask the server to calculate and apply the damage against the explicit target netId.
    if (isServer || !NetworkClient.active)
    {
        Debug.Log($"[EnemyAttack] {entityBehaviour.name} is attacking another enemy. Calculating damage.");
        damage = ApplyDamageToNonPlayer(weapon, transform.forward);

        // Always send the RPC, even if damage = 0, so observers get hit/miss effects.
        NetworkIdentity targetIdentity = senses.Target.GetComponent<NetworkIdentity>();
        if (targetIdentity != null)
        {
            Debug.Log($"[EnemyAttack] {entityBehaviour.name} hit {senses.Target.name} with {damage} damage. Syncing hit/miss effects.");
            RpcApplyEnemyHitEffects(targetIdentity.netId, weapon, damage);
        }
    }
    else if (hasAuthority && NetworkClient.active)
    {
        NetworkIdentity targetIdentity = senses.Target.GetComponent<NetworkIdentity>();
        if (targetIdentity != null)
        {
            Debug.Log($"[EnemyAttack] Client-owned enemy '{entityBehaviour.name}' hit enemy '{senses.Target.name}'. Requesting server infighting damage.");
            CmdApplyEnemyInfightingDamage(targetIdentity.netId, transform.forward);
        }
    }
}


        if (damage > 0)
        {
            Debug.Log($"[EnemyAttack] {entityBehaviour.name} successfully dealt {damage} damage to {senses.Target.name}!");
        }
        else
        {
            Debug.LogWarning($"[EnemyAttack] {entityBehaviour.name} ATTACKED but dealt **NO DAMAGE** to {senses.Target.name}!");
        }
    }
}



// Owner-only hit reporting. No observer EnemySenses is consulted for melee.
private void ReportOwnerMeleeHit()
{
    if (!IsOwnerSimulator() || !ownerHasActiveTarget || activeOwnerSwingTarget == null ||
        Time.unscaledTime - ownerSwingStartedAt > 3f ||
        activeOwnerSwingId == 0 || activeOwnerSwingId == lastReportedOwnerSwingId)
        return;

    // The owner chose this target at the animation's START, after checking
    // sensing / LOS / facing. The damage frame may arrive several frames later
    // after senses updates to a different target. Never rely on an observer's
    // current senses, or on a mutable owner target, to route the swing.
    NetworkIdentity targetIdentity = activeOwnerSwingTarget.GetComponent<NetworkIdentity>();
    if (targetIdentity == null || activeOwnerTargetNetId != targetIdentity.netId)
        return;

    uint swingId = activeOwnerSwingId;
    lastReportedOwnerSwingId = swingId;
    if (isServer)
        ServerResolveOwnerHit(activeOwnerSwingTarget, false, swingId, null);
    else if (hasAuthority)
        CmdReportOwnerMeleeHit(activeOwnerTargetNetId, swingId);
}

[Command(requiresAuthority = true)]
private void CmdReportOwnerMeleeHit(uint targetNetId, uint swingId,
    NetworkConnectionToClient sender = null)
{
    if (!isServer || sender == null || swingId == 0)
        return;
    NetworkIdentity attacker = GetComponent<NetworkIdentity>();
    if (attacker == null || attacker.connectionToClient != sender)
        return;
    // A client may report the damage frame only for an animation the server
    // previously accepted from that same current owner. Commands are reliable
    // and ordered; ownership changes invalidate the sender check above.
    if (serverApprovedSwingId != swingId ||
        serverApprovedSwingTargetId != targetNetId ||
        Time.unscaledTime - serverApprovedSwingAt > 3f)
        return;
    // Each server-approved swing can submit exactly one damage report.
    // Consume it even if the server later rejects the hit as out-of-range.
    serverApprovedSwingAt = -999f;
    DaggerfallEntityBehaviour targetEntity = ServerResolveCombatTarget(targetNetId);
    ServerResolveOwnerHit(targetEntity, false, swingId, sender);
}

[Command(requiresAuthority = true)]
private void CmdReportOwnerBowPlayerHit(uint targetNetId, NetworkConnectionToClient sender = null)
{
    if (!isServer || sender == null)
        return;
    NetworkIdentity attacker = GetComponent<NetworkIdentity>();
    if (attacker == null || attacker.connectionToClient != sender)
        return;
    ServerResolveOwnerHit(ServerResolveCombatTarget(targetNetId), true, 0, sender);
}

[Server]
private DaggerfallEntityBehaviour ServerResolveCombatTarget(uint targetNetId)
{
    NetworkIdentity identity;
    if (targetNetId != 0 && NetworkServer.spawned.TryGetValue(targetNetId, out identity) && identity != null)
        return identity.GetComponent<DaggerfallEntityBehaviour>();

    // Existing project allows host player netId 0. Resolve it without
    // mistaking it for a missing enemy ID.
    if (targetNetId == 0 && NetworkServer.localConnection != null &&
        NetworkServer.localConnection.identity != null)
    {
        PlayerMultiplayer hostPM = NetworkServer.localConnection.identity.GetComponent<PlayerMultiplayer>();
        if (hostPM != null)
            return hostPM.GetComponent<DaggerfallEntityBehaviour>();
    }
    return null;
}

[Server]
private void ServerResolveOwnerHit(DaggerfallEntityBehaviour victim, bool ranged,
    uint swingId, NetworkConnectionToClient sender)
{
    if (!isServer || victim == null || victim == entityBehaviour || victim.Entity == null)
        return;
    if (entityBehaviour == null) entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
    if (entityBehaviour == null || !(entityBehaviour.Entity is EnemyEntity attackerEnemy))
        return;

    // Throttle and de-duplicate at the server, even across late animation
    // callbacks and ownership transitions. The identity Command permission
    // prevents observers from submitting somebody else's attacks.
    float now = Time.unscaledTime;
    if (!ranged && now - lastServerOwnerHitAt < MinServerMeleeHitInterval)
        return;
    if (!ranged && sender == lastServerSwingConnection && swingId == lastServerSwingId)
        return;

    float allowed = ranged ? 300f :
        Mathf.Max(MeleeDistance, ClassicMeleeDistanceVsAI) + 1.25f;
    if ((transform.position - victim.transform.position).sqrMagnitude > allowed * allowed)
        return;

    PlayerMultiplayer victimPlayer = victim.GetComponent<PlayerMultiplayer>();
    if (victimPlayer != null)
    {
        if (victimPlayer.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive ||
            victimPlayer.IsDownedForRevive)
            return;
        NetworkConnection victimConnection = victimPlayer.connectionToClient;
        // Listen host's physical PlayerAdvanced is available only on the host.
        if (victimPlayer.isLocalPlayer && NetworkServer.active && NetworkClient.active)
        {
            Items.DaggerfallUnityItem hostWeapon = attackerEnemy.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
            ApplyDamageToPlayer(hostWeapon);
        }
        else if (victimConnection != null && victimConnection.isReady)
        {
            TargetApplyOwnerHitToPlayer(victimConnection, ranged);
        }
        else
            return;
    }
    else if (victim.Entity is EnemyEntity victimEnemy)
    {
        if (senses == null) senses = GetComponent<EnemySenses>();
        if (senses != null && senses.IsQuestInfightingTargetBlocked(victim))
            return;
        Items.DaggerfallUnityItem weapon = attackerEnemy.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
        if (weapon != null && victimEnemy.MobileEnemy.MinMetalToHit >
            (Items.WeaponMaterialTypes)weapon.NativeMaterialValue)
            weapon = null;
        int applied = ApplyDamageToExplicitNonPlayerTarget(victim, weapon,
            transform.forward, ranged, -1, true);
        NetworkIdentity victimId = victim.GetComponent<NetworkIdentity>();
        if (victimId != null && victimId.netId != 0)
            RpcApplyEnemyHitEffectsExplicit(victimId.netId, applied, ranged);
    }
    else
        return;

    if (!ranged)
    {
        lastServerOwnerHitAt = now;
        lastServerSwingId = swingId;
        lastServerSwingConnection = sender;
        serverApprovedSwingAt = -999f;
    }
}

// The victim applies its own normal DFU damage/dodge/guard/enchanted-strike
// mechanics. No enemy sight/target data is needed on the victim's machine.
[TargetRpc]
private void TargetApplyOwnerHitToPlayer(NetworkConnection target, bool ranged)
{
    PlayerMultiplayer local = PlayerMultiplayer.GetLocalPlayer();
    if (local == null || !local.isLocalPlayer ||
        GameManager.Instance == null || GameManager.Instance.PlayerEntity == null ||
        GameManager.Instance.PlayerEntity.CurrentHealth <= 0)
        return;
    if (entityBehaviour == null) entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
    if (entityBehaviour == null || !(entityBehaviour.Entity is EnemyEntity attackerEntity))
        return;
    Items.DaggerfallUnityItem weapon = attackerEntity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
    ApplyDamageToPlayer(weapon);
}

[Command(requiresAuthority = true)]
private void CmdApplyEnemyInfightingDamage(uint targetNetId, Vector3 direction)
{
    if (!isServer)
        return;

    if (entityBehaviour == null)
        entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

    if (entityBehaviour == null || !(entityBehaviour.Entity is EnemyEntity attackerEntity))
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyInfightingDamage] Missing attacker entity on '{gameObject.name}'.");
        return;
    }

    if (!NetworkServer.spawned.TryGetValue(targetNetId, out NetworkIdentity targetIdentity) || targetIdentity == null)
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyInfightingDamage] Target netId={targetNetId} not found on server.");
        return;
    }

    DaggerfallEntityBehaviour explicitTarget = targetIdentity.GetComponent<DaggerfallEntityBehaviour>();
    if (explicitTarget == null || explicitTarget == entityBehaviour || !(explicitTarget.Entity is EnemyEntity targetEnemyEntity))
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyInfightingDamage] Invalid enemy target netId={targetNetId}.");
        return;
    }

    // Server-side sanity check. The owner client may have produced the attack frame, but the server
    // still validates that both enemies are close enough to make an infighting hit plausible.
    float serverDistance = Vector3.Distance(transform.position, explicitTarget.transform.position);
    float allowedDistance = Mathf.Max(MeleeDistance, ClassicMeleeDistanceVsAI) + 0.75f;
    if (serverDistance > allowedDistance)
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyInfightingDamage] Rejected hit from '{name}' to '{explicitTarget.name}' distance={serverDistance:F2} allowed={allowedDistance:F2}.");
        return;
    }

    Items.DaggerfallUnityItem weapon = attackerEntity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
    if (weapon != null && targetEnemyEntity.MobileEnemy.MinMetalToHit > (Items.WeaponMaterialTypes)weapon.NativeMaterialValue)
        weapon = null;

    int serverDamage = ApplyDamageToExplicitNonPlayerTarget(explicitTarget, weapon, direction, false, -1, true);

    Debug.Log($"[EnemyAttack][CmdApplyEnemyInfightingDamage] '{entityBehaviour.name}' hit '{explicitTarget.name}' damage={serverDamage}.");
    RpcApplyEnemyHitEffectsExplicit(targetNetId, serverDamage, false);
}


[Command(requiresAuthority = true)]
private void CmdApplyEnemyBowInfightingDamage(uint targetNetId, Vector3 direction)
{
    if (!isServer)
        return;

    if (entityBehaviour == null)
        entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

    if (entityBehaviour == null || !(entityBehaviour.Entity is EnemyEntity attackerEntity))
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyBowInfightingDamage] Missing attacker entity on '{gameObject.name}'.");
        return;
    }

    if (!NetworkServer.spawned.TryGetValue(targetNetId, out NetworkIdentity targetIdentity) || targetIdentity == null)
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyBowInfightingDamage] Target netId={targetNetId} not found on server.");
        return;
    }

    DaggerfallEntityBehaviour explicitTarget = targetIdentity.GetComponent<DaggerfallEntityBehaviour>();
    if (explicitTarget == null || explicitTarget == entityBehaviour || !(explicitTarget.Entity is EnemyEntity))
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyBowInfightingDamage] Invalid enemy target netId={targetNetId}.");
        return;
    }

    // Server-side sanity check. This is intentionally looser than melee because the arrow
    // hit was detected on the owning client, but still prevents obviously impossible hits.
    float serverDistance = Vector3.Distance(transform.position, explicitTarget.transform.position);
    float allowedDistance = maxRangedDistance + 2.0f;
    if (serverDistance > allowedDistance)
    {
        Debug.LogWarning($"[EnemyAttack][CmdApplyEnemyBowInfightingDamage] Rejected ranged hit from '{name}' to '{explicitTarget.name}' distance={serverDistance:F2} allowed={allowedDistance:F2}.");
        return;
    }

    Items.DaggerfallUnityItem weapon = attackerEntity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);

    int serverDamage = ApplyDamageToExplicitNonPlayerTarget(explicitTarget, weapon, direction, true, -1, true);

    Debug.Log($"[EnemyAttack][CmdApplyEnemyBowInfightingDamage] '{entityBehaviour.name}' bow-hit '{explicitTarget.name}' damage={serverDamage}.");
    RpcApplyEnemyHitEffectsExplicit(targetNetId, serverDamage, true);
}


[ClientRpc]
private void RpcApplyEnemyHitEffectsExplicit(uint targetNetId, int hostDamage, bool bowAttack)
{
    if (isServer)
        return;

    NetworkIdentity targetIdentity = null;

    if (!NetworkClient.spawned.TryGetValue(targetNetId, out targetIdentity) || targetIdentity == null)
    {
        foreach (NetworkIdentity identity in FindObjectsOfType<NetworkIdentity>())
        {
            if (identity != null && identity.netId == targetNetId)
            {
                targetIdentity = identity;
                break;
            }
        }
    }

    if (targetIdentity == null)
        return;

    DaggerfallEntityBehaviour explicitTarget = targetIdentity.GetComponent<DaggerfallEntityBehaviour>();
    if (explicitTarget == null || !(explicitTarget.Entity is EnemyEntity))
        return;

    Items.DaggerfallUnityItem weapon = null;
    EnemyEntity attackerEntity = entityBehaviour != null ? entityBehaviour.Entity as EnemyEntity : null;
    if (attackerEntity != null)
        weapon = attackerEntity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);

    ApplyDamageToExplicitNonPlayerTarget(explicitTarget, weapon, Vector3.zero, bowAttack, hostDamage, false);
}


[ClientRpc]
void RpcApplyEnemyHitEffects(uint targetNetId, Items.DaggerfallUnityItem weapon, int hostDamage)
{
    if (isServer) return; // Host already applied effects, ignore

    NetworkIdentity targetIdentity = null;

    // Try to find the target enemy on this client. Use NetworkClient.spawned here;
    // NetworkServer.spawned is not populated on pure clients.
    if (!NetworkClient.spawned.TryGetValue(targetNetId, out targetIdentity) || targetIdentity == null)
    {
        Debug.LogWarning($"[EnemyAttack] WARNING: Could not find enemy with NetID {targetNetId}. Trying fallback method.");

        foreach (NetworkIdentity identity in FindObjectsOfType<NetworkIdentity>())
        {
            if (identity.netId == targetNetId)
            {
                targetIdentity = identity;
                break;
            }
        }

        if (targetIdentity == null)
        {
            Debug.LogError($"[EnemyAttack] ERROR: Still could not find enemy with NetID {targetNetId} on the client.");
            return;
        }
    }

    DaggerfallEntityBehaviour explicitTarget = targetIdentity.GetComponent<DaggerfallEntityBehaviour>();
    if (explicitTarget == null || !(explicitTarget.Entity is EnemyEntity))
    {
        Debug.LogError($"[EnemyAttack] ERROR: Target with NetID {targetNetId} has no valid enemy entity!");
        return;
    }

    Debug.Log($"[EnemyAttack] Syncing explicit hit effects for {targetIdentity.name} on client. Host Damage: {hostDamage}");

    // Play hit/miss/blood/hurt animation on the actual target enemy.
    // Do not call the target's EnemyAttack.ApplyDamageToNonPlayer(), because that uses
    // that target's own senses.Target and can apply effects to the wrong object.
    ApplyDamageToExplicitNonPlayerTarget(explicitTarget, weapon, Vector3.zero, false, hostDamage, false);
}


        private void ShootBow()
        {
            if (IsSpawnedNetworkEnemy())
            {
                ShootOwnerArrow();
                return;
            }
            if (entityBehaviour)
            {
                DaggerfallMissile missile = Instantiate(ArrowMissilePrefab);
                if (missile)
                {
                    missile.Caster = entityBehaviour;
                    missile.TargetType = TargetTypes.SingleTargetAtRange;
                    missile.ElementType = ElementTypes.None;
                    missile.IsArrow = true;
                }
            }
        }

        private int ApplyDamageToPlayer(Items.DaggerfallUnityItem weapon)
        {
            const int doYouSurrenderToGuardsTextID = 15;

            EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;

            // Calculate damage
            damage = FormulaHelper.CalculateAttackDamage(entity, playerEntity, false, 0, weapon);

            // Break any "normal power" concealment effects on enemy
            if (entity.IsMagicallyConcealedNormalPower && damage > 0)
                EntityEffectManager.BreakNormalPowerConcealmentEffects(entityBehaviour);

            // Tally player's dodging skill
            playerEntity.TallySkill(DFCareer.Skills.Dodging, 1);

            // Handle Strikes payload from enemy to player target - this could change damage amount
            if (damage > 0 && weapon != null && weapon.IsEnchanted)
            {
                EntityEffectManager effectManager = GetComponent<EntityEffectManager>();
                if (effectManager)
                    damage = effectManager.DoItemEnchantmentPayloads(EnchantmentPayloadFlags.Strikes, weapon, entity.Items, playerEntity.EntityBehaviour, damage);
            }

            if (damage > 0)
            {
                if (entity.MobileEnemy.ID == (int)MobileTypes.Knight_CityWatch)
                {
                    // If hit by a guard, lower reputation and show the surrender dialogue
                    if (!playerEntity.HaveShownSurrenderToGuardsDialogue && playerEntity.CrimeCommitted != PlayerEntity.Crimes.None)
                    {
                        playerEntity.LowerRepForCrime();

                        DaggerfallMessageBox messageBox = new DaggerfallMessageBox(DaggerfallUI.UIManager);
                        messageBox.SetTextTokens(DaggerfallUnity.Instance.TextProvider.GetRSCTokens(doYouSurrenderToGuardsTextID));
                        messageBox.ParentPanel.BackgroundColor = Color.clear;
                        messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
                        messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
                        messageBox.OnButtonClick += SurrenderToGuardsDialogue_OnButtonClick;
                        messageBox.Show();

                        playerEntity.HaveShownSurrenderToGuardsDialogue = true;
                    }
                    // Surrender dialogue has been shown and player refused to surrender
                    // Guard damages player if player can survive hit, or if hit is fatal but guard rejects player's forced surrender
                    else if (playerEntity.CurrentHealth > damage || !playerEntity.SurrenderToCityGuards(false))
                        SendDamageToPlayer();
                }
                else
                    SendDamageToPlayer();
            }
            else
                sounds.PlayMissSound(weapon);

            return damage;
        }


        private void PlayHurtAnimationOnExplicitTarget(DaggerfallEntityBehaviour explicitTarget, int damageToShow)
        {
            if (damageToShow <= 0 || explicitTarget == null || explicitTarget.Entity == null)
                return;

            // If the target is already dead or dying, do not force a hurt animation over death/corpse handling.
            if (explicitTarget.Entity.CurrentHealth <= 0)
                return;

            MobileUnit targetMobile = explicitTarget.GetComponentInChildren<MobileUnit>();
            if (targetMobile == null || !targetMobile.IsSetup)
                return;

            // MobileStates.Hurt is the normal one-shot hurt animation. MobileUnit will return
            // to idle/move after the animation completes, same as normal DFU behaviour.
            targetMobile.ChangeEnemyState(MobileStates.Hurt);
        }

        private int ApplyDamageToExplicitNonPlayerTarget(DaggerfallEntityBehaviour explicitTarget, Items.DaggerfallUnityItem weapon, Vector3 direction, bool bowAttack = false, int hostDamage = -1, bool applyActualDamage = true)
        {
            if (explicitTarget == null || explicitTarget.Entity == null)
                return 0;

            EnemyEntity attackerEntity = entityBehaviour != null ? entityBehaviour.Entity as EnemyEntity : null;
            EnemyEntity targetEntity = explicitTarget.Entity as EnemyEntity;
            if (attackerEntity == null || targetEntity == null)
                return 0;

            EnemySounds targetSounds = explicitTarget.GetComponent<EnemySounds>();
            EnemyMotor targetMotor = explicitTarget.transform.GetComponent<EnemyMotor>();

            // Server/SP calculate real damage. Clients only play the host-reported effects.
            bool canCalculateDamage = isServer || !NetworkClient.active;
            int calculatedDamage = canCalculateDamage
                ? FormulaHelper.CalculateAttackDamage(attackerEntity, targetEntity, false, 0, weapon)
                : 0;

            int damageToShow = calculatedDamage;
            if (!isServer && NetworkClient.active && hostDamage >= 0)
                damageToShow = hostDamage;

            if (damageToShow > 0)
            {
                if (targetSounds != null)
                    targetSounds.PlayHitSound(weapon);

                EnemyBlood blood = explicitTarget.transform.GetComponent<EnemyBlood>();
                if (blood != null)
                {
                    CharacterController targetController = explicitTarget.transform.GetComponent<CharacterController>();
                    Vector3 bloodPos = explicitTarget.transform.position;
                    if (targetController != null)
                    {
                        bloodPos += targetController.center;
                        bloodPos.y += targetController.height / 8;
                    }

                    blood.ShowBloodSplash(targetEntity.MobileEnemy.BloodIndex, bloodPos);
                }

                PlayHurtAnimationOnExplicitTarget(explicitTarget, damageToShow);
            }
            else
            {
                if (targetSounds != null)
                {
                    WeaponTypes weaponType = (weapon != null) ?
                        DaggerfallUnity.Instance.ItemHelper.ConvertItemToAPIWeaponType(weapon) : WeaponTypes.Melee;

                    if ((!bowAttack && !targetEntity.MobileEnemy.ParrySounds) || weaponType == WeaponTypes.Melee)
                        sounds.PlayMissSound(weapon);
                    else if (targetEntity.MobileEnemy.ParrySounds)
                        targetSounds.PlayParrySound();
                }
            }

            // Apply actual HP change only where authoritative.
            if (applyActualDamage && canCalculateDamage && calculatedDamage > 0)
                targetEntity.DecreaseHealth(calculatedDamage);

            // Knockback only where authoritative.
            if (applyActualDamage && canCalculateDamage && calculatedDamage > 0 && targetMotor)
            {
                float enemyWeight = targetEntity.GetWeightInClassicUnits();
                float knockBackAmount = ((calculatedDamage * 10 - enemyWeight) * 256) / (enemyWeight + calculatedDamage * 10) * calculatedDamage * 2;
                float knockBackSpeed = (calculatedDamage * 10 / enemyWeight) * (calculatedDamage * 2 - (knockBackAmount / 256));
                knockBackSpeed /= (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10);

                if (knockBackSpeed < (15 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)))
                    knockBackSpeed = (15 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10));

                targetMotor.KnockbackSpeed = knockBackSpeed;
                targetMotor.KnockbackDirection = direction;
            }

            if (targetMotor)
                targetMotor.MakeEnemyHostileToAttacker(entityBehaviour);

            return damageToShow;
        }

          private int ApplyDamageToNonPlayer(Items.DaggerfallUnityItem weapon, Vector3 direction, bool bowAttack = false, int hostDamage = -1)
{
    if (senses.Target == null)
        return 0;

    EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
    EnemyEntity targetEntity = senses.Target.Entity as EnemyEntity;
    EnemySounds targetSounds = senses.Target.GetComponent<EnemySounds>();
    EnemyMotor targetMotor = senses.Target.transform.GetComponent<EnemyMotor>();

    // 🔹 **Clients always have damage = 0, unless the host sends a valid damage amount**
    bool isDamageAllowed = isServer || !NetworkClient.active;
    int damage = isDamageAllowed ? FormulaHelper.CalculateAttackDamage(entity, targetEntity, false, 0, weapon) : 0;

    // 🔹 **If the host sent a valid damage amount, use that instead (for effects)**
    if (!isServer && NetworkClient.active && hostDamage > 0)
    {
        Debug.Log($"[ApplyDamageToNonPlayer] Client applying host-reported damage effects: {hostDamage}");
        damage = hostDamage;
    }

    // **Only apply hit effects if damage > 0**
    if (damage > 0)
    {
        if (targetSounds != null)
        {
            targetSounds.PlayHitSound(weapon);
        }

        // Show blood effects
        EnemyBlood blood = senses.Target.transform.GetComponent<EnemyBlood>();
        if (blood != null)
        {
            CharacterController targetController = senses.Target.transform.GetComponent<CharacterController>();
            Vector3 bloodPos = senses.Target.transform.position + targetController.center;
            bloodPos.y += targetController.height / 8;
            blood.ShowBloodSplash(targetEntity.MobileEnemy.BloodIndex, bloodPos);
        }

        PlayHurtAnimationOnExplicitTarget(senses.Target, damage);
    }
    else
    {
        // **If the attack missed, play the appropriate sound**
        if (targetSounds != null)
        {
            WeaponTypes weaponType = (weapon != null) ? 
                DaggerfallUnity.Instance.ItemHelper.ConvertItemToAPIWeaponType(weapon) : WeaponTypes.Melee;

            if ((!bowAttack && !targetEntity.MobileEnemy.ParrySounds) || weaponType == WeaponTypes.Melee)
                sounds.PlayMissSound(weapon);
            else if (targetEntity.MobileEnemy.ParrySounds)
                targetSounds.PlayParrySound();
        }
    }

    // **Apply actual damage only on the host**
    if (isDamageAllowed && damage > 0)
    {
        targetEntity.DecreaseHealth(damage);
    }

    // Knockback (only on host)
    if (isDamageAllowed && damage > 0 && targetMotor)
    {
        float enemyWeight = targetEntity.GetWeightInClassicUnits();
        float knockBackAmount = ((damage * 10 - enemyWeight) * 256) / (enemyWeight + damage * 10) * damage * 2;
        float KnockbackSpeed = (damage * 10 / enemyWeight) * (damage * 2 - (knockBackAmount / 256));
        KnockbackSpeed /= (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10);

        if (KnockbackSpeed < (15 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10)))
            KnockbackSpeed = (15 / (PlayerSpeedChanger.classicToUnitySpeedUnitRatio / 10));

        targetMotor.KnockbackSpeed = KnockbackSpeed;
        targetMotor.KnockbackDirection = direction;
    }

    // Make enemy hostile
    if (targetMotor)
    {
        targetMotor.MakeEnemyHostileToAttacker(entityBehaviour);
    }

    return damage;
}

        private void SurrenderToGuardsDialogue_OnButtonClick(DaggerfallMessageBox sender, DaggerfallMessageBox.MessageBoxButtons messageBoxButton)
        {
            sender.CloseWindow();
            if (messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Yes)
                GameManager.Instance.PlayerEntity.SurrenderToCityGuards(true);
            else
                SendDamageToPlayer();
        }

        private void SendDamageToPlayer()
        {
            GameManager.Instance.PlayerObject.SendMessage("RemoveHealth", damage);

            EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
            Items.DaggerfallUnityItem weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
            if (weapon == null)
                weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.LeftHand);
            if (weapon != null)
                GameManager.Instance.PlayerObject.SendMessage("PlayWeaponHitSound");
            else
                GameManager.Instance.PlayerObject.SendMessage("PlayWeaponlessHitSound");
        }

        #endregion
    }
}
