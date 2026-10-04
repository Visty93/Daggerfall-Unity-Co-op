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

using System;
using UnityEngine;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Entity;
using Mirror;

namespace DaggerfallWorkshop.Game.Utility
{
    /// <summary>
    /// Spawn one or more enemies near player.
    /// Will attempt to start placing spawns after game objects are set and spawn count greater than 0.
    /// This is a generic spawn helper not tied to any specific system.
    /// NOTES:
    ///  * Spawns foes immediately. Be careful spawning multiple foes as they are likely to become stuck on each other.
    ///  * The spawner will self-destroy once all foes spawned. Do not attach to anything you want to remain in scene.
    ///  * There is a prefab carrying this component in Prefabs/Scene for easy spawner setups.
    ///  * Will attempt to find best parent at time if none specified (e.g. dungeon, interior).
    ///  * Might need to reduce MinDistance if expecting to spawn in tigt confines like small interiors.
    /// </summary>
    public class FoeSpawner : NetworkBehaviour
    {
        // Set these values at create time to setup a foe spawn automatically on start
        public MobileTypes FoeType = MobileTypes.None;
        public int SpawnCount = 0;
        public float MinDistance = 4f;
        public float MaxDistance = 20f;
        public Transform Parent = null;
        public bool LineOfSightCheck = true;
        public bool AlliedToPlayer = false;
        public int RequesterLevel = 0;

        public MobileTypes lastFoeType = MobileTypes.None;
        GameObject[] pendingFoeGameObjects;
        int pendingFoesSpawned = 0;
        bool spawnInProgress = false;

        // Multiplayer compatibility for callers that use the vanilla two-step pattern:
        // CreateFoeSpawner(spawnCount: 0) -> CreateFoeGameObjects() -> SetFoeGameObjects().
        // In MP these supplied GameObjects must remain local/unnetworked until FoeSpawner
        // has placed and the caller has finished customizing them.
        bool awaitingExternallySuppliedFoes = false;
        bool externalCreationClaimed = false;
        bool externallySuppliedFoes = false;
        string externalCallerKey = string.Empty;
        int externalRegistrationFrame = -1;

        static readonly System.Collections.Generic.List<FoeSpawner> pendingExternalSpawners =
            new System.Collections.Generic.List<FoeSpawner>();

        public void RegisterExternalObjectMode(string callerKey)
        {
            awaitingExternallySuppliedFoes = true;
            externalCreationClaimed = false;
            externallySuppliedFoes = false;
            externalCallerKey = callerKey ?? string.Empty;
            externalRegistrationFrame = Time.frameCount;

            if (!pendingExternalSpawners.Contains(this))
                pendingExternalSpawners.Add(this);

            Debug.Log($"[ExternalFoeMP] Registered zero-count FoeSpawner '{name}' caller='{externalCallerKey}' frame={externalRegistrationFrame} min={MinDistance} max={MaxDistance}");
        }

        public static bool TryClaimPendingExternalSpawner(string callerKey, out FoeSpawner spawner)
        {
            spawner = null;

            for (int i = pendingExternalSpawners.Count - 1; i >= 0; i--)
            {
                FoeSpawner candidate = pendingExternalSpawners[i];
                if (candidate == null)
                {
                    pendingExternalSpawners.RemoveAt(i);
                    continue;
                }

                // The vanilla external-object pattern is synchronous. Restrict the claim
                // to the same frame so an abandoned zero-count spawner cannot capture an
                // unrelated CreateFoeGameObjects() call later.
                if (candidate.externalRegistrationFrame != Time.frameCount)
                {
                    pendingExternalSpawners.RemoveAt(i);
                    continue;
                }

                if (!candidate.awaitingExternallySuppliedFoes || candidate.externalCreationClaimed)
                    continue;

                string wanted = candidate.externalCallerKey ?? string.Empty;
                string actual = callerKey ?? string.Empty;
                if (wanted.Length > 0 && actual.Length > 0 && wanted != actual)
                    continue;

                candidate.externalCreationClaimed = true;
                pendingExternalSpawners.RemoveAt(i);
                spawner = candidate;

                Debug.Log($"[ExternalFoeMP] Claimed pending FoeSpawner '{candidate.name}' caller='{actual}' frame={Time.frameCount}");
                return true;
            }

            return false;
        }

