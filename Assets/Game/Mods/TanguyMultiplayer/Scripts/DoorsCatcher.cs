using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

// TanguyMultiplayer door/action sync revision: state, text-answer, enemy-door, and bash sync.

/// <summary>
/// Multiplayer bridge for player-driven action doors and shared dungeon action graphs.
///
/// The original implementation watched the camera ray every frame and tried to infer an
/// activation from a moving collider.  That synchronized a door's animation, but not the
/// DaggerfallAction fired by a player-driven door toggle.  It also missed short/non-moving
/// switches and identified objects using only a small overlap sphere around the hit point.
///
/// PlayerActivate now calls the two Notify methods only after DFU accepted the local
/// activation.  We send the action's deterministic LoadID plus its original world position,
/// then replay the same semantic root activation on the other peers.  Replaying the root lets
/// DFU's existing NextObject chain move linked levers, platforms, blue barriers, and special
/// action doors exactly as it does for the player who clicked them.
/// </summary>
public class DoorsCatcher : NetworkBehaviour
{
    // Retained so the existing PlayerMultiplayer prefab does not lose its serialized field.
    // The semantic synchronizer no longer performs a camera raycast or overlap query.
    public LayerMask layerMask;

    // Current mechanism state for a client which has just generated this dungeon.
    // This is a snapshot, never a replay of historical activation effects.
    [System.Serializable]
    public struct DungeonMechanismState
    {
        public byte kind; // 0 movement action, 1 door, 2 special door, 3 answered question
        public ulong loadID;
        public Vector3 anchor;
        public Vector3 position;
        public Quaternion rotation;
        public int actionFlag;
        public ActionState state;
        public float progress;
        public int lockValue;
        public short failedSkill;
    }

    sealed class PendingDungeonState
    {
        public uint request;
        public int nextBatch;
        public bool failed;
    }

    readonly Dictionary<uint, PendingDungeonState> pendingDungeonStates =
        new Dictionary<uint, PendingDungeonState>();
    uint nextDungeonStateRequest;
    const int DungeonStateBatchSize = 64;

    public static void RequestDungeonState(DaggerfallDungeon dungeon)
    {
        if (dungeon == null || !dungeon.isSet || dungeon.netId == 0 ||
            NetworkServer.active || !NetworkClient.active || NetworkClient.localPlayer == null)
            return;
        DoorsCatcher bridge = NetworkClient.localPlayer.GetComponent<DoorsCatcher>();
        if (bridge == null) return;
        uint request = ++bridge.nextDungeonStateRequest;
        if (request == 0) request = ++bridge.nextDungeonStateRequest;
        bridge.pendingDungeonStates[dungeon.netId] = new PendingDungeonState { request = request };
        bridge.CmdRequestDungeonState(dungeon.netId, request);
    }

    static DaggerfallDungeon FindSnapshotDungeon(uint id, bool server)
    {
        NetworkIdentity identity;
        var spawned = server ? NetworkServer.spawned : NetworkClient.spawned;
        return spawned.TryGetValue(id, out identity) && identity != null
            ? identity.GetComponent<DaggerfallDungeon>() : null;
    }

    static float SnapshotProgress(GameObject obj)
    {
        __ExternalAssets.iTween tween = obj.GetComponent<__ExternalAssets.iTween>();
        return tween != null ? Mathf.Clamp01(tween.Percentage) : 0f;
    }

    static DungeonMechanismState CaptureMechanism(Component component, byte kind,
        ulong id, Vector3 anchor, ActionState state, Vector3 root)
    {
        return new DungeonMechanismState
        {
            kind = kind, loadID = id, anchor = anchor - root,
            position = component.transform.position - root,
            rotation = component.transform.rotation, state = state,
            progress = SnapshotProgress(component.gameObject)
        };
    }

