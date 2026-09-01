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

        // Last local attack-sound decision for the current melee swing.
        // The actual player-damage event is local in MP, so observers should get
        // enemy attack audio from the same local damage report that already syncs
        // player hit sounds.
        private bool lastMeleeAttackSoundPlayed = false;

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
            // Unable to attack if paralyzed
            if (entityBehaviour.Entity.IsParalyzed)
                return;

            // If a melee attack has reached the damage frame we can run a melee attempt
            if (mobile.DoMeleeDamage)
            {
                MeleeDamage();
                mobile.DoMeleeDamage = false;
            }
            // If a bow attack has reached the shoot frame we can shoot an arrow
            else if (mobile.ShootArrow)
            {
                ShootBow();
                mobile.ShootArrow = false;

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
    if (senses.Target == null)
        return;

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
    // 🔹 **Ensure only the host runs this in multiplayer**
    if (NetworkClient.active && !isServer) 
    {
        Debug.LogWarning($"[MeleeAnimation] ERROR: Client is running MeleeAnimation() when it should not! ({gameObject.name})");
        return false; // 🔹 **Clients should never execute this**
    }

    if (senses.TargetInSight && senses.TargetIsWithinYawAngle(22.5f, senses.LastKnownTargetPos))
    {
        float distance = MeleeDistance;

        if (!DaggerfallUnity.Settings.EnhancedCombatAI && senses.Target != GameManager.Instance.PlayerEntityBehaviour)
            distance = ClassicMeleeDistanceVsAI;

        if (senses.DistanceToTarget > distance + senses.TargetRateOfApproach)
            return false;

        bool playedAttackSound = false;
        lastMeleeAttackSoundPlayed = false;

        // The simulator decides whether this swing actually made an attack sound.
        // Observing clients will replay the same decision through the animation RPC.
        if (sounds)
        {
            Debug.Log($"[MeleeAnimation] Playing attack sound on {gameObject.name}");
            playedAttackSound = sounds.PlayAttackSound();
            lastMeleeAttackSoundPlayed = playedAttackSound;
        }

        // 🔹 **Single-Player Mode: Play Animation Locally**
        if (!NetworkServer.active) 
        {
            Debug.Log($"[MeleeAnimation] Playing attack animation in single-player mode.");
            mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
        }
        else 
        {
            Debug.Log($"[MeleeAnimation] Host is executing attack animation.");
            mobile.ChangeEnemyState(MobileStates.PrimaryAttack);

            // 🔹 **Tell all clients to play the animation and the same attack sound decision**
            RpcPlayAttackAnimation(playedAttackSound);
        }

        return true;
    }

    return false;
}

[ClientRpc]
void RpcPlayAttackAnimation(bool playedAttackSound)
{
    if (isServer) return; // 🔹 Host already played animation/sound, ignore it

    Debug.Log($"[RpcPlayAttackAnimation] Client received animation RPC. Playing attack animation.");

    if (playedAttackSound)
    {
        if (sounds == null)
            sounds = GetComponent<EnemySounds>();

        if (sounds != null)
            sounds.PlayAttackSoundForced();
    }

    mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
}


private void MeleeDamage()
{
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
            {
                if (sounds != null)
                    sounds.PlayMissSound(weapon);

                // Multiplayer cosmetic sync: the local target hears the miss locally,
                // but observers do not unless we report the 0-damage enemy swing too.
                ReportLocalPlayerHitByEnemyCosmetics(false, 0);
            }

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

        private void ReportLocalPlayerHitByEnemyCosmetics(bool weaponHit, int damageAmount)
        {
            if (!NetworkClient.active)
                return;

            PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayerForCommand("enemy-hit-player-cosmetics");
            if (localPlayer == null)
                localPlayer = PlayerMultiplayer.GetAnyPlayerForCommandFallback("enemy-hit-player-cosmetics");

            NetworkIdentity enemyIdentity = GetComponent<NetworkIdentity>();
            if (localPlayer != null && enemyIdentity != null)
                localPlayer.CmdReportLocalPlayerHitByEnemyCosmetics(enemyIdentity.netId, weaponHit, damageAmount, lastMeleeAttackSoundPlayed);
        }

        private void SendDamageToPlayer()
        {
            GameManager.Instance.PlayerObject.SendMessage("RemoveHealth", damage);

            EnemyEntity entity = entityBehaviour.Entity as EnemyEntity;
            Items.DaggerfallUnityItem weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.RightHand);
            if (weapon == null)
                weapon = entity.ItemEquipTable.GetItem(Items.EquipSlots.LeftHand);
            bool weaponHit = weapon != null;
            if (weaponHit)
                GameManager.Instance.PlayerObject.SendMessage("PlayWeaponHitSound");
            else
                GameManager.Instance.PlayerObject.SendMessage("PlayWeaponlessHitSound");

            // The target player hears this locally through PlayerFootsteps, but other clients
            // only see synced health/animation. Report a cosmetic hit event so observers also
            // hear the hit sound near the attacking enemy.
            ReportLocalPlayerHitByEnemyCosmetics(weaponHit, damage);
        }

        #endregion
    }
}
