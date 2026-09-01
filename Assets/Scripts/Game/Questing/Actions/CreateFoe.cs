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
using System;
using System.Text.RegularExpressions;
using DaggerfallWorkshop.Utility;
using FullSerializer;
using DaggerfallWorkshop.Game.Utility;
using System.Collections.Generic;

namespace DaggerfallWorkshop.Game.Questing
{
    /// <summary>
    /// Spawn a Foe resource into the world.
    /// </summary>
    public class CreateFoe : ActionTemplate
    {
        const string optionsMatchStr = @"msg (?<msgId>\d+)";

        Symbol foeSymbol;
        uint spawnInterval;
        int spawnMaxTimes = -1;
        int spawnChance;
        int msgMessageID = -1;

        ulong lastSpawnTime = 0;
        int spawnCounter = 0;

        // Pure-client CreateFoe waves are requested from the host rather than placed locally.
        // If a later action in the same task raises QuestBreak (for example a Say popup),
        // Task.Update() can return before committing prevTriggered=true. On the next quest tick
        // InitialiseOnSet() is called again even though the task was never genuinely cleared.
        // The host/server path survives this because spawnInProgress remains true, but a pure
        // client deliberately has no local spawnInProgress and would otherwise reset
        // spawnCounter to 0 and send the same wave again.
        //
        // Keep a runtime-only fence after the first successful client wave request. It is
        // cleared only when the owning task is genuinely rearmed (IsTriggered=false).
        bool pureClientWaveSentSinceTaskRearm = false;
        Task lastUpdateCaller = null;

        bool spawnInProgress = false;
        GameObject[] pendingFoeGameObjects;
        int pendingFoesSpawned;

        // MP HOST ONLY:
        // Do not create real network enemies at Vector3.zero and relocate them later.
        // GameObjectHelper.CreateFoeGameObjects() NetworkServer.Spawn()s immediately on
        // the host, so the old staging workflow allowed EnemyMotor to initialize at Y=0
        // before the object was teleported into an MP dungeon around Y=-500/-1200/etc.
        // That teleport could then be interpreted as a gigantic physical fall.
        //
        // In this mode pendingFoeGameObjects is only a slot/count array. Each real enemy
        // is created only AFTER PlaceFoeFreely/force-place has resolved its final point.
        bool mpHostDeferredRealSpawn = false;
        Foe mpHostDeferredFoeResource = null;
        MobileTypes mpHostDeferredFoeType = MobileTypes.None;

        ulong spawnWaveStartTime = 0;
        const uint ForcePlaceAfterSeconds = 5;

        
        const float MaxFloorDeltaYPrimary = 1.25f;
        const float ClientSpawnLift = 0.35f; // extra lift to avoid starting inside floor on remote
        const float ClientFloorProbeYOffset = 0.30f;
        const float ClientFloorProbeDownDistance = 1.20f;
   // prefer same floor as player
        const float MaxFloorDeltaYSecondary = 1.5f;  // fallback for small split-levels
// Cached placement context for force-place (so we can place even if host player is not moving / not in same interior).
        Transform forcePlaceParent = null;
        Vector3 forcePlaceOrigin = Vector3.zero;
bool isSendAction = false;


        // Multiplayer-local-only quest blacklist.
        // These quests must not share player-specific cure/faction/progression state.
        // CreateFoe waves from them still spawn as normal MP enemies, but without
        // QuestResourceBehaviour linkage to another player's quest.
        static bool IsQuestSharingBlacklisted(Quest q)
        {
            if (q == null || string.IsNullOrEmpty(q.QuestName))
                return false;

            return string.Equals(q.QuestName, "L0A01L00", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "The Acceptance Test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "$CUREVAM", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "Cure for Vampirism", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "$CUREWER", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "Cure for Lycanthropy", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "O0A0AL00", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(q.QuestName, "The Qualifying Examination", StringComparison.OrdinalIgnoreCase);
        }

        // Debug instrumentation
        static int s_nextDebugActionId = 1;
        int debugActionId = 0;

        void EnsureDebugActionId()
        {
            if (debugActionId == 0)
                debugActionId = System.Threading.Interlocked.Increment(ref s_nextDebugActionId);
        }

        int QuestHash()
        {
            return ParentQuest != null ? ParentQuest.GetHashCode() : 0;
        }

        string DescribeMatchingQuests()
        {
            try
            {
                if (ParentQuest == null || QuestMachine.Instance == null || string.IsNullOrEmpty(ParentQuest.QuestName))
                    return "<none>";
                ulong[] found = QuestMachine.Instance.FindQuests(ParentQuest.QuestName, true);
                if (found == null || found.Length == 0)
                    return "<none>";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < found.Length; i++)
                {
                    Quest qq = QuestMachine.Instance.GetQuest(found[i]);
                    if (qq == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append("uid=").Append(qq.UID).Append("/qHash=").Append(qq.GetHashCode());
                }
                return sb.Length > 0 ? sb.ToString() : "<none>";
            }
            catch (System.Exception ex)
            {
                return "<err:" + ex.Message + ">";
            }
        }

        void LogDbg(string phase, string extra)
        {
            EnsureDebugActionId();
            string qn = ParentQuest != null ? ParentQuest.QuestName : "<null>";
            ulong uid = ParentQuest != null ? ParentQuest.UID : 0UL;
            string matches = DescribeMatchingQuests();
            UnityEngine.Debug.Log($"[CreateFoeDbg] {phase} actionId={debugActionId} questUID={uid} qHash={QuestHash()} questName='{qn}' {extra} matchingQuests=[{matches}]");
        }

        public override string Pattern
        {
            get
            {
                return @"create foe (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes (?<infinite>indefinitely) with (?<percent>\d+)% success|" +
                       @"create foe (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes (?<count>\d+) times with (?<percent>\d+)% success|" +
                       @"(?<send>send) (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes (?<count>\d+) times with (?<percent>\d+)% success|" +
                       @"(?<send>send) (?<symbol>[a-zA-Z0-9_.-]+) every (?<minutes>\d+) minutes with (?<percent>\d+)% success";
            }
        }

        public CreateFoe(Quest parentQuest)
            : base(parentQuest)
        {
            PlayerEnterExit.OnTransitionDungeonExterior += PlayerEnterExit_OnTransitionExterior;
            PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
            StreamingWorld.OnInitWorld += StreamingWorld_OnInitWorld;
        }

        public override void InitialiseOnSet()
        {
            // Pure-client only: preserve wave progress when Task.Update() re-enters this
            // still-triggered task after QuestBreak without ever committing prevTriggered.
            // A genuine task clear calls RearmAction(), which releases this fence.
            if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active &&
                pureClientWaveSentSinceTaskRearm)
            {
                LogDbg(
                    "client-init-preserved",
                    $"foeSymbol='{(foeSymbol != null ? foeSymbol.Name : "<null>")}' " +
                    $"spawnCounter={spawnCounter} lastSpawnTime={lastSpawnTime} " +
                    "reason=same-task QuestBreak re-entry");
                return;
            }

            lastSpawnTime = 0;
            spawnCounter = 0;
        }

        public override void RearmAction()
        {
            base.RearmAction();

            // Task.SetTriggerValue(false) assigns triggered=false before it rearms actions.
            // This is the reliable distinction between a real task rearm and the repeated
            // InitialiseOnSet() calls caused by QuestBreak while the task remains true.
            if (lastUpdateCaller != null && !lastUpdateCaller.IsTriggered)
            {
                if (pureClientWaveSentSinceTaskRearm && Debug.isDebugBuild)
                {
                    LogDbg(
                        "client-rearm-reset",
                        $"foeSymbol='{(foeSymbol != null ? foeSymbol.Name : "<null>")}' " +
                        "owning task genuinely cleared; client resend fence released");
                }

                pureClientWaveSentSinceTaskRearm = false;
            }
        }