    [Command]
    void CmdRequestDungeonState(uint dungeonId, uint request)
    {
        DaggerfallDungeon dungeon = FindSnapshotDungeon(dungeonId, true);
        if (connectionToClient == null || !connectionToClient.isReady || dungeon == null ||
            !dungeon.isSet || !dungeon.InitialSavedActionStateReady)
            return; // Client retries after server generation/save restoration finishes.

        Vector3 root = dungeon.transform.position;
        var entries = new List<DungeonMechanismState>();
        foreach (DaggerfallAction action in dungeon.GetComponentsInChildren<DaggerfallAction>(true))
        {
            if (!action.gameObject.activeInHierarchy) continue;
            if (IsMovementAction(action.ActionFlag))
            {
                var entry = CaptureMechanism(action, 0, action.LoadID,
                    action.StartingPosition, action.CurrentState, root);
                entry.actionFlag = (int)action.ActionFlag;
                entries.Add(entry);
            }
            else if (completedTextInputActions.Contains(action))
            {
                var entry = CaptureMechanism(action, 3, action.LoadID,
                    action.StartingPosition, action.CurrentState, root);
                entry.actionFlag = (int)action.ActionFlag;
                entries.Add(entry);
            }
        }
        foreach (DaggerfallActionDoor door in dungeon.GetComponentsInChildren<DaggerfallActionDoor>(true))
        {
            if (!door.gameObject.activeInHierarchy) continue;
            var entry = CaptureMechanism(door, 1, door.LoadID,
                door.transform.position, door.CurrentState, root);
            entry.lockValue = door.CurrentLockValue;
            entry.failedSkill = door.FailedSkillLevel;
            entries.Add(entry);
        }
        foreach (DaggerfallActionDoorSpecial door in dungeon.GetComponentsInChildren<DaggerfallActionDoorSpecial>(true))
        {
            if (!door.gameObject.activeInHierarchy) continue;
            entries.Add(CaptureMechanism(door, 2, door.LoadID,
                door.transform.position, door.CurrentState, root));
        }

        // Capture and enqueue all batches in one callback, on the reliable channel.
        // Later activation RPCs follow the final batch instead of interleaving it.
        int batches = Mathf.Max(1, (entries.Count + DungeonStateBatchSize - 1) / DungeonStateBatchSize);
        for (int batch = 0; batch < batches; batch++)
        {
            int offset = batch * DungeonStateBatchSize;
            int count = Mathf.Min(DungeonStateBatchSize, entries.Count - offset);
            TargetDungeonState(connectionToClient, dungeonId, request, batch,
                batch == batches - 1, entries.GetRange(offset, count).ToArray());
        }
    }

    [TargetRpc]
    void TargetDungeonState(NetworkConnection target, uint dungeonId, uint request,
        int batch, bool last, DungeonMechanismState[] entries)
    {
        PendingDungeonState pending;
        DaggerfallDungeon dungeon = FindSnapshotDungeon(dungeonId, false);
        if (!pendingDungeonStates.TryGetValue(dungeonId, out pending) ||
            pending.request != request || dungeon == null || !dungeon.isSet)
            return;
        if (batch != pending.nextBatch++ || entries == null)
            pending.failed = true;
        if (!pending.failed)
        {
            remoteApplyDepth++;
            try
            {
                foreach (var entry in entries)
                {
                    if (ApplyDungeonMechanism(dungeon, entry)) continue;
                    pending.failed = true;
                    Debug.LogError("[DungeonLiveState] Missing mechanism kind=" + entry.kind +
                        " loadID=" + entry.loadID + " anchor=" + entry.anchor + " dungeon=" + dungeon.name);
                    break;
                }
            }
            catch (System.Exception ex)
            {
                pending.failed = true;
                Debug.LogError("[DungeonLiveState] Restore failed: " + ex);
            }
            finally { remoteApplyDepth--; }
        }
        if (!last) return;
        pendingDungeonStates.Remove(dungeonId);
        if (pending.failed)
        {
            Debug.LogError("[DungeonLiveState] Incomplete mechanism snapshot for " + dungeon.name +
                "; entry remains blocked and the state request will retry.");
            return;
        }
        dungeon.LiveActionStateReady = true;
        Debug.Log("[DungeonLiveState] Current mechanisms restored before entry: " + dungeon.name);
    }