        void OnDestroy()
        {
            pendingExternalSpawners.Remove(this);
        }

        void Awake()
        {
            // Register as Foe Spawner object
            ActiveGameObjectDatabase.RegisterFoeSpawner(gameObject);
        }

        void Update()
        {
            // Create new foe list when changed in editor
            if (FoeType != MobileTypes.None && FoeType != lastFoeType && SpawnCount > 0)
            {
if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
{
    // Client: no local GOs; we’ll compute positions and request spawns with those coords.
    pendingFoeGameObjects = new GameObject[SpawnCount]; // stub to drive the loop
    pendingFoesSpawned = 0;
    spawnInProgress = true;
    lastFoeType = FoeType;
}
else if (Mirror.NetworkServer.active)
{
    // Network host/server: do NOT pre-create real network enemies at Vector3.zero.
    // GameObjectHelper.CreateFoeGameObjects() immediately NetworkServer.Spawn()s on host,
    // so creating the pending array at Vector3.zero can leak a real enemy at world origin
    // if placement is delayed or a NetworkTransform update is sent before relocation.
    DestroyOldFoeGameObjects(pendingFoeGameObjects);
    pendingFoeGameObjects = new GameObject[SpawnCount]; // stub only; actual spawn happens after TryFindSpawnPoint()
    pendingFoesSpawned = 0;
    spawnInProgress = true;
    lastFoeType = FoeType;
}
else
{
    // Single-player: preserve vanilla pending-object behavior.
    DestroyOldFoeGameObjects(pendingFoeGameObjects);
    SetFoeGameObjects(GameObjectHelper.CreateFoeGameObjects(Vector3.zero, FoeType, SpawnCount, alliedToPlayer: AlliedToPlayer));
    lastFoeType = FoeType;
}
            }

            // Do nothing if no spawns or we are done spawning
            if (pendingFoeGameObjects == null || !spawnInProgress)
                return;

            // Clear pending foes if all have been spawned
            if (spawnInProgress && pendingFoesSpawned >= pendingFoeGameObjects.Length)
            {
                spawnInProgress = false;
                Destroy(gameObject);
                return;
            }

            // Try placing foes near player
            PlaceFoeFreely(pendingFoeGameObjects, MinDistance, MaxDistance);

            if (spawnInProgress)
                GameManager.Instance.RaiseOnEncounterEvent();
        }

        #region Public Methods

        /// <summary>
        /// Assign an array of pending foe GameObjects to spawn.
        /// The spawner will then try to place these foes around player until none remain.
        /// Use GameObjectHelper.CreateFoeGameObjects() static method to create foe GameObjects first.
        /// </summary>
        public void SetFoeGameObjects(GameObject[] gameObjects, Transform parent = null)
        {
            // Do nothing if array not valid
            if (gameObjects == null || gameObjects.Length == 0)
            {
                spawnInProgress = false;
                return;
            }

            // A zero-count spawner supplied with actual GameObjects is the stock DFU
            // caller-supplied pattern, regardless of whether CreateFoeGameObjects() happened
            // before or after CreateFoeSpawner(). Some third-party mods create/customize the
            // foe FIRST, then create the empty spawner and hand the object to it.
            //
            // Do not require externalCreationClaimed here. Some callers create and
            // customize foe objects before assigning them to the empty spawner.
            bool clientPendingObjects = GameObjectHelper.TryClaimPendingExternalClientFoes(gameObjects);
            externallySuppliedFoes =
                (Mirror.NetworkClient.active || Mirror.NetworkServer.active) &&
                SpawnCount == 0 &&
                (awaitingExternallySuppliedFoes || clientPendingObjects);

            bool wasPreclaimed = externalCreationClaimed;
            awaitingExternallySuppliedFoes = false;
            externalCreationClaimed = false;
            pendingExternalSpawners.Remove(this);

            if (externallySuppliedFoes)
            {
                Debug.Log(
                    $"[ExternalFoeMP] SetFoeGameObjects accepted {gameObjects.Length} caller-supplied object(s) " +
                    $"on '{name}' preclaimed={wasPreclaimed} clientPending={clientPendingObjects}. " +
                    $"They will be placed as the SAME objects; no generic replacements will be created.");

                for (int i = 0; i < gameObjects.Length; i++)
                {
                    if (gameObjects[i] == null)
                        continue;
                    ExternalFoeLifetimeDiagnostics diag = ExternalFoeLifetimeDiagnostics.Attach(
                        gameObjects[i],
                        Mirror.NetworkServer.active ? "HOST_EXTERNAL_SUPPLIED" : "CLIENT_STAGING",
                        PlayerMultiplayer.GetLocalPlayerNetIdSafe());
                    if (diag != null)
                        diag.Mark("SET_FOE_GAME_OBJECTS_ACCEPTED");
                }
            }

            // Store array and start spawning
            pendingFoeGameObjects = gameObjects;
            pendingFoesSpawned = 0;
            spawnInProgress = true;
            
            // Set parent if specified
            if (parent)
            {
                Parent = parent;
                for (int i = 0; i < pendingFoeGameObjects.Length; i++)
                {
                    pendingFoeGameObjects[i].transform.parent = parent;
                }
            }
        }

        #endregion

        #region Private Methods