        public override IQuestAction CreateNew(string source, Quest parentQuest)
        {
            // Source must match pattern
            Match match = Test(source);
            if (!match.Success)
                return null;

            // Factory new action
            CreateFoe action = new CreateFoe(parentQuest);
            action.foeSymbol = new Symbol(match.Groups["symbol"].Value);
            action.spawnInterval = (uint)Parser.ParseInt(match.Groups["minutes"].Value) * 60;
            action.spawnMaxTimes = Parser.ParseInt(match.Groups["count"].Value);
            action.spawnChance = Parser.ParseInt(match.Groups["percent"].Value);

            // Handle infinite
            if (!string.IsNullOrEmpty(match.Groups["infinite"].Value))
                action.spawnMaxTimes = -1;

            // Handle "send" variant
            if (!string.IsNullOrEmpty(match.Groups["send"].Value))
            {
                action.isSendAction = true;

                // "send" without "count" implies infinite
                if (action.spawnMaxTimes == 0)
                    action.spawnMaxTimes = -1;
            }

            // Split options from declaration
            string optionsSource = source.Substring(match.Length);
            MatchCollection options = Regex.Matches(optionsSource, optionsMatchStr);
            foreach (Match option in options)
            {
                // Message ID
                Group msgIDGroup = option.Groups["msgId"];
                if (msgIDGroup.Success)
                    action.msgMessageID = Parser.ParseInt(msgIDGroup.Value);
            }

            action.LogDbg("created", $"foeSymbol='{action.foeSymbol.Name}' interval={action.spawnInterval} maxTimes={action.spawnMaxTimes} chance={action.spawnChance} send={action.isSendAction}");
            return action;
        }
		
		
		
		
public override void Update(Task caller)
{
    // Remember the owning task so RearmAction() can distinguish an actual clear from
    // QuestBreak-driven re-entry of the same still-triggered task.
    lastUpdateCaller = caller;

    ulong gameSeconds = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
    EnsureDebugActionId();

    // Init spawn timer on first update
    if (lastSpawnTime == 0)
        lastSpawnTime = gameSeconds - (uint)UnityEngine.Random.Range(0, spawnInterval);

    // Stop if max foes already spawned (unless infinite)
    if (spawnCounter >= spawnMaxTimes && spawnMaxTimes != -1)
        return;

    // If a server-side placement wave is in progress, continue placing
    if (spawnInProgress)
    {
        // Safety: if wave already finished, stop before trying to place again (avoids IndexOutOfRange)
        if (pendingFoeGameObjects == null || pendingFoesSpawned >= (pendingFoeGameObjects.Length))
        {
            spawnInProgress = false;
            spawnCounter++;
            lastSpawnTime = gameSeconds;
            ClearMpHostDeferredSpawnState();
            return;
        }

        TryPlacement();
            // If placement is stalled (e.g. player not moving), force-place remaining foes after timeout.
            ForcePlaceRemainingFoes(GameManager.Instance.PlayerEnterExit);
        GameManager.Instance.RaiseOnEncounterEvent();

        if (pendingFoesSpawned >= (pendingFoeGameObjects?.Length ?? 0))
        {
            spawnInProgress = false;
            spawnCounter++;
            ClearMpHostDeferredSpawnState();
        }
        return;
    }

    // Time to attempt new wave?
    bool timeForWave = (gameSeconds >= lastSpawnTime + spawnInterval);
    if (!timeForWave)
        return;

    // Roll chance
    if (Dice100.FailedRoll(spawnChance))
    {
        lastSpawnTime = gameSeconds; // consume interval even on fail (vanilla)
        return;
    }

    // Get the Foe resource
    Foe foe = ParentQuest.GetFoe(foeSymbol);
    if (foe == null)
    {
        SetComplete();
        throw new Exception(string.Format("create foe could not find Foe with symbol name {0}", Symbol.Name));
    }

    // Do not spawn if foe hidden
    if (foe.IsHidden)
    {
        lastSpawnTime = gameSeconds;
        return;
    }

    // For "send" variant, only when inside a location rect (vanilla behavior)
    if (isSendAction && !GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
        return;

    bool mpActive = Mirror.NetworkClient.active || Mirror.NetworkServer.active;
    bool suppressQuestLinkForMP = mpActive && IsQuestSharingBlacklisted(ParentQuest);

    if (suppressQuestLinkForMP && Debug.isDebugBuild)
        LogDbg("local-only-createfoe", $"foeSymbol='{foeSymbol.Name}' questName='{(ParentQuest != null ? ParentQuest.QuestName : "<null>")}' will spawn without quest resource link");

    LogDbg("wave-ready", $"foeSymbol='{foeSymbol.Name}' foeResSymbol='{foe.Symbol.Name}' foeType='{foe.FoeType}' spawnCounter={spawnCounter} spawnMaxTimes={spawnMaxTimes} lastSpawnTime={lastSpawnTime} gameSeconds={gameSeconds} caller='{(caller != null ? caller.Symbol.Name : "<null>")}' inBuilding={GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true} inDungeon={GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideDungeon == true} interior='{(GameManager.Instance?.PlayerEnterExit?.Interior ? GameManager.Instance.PlayerEnterExit.Interior.name : "<none>")}' dungeon='{(GameManager.Instance?.PlayerEnterExit?.Dungeon ? GameManager.Instance.PlayerEnterExit.Dungeon.name : "<none>")}'");

    // ===== SERVER/HOST or Single-Player =====
    if (!mpActive || Mirror.NetworkServer.active)
    {
        LogDbg("server-path", $"foeSymbol='{foeSymbol.Name}' foeType='{foe.FoeType}' spawnCounterBefore={spawnCounter} lastSpawnTime={lastSpawnTime}");
        // Vanilla: build batch and use TryPlacement on server/host
        CreatePendingFoeSpawn(foe, suppressQuestLinkForMP);
        TryPlacement();
        GameManager.Instance.RaiseOnEncounterEvent();
        lastSpawnTime = gameSeconds;
        return;
    }

    // ===== CLIENT =====
    // We do NOT spawn locally. We compute safe positions and ask host to spawn AT those positions.
    PlayerMultiplayer pm = PlayerMultiplayer.GetLocalPlayerForCommand("CreateFoe.Update");
    if (pm == null)
    {
        // Can't send yet; try again next tick without advancing timers
        return;
    }

    // Where to place this wave (client computes, host uses).
    //
    // Dungeons need a stricter path than the old generic 5-7m radial search. Small
    // dungeon rooms/corridors often have no valid point that far away, and the old
    // fallback could silently use an UNVALIDATED point several metres through a wall
    // or over the void. That is exactly how client-owned wave enemies could begin
    // falling outside the playable room.
    var pee = GameManager.Instance?.PlayerEnterExit;
    bool inDungeonAtRequest =
        pee != null &&
        pee.IsPlayerInsideDungeon &&
        pee.Dungeon != null;

    List<Vector3> positions;
    if (inDungeonAtRequest)
    {
        positions = BuildSafeDungeonWavePositions(
            foe.SpawnCount,
            pee.Dungeon.transform);

        Debug.Log(
            $"[CreateFoeMP][DungeonSafe] client selected " +
            $"{(positions != null ? positions.Count : 0)}/{foe.SpawnCount} position(s)");

        // Never manufacture an unvalidated dungeon point. If even the player's
        // current floor cannot be resolved this tick, leave the action pending and
        // retry on the next quest tick rather than spawn into the void.
        if (positions == null || positions.Count == 0)
        {
            Debug.LogWarning(
                "[CreateFoeMP][DungeonSafe] No supported dungeon spawn point found. " +
                "Wave remains pending and will retry instead of using a void fallback.");
            return;
        }
    }
    else
    {
        positions = BuildSuggestedWavePositions(foe.SpawnCount);
        Debug.Log($"[CreateFoe] client: BuildSuggestedWavePositions returned {(positions != null ? positions.Count : 0)} positions");

        var interior = pee != null ? pee.Interior : null;
        if (interior != null)
        {
            var vanilla = ComputeWavePositionsLikeVanillaPlaceFoeFreely(foe.SpawnCount, interior);
            Debug.Log($"[CreateFoe] client: vanilla returned {(vanilla != null ? vanilla.Count : 0)} positions");
            if (vanilla != null && vanilla.Count > 0)
            {
                positions = vanilla;
                Debug.Log("[CreateFoe] client: using vanilla PlaceFoeFreely-style positions");
            }
        }

        if (positions == null || positions.Count == 0)
        {
            // Existing non-dungeon fallback. Dungeon waves never use this.
            Vector3 fallback = GameManager.Instance.PlayerObject.transform.position +
                               GameManager.Instance.PlayerObject.transform.forward * 6f;
            positions = new System.Collections.Generic.List<Vector3> { fallback };
        }
    }

    if (positions != null && positions.Count > 0)
        Debug.Log($"[CreateFoe] client: final positions count={positions.Count} first={positions[0]}");
    else
        Debug.Log("[CreateFoe] client: final positions EMPTY (unexpected)");

    // Client’s interior state at request time (buildings only, per your rule)
    bool isInteriorAtRequest = GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true;

    // Filter indoor candidate positions (avoid outside-shell hits when standing near problematic spots).
    if (isInteriorAtRequest && positions != null && positions.Count > 0)
    {
        var indoor = new System.Collections.Generic.List<Vector3>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            if (LooksIndoors(positions[i]))
                indoor.Add(positions[i]);
        }

        if (indoor.Count == 0)
        {
            Debug.LogWarning("[CreateFoe] client: no indoor spawn points found at current player position - will retry next tick.");
            return; // don't send a bad outside position; try again next Update (player can move slightly)
        }

        positions = indoor;
        Debug.Log($"[CreateFoe] client: indoor-filter kept {positions.Count} position(s), first={positions[0]}");
    }


    // Quest context (so server marks QuestSpawn & attaches QuestResourceBehaviour).
    // For local-only blacklisted quests, keep questUID=0 and foeSymbolName empty so
    // the host creates normal enemies instead of quest-linked shared foes.
    ulong questUID = 0UL;
    string foeSymbolName = string.Empty;
    if (!suppressQuestLinkForMP && foe.ParentQuest != null)
    {
        questUID = foe.ParentQuest.UID;
        foeSymbolName = foe.Symbol.Name;
    }

    LogDbg("client-send", $"foeSymbol='{foeSymbol.Name}' foeResSymbol='{foeSymbolName}' foeType='{foe.FoeType}' spawnCount={foe.SpawnCount} spawnCounterBefore={spawnCounter} spawnMaxTimes={spawnMaxTimes} lastSpawnTime={lastSpawnTime} positionsCount={(positions != null ? positions.Count : 0)} isInteriorAtRequest={isInteriorAtRequest} inBuilding={GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true} inDungeon={GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideDungeon == true} interior='{(GameManager.Instance?.PlayerEnterExit?.Interior ? GameManager.Instance.PlayerEnterExit.Interior.name : "<none>")}' dungeon='{(GameManager.Instance?.PlayerEnterExit?.Dungeon ? GameManager.Instance.PlayerEnterExit.Dungeon.name : "<none>")}'");
    // Send to server: spawn directly at provided positions
    pm.CmdCreateFoesWithPositions(
        positions.ToArray(),
        foe.FoeType,
        foe.SpawnCount,
        MobileReactions.Hostile,
        false,                 // alliedToPlayer
        questUID,
        foeSymbolName,
        isInteriorAtRequest
    );

    // Show the "msg" text on client just like vanilla
    if (msgMessageID != -1)
    {
        ParentQuest.ShowMessagePopup(msgMessageID, oncePerQuest: true);
        msgMessageID = -1;
    }

    // Advance timing locally so this quest action proceeds as if the wave started.
    // Set the runtime fence only after the Command was actually issued. If position
    // selection or command-owner lookup failed above, the action remains free to retry.
    lastSpawnTime = gameSeconds;
    spawnCounter++;
    pureClientWaveSentSinceTaskRearm = true;
    LogDbg("client-advanced", $"foeSymbol='{foeSymbol.Name}' spawnCounterNow={spawnCounter} lastSpawnTimeNow={lastSpawnTime}");
    // Do NOT set spawnInProgress on the client; host owns placement.
}




// ===== CLIENT placement helper =====

bool IsColliderInsideDungeonHierarchy(Collider collider, Transform dungeonRoot)
{
    if (collider == null || dungeonRoot == null)
        return false;

    Transform t = collider.transform;
    return t == dungeonRoot || t.IsChildOf(dungeonRoot);
}

bool TryFindDungeonFloor(
    Vector3 horizontalPoint,
    Transform dungeonRoot,
    float referenceFloorY,
    float maxFloorDelta,
    out Vector3 floorPoint)
{
    floorPoint = Vector3.zero;
    if (dungeonRoot == null)
        return false;

    // Cast across a short local vertical range and choose the floor closest to
    // the player's current floor. This avoids stacked lower dungeon geometry.
    Vector3 start = new Vector3(
        horizontalPoint.x,
        referenceFloorY + 2.25f,
        horizontalPoint.z);

    RaycastHit[] hits = Physics.RaycastAll(
        start,
        Vector3.down,
        5.0f,
        Physics.DefaultRaycastLayers,
        QueryTriggerInteraction.Ignore);

    if (hits == null || hits.Length == 0)
        return false;

    bool found = false;
    float bestScore = float.PositiveInfinity;
    RaycastHit best = default;

    for (int i = 0; i < hits.Length; i++)
    {
        RaycastHit hit = hits[i];
        if (hit.collider == null ||
            hit.normal.y < 0.7f ||
            !IsColliderInsideDungeonHierarchy(hit.collider, dungeonRoot))
            continue;

        // Ignore live actors/quest objects as "floor".
        Transform ht = hit.collider.transform;
        if (ht.GetComponentInParent<DaggerfallEnemy>() != null ||
            ht.GetComponentInParent<PlayerMultiplayer>() != null ||
            ht.GetComponentInParent<QuestResourceBehaviour>() != null)
            continue;

        float score = Mathf.Abs(hit.point.y - referenceFloorY);
        if (score > maxFloorDelta)
            continue;

        if (!found || score < bestScore)
        {
            found = true;
            bestScore = score;
            best = hit;
        }
    }

    if (!found)
        return false;

    floorPoint = best.point;
    return true;
}

bool HasDungeonFloorFootprint(
    Vector3 floorPoint,
    Transform dungeonRoot,
    float radius = 0.22f)
{
    Vector3[] offsets = new Vector3[]
    {
        Vector3.zero,
        new Vector3( radius, 0f, 0f),
        new Vector3(-radius, 0f, 0f),
        new Vector3(0f, 0f,  radius),
        new Vector3(0f, 0f, -radius),
    };

    for (int i = 0; i < offsets.Length; i++)
    {
        Vector3 start = floorPoint + offsets[i] + Vector3.up * 0.30f;
        RaycastHit hit;
        if (!Physics.Raycast(
                start,
                Vector3.down,
                out hit,
                0.70f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            return false;

        if (hit.collider == null ||
            hit.normal.y < 0.7f ||
            !IsColliderInsideDungeonHierarchy(hit.collider, dungeonRoot) ||
            Mathf.Abs(hit.point.y - floorPoint.y) > 0.20f)
            return false;
    }

    return true;
}

bool HasOpenLineFromPlayerToDungeonPoint(
    Vector3 playerFloor,
    Vector3 candidateFloor)
{
    Vector3 from = playerFloor + Vector3.up * 0.85f;
    Vector3 to = candidateFloor + Vector3.up * 0.85f;
    Vector3 delta = to - from;
    float distance = delta.magnitude;

    if (distance <= 0.25f)
        return true;

    Vector3 dir = delta / distance;

    // Move the ray start out of the player's own capsule.
    from += dir * Mathf.Min(0.55f, distance * 0.25f);
    distance = Vector3.Distance(from, to);
    if (distance <= 0.20f)
        return true;

    RaycastHit hit;
    return !Physics.Raycast(
        from,
        dir,
        out hit,
        Mathf.Max(0f, distance - 0.15f),
        Physics.DefaultRaycastLayers,
        QueryTriggerInteraction.Ignore);
}

bool TryBuildSafeDungeonWavePoint(
    Vector3 horizontalPoint,
    Transform dungeonRoot,
    Vector3 playerFloor,
    out Vector3 spawnPoint)
{
    spawnPoint = Vector3.zero;

    Vector3 floor;
    if (!TryFindDungeonFloor(
            horizontalPoint,
            dungeonRoot,
            playerFloor.y,
            MaxFloorDeltaYSecondary,
            out floor))
        return false;

    if (!HasDungeonFloorFootprint(floor, dungeonRoot))
        return false;

    // Keep client-requested dungeon waves in the same locally connected room/
    // corridor space as the player. This intentionally prefers a visible enemy
    // in front/near the player over a hidden enemy outside the playable shell.
    if (!HasOpenLineFromPlayerToDungeonPoint(playerFloor, floor))
        return false;

    // Dungeon rooms should have overhead geometry. This rejects open void points
    // that happen to raycast onto some distant/technical floor collider.
    RaycastHit ceiling;
    if (!Physics.Raycast(
            floor + Vector3.up * 0.20f,
            Vector3.up,
            out ceiling,
            8.0f,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore) ||
        ceiling.collider == null ||
        !IsColliderInsideDungeonHierarchy(ceiling.collider, dungeonRoot))
        return false;

    const float approxRadius = 0.28f;
    const float approxHeight = 1.80f;

    // Test body clearance using floor-relative capsule points. Keep the bottom
    // sphere slightly above the floor so the floor itself is not counted.
    Vector3 capBottom = floor + Vector3.up * (approxRadius + 0.06f);
    Vector3 capTop = floor + Vector3.up * (approxHeight - approxRadius);
    if (Physics.CheckCapsule(
            capBottom,
            capTop,
            approxRadius,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore))
        return false;

    // Match DFU's AlignControllerToGround convention closely. The server still
    // performs the real controller alignment using the actual enemy height.
    spawnPoint = floor + Vector3.up * (approxHeight * 0.52f + 0.05f);
    return true;
}

List<Vector3> BuildSafeDungeonWavePositions(
    int count,
    Transform dungeonRoot)
{
    var results = new List<Vector3>(Mathf.Max(0, count));

    if (count <= 0 ||
        dungeonRoot == null ||
        GameManager.Instance == null ||
        GameManager.Instance.PlayerObject == null)
        return results;

    Transform player = GameManager.Instance.PlayerObject.transform;

    // Resolve the player's actual supported floor first. We never fall back to
    // a blind transform+forward point in a dungeon.
    Vector3 playerFloor;
    if (!TryFindDungeonFloor(
            player.position,
            dungeonRoot,
            player.position.y,
            2.5f,
            out playerFloor))
    {
        Debug.LogWarning(
            $"[CreateFoeMP][DungeonSafe] Could not resolve player floor at {player.position}.");
        return results;
    }

    const float minDistance = 1.0f;
    const float maxDistance = 4.25f;
    const float minSeparation = 0.70f;

    int attemptBudget = Mathf.Clamp(count * 320, 640, 2600);

    for (int attempt = 0;
         attempt < attemptBudget && results.Count < count;
         attempt++)
    {
        Vector2 circle = UnityEngine.Random.insideUnitCircle;
        if (circle.sqrMagnitude < 0.0001f)
            circle = Vector2.right;
        circle.Normalize();

        float distance = UnityEngine.Random.Range(minDistance, maxDistance);
        Vector3 horizontalPoint =
            player.position +
            new Vector3(circle.x, 0f, circle.y) * distance;

        Vector3 candidate;
        if (!TryBuildSafeDungeonWavePoint(
                horizontalPoint,
                dungeonRoot,
                playerFloor,
                out candidate))
            continue;

        bool tooClose = false;
        for (int i = 0; i < results.Count; i++)
        {
            Vector2 a = new Vector2(results[i].x, results[i].z);
            Vector2 b = new Vector2(candidate.x, candidate.z);
            if ((a - b).sqrMagnitude <
                minSeparation * minSeparation)
            {
                tooClose = true;
                break;
            }
        }

        if (!tooClose)
            results.Add(candidate);
    }

    // Deterministic close-range fallback for tiny rooms and narrow corridors.
    // These are all validated exactly like random candidates.
    if (results.Count < count)
    {
        Vector3[] dirs = new Vector3[]
        {
            player.forward,
            -player.forward,
            player.right,
            -player.right,
            (player.forward + player.right).normalized,
            (player.forward - player.right).normalized,
            (-player.forward + player.right).normalized,
            (-player.forward - player.right).normalized,
        };

        float[] distances = new float[]
        {
            1.10f, 1.60f, 2.10f, 2.70f, 3.40f
        };

        for (int di = 0;
             di < distances.Length && results.Count < count;
             di++)
        {
            for (int vi = 0;
                 vi < dirs.Length && results.Count < count;
                 vi++)
            {
                Vector3 horizontalPoint =
                    player.position + dirs[vi] * distances[di];

                Vector3 candidate;
                if (!TryBuildSafeDungeonWavePoint(
                        horizontalPoint,
                        dungeonRoot,
                        playerFloor,
                        out candidate))
                    continue;

                bool tooClose = false;
                for (int i = 0; i < results.Count; i++)
                {
                    Vector2 a = new Vector2(results[i].x, results[i].z);
                    Vector2 b = new Vector2(candidate.x, candidate.z);
                    if ((a - b).sqrMagnitude <
                        minSeparation * minSeparation)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                    results.Add(candidate);
            }
        }
    }

    // If the room is too small for N unique positions, reuse only positions
    // that already passed every floor/room/support test. Overlap is preferable
    // to inventing a point outside the dungeon shell.
    if (results.Count > 0 && results.Count < count)
    {
        int safeCount = results.Count;
        int copyIndex = 0;
        while (results.Count < count)
        {
            results.Add(results[copyIndex % safeCount]);
            copyIndex++;
        }

        Debug.LogWarning(
            $"[CreateFoeMP][DungeonSafe] Room only had {safeCount} unique safe point(s) " +
            $"for wave count={count}; reusing validated points instead of void positions.");
    }

    // Absolute last resort: the player's own confirmed floor. This can place an
    // enemy very close to the player, but cannot deliberately place it outside
    // the playable dungeon. Use only if every nearby clearance test failed.
    if (results.Count == 0)
    {
        const float approxHeight = 1.80f;
        Vector3 playerFloorSpawn =
            playerFloor + Vector3.up * (approxHeight * 0.52f + 0.05f);

        for (int i = 0; i < count; i++)
            results.Add(playerFloorSpawn);

        Debug.LogWarning(
            $"[CreateFoeMP][DungeonSafe] Using player-floor emergency fallback " +
            $"at {playerFloorSpawn} for count={count}.");
    }

    return results;
}

// Compute "host-like" safe interior/dungeon spawn points so the server can spawn enemies directly at valid locations.
List<Vector3> BuildSuggestedWavePositions(int count, float minDistance = 5f, float maxDistance = 20f)
{
    var results = new List<Vector3>(count);

    var gm = GameManager.Instance;
    var playerObj = gm?.PlayerObject;
    var pee = gm?.PlayerEnterExit;
    if (playerObj == null || pee == null)
        return results;

    Transform player = playerObj.transform;

    // Prefer spawning on the same floor as the player (avoid attic/upper floors)
    float desiredY = GameManager.Instance.PlayerObject.transform.position.y;
    float[] bands = new float[] { MaxFloorDeltaYPrimary, MaxFloorDeltaYSecondary, 99999f };

    // Constrain hits to current interior/dungeon hierarchy when possible
    Transform parent = null;
    if (pee.IsPlayerInsideBuilding && pee.Interior)
        parent = pee.Interior.transform;
    else if (pee.IsPlayerInsideDungeon && pee.Dungeon)
        parent = pee.Dungeon.transform;

    // Approximate enemy capsule (we don't know exact CC size until spawned on server)
    const float approxRadius = 0.30f;
    const float approxHeight = 1.80f;

    int attemptsPerBand = 60;

    for (int want = 0; want < count; want++)
    {
        bool found = false;
        Vector3 chosen = player.position + player.forward * Mathf.Clamp((minDistance + maxDistance) * 0.5f, 2f, 8f);

        for (int b = 0; b < bands.Length && !found; b++)
        {
            float maxDeltaY = bands[b];

            for (int attempt = 0; attempt < attemptsPerBand; attempt++)
            {
                Vector3 rnd = UnityEngine.Random.insideUnitSphere;
                rnd.y = 0;
                if (rnd.sqrMagnitude < 0.0001f) rnd = Vector3.forward;
                rnd.Normalize();

                // Keep roughly within requested distance band
                float dist = UnityEngine.Random.Range(Mathf.Max(2.0f, minDistance), Mathf.Max(minDistance + 0.01f, Mathf.Min(7.0f, maxDistance)));
                Vector3 probe = player.position + rnd * dist;
                probe.y = desiredY + ClientFloorProbeYOffset;

                RaycastHit hit;
                if (!Physics.Raycast(probe, Vector3.down, out hit, ClientFloorProbeDownDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;

                if (hit.normal.y < 0.7f)
                    continue;

                

                    // Hard clamp: never accept a surface significantly above the player\'s current floor.
                    if (hit.point.y > desiredY + 0.75f)
                        continue;
if (Mathf.Abs(hit.point.y - desiredY) > maxDeltaY)
                    continue;

                if (parent)
                {
                    Transform ht = hit.collider ? hit.collider.transform : null;
                    if (!ht)
                        continue;

                    // Some interiors have colliders on children; require the same hierarchy.
                    if (!ht.IsChildOf(parent))
                        continue;
                }

                // Lift above floor so we don't start embedded
                Vector3 pos = hit.point + Vector3.up * (0.05f + (approxHeight * 0.5f) + ClientSpawnLift);

                // Clearance check to reduce wall/prop spawns
                Vector3 capStart = pos + Vector3.up * 0.05f;
                Vector3 capEnd   = pos + Vector3.up * (approxHeight - 0.1f);
                if (Physics.CheckCapsule(capStart, capEnd, approxRadius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;

                // Avoid stacking too tightly
                bool tooClose = false;
                for (int i = 0; i < results.Count; i++)
                {
                    if ((results[i] - pos).sqrMagnitude < 1.25f * 1.25f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose)
                    continue;

                chosen = pos;
                found = true;
                break;
            }
        }

        // Hard fallback: straight down from player (still inside interior if parent filtering worked)
        if (!found)
        {
            RaycastHit hit;
            Vector3 probe = player.position;
            probe.y = desiredY + ClientFloorProbeYOffset;
            if (Physics.Raycast(probe, Vector3.down, out hit, ClientFloorProbeDownDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                chosen = hit.point + Vector3.up * 0.05f + Vector3.up * (approxHeight * 0.5f);
            }
        }

        results.Add(chosen);
    }

    return results;
}




// Client-side helper: find a valid spawn point using same rules as PlaceFoeFreely()
bool ComputeSpawnPoint(out Vector3 testPoint, float minDistance = 5f, float maxDistance = 20f)
{
    testPoint = default;

    // Select a left or right direction outside of camera FOV
    Quaternion rotation;
    float directionAngle = GameManager.Instance.MainCamera.fieldOfView;
    directionAngle += UnityEngine.Random.Range(0f, 4f);
    rotation = (UnityEngine.Random.Range(0f, 1f) > 0.5f)
        ? Quaternion.Euler(0, -directionAngle, 0)
        : Quaternion.Euler(0,  directionAngle, 0);

    Vector3 angle = (rotation * Vector3.forward).normalized;
    Vector3 spawnDirection = GameManager.Instance.PlayerObject.transform.TransformDirection(angle).normalized;
    Ray ray = new Ray(GameManager.Instance.PlayerObject.transform.position, spawnDirection);

    const float overlapSphereRadius = 0.65f;
    const float separationDistance  = 1.25f;
    const float maxFloorDistance    = 4f;

    Vector3 currentPoint;
    RaycastHit initialHit;
    if (Physics.Raycast(ray, out initialHit, maxDistance))
    {
        float cos_normal = Vector3.Dot(-spawnDirection, initialHit.normal.normalized);
        if (cos_normal < 1e-6f) return false;

        float separationForward = separationDistance / cos_normal;
        float distanceSlack = initialHit.distance - separationForward - minDistance;
        if (distanceSlack < 0f) return false;

        float extraDistance = UnityEngine.Random.Range(0f, Mathf.Min(2f, distanceSlack));
        currentPoint = initialHit.point - spawnDirection * (separationForward + extraDistance);
    }
    else
    {
        currentPoint = GameManager.Instance.PlayerObject.transform.position +
                       spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
    }

    // Must be able to find a surface below
    RaycastHit floorHit;
    ray = new Ray(currentPoint, Vector3.down);
    if (!Physics.Raycast(ray, out floorHit, maxFloorDistance))
        return false;

    Vector3 candidate = floorHit.point + Vector3.up * separationDistance;
    Collider[] colliders = Physics.OverlapSphere(candidate, overlapSphereRadius);
    if (colliders.Length > 0)
        return false;

    testPoint = candidate;
    return true;
}



        #region Private Methods

        void CreatePendingFoeSpawn(Foe foe, bool suppressQuestLinkForMP = false)
        {
            // Get foe GameObjects. For local-only MP quests, deliberately pass null as
            // the quest Foe resource so these spawn as normal enemies and cannot sync
            // quest resource state/failure-side-effects to other players.
            Foe questResourceForSpawn = suppressQuestLinkForMP ? null : foe;

            if (Mirror.NetworkServer.active)
            {
                // MP host/server: DO NOT create the real enemies at Vector3.zero.
                //
                // GameObjectHelper.CreateFoeGameObjects() immediately calls
                // NetworkServer.Spawn() on the host. The old CreateFoe path therefore
                // network-spawned the whole wave at world Y=0, and only afterwards
                // DisableFoeUntilPlaced()/PlaceFoeFreely moved them into the dungeon.
                //
                // EnemyMotor could initialize LastGroundedY=0 during that window.
                // Relocating to e.g. Y=-499 then looked like a real ~499m fall and
                // ApplyFallDamage() killed the enemy instantly.
                //
                // Keep only logical pending slots here. Spawn each real enemy only after
                // a valid placement point has been found.
                pendingFoeGameObjects = new GameObject[foe.SpawnCount];
                mpHostDeferredRealSpawn = true;
                mpHostDeferredFoeResource = questResourceForSpawn;
                mpHostDeferredFoeType = foe.FoeType;

                Debug.Log(
                    $"[CreateFoeMP][HostDeferred] Prepared {foe.SpawnCount} logical slot(s) " +
                    $"for foe='{foe.Symbol.Name}' type={foe.FoeType}; no network enemy was created at Vector3.zero.");
            }
            else
            {
                // Single-player: preserve the original DFU pending-object workflow.
                // Pure clients never enter this method because CreateFoe.Update() uses
                // CmdCreateFoesWithPositions instead.
                mpHostDeferredRealSpawn = false;
                mpHostDeferredFoeResource = null;
                mpHostDeferredFoeType = MobileTypes.None;

                pendingFoeGameObjects = GameObjectHelper.CreateFoeGameObjects(
                    Vector3.zero,
                    foe.FoeType,
                    foe.SpawnCount,
                    MobileReactions.Hostile,
                    questResourceForSpawn);

                if (pendingFoeGameObjects == null || pendingFoeGameObjects.Length != foe.SpawnCount)
                {
                    SetComplete();
                    throw new Exception(string.Format(
                        "create foe attempted to create {0}x{1} GameObjects and failed.",
                        foe.SpawnCount,
                        Symbol.Name));
                }
            }

            // Initiate deployment process.
            spawnInProgress = true;
            spawnWaveStartTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
            pendingFoesSpawned = 0;

            // SP only: pending GameObjects really exist at this point, so keep the
            // existing staging safety. MP host has only null logical slots.
            if (!mpHostDeferredRealSpawn)
            {
                for (int i = 0; i < pendingFoeGameObjects.Length; i++)
                    DisableFoeUntilPlaced(pendingFoeGameObjects[i]);
            }
        }

        void ClearMpHostDeferredSpawnState()
        {
            mpHostDeferredRealSpawn = false;
            mpHostDeferredFoeResource = null;
            mpHostDeferredFoeType = MobileTypes.None;
        }

        GameObject SpawnMpHostDeferredFoeAt(Vector3 resolvedPoint, string source)
        {
            if (!mpHostDeferredRealSpawn || !Mirror.NetworkServer.active)
                return null;

            GameObject[] spawned = GameObjectHelper.CreateFoeGameObjects(
                resolvedPoint,
                mpHostDeferredFoeType,
                1,
                MobileReactions.Hostile,
                mpHostDeferredFoeResource);

            if (spawned == null || spawned.Length == 0 || spawned[0] == null)
            {
                Debug.LogWarning(
                    $"[CreateFoeMP][HostDeferred] Failed to create real foe at resolved point {resolvedPoint}; " +
                    $"source='{source}'. Slot remains pending.");
                return null;
            }

            GameObject go = spawned[0];

            // GameObjectHelper aligns non-flying units before NetworkServer.Spawn().
            // Use that final post-alignment pose as the authoritative intended position.
            Vector3 finalSpawnPos = go.transform.position;

            PlayerEnterExit pee = GameManager.Instance != null
                ? GameManager.Instance.PlayerEnterExit
                : null;

            uint requesterNetId = 0U;
            PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
            if (localPlayer != null)
                requesterNetId = localPlayer.netId;

            EnemyWorldPosition ewp = go.GetComponent<EnemyWorldPosition>();
            if (ewp != null)
            {
                bool inDungeon = pee != null && pee.IsPlayerInsideDungeon;
                bool inBuilding = pee != null && pee.IsPlayerInsideBuilding;

                if (inDungeon)
                {
                    // GameObjectHelper's MP host wrapper normally stamps this already,
                    // but keep the CreateFoe path self-contained if that helper changes.
                    if (!ewp.isDungeonSpawn)
                        ewp.SetDungeonSpawnContext(requesterNetId, 0, 0, false);

                    SetupDemoEnemy setupEnemy = go.GetComponent<SetupDemoEnemy>();
                    if (setupEnemy != null)
                        setupEnemy.isDungeonEnemy = true;
                }
                else
                {
                    ewp.SetSpawnContext(inBuilding, requesterNetId);
                }

                ewp.intendedSpawnPos = finalSpawnPos;
                ewp.isCreateFoeWaveSpawn = true;
            }

            // It should already be scene-root in MP host mode, but preserve the existing
            // CreateFoe root rule without moving its world pose.
            if (go.transform.parent != null)
            {
                Vector3 worldPos = go.transform.position;
                Quaternion worldRot = go.transform.rotation;
                go.transform.SetParent(null, true);
                go.transform.position = worldPos;
                go.transform.rotation = worldRot;
            }

            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                go.transform.LookAt(GameManager.Instance.PlayerObject.transform.position);

            // Store the actual spawned object in its logical slot for diagnostics/cleanup
            // parity. pendingFoesSpawned is incremented by the caller after success.
            if (pendingFoeGameObjects != null &&
                pendingFoesSpawned >= 0 &&
                pendingFoesSpawned < pendingFoeGameObjects.Length)
            {
                pendingFoeGameObjects[pendingFoesSpawned] = go;
            }

            Debug.Log(
                $"[CreateFoeMP][HostDeferred] Spawned real foe only after placement " +
                $"slot={pendingFoesSpawned} source='{source}' requested={resolvedPoint} final={finalSpawnPos} " +
                $"go='{go.name}'.");

            return go;
        }

        
        // Multiplayer safety: network or authority systems may destroy foes before this action finishes placing them.
        // Skip any destroyed/null entries in pendingFoeGameObjects to avoid MissingReferenceException.
        
        // If placement keeps failing (e.g., player not moving / camera rays not finding space),
        // force-place remaining foes near player after a short timeout to avoid leaving them at (0,0,0).
        void ForcePlaceRemainingFoes(PlayerEnterExit playerEnterExit)
        {
            if (pendingFoeGameObjects == null) return;

            // Only force after timeout
            ulong now = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
            if (spawnWaveStartTime == 0) spawnWaveStartTime = now;
            if (now - spawnWaveStartTime < ForcePlaceAfterSeconds)
                return;

            // Use cached context if available (important for host when quest taker is elsewhere / not moving)
            Transform parent = forcePlaceParent;
            Vector3 basePos = (forcePlaceOrigin != Vector3.zero) ? forcePlaceOrigin : GameManager.Instance.PlayerObject.transform.position;

             // If we're force-placing near the local host/player, avoid spawning directly in front of the camera.
             Transform localPlayerT = GameManager.Instance.PlayerObject ? GameManager.Instance.PlayerObject.transform : null;
             Camera localCam = GameManager.Instance.MainCamera;
             bool localOrigin = (localPlayerT != null) && (Vector3.Distance(basePos, localPlayerT.position) < 3.0f);


            // Fallback to local player context if cache not set
            if (!parent && playerEnterExit != null)
            {
                if (playerEnterExit.IsPlayerInsideBuilding && playerEnterExit.Interior)
                    parent = playerEnterExit.Interior.transform;
                else if (playerEnterExit.IsPlayerInsideDungeon && playerEnterExit.Dungeon)
                    parent = playerEnterExit.Dungeon.transform;
            }

            // Place all remaining at safe floor near basePos
             const float minDistance = 5f;
             const float maxDistance = 20f;

            while (pendingFoesSpawned < pendingFoeGameObjects.Length)
            {
                GameObject go = pendingFoeGameObjects[pendingFoesSpawned];

                // Null is an expected unspawned logical slot in MP host deferred mode.
                if (!mpHostDeferredRealSpawn && !go)
                {
                    pendingFoesSpawned++;
                    continue;
                }

                CharacterController cc = go != null ? go.GetComponent<CharacterController>() : null;
                float radius = cc ? Mathf.Max(0.2f, cc.radius * 0.9f) : 0.3f;
                float height = cc ? Mathf.Max(1.0f, cc.height) : 1.8f;

                Vector3 candidate = basePos;
                bool found = false;
                float desiredY = basePos.y;
                float[] bands = new float[] { MaxFloorDeltaYPrimary, MaxFloorDeltaYSecondary, 99999f };

                // Prefer vanilla out-of-view placement when force-placing near the local host/player.
                if (localOrigin)
                {
                    for (int attempt = 0; attempt < 60 && !found; attempt++)
                    {
                        Vector3 tp;
                        if (!ComputeSpawnPoint(out tp, minDistance, maxDistance))
                            continue;

                        // Convert PlaceFoeFreely-style testPoint (floor + separationDistance) to a CC-safe position.
                        Vector3 floor = tp - Vector3.up * 1.25f; // separationDistance used by ComputeSpawnPoint
                        Vector3 pos = floor + Vector3.up * 0.05f + Vector3.up * (height * 0.5f);

                        // Keep on same floor (avoid attic)
                        if (Mathf.Abs(floor.y - desiredY) > MaxFloorDeltaYSecondary)
                            continue;

                        // Indoors heuristic (prevents outside shell)
                        if (playerEnterExit != null && (playerEnterExit.IsPlayerInsideBuilding || playerEnterExit.IsPlayerInsideDungeon))
                        {
                            if (!LooksIndoors(floor))
                                continue;
                        }

                        // Clearance check
                        Vector3 capStart = pos + Vector3.up * 0.05f;
                        Vector3 capEnd = pos + Vector3.up * (height - 0.1f);
                        if (Physics.CheckCapsule(capStart, capEnd, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                            continue;

                        candidate = pos;
                        found = true;
                    }
                }

                // Fallback: radial search around basePos (used for remote origins where local camera doesn't apply)
                if (!found)
                {
                    for (int b = 0; b < bands.Length && !found; b++)
                    {
                        float maxDeltaY = bands[b];
                        for (int attempt = 0; attempt < 50; attempt++)
                        {
                            Vector3 rnd = UnityEngine.Random.insideUnitSphere;
                            rnd.y = 0;
                            if (rnd.sqrMagnitude < 0.0001f) rnd = Vector3.forward;
                            rnd.Normalize();

                            float dist = UnityEngine.Random.Range(2.0f, 7.0f);
                            Vector3 probe = basePos + rnd * dist + Vector3.up * 1.5f;

                            RaycastHit hit;
                            if (Physics.Raycast(probe, Vector3.down, out hit, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                            {
                                if (hit.normal.y < 0.7f)
                                    continue;

                                if (Mathf.Abs(hit.point.y - desiredY) > maxDeltaY)
                                    continue;

                                if (parent)
                                {
                                    Transform ht = hit.collider ? hit.collider.transform : null;
                                    if (!ht) continue;
                                    if (!ht.IsChildOf(parent))
                                        continue;
                                }

                                Vector3 pos = hit.point + Vector3.up * 0.05f + Vector3.up * (height * 0.5f);

                                Vector3 capStart = pos + Vector3.up * 0.05f;
                                Vector3 capEnd = pos + Vector3.up * (height - 0.1f);
                                if (Physics.CheckCapsule(capStart, capEnd, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                                    continue;

                                candidate = pos;
                                found = true;
                                break;
                            }
                        }
                    }
                }
// Fallback: straight down from basePos
                if (!found)
                {
                    RaycastHit hit;
                    Vector3 probe = basePos + Vector3.up * 1.5f;
                    if (Physics.Raycast(probe, Vector3.down, out hit, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    {
                        Vector3 pos = hit.point + Vector3.up * 0.05f + Vector3.up * (height * 0.5f);
                        candidate = pos;
                    }
                }

                if (mpHostDeferredRealSpawn)
                {
                    // MP host: create the real network enemy at the already-resolved
                    // force-placement point. Never create it at Vector3.zero.
                    go = SpawnMpHostDeferredFoeAt(
                        candidate,
                        "ForcePlaceRemainingFoes");

                    if (go == null)
                        return;
                }
                else
                {
                    // SP original behaviour: move the already-created pending object.
                    if (cc) cc.enabled = false;
                    go.transform.position = candidate;
                    if (cc) cc.enabled = true;

                    EnableFoeAfterPlaced(go);
                    go.SetActive(true);

                    FinalizeNetworkedCreateFoePlacement(go, "ForcePlaceRemainingFoes");
                }

                pendingFoesSpawned++;
            }

            // Wave done
            spawnInProgress = false;
            spawnCounter++;
            ClearMpHostDeferredSpawnState();

            // Clear cache for next wave
            forcePlaceParent = null;
            forcePlaceOrigin = Vector3.zero;
        }


void AdvancePastDestroyed(GameObject[] gameObjects)
        {
            if (gameObjects == null) return;

            // In MP host deferred mode, null entries are intentional logical slots that
            // have not been spawned yet. Do not skip them as if enemies were destroyed.
            if (mpHostDeferredRealSpawn)
                return;

            while (pendingFoesSpawned < gameObjects.Length && gameObjects[pendingFoesSpawned] == null)
            {
                pendingFoesSpawned++;
            }
        }


        // Disable physics/AI on newly created pending foes so they don't fall or get processed before placement succeeds.
        void DisableFoeUntilPlaced(GameObject go)
        {
            if (!go) return;

            // Disable character controller if present
            var cc = go.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;

            // Disable rigidbody gravity if present
            var rb = go.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.useGravity = false;
                rb.isKinematic = true;
            }
        }

        void EnableFoeAfterPlaced(GameObject go)
        {
            if (!go) return;

            var rb = go.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }

            var cc = go.GetComponent<CharacterController>();
            if (cc) cc.enabled = true;
        }

void TryPlacement()
        {
            
            AdvancePastDestroyed(pendingFoeGameObjects);
if (!spawnInProgress || pendingFoeGameObjects == null || pendingFoeGameObjects.Length == 0)
                return;
            if (pendingFoesSpawned < 0 || pendingFoesSpawned >= pendingFoeGameObjects.Length)
                return;


            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;

            // The "send" variant is only used when player within a town/exterior location
            // The placement will remain pending until player matches conditions
            if (isSendAction)
            {
                if (!GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
                    return;
            }

            // Place in world near player depending on local area
            if (playerEnterExit.IsPlayerInsideBuilding)
            {
                PlaceFoeBuildingInterior(pendingFoeGameObjects, playerEnterExit.Interior);
            }
            else if (playerEnterExit.IsPlayerInsideDungeon)
            {
                PlaceFoeDungeonInterior(pendingFoeGameObjects, playerEnterExit.Dungeon);
            }
            else if (!playerEnterExit.IsPlayerInside && GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
            {
                PlaceFoeExteriorLocation(pendingFoeGameObjects, GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject);
            }
            else
            {
                PlaceFoeWilderness(pendingFoeGameObjects);
            }
        }

        #endregion

        
        // ─────────────────────────────────────────────────────────────────────────────
        // Multiplayer helper: compute wave positions using vanilla PlaceFoeFreely logic
        // (FOV direction, wall backoff, down-ray to floor, overlap check).
        // Used on CLIENT to choose good interior positions to send to host.
        // Allows other rooms, but rejects hits not belonging to the same DaggerfallInterior.
        // ─────────────────────────────────────────────────────────────────────────────
        
        // ─────────────────────────────────────────────────────────────────────────────
        // Multiplayer helper: compute wave positions using vanilla PlaceFoeFreely logic.
        // IMPORTANT: In multiplayer, we must avoid "outside interior" hits without relying on fragile hierarchy checks.
        // Strategy:
        //  - Use vanilla FOV/wall-backoff + down-ray for floor.
        //  - Accept candidates near player's current floor level.
        //  - Require a "ceiling" above the candidate within a reasonable distance to reject outside/void.
        //  - Do NOT hard-fail if space checks are too strict; prefer best-effort to always return at least 1 position.
        // This keeps "other rooms" valid (they still have ceilings).
        // ─────────────────────────────────────────────────────────────────────────────
        
        // ─────────────────────────────────────────────────────────────────────────────
        // Multiplayer helper: heuristic to reject "outside interior" positions even when hierarchy/markers are unreliable.
        // We consider a point "indoors" if it has a ceiling above within a reasonable distance and at least one nearby wall.
        // This still allows other rooms (they have ceilings/walls), but rejects void/outside shell areas.
        // ─────────────────────────────────────────────────────────────────────────────
        static bool LooksIndoors(Vector3 point,
            float ceilingProbeUp = 10f,
            float wallProbeDist = 12f,
            int minWallHits = 1)
        {
            // Ceiling test
            Vector3 upStart = point + Vector3.up * 0.2f;
            if (!Physics.Raycast(upStart, Vector3.up, ceilingProbeUp, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;

            // Wall proximity test
            Vector3 origin = point + Vector3.up * 1.0f;
            Vector3[] dirs = new Vector3[]
            {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
            };

            int wallHits = 0;
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Physics.Raycast(origin, dirs[i], out RaycastHit h, wallProbeDist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider != null && !h.collider.isTrigger && Mathf.Abs(h.normal.y) < 0.5f)
                    {
                        wallHits++;
                        if (wallHits >= minWallHits)
                            return true;
                    }
                }
            }

            return wallHits >= minWallHits;
        }


static List<Vector3> ComputeWavePositionsLikeVanillaPlaceFoeFreely(
            int count,
            DaggerfallInterior interiorParent,
            float minDistance = 5f,
            float maxDistance = 20f,
            float maxFloorDistance = 6f,
            float separationDistance = 1.25f,
            float floorBand = 2.5f,
            float ceilingProbeUp = 10f)
        {
            var positions = new List<Vector3>(Mathf.Max(0, count));
            if (count <= 0 || GameManager.Instance == null || GameManager.Instance.PlayerObject == null)
                return positions;

            Transform playerT = GameManager.Instance.PlayerObject.transform;
            Camera cam = GameManager.Instance.MainCamera;

            float desiredY = playerT.position.y;

            // Try hard to find points, but never return 0 if we can find ANY floor.
            int attemptsBudget = Mathf.Clamp(count * 200, 250, 2000);

            Vector3? bestAnyFloor = null;
            float bestAnyFloorScore = float.PositiveInfinity;

            for (int attempt = 0; attempt < attemptsBudget && positions.Count < count; attempt++)
            {
                // Choose direction outside camera FOV
                float directionAngle = (cam != null ? cam.fieldOfView : 60f);
                directionAngle += UnityEngine.Random.Range(0f, 4f);
                Quaternion rotation = (UnityEngine.Random.Range(0f, 1f) > 0.5f)
                    ? Quaternion.Euler(0, -directionAngle, 0)
                    : Quaternion.Euler(0,  directionAngle, 0);

                Vector3 angle = (rotation * Vector3.forward).normalized;
                Vector3 spawnDirection = playerT.TransformDirection(angle).normalized;

                Vector3 currentPoint;
                Ray ray = new Ray(playerT.position, spawnDirection);
                if (Physics.Raycast(ray, out RaycastHit initialHit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    float cosNormal = Vector3.Dot(-spawnDirection, initialHit.normal.normalized);
                    if (cosNormal < 1e-6f)
                        continue;

                    float separationForward = separationDistance / cosNormal;
                    float distanceSlack = initialHit.distance - separationForward - minDistance;
                    if (distanceSlack < 0f)
                        continue;

                    float extraDistance = UnityEngine.Random.Range(0f, Mathf.Min(2f, distanceSlack));
                    currentPoint = initialHit.point - spawnDirection * (separationForward + extraDistance);
                }
                else
                {
                    currentPoint = playerT.position + spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
                }

                // Find floor below (choose best hit in range)
                ray = new Ray(currentPoint, Vector3.down);
                var hits = Physics.RaycastAll(ray, maxFloorDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (hits == null || hits.Length == 0)
                    continue;

                RaycastHit best = default;
                bool found = false;
                float bestDist = float.PositiveInfinity;

                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (h.normal.y < 0.7f) // floor-ish
                        continue;

                    if (h.distance < bestDist)
                    {
                        best = h;
                        bestDist = h.distance;
                        found = true;
                    }
                }

                if (!found)
                    continue;

                Vector3 testPoint = best.point + Vector3.up * separationDistance;

                float score = Mathf.Abs(best.point.y - desiredY);

                if (score < bestAnyFloorScore)
                {
                    bestAnyFloorScore = score;
                    bestAnyFloor = testPoint;
                }

                if (score > floorBand)
                    continue;

                // Ceiling test: from just above testPoint, we should hit something above within ceilingProbeUp.
                Vector3 upStart = testPoint + Vector3.up * 0.2f;
                if (!Physics.Raycast(upStart, Vector3.up, ceilingProbeUp, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;

                bool tooClose = false;
                for (int i = 0; i < positions.Count; i++)
                {
                    if ((positions[i] - testPoint).sqrMagnitude < (separationDistance * separationDistance))
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                positions.Add(testPoint);
            }

            while (positions.Count > 0 && positions.Count < count)
                positions.Add(positions[positions.Count - 1]);

            if (positions.Count == 0 && bestAnyFloor.HasValue)
                positions.Add(bestAnyFloor.Value);

            if (positions.Count == 0)
                positions.Add(playerT.position + playerT.forward * 6f);

            return positions;
        }



#region Placement Methods

        void FinalizeNetworkedCreateFoePlacement(GameObject go, string source)
        {
            if (!go)
                return;

            if (!Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
                return;

            // In MP, quest-spawned CreateFoe enemies must survive exterior/block unloads and
            // must not inherit DaggerfallLocation / loose-object hierarchy behaviour.
            // Keep world pose exactly as placed, but force the network object to scene root.
            Vector3 worldPos = go.transform.position;
            Quaternion worldRot = go.transform.rotation;
            if (go.transform.parent != null)
            {
                Transform oldParent = go.transform.parent;
                go.transform.SetParent(null, true);
                go.transform.position = worldPos;
                go.transform.rotation = worldRot;
                Debug.Log($"[CreateFoeMP][Root] {source}: moved '{go.name}' from parent='{oldParent.name}' to scene root at {worldPos}");
            }

            // Stamp exterior/wilderness CreateFoe context the same way PlayerMultiplayer's
            // CmdCreateFoesWithPositions path does. isInteriorSpawn is buildings-only.
            EnemyWorldPosition ewp = go.GetComponent<EnemyWorldPosition>();
            if (ewp != null)
            {
                uint requesterNetId = 0;
                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer != null)
                    requesterNetId = localPlayer.netId;

                ewp.SetSpawnContext(false, requesterNetId);
                ewp.intendedSpawnPos = go.transform.position;
                ewp.isCreateFoeWaveSpawn = true;
            }
        }

// BUILDING interior spawns (mark isInteriorSpawn = true)
// Place foe somewhere near player when inside a building
// Building interiors have spawn nodes for this placement so we can roll out foes all at once
void PlaceFoeBuildingInterior(GameObject[] gameObjects, DaggerfallInterior interiorParent)
{
// Must have a DaggerfallLocation parent
    if (interiorParent == null)
    {
        SetComplete();
        throw new Exception("PlaceFoeFreely() must have a DaggerfallLocation parent object.");
    }

    // Mark all still-pending foes in this wave as building-interior
    for (int i = pendingFoesSpawned; i < gameObjects.Length; i++)
    {
        var go = gameObjects[i];
        if (!go) continue;
        var ewp = go.GetComponent<EnemyWorldPosition>();
        if (ewp) ewp.isInteriorSpawn = true;  // BUILDINGS ONLY
    }
            // Always place foes around player rather than use spawn points
            // Spawn points work well for "interior hunt" quests but less so for "directly attack the player"
            // Feel just placing freely will yield best results overall
    PlaceFoeFreely(gameObjects, interiorParent.transform);
}


        // Place foe somewhere near player when inside a dungeon
        // Dungeons interiors are complex 3D environments with no navgrid/navmesh or known spawn nodes
        void PlaceFoeDungeonInterior(GameObject[] gameObjects, DaggerfallDungeon dungeonParent)
        {
            PlaceFoeFreely(gameObjects, dungeonParent.transform);
        }

        // Place foe somewhere near player when outside a location navgrid is available
        // Navgrid placement helps foe avoid getting tangled in geometry like buildings
        void PlaceFoeExteriorLocation(GameObject[] gameObjects, DaggerfallLocation locationParent)
        {
            PlaceFoeFreely(gameObjects, locationParent.transform);
        }

        // Place foe somewhere near player when outside and no navgrid available
        // Wilderness environments are currently open so can be placed on ground anywhere within range
        void PlaceFoeWilderness(GameObject[] gameObjects)
        {
            if (gameObjects == null || gameObjects.Length == 0)
                return;
            if (pendingFoesSpawned < 0 || pendingFoesSpawned >= gameObjects.Length)
                return;


            // SP can still use StreamingWorld loose-object tracking.
            // MP networked quest foes must remain scene-root objects, otherwise exterior/world
            // unloading can disable/destroy their parent while the NetworkIdentity still exists.
            if (!Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
                GameManager.Instance.StreamingWorld.TrackLooseObject(gameObjects[pendingFoesSpawned], false, -1, -1, true);

            PlaceFoeFreely(gameObjects, null, 8f, 25f);
        }

        // Uses raycasts to find next spawn position just outside of player's field of view
        void PlaceFoeFreely(GameObject[] gameObjects, Transform parent, float minDistance = 5f, float maxDistance = 20f)
        {
            const float overlapSphereRadius = 0.65f;
            const float separationDistance = 1.25f;
            const float maxFloorDistance = 4f;

            // Must have received a valid array
            if (gameObjects == null || gameObjects.Length == 0)
                return;


            // Safety: pending index might already be at end if placement was retried after completing wave
            if (pendingFoesSpawned < 0 || pendingFoesSpawned >= gameObjects.Length)
                return;

            // Set parent - otherwise caller must set a parent
if (parent && !Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
    gameObjects[pendingFoesSpawned].transform.SetParent(parent, /*worldPositionStays*/ true);

            // Select a left or right direction outside of camera FOV
            Quaternion rotation;
            float directionAngle = GameManager.Instance.MainCamera.fieldOfView;
            directionAngle += UnityEngine.Random.Range(0f, 4f);
            if (UnityEngine.Random.Range(0f, 1f) > 0.5f)
                rotation = Quaternion.Euler(0, -directionAngle, 0);
            else
                rotation = Quaternion.Euler(0, directionAngle, 0);

            // Get direction vector and create a new ray
            Vector3 angle = (rotation * Vector3.forward).normalized;
            Vector3 spawnDirection = GameManager.Instance.PlayerObject.transform.TransformDirection(angle).normalized;
            Ray ray = new Ray(GameManager.Instance.PlayerObject.transform.position, spawnDirection);

            // Check for a hit
            Vector3 currentPoint;
            RaycastHit initialHit;
            if (Physics.Raycast(ray, out initialHit, maxDistance))
            {
                float cos_normal = Vector3.Dot(- spawnDirection, initialHit.normal.normalized);
                if (cos_normal < 1e-6)
                    return;
                float separationForward = separationDistance / cos_normal;

                // Must be greater than minDistance
                float distanceSlack = initialHit.distance - separationForward - minDistance;
                if (distanceSlack < 0f)
                    return;

                // Separate out from hit point
                float extraDistance = UnityEngine.Random.Range(0f, Mathf.Min(2f, distanceSlack));
                currentPoint = initialHit.point - spawnDirection * (separationForward + extraDistance);
            }
            else
            {
                // Player might be in an open area (e.g. outdoors) pick a random point along spawn direction
                currentPoint = GameManager.Instance.PlayerObject.transform.position + spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
            }

            // Must be able to find a surface below
            RaycastHit floorHit;
            ray = new Ray(currentPoint, Vector3.down);
            if (!Physics.Raycast(ray, out floorHit, maxFloorDistance))
                return;

            // Ensure this is open space
            Vector3 testPoint = floorHit.point + Vector3.up * separationDistance;
            Collider[] colliders = Physics.OverlapSphere(testPoint, overlapSphereRadius);
            if (colliders.Length > 0)
                return;

            // This looks like a good spawn position.
            if (mpHostDeferredRealSpawn)
            {
                // MP host/server: this is the FIRST moment the real network enemy exists.
                // It is born at testPoint (and ground-aligned inside GameObjectHelper)
                // instead of being born at Vector3.zero and teleported here later.
                GameObject spawned = SpawnMpHostDeferredFoeAt(
                    testPoint,
                    "PlaceFoeFreely");

                if (spawned == null)
                    return;
            }
            else
            {
                // SP original behaviour.
                pendingFoeGameObjects[pendingFoesSpawned].transform.position = testPoint;
                FinalizeFoe(pendingFoeGameObjects[pendingFoesSpawned]);
                gameObjects[pendingFoesSpawned].transform.LookAt(GameManager.Instance.PlayerObject.transform.position);
            }

            // Send msg message on first spawn only
            if (msgMessageID != -1)
            {
                ParentQuest.ShowMessagePopup(msgMessageID, oncePerQuest:true);
                msgMessageID = -1;
            }

            // Increment count
            pendingFoesSpawned++;
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

            EnableFoeAfterPlaced(go);
            go.SetActive(true);

            FinalizeNetworkedCreateFoePlacement(go, "FinalizeFoe");
        }

        #endregion

        #region Event Handlers

        private void PlayerEnterExit_OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            // Any foes pending placement to dungeon or building interior are now invalid
            pendingFoeGameObjects = null;
            spawnInProgress = false;
            ClearMpHostDeferredSpawnState();
        }

        private void StreamingWorld_OnInitWorld()
        {
            // Any foes pending placement to loose objects container are now invalid
            pendingFoeGameObjects = null;
            spawnInProgress = false;
            ClearMpHostDeferredSpawnState();
        }

        #endregion

        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public Symbol foeSymbol;
            public ulong lastSpawnTime;
            public uint spawnInterval;
            public int spawnMaxTimes;
            public int spawnChance;
            public int spawnCounter;
            public bool isSendAction;
            public int msgMessageID;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.foeSymbol = foeSymbol;
            data.lastSpawnTime = lastSpawnTime;
            data.spawnInterval = spawnInterval;
            data.spawnMaxTimes = spawnMaxTimes;
            data.spawnChance = spawnChance;
            data.spawnCounter = spawnCounter;
            data.isSendAction = isSendAction;
            data.msgMessageID = msgMessageID;

            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            foeSymbol = data.foeSymbol;
            lastSpawnTime = data.lastSpawnTime;
            spawnInterval = data.spawnInterval;
            spawnMaxTimes = data.spawnMaxTimes;
            spawnChance = data.spawnChance;
            spawnCounter = data.spawnCounter;
            isSendAction = data.isSendAction;
            msgMessageID = data.msgMessageID;

            // Set timer to current game time if not loaded from save
            if (lastSpawnTime == 0)
                lastSpawnTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
        }

        #endregion
    }
}