    static bool IsWaitingForDungeonState(Component component)
    {
        DaggerfallDungeon dungeon = component.GetComponentInParent<DaggerfallDungeon>();
        return dungeon != null && !dungeon.LiveActionStateReady;
    }

    // Both peers use the same authoritative layout. Match within this root, using
    // original positions as well as IDs. Door save registration can renumber IDs.
    static T MatchMechanism<T>(DaggerfallDungeon dungeon, DungeonMechanismState entry)
        where T : Component
    {
        T fallback = null;
        int fallbackCount = 0;
        foreach (T component in dungeon.GetComponentsInChildren<T>(true))
        {
            if (!component.gameObject.activeInHierarchy) continue;
            DaggerfallAction action = component as DaggerfallAction;
            DaggerfallActionDoor door = component as DaggerfallActionDoor;
            DaggerfallActionDoorSpecial special = component as DaggerfallActionDoorSpecial;
            if (action != null && (int)action.ActionFlag != entry.actionFlag) continue;
            Vector3 anchor = action != null ? action.StartingPosition : component.transform.position;
            if ((anchor - dungeon.transform.position - entry.anchor).sqrMagnitude > 0.01f) continue;
            ulong id = action != null ? action.LoadID : (door != null ? door.LoadID : special.LoadID);
            if (id == entry.loadID) return component;
            fallback = component;
            fallbackCount++;
        }
        return fallbackCount == 1 ? fallback : null;
    }

    static bool ApplyDungeonMechanism(DaggerfallDungeon dungeon, DungeonMechanismState entry)
    {
        if (entry.kind == 0 || entry.kind == 3)
        {
            DaggerfallAction action = MatchMechanism<DaggerfallAction>(dungeon, entry);
            if (action == null) return false;
            if (entry.kind == 3)
            {
                completedTextInputActions.Add(action);
                return true;
            }
            __ExternalAssets.iTween.Stop(action.gameObject);
            action.transform.position = dungeon.transform.position + entry.position;
            action.transform.rotation = entry.rotation;
            action.CurrentState = entry.state;
            if (entry.state == ActionState.PlayingForward || entry.state == ActionState.PlayingReverse)
            {
                // Resume only the remaining motion. RestartTween scales duration,
                // but action rotation also needs its remaining angle scaled here.
                bool forward = entry.state == ActionState.PlayingForward;
                float remaining = Mathf.Clamp01(1f - entry.progress);
                float duration = Mathf.Max(0.001f, action.Duration * remaining);
                ActionState end = forward ? ActionState.End : ActionState.Start;
                __ExternalAssets.iTween.RotateBy(action.gameObject, __ExternalAssets.iTween.Hash(
                    "amount", action.ActionRotation * ((forward ? 1f : -1f) * remaining / 360f),
                    "space", action.ActionSpace, "time", duration,
                    "easetype", __ExternalAssets.iTween.EaseType.linear,
                    "oncomplete", "SetState", "oncompleteparams", end));
                __ExternalAssets.iTween.MoveTo(action.gameObject, __ExternalAssets.iTween.Hash(
                    "position", action.StartingPosition + (forward ? action.ActionTranslation : Vector3.zero),
                    "time", duration, "easetype", __ExternalAssets.iTween.EaseType.linear,
                    "oncomplete", "SetState", "oncompleteparams", end));
            }
            return true;
        }

        DaggerfallActionDoor door = entry.kind == 1 ? MatchMechanism<DaggerfallActionDoor>(dungeon, entry) : null;
        DaggerfallActionDoorSpecial special = entry.kind == 2 ? MatchMechanism<DaggerfallActionDoorSpecial>(dungeon, entry) : null;
        Component component = door != null ? (Component)door : special;
        if (component == null) return false;
        __ExternalAssets.iTween.Stop(component.gameObject);
        component.transform.rotation = entry.rotation;
        if (door != null)
        {
            door.CurrentState = entry.state;
            door.CurrentLockValue = entry.lockValue;
            door.FailedSkillLevel = entry.failedSkill;
        }
        else special.CurrentState = entry.state;
        // Use the door's existing collider handling (including mesh convex state),
        // without ToggleDoor/Receive, sounds, linked actions or player side effects.
        bool wantTrigger = entry.state != ActionState.Start;
        BoxCollider box = component.GetComponent<BoxCollider>();
        Collider doorCollider = box != null && box.enabled ? (Collider)box : component.GetComponent<MeshCollider>();
        if (doorCollider != null && doorCollider.isTrigger != wantTrigger)
            component.SendMessage("MakeTrigger", wantTrigger, SendMessageOptions.RequireReceiver);
        float remainingDoor = Mathf.Clamp01(1f - entry.progress);
        float durationDoor = Mathf.Max(0.001f, (door != null ? door.OpenDuration : special.OpenDuration) * remainingDoor);
        if (entry.state == ActionState.PlayingForward)
        {
            float angle = door != null ? door.OpenAngle : special.OpenAngle;
            __ExternalAssets.iTween.RotateBy(component.gameObject, __ExternalAssets.iTween.Hash(
                "amount", new Vector3(0f, angle * remainingDoor / 360f, 0f),
                "space", Space.Self, "time", durationDoor,
                "easetype", __ExternalAssets.iTween.EaseType.linear,
                "oncomplete", "OnCompleteOpen"));
        }
        else if (entry.state == ActionState.PlayingReverse)
        {
            Quaternion closed = door != null ? door.ClosedRotation : special.ClosedRotation;
            __ExternalAssets.iTween.RotateTo(component.gameObject, __ExternalAssets.iTween.Hash(
                "rotation", closed.eulerAngles, "time", durationDoor,
                "easetype", __ExternalAssets.iTween.EaseType.linear,
                "oncomplete", "OnCompleteClose", "oncompleteparams", 0f));
        }
        return true;
    }

