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
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallConnect;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Utility;
using System.Collections.Generic;
using Mirror;
using System.Collections;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Example enemy senses.
    /// </summary>
    public class EnemySenses : NetworkBehaviour
    {
        public static readonly Vector3 ResetPlayerPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);

        public float SightRadius = 4096 * MeshReader.GlobalScale;       // Range of enemy sight
        public float HearingRadius = 25f;                               // Range of enemy hearing
        public float FieldOfView = 180f;                                // Enemy field of view

        const float predictionInterval = 0.0625f;

        MobileUnit mobile;
        DaggerfallEntityBehaviour entityBehaviour;
        QuestResourceBehaviour questBehaviour;
        EnemyMotor motor;
        EnemyEntity enemyEntity;
        EnemyWorldPosition worldPosition;
        bool targetInSight;
        bool playerInSight;
        bool targetInEarshot;
        Vector3 directionToTarget;
        float distanceToPlayer;
        float distanceToTarget;
        DaggerfallEntityBehaviour player;
        DaggerfallEntityBehaviour target;
        DaggerfallEntityBehaviour targetOnLastUpdate;
        DaggerfallEntityBehaviour secondaryTarget;
        bool sawSecondaryTarget;
        Vector3 secondaryTargetPos;
        EnemySenses targetSenses;
        float lastDistanceToTarget;
        float targetRateOfApproach;
        Vector3 lastKnownTargetPos = ResetPlayerPos;
        Vector3 oldLastKnownTargetPos = ResetPlayerPos;
        Vector3 predictedTargetPos = ResetPlayerPos;
        Vector3 predictedTargetPosWithoutLead = ResetPlayerPos;
        Vector3 lastPositionDiff;
        bool awareOfTargetForLastPrediction;
        DaggerfallActionDoor actionDoor;
        float distanceToActionDoor;
        bool hasEncounteredPlayer = false;
        bool wouldBeSpawnedInClassic = false;
        bool detectedTarget = false;
        uint timeOfLastStealthCheck = 0;
        bool blockedByIllusionEffect = false;
        float lastHadLOSTimer = 0f;

        float targetPosPredictTimer = 0f;
        bool targetPosPredict = false;

        float classicTargetUpdateTimer = 0f;
        const float systemTimerUpdatesDivisor = .0549254f;  // Divisor for updates per second by the system timer at memory location 0x46C.

        const float classicSpawnDespawnExterior = 4096 * MeshReader.GlobalScale;
        float classicSpawnXZDist = 0f;
        float classicSpawnYDistUpper = 0f;
        float classicSpawnYDistLower = 0f;
        float classicDespawnXZDist = 0f;
        float classicDespawnYDist = 0f;

        public DaggerfallEntityBehaviour Target
        {
            get { return target; }
            set { target = value; }
        }

        public DaggerfallEntityBehaviour SecondaryTarget
        {
            get { return secondaryTarget; }
            set { secondaryTarget = value; }
        }

        public bool TargetInSight
        {
            get { return targetInSight; }
        }

        public bool DetectedTarget
        {
            get { return detectedTarget; }
            set { detectedTarget = value; }
        }

        public bool TargetInEarshot
        {
            get { return targetInEarshot; }
        }

        public Vector3 DirectionToTarget
        {
            get { return directionToTarget; }
        }

        public float DistanceToPlayer
        {
            get { return distanceToPlayer; }
        }

        public float DistanceToTarget
        {
            get { return distanceToTarget; }
        }

        public Vector3 LastKnownTargetPos
        {
            get { return lastKnownTargetPos; }
            set { lastKnownTargetPos = value; }
        }

        public Vector3 OldLastKnownTargetPos
        {
            get { return oldLastKnownTargetPos; }
            set { oldLastKnownTargetPos = value; }
        }

        public Vector3 LastPositionDiff
        {
            get { return lastPositionDiff; }
            set { lastPositionDiff = value; }
        }

        public Vector3 PredictedTargetPos
        {
            get { return predictedTargetPos; }
            set { predictedTargetPos = value; }
        }

        public DaggerfallActionDoor LastKnownDoor
        {
            get { return actionDoor; }
            set { actionDoor = value; }
        }

        public float DistanceToDoor
        {
            get { return distanceToActionDoor; }
            set { distanceToActionDoor = value; }
        }

        public bool HasEncounteredPlayer
        {
            get { return hasEncounteredPlayer; }
            set { hasEncounteredPlayer = value; }
        }

        public bool WouldBeSpawnedInClassic
        {
            get { return wouldBeSpawnedInClassic; }
            set { wouldBeSpawnedInClassic = value; }
        }

        public QuestResourceBehaviour QuestBehaviour
        {
            get { return questBehaviour; }
            set { questBehaviour = value; }
        }

        public float TargetRateOfApproach
        {
            get { return targetRateOfApproach; }
            set { targetRateOfApproach = value; }
        }

        public float LastHadLOSTimer
        {
            get { return lastHadLOSTimer; }
            set { lastHadLOSTimer = value; }
        }



        //Delegates to allow mods to replace or extend senses logic.
        //Mods can potentially save the original value before replacing it, if access to default behaviour is still desired.
        public delegate bool BlockedByIllusionEffectCallback();
        public BlockedByIllusionEffectCallback BlockedByIllusionEffectHandler { get; set; }

        public delegate bool CanSeeTargetCallback(DaggerfallEntityBehaviour target);
        public CanSeeTargetCallback CanSeeTargetHandler { get; set; }

        public delegate bool CanHearTargetCallback();
        public CanHearTargetCallback CanHearTargetHandler { get; set; }

        public delegate bool CanDetectOtherwiseCallback(DaggerfallEntityBehaviour target);
        public CanDetectOtherwiseCallback CanDetectOtherwiseHandler { get; set; }

				[SyncVar(hook = nameof(OnTargetNetIdChanged))] public uint targetNetId;

