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
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using Mirror;

namespace DaggerfallWorkshop
{
    /// <summary>
    /// Special mobile unit behaviours for Daedra Seducer.
    /// </summary>
    public class DaedraSeducerMobileBehaviour : MonoBehaviour
    {
        const float secondsToTransform = 8.0f;      // 0.0 will disable transform completely

        MobileUnit enemyMobile;
        DaggerfallEntityBehaviour enemyEntityBehaviour;
        EnemyEntity enemyEntity;
        EnemySenses enemySenses;
        SetupDemoEnemy networkSetup;

        float transformCountdown = secondsToTransform;
        bool transformStarted = false;

        private void Start()
        {
            // Get references
            enemyMobile = GetComponent<MobileUnit>();
            enemyEntityBehaviour = GetComponentInParent<DaggerfallEntityBehaviour>();
            networkSetup = GetComponentInParent<SetupDemoEnemy>();
            if (enemyEntityBehaviour && enemyEntityBehaviour.EntityType == EntityTypes.EnemyMonster)
            {
                enemyEntity = (EnemyEntity)enemyEntityBehaviour.Entity;
                enemySenses = enemyEntityBehaviour.GetComponent<EnemySenses>();
            }
        }

        private void Update()
        {
            // Dedicated server has no local player whose vanilla trigger should run.
            // Player clients (including a listen host) report the trigger to the server.
            if (NetworkServer.active && !NetworkClient.active)
                return;

            // Validate references
            if (!enemySenses || !enemyMobile || enemyEntity == null)
                return;

            // Exit if special transformation already completed
            // Raise suppress infighting flag in case player has loaded game after transform
            if (enemyMobile.SpecialTransformationCompleted)
            {
                enemyEntity.SuppressInfighting = true;
                return;
            }

            // Once the server has retained the transformation event, every observer owns
            // its local animation playback. This also restores vanilla's retry behaviour if
            // Hurt or another one-shot state interrupts Transform1/Transform2 on an observer.
            if (networkSetup != null && networkSetup.SeducerTransformationStarted)
            {
                transformStarted = true;
                if (enemyMobile.EnemyState != MobileStates.SeducerTransform1 &&
                    enemyMobile.EnemyState != MobileStates.SeducerTransform2)
                {
                    enemyMobile.ChangeEnemyState(MobileStates.SeducerTransform1);
                }
                return;
            }

            // In single-player, keep trying to raise transform state if another state
            // (for example Hurt) interrupts the local vanilla transformation. In MP, a
            // transformStarted value before the SyncVar arrives only means this client
            // already sent its reliable request, so do not spam a Command every frame.
            if (transformStarted)
            {
                if (!NetworkClient.active && !NetworkServer.active &&
                    enemyMobile.EnemyState != MobileStates.SeducerTransform1 &&
                    enemyMobile.EnemyState != MobileStates.SeducerTransform2)
                {
                    StartTransformation();
                }
                return;
            }

            // Only transform when targeting this peer's local player and hurt or after timer elapsed.
            // In MP EnemySenses deliberately targets the PlayerMultiplayer body, not PlayerAdvanced,
            // so resolve the local network player instead of comparing only to PlayerEntityBehaviour.
            if (IsTargetingLocalPlayer() && transformCountdown > 0)
            {
                bool isHurt = enemyEntity.CurrentHealth < enemyEntity.MaxHealth;
                transformCountdown -= Time.deltaTime;

                // Countdown allows winged form to reach player even when humanoid form cannot
                // (e.g. stuck on pillar in Direnni Tower).
                if (isHurt || transformCountdown <= 0)
                    StartTransformation();
            }
        }

        bool IsTargetingLocalPlayer()
        {
            DaggerfallEntityBehaviour target = enemySenses != null ? enemySenses.Target : null;
            if (target == null)
                return false;

            // Pure single-player retains the original DFU comparison.
            if (!NetworkClient.active && !NetworkServer.active)
                return GameManager.Instance != null &&
                       target == GameManager.Instance.PlayerEntityBehaviour;

            // Multiplayer EnemySenses targets PlayerMultiplayer. Check both the target
            // object and its parent so this remains valid if the entity component lives
            // on a child/collision proxy. Ownership of the enemy is intentionally irrelevant.
            PlayerMultiplayer player = target.GetComponent<PlayerMultiplayer>();
            if (player == null)
                player = target.GetComponentInParent<PlayerMultiplayer>();

            return player != null && player.isLocalPlayer;
        }

        void StartTransformation()
        {
            transformCountdown = 0;

            // In multiplayer, any player can trigger this vanilla condition regardless of
            // who currently owns enemy movement. The server turns that request into one
            // retained event so every observer starts the same transformation.
            if ((NetworkClient.active || NetworkServer.active) && networkSetup != null)
            {
                transformStarted = true;
                networkSetup.RequestSeducerTransformation();
                return;
            }

            // Single-player keeps the original DFU behaviour.
            enemyMobile.ChangeEnemyState(MobileStates.SeducerTransform1);
            transformStarted = true;
        }
    }
}