    public bool logActionSync = false;

    const float PositionFallbackMaxDistance = 0.75f;
    const int MaxActionChainLength = 128;
    const int MaxStateSignatureBits = 64;

    static readonly HashSet<DaggerfallAction> completedTextInputActions = new HashSet<DaggerfallAction>();
    static int remoteApplyDepth = 0;

    /// <summary>
    /// Called after a local Direct DaggerfallAction activation was accepted.
    /// Only shared world-action graphs are replayed.  Player-specific effects such as text,
    /// teleport, damage, poison, spells, and quest/global variables remain local.
    /// </summary>
    public static void NotifyActionActivated(
        DaggerfallAction action,
        Vector3 originalPosition,
        DaggerfallAction.TriggerTypes triggerType = DaggerfallAction.TriggerTypes.Direct)
    {
        if (remoteApplyDepth > 0 || action == null || !IsSharedWorldActionGraph(action))
            return;

        DoorsCatcher bridge = GetLocalBridge();
        if (bridge == null)
            return;

        byte stateProbeCount;
        ulong targetStateBits;
        BuildActionStateSignature(action, out stateProbeCount, out targetStateBits);

        bridge.CmdReplayAction(
            action.LoadID,
            originalPosition,
            (byte)triggerType,
            stateProbeCount,
            targetStateBits);
    }

    /// <summary>
    /// Called after a local action door actually began opening or closing.
    /// A safe attached Door action is replayed with player semantics so its linked dungeon
    /// chain also runs.  Unsafe/player-specific attached actions retain visual-only door sync.
    /// </summary>
    public static void NotifyDoorActivated(
        DaggerfallActionDoor door,
        Vector3 originalPosition,
        ActionState stateBefore,
        bool activatedByPlayer)
    {
        if (remoteApplyDepth > 0 || door == null || door.CurrentState == stateBefore)
            return;

        DoorsCatcher bridge = GetLocalBridge();
        if (bridge == null)
            return;

        DaggerfallAction attachedAction = door.GetComponent<DaggerfallAction>();
        bool identifyByAction = attachedAction != null && attachedAction.LoadID != 0;
        ulong loadID = identifyByAction ? attachedAction.LoadID : door.LoadID;
        bool replayLinkedAction = activatedByPlayer &&
                                  attachedAction != null &&
                                  IsSharedWorldActionGraph(attachedAction);
        bool targetOpen = StateTargetsEnd(door.CurrentState);

        bridge.CmdReplayDoor(
            loadID,
            identifyByAction,
            originalPosition,
            targetOpen,
            door.CurrentLockValue,
            replayLinkedAction);
    }