private PlayerMultiplayer targetPlayer; // Cached reference


        // MP targeting separation:
        // "player" / PlayerTarget is the selected PlayerMultiplayer body that represents the player.
        // It is NOT proof this enemy has detected that player. Actual AI commitment still uses target,
        // targetInSight, detectedTarget, lastKnownTargetPos, and predictedTargetPos.
        private static readonly bool DEBUG_MP_TARGET_WAKE = false;

        public DaggerfallEntityBehaviour PlayerTarget
        {
            get { return player; }
        }

        private bool IsMultiplayerActive()
        {
            return NetworkServer.active || NetworkClient.active ||
                   (NetworkManager.singleton != null && NetworkManager.singleton.isNetworkActive);
        }

        private bool IsCurrentPlayerTarget(DaggerfallEntityBehaviour candidate)
        {
            return candidate != null && player != null && candidate == player;
        }

        private bool IsSinglePlayerAdvanced(DaggerfallEntityBehaviour candidate)
        {
            return candidate != null && GameManager.Instance != null &&
                   candidate == GameManager.Instance.PlayerEntityBehaviour;
        }

        private void ClearPlayerAdvancedTargetInMultiplayer(string reason)
        {
            if (!IsMultiplayerActive())
                return;

            DaggerfallEntityBehaviour playerAdvanced = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
            if (playerAdvanced == null)
                return;

            // In MP, PlayerAdvanced is the local single-player body and must never be the
            // network enemy AI's player proxy or active target. Enemies should target
            // PlayerMultiplayer only. If PlayerAdvanced leaks in, it gives EnemyMotor a valid
            // lastKnown/predicted position and wakes enemies instantly.
            if (target == playerAdvanced)
                ClearTargetTracking(reason + ":target-PlayerAdvanced");

            if (secondaryTarget == playerAdvanced)
            {
                secondaryTarget = null;
                sawSecondaryTarget = false;
                secondaryTargetPos = ResetPlayerPos;
            }

            if (player == playerAdvanced)
            {
                player = null;
                targetPlayer = null;
                targetNetId = 0;
                playerInSight = false;
                distanceToPlayer = 0;
            }
        }

        private bool IsUnderPlayerMultiplayer(DaggerfallEntityBehaviour behaviour)
        {
            if (behaviour == null)
                return false;

            PlayerMultiplayer pm = behaviour.GetComponent<PlayerMultiplayer>();
            if (pm != null)
                return true;

            return behaviour.GetComponentInParent<PlayerMultiplayer>() != null;
        }

        private static PlayerMultiplayer GetPlayerMultiplayerFromBehaviour(DaggerfallEntityBehaviour behaviour)
        {
            if (behaviour == null)
                return null;

            PlayerMultiplayer pm = behaviour.GetComponent<PlayerMultiplayer>();
            if (pm != null)
                return pm;

            return behaviour.GetComponentInParent<PlayerMultiplayer>();
        }

        private static bool IsUntargetableMultiplayerPlayer(PlayerMultiplayer playerMultiplayer)
        {
            if (playerMultiplayer == null)
                return false;

            // Downed players should be revivable but invisible to enemy AI targeting.
            // Respawning is also untargetable because the owner is already leaving the death state.
            if (playerMultiplayer.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
                return true;

            // Fallback for the short window where health has synced to zero before the explicit
            // Downed SyncVar reaches this enemy's owner/server.
            return playerMultiplayer.IsDownedForRevive;
        }

        private bool IsUntargetableMultiplayerBehaviour(DaggerfallEntityBehaviour behaviour)
        {
            if (!IsMultiplayerActive())
                return false;

            return IsUntargetableMultiplayerPlayer(GetPlayerMultiplayerFromBehaviour(behaviour));
        }

        private void ClearUntargetableMultiplayerPlayerTargets(string reason)
        {
            if (!IsMultiplayerActive())
                return;

            bool clearedPlayerProxy = false;

            if (IsUntargetableMultiplayerBehaviour(target))
                ClearTargetTracking(reason + ":target-downed-player");

            if (IsUntargetableMultiplayerBehaviour(secondaryTarget))
            {
                secondaryTarget = null;
                sawSecondaryTarget = false;
                secondaryTargetPos = ResetPlayerPos;
            }

            if (IsUntargetableMultiplayerBehaviour(player))
            {
                clearedPlayerProxy = true;
                player = null;
                targetPlayer = null;
                targetNetId = 0;
                playerInSight = false;
                distanceToPlayer = 0f;
                wouldBeSpawnedInClassic = false;
            }

            if (clearedPlayerProxy && DEBUG_MP_TARGET_WAKE)
                Debug.Log($"[MPTargetWakeDbg][ClearDowned] reason={reason} enemy='{name}' netId={GetComponent<NetworkIdentity>()?.netId}");
        }

        private void ClearTargetTracking(string reason)
        {
            if (DEBUG_MP_TARGET_WAKE && IsMultiplayerActive() && target != null)
            {
                Debug.Log($"[MPTargetWakeDbg][Clear] reason={reason} enemy='{name}' netId={GetComponent<NetworkIdentity>()?.netId} target='{target.name}' targetNetId={target.GetComponent<NetworkIdentity>()?.netId} targetEntityType={target.EntityType} detected={detectedTarget} targetInSight={targetInSight} lastKnownReset={lastKnownTargetPos == ResetPlayerPos} predReset={predictedTargetPos == ResetPlayerPos}");
            }

            target = null;
            targetSenses = null;
            secondaryTarget = null;
            sawSecondaryTarget = false;
            secondaryTargetPos = ResetPlayerPos;
            targetInSight = false;
            targetInEarshot = false;
            detectedTarget = false;
            lastKnownTargetPos = ResetPlayerPos;
            oldLastKnownTargetPos = ResetPlayerPos;
            predictedTargetPos = ResetPlayerPos;
            predictedTargetPosWithoutLead = ResetPlayerPos;
            directionToTarget = ResetPlayerPos;
            lastDistanceToTarget = 0;
            targetRateOfApproach = 0;
            distanceToTarget = 0;
            awareOfTargetForLastPrediction = false;
            lastPositionDiff = Vector3.zero;
        }

        private void DebugLogMpTargetWakeState(string phase)
        {
            if (!DEBUG_MP_TARGET_WAKE || !IsMultiplayerActive())
                return;

            if (target == null)
                return;

            bool targetHasPM = target.GetComponent<PlayerMultiplayer>() != null;
            PlayerMultiplayer parentPM = target.GetComponentInParent<PlayerMultiplayer>();
            EnemySenses ts = target.GetComponent<EnemySenses>();
            NetworkIdentity myNi = GetComponent<NetworkIdentity>();
            NetworkIdentity targetNi = target.GetComponent<NetworkIdentity>();
            NetworkIdentity playerNi = player != null ? player.GetComponent<NetworkIdentity>() : null;

            bool movingRelevant = predictedTargetPos != ResetPlayerPos || lastKnownTargetPos != ResetPlayerPos || !IsCurrentPlayerTarget(target);
            if (!movingRelevant)
                return;

          //  Debug.Log($"[MPTargetWakeDbg] phase={phase} enemy='{name}' netId={myNi?.netId} server={isServer} client={isClient} hasAuth={hasAuthority} target='{target.name}' targetNetId={targetNi?.netId} targetEntityType={target.EntityType} targetTeam={target.Entity?.Team} targetIsPlayerTarget={IsCurrentPlayerTarget(target)} targetHasPM={targetHasPM} targetParentPM={(parentPM != null ? parentPM.netId.ToString() : "none")} player='{(player != null ? player.name : "null")}' playerPM={(playerNi != null ? playerNi.netId.ToString() : "none")} syncedTargetNetId={targetNetId} detected={detectedTarget} targetInSight={targetInSight} playerInSight={playerInSight} earshot={targetInEarshot} wouldClassic={wouldBeSpawnedInClassic} targetSenseWould={(ts != null ? ts.WouldBeSpawnedInClassic.ToString() : "none")} distPlayer={distanceToPlayer:F2} distTarget={distanceToTarget:F2} lastKnownReset={lastKnownTargetPos == ResetPlayerPos} predReset={predictedTargetPos == ResetPlayerPos} giveUp={(motor != null ? motor.GiveUpTimer.ToString() : "noMotor")} lastKnown={lastKnownTargetPos} predicted={predictedTargetPos}");
        }

        void Start()
        {
            //Initialize delegates to standard defaults
            BlockedByIllusionEffectHandler = BlockedByIllusionEffect;
            CanSeeTargetHandler = CanSeeTarget;
            CanHearTargetHandler = CanHearTarget;
            CanDetectOtherwiseHandler = delegate (DaggerfallEntityBehaviour target) { return false; };

            mobile = GetComponent<DaggerfallEnemy>().MobileUnit;
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();

            if (entityBehaviour == null)
            {
                Debug.LogError("[EnemySenses] ERROR: entityBehaviour is NULL!");
                return;
            }

            // Preserve vanilla initialization whenever the entity is already available.
            // The coroutine is only a late-spawn fallback and must never acquire a combat target.
            enemyEntity = entityBehaviour.Entity as EnemyEntity;
            if (enemyEntity == null)
                StartCoroutine(EnsureEnemyEntityIsSet());

    motor = GetComponent<EnemyMotor>();
    questBehaviour = GetComponent<QuestResourceBehaviour>();
    worldPosition = GetComponent<EnemyWorldPosition>();

    // ✅ Network setup
   if (NetworkManager.singleton != null && NetworkManager.singleton.isNetworkActive)
    {
        if (isServer)
        {
            player = FindClosestMultiplayer();
            if (player != null)
            {
                PlayerMultiplayer pMP = player.GetComponent<PlayerMultiplayer>();
                if (pMP != null)
                {
                    targetNetId = pMP.netId;
                    AssignPlayerTeam(player);
                    Debug.Log($"[EnemySenses] (Server) Initial Player Set: {player.name} | NetID: {targetNetId}");
                }
                else
                {
                    Debug.LogWarning("[EnemySenses] (Server) Found player but missing PlayerMultiplayer component.");
                }
            }
            else
            {
                Debug.LogWarning("[EnemySenses] (Server) No PlayerMultiplayer found.");
            }
        }
         else
        {
            player = FindClosestMultiplayer();
            if (player != null)
            {
                PlayerMultiplayer pMP = player.GetComponent<PlayerMultiplayer>();
                if (pMP != null)
                {
                  //  AssignPlayerTeam(player);
                    Debug.Log($"[EnemySenses] (Client) Initial Player Set: {player.name} | NetID: {pMP.netId}");
                }
                else
                {
                    Debug.LogWarning("[EnemySenses] (Client) Found player but missing PlayerMultiplayer component.");
                }
            }
            else
            {
                Debug.LogWarning("[EnemySenses] (Client) No PlayerMultiplayer found initially.");
            }
            StartCoroutine(WaitForTargetSync());
        }
    }
    else
    {
        // Pure single-player keeps the original PlayerAdvanced reference and normal
        // classic wake-up/target-acquisition path. Do not force a target scan here.
        player = GameManager.Instance.PlayerEntityBehaviour;
    }

            ClearPlayerAdvancedTargetInMultiplayer("Start");

            short[] classicSpawnXZDistArray = { 1024, 384, 640, 768, 768, 768, 768 };
            short[] classicSpawnYDistUpperArray = { 128, 128, 128, 384, 768, 128, 256 };
            short[] classicSpawnYDistLowerArray = { 0, 0, 0, 0, -128, -768, 0 };
            short[] classicDespawnXZDistArray = { 1024, 1024, 1024, 1024, 768, 768, 768 };
            short[] classicDespawnYDistArray = { 384, 384, 384, 384, 768, 768, 768 };

            byte index = mobile.ClassicSpawnDistanceType;

            classicSpawnXZDist = classicSpawnXZDistArray[index] * MeshReader.GlobalScale;
            classicSpawnYDistUpper = classicSpawnYDistUpperArray[index] * MeshReader.GlobalScale;
            classicSpawnYDistLower = classicSpawnYDistLowerArray[index] * MeshReader.GlobalScale;
            classicDespawnXZDist = classicDespawnXZDistArray[index] * MeshReader.GlobalScale;
            classicDespawnYDist = classicDespawnYDistArray[index] * MeshReader.GlobalScale;

            // 180 degrees is classic's value. 190 degrees is actual human FOV according to online sources.
            if (DaggerfallUnity.Settings.EnhancedCombatAI)
                FieldOfView = 190;
        }
		
        private IEnumerator EnsureEnemyEntityIsSet()
        {
            const float maxWait = 2.0f;
            const float retryInterval = 0.2f;
            float elapsed = 0f;

            while (elapsed < maxWait)
            {
                if (entityBehaviour != null)
                    enemyEntity = entityBehaviour.Entity as EnemyEntity;

                if (enemyEntity != null)
                    yield break;

                yield return new WaitForSeconds(retryInterval);
                elapsed += retryInterval;
            }

            Debug.LogError($"[EnemySenses] FAILED: enemyEntity is STILL NULL after waiting! ({gameObject.name})");
        }
	

		
private void AssignPlayerTeam(DaggerfallEntityBehaviour playerEntity)
{
    if (playerEntity == null)
    {
        Debug.LogError("[EnemySenses] ERROR: Trying to assign team to a NULL player!");
        return;
    }

    // Ensure player entity exists
    if (playerEntity.Entity == null)
    {
        Debug.LogError($"[EnemySenses] ERROR: Player {playerEntity.name} does not have a valid Entity component!");
        return;
    }

    // Assign the correct team if it's a PlayerMultiplayer instance
    if (playerEntity.EntityType == EntityTypes.Player)
    {
        playerEntity.Entity.Team = MobileTeams.PlayerAlly; // ✅ Set PlayerMultiplayer's team
        Debug.Log($"[EnemySenses] Assigned Player {playerEntity.name} to Team: {playerEntity.Entity.Team}");
    }
    else
    {
        Debug.LogWarning($"[EnemySenses] WARNING: Tried to assign a team to {playerEntity.name}, but it is not a Player!");
    }
}

private static readonly List<EnemySenses> enemyUpdateQueue = new List<EnemySenses>();
private static int nextUpdateSlot = 0;

// Multiplayer target refresh is intentionally throttled. The previous code used
// enemyUpdateQueue.IndexOf(this) every FixedUpdate and incremented the global index
// from every enemy, which effectively made most enemies run the expensive retarget
// path every physics frame. With 40-60 enemies this becomes extremely expensive on
// the host.
private const int serverRetargetSlots = 16;
private const float serverRetargetInterval = 0.75f;
private int updateSlot;
private float nextServerRetargetTime;

void Awake()
{
    updateSlot = nextUpdateSlot++;
    enemyUpdateQueue.Add(this); // Register enemy in the update queue
    nextServerRetargetTime = Time.time + (updateSlot % serverRetargetSlots) * 0.05f;
}

void OnDestroy()
{
    enemyUpdateQueue.Remove(this); // Remove when enemy is destroyed
}


private static readonly List<PlayerMultiplayer> cachedPlayers = new List<PlayerMultiplayer>();
private static float lastGlobalCacheUpdate = -999f;
private const float globalCacheInterval = 1.0f;

private static void UpdatePlayerCache(bool force = false)
{
    if (!force && Time.time - lastGlobalCacheUpdate <= globalCacheInterval)
        return;

    cachedPlayers.Clear();
    cachedPlayers.AddRange(FindObjectsOfType<PlayerMultiplayer>());
    lastGlobalCacheUpdate = Time.time;
}

private static DaggerfallEntityBehaviour FindCachedPlayerEntityByNetId(uint netId)
{
    UpdatePlayerCache();

    foreach (PlayerMultiplayer p in cachedPlayers)
    {
        if (p == null)
            continue;

        if (p.netId == netId)
        {
            if (IsUntargetableMultiplayerPlayer(p))
                return null;

            return p.GetComponent<DaggerfallEntityBehaviour>();
        }
    }

    return null;
}


        // Find the closest PlayerMultiplayer in the scene.
        //
        // Multiplayer distance is not always the same as raw Unity distance:
        // - Exterior / building-interior enemies need DF X/Z distance so players in a
        //   different world location but similar local Unity coordinates are ignored.
        // - Dungeon enemies must NOT use DF X/Z distance because dungeon enemies and
        //   players are anchored to the dungeon entrance while the player moves in the
        //   artificial underground dungeon space. For those, use Unity/Y-slot distance.
        const float multiplayerTargetUnityPerDF = 1f / 40f;
        const float multiplayerTargetWorldDistanceLimitDF = 16000f; // 400 Unity with 0.025 conversion
        const float multiplayerTargetYLimit = 180f;                 // safely below the 200Y separation threshold
        const float networkDungeonTargetYThreshold = -300f;         // only real underground network dungeons use Unity/Y-only targeting

        public DaggerfallEntityBehaviour FindClosestMultiplayer()
        {
            UpdatePlayerCache();

            if (cachedPlayers.Count == 0)
            {
                // MP must never fall back to PlayerAdvanced. If there is no
                // PlayerMultiplayer candidate yet, leave the player proxy unset.
                if (IsMultiplayerActive())
                    return null;

                return player ?? GameManager.Instance.PlayerEntityBehaviour;
            }

            DaggerfallEntityBehaviour closest = null;
            float closestScore = float.MaxValue;

            foreach (PlayerMultiplayer p in cachedPlayers)
            {
                if (p == null || !p.isActiveAndEnabled)
                    continue;

                float score;
                if (!TryGetMultiplayerTargetScore(p, out score))
                    continue;

                if (score < closestScore)
                {
                    DaggerfallEntityBehaviour candidateEntity = p.GetComponent<DaggerfallEntityBehaviour>();
                    if (candidateEntity == null)
                        continue;

                    closestScore = score;
                    closest = candidateEntity;
                }
            }

            if (closest != null)
                return closest;

            // MP must never use PlayerAdvanced as fallback. Keep an existing
            // PlayerMultiplayer proxy only if it is still actually a PlayerMultiplayer.
            if (IsMultiplayerActive())
            {
                if (player != null)
                {
                    PlayerMultiplayer currentPM = player.GetComponent<PlayerMultiplayer>();
                    if (currentPM != null && !IsUntargetableMultiplayerPlayer(currentPM))
                        return player;
                }

                return null;
            }

            return player ?? GameManager.Instance.PlayerEntityBehaviour;
        }

        private bool TryGetMultiplayerTargetScore(PlayerMultiplayer candidate, out float score)
        {
            score = float.MaxValue;

            if (candidate == null)
                return false;

            if (IsUntargetableMultiplayerPlayer(candidate))
                return false;

            EnemyWorldPosition ewp = worldPosition != null ? worldPosition : GetComponent<EnemyWorldPosition>();
            float dy = candidate.transform.position.y - transform.position.y;
            float absY = Mathf.Abs(dy);

            // Dungeon enemies live in artificial underground Unity space. Their DF X/Z is
            // intentionally anchored to the dungeon entrance, so target by Unity/Y slot.
            if (IsActualNetworkDungeonEnemyForTargeting(ewp))
            {
                if (absY >= multiplayerTargetYLimit)
                    return false;

                Vector3 delta = candidate.transform.position - transform.position;
                score = delta.sqrMagnitude;
                return true;
            }

            PositionMultiplayer candidatePosition = candidate.GetComponent<PositionMultiplayer>();

            // Exterior and building-interior enemies use DF X/Z + Unity Y hybrid distance.
            // This prevents an enemy from targeting a far-away player who happens to share
            // similar local Unity X/Z coordinates.
            if (ewp != null && ewp.initialized && candidatePosition != null)
            {
                if (absY >= multiplayerTargetYLimit)
                    return false;

                float dxU = (candidatePosition.x - ewp.worldX) * multiplayerTargetUnityPerDF;
                float dzU = (candidatePosition.z - ewp.worldZ) * multiplayerTargetUnityPerDF;
                float maxU = multiplayerTargetWorldDistanceLimitDF * multiplayerTargetUnityPerDF;

                float xzScore = dxU * dxU + dzU * dzU;
                if (xzScore > maxU * maxU)
                    return false;

                score = xzScore + dy * dy;
                return true;
            }

            // Fallback for very early spawn frames before EnemyWorldPosition is initialized.
            // Keep the old Unity behaviour, but avoid cross-slot targeting for known interior
            // spawns if we can identify them.
            if (ewp != null && ewp.isInteriorSpawn && absY >= multiplayerTargetYLimit)
                return false;

            Vector3 unityDelta = candidate.transform.position - transform.position;
            score = unityDelta.sqrMagnitude;
            return true;
        }



        private bool IsActualNetworkDungeonEnemyForTargeting(EnemyWorldPosition ewp)
        {
            if (ewp == null || !ewp.isDungeonSpawn)
                return false;

            // Some non-dungeon enemies can be incorrectly flagged as dungeon enemies when
            // a Dungeon object exists in the scene. Only enemies actually in the network
            // dungeon underground band should use dungeon/Y-slot targeting.
            return transform.position.y <= networkDungeonTargetYThreshold;
        }


[Server]
public void ClearTargetIfDisconnected()
{
    if (target == null || target.Equals(null))
    {
        // Debug.LogWarning($"[ClearTargetIfDisconnected] {gameObject.name} target is null or destroyed, clearing target.");
        target = null;
        targetNetId = 0;
        player = null;
        return;
    }

    if (player != null && player.GetComponent<NetworkIdentity>() == null)
    {
        // Debug.LogWarning($"[ClearTargetIfDisconnected] {gameObject.name} detected disconnected player, clearing target.");
        target = null;
        targetNetId = 0;
        player = null;
    }
}



[Server]
public void SetTarget(DaggerfallEntityBehaviour newTarget)
{
    // Clear any invalid or disconnected targets first
    ClearTargetIfDisconnected();

    // Check if the new target is null or has been destroyed
    if (newTarget == null || newTarget.Equals(null))
    {
        // Debug.LogWarning($"[SetTarget] {gameObject.name} LOST its target! Previous Target: {player?.name}");
        player = null;
        targetNetId = 0; // Reset target ID to indicate no target
        return;
    }

    // Ensure we get the correct PlayerMultiplayer component first
    PlayerMultiplayer newTargetPlayer = newTarget.GetComponent<PlayerMultiplayer>();
    if (newTargetPlayer == null)
    {
        // Debug.LogWarning($"[SetTarget] {gameObject.name} tried to target {newTarget.name}, but it lacks PlayerMultiplayer component!");
        return;
    }

    if (IsUntargetableMultiplayerPlayer(newTargetPlayer))
    {
        if (player == newTarget || target == newTarget)
            ClearTargetTracking("SetTarget-downed-player");

        player = null;
        targetPlayer = null;
        targetNetId = 0;
        playerInSight = false;
        distanceToPlayer = 0f;
        wouldBeSpawnedInClassic = false;
        return;
    }

    // In this project host player netId can be 0. Do not treat targetNetId == 0 as
    // "no target" here. Also update when player is null or points at a different object,
    // even if the netId value itself did not change.
    if (player != newTarget || targetNetId != newTargetPlayer.netId)
    {
        // Debug.Log($"[SetTarget] {gameObject.name} switching target: {player?.name} → {newTarget.name} (NetID: {newTargetPlayer.netId})");
        targetNetId = newTargetPlayer.netId; // Sync NetID
        player = newTarget; // Correct type is now assigned

        // Additional Debug Logging
        // Debug.Log($"[SetTarget] SUCCESS: {gameObject.name} now targeting {player.name} (NetID: {targetNetId})");
    }
}







       private IEnumerator WaitForTargetSync()
        {
            // Host player netId can be 0 in this project, so do not wait for targetNetId != 0.
            // Instead poll the cached player list for the currently synced id.
            float timeout = Time.time + 5f;
            while (Time.time < timeout)
            {
                DaggerfallEntityBehaviour syncedPlayer = FindCachedPlayerEntityByNetId(targetNetId);
                if (syncedPlayer != null)
                {
                    player = syncedPlayer;
                    // Debug.Log($"[EnemySenses] (Client) Target synced: {player.name} with netid {targetNetId}");
                    yield break;
                }

                yield return new WaitForSeconds(0.1f);
            }

            if (player == null)
                Debug.LogWarning("[EnemySenses] (Client) Failed to sync player target.");
        }
    private void OnTargetNetIdChanged(uint oldValue, uint newValue)
    {
        if (!isServer) // Only clients need to handle this
        {
            DaggerfallEntityBehaviour syncedPlayer = FindCachedPlayerEntityByNetId(newValue);
            if (syncedPlayer != null)
            {
                player = syncedPlayer;
            }
            else
            {
                player = null;
                targetPlayer = null;
                playerInSight = false;
                distanceToPlayer = 0f;
            }
        }
    }


	

        void FixedUpdate()
        {
     if (GameManager.Instance.DisableAI)
        return;

    ClearPlayerAdvancedTargetInMultiplayer("FixedUpdateStart");
    ClearUntargetableMultiplayerPlayerTargets("FixedUpdateStart");

    // Server-only multiplayer retarget check. This is intentionally throttled and staggered.
    // Do not use enemyUpdateQueue.IndexOf(this) here; that was O(enemy count) per enemy per FixedUpdate.
    if (NetworkServer.active && Time.time >= nextServerRetargetTime && ((Time.frameCount + updateSlot) % serverRetargetSlots == 0))
    {
        nextServerRetargetTime = Time.time + serverRetargetInterval;
        DaggerfallEntityBehaviour closestPlayer = FindClosestMultiplayer();
        if (closestPlayer != null && player != closestPlayer)
            SetTarget(closestPlayer);
        else if (closestPlayer == null)
        {
            player = null;
            targetPlayer = null;
            targetNetId = 0;
            playerInSight = false;
            distanceToPlayer = 0f;
            wouldBeSpawnedInClassic = false;
        }
    }

            ClearPlayerAdvancedTargetInMultiplayer("AfterServerRetarget");
            ClearUntargetableMultiplayerPlayerTargets("AfterServerRetarget");

            targetPosPredictTimer += Time.deltaTime;
            if (targetPosPredictTimer >= predictionInterval)
            {
                targetPosPredictTimer = 0f;
                targetPosPredict = true;
            }
            else
                targetPosPredict = false;

            // Update whether enemy would be spawned or not in classic.
            // Only check if within the maximum possible distance (Just under 1094 classic units)
            if (GameManager.ClassicUpdate && player != null)
            {
                if (distanceToPlayer < 1094 * MeshReader.GlobalScale)
                {
                    float upperXZ;
                    float upperY = 0;
                    float lowerY = 0;
                    bool playerInside = GameManager.Instance.PlayerGPS.GetComponent<PlayerEnterExit>().IsPlayerInside;

                    if (!playerInside)
                    {
                        upperXZ = classicSpawnDespawnExterior;
                    }
                    else
                    {
                        if (!wouldBeSpawnedInClassic)
                        {
                            upperXZ = classicSpawnXZDist;
                            upperY = classicSpawnYDistUpper;
                            lowerY = classicSpawnYDistLower;
                        }
                        else
                        {
                            upperXZ = classicDespawnXZDist;
                            upperY = classicDespawnYDist;
                        }
                    }

                    float YDiffToPlayer = transform.position.y - player.transform.position.y;
                    float YDiffToPlayerAbs = Mathf.Abs(YDiffToPlayer);
                    float distanceToPlayerXZ = Mathf.Sqrt(distanceToPlayer * distanceToPlayer - YDiffToPlayerAbs * YDiffToPlayerAbs);

                    wouldBeSpawnedInClassic = true;

                    if (distanceToPlayerXZ > upperXZ)
                        wouldBeSpawnedInClassic = false;

                    if (playerInside)
                    {
                        if (lowerY == 0)
                        {
                            if (YDiffToPlayerAbs > upperY)
                                wouldBeSpawnedInClassic = false;
                        }
                        else if (YDiffToPlayer < lowerY || YDiffToPlayer > upperY)
                            wouldBeSpawnedInClassic = false;
                    }
                }
                else
                    wouldBeSpawnedInClassic = false;
            }
            else if (player == null)
            {
                wouldBeSpawnedInClassic = false;
            }

            if (GameManager.ClassicUpdate)
            {
                classicTargetUpdateTimer += Time.deltaTime / systemTimerUpdatesDivisor;

                if (target != null && target.Entity.CurrentHealth <= 0)
                {
                    target = null;
                }

                // Non-hostile mode
                if (GameManager.Instance.PlayerEntity.NoTargetMode || !motor.IsHostile)
                {
                    if (IsCurrentPlayerTarget(target))
                        target = null;
                    if (IsCurrentPlayerTarget(secondaryTarget))
                        secondaryTarget = null;
                }

                // Reset these values if no target
                if (target == null)
                {
                    lastKnownTargetPos = ResetPlayerPos;
                    predictedTargetPos = ResetPlayerPos;
                    directionToTarget = ResetPlayerPos;
                    lastDistanceToTarget = 0;
                    targetRateOfApproach = 0;
                    distanceToTarget = 0;
                    targetSenses = null;

                    // If we have a valid secondary target that we acquired when we got the primary, switch to it.
                    // There will only be a secondary target if using enhanced combat AI.
                    if (secondaryTarget != null && secondaryTarget.Entity.CurrentHealth > 0)
                    {
                        target = secondaryTarget;

                        // If the secondary target was actually seen, use the last place we saw it to begin pursuit.
                        if (sawSecondaryTarget)
                            lastKnownTargetPos = secondaryTargetPos;
                        awareOfTargetForLastPrediction = false;
                    }
                }

                // Compare change in target position to give AI some ability to read opponent's movements
                if (target != null && target == targetOnLastUpdate)
                {
                    if (DaggerfallUnity.Settings.EnhancedCombatAI)
                        targetRateOfApproach = (lastDistanceToTarget - distanceToTarget);
                }
                else
                {
                    lastDistanceToTarget = 0;
                    targetRateOfApproach = 0;
                }

                if (target != null)
                {
                    lastDistanceToTarget = distanceToTarget;
                    targetOnLastUpdate = target;
                }
            }

            if (player != null)
            {
                if (IsUntargetableMultiplayerBehaviour(player))
                {
                    ClearUntargetableMultiplayerPlayerTargets("BeforePlayerProcessing");
                    return;
                }

                // Get distance to player
                Vector3 toPlayer = player.transform.position - transform.position;
                distanceToPlayer = toPlayer.magnitude;

                // If out of classic spawn range, still check for direct LOS to player so that enemies who see player will
                // try to attack.
                if (!wouldBeSpawnedInClassic)
                {
                    distanceToTarget = distanceToPlayer;
                    directionToTarget = toPlayer.normalized;
                    playerInSight = CanSeeTargetHandler(player);
                }

                if (classicTargetUpdateTimer > 5)
                {
                    classicTargetUpdateTimer = 0f;

                    // Is enemy in area around player or can see player?
                    if (wouldBeSpawnedInClassic || playerInSight)
                    {
                        GetTargets();

                        if (target != null && !IsCurrentPlayerTarget(target))
                            targetSenses = target.GetComponent<EnemySenses>();
                        else
                            targetSenses = null;
                    }

                    // Make targeted character also target this character if it doesn't have a target yet.
                    if (target != null && targetSenses && targetSenses.Target == null)
                    {
                        targetSenses.Target = entityBehaviour;
                    }
                }

                if (target == null)
                {
                    targetInSight = false;
                    detectedTarget = false;
                    return;
                }

                if (!wouldBeSpawnedInClassic && IsCurrentPlayerTarget(target))
                {
                    distanceToTarget = distanceToPlayer;
                    directionToTarget = toPlayer.normalized;
                    targetInSight = CanSeeTargetHandler(player);
                }
                else
                {
                    Vector3 toTarget = target.transform.position - transform.position;
                    distanceToTarget = toTarget.magnitude;
                    directionToTarget = toTarget.normalized;
                    targetInSight = CanSeeTargetHandler(target);
                }

                // Classic stealth mechanics would be interfered with by hearing, so only enable
                // hearing if the enemy has detected the target. If target is visible we can omit hearing.
                if (detectedTarget && !targetInSight)
                    targetInEarshot = CanHearTargetHandler();
                else
                    targetInEarshot = false;

                // Note: In classic an enemy can continue to track the player as long as their
                // giveUpTimer is > 0. Since the timer is reset to 200 on every detection this
                // would make chameleon and shade essentially useless, since the enemy is sure
                // to detect the player during one of the many AI updates. Here, the enemy has to
                // successfully see through the illusion spell each classic update to continue
                // to know where the player is.
                if (GameManager.ClassicUpdate)
                {
                    blockedByIllusionEffect = BlockedByIllusionEffectHandler();
                    if (lastHadLOSTimer > 0)
                        lastHadLOSTimer--;
                }

                if (!blockedByIllusionEffect && (targetInSight || targetInEarshot))
                {
                    detectedTarget = true;
                    lastKnownTargetPos = target.transform.position;
                    lastHadLOSTimer = 200f;
                }
                else if (!blockedByIllusionEffect && StealthCheck())
                {
                    detectedTarget = true;

                    // Only get the target's location from the stealth check if we haven't had
                    // actual LOS for a while. This gives better pursuit behavior since enemies
                    // will go to the last spot they saw the player instead of walking into walls.
                    if (lastHadLOSTimer <= 0)
                        lastKnownTargetPos = target.transform.position;
                }
                else if (CanDetectOtherwiseHandler(target))
                    detectedTarget = true;
                else
                    detectedTarget = false;

                if (oldLastKnownTargetPos == ResetPlayerPos)
                    oldLastKnownTargetPos = lastKnownTargetPos;

                if (predictedTargetPos == ResetPlayerPos || !DaggerfallUnity.Settings.EnhancedCombatAI)
                    predictedTargetPos = lastKnownTargetPos;

                // Predict target's next position
                if (targetPosPredict && lastKnownTargetPos != ResetPlayerPos)
                {
                    // Be sure to only take difference of movement if we've seen the target for two consecutive prediction updates
                    if (!blockedByIllusionEffect && targetInSight)
                    {
                        if (awareOfTargetForLastPrediction)
                            lastPositionDiff = lastKnownTargetPos - oldLastKnownTargetPos;

                        // Store current last known target position for next prediction update
                        oldLastKnownTargetPos = lastKnownTargetPos;

                        awareOfTargetForLastPrediction = true;
                    }
                    else
                    {
                        awareOfTargetForLastPrediction = false;
                    }

                    if (DaggerfallUnity.Settings.EnhancedCombatAI)
                    {
                        float moveSpeed = (enemyEntity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale;
                        predictedTargetPos = PredictNextTargetPos(moveSpeed);
                    }
                }

                if (detectedTarget && !hasEncounteredPlayer && IsCurrentPlayerTarget(target))
                {
                    hasEncounteredPlayer = true;

                    // Check appropriate language skill to see if player can pacify enemy
                    if (!questBehaviour && entityBehaviour && motor &&
                        (entityBehaviour.EntityType == EntityTypes.EnemyMonster || entityBehaviour.EntityType == EntityTypes.EnemyClass))
                    {
                        DFCareer.Skills languageSkill = enemyEntity.GetLanguageSkill();
                        if (languageSkill != DFCareer.Skills.None)
                        {
                            PlayerEntity player = GameManager.Instance.PlayerEntity;
                            if (FormulaHelper.CalculateEnemyPacification(player, languageSkill))
                            {
                                motor.IsHostile = false;
                                var enemyName = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                                var languageSkillName = DaggerfallUnity.Instance.TextProvider.GetSkillName(languageSkill);
                                DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("languagePacified").Replace("%e", enemyName).Replace("%s", languageSkillName), 5);
                                player.TallySkill(languageSkill, 3);    // BCHG: increased skill uses from 1 in classic on success to make raising language skills easier
                            }
                            else if (languageSkill != DFCareer.Skills.Etiquette && languageSkill != DFCareer.Skills.Streetwise)
                                player.TallySkill(languageSkill, 1);
                        }
                    }
                }
            }

            ClearPlayerAdvancedTargetInMultiplayer("FixedUpdateEnd");
            DebugLogMpTargetWakeState("FixedUpdateEnd");

            // If target is player and in sight then raise enemy alert on player
            // This can only be lowered again by killing an enemy or escaping for some amount of time
            // Any enemies actively targeting player will continue to raise alert state
            if ((Target == GameManager.Instance.PlayerEntityBehaviour || IsCurrentPlayerTarget(Target)) && TargetInSight)
                GameManager.Instance.PlayerEntity.SetEnemyAlert(true);
        }


        #region Public Methods

        public Vector3 PredictNextTargetPos(float interceptSpeed)
        {
            Vector3 assumedCurrentPosition;
            RaycastHit tempHit;

            if (predictedTargetPosWithoutLead == ResetPlayerPos)
            {
                predictedTargetPosWithoutLead = lastKnownTargetPos;
            }

            // If aware of target, if distance is too far or can see nothing is there, use last known position as assumed current position
            if (targetInSight || targetInEarshot || (predictedTargetPos - transform.position).magnitude > SightRadius + mobile.Enemy.SightModifier
                || !Physics.Raycast(transform.position, (predictedTargetPosWithoutLead - transform.position).normalized, out tempHit, SightRadius + mobile.Enemy.SightModifier))
            {
                assumedCurrentPosition = lastKnownTargetPos;
            }
            // If not aware of target and predicted position may still be good, use predicted position
            else
            {
                assumedCurrentPosition = predictedTargetPosWithoutLead;
            }

            float divisor = predictionInterval;

            // Account for mid-interval call by DaggerfallMissile
            if (targetPosPredictTimer != 0)
            {
                divisor = targetPosPredictTimer;
                lastPositionDiff = lastKnownTargetPos - oldLastKnownTargetPos;
            }

            // Let's solve cone / line intersection (quadratic equation)
            Vector3 d = assumedCurrentPosition - transform.position;
            Vector3 v = lastPositionDiff / divisor;
            float a = v.sqrMagnitude - interceptSpeed * interceptSpeed;
            float b = 2 * Vector3.Dot(d, v);
            float c = d.sqrMagnitude;

            Vector3 prediction = assumedCurrentPosition;

            float t = -1;
            if (Mathf.Abs(a) >= 1e-5)
            {
                float disc = b * b - 4 * a * c;
                if (disc >= 0)
                {
                    // find the minimal positive solution
                    float discSqrt = Mathf.Sqrt(disc) * Mathf.Sign(a);
                    t = (-b - discSqrt) / (2 * a);
                    if (t < 0)
                        t = (-b + discSqrt) / (2 * a);
                }
            }
            else
            {
                // degenerated cases
                if (Mathf.Abs(b) >= 1e-5)
                    t = -d.sqrMagnitude / b;
            }

            if (t >= 0)
            {
                prediction = assumedCurrentPosition + v * t;

                // Don't predict target will move through obstacles (prevent predicting movement through walls)
                RaycastHit hit;
                Ray ray = new Ray(assumedCurrentPosition, (prediction - assumedCurrentPosition).normalized);
                if (Physics.Raycast(ray, out hit, (prediction - assumedCurrentPosition).magnitude))
                    prediction = assumedCurrentPosition;
            }

            // Store prediction minus lead for next prediction update
            predictedTargetPosWithoutLead = assumedCurrentPosition + lastPositionDiff;

            return prediction;
        }

        public bool StealthCheck()
        {
            if (GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeonCastle && !motor.IsHostile)
                return false;

            if (!wouldBeSpawnedInClassic)
                return false;

            if (distanceToTarget > 1024 * MeshReader.GlobalScale)
                return false;

            uint gameMinutes = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            if (gameMinutes == timeOfLastStealthCheck)
                return detectedTarget;

            PlayerMultiplayer mpTarget = IsCurrentPlayerTarget(target) ? GetPlayerMultiplayerFromBehaviour(target) : null;
            if (mpTarget != null)
            {
                if (mpTarget.PlayerMPIsStealthModeActive)
                {
                    if ((gameMinutes & 1) == 1)
                        return detectedTarget;
                }
                else if (hasEncounteredPlayer)
                    return true;

                timeOfLastStealthCheck = gameMinutes;

                int mpStealthChance = CalculateStealthChanceForSyncedSkill(distanceToTarget, mpTarget.PlayerMPStealthSkill);
                return Dice100.FailedRoll(mpStealthChance);
            }

            if (IsCurrentPlayerTarget(target))
            {
                PlayerMotor playerMotor = GameManager.Instance.PlayerMotor;
                if (playerMotor.IsMovingLessThanHalfSpeed)
                {
                    if ((gameMinutes & 1) == 1)
                        return detectedTarget;
                }
                else if (hasEncounteredPlayer)
                    return true;

                PlayerEntity player = GameManager.Instance.PlayerEntity;
                if (player.TimeOfLastStealthCheck != gameMinutes)
                {
                    player.TallySkill(DFCareer.Skills.Stealth, 1);
                    player.TimeOfLastStealthCheck = gameMinutes;
                }
            }

            timeOfLastStealthCheck = gameMinutes;

            int stealthChance = FormulaHelper.CalculateStealthChance(distanceToTarget, target);

            return Dice100.FailedRoll(stealthChance);
        }

        private static int CalculateStealthChanceForSyncedSkill(float distanceToTarget, int stealthSkill)
        {
            int liveStealthSkill = Mathf.Clamp(stealthSkill, 0, 200);
            return 2 * ((int)(distanceToTarget / MeshReader.GlobalScale) * liveStealthSkill >> 10);
        }

        public bool BlockedByIllusionEffect()
        {
            // In classic if the target is another AI character true is always returned.

            // Some enemy types can see through these effects.
            if (mobile.Enemy.SeesThroughInvisibility)
                return false;

            // If not one of the above enemy types, and target has invisibility,
            // detection is always blocked.
            if (target.Entity.IsInvisible)
                return true;

            // If target doesn't have any illusion effect, detection is not blocked.
            if (!target.Entity.IsBlending && !target.Entity.IsAShade)
                return false;

            // Target has either chameleon or shade. Try to see through it.
            int chance;
            if (target.Entity.IsBlending)
                chance = 8;
            else // is a shade
                chance = 4;

            return Dice100.FailedRoll(chance);
        }

        public bool TargetIsWithinYawAngle(float targetAngle, Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - transform.position;
            toTarget.y = 0;

            Vector3 enemyDirection2D = transform.forward;
            enemyDirection2D.y = 0;

            return Vector3.Angle(toTarget, enemyDirection2D) < targetAngle;
        }

        public bool TargetHasBackTurned()
        {
            Vector3 toTarget = predictedTargetPos - transform.position;
            toTarget.y = 0;

            Vector3 targetDirection2D;

            if (IsCurrentPlayerTarget(target))
            {
                Camera mainCamera = GameObject.FindGameObjectWithTag("MainCamera").GetComponent<Camera>();
                targetDirection2D = -new Vector3(mainCamera.transform.forward.x, 0, mainCamera.transform.forward.z);
            }
            else
                targetDirection2D = -new Vector3(target.transform.forward.x, 0, target.transform.forward.z);

            return Vector3.Angle(toTarget, targetDirection2D) > 157.5f;
        }

        public bool TargetIsWithinPitchAngle(float targetAngle)
        {
            Vector3 toTarget = predictedTargetPos - transform.position;
            Vector3 directionToLastKnownTarget2D = toTarget.normalized;
            Plane verticalTransformToLastKnownPos = new Plane(predictedTargetPos, transform.position, transform.position + Vector3.up);
            // first project enemy direction to horizontal plane.
            Vector3 enemyDirection2D = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            // next project enemy direction to vertical plane intersecting with last known position
            enemyDirection2D = Vector3.ProjectOnPlane(enemyDirection2D, verticalTransformToLastKnownPos.normal);

            float angle = Vector3.Angle(directionToLastKnownTarget2D, enemyDirection2D);

            return angle < targetAngle;
        }

        public bool TargetIsAbove()
        {
            return predictedTargetPos.y > transform.position.y;
        }

        #endregion

        #region Private Methods

        // Enemies consider only other enemies and the player
        // Civilian Mobile NPCs are not handled here
        IEnumerable<DaggerfallEntityBehaviour> GetActiveTargetEntityBehaviours()
        {
            foreach(DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                // MP player avatars can carry enemy-like visual/entity components. Those must not
                // be enumerated as normal enemy-vs-enemy targets, or dungeon enemies can start
                // hunting multiplayer player visuals instead of using the dedicated player proxy.
                if (IsMultiplayerActive() && IsUnderPlayerMultiplayer(behaviour))
                    continue;

                yield return behaviour;
            }

            if (player != null)
            {
                // In MP, only PlayerMultiplayer may enter the player-target scan. Never yield
                // PlayerAdvanced as a multiplayer target candidate. Downed/respawning players
                // are intentionally skipped so they remain revivable instead of attackable.
                if (!IsMultiplayerActive() ||
                    (player.GetComponent<PlayerMultiplayer>() != null && !IsUntargetableMultiplayerBehaviour(player)))
                    yield return player;
            }
        }

        void GetTargets()
        {
            DaggerfallEntityBehaviour highestPriorityTarget = null;
            DaggerfallEntityBehaviour secondHighestPriorityTarget = null;
            float highestPriority = -1;
            float secondHighestPriority = -1;
            bool sawSelectedTarget = false;
            Vector3 directionToTargetHolder = directionToTarget;
            float distanceToTargetHolder = distanceToTarget;

            foreach (DaggerfallEntityBehaviour targetBehaviour in GetActiveTargetEntityBehaviours())
            {
                if (targetBehaviour == null)
                    continue;

                if (IsUntargetableMultiplayerBehaviour(targetBehaviour))
                    continue;

                // MP player visuals can carry enemy-like components. Never let those enter normal
                // enemy-vs-enemy target selection as independent monsters.
                if (IsMultiplayerActive() && IsUnderPlayerMultiplayer(targetBehaviour) && !IsCurrentPlayerTarget(targetBehaviour))
                    continue;

                EnemyEntity targetEntity = null;
                if (!IsCurrentPlayerTarget(targetBehaviour))
                    targetEntity = targetBehaviour.Entity as EnemyEntity;

                // Can't target self
                if (targetBehaviour == entityBehaviour)
                    continue;

                // Evaluate potential targets
				// Debug.Log($"[EnemySenses] Checking target: {targetBehaviour.name} | " +
    // $"NetID: {targetBehaviour.GetComponent<NetworkIdentity>()?.netId} | " +
    // $"EntityType: {targetBehaviour.EntityType}");
				
				
                if (targetBehaviour.EntityType == EntityTypes.EnemyMonster || targetBehaviour.EntityType == EntityTypes.EnemyClass
                    || targetBehaviour.EntityType == EntityTypes.Player)
                {
				//	Debug.Log($"[EnemySenses]1 {enemyEntity.MobileEnemy.Team}");
					
                    // NoTarget mode
                    if ((GameManager.Instance.PlayerEntity.NoTargetMode || !motor.IsHostile || enemyEntity.MobileEnemy.Team == MobileTeams.PlayerAlly) && IsCurrentPlayerTarget(targetBehaviour))
						            {
                // Debug.Log($"[EnemySenses] Skipping {targetBehaviour.name} (NoTarget mode or ally)");
				// Debug.Log($"[EnemySenses]2 {enemyEntity.MobileEnemy.Team}");
				{
    // Debug.LogError("[GetTargets] ERROR: enemyEntity is NULL!");
}
                        continue;
						 }

                    //Pacified enemies should not attack player allies.
                    if (!motor.IsHostile && targetEntity != null && targetEntity.Team == MobileTeams.PlayerAlly)
                        continue;

                    //Player allies should not attack pacified enemies.
                    if (enemyEntity.Team == MobileTeams.PlayerAlly && !IsCurrentPlayerTarget(targetBehaviour))
                    {
                        EnemyMotor targetMotor = targetBehaviour.GetComponent<EnemyMotor>();
                        if (targetMotor && !targetMotor.IsHostile)
                            continue;
                    }

                    // Can't target ally
                    if (IsCurrentPlayerTarget(targetBehaviour) && enemyEntity.Team == MobileTeams.PlayerAlly)
						{
						     // Debug.Log($"[EnemySenses] Skipping {targetBehaviour.name} (Same Team)");
                        continue;
						 }
                    else if (DaggerfallUnity.Settings.EnemyInfighting && !enemyEntity.SuppressInfighting && targetEntity != null && !targetEntity.SuppressInfighting)
                    {
                        if (targetEntity.Team == enemyEntity.Team)
							{
							         // Debug.Log($"[EnemySenses] Skipping {targetBehaviour.name} (Infighting rules)");
                            continue;
							}
                    }
                    else
                    {
                        if (!IsCurrentPlayerTarget(targetBehaviour) && enemyEntity.MobileEnemy.Team != MobileTeams.PlayerAlly)
							{
							// Debug.Log($"[EnemySenses] Skipping {targetBehaviour.name} (Not a valid target) " +
    // $"| Target NetID: {targetBehaviour.GetComponent<NetworkIdentity>()?.netId} " +
    // $"| Target Team: {targetBehaviour.Entity.Team} | Enemy Team: {enemyEntity.MobileEnemy.Team} " +
    // $"| Enemy: {gameObject.name} | Enemy NetID: {GetComponent<NetworkIdentity>()?.netId}");
                            continue;
							 }
                    }

                    // Quest enemy AI only targets player by default unless explicitly marked as attackable by a mod/quest.
                    if (questBehaviour && !questBehaviour.IsAttackableByAI && !IsCurrentPlayerTarget(targetBehaviour))
                        continue;

                    EnemySenses targetSenses = null;
                    if (targetBehaviour.EntityType == EntityTypes.EnemyMonster || targetBehaviour.EntityType == EntityTypes.EnemyClass)
                        targetSenses = targetBehaviour.GetComponent<EnemySenses>();

                    // For now, quest AI can't be targeted
                    if (targetSenses && targetSenses.QuestBehaviour && !targetSenses.QuestBehaviour.IsAttackableByAI)
                        continue;

                    Vector3 toTarget = targetBehaviour.transform.position - transform.position;
                    directionToTarget = toTarget.normalized;
                    distanceToTarget = toTarget.magnitude;

                    bool see = CanSeeTargetHandler(targetBehaviour);

                    // MP-only: allow enemy infighting again, but do not allow passive spawn-range
                    // proximity to be the first reason one enemy targets another. Your earlier logs
                    // showed unseen enemies selecting GiantBat just because targetSenses.WouldBeSpawnedInClassic
                    // was true, which woke the whole dungeon. In MP, a non-player target must either
                    // be actually visible, or already be this enemy's tracked/retaliation target.
                    if (IsMultiplayerActive() && !IsCurrentPlayerTarget(targetBehaviour) && !see)
                    {
                        bool alreadyTrackingThisTarget =
                            targetBehaviour == target &&
                            (detectedTarget || lastKnownTargetPos != ResetPlayerPos || predictedTargetPos != ResetPlayerPos);

                        if (!alreadyTrackingThisTarget)
                            continue;
                    }

                    // Is potential target neither visible nor in area around player? If so, reject as target.
                    if (targetSenses && !targetSenses.WouldBeSpawnedInClassic && !see)
                        continue;

                    float priority = 0;

                    // Add 5 priority if this potential target isn't already targeting someone
                    if (targetSenses && targetSenses.Target == null)
                        priority += 5;

                    if (see)
                        priority += 10;

                    // Add distance priority
                    float distancePriority = 30 - distanceToTarget;
                    if (distancePriority < 0)
                        distancePriority = 0;

                    priority += distancePriority;
                    if (priority > highestPriority)
                    {
                        secondHighestPriority = highestPriority;
                        highestPriority = priority;
                        secondHighestPriorityTarget = highestPriorityTarget;
                        highestPriorityTarget = targetBehaviour;
                        sawSecondaryTarget = sawSelectedTarget;
                        sawSelectedTarget = see;
                        directionToTargetHolder = directionToTarget;
                        distanceToTargetHolder = distanceToTarget;
                    }
                    else if (priority > secondHighestPriority)
                    {
                        sawSecondaryTarget = see;
                        secondHighestPriority = priority;
                        secondHighestPriorityTarget = targetBehaviour;
                    }
                }
            }

            // Restore direction and distance values
            directionToTarget = directionToTargetHolder;
            distanceToTarget = distanceToTargetHolder;

            targetInSight = sawSelectedTarget;
            target = highestPriorityTarget;
			
			    // Debug.Log($"[EnemySenses] (AI) Final Target: {(target ? target.name : "None")} " +
          // $"| Target NetID: {target?.GetComponent<NetworkIdentity>()?.netId} " +
          // $"| CanSeeTarget: {targetInSight} " +
          // $"| Enemy: {gameObject.name} | Enemy NetID: {GetComponent<NetworkIdentity>()?.netId}");
		  
            if (DaggerfallUnity.Settings.EnhancedCombatAI && secondHighestPriorityTarget)
            {
                secondaryTarget = secondHighestPriorityTarget;
                if (sawSecondaryTarget)
                    secondaryTargetPos = secondaryTarget.transform.position;
            }
        }

        bool CanSeeTarget(DaggerfallEntityBehaviour target)
        {
            bool seen = false;
            actionDoor = null;

            if (IsUntargetableMultiplayerBehaviour(target))
                return false;

            if (distanceToTarget < SightRadius + mobile.Enemy.SightModifier)
            {
                // Check if target in field of view
                float angle = Vector3.Angle(directionToTarget, transform.forward);
                if (angle < FieldOfView * 0.5f)
                {
                    // Check if line of sight to target
                    RaycastHit hit;

                    // Set origin of ray to approximate eye position
                    CharacterController controller = entityBehaviour.transform.GetComponent<CharacterController>();
                    Vector3 eyePos = transform.position + controller.center;
                    eyePos.y += controller.height / 3;

                    // Set destination to the target's approximate eye position
                    controller = target.transform.GetComponent<CharacterController>();
                    Vector3 targetEyePos = target.transform.position + controller.center;
                    targetEyePos.y += controller.height / 3;

                    // Check if can see.
                    Vector3 eyeToTarget = targetEyePos - eyePos;
                    Vector3 eyeDirectionToTarget = eyeToTarget.normalized;
                    Ray ray = new Ray(eyePos, eyeDirectionToTarget);

                    if (Physics.Raycast(ray, out hit, SightRadius))
                    {
                        // Check if hit was target
                        DaggerfallEntityBehaviour entity = hit.transform.gameObject.GetComponent<DaggerfallEntityBehaviour>();
                        if (entity == target)
                            seen = true;

                        // Check if hit was an action door
                        DaggerfallActionDoor door = hit.transform.gameObject.GetComponent<DaggerfallActionDoor>();
                        if (door != null)
                        {
                            actionDoor = door;
                            distanceToActionDoor = Vector3.Distance(transform.position, actionDoor.transform.position);
                        }
                    }
                }
            }

            return seen;
        }

        bool CanHearTarget()
        {
            if (IsUntargetableMultiplayerBehaviour(target))
                return false;

            float hearingScale = 1f;

            // If something is between enemy and target then return false (was reduce hearingScale by half), to minimize
            // enemies walking against walls.
            // Hearing is not impeded by doors or other non-static objects
            RaycastHit hit;
            Ray ray = new Ray(transform.position, directionToTarget);
            if (Physics.Raycast(ray, out hit))
            {
                //DaggerfallEntityBehaviour entity = hit.transform.gameObject.GetComponent<DaggerfallEntityBehaviour>();
                if (GameObjectHelper.IsStaticGeometry(hit.transform.gameObject))
                    return false;
            }

            // TODO: Modify this by how much noise the target is making
            return distanceToTarget < (HearingRadius * hearingScale) + mobile.Enemy.HearingModifier;
        }

        #endregion
    }
}