        private int GetEffectiveRequesterLevel()
        {
            if (RequesterLevel > 0)
                return Mathf.Clamp(RequesterLevel, 1, 100);

            if (GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
                return Mathf.Clamp(GameManager.Instance.PlayerEntity.Level, 1, 100);

            return 1;
        }

        // Uses raycasts to find next spawn position just outside of player's field of view
void PlaceFoeFreely(GameObject[] gameObjects, float minDistance = 5f, float maxDistance = 20f)
{
    // ------- CALLER-SUPPLIED OBJECTS: preserve the actual customized object -------
    // This branch is deliberately before the ordinary host/client FoeSpawner paths.
    // Normal SpawnCount>0 encounters and quest CreateFoe waves therefore remain unchanged.
    if (externallySuppliedFoes)
    {
        PlaceExternallySuppliedFoe(gameObjects, minDistance, maxDistance);
        return;
    }

    // ------- PURE CLIENT: compute locally, request spawn with exact coords, return -------
    if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
    {
        Vector3 testPoint;
        if (!TryFindSpawnPoint(out testPoint, minDistance, maxDistance))
            return;

        // Use your existing helper – on client this sends Cmd to host and returns null
        GameObjectHelper.CreateFoeGameObjects(
            testPoint,
            FoeType,
            1,
            alliedToPlayer: AlliedToPlayer,
            requesterLevel: GetEffectiveRequesterLevel()
        );

        pendingFoesSpawned++;
        if (pendingFoesSpawned >= (pendingFoeGameObjects?.Length ?? SpawnCount))
        {
            spawnInProgress = false;
            Destroy(gameObject);
        }

        GameManager.Instance.RaiseOnEncounterEvent();
        return; // IMPORTANT: don’t fall through to host/SP path
    }

    // ------- NETWORK HOST/SERVER: compute a valid point first, then spawn the real network enemy there -------
    if (Mirror.NetworkServer.active)
    {
        Vector3 networkHostTestPoint;
        if (!TryFindSpawnPoint(out networkHostTestPoint, minDistance, maxDistance))
            return;

        GameObject[] spawnedEnemies = GameObjectHelper.CreateFoeGameObjects(
            networkHostTestPoint,
            FoeType,
            1,
            alliedToPlayer: AlliedToPlayer,
            requesterLevel: GetEffectiveRequesterLevel()
        );

        if (spawnedEnemies == null || spawnedEnemies.Length == 0 || spawnedEnemies[0] == null)
        {
            Debug.LogWarning($"[FoeSpawner] Network host failed to spawn {FoeType} at computed point {networkHostTestPoint}; will retry.");
            return;
        }

        GameObject spawned = spawnedEnemies[0];
        spawned.transform.position = networkHostTestPoint;
        if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
            spawned.transform.LookAt(GameManager.Instance.PlayerObject.transform.position);

        // Store a non-zero intended spawn position when the helper has this component.
        // This prevents later settle/resnap helpers from having only Vector3.zero metadata.
        EnemyWorldPosition ewp = spawned.GetComponent<EnemyWorldPosition>();
        if (ewp != null)
            ewp.intendedSpawnPos = networkHostTestPoint;

        pendingFoesSpawned++;
        if (pendingFoesSpawned >= (pendingFoeGameObjects?.Length ?? SpawnCount))
        {
            spawnInProgress = false;
            Destroy(gameObject);
        }

        GameManager.Instance.RaiseOnEncounterEvent();
        return;
    }

    // ------- SINGLE-PLAYER: original pending-object behavior preserved -------
    if (gameObjects == null || gameObjects.Length == 0) return;
    if (pendingFoesSpawned >= gameObjects.Length) return;

    // Skip this foe if destroyed (e.g. player left building where pending)
    if (!gameObjects[pendingFoesSpawned])
    {
        pendingFoesSpawned++;
        return;
    }

    // Set parent if none specified already
if (!gameObjects[pendingFoesSpawned].transform.parent)
{
    // ✅ Only parent in single-player
    if (!Mirror.NetworkServer.active && !Mirror.NetworkClient.active)
        gameObjects[pendingFoesSpawned].transform.parent = GameObjectHelper.GetBestParent();
}

    // Compute the spawn point using the exact same logic
    Vector3 spTestPoint;
    if (!TryFindSpawnPoint(out spTestPoint, minDistance, maxDistance))
        return;

    // Original finalize & face player
    gameObjects[pendingFoesSpawned].transform.position = spTestPoint;
    FinalizeFoe(gameObjects[pendingFoesSpawned]);
    gameObjects[pendingFoesSpawned].transform.LookAt(GameManager.Instance.PlayerObject.transform.position);

    pendingFoesSpawned++;
}

		

        void PlaceExternallySuppliedFoe(GameObject[] gameObjects, float minDistance, float maxDistance)
        {
            if (gameObjects == null || gameObjects.Length == 0)
                return;
            if (pendingFoesSpawned < 0 || pendingFoesSpawned >= gameObjects.Length)
                return;

            GameObject go = gameObjects[pendingFoesSpawned];
            if (!go)
            {
                pendingFoesSpawned++;
                return;
            }

            ExternalFoeLifetimeDiagnostics lifetimeDiag = ExternalFoeLifetimeDiagnostics.Attach(
                go,
                Mirror.NetworkServer.active ? "HOST_EXTERNAL_SUPPLIED" : "CLIENT_STAGING",
                PlayerMultiplayer.GetLocalPlayerNetIdSafe());
            if (lifetimeDiag != null)
                lifetimeDiag.Mark("PLACEMENT_ATTEMPT");

            Vector3 testPoint;
            if (!TryFindSpawnPoint(out testPoint, minDistance, maxDistance))
                return;

            // Preserve vanilla placement of the SAME object the caller customized.
            go.transform.position = testPoint;
            FinalizeFoe(go);
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                go.transform.LookAt(GameManager.Instance.PlayerObject.transform.position);

            Vector3 finalPosition = go.transform.position;
            Quaternion finalRotation = go.transform.rotation;
            Vector3 finalScale = go.transform.lossyScale;

            if (lifetimeDiag != null)
                lifetimeDiag.Mark("PLACED_AND_FINALIZED_LOCAL");

            if (NetworkServer.active)
            {
                FinalizeExternallySuppliedFoeOnServer(go, finalPosition, finalRotation);
            }
            else if (NetworkClient.active)
            {
                SendExternallySuppliedFoeToServer(go, finalPosition, finalRotation, finalScale);
            }

            pendingFoesSpawned++;
        }

        void CaptureExternalSpawnOverrides(GameObject go, SetupDemoEnemy setup, uint requesterNetId)
        {
            if (go == null || setup == null)
                return;

            string originalDisplayName = go.name;

            DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            MobileUnit mobileUnit = go.GetComponentInChildren<MobileUnit>();

            int entityId = entity != null ? entity.MobileEnemy.ID : (int)setup.EnemyType;
            int visualId = mobileUnit != null ? mobileUnit.Enemy.ID : entityId;

            setup.ExternalVisualEnemyId =
                visualId >= 0 && visualId != entityId && GameObjectHelper.EnemyDict.ContainsKey(visualId)
                    ? visualId
                    : -1;

            if (mobileUnit != null)
            {
                setup.ExternalVisualMaleTexture = mobileUnit.Enemy.MaleTexture;
                setup.ExternalVisualFemaleTexture = mobileUnit.Enemy.FemaleTexture;
                setup.ExternalVisualRangedFlags =
                    (mobileUnit.Enemy.HasRangedAttack1 ? 1 : 0) |
                    (mobileUnit.Enemy.HasRangedAttack2 ? 2 : 0) |
                    (mobileUnit.Enemy.CastsMagic ? 4 : 0);
            }
            else
            {
                setup.ExternalVisualRangedFlags = -1;
            }

            if (entity != null)
            {
                MobileEnemy mobile = entity.MobileEnemy;

                // Publish the caller's finished entity state into the normal MP SyncVars.
                setup.EnemyType = (MobileTypes)mobile.ID;
                setup.EnemyGender = mobile.Gender;
                setup.EnemyReaction = mobile.Reactions;

                // OnStartServer normally restores the enemy type's default team.
                // Preserve either kind of caller modification: some code changes
                // EnemyEntity.Team directly, while other code changes MobileEnemy.Team.
                MobileTeams effectiveTeam = entity.Team;
                MobileEnemy defaults;
                if (setup.SpawnTeamOverride >= 0)
                {
                    setup.SpawnTeamOverride = (int)effectiveTeam;
                }
                else if (GameObjectHelper.EnemyDict.TryGetValue(mobile.ID, out defaults) &&
                         (effectiveTeam != defaults.Team || mobile.Team != defaults.Team))
                {
                    setup.SpawnTeamOverride = (int)effectiveTeam;
                }
            }

            setup.ExternalSpawnOriginNetId = requesterNetId;
            setup.ExternalSpawnDisplayName = originalDisplayName;

            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            if (motor != null)
            {
                // External callers can customize the finished MobileUnit independently
                // from the backing EnemyEntity. Keep this actor's bow decision aligned
                // with the MobileUnit state that was actually supplied to FoeSpawner.
                if (mobileUnit != null)
                {
                    motor.hasBowAttack =
                        mobileUnit.Enemy.HasRangedAttack1 &&
                        (!mobileUnit.Enemy.CastsMagic || mobileUnit.Enemy.HasRangedAttack2);
                }

                setup.SyncedMotorIsHostile = motor.IsHostile;
                setup.SpawnedMotorIsHostile = motor.IsHostile;
                setup.CurrentMotorIsHostile = motor.IsHostile;
                setup.LastAppliedMotorIsHostile = motor.IsHostile;
            }

            string entityRangedState = entity != null
                ? $"{entity.MobileEnemy.HasRangedAttack1}/{entity.MobileEnemy.HasRangedAttack2}/{entity.MobileEnemy.CastsMagic}"
                : "<none>";
            string visualRangedState = mobileUnit != null
                ? $"{mobileUnit.Enemy.HasRangedAttack1}/{mobileUnit.Enemy.HasRangedAttack2}/{mobileUnit.Enemy.CastsMagic}"
                : "<none>";
            string visualTextureState = mobileUnit != null
                ? $"{mobileUnit.Enemy.MaleTexture}/{mobileUnit.Enemy.FemaleTexture}"
                : "<none>";
            string motorBowState = motor != null ? motor.hasBowAttack.ToString() : "<none>";

            Debug.Log(
                $"[ExternalFoeStateDiag][SOURCE_CAPTURE] name='{go.name}' requester={requesterNetId} " +
                $"entityId={entityId} visualId={visualId} setupType={setup.EnemyType} " +
                $"entityRanged={entityRangedState} visualRanged={visualRangedState} motorBow={motorBowState} " +
                $"visualTex={visualTextureState} extVisualId={setup.ExternalVisualEnemyId} " +
                $"extTex={setup.ExternalVisualMaleTexture}/{setup.ExternalVisualFemaleTexture} " +
                $"extVisualRangedFlags={setup.ExternalVisualRangedFlags} " +
                $"reaction={setup.EnemyReaction} teamOverride={setup.SpawnTeamOverride} allied={setup.AlliedToPlayer}");
        }

        void FinalizeExternallySuppliedFoeOnServer(GameObject go, Vector3 finalPosition, Quaternion finalRotation)
        {
            uint requesterNetId = PlayerMultiplayer.GetLocalPlayerNetIdSafe();

            SetupDemoEnemy setup = go.GetComponent<SetupDemoEnemy>();
            CaptureExternalSpawnOverrides(go, setup, requesterNetId);

            // Preserve the exact placed world pose while moving the network object to scene root.
            go.transform.SetParent(null, true);
            go.transform.SetPositionAndRotation(finalPosition, finalRotation);

            NetworkIdentity identity = go.GetComponent<NetworkIdentity>();
            if (identity == null)
                identity = go.AddComponent<NetworkIdentity>();

            DaggerfallEnemy dfEnemy = go.GetComponent<DaggerfallEnemy>();
            if (dfEnemy != null && dfEnemy.LoadID == 0)
                dfEnemy.LoadID = DaggerfallUnity.NextUID;

            bool isDungeon =
                GameManager.Instance != null &&
                GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon;
            bool isBuilding =
                !isDungeon &&
                GameManager.Instance != null &&
                GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInsideBuilding;

            if (setup != null && isDungeon)
                setup.isDungeonEnemy = true;

            EnemyWorldPosition world = go.GetComponent<EnemyWorldPosition>();
            if (world != null)
            {
                if (isDungeon)
                    world.SetDungeonSpawnContext(requesterNetId, 0, 0, false);
                else
                    world.SetSpawnContext(isBuilding, requesterNetId);

                // This is a generic FoeSpawner actor, not a quest CreateFoe wave marker.
                // Do not opt it into the quest-wave settle/resnap machinery.
                world.intendedSpawnPos = Vector3.zero;
                world.isCreateFoeWaveSpawn = false;
            }

            // In the object-first pattern the host's normal CreateFoeGameObjects() path has
            // already NetworkServer.Spawn()ed this exact object at Vector3.zero. Do NOT spawn
            // it a second time. Just move/reconfigure the same network object in place.
            bool alreadyNetworked = identity != null && identity.netId != 0U;
            ExternalFoeLifetimeDiagnostics diag = ExternalFoeLifetimeDiagnostics.Attach(
                go, "HOST_EXTERNAL_SUPPLIED", requesterNetId);
            if (diag != null)
                diag.Mark(alreadyNetworked ? "HOST_ALREADY_NETWORKED_BEFORE_FINALIZE" : "HOST_BEFORE_NETWORK_SPAWN");

            if (!alreadyNetworked)
                NetworkServer.Spawn(go);

            if (diag != null)
                diag.Mark("HOST_AFTER_NETWORK_FINALIZE");

            // Re-bake world coordinates from the final placed position. This is required for
            // object-first callers because EnemyWorldPosition may already have initialized
            // while the actor was still sitting at world origin.
            if (world != null)
                world.ResumeRetainedActorNetworking();

            Debug.Log(
                $"[ExternalFoeMP] Host finalized caller-supplied foe '{go.name}' " +
                $"alreadyNetworked={alreadyNetworked} netId={(identity != null ? identity.netId : 0U)} " +
                $"pos={go.transform.position} min={MinDistance} max={MaxDistance}");
        }

        void SendExternallySuppliedFoeToServer(GameObject go, Vector3 finalPosition, Quaternion finalRotation, Vector3 finalScale)
        {
            PlayerMultiplayer multiplayer =
                PlayerMultiplayer.GetLocalPlayerForCommand("FoeSpawner external supplied foe");
            if (multiplayer == null)
            {
                Debug.LogWarning($"[ExternalFoeMP] No local PlayerMultiplayer found for '{go.name}'.");
                Destroy(go);
                return;
            }

            SetupDemoEnemy setup = go.GetComponent<SetupDemoEnemy>();
            DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            MobileUnit mobileUnit = go.GetComponentInChildren<MobileUnit>();

            int entityId = entity != null ? entity.MobileEnemy.ID :
                (setup != null ? (int)setup.EnemyType : (int)FoeType);
            int visualId = mobileUnit != null ? mobileUnit.Enemy.ID : entityId;
            int visualMaleTexture = mobileUnit != null ? mobileUnit.Enemy.MaleTexture : -1;
            int visualFemaleTexture = mobileUnit != null ? mobileUnit.Enemy.FemaleTexture : -1;
            int visualRangedFlags = mobileUnit != null
                ? (mobileUnit.Enemy.HasRangedAttack1 ? 1 : 0) |
                  (mobileUnit.Enemy.HasRangedAttack2 ? 2 : 0) |
                  (mobileUnit.Enemy.CastsMagic ? 4 : 0)
                : -1;

            MobileReactions reaction = entity != null ? entity.MobileEnemy.Reactions :
                (setup != null ? setup.EnemyReaction : MobileReactions.Hostile);
            MobileGender gender = entity != null ? entity.MobileEnemy.Gender :
                (setup != null ? setup.EnemyGender : MobileGender.Unspecified);
            bool allied = setup != null && setup.AlliedToPlayer;
            byte classicDistance = setup != null ? setup.ClassicSpawnDistanceType : (byte)0;

            MobileTeams team = entity != null ? entity.Team : (MobileTeams)0;
            bool hasExplicitTeam = false;
            if (setup != null && setup.SpawnTeamOverride >= 0 &&
                System.Enum.IsDefined(typeof(MobileTeams), setup.SpawnTeamOverride))
            {
                team = (MobileTeams)setup.SpawnTeamOverride;
                hasExplicitTeam = true;
            }
            else if (entity != null)
            {
                MobileEnemy defaults;
                if (GameObjectHelper.EnemyDict.TryGetValue(entity.MobileEnemy.ID, out defaults) &&
                    (entity.Team != defaults.Team || entity.MobileEnemy.Team != defaults.Team))
                {
                    hasExplicitTeam = true;
                }
            }

            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            bool liveHostile = motor != null ? motor.IsHostile : reaction == MobileReactions.Hostile;

            int currentHealth = entity != null ? entity.CurrentHealth : 0;
            int maxHealth = entity != null ? entity.MaxHealth : 0;

            bool isDungeon =
                GameManager.Instance != null &&
                GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon;
            bool isBuilding =
                !isDungeon &&
                GameManager.Instance != null &&
                GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInsideBuilding;

            string displayName = go.name;
            if (displayName != null && displayName.Length > 120)
                displayName = displayName.Substring(0, 120);

            Debug.Log(
                $"[ExternalFoeMP] Client submitting '{displayName}' entityId={entityId} visualId={visualId} " +
                $"visualTex={visualMaleTexture}/{visualFemaleTexture} visualRangedFlags={visualRangedFlags} " +
                $"team={team} explicitTeam={hasExplicitTeam} reaction={reaction} hostile={liveHostile} " +
                $"pos={finalPosition}");

            ExternalFoeLifetimeDiagnostics diag = ExternalFoeLifetimeDiagnostics.Attach(
                go, "CLIENT_STAGING", PlayerMultiplayer.GetLocalPlayerNetIdSafe());
            if (diag != null)
                diag.Mark("CLIENT_BEFORE_COMMAND_SUBMIT");

            multiplayer.CmdSpawnExternallySuppliedFoe(
                finalPosition,
                finalRotation,
                finalScale,
                displayName,
                entityId,
                visualId,
                visualMaleTexture,
                visualFemaleTexture,
                visualRangedFlags,
                (int)reaction,
                (int)gender,
                allied,
                (int)team,
                hasExplicitTeam,
                liveHostile,
                currentHealth,
                maxHealth,
                classicDistance,
                GetEffectiveRequesterLevel(),
                isBuilding,
                isDungeon
            );

            // This was only the client's local staging object. The authoritative
            // network copy will arrive from the host.
            if (diag != null)
                diag.Mark("CLIENT_STAGING_INTENTIONAL_DESTROY");
            go.SetActive(false);
            Destroy(go);
        }


		// Returns true and sets testPoint when a valid spawn point is found.
// Contains the original LineOfSight/FOV, raycasts, and clearance checks.
bool TryFindSpawnPoint(out Vector3 testPoint, float minDistance, float maxDistance)
{
    const float overlapSphereRadius = 0.65f;
    const float separationDistance  = 1.25f;
    const float maxFloorDistance    = 4f;

    // Get rotation of spawn ray (original logic)
    Quaternion rotation;
    if (LineOfSightCheck)
    {
        float directionAngle = GameManager.Instance.MainCamera.fieldOfView + UnityEngine.Random.Range(0f, 4f);
        rotation = Quaternion.Euler(0, (UnityEngine.Random.value > 0.5f) ? -directionAngle : directionAngle, 0);
    }
    else
    {
        rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360), 0);
    }