    /// <summary>
    /// Text-input actions (Benefactor, crossbow, etc.) activate their NextObject only after
    /// the correct answer callback.  These helpers make that accepted answer one shared
    /// world activation without showing the input box on every peer.
    /// </summary>
    public static bool IsTextAnswerCompleted(DaggerfallAction questionAction)
    {
        return NetworkClient.active &&
               questionAction != null &&
               completedTextInputActions.Contains(questionAction);
    }

    public static bool TryBeginSharedTextAnswer(
        DaggerfallAction questionAction,
        DaggerfallAction nextAction)
    {
        if (!NetworkClient.active ||
            questionAction == null ||
            nextAction == null ||
            !IsSharedWorldActionGraph(nextAction))
        {
            // Preserve ordinary SP and player-specific answer behavior.
            return true;
        }

        if (completedTextInputActions.Contains(questionAction))
            return false;

        completedTextInputActions.Add(questionAction);
        return true;
    }

    public static void NotifySharedTextAnswer(
        DaggerfallAction questionAction,
        DaggerfallAction nextAction,
        Vector3 nextOriginalPosition)
    {
        if (remoteApplyDepth > 0 ||
            questionAction == null ||
            nextAction == null ||
            !IsSharedWorldActionGraph(nextAction))
        {
            return;
        }

        DoorsCatcher bridge = GetLocalBridge();
        if (bridge == null)
            return;

        bridge.CmdMarkTextAnswerCompleted(questionAction.LoadID, questionAction.transform.position);
        NotifyActionActivated(
            nextAction,
            nextOriginalPosition,
            DaggerfallAction.TriggerTypes.ActionObject);
    }

    static DoorsCatcher GetLocalBridge()
    {
        if (!NetworkClient.active || NetworkClient.localPlayer == null)
            return null;

        DoorsCatcher bridge = NetworkClient.localPlayer.GetComponent<DoorsCatcher>();
        if (bridge == null || !bridge.isLocalPlayer)
            return null;

        return bridge;
    }

    [Command]
    void CmdReplayAction(
        ulong loadID,
        Vector3 originalPosition,
        byte triggerType,
        byte stateProbeCount,
        ulong targetStateBits)
    {
        RpcReplayAction(
            loadID,
            originalPosition,
            triggerType,
            stateProbeCount,
            targetStateBits);
    }

    [ClientRpc]
    void RpcReplayAction(
        ulong loadID,
        Vector3 originalPosition,
        byte triggerType,
        byte stateProbeCount,
        ulong targetStateBits)
    {
        // Stateful actions are echoed to every peer, including the activator.  The signature
        // check below makes the activator a no-op when already correct, while giving all peers
        // the same server event order during simultaneous puzzle interactions.  Pure events
        // have no signature and must still be skipped on their activating peer.
        if (isLocalPlayer && stateProbeCount == 0)
            return;

        DaggerfallAction action = FindAction(loadID, originalPosition);
        if (action == null)
        {
            Log("Could not resolve action LoadID=" + loadID + " near " + originalPosition);
            return;
        }

        // Earlier events are covered by the pending snapshot. Later reliable
        // events arrive after its final batch and are replayed normally.
        if (IsWaitingForDungeonState(action)) return;

        // Recheck the local graph before replaying.  This protects mixed/modded clients from
        // globally applying a player-specific action merely because the sender classified it.
        if (!IsSharedWorldActionGraph(action))
        {
            Log("Refused non-world action graph LoadID=" + loadID + " flag=" + action.ActionFlag);
            return;
        }

        if (ActionStateSignatureMatches(action, stateProbeCount, targetStateBits))
            return;

        if (action.IsPlaying())
        {
            StartCoroutine(ReplayActionAfterMovement(
                action,
                triggerType,
                stateProbeCount,
                targetStateBits));
            return;
        }

        ReplayAction(action, triggerType);
    }

    IEnumerator ReplayActionAfterMovement(
        DaggerfallAction action,
        byte triggerType,
        byte stateProbeCount,
        ulong targetStateBits)
    {
        while (action != null && action.IsPlaying())
            yield return null;

        if (action == null || !IsSharedWorldActionGraph(action))
            yield break;

        if (!ActionStateSignatureMatches(action, stateProbeCount, targetStateBits))
            ReplayAction(action, triggerType);
    }

    static void ReplayAction(DaggerfallAction action, byte triggerTypeValue)
    {
        if (action == null)
            return;

        GameObject localPlayerObject = GameManager.Instance != null
            ? GameManager.Instance.PlayerObject
            : PlayerMultiplayer.playerObject;

        DaggerfallAction.TriggerTypes triggerType =
            (DaggerfallAction.TriggerTypes)triggerTypeValue;

        remoteApplyDepth++;
        try
        {
            action.Receive(localPlayerObject, triggerType);
        }
        finally
        {
            remoteApplyDepth--;
        }
    }

    [Command]
    void CmdMarkTextAnswerCompleted(ulong loadID, Vector3 originalPosition)
    {
        RpcMarkTextAnswerCompleted(loadID, originalPosition);
    }

    [ClientRpc]
    void RpcMarkTextAnswerCompleted(ulong loadID, Vector3 originalPosition)
    {
        if (isLocalPlayer)
            return;

        DaggerfallAction questionAction = FindAction(loadID, originalPosition);
        if (questionAction != null)
            completedTextInputActions.Add(questionAction);
    }

    [Command]
    void CmdReplayDoor(
        ulong loadID,
        bool identifyByAction,
        Vector3 originalPosition,
        bool targetOpen,
        int currentLockValue,
        bool replayLinkedAction)
    {
        RpcReplayDoor(
            loadID,
            identifyByAction,
            originalPosition,
            targetOpen,
            currentLockValue,
            replayLinkedAction);
    }

    [ClientRpc]
    void RpcReplayDoor(
        ulong loadID,
        bool identifyByAction,
        Vector3 originalPosition,
        bool targetOpen,
        int currentLockValue,
        bool replayLinkedAction)
    {
        DaggerfallActionDoor door = FindDoor(loadID, identifyByAction, originalPosition);
        if (door == null)
        {
            Log("Could not resolve door LoadID=" + loadID + " near " + originalPosition);
            return;
        }

        if (IsWaitingForDungeonState(door)) return;
        ApplyDoorState(door, targetOpen, currentLockValue, replayLinkedAction);
    }

    void ApplyDoorState(
        DaggerfallActionDoor door,
        bool targetOpen,
        int currentLockValue,
        bool replayLinkedAction)
    {
        if (door == null)
            return;

        door.CurrentLockValue = currentLockValue;

        if (StateTargetsEnd(door.CurrentState) == targetOpen)
            return;

        if (door.IsMoving)
        {
            StartCoroutine(ApplyDoorAfterMovement(
                door,
                targetOpen,
                currentLockValue,
                replayLinkedAction));
            return;
        }

        DaggerfallAction attachedAction = door.GetComponent<DaggerfallAction>();
        bool canReplayLinkedAction = replayLinkedAction &&
                                     attachedAction != null &&
                                     IsSharedWorldActionGraph(attachedAction);

        if (canReplayLinkedAction)
        {
            // This is the important difference from the old SetOpen-only RPC.  Passing true
            // fires the attached Door trigger and therefore its linked barrier/platform chain.
            remoteApplyDepth++;
            try
            {
                door.ToggleDoor(true);
            }
            finally
            {
                remoteApplyDepth--;
            }
        }
        else
        {
            // Keep unsafe/player-specific door actions local, but preserve visual door sync.
            remoteApplyDepth++;
            try
            {
                door.SetOpen(targetOpen, false, true);
            }
            finally
            {
                remoteApplyDepth--;
            }
        }
    }