    Vector3 angle          = (rotation * Vector3.forward).normalized;
    Vector3 spawnDirection = GameManager.Instance.PlayerObject.transform.TransformDirection(angle).normalized;
    Ray ray = new Ray(GameManager.Instance.PlayerObject.transform.position, spawnDirection);

    Vector3 currentPoint;
    if (Physics.Raycast(ray, out RaycastHit initialHit, maxDistance))
    {
        float cos_normal = Vector3.Dot(-spawnDirection, initialHit.normal.normalized);
        if (cos_normal < 1e-6f) { testPoint = default; return false; }

        float separationForward = separationDistance / cos_normal;

        float distanceSlack = initialHit.distance - separationForward - minDistance;
        if (distanceSlack < 0f) { testPoint = default; return false; }

        float extraDistance = UnityEngine.Random.Range(0f, Mathf.Min(2f, distanceSlack));
        currentPoint = initialHit.point - spawnDirection * (separationForward + extraDistance);
    }
    else
    {
        currentPoint = GameManager.Instance.PlayerObject.transform.position +
                       spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
    }

    // Must find a surface below
    if (!Physics.Raycast(new Ray(currentPoint, Vector3.down), out RaycastHit floorHit, maxFloorDistance))
    {
        testPoint = default; return false;
    }

    // Ensure this is open space
    testPoint = floorHit.point + Vector3.up * separationDistance;
    if (Physics.OverlapSphere(testPoint, overlapSphereRadius).Length > 0)
    {
        testPoint = default; return false;
    }

    return true;
}

		

        // Fine tunes foe position slightly based on mobility and enables GameObject
        void FinalizeFoe(GameObject go)
        {
            var mobileUnit = go.GetComponentInChildren<MobileUnit>();
            if (mobileUnit)
            {
                // Align ground creatures on surface, raise flying creatures slightly into air
                if (mobileUnit.Enemy.Behaviour != MobileBehaviour.Flying)
                    GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
                else
                    go.transform.localPosition += Vector3.up * 1.5f;
            }
            else
            {
                // Just align to ground
                GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
            }

            go.SetActive(true);
        }

        // Destroy other foes if replaced during spawn
        void DestroyOldFoeGameObjects(GameObject[] gameObjects)
        {
            if (gameObjects == null || gameObjects.Length == 0)
                return;

            foreach(GameObject go in gameObjects)
            {
                Destroy(go);
            }
        }

        #endregion
    }
}