    IEnumerator ApplyDoorAfterMovement(
        DaggerfallActionDoor door,
        bool targetOpen,
        int currentLockValue,
        bool replayLinkedAction)
    {
        while (door != null && door.IsMoving)
            yield return null;

        if (door != null)
            ApplyDoorState(door, targetOpen, currentLockValue, replayLinkedAction);
    }

    static DaggerfallAction FindAction(ulong loadID, Vector3 originalPosition)
    {
        DaggerfallAction[] actions = Object.FindObjectsOfType<DaggerfallAction>();
        DaggerfallAction nearestExact = null;
        float nearestExactDistance = float.PositiveInfinity;
        DaggerfallAction nearestPosition = null;
        float nearestPositionDistance = float.PositiveInfinity;

        for (int i = 0; i < actions.Length; i++)
        {
            DaggerfallAction candidate = actions[i];
            if (candidate == null)
                continue;

            float distance = (candidate.transform.position - originalPosition).sqrMagnitude;
            if (distance < nearestPositionDistance)
            {
                nearestPosition = candidate;
                nearestPositionDistance = distance;
            }

            if (loadID != 0 && candidate.LoadID == loadID && distance < nearestExactDistance)
            {
                nearestExact = candidate;
                nearestExactDistance = distance;
            }
        }

        if (nearestExact != null)
            return nearestExact;

        return nearestPositionDistance <= PositionFallbackMaxDistance * PositionFallbackMaxDistance
            ? nearestPosition
            : null;
    }

    static DaggerfallActionDoor FindDoor(
        ulong loadID,
        bool identifyByAction,
        Vector3 originalPosition)
    {
        DaggerfallActionDoor[] doors = Object.FindObjectsOfType<DaggerfallActionDoor>();
        DaggerfallActionDoor nearestExact = null;
        float nearestExactDistance = float.PositiveInfinity;
        DaggerfallActionDoor nearestPosition = null;
        float nearestPositionDistance = float.PositiveInfinity;

        for (int i = 0; i < doors.Length; i++)
        {
            DaggerfallActionDoor candidate = doors[i];
            if (candidate == null)
                continue;

            float distance = (candidate.transform.position - originalPosition).sqrMagnitude;
            if (distance < nearestPositionDistance)
            {
                nearestPosition = candidate;
                nearestPositionDistance = distance;
            }

            ulong candidateLoadID = candidate.LoadID;
            if (identifyByAction)
            {
                DaggerfallAction candidateAction = candidate.GetComponent<DaggerfallAction>();
                candidateLoadID = candidateAction != null ? candidateAction.LoadID : 0;
            }

            if (loadID != 0 && candidateLoadID == loadID && distance < nearestExactDistance)
            {
                nearestExact = candidate;
                nearestExactDistance = distance;
            }
        }

        if (nearestExact != null)
            return nearestExact;

        // SerializableActionDoor can increment duplicate door LoadIDs at runtime.  Exact
        // original transform is therefore the deterministic fallback, not a collider hit point.
        return nearestPositionDistance <= PositionFallbackMaxDistance * PositionFallbackMaxDistance
            ? nearestPosition
            : null;
    }

    static bool IsSharedWorldActionGraph(DaggerfallAction root)
    {
        HashSet<DaggerfallAction> visited = new HashSet<DaggerfallAction>();
        DaggerfallAction action = root;

        for (int i = 0; action != null && i < MaxActionChainLength; i++)
        {
            if (!visited.Add(action))
                return true;

            if (!IsSharedWorldAction(action.ActionFlag))
                return false;

            if (action.NextObject == null)
                return true;

            action = action.NextObject.GetComponent<DaggerfallAction>();
        }

        return action == null;
    }

    static void BuildActionStateSignature(
        DaggerfallAction root,
        out byte probeCount,
        out ulong stateBits)
    {
        probeCount = 0;
        stateBits = 0;

        HashSet<DaggerfallAction> visited = new HashSet<DaggerfallAction>();
        DaggerfallAction action = root;

        for (int i = 0;
             action != null && i < MaxActionChainLength && probeCount < MaxStateSignatureBits;
             i++)
        {
            if (!visited.Add(action))
                break;

            if (IsMovementAction(action.ActionFlag))
                AddStateProbe(ref probeCount, ref stateBits, StateTargetsEnd(action.CurrentState));

            DaggerfallActionDoor door = action.GetComponent<DaggerfallActionDoor>();
            if (door != null && probeCount < MaxStateSignatureBits)
            {
                AddStateProbe(ref probeCount, ref stateBits, StateTargetsEnd(door.CurrentState));
                if (probeCount < MaxStateSignatureBits)
                    AddStateProbe(ref probeCount, ref stateBits, door.IsLocked);
            }

            DaggerfallActionDoorSpecial specialDoor = action.GetComponent<DaggerfallActionDoorSpecial>();
            if (specialDoor != null && probeCount < MaxStateSignatureBits)
                AddStateProbe(ref probeCount, ref stateBits, StateTargetsEnd(specialDoor.CurrentState));

            action = action.NextObject != null
                ? action.NextObject.GetComponent<DaggerfallAction>()
                : null;
        }
    }

    static void AddStateProbe(ref byte probeCount, ref ulong stateBits, bool value)
    {
        if (value)
            stateBits |= 1UL << probeCount;

        probeCount++;
    }

    static bool ActionStateSignatureMatches(
        DaggerfallAction action,
        byte expectedProbeCount,
        ulong expectedStateBits)
    {
        // With no moving/door state in the graph, this is an event rather than a toggle state.
        if (expectedProbeCount == 0)
            return false;

        byte localProbeCount;
        ulong localStateBits;
        BuildActionStateSignature(action, out localProbeCount, out localStateBits);

        return localProbeCount == expectedProbeCount && localStateBits == expectedStateBits;
    }

    static bool IsSharedWorldAction(DFBlock.RdbActionFlags flag)
    {
        switch (flag)
        {
            case DFBlock.RdbActionFlags.None:
            case DFBlock.RdbActionFlags.Translation:
            case DFBlock.RdbActionFlags.Rotation:
            case DFBlock.RdbActionFlags.PositiveX:
            case DFBlock.RdbActionFlags.NegativeX:
            case DFBlock.RdbActionFlags.PositiveY:
            case DFBlock.RdbActionFlags.NegativeY:
            case DFBlock.RdbActionFlags.PositiveZ:
            case DFBlock.RdbActionFlags.NegativeZ:
            case DFBlock.RdbActionFlags.LockDoor:
            case DFBlock.RdbActionFlags.UnlockDoor:
            case DFBlock.RdbActionFlags.OpenDoor:
            case DFBlock.RdbActionFlags.CloseDoor:
            case DFBlock.RdbActionFlags.Activate:
                return true;

            default:
                return false;
        }
    }

    static bool IsMovementAction(DFBlock.RdbActionFlags flag)
    {
        switch (flag)
        {
            case DFBlock.RdbActionFlags.Translation:
            case DFBlock.RdbActionFlags.Rotation:
            case DFBlock.RdbActionFlags.PositiveX:
            case DFBlock.RdbActionFlags.NegativeX:
            case DFBlock.RdbActionFlags.PositiveY:
            case DFBlock.RdbActionFlags.NegativeY:
            case DFBlock.RdbActionFlags.PositiveZ:
            case DFBlock.RdbActionFlags.NegativeZ:
                return true;

            default:
                return false;
        }
    }

    static bool StateTargetsEnd(ActionState state)
    {
        return state == ActionState.PlayingForward || state == ActionState.End;
    }

    void Log(string message)
    {
        if (logActionSync)
            Debug.Log("[DoorActionSync] " + message);
    }
}

