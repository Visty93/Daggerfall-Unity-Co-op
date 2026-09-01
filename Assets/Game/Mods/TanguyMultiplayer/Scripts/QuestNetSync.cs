using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Text;
using Mirror;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Entity; // Races, Genders
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.Serialization;
using FullSerializer;


public class QuestNetSync : NetworkBehaviour
{
    // v40: Replicates ItemUsedDo as an exact quest event so a pure client using a
    // watched quest item advances the same target task on host/other participants.
    // Also protects the exact shared-NPC-click task from a stale routine snapshot
    // clearing/rearming it while its synchronized popup interaction is active when
    // that task contains CreateFoe/PlaceFoe. This prevents duplicate encounter waves
    // without making general quest task state monotonic.
    // v39: Repairs already-persisted M0B11Y18 state corruption at the K'avar combat
    // boundary. An impossible early _S.30_ (Queen clicked before K'avar is resolved) is
    // cleared, and the flat _traitor_ Person is forced hidden while _mtraitor_ is active.
    // This also makes PersonDTO snapshots unable to resurrect the flat NPC after a foe
    // injury/kill packet. No delay/timer heuristic is used.
    // v38: Remote toting turn-ins no longer synthesize a generic Person click when the
    // exact owning task is already known; this prevents the Queen letter hand-in from
    // pre-triggering M0B11Y18's later _S.30_ "clicked npc _ruler_" condition.
    // Foe trigger events can also carry a task-only event (messageId 0) for quests where
    // InjuredFoe/KilledFoe is followed by a separate Say/timer action, closing the
    // _hittraitor_ / _S.29_ versus _S.35_ timing race without changing inline-saying foes.
    // v37: Final quest-end Person snapshots are state-only and can no longer reactivate
    // scene NPCs while reward popups are still draining. GetItem rewards that have a
    // scripted MakePermanent in the same task are permanentized synchronously when the
    // local GetItem is reported, closing the race where quest-end cleanup could delete
    // the still-green reward before the deferred permanence repair ran.
    // v36: A suppressed parent-to-child person handoff is attached to the exact
    // spawned scene behaviour, so later durable Person snapshots cannot reactivate it.
    // Authoritative GetItem backstops retain their validated quest through parent
    // tombstoning. Prompt continuation, ownership, nested-start deduplication, exact
    // toting identity mapping, and ended-resource cleanup remain universal.
    // v24: TotingItemAndClickedNpc reward messages stay outside the generic
    // ClickedNpc popup-owner barrier. These turn-ins already have their own exact
    // replication path and must complete independently on every participant.
    // Multiplayer helpers
    public static QuestNetSync LocalInstance { get; private set; }
    private static readonly System.Collections.Generic.HashSet<ulong> _localStartedUids = new System.Collections.Generic.HashSet<ulong>();
    private static readonly System.Collections.Generic.HashSet<ulong> _remoteStartedUids = new System.Collections.Generic.HashSet<ulong>();
    private static readonly System.Collections.Generic.HashSet<string> _replicatedGetItems = new System.Collections.Generic.HashSet<string>();

    // Legacy save repair for v15-and-earlier client quest-chain suppression.
    // The old implementation called SetComplete() on future StartQuest actions,
    // and DFU serialized that state into the character save. Repair is scheduled
    // from Quest.RestoreSaveData(), so it also runs when an affected save is loaded
    // in normal single-player without creating a multiplayer player object.
    private static bool _legacyQuestChainRepairCoroutineRunning = false;
    private static readonly HashSet<string> _legacyQuestChainRepairExecuted =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _clientNestedStartRequestsSent =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _serverNestedStartRequestsHandled =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Large generated/main quests can exceed Mirror 57's single RPC/Command string
    // serialization buffer. StartPacket is therefore sent as compressed byte chunks when
    // it is a manual journal share or its serialized snapshot is large. Small packets keep
    // the original direct path.
    private const int StartPacketDirectUtf8Limit = 12 * 1024;
    private const int StartPacketChunkBytes = 8 * 1024;
    private const int StartPacketMaxTransferBytes = 16 * 1024 * 1024;
    private const int StartPacketMaxChunks = 2048;
    private const float StartPacketAssemblyTimeoutSeconds = 45f;

    private sealed class StartPacketChunkAssembly
    {
        public readonly byte[][] chunks;
        public readonly int totalBytes;
        public readonly int rawUtf8Bytes;
        public int receivedChunks;
        public int receivedBytes;
        public float lastTouchedRealtime;

        public StartPacketChunkAssembly(int chunkCount, int totalBytes, int rawUtf8Bytes)
        {
            chunks = new byte[chunkCount][];
            this.totalBytes = totalBytes;
            this.rawUtf8Bytes = rawUtf8Bytes;
            lastTouchedRealtime = Time.realtimeSinceStartup;
        }
    }

    // Commands arrive on the sender's server-side QuestNetSync object. TargetRpc chunks
    // arrive on the receiving client's local QuestNetSync object, so instance dictionaries
    // keep simultaneous players/transfers isolated without changing normal quest mappings.
    private readonly Dictionary<string, StartPacketChunkAssembly> _serverStartPacketAssemblies =
        new Dictionary<string, StartPacketChunkAssembly>(StringComparer.Ordinal);
    private readonly Dictionary<string, StartPacketChunkAssembly> _clientStartPacketAssemblies =
        new Dictionary<string, StartPacketChunkAssembly>(StringComparer.Ordinal);

    // Runtime "this quest item is currently protected in inventory" guard. This protects
    // placed quest items from stale pre-click item-state packets on both the local picker
    // and remote machines that received the pickup event. Cleared on turn-in/end/restart.
    private static readonly System.Collections.Generic.HashSet<string> _localPickedQuestItems =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    // Pickup protection must be short-lived. Keeping this forever caused a loaded
    // older save in the same MP process to replay the Rare Book pickup state/popup.
    private static readonly System.Collections.Generic.Dictionary<string, float> _localPickedQuestItemProtectUntil =
        new System.Collections.Generic.Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
    private const float QuestItemPickupProtectSeconds = 5f;

    // Exact local physical inventory object tracking. GivePc/GetItem reward fixes are
    // reliable because they permanentize the actual DaggerfallUnityItem that entered
    // PlayerEntity.Items, not only the virtual Questing.Item resource. Placed world
    // pickups need the same invariant. Track the exact local inventory object by
    // quest UID + symbol on every machine, including copies reconstructed by QNS.
    private static readonly Dictionary<string, DaggerfallUnityItem>
        _physicalQuestInventoryItemByKey =
            new Dictionary<string, DaggerfallUnityItem>(StringComparer.OrdinalIgnoreCase);

    // Permanence is a durable one-way transition. Once a local quest item symbol has
    // been made permanent, no later inventory repair/snapshot is allowed to relink a
    // physical copy back to the quest. This is runtime-only and is cleared on load or
    // when the quest UID is reused.
    private static readonly HashSet<string> _permanentQuestItemKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static void RegisterLocalPhysicalQuestInventoryItem(
        ulong questUID,
        string itemSymbol,
        DaggerfallUnityItem inventoryItem)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || inventoryItem == null)
            return;

        string key = MakeQuestItemInventoryKey(questUID, itemSymbol);
        _physicalQuestInventoryItemByKey[key] = inventoryItem;

        // A late reconstruction can occur after the authoritative permanence event.
        // Never allow that replacement object to become green again.
        if (_permanentQuestItemKeys.Contains(key))
            inventoryItem.MakePermanent();

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestItemPickupDbg] Tracked physical quest inventory object " +
                $"uid={questUID} symbol='{itemSymbol}' itemUid={inventoryItem.UID} " +
                $"questItem={inventoryItem.IsQuestItem} permanentLatched={_permanentQuestItemKeys.Contains(key)}");
        }
    }

    private static bool IsQuestItemPermanenceLatched(Quest q, string itemSymbol)
    {
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return false;

        string key = MakeQuestItemInventoryKey(q.UID, itemSymbol);
        if (_permanentQuestItemKeys.Contains(key))
            return true;

        try
        {
            Item questItem = q.GetItem(new Symbol(itemSymbol));
            return questItem != null && questItem.MadePermanent;
        }
        catch { return false; }
    }

    private static void TrackQuestInventoryObject(Quest q, string itemSymbol, DaggerfallUnityItem inventoryItem)
    {
        if (q == null || inventoryItem == null || string.IsNullOrEmpty(itemSymbol))
            return;

        RegisterLocalPhysicalQuestInventoryItem(q.UID, itemSymbol, inventoryItem);
    }

    private static void ProtectPickedQuestItemKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _localPickedQuestItems.Add(key);
        _localPickedQuestItemProtectUntil[key] = Time.realtimeSinceStartup + QuestItemPickupProtectSeconds;
    }

    private static void RemovePickedQuestItemKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _localPickedQuestItems.Remove(key);
        _localPickedQuestItemProtectUntil.Remove(key);
    }

    private static bool IsPickedQuestItemKeyProtected(string key)
    {
        if (string.IsNullOrEmpty(key) || !_localPickedQuestItems.Contains(key))
            return false;

        float until;
        if (_localPickedQuestItemProtectUntil.TryGetValue(key, out until))
        {
            if (Time.realtimeSinceStartup > until)
            {
                RemovePickedQuestItemKey(key);
                return false;
            }
        }

        return true;
    }


// Random delivery GetItem (book/ingredient/jewelry/clothing/painting/potion/weapon)
// These courier/delivery quests contain multiple possible GetItem actions but only ONE should execute.
// Track which symbol was chosen for each quest UID, and clear this when a quest starts/ends.
private static readonly System.Collections.Generic.HashSet<string> _randomDeliverySymbols =
    new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    {
        "book","ingredient","jewelry","mensclothing","womensclothing","painting","potion","weapon"
    };

private static readonly System.Collections.Generic.Dictionary<ulong, string> _randomDeliveryChosenByUid =
    new System.Collections.Generic.Dictionary<ulong, string>();


    public static void MarkQuestLocalStarted(ulong uid)
    {
        _localStartedUids.Add(uid);
        _remoteStartedUids.Remove(uid);
    }

    public static void MarkQuestRemoteStarted(ulong uid)
    {
        if (!_localStartedUids.Contains(uid))
            _remoteStartedUids.Add(uid);
    }

    public static bool IsRemoteQuest(ulong uid)
    {
        return _remoteStartedUids.Contains(uid);
    }

    private static bool FalseBool => false;


    // ─────────────────────────────────────────────────────────────────────────────
    // Local-only / do-not-share quests.
    // These quests contain player-specific cure, faction-entry, generated destination,
    // inventory, or progression state that must remain private to the player who owns
    // the quest. Match both internal quest name and display name as a safety fallback.
    // ─────────────────────────────────────────────────────────────────────────────
    private static readonly HashSet<string> _questSharingBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Dark Brotherhood entrance
        "L0A01L00",
        "The Acceptance Test",

        // Vampire cure
        "$CUREVAM",
        "Cure for Vampirism",

        // Lycanthropy cure
        "$CUREWER",
        "Cure for Lycanthropy",

        // Thieves Guild entrance
        "O0A0AL00",
        "The Qualifying Examination",
    };

    public static bool IsQuestSharingBlacklistedName(string questName)
    {
        return !string.IsNullOrEmpty(questName) && _questSharingBlacklist.Contains(questName);
    }

    public static bool IsQuestSharingBlacklisted(Quest q)
    {
        return q != null && IsQuestSharingBlacklistedName(q.QuestName);
    }

    private static bool IsQuestSharingBlacklistedUid(ulong questUID)
    {
        if (questUID == 0UL)
            return false;

        try
        {
            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
            return IsQuestSharingBlacklisted(q);
        }
        catch { return false; }
    }

    private static void LogQuestSharingBlacklisted(Quest q, string source)
    {
        if (q == null)
            return;

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][LocalOnly] Suppressed multiplayer quest sync source={source} uid={q.UID} name='{q.QuestName}'");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mirror DTOs
    // ─────────────────────────────────────────────────────────────────────────────
    [Serializable] public struct TaskStateDTO { public string symbol; public bool set; }
    [Serializable] public struct LogEntryDTO  { public int stepID; public int messageID; }
    [Serializable] public struct LogDeltaDTO
    {
        public int stepID;
        public int messageID;
        public bool present;
    }
    [Serializable] public struct ActiveQuestSummaryDTO { public ulong uid; public string questName; }
    [Serializable] public struct ItemDTO
    {
        public string symbol;
        public int stackCount;
        public bool hasPlayerClicked;
        public bool isHidden;
        public bool inPlayerInventory;
        // MakePermanent is durable quest-item state. Physical pickup rewards can be
        // converted from a green quest item into a normal permanent item immediately
        // before EndQuest, so this transition must travel with normal item state.
        public bool madePermanent;
    }



[Serializable] public struct ItemStartDTO { public string symbol; public int stackCount; public string itemDataJson; }

[Serializable] public struct ClockDTO
{
    public string symbol;
    public int startingTimeInSeconds;
    public int remainingTimeInSeconds;
    public int flag;
    public int minRange;
    public int maxRange;
    public bool enabled;
    public bool finished;
}

[Serializable] public struct FoeDTO
{
    public string symbol;
    public int foeId;                 // (int)MobileTypes
    public int spawnCount;
    public int humanoidGender;        // (int)Genders
    public bool injuredTrigger;
    public bool restrained;
    public int killCount;
    public string displayName;
    public string typeName;
}
[Serializable] public struct FoeProgressDeltaDTO
{
    public string symbol;
    public int killCountCandidate;
    public bool injuredChanged;
    public bool injuredTrigger;
    public bool restrainedChanged;
    public bool restrained;
}
[Serializable] public struct PersonDTO
    {
        public string symbol;
        public int race;
        public int gender;
        public int faceIndex;
        public int nameSeed;
        public bool isQuestor;
        public bool isIndividualNPC;
        public bool isIndividualAtHome;
        public string displayName;
        public string homePlaceSymbol;
        public string lastAssignedPlaceSymbol;
        public bool assignedToHome;
        public int factionID;
        public string factionTableKey;
        public bool discoveredThroughTalkManager;
        public bool isMuted;
        public bool isDestroyed;
        public bool isHidden;
        public bool hasPlayerClicked;

        public string saveDataJson; // full Person.SaveData_v1 (for questor linkage/dialog)
    }

    [Serializable] public struct PlaceDTO
    {
        public string symbol;
        public int scope;                 // Place.Scopes
        public string name;
        public int p1, p2, p3;

        // Site identity (all ints in DTO; cast as needed to DFU types)
        public int siteType;
        public int mapId;
        public int locationId;            // DFU side may be uint
        public int regionIndex;
        public string regionName;
        public string locationName;
        public int buildingKey;           // DFU side is int
        public string buildingName;
        public int magicNumberIndex;

        // Canonical marker/resource assignment state only. Place.SaveData also contains
        // large generated dungeon marker data that can be reserialized differently while
        // a site is loaded. Comparing that entire JSON blob caused a full quest snapshot
        // to be broadcast every quest tick. This fingerprint still detects PlaceItem,
        // PlaceFoe, and PlaceNpc marker assignments without treating layout data as traffic.
        public string markerTargetsFingerprint;
        public string saveDataJson;
    }

[Serializable] public struct StartPacket
    {
        public string instanceId;
        public string questName;
        public int    factionId;
        public ulong  uid;
        // Stable duplicate guard for manual journal sharing across save/load.
        // This is compared against local active quests when runtime instanceId mappings
        // were lost, so the same manually-shared quest is rebound instead of imported again.
        public string manualShareFingerprint;
        // Explicit journal Share button mode. The packet is a one-time catch-up for
        // players who do not have this quest; existing holders bind/ignore it without
        // applying state or replaying item/reward side effects.
        public bool   shareOnlyIfMissing;
        // Full DFU task/action snapshot for this one-time quest start/import packet.
        // TaskStateDTO alone does not include action completion state, so rebuilding a
        // quest from booleans can replay timeout/end/reward actions on its first tick.
        public string taskSaveDataJson;
        // Complete generated quest snapshot. This is used only as an import fallback
        // when a receiver cannot parse the quest template in its current world context.
        public string questSaveDataJson;
        public bool   questSuccess;

        // Original source metadata and GetItem replication. takerNetId identifies the
        // StartPacket source; it does not own progression or rewards.
        public uint takerNetId;
        public string[] grantedSymbols;
        public int[] grantedPopupIds;

        // Say actions are UI side effects and are not recreated by restoring task/action
        // save data. For a live quest start, carry the completed startup Say message IDs
        // so remote participants see the same accepted-quest popup once.
        public int[] startupSayMessageIds;

        // RevealLocation writes to PlayerGPS, which lives outside Quest save data.
        // Carry only completed reveal actions so a one-time/manual import restores
        // clickable journal locations without exposing future quest destinations.
        public string[] revealedPlaceSymbols;

        public PersonDTO[] persons;
        public PlaceDTO[]  places;
        public ItemDTO[]   items;
        public ItemStartDTO[] itemsFull;
        public ClockDTO[]  clocks;
        public FoeDTO[]    foes;
        public TaskStateDTO[] tasks;
        public LogEntryDTO[]  logs;
    }

    [Serializable] public struct UpdatePacket
    {
        public string instanceId;
        // NetId of the player whose local quest change caused this update.
        // Source clients must not apply their own echo, and the host/server must
        // never apply ClientRpc state back onto its authoritative local quest copy.
        public uint sourceNetId;
        public TaskStateDTO[] tasks;
        public LogEntryDTO[]  logs;
        public ItemDTO[]      items;
        public PlaceDTO[]     places;
        public PersonDTO[]    persons;
        public FoeDTO[]       foes;
        public bool           questSuccess;
    }

    [Serializable] public struct EndPacket
    {
        public string instanceId;
        public ulong uid;
        // NetId of the player who completed the quest/reward locally.
        // The source already received the reward from vanilla GivePc and must skip
        // remote reward replay or reputation/gold can be granted twice.
        public uint sourceNetId;
        public bool questSuccess;
        public TaskStateDTO[] tasks;
        public LogEntryDTO[]  logs;
        public ItemDTO[]      items;
        public PlaceDTO[]     places;
        public PersonDTO[]    persons;
        public FoeDTO[]       foes;

        // Final reward/UI actions can execute and complete on the finisher before
        // the normal tick-delta sees them. Send the task symbols that completed a
        // GivePc action so remote machines can replay that local reward flow once.
        public string[] replayRewardTasks;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Server state
    // ─────────────────────────────────────────────────────────────────────────────
    private static int serverHookRefs = 0;
    private static int _srvSuppressStartDepth = 0; // suppress Srv_OnQuestStarted while reconstructing from client packet

    // ─────────────────────────────────────────────────────────────────────────────
    // Quest source metadata and GetItem replication. Source identity is never a
    // progression or reward permission check.
    // ─────────────────────────────────────────────────────────────────────────────
    private static uint _localNetId = 0;
    private static readonly Dictionary<ulong, uint> _questTakerByUid = new Dictionary<ulong, uint>();
    private static readonly Dictionary<ulong, uint> _questEndSourceNetIdByUid = new Dictionary<ulong, uint>();
    private static readonly HashSet<ulong> _shownGetItemPopup = new HashSet<ulong>();
    private static ulong _questNetSyncGeneratedItemUid = 0x7100000000000000UL;

    // Quest-end echo guards. When one machine receives a remote EndPacket and locally
    // tombstones/EndQuest()s that quest, DFU fires OnQuestEnded again on that receiver.
    // Without these guards the receiver reports CmdClientEnded back to the host, and the
    // host replays GivePc even though the host already received the vanilla reward.
    private static readonly HashSet<ulong> _suppressClientQuestEndReportUids = new HashSet<ulong>();
    private static readonly HashSet<ulong> _serverRecentlyEndedQuestUids = new HashSet<ulong>();
    private static readonly HashSet<ulong> _serverQuestEndInProgressUids = new HashSet<ulong>();
    private static readonly HashSet<ulong> _clientQuestEndReportedUids = new HashSet<ulong>();
    private static readonly Dictionary<ulong, float> _clientCatchupEndSuppressUntil = new Dictionary<ulong, float>();
    private static readonly HashSet<string> _serverAcceptedGetItemGrants = new HashSet<string>();
    private static readonly HashSet<string> _appliedGetItemGrants = new HashSet<string>();

    // A courier/GetItem event can arrive while quest mappings are being reconstructed
    // after load, or just before the receiving quest StartPacket is fully imported.
    // Keep the one-time event until its exact local quest copy can be resolved instead
    // of dropping it and waiting days for this machine's own clock to expire.
    private sealed class PendingGetItemGrant
    {
        public string instanceId;
        public ulong remoteQuestUid;
        public string questName;
        public string symbol;
        public int popupTextId;
        public int grantedStackCount;
        public float queuedAtRealtime;
        public float nextDebugLogRealtime;
    }

    private static readonly Dictionary<string, PendingGetItemGrant>
        _pendingGetItemGrantPackets =
            new Dictionary<string, PendingGetItemGrant>(
                StringComparer.OrdinalIgnoreCase);

    // Exact TotingItemAndClickedNpc events are interaction side effects, not passive
    // task state. If a recipient's runtime quest-instance mapping is temporarily absent
    // (most often around resume/load or UID-collision-safe imports), never drop the
    // interaction. Keep it until the exact local quest can be resolved by instance,
    // UID, or a unique template/resource/task match.
    private sealed class PendingTotingClickPacket
    {
        public string instanceId;
        public ulong remoteQuestUid;
        public string questName;
        public string itemSymbol;
        public string personSymbol;
        public int messageId;
        public string triggerTaskSymbol;
        public uint sourceNetId;
    }

    private static readonly Dictionary<string, PendingTotingClickPacket>
        _pendingTotingClickPackets =
            new Dictionary<string, PendingTotingClickPacket>(
                StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _appliedStartupSayMessages =
        new HashSet<string>(StringComparer.Ordinal);

    // Mid-quest yes/no offers (notably S0000010 and S0000017) already exist on
    // every participant before the player answers. Replicate the selected branch
    // explicitly so PlaceItem/Log/Say run from the same answer on every quest copy.
    private static readonly HashSet<string> _serverAcceptedPromptChoices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _appliedPromptChoices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // A physical TotingItemAndClickedNpc interaction can open one prompt followed by
    // more prompt tasks through When dependencies. Only that interaction's owner may
    // answer; the selected task is then replicated to every participant.
    private sealed class SharedTotingPromptContext
    {
        public bool localIsOwner;
        public uint sourceNetId;
        public readonly HashSet<string> allowedPromptTasks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<ulong, SharedTotingPromptContext>
        _sharedTotingPromptContexts =
            new Dictionary<ulong, SharedTotingPromptContext>();

    // Say tasks reached through an explicitly replicated prompt branch execute
    // locally and must not be captured by an unrelated ClickedNpc popup barrier.
    private static readonly HashSet<string> _promptChoiceSayBypassTasks =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Pure clients pause at StartQuest without saving it complete. Record the exact
    // local parent action so arrival of the authoritative child can resume everything
    // following StartQuest once, regardless of quest/template names.
    private static readonly HashSet<string> _clientDeferredNestedStarts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _clientApprovedNestedStarts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // A selected prompt branch can end its parent before the scheduled child enters
    // QuestMachine. Capture the parent's current-scene people while that branch is
    // approved, so an imported child cannot briefly recreate the same person at the
    // same marker after the parent object has already been removed.
    private sealed class PendingScenePersonHandoff
    {
        public string identity;
        public int sceneHandle;
        public Vector3 position;
    }

    private static readonly Dictionary<string, List<PendingScenePersonHandoff>>
        _pendingScenePersonHandoffsByChild =
            new Dictionary<string, List<PendingScenePersonHandoff>>(
                StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<QuestResourceBehaviour>
        _suppressedScenePersonHandoffBehaviours =
            new HashSet<QuestResourceBehaviour>();
    private static int _promptChoiceBranchExecutionDepth = 0;


    private static readonly Dictionary<ulong, string> _srvUid2Inst = new Dictionary<ulong, string>();
    private static readonly Dictionary<string, ulong> _srvInst2Uid = new Dictionary<string, ulong>();
    private static readonly Dictionary<string, Quest.TaskState[]> _srvLastTasks = new Dictionary<string, Quest.TaskState[]>();
    private static readonly Dictionary<string, HashSet<int>>      _srvLastLogs  = new Dictionary<string, HashSet<int>>();
    private static readonly Dictionary<string, Dictionary<string, ItemState>> _srvLastItems = new Dictionary<string, Dictionary<string, ItemState>>();
    private static readonly Dictionary<string, PersonDTO[]> _srvLastPersons = new Dictionary<string, PersonDTO[]>();
    private static readonly Dictionary<string, PlaceDTO[]> _srvLastPlaces = new Dictionary<string, PlaceDTO[]>();
    private static readonly Dictionary<string, FoeDTO[]> _srvLastFoes = new Dictionary<string, FoeDTO[]>();
    private static readonly Dictionary<ulong, Quest> _srvQuestObjectByUid = new Dictionary<ulong, Quest>();

    // ─────────────────────────────────────────────────────────────────────────────
    // Client state
    // ─────────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, ulong> _cliInst2Uid = new Dictionary<string, ulong>();
    private static readonly Dictionary<ulong, string> _cliUid2Inst = new Dictionary<ulong, string>();
    private static readonly Dictionary<string, Quest.TaskState[]> _cliLastTasks = new Dictionary<string, Quest.TaskState[]>();
    private static readonly Dictionary<string, HashSet<int>>      _cliLastLogs  = new Dictionary<string, HashSet<int>>();
    private static readonly Dictionary<string, Dictionary<string, ItemState>> _cliLastItems = new Dictionary<string, Dictionary<string, ItemState>>();
    private static readonly Dictionary<string, PersonDTO[]> _cliLastPersons = new Dictionary<string, PersonDTO[]>();
    private static readonly Dictionary<string, FoeDTO[]> _cliLastFoes = new Dictionary<string, FoeDTO[]>();
    private static readonly Dictionary<ulong, Quest> _cliQuestObjectByUid = new Dictionary<ulong, Quest>();

    // Live routine updates can arrive while a loaded quest is between the server's
    // post-load mapping reset and the client's resume acknowledgement. These packets
    // are full current-state snapshots, so retaining only the newest packet per
    // instance is sufficient and avoids losing one-shot Say/branch side effects.
    private static readonly Dictionary<string, UpdatePacket> _pendingRoutineUpdatesByInstance =
        new Dictionary<string, UpdatePacket>(StringComparer.Ordinal);

    private struct ItemState
    {
        public string symbol;
        public int stackCount;
        public bool hasPlayerClicked;
        public bool isHidden;
        public bool inPlayerInventory;
        public bool madePermanent;
    }

    private static readonly HashSet<string> _applying = new HashSet<string>();        // suppress deltas while applying
    private static readonly HashSet<string> _suppressStartByName = new HashSet<string>(); // one-shot guard by name
    private static readonly HashSet<string> _startingInst = new HashSet<string>();    // in-progress instance starts
    private static readonly HashSet<string> _startedInst  = new HashSet<string>();    // fully started instances
    private static int _suppressStartDepth = 0;                                       // robust echo suppression
    private static int _suppressPersonClickReportDepth = 0;                           // suppress click echo while applying remote NPC clicks
    private static int _suppressItemClickReportDepth = 0;                             // suppress click echo while applying remote item clicks
    private static int _suppressDroppedItemReportDepth = 0;                            // suppress drop echo while applying remote DroppedItemAtPlace events
    private static int _suppressPcAtReportDepth = 0;                                  // suppress PcAt echo while applying remote location triggers
    private static int _suppressPromptChoiceReportDepth = 0;                          // suppress prompt-choice echo while applying remote branch

    // Local guards for replicated click/message/HUD face side effects. These are not quest state;
    // they only prevent duplicate popups/faces when a remote click is both replayed directly and
    // later also causes the local quest action to run.
    private static readonly HashSet<string> _remotePersonClickMessagesShown = new HashSet<string>();
    // Message popups shown by ApplyRemotePersonClick/ApplyRemoteTotingItemAndPersonClicked
    // are only allowed to suppress the next local ClickedNpc/Toting popup when that local
    // action is confirmed to be echoing a freshly-applied remote click. This prevents
    // stale static guards from save/load from swallowing a real local popup later.
    private static readonly HashSet<string> _remotePersonClickMessageConsumeAllowed = new HashSet<string>();
    private static readonly HashSet<string> _remotePersonClicksApplied = new HashSet<string>();

    // A synchronized ClickedNpc branch must still execute on every participant because
    // quest actions after a popup can remove local items, hide local NPCs, place foes,
    // and perform other world-context side effects. The interaction below coordinates
    // only popup pause/release points. Each popup is an exact, owner-announced stage of
    // one physical ClickedNpc task. This prevents a different ClickedNpc/Say branch for
    // the same Person from consuming the acknowledgement.
    private sealed class SharedPersonClickPopupStage
    {
        public int sequence;
        public bool isDirectClickedNpcPopup;
        public string taskSymbol;
        public int messageId;
        public bool released;
        public bool popupShown;
        public bool localConsumed;
        public DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMessageBox popupWindow;
    }

    private sealed class SharedPersonClickInteraction
    {
        public string interactionId;
        public ulong questUID;
        public string personSymbol;
        public string triggerTaskSymbol;
        public uint sourceNetId;
        public bool localIsOwner;
        public bool localTriggerClaimed;
        public bool allowSingleUndiscoveredSay;
        public int nextStageSequence;
        public NetworkConnection serverSourceConnection;
        public readonly List<SharedPersonClickPopupStage> stages =
            new List<SharedPersonClickPopupStage>();
    }

    private static readonly Dictionary<ulong, SharedPersonClickInteraction>
        _sharedPersonClickInteractions =
            new Dictionary<ulong, SharedPersonClickInteraction>();

    private static readonly Dictionary<string, HashSet<string>>
        _personClickTaskChainCache =
            new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _remoteItemClickMessagesShown = new HashSet<string>();
    private static readonly HashSet<string> _remoteItemClicksApplied = new HashSet<string>();

    // Some DFU quest items (notably letter-style delivery items) are real clicked
    // world pickups, but their quest scripts do not always expose a plain ClickedItem
    // action on the same item symbol. The reward-item fix intentionally blocked
    // generic quest-linked inventory changes, but that also blocked these real
    // clicked pickups. Keep a very short-lived proof that this symbol came from an
    // actual quest item click/RPC, so inventory repair can run for it without
    // re-opening reward-window item sync.
    private static readonly Dictionary<string, float> _recentQuestItemClickPickupUntil =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private const float QuestItemClickPickupAllowSeconds = 8f;


    // Remote quest inventory grants must not be applied while DFU is inside an inventory,
    // loot, popup, or other pause-style UI window. Adding/removing items during those
    // windows can be lost or overwritten when the window closes. Queue them and apply
    // from the local player Update() once the UI/game is back in normal gameplay.
    private struct PendingQuestInventoryChange
    {
        public ulong questUID;
        public string itemSymbol;
        public bool inInventory;
        public string itemDataJson;
        public float queuedAtRealtime;
        public float nextDebugLogRealtime;
    }

    private static readonly Dictionary<string, PendingQuestInventoryChange> _pendingQuestInventoryChanges =
        new Dictionary<string, PendingQuestInventoryChange>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _remoteLocationRevealsApplied = new HashSet<string>();
    private static readonly HashSet<string> _remotePcAtApplied = new HashSet<string>();
    private static readonly HashSet<string> _remoteRewardReplayApplied = new HashSet<string>();

    // Forced remote GivePc replay must not run underneath an existing Say/prompt/
    // inventory/reward window. DFU can then fall back to dropping the reward on the
    // ground, and the visible popup sequence differs between participants.
    private static readonly HashSet<string> _pendingRewardReplayKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _remoteEscortFacesApplied = new HashSet<string>();

    // If a pure-client NPC click completes a parent quest while the host is still in an
    // unrelated dungeon/region, the host can execute StartQuest but fail to construct
    // the context-sensitive child. Keep recovery one-shot per parent/child.
    private static readonly HashSet<string> _serverClientContextChainFallbackSent =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _clientContextChainFallbackScheduled =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Foe injured side-effect replay. Syncing Foe.injuredTrigger by reflection can
    // otherwise mark the trigger complete before the local InjuredFoe action has a
    // chance to show its "saying" message. Keep this one-shot per quest/foe/message.
    private static readonly HashSet<string> _remoteFoeInjuredMessagesShown = new HashSet<string>();

    // Remote foe speech can arrive while this player is paused or another modal window
    // is active. Showing it immediately can be swallowed, while marking it as shown
    // prevents every later retry. Queue the exact one-shot side effect until local UI
    // is ready.
    private struct PendingFoePopupMessage
    {
        public ulong questUID;
        public string foeSymbol;
        public int messageId;
        public string taskSymbol;
        public string reason;
        public uint sourceNetId;
        public float queuedAtRealtime;
        public float nextDebugLogRealtime;
    }

    private static readonly Dictionary<string, PendingFoePopupMessage>
        _pendingFoePopupMessages =
            new Dictionary<string, PendingFoePopupMessage>(
                StringComparer.OrdinalIgnoreCase);

    // One-shot diagnostics for volatile Foe fields that are not used by this quest's
    // script. Live dungeon enemy objects can disagree about these fields on different
    // machines; allowing those differences into the 10 Hz quest delta path can create
    // a full-snapshot feedback loop.
    private static readonly HashSet<string> _ignoredVolatileFoeStateLogs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Rate-limited traffic tracing. This does not change quest state or network cadence;
    // it only identifies the exact category and first field keeping RpcUpdate alive.
    private static readonly Dictionary<string, float> _nextQuestTrafficTraceTime =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    // PcAt is a continuously evaluated local-position sensor. Its target task is true
    // only on the machine whose local player is currently at that place. Every "when"
    // task derived from that sensor is local too. Mirroring either the raw sensor or a
    // derived action task makes an away client clear it locally, then re-start it from
    // the next server snapshot. In S7 this rearmed S.20/PlaceFoe and spawned hundreds
    // of Wereboars. Local condition chains are excluded from routine task sync.
    private static readonly Dictionary<Quest, HashSet<string>>
        _localPcAtTargetTasksByQuest =
            new Dictionary<Quest, HashSet<string>>();

    // Direct PcAt targets plus every "when ..." task depending on one, recursively.
    // These are local condition state, not globally authoritative quest progress.
    private static readonly Dictionary<Quest, HashSet<string>>
        _localConditionDependentTasksByQuest =
            new Dictionary<Quest, HashSet<string>>();

    // Quest items assigned by a GiveItem action to a Foe are not clicked world
    // resources. They enter PlayerEntity.Items through corpse loot, so client-only
    // players need a positive inventory-edge bridge instead of the ordinary ClickedItem
    // path. Cache these symbols per parsed quest; this deliberately excludes GivePc
    // rewards and therefore does not reopen reward-window item sharing.
    private static readonly Dictionary<Quest, HashSet<string>>
        _foeLootItemSymbolsByQuest =
            new Dictionary<Quest, HashSet<string>>();

    // A remote corpse-loot grant can produce the same local false->true inventory edge
    // that the source detector watches. Suppress exactly that one echo on receivers.
    private static readonly HashSet<string>
        _remoteFoeLootInventoryEchoGuards =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Actual local inventory-edge watcher. Cli_OnTick is pure-client only, so without
    // this a host corpse pickup reaches the explicit quest-item Command only by chance.
    private static readonly HashSet<ulong> _seenLocalInventoryUids =
        new HashSet<ulong>();
    private static readonly HashSet<string> _reportedLocalFoeLootKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool _localInventoryUidBaselineReady = false;

    // Once a TotingItemAndClickedNpc turn-in consumes an item, passive ItemDTO repair
    // must not add that item back merely because another participant still reports it
    // as carried. This is local runtime state and is cleared on load/quest reset.
    private static readonly HashSet<string>
        _consumedTotingQuestItemKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Re-applies quest NPC injection when a synced Person state becomes visible while
    // this client/host is already inside the relevant building/dungeon. This covers the
    // case where the network update arrived before the local interior existed.
    private static float _nextCurrentSiteQuestResourceRefreshTime = 0f;

    // Client-side retry timer for safe save/load quest re-binding. This only binds
    // quests that already exist and are active with the same UID on both machines.
    // It never starts/recreates a quest and never replays GetItem grants.
    private float _nextLoadedQuestResumeRequestTime = 0f;

    // Save/load hygiene. DFU restores Quest objects from save, but all of these
    // QuestNetSync collections are static runtime-only state and normally survive
    // repeated loads in the same MP process. If they are not cleared, stale click
    // guards/mappings can swallow the next NPC click or replay old quest state.
    private static bool _observedSaveLoadInProgress = false;
    private static float _questSyncPausedUntilRealtime = 0f;
    private static int _loadHygieneSerial = 0;
    private Coroutine _postLoadHygieneCoroutine = null;

    // A manual Share packet is a one-time import. Previously ApplyStartPacket simply
    // discarded it while load hygiene was active. Preserve the newest packet per network
    // instance until both quest load settling and authoritative post-load time sync finish.
    private static readonly Dictionary<string, StartPacket> _pendingPostLoadManualSharePackets =
        new Dictionary<string, StartPacket>(StringComparer.Ordinal);

    private static bool IsSaveLoadInProgressNow()
    {
        try
        {
            return SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
        }
        catch { return false; }
    }

    private static bool IsQuestNetSyncPausedForLoad()
    {
        return IsSaveLoadInProgressNow() || Time.realtimeSinceStartup < _questSyncPausedUntilRealtime;
    }

    private static bool IsAuthoritativeTimeReadyForQuestSharing()
    {
        // TimeCatcher treats non-host-authoritative mode as always ready. In the normal
        // host-authoritative mode this becomes true only after a post-load host packet
        // has actually been received (or immediately for the authoritative host).
        return TimeCatcher.IsPostLoadAuthoritativeTimeReady;
    }

    private static bool IsManualShareStartPacket(StartPacket pkt)
    {
        return pkt.shareOnlyIfMissing || IsManualShareInstanceId(pkt.instanceId);
    }

    private static string GetPendingManualShareKey(StartPacket pkt)
    {
        if (!string.IsNullOrEmpty(pkt.instanceId))
            return pkt.instanceId;

        return pkt.uid.ToString() + ":" + (pkt.questName ?? string.Empty);
    }

    private static void QueuePendingManualShare(StartPacket pkt, string reason)
    {
        string key = GetPendingManualShareKey(pkt);
        _pendingPostLoadManualSharePackets[key] = pkt;

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][PostLoadTimeGate] Queued manual share quest='{pkt.questName}' uid={pkt.uid} inst='{pkt.instanceId}' reason={reason} pending={_pendingPostLoadManualSharePackets.Count}");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Remote-parse window (MP): allow Place.SetupLocalSite() to temporarily defer
    // when compiling a quest from a network packet on an "away" machine.
    // This is ONLY true while StartQuest() is running for that UID.
    // ─────────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<ulong, int> _remoteParseRefs = new Dictionary<ulong, int>();

    public static bool IsInRemoteParseWindow(ulong uid)
    {
        return _remoteParseRefs.TryGetValue(uid, out int c) && c > 0;
    }

    private static void BeginRemoteParseWindow(ulong uid)
    {
        if (_remoteParseRefs.TryGetValue(uid, out int c))
            _remoteParseRefs[uid] = c + 1;
        else
            _remoteParseRefs[uid] = 1;
    }

    private static void EndRemoteParseWindow(ulong uid)
    {
        if (_remoteParseRefs.TryGetValue(uid, out int c))
        {
            c--;
            if (c <= 0) _remoteParseRefs.Remove(uid);
            else _remoteParseRefs[uid] = c;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mirror lifecycle
    // ─────────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────────
    // Cross-process quest_log.txt safety (multiple DFU instances on same Windows user)
    // ─────────────────────────────────────────────────────────────────────────────
    private static Mutex _questLogMutex;

    private static void WithQuestLogMutex(Action action)
    {
        if (action == null) return;

        bool hasHandle = false;
        try
        {
            // Named mutex is shared across processes on the same machine/user.
            if (_questLogMutex == null)
                _questLogMutex = new Mutex(false, "DaggerfallUnity_QuestLog_Mutex");

            try
            {
                hasHandle = _questLogMutex.WaitOne(10000); // up to 10s
            }
            catch (AbandonedMutexException)
            {
                hasHandle = true; // we now own it
            }

            // Even with mutex, never allow an exception to escape an RPC/Command handler.
            try
            {
                action();
            }
            catch (IOException io)
            {
                Debug.LogWarning("[QuestNetSync] QuestMachine logging IO exception suppressed: " + io.Message);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestNetSync] StartQuest exception suppressed: " + ex);
            }
        }
        finally
        {
            if (hasHandle)
            {
                try { _questLogMutex.ReleaseMutex(); }
                catch { /* ignore */ }
            }
        }
    }

public override void OnStartServer()
    {
        if (serverHookRefs++ == 0)
        {
            QuestMachine.OnQuestStarted += Srv_OnQuestStarted;
            QuestMachine.OnQuestEnded   += Srv_OnQuestEnded;
            QuestMachine.OnTick         += Srv_OnTick;
        }

        ScheduleLegacyQuestChainSaveRepair("server-start");
    }

    public override void OnStopServer()
    {
        _serverStartPacketAssemblies.Clear();

        if (--serverHookRefs == 0)
        {
            QuestMachine.OnQuestStarted -= Srv_OnQuestStarted;
            QuestMachine.OnQuestEnded   -= Srv_OnQuestEnded;
            QuestMachine.OnTick         -= Srv_OnTick;

            _srvUid2Inst.Clear();
            _srvInst2Uid.Clear();
            _srvLastTasks.Clear();
            _srvLastLogs.Clear();
            _srvLastItems.Clear();
            _srvLastPersons.Clear();
            _srvLastPlaces.Clear();
            _srvLastFoes.Clear();
            _srvQuestObjectByUid.Clear();
            _remoteParseRefs.Clear();
            _localPickedQuestItems.Clear();
            _localPickedQuestItemProtectUntil.Clear();
            _physicalQuestInventoryItemByKey.Clear();
            _permanentQuestItemKeys.Clear();
            _remoteFoeInjuredMessagesShown.Clear();
            _pendingFoePopupMessages.Clear();
            _questEndSourceNetIdByUid.Clear();
            _suppressClientQuestEndReportUids.Clear();
            _serverRecentlyEndedQuestUids.Clear();
            _serverQuestEndInProgressUids.Clear();
            _clientQuestEndReportedUids.Clear();
            _clientCatchupEndSuppressUntil.Clear();
            _serverAcceptedGetItemGrants.Clear();
            _appliedGetItemGrants.Clear();
            _pendingGetItemGrantPackets.Clear();
            _pendingTotingClickPackets.Clear();
            _pendingRoutineUpdatesByInstance.Clear();
            _appliedStartupSayMessages.Clear();
            _serverAcceptedPromptChoices.Clear();
            _appliedPromptChoices.Clear();
            _sharedTotingPromptContexts.Clear();
            _promptChoiceSayBypassTasks.Clear();
            _clientDeferredNestedStarts.Clear();
            _clientApprovedNestedStarts.Clear();
            _pendingScenePersonHandoffsByChild.Clear();
            _suppressedScenePersonHandoffBehaviours.Clear();
            _promptChoiceBranchExecutionDepth = 0;
            _pendingRewardReplayKeys.Clear();
            _serverClientContextChainFallbackSent.Clear();
            _clientContextChainFallbackScheduled.Clear();
            _serverNestedStartRequestsHandled.Clear();
            _clientNestedStartRequestsSent.Clear();
            _localPcAtTargetTasksByQuest.Clear();
            _localConditionDependentTasksByQuest.Clear();
            _foeLootItemSymbolsByQuest.Clear();
            _remoteFoeLootInventoryEchoGuards.Clear();
            _seenLocalInventoryUids.Clear();
            _reportedLocalFoeLootKeys.Clear();
            _localInventoryUidBaselineReady = false;
            _consumedTotingQuestItemKeys.Clear();
            _nextCurrentSiteQuestResourceRefreshTime = 0f;
        }
    }

    public override void OnStartLocalPlayer()
    {
        _localNetId = netId;
        LocalInstance = this;
        if (Debug.isDebugBuild) Debug.Log($"[QuestNetSync] Local player netId={_localNetId}");
        QuestMachine.OnQuestStarted += Cli_OnQuestStarted;
        QuestMachine.OnQuestEnded   += Cli_OnQuestEnded;
        QuestMachine.OnTick         += Cli_OnTick;

        ScheduleLegacyQuestChainSaveRepair("local-player-start");
        StartCoroutine(RequestCurrentNextFrame());
    }

    public override void OnStopClient()
    {
        _clientStartPacketAssemblies.Clear();

        if (isLocalPlayer)
        {
            QuestMachine.OnQuestStarted -= Cli_OnQuestStarted;
            QuestMachine.OnQuestEnded   -= Cli_OnQuestEnded;
            QuestMachine.OnTick         -= Cli_OnTick;

            _cliInst2Uid.Clear();
            _cliUid2Inst.Clear();
            _cliLastTasks.Clear();
            _cliLastLogs.Clear();
            _cliLastItems.Clear();
            _cliLastPersons.Clear();
            _cliLastFoes.Clear();
            _cliQuestObjectByUid.Clear();
            _pendingRoutineUpdatesByInstance.Clear();
            _clientQuestEndReportedUids.Clear();
            _clientCatchupEndSuppressUntil.Clear();
            _appliedGetItemGrants.Clear();
            _pendingGetItemGrantPackets.Clear();
            _pendingTotingClickPackets.Clear();
            _pendingRewardReplayKeys.Clear();
            _appliedPromptChoices.Clear();
            _sharedTotingPromptContexts.Clear();
            _promptChoiceSayBypassTasks.Clear();
            _clientDeferredNestedStarts.Clear();
            _clientApprovedNestedStarts.Clear();
            _pendingScenePersonHandoffsByChild.Clear();
            _suppressedScenePersonHandoffBehaviours.Clear();
            _promptChoiceBranchExecutionDepth = 0;
            _pendingPostLoadManualSharePackets.Clear();
            _pendingFoePopupMessages.Clear();
            _clientNestedStartRequestsSent.Clear();
            _localPcAtTargetTasksByQuest.Clear();
            _localConditionDependentTasksByQuest.Clear();
            _foeLootItemSymbolsByQuest.Clear();
            _remoteFoeLootInventoryEchoGuards.Clear();
            _seenLocalInventoryUids.Clear();
            _reportedLocalFoeLootKeys.Clear();
            _localInventoryUidBaselineReady = false;
            _consumedTotingQuestItemKeys.Clear();
            _physicalQuestInventoryItemByKey.Clear();
            _permanentQuestItemKeys.Clear();

            if (object.ReferenceEquals(LocalInstance, this))
                LocalInstance = null;

            // If an old poisoned save was being inspected as a pure client, perform
            // any now-safe triggered repair after leaving multiplayer.
            ScheduleLegacyQuestChainSaveRepair("client-stop");
        }
    }

    private void Update()
    {
        // Only the local player object should monitor SaveLoadManager in this process.
        // Server mappings are still static and will be cleared from the host's local player.
        if (!isLocalPlayer)
            return;

        bool loading = IsSaveLoadInProgressNow();

        if (loading && !_observedSaveLoadInProgress)
        {
            _observedSaveLoadInProgress = true;
            BeginQuestNetSyncLoadHygiene("load-start");
        }
        else if (!loading && _observedSaveLoadInProgress)
        {
            _observedSaveLoadInProgress = false;
            BeginQuestNetSyncLoadHygiene("load-finished");

            if (_postLoadHygieneCoroutine != null)
                StopCoroutine(_postLoadHygieneCoroutine);

            _postLoadHygieneCoroutine = StartCoroutine(CoPostLoadQuestNetSyncHygiene(++_loadHygieneSerial));
        }

        DetectNewLocalFoeLootInventoryItem();
        TryApplyPendingPostLoadManualShares();
        ProcessPendingGetItemGrantPackets();
        ProcessPendingTotingClickPackets();
        ProcessPendingFoePopupMessages();
        ProcessPendingQuestInventoryChanges();
    }

    private void TryApplyPendingPostLoadManualShares()
    {
        if (!isLocalPlayer || _pendingPostLoadManualSharePackets.Count == 0)
            return;

        if (IsQuestNetSyncPausedForLoad() || !IsAuthoritativeTimeReadyForQuestSharing())
            return;

        if (QuestMachine.Instance == null)
            return;

        StartPacket[] packets = _pendingPostLoadManualSharePackets.Values.ToArray();
        _pendingPostLoadManualSharePackets.Clear();

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][PostLoadTimeGate] Applying {packets.Length} queued manual share packet(s) after authoritative time became ready.");

        for (int i = 0; i < packets.Length; i++)
            ApplyStartPacket(packets[i]);
    }

    private void BeginQuestNetSyncLoadHygiene(string reason)
    {
        _questSyncPausedUntilRealtime = Time.realtimeSinceStartup + 1.0f;
        ClearTransientQuestNetSyncStateForLoad(reason);
        DropQuestNetworkMappingsForLoad(reason);

        if (Debug.isDebugBuild)
            Debug.Log("[QuestNetSync][LoadHygiene] Paused and cleared runtime state: " + reason);
    }

    private IEnumerator CoPostLoadQuestNetSyncHygiene(int serial)
    {
        // Let SaveLoadManager, QuestMachine, and interior/resource rebuild finish first.
        for (int i = 0; i < 12; i++)
            yield return null;

        if (serial != _loadHygieneSerial)
            yield break;

        ClearTransientQuestNetSyncStateForLoad("post-load-settle");
        DropQuestNetworkMappingsForLoad("post-load-settle");

        // Keep all outgoing/incoming quest sync quiet for a small grace window after load.
        _questSyncPausedUntilRealtime = Time.realtimeSinceStartup + 0.35f;
        while (IsQuestNetSyncPausedForLoad())
            yield return null;

        if (serial != _loadHygieneSerial)
            yield break;

        // Do not push quest state from load. Only re-bind quests already present on both
        // machines with the same UID+name, so future real interactions can sync again.
        if (isClientOnly)
        {
            _nextLoadedQuestResumeRequestTime = 0f;
            TryRequestLoadedQuestResume(true);
        }
        else if (isServer)
        {
            Srv_PruneToActive();

            // The host's final post-load hygiene pass intentionally drops and rotates
            // every loaded quest network instance. Pure clients might already have
            // resumed against the pre-load/pre-settle instance (especially when they
            // loaded first), so explicitly invalidate those runtime mappings and make
            // them request the authoritative post-load instance again.
            RpcInvalidateClientQuestMappingsAfterHostLoad();
        }

        if (Debug.isDebugBuild)
            Debug.Log("[QuestNetSync][LoadHygiene] Post-load resume window complete.");
    }

    [ClientRpc]
    private void RpcInvalidateClientQuestMappingsAfterHostLoad()
    {
        // Host mode shares the server dictionaries in this process and must not clear
        // them from its own ClientRpc echo. Only pure clients need to rebind.
        if (!isClient || isServer)
            return;

        DropClientQuestMappingsOnly();

        QuestNetSync local = LocalInstance;
        if (local != null && local.isLocalPlayer && local.isClientOnly)
        {
            local._nextLoadedQuestResumeRequestTime = 0f;
            local.TryRequestLoadedQuestResume(true);
        }

        Debug.Log(
            "[QuestNetSync][LoadResume] Host completed a save load; " +
            "cleared stale client quest mappings and requested fresh resume mappings.");
    }

    private static void ClearTransientQuestNetSyncStateForLoad(string reason)
    {
        _applying.Clear();
        _suppressStartByName.Clear();
        _startingInst.Clear();
        _startedInst.Clear();

        _pendingRoutineUpdatesByInstance.Clear();

        _remotePersonClickMessagesShown.Clear();
        _remotePersonClickMessageConsumeAllowed.Clear();
        _remotePersonClicksApplied.Clear();
        _sharedPersonClickInteractions.Clear();
        _personClickTaskChainCache.Clear();
        _remoteItemClickMessagesShown.Clear();
        _remoteItemClicksApplied.Clear();
        _recentQuestItemClickPickupUntil.Clear();
        _pendingQuestInventoryChanges.Clear();
        _remoteLocationRevealsApplied.Clear();
        _remotePcAtApplied.Clear();
        _remoteRewardReplayApplied.Clear();
        _pendingRewardReplayKeys.Clear();
        _remoteEscortFacesApplied.Clear();
        _serverClientContextChainFallbackSent.Clear();
        _clientContextChainFallbackScheduled.Clear();
        _serverNestedStartRequestsHandled.Clear();
        _clientNestedStartRequestsSent.Clear();
        // Clear one-shot repair execution guards only at the beginning of a new
        // save load. Later post-load hygiene passes must not reopen a child start
        // that was just repaired but has not entered QuestMachine yet.
        if (string.Equals(reason, "load-start", StringComparison.Ordinal))
            _legacyQuestChainRepairExecuted.Clear();
        _remoteFoeInjuredMessagesShown.Clear();
        _pendingFoePopupMessages.Clear();

        _localPickedQuestItems.Clear();
        _localPickedQuestItemProtectUntil.Clear();
        _physicalQuestInventoryItemByKey.Clear();
        _permanentQuestItemKeys.Clear();
        _replicatedGetItems.Clear();
        _serverAcceptedGetItemGrants.Clear();
        _appliedGetItemGrants.Clear();
        _appliedStartupSayMessages.Clear();
        _serverAcceptedPromptChoices.Clear();
        _appliedPromptChoices.Clear();
        _sharedTotingPromptContexts.Clear();
        _promptChoiceSayBypassTasks.Clear();
        _clientDeferredNestedStarts.Clear();
        _clientApprovedNestedStarts.Clear();
        _pendingScenePersonHandoffsByChild.Clear();
        _suppressedScenePersonHandoffBehaviours.Clear();
        _promptChoiceBranchExecutionDepth = 0;
        _randomDeliveryChosenByUid.Clear();
        _shownGetItemPopup.Clear();
        _questTakerByUid.Clear();
        _questEndSourceNetIdByUid.Clear();
        _suppressClientQuestEndReportUids.Clear();
        _serverRecentlyEndedQuestUids.Clear();
        _serverQuestEndInProgressUids.Clear();
        _clientQuestEndReportedUids.Clear();
        _clientCatchupEndSuppressUntil.Clear();
        _localStartedUids.Clear();
        _remoteStartedUids.Clear();
        _remoteParseRefs.Clear();
        _localPcAtTargetTasksByQuest.Clear();
        _localConditionDependentTasksByQuest.Clear();
        _foeLootItemSymbolsByQuest.Clear();
        _remoteFoeLootInventoryEchoGuards.Clear();
        _seenLocalInventoryUids.Clear();
        _reportedLocalFoeLootKeys.Clear();
        _localInventoryUidBaselineReady = false;
        _consumedTotingQuestItemKeys.Clear();

        _nextCurrentSiteQuestResourceRefreshTime = 0f;
    }

    private static void DropQuestNetworkMappingsForLoad(string reason)
    {
        _srvUid2Inst.Clear();
        _srvInst2Uid.Clear();
        _srvLastTasks.Clear();
        _srvLastLogs.Clear();
        _srvLastItems.Clear();
        _srvLastPersons.Clear();
        _srvLastPlaces.Clear();
        _srvLastFoes.Clear();
        _srvQuestObjectByUid.Clear();

        DropClientQuestMappingsOnly();
    }

    private static void DropClientQuestMappingsOnly()
    {
        _cliInst2Uid.Clear();
        _cliUid2Inst.Clear();
        _cliLastTasks.Clear();
        _cliLastLogs.Clear();
        _cliLastItems.Clear();
        _cliLastPersons.Clear();
        _cliLastFoes.Clear();
        _cliQuestObjectByUid.Clear();
        _pendingRoutineUpdatesByInstance.Clear();
    }

    private IEnumerator RequestCurrentNextFrame()
    {
        yield return null;
        if (isLocalPlayer && !IsQuestNetSyncPausedForLoad())
        {
            // Never import quests merely because this player connected. A different
            // save may not contain the quest, and reconstructing a late-stage quest can
            // evaluate an end/failure branch before the snapshot is fully restored.
            // Missing players receive a quest only through the explicit Share button.
            TryRequestLoadedQuestResume(true);
        }
    }

    private void TryRequestLoadedQuestResume(bool force = false)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (!isLocalPlayer || !isClientOnly)
            return;

        if (!force && Time.realtimeSinceStartup < _nextLoadedQuestResumeRequestTime)
            return;

        _nextLoadedQuestResumeRequestTime = Time.realtimeSinceStartup + 3f;

        ActiveQuestSummaryDTO[] summaries = BuildActiveQuestSummariesForResume();
        if (summaries != null && summaries.Length > 0)
            CmdRequestResumeLoadedQuestMappings(summaries);
    }

    private ActiveQuestSummaryDTO[] BuildActiveQuestSummariesForResume()
    {
        if (QuestMachine.Instance == null)
            return new ActiveQuestSummaryDTO[0];

        ulong[] active = QuestMachine.Instance.GetAllActiveQuests() ?? new ulong[0];
        List<ActiveQuestSummaryDTO> result = new List<ActiveQuestSummaryDTO>();

        for (int i = 0; i < active.Length; i++)
        {
            ulong uid = active[i];

            // Already mapped quests are normal live MP quests. This resume path is only
            // for loaded active quests whose static mapping was intentionally dropped.
            if (_cliUid2Inst.ContainsKey(uid))
                continue;

            Quest q = QuestMachine.Instance.GetQuest(uid);
            if (q == null || q.QuestComplete || q.QuestTombstoned)
                continue;
            if (IsQuestSharingBlacklisted(q))
                continue;

            result.Add(new ActiveQuestSummaryDTO
            {
                uid = uid,
                questName = q.QuestName ?? string.Empty,
            });
        }

        return result.ToArray();
    }

    [Command]
    private void CmdRequestResumeLoadedQuestMappings(ActiveQuestSummaryDTO[] clientQuests)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (!isServer || clientQuests == null || QuestMachine.Instance == null)
            return;

        for (int i = 0; i < clientQuests.Length; i++)
        {
            ActiveQuestSummaryDTO summary = clientQuests[i];
            if (summary.uid == 0UL || string.IsNullOrEmpty(summary.questName))
                continue;
            if (IsQuestSharingBlacklistedName(summary.questName))
                continue;

            Quest serverQuest = QuestMachine.Instance.GetQuest(summary.uid);
            if (serverQuest == null || serverQuest.QuestComplete || serverQuest.QuestTombstoned)
                continue;
            if (IsQuestSharingBlacklisted(serverQuest))
                continue;

            if (!string.Equals(serverQuest.QuestName, summary.questName, StringComparison.Ordinal))
                continue;

            string inst;
            if (!_srvUid2Inst.TryGetValue(summary.uid, out inst))
            {
                // Safe resume: both machines already have the same active quest UID.
                // Do not send a StartPacket and do not replay GetItem. Just restore the
                // network mapping so future deltas/click side-effects are shared again.
                inst = "resume_" + summary.uid.ToString("X") + "_" + Guid.NewGuid().ToString("N");

                _srvUid2Inst[summary.uid] = inst;
                _srvInst2Uid[inst] = summary.uid;
                _srvQuestObjectByUid[summary.uid] = serverQuest;

                _srvLastTasks[inst] = serverQuest.GetTaskStates();
                _srvLastLogs[inst] = new HashSet<int>((serverQuest.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
                _srvLastItems[inst] = CaptureItemStates(serverQuest);
                _srvLastPersons[inst] = BuildPersons(serverQuest);
                _srvLastPlaces[inst] = BuildPlaces(serverQuest);
                _srvLastFoes[inst] = BuildFoes(serverQuest);

                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync] Resumed loaded quest mapping on server uid={summary.uid} name={serverQuest.QuestName} inst={inst}");
            }

            TargetResumeLoadedQuestMapping(connectionToClient, inst, summary.uid, serverQuest.QuestName);
        }
    }

    [TargetRpc]
    private void TargetResumeLoadedQuestMapping(NetworkConnection target, string instanceId, ulong uid, string questName)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (!isClient || string.IsNullOrEmpty(instanceId) || uid == 0UL || QuestMachine.Instance == null)
            return;
        if (IsQuestSharingBlacklistedName(questName))
            return;

        string existingInstance;
        if (_cliUid2Inst.TryGetValue(uid, out existingInstance))
        {
            if (string.Equals(existingInstance, instanceId, StringComparison.Ordinal))
            {
                // Repair a missing reverse entry if needed, then drain any live update
                // that arrived before this acknowledgement.
                _cliInst2Uid[instanceId] = uid;
                TryApplyPendingRoutineUpdate(instanceId);
                return;
            }

            // The server rotates resume instance IDs after its own save-load hygiene.
            // A client that loaded first can still hold the old instance and would
            // otherwise reject every later RpcUpdate for this quest forever.
            CleanupClientMapping(existingInstance, uid);
            _startedInst.Remove(existingInstance);
            _startingInst.Remove(existingInstance);
            _applying.Remove(existingInstance);
            _pendingRoutineUpdatesByInstance.Remove(existingInstance);

            Debug.Log(
                $"[QuestNetSync][LoadResume] Replacing stale quest mapping uid={uid} " +
                $"oldInst='{existingInstance}' newInst='{instanceId}' quest='{questName}'");
        }

        Quest localQuest = QuestMachine.Instance.GetQuest(uid);
        if (localQuest == null || localQuest.QuestComplete || localQuest.QuestTombstoned)
            return;
        if (IsQuestSharingBlacklisted(localQuest))
            return;

        if (!string.Equals(localQuest.QuestName, questName, StringComparison.Ordinal))
            return;

        _cliInst2Uid[instanceId] = uid;
        _cliUid2Inst[uid] = instanceId;
        _cliQuestObjectByUid[uid] = localQuest;
        _startedInst.Add(instanceId);

        _cliLastTasks[instanceId] = localQuest.GetTaskStates();
        _cliLastLogs[instanceId] = new HashSet<int>((localQuest.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _cliLastItems[instanceId] = CaptureItemStates(localQuest);
        _cliLastPersons[instanceId] = BuildPersons(localQuest);
        _cliLastFoes[instanceId] = BuildFoes(localQuest);

        ApplyClientQuestChainAuthority(localQuest, "loaded-quest-resume");

        // If a live quest change (for example CastSpellDo -> PickOneOf -> Say)
        // arrived before this resume mapping, apply the newest full snapshot now
        // instead of permanently losing its one-shot action side effects.
        TryApplyPendingRoutineUpdate(instanceId);

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] Resumed loaded quest mapping on client uid={uid} name={questName} inst={instanceId}");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SERVER: DFU → network
    // ─────────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────────
    // State hygiene: handle save-load / UID reuse by pruning stale UID ↔ instance mappings
    // ─────────────────────────────────────────────────────────────────────────────
    private static void Srv_RemoveMappingByUid(ulong uid)
    {
        string inst;
        if (_srvUid2Inst.TryGetValue(uid, out inst))
        {
            _srvUid2Inst.Remove(uid);
            if (!string.IsNullOrEmpty(inst))
            {
                _srvInst2Uid.Remove(inst);
                _srvLastTasks.Remove(inst);
                _srvLastLogs.Remove(inst);
                _srvLastItems.Remove(inst);
                _srvLastPersons.Remove(inst);
                _srvLastPlaces.Remove(inst);
                _srvLastFoes.Remove(inst);
                _srvQuestObjectByUid.Remove(uid);
            }
        }
    }

    private void Srv_PruneToActive()
    {
        if (!isServer) return;

        ulong[] active = QuestMachine.Instance.GetAllActiveQuests() ?? new ulong[0];
        HashSet<ulong> activeSet = new HashSet<ulong>(active);

        // Remove mappings for quests that are no longer active (common after loading an earlier save).
        // Copy keys first to avoid modifying collection while iterating.
        List<ulong> stale = new List<ulong>(_srvUid2Inst.Keys);
        for (int k = 0; k < stale.Count; k++)
        {
            ulong uid = stale[k];
            if (!activeSet.Contains(uid))
            {
                Srv_RemoveMappingByUid(uid);
                ResetRandomDeliveryForQuest(uid);
                continue;
            }

            Quest current = QuestMachine.Instance.GetQuest(uid);
            Quest oldQuest;
            if (current != null && _srvQuestObjectByUid.TryGetValue(uid, out oldQuest) && oldQuest != null && !object.ReferenceEquals(oldQuest, current))
            {
                // Save/load can replace the Quest object while reusing the same UID in
                // this running MP process. Drop old clicked/inventory guards or the old
                // Rare Book pickup can replay on the freshly loaded quest.
                Srv_RemoveMappingByUid(uid);
                ResetRandomDeliveryForQuest(uid);
            }
        }
    }

private void Srv_OnQuestStarted(Quest q)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (q == null) return;

        if (IsQuestSharingBlacklisted(q))
        {
            LogQuestSharingBlacklisted(q, "Srv_OnQuestStarted");
            return;
        }

        // Clear random-delivery selection/replication guards for reused quest UIDs.
        ResetRandomDeliveryForQuest(q.UID);
        _serverRecentlyEndedQuestUids.Remove(q.UID);
        _serverQuestEndInProgressUids.Remove(q.UID);
        _clientQuestEndReportedUids.Remove(q.UID);
        _suppressClientQuestEndReportUids.Remove(q.UID);

if (!isServer) return;
        MarkQuestLocalStarted(q.UID);
        AcknowledgeNetworkQuestStartInLocalParents(q, "server-quest-start");
        if (_srvSuppressStartDepth > 0) return; // reconstructing from client packet
        Srv_PruneToActive();
        if (_srvUid2Inst.ContainsKey(q.UID))
        {
            // UID can be reused after loading an earlier save; drop stale mapping and continue.
            Srv_RemoveMappingByUid(q.UID);
        }

        string inst = Guid.NewGuid().ToString("N");
        _srvUid2Inst[q.UID] = inst;
        _srvInst2Uid[inst]  = q.UID;
        _srvQuestObjectByUid[q.UID] = q;

        _srvLastTasks[inst] = q.GetTaskStates();
        _srvLastLogs[inst]  = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _srvLastItems[inst] = CaptureItemStates(q);
        _srvLastPersons[inst] = BuildPersons(q);
        _srvLastPlaces[inst] = BuildPlaces(q);
        _srvLastFoes[inst] = BuildFoes(q);

        StartCoroutine(Srv_SendStartPacketAfterDelay(q, inst));
    }


    private IEnumerator Srv_SendStartPacketAfterDelay(Quest q, string inst)
    {
        // Let the startup task execute far enough to display any quest-offer Prompt.
        yield return null;
        yield return null;

        if (q == null || IsQuestSharingBlacklisted(q))
            yield break;

        // Prompt.Update() marks itself complete as soon as the yes/no window opens, but
        // its yes/no task is not started until the player answers. Capturing after a fixed
        // two frames therefore sends an empty pre-accept quest shell. Wait for the host's
        // active startup prompt to receive a choice before broadcasting this live start.
        bool loggedWait = false;
        while (q != null && !q.QuestComplete && HasUnresolvedTriggeredQuestPrompt(q))
        {
            if (!loggedWait)
            {
                loggedWait = true;
                Debug.Log(
                    $"[QuestNetSync][QuestStartOffer] Waiting for host quest-offer response " +
                    $"uid={q.UID} quest='{q.QuestName}'");
            }

            yield return null;
        }

        if (q == null || q.QuestComplete || IsQuestSharingBlacklisted(q))
            yield break;

        if (loggedWait)
        {
            // The prompt callback only sets the selected task. Allow the next quest tick
            // to execute its Log/Say/AddQuestor/Place actions before taking the snapshot.
            yield return new WaitForSecondsRealtime(0.25f);

            if (q == null || q.QuestComplete || IsQuestSharingBlacklisted(q))
                yield break;
        }

        // Record taker for this quest uid (host's local player).
        _questTakerByUid[q.UID] = _localNetId;

        string[] granted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
        int[] popups = CaptureGetItemPopupIdsForSymbols(q, granted);

        StartPacket pkt = BuildStartPacket(q, inst, _localNetId, granted, popups);
        Debug.Log(
            $"[QuestNetSync][QuestStartOffer] Host broadcasting quest start " +
            $"uid={pkt.uid} quest='{pkt.questName}' logs=" +
            $"{(pkt.logs != null ? pkt.logs.Length : 0)} startupSay=" +
            $"{(pkt.startupSayMessageIds != null ? string.Join(",", pkt.startupSayMessageIds) : "<none>")}");

        ServerBroadcastStartPacket(pkt);
    }


    private IEnumerator Cli_SendStartPacketAfterDelay(Quest q, string inst)
    {
        // Let startup actions display an ordinary Prompt or PromptMulti first.
        yield return null;
        yield return null;

        if (q == null || IsQuestSharingBlacklisted(q))
            yield break;

        bool loggedWait = false;
        while (q != null &&
               !q.QuestComplete &&
               HasUnresolvedTriggeredQuestPrompt(q))
        {
            if (!loggedWait)
            {
                loggedWait = true;
                Debug.Log(
                    $"[QuestNetSync][QuestStartOffer] Waiting for client quest-offer " +
                    $"response uid={q.UID} quest='{q.QuestName}'");
            }

            yield return null;
        }

        if (q == null || q.QuestComplete || IsQuestSharingBlacklisted(q))
            yield break;

        if (loggedWait)
        {
            // ReportLocalPromptChoice executes the selected task immediately. Keep a
            // small settling window for any resulting chained/startup side effects.
            yield return new WaitForSecondsRealtime(0.25f);

            if (q == null || q.QuestComplete || IsQuestSharingBlacklisted(q))
                yield break;
        }

        // Record taker for this quest uid (this local player).
        _questTakerByUid[q.UID] = _localNetId;

        string[] granted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
        int[] popups = CaptureGetItemPopupIdsForSymbols(q, granted);

        StartPacket pkt = BuildStartPacket(q, inst, _localNetId, granted, popups);
		if (Debug.isDebugBuild)
			Debug.Log($"[QuestNetSync] Cli_Start uid={pkt.uid} taker={pkt.takerNetId} granted={string.Join(",", pkt.grantedSymbols ?? new string[0])} popups={(pkt.grantedPopupIds != null ? pkt.grantedPopupIds.Length : 0)}");

        string sendError;
        if (!SendStartPacketToServerSmart(pkt, out sendError))
        {
            Debug.LogWarning("[QuestNetSync][StartPacketChunk] Client quest start was not sent: " + sendError);
        }
        else
        {
            // Build the authoritative snapshot first, then reconcile any child quests
            // that already genuinely exist. Missing children remain incomplete and are
            // deferred at runtime by Task.Update().
            ApplyClientQuestChainAuthority(q, "local-start-packet-sent");
        }
    }

    private StartPacket BuildStartPacket(Quest q, string inst, uint takerNetId, string[] granted, int[] popupIds, bool shareOnlyIfMissing = false)
    {
        return new StartPacket
        {
            instanceId = inst,
            questName  = q.QuestName,
            factionId  = q.FactionId,
            uid        = q.UID,
            manualShareFingerprint = (IsManualShareInstanceId(inst) || shareOnlyIfMissing) ? BuildManualShareFingerprint(q) : string.Empty,
            shareOnlyIfMissing = shareOnlyIfMissing,
            // Every one-time StartPacket carries action completion state. This lets a
            // newly imported quest suppress its startup GetItem actions, restore them
            // exactly, then allow all players to execute later progress/reward actions.
            taskSaveDataJson = BuildTaskSaveDataJson(q),
            questSaveDataJson = BuildQuestSaveDataJson(q),
            questSuccess = q.QuestSuccess,
            takerNetId = takerNetId,
            grantedSymbols = granted,
            grantedPopupIds = popupIds,
            startupSayMessageIds = BuildCompletedSayMessageIds(q),
            revealedPlaceSymbols = BuildCompletedRevealPlaceSymbols(q),
            persons    = BuildPersons(q),
            places     = BuildPlaces(q),
            items      = BuildItems(q),
            itemsFull  = BuildFullItems(q),
            clocks     = BuildClocks(q),
            foes       = BuildFoes(q),
            tasks      = ToTaskDTOs(q, q.GetTaskStates()),
            logs       = (q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(L => new LogEntryDTO { stepID = L.stepID, messageID = L.messageID }).ToArray()
        };
    }

    private static bool HasUnresolvedTriggeredQuestPrompt(Quest q)
    {
        if (q == null)
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || !task.IsTriggered || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null)
                        continue;

                    string actionTypeName = action.GetType().Name;
                    bool normalPrompt =
                        string.Equals(
                            actionTypeName,
                            "Prompt",
                            StringComparison.Ordinal);
                    bool multiPrompt =
                        string.Equals(
                            actionTypeName,
                            "PromptMulti",
                            StringComparison.Ordinal);
                    if (!normalPrompt && !multiPrompt)
                        continue;

                    // The prompt has not displayed its window yet.
                    if (!action.IsComplete)
                        return true;

                    object saveData = null;
                    try { saveData = action.GetSaveData(); } catch { }
                    if (saveData == null)
                        continue;

                    Type saveType = saveData.GetType();
                    BindingFlags flags =
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic;

                    List<string> optionTaskNames = new List<string>(4);
                    if (normalPrompt)
                    {
                        FieldInfo yesField =
                            saveType.GetField("yesTaskSymbol", flags);
                        FieldInfo noField =
                            saveType.GetField("noTaskSymbol", flags);

                        string yesTaskName = yesField != null
                            ? GetSymbolName(yesField.GetValue(saveData))
                            : string.Empty;
                        string noTaskName = noField != null
                            ? GetSymbolName(noField.GetValue(saveData))
                            : string.Empty;

                        if (!string.IsNullOrEmpty(yesTaskName))
                            optionTaskNames.Add(yesTaskName);
                        if (!string.IsNullOrEmpty(noTaskName))
                            optionTaskNames.Add(noTaskName);
                    }
                    else
                    {
                        for (int optionIndex = 1;
                             optionIndex <= 4;
                             optionIndex++)
                        {
                            FieldInfo optionField =
                                saveType.GetField(
                                    "opt" + optionIndex.ToString() +
                                    "TaskSymbol",
                                    flags);
                            string optionTaskName = optionField != null
                                ? GetSymbolName(
                                    optionField.GetValue(saveData))
                                : string.Empty;
                            if (!string.IsNullOrEmpty(optionTaskName))
                                optionTaskNames.Add(optionTaskName);
                        }
                    }

                    bool anySelected = false;
                    for (int optionIndex = 0;
                         optionIndex < optionTaskNames.Count;
                         optionIndex++)
                    {
                        DaggerfallWorkshop.Game.Questing.Task optionTask =
                            q.GetTask(
                                new Symbol(
                                    optionTaskNames[optionIndex]));
                        if (optionTask != null &&
                            optionTask.IsTriggered)
                        {
                            anySelected = true;
                            break;
                        }
                    }

                    // Prompt.Update()/PromptMulti.Update() has completed, but no callback
                    // task has been selected yet: the player is still looking at the
                    // modal choice window.
                    if (!anySelected)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[QuestNetSync][QuestStartOffer] Prompt-state scan failed: " +
                ex.Message);
        }

        return false;
    }

    private static int[] BuildCompletedSayMessageIds(Quest q)
    {
        HashSet<int> messageIds = new HashSet<int>();
        if (q == null)
            return messageIds.ToArray();

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null || !action.IsComplete ||
                        !string.Equals(
                            action.GetType().Name,
                            "Say",
                            StringComparison.Ordinal))
                        continue;

                    object saveData = null;
                    try { saveData = action.GetSaveData(); } catch { }
                    if (saveData == null)
                        continue;

                    FieldInfo idField = saveData.GetType().GetField(
                        "id",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (idField == null)
                        continue;

                    object value = idField.GetValue(saveData);
                    if (value is int && (int)value != 0)
                        messageIds.Add((int)value);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[QuestNetSync][QuestStartOffer] Say-state scan failed: " +
                ex.Message);
        }

        return messageIds.OrderBy(id => id).ToArray();
    }

    private static void ApplyStartPacketSayMessages(
        StartPacket pkt,
        Quest q)
    {
        if (q == null || pkt.startupSayMessageIds == null ||
            pkt.startupSayMessageIds.Length == 0 ||
            IsManualShareStartPacket(pkt))
            return;

        for (int i = 0; i < pkt.startupSayMessageIds.Length; i++)
        {
            int messageId = pkt.startupSayMessageIds[i];
            if (messageId == 0)
                continue;

            string key =
                (pkt.instanceId ?? string.Empty) + "|say|" + messageId.ToString();
            if (!_appliedStartupSayMessages.Add(key))
                continue;

            q.ShowMessagePopup(messageId, true);
        }
    }

    private static string[] BuildCompletedRevealPlaceSymbols(Quest q)
    {
        HashSet<string> revealed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (q == null)
            return revealed.ToArray();

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null || !action.IsComplete ||
                        !string.Equals(action.GetType().Name, "RevealLocation", StringComparison.Ordinal))
                        continue;

                    object placeSymbolValue = null;
                    FieldInfo placeField = action.GetType().GetField(
                        "placeSymbol",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (placeField != null)
                        placeSymbolValue = placeField.GetValue(action);

                    // DFU's serialized RevealLocation state also exposes placeSymbol.
                    // Keep this fallback for versions where the runtime field changes.
                    if (placeSymbolValue == null)
                    {
                        object saveData = action.GetSaveData();
                        if (saveData != null)
                        {
                            FieldInfo savePlaceField = saveData.GetType().GetField(
                                "placeSymbol",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (savePlaceField != null)
                                placeSymbolValue = savePlaceField.GetValue(saveData);
                        }
                    }

                    string symbolName = GetSymbolName(placeSymbolValue);
                    if (!string.IsNullOrEmpty(symbolName))
                        revealed.Add(symbolName);
                }
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] Failed to capture completed RevealLocation actions: " + ex.Message);
        }

        return revealed.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ApplyStartPacketRevealedLocations(Quest q, string[] placeSymbols)
    {
        if (q == null || placeSymbols == null || placeSymbols.Length == 0 ||
            GameManager.Instance == null || GameManager.Instance.PlayerGPS == null)
            return;

        HashSet<string> applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < placeSymbols.Length; i++)
        {
            string symbolName = placeSymbols[i];
            if (string.IsNullOrEmpty(symbolName) || !applied.Add(symbolName))
                continue;

            Place place = q.GetPlace(new Symbol(symbolName));
            if (place == null || string.IsNullOrEmpty(place.SiteDetails.regionName) ||
                string.IsNullOrEmpty(place.SiteDetails.locationName))
                continue;

            // DiscoverLocation is idempotent. Do not replay the read-map notebook note:
            // this is state catch-up for the imported quest, not a second quest event.
            GameManager.Instance.PlayerGPS.DiscoverLocation(
                place.SiteDetails.regionName,
                place.SiteDetails.locationName);
            _remoteLocationRevealsApplied.Add(MakeLocationRevealKey(q.UID, symbolName));

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Restored shared quest map location uid={q.UID} place='{symbolName}' location='{place.SiteDetails.locationName}'");
        }
    }

    private void Srv_OnQuestEnded(Quest q)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || q == null) return;
        if (IsQuestSharingBlacklisted(q)) return;
        MarkQuestLocalStarted(q.UID);

        // A final "when ..." task can already be true while its MakePermanent action
        // has not received a normal quest tick yet. MP end fanout/cleanup must not
        // delete or serialize that still-green physical quest item first. Apply only
        // MakePermanent actions owned by tasks that are actually triggered in the
        // authoritative ending quest. This is idempotent and quest-agnostic.
        ApplyTriggeredMakePermanentActionsForEndingQuest(
            q,
            null,
            "server-local-end");

        RemoveNonPermanentQuestInventoryItems(q);

        uint endSourceNetId = 0;
        if (!_questEndSourceNetIdByUid.TryGetValue(q.UID, out endSourceNetId))
            endSourceNetId = _localNetId;

        string inst;
        if (_srvUid2Inst.TryGetValue(q.UID, out inst))
        {
            // Block late CmdClientEnded echoes from clients that are only ending this
            // quest because they received our EndPacket. This is the host double-reward fix.
            _serverRecentlyEndedQuestUids.Add(q.UID);
            _serverQuestEndInProgressUids.Add(q.UID);

            Quest.TaskState[] finalTasks = q.GetTaskStates();
            Quest.LogEntry[] finalLogs = q.GetLogMessages() ?? new Quest.LogEntry[0];
            Dictionary<string, ItemState> finalItems = CaptureItemStates(q);
            PlaceDTO[] finalPlaces = BuildPlaces(q);
            PersonDTO[] finalPersons = BuildPersons(q);
            FoeDTO[] finalFoes = BuildFoes(q);

            EndPacket endPacket = new EndPacket
            {
                instanceId = inst,
                uid = q.UID,
                sourceNetId = endSourceNetId,
                questSuccess = q.QuestSuccess,
                tasks = ToTaskDTOs(q, finalTasks),
                logs = finalLogs.Select(L => new LogEntryDTO { stepID = L.stepID, messageID = L.messageID }).ToArray(),
                items = ToItemDTOs(finalItems),
                places = finalPlaces,
                persons = finalPersons,
                foes = finalFoes,
                replayRewardTasks = q.QuestSuccess ? FindTasksWithRewardActionToReplay(q) : new string[0],
            };

            ServerBroadcastEndPacket(endPacket);

            _srvUid2Inst.Remove(q.UID);
            _srvInst2Uid.Remove(inst);
            _srvLastTasks.Remove(inst);
            _srvLastLogs.Remove(inst);
            _srvLastItems.Remove(inst);
            _srvLastPersons.Remove(inst);
            _srvLastPlaces.Remove(inst);
            _srvLastFoes.Remove(inst);
            _srvQuestObjectByUid.Remove(q.UID);
            _questEndSourceNetIdByUid.Remove(q.UID);
        }
    }

    private static void ServerBroadcastEndPacket(EndPacket endPacket)
    {
        int connectedCount = 0;
        int sentCount = 0;
        int missingIdentityCount = 0;
        int missingSyncCount = 0;

        // ClientRpc is observer-based. A passive player in another town/interior can
        // stop observing the host player object and therefore miss the final task state
        // that repairs and drains unfinished local reward chains. Deliver through each
        // connection's own player object, which remains addressable even while hidden.
        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection connection = entry.Value;
            if (connection == null)
                continue;

            connectedCount++;

            NetworkIdentity identity = connection.identity;
            if (identity == null)
            {
                missingIdentityCount++;
                continue;
            }

            QuestNetSync recipientSync =
                identity.GetComponent<QuestNetSync>();
            if (recipientSync == null)
            {
                recipientSync =
                    identity.GetComponentInChildren<QuestNetSync>(true);
            }

            if (recipientSync == null || !recipientSync.isServer)
            {
                missingSyncCount++;
                continue;
            }

            recipientSync.TargetEndPacket(
                connection,
                endPacket);
            sentCount++;
        }

        Debug.Log(
            $"[QuestNetSync][EndFanout] Sent final quest state " +
            $"uid={endPacket.uid} inst='{endPacket.instanceId}' " +
            $"connected={connectedCount} recipients={sentCount} " +
            $"missingIdentity={missingIdentityCount} missingSync={missingSyncCount} " +
            $"source={endPacket.sourceNetId}");
    }

    private void Srv_OnTick()
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer) return;

        RefreshCurrentSiteQuestResourceObjects();

        Srv_PruneToActive();
        ulong[] actives = QuestMachine.Instance.GetAllActiveQuests();
        for (int i = 0; i < actives.Length; i++)
        {
            ulong uid = actives[i];
            string inst;
            Quest q = QuestMachine.Instance.GetQuest(uid);
            if (q == null) continue;
            if (IsQuestSharingBlacklisted(q))
            {
                Srv_RemoveMappingByUid(uid);
                continue;
            }

            if (!_srvUid2Inst.TryGetValue(uid, out inst) ||
                string.IsNullOrEmpty(inst))
            {
                // Do not auto-share quests merely because they were loaded from a save.
                // Save/load can roll one machine back while another player has already
                // completed that quest in the current MP session. Re-announcing every
                // loaded active quest here was resurrecting completed quests and replaying
                // GetItem grants (e.g. Rare Book). Only quests started through the live
                // MP start paths are registered in _srvUid2Inst and synced from here.
                if (Debug.isDebugBuild)
                {
                    // Debug.Log(
                    //     $"[QuestNetSync] Skipping unmapped loaded quest on server tick " +
                    //     $"uid={uid} name={q.QuestName}");
                }

                // This must be unconditional. Previously the commented Debug.Log left
                // this continue as the body of if (Debug.isDebugBuild), so release builds
                // fell through with inst == null and called Dictionary.TryGetValue(null).
                continue;
            }

            Quest.TaskState[] nowTasks = q.GetTaskStates();
            Quest.LogEntry[]  nowLogs  = q.GetLogMessages() ?? new Quest.LogEntry[0];
            Dictionary<string, ItemState> nowItems = CaptureItemStates(q);
            PlaceDTO[] nowPlaces = BuildPlaces(q);
            PersonDTO[] nowPersons = BuildPersons(q);
            FoeDTO[] nowFoes = BuildFoes(q);
            FoeDTO[] previousFoes;
            _srvLastFoes.TryGetValue(inst, out previousFoes);
            nowFoes = SanitizeVolatileFoeStateForNetwork(q, previousFoes, nowFoes, "server-tick");

            bool tasksChanged =
                !_srvLastTasks.ContainsKey(inst) ||
                !SameTasks(q, _srvLastTasks[inst], nowTasks);
            bool logsChanged =
                !_srvLastLogs.ContainsKey(inst) ||
                !_srvLastLogs[inst].SetEquals(nowLogs.Select(l => l.stepID));
            bool itemsChanged =
                !_srvLastItems.ContainsKey(inst) ||
                !SameItems(_srvLastItems[inst], nowItems);
            bool placesChanged =
                !_srvLastPlaces.ContainsKey(inst) ||
                !SamePlaces(_srvLastPlaces[inst], nowPlaces);
            bool personsChanged =
                !_srvLastPersons.ContainsKey(inst) ||
                !SamePersons(_srvLastPersons[inst], nowPersons);
            bool foesChanged =
                !_srvLastFoes.ContainsKey(inst) ||
                !SameFoes(_srvLastFoes[inst], nowFoes);

            bool changed =
                tasksChanged || logsChanged || itemsChanged ||
                placesChanged || personsChanged || foesChanged;

            // Host-local TotingItemAndClickedNpc can complete before its explicit
            // side-effect RPC is observed by remote clients. The normal ItemDTO path
            // intentionally never applies negative inventory state, so those clients
            // can receive the correct popup/task while retaining the delivered letter.
            // Detect the authoritative server's real true->false inventory edge on a
            // newly-triggered toting task and broadcast only that removal explicitly.
            if (tasksChanged && itemsChanged)
            {
                Dictionary<string, ItemState> previousItems;
                Quest.TaskState[] previousTasks;
                _srvLastItems.TryGetValue(inst, out previousItems);
                _srvLastTasks.TryGetValue(inst, out previousTasks);

                BroadcastServerTotingInventoryRemovals(
                    q,
                    previousTasks,
                    nowTasks,
                    previousItems,
                    nowItems);
            }

            if (changed)
            {
                TraceServerQuestTraffic(
                    q,
                    inst,
                    "tick",
                    nowTasks,
                    nowLogs,
                    nowItems,
                    nowPlaces,
                    nowPersons,
                    nowFoes,
                    tasksChanged,
                    logsChanged,
                    itemsChanged,
                    placesChanged,
                    personsChanged,
                    foesChanged);
                RpcUpdate(new UpdatePacket
                {
                    instanceId = inst,
                    sourceNetId = _localNetId,
                    tasks = ToTaskDTOs(q, nowTasks),
                    logs  = nowLogs.Select(L => new LogEntryDTO { stepID = L.stepID, messageID = L.messageID }).ToArray(),
                    items = ToItemDTOs(nowItems),
                    places = nowPlaces,
                    persons = nowPersons,
                    foes = nowFoes,
                    questSuccess = q.QuestSuccess,
                });

                _srvLastTasks[inst] = nowTasks;
                _srvLastLogs[inst]  = new HashSet<int>(nowLogs.Select(l => l.stepID));
                _srvLastItems[inst] = nowItems;
                _srvLastPersons[inst] = nowPersons;
                _srvLastPlaces[inst] = nowPlaces;
                _srvLastFoes[inst] = nowFoes;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CLIENT: apply network
    // ─────────────────────────────────────────────────────────────────────────────
    [ClientRpc]
    private void RpcStart(StartPacket pkt)
    {
        ApplyStartPacket(pkt);
    }

    private void ApplyStartPacket(StartPacket pkt)
    {
        if (!isClient) return;

        bool manualShare = IsManualShareStartPacket(pkt);
        if (manualShare && IsQuestNetSyncPausedForLoad())
        {
            QueuePendingManualShare(pkt, "quest-load-settle");
            return;
        }

        if (manualShare && !IsAuthoritativeTimeReadyForQuestSharing())
        {
            QueuePendingManualShare(pkt, "authoritative-time-pending");
            return;
        }

        if (manualShare && QuestMachine.Instance == null)
        {
            QueuePendingManualShare(pkt, "quest-machine-not-ready");
            return;
        }

        if (IsQuestNetSyncPausedForLoad()) return;
        if (IsQuestSharingBlacklistedName(pkt.questName)) return;
        if (QuestMachine.Instance == null) return;

        // Existing instance mapping: always apply by this machine's local UID.
        ulong boundUid;
        if (_cliInst2Uid.TryGetValue(pkt.instanceId, out boundUid))
        {
            Quest bound = QuestMachine.Instance.GetQuest(boundUid);
            if (bound != null)
            {
                if (pkt.shareOnlyIfMissing)
                {
                    // This player already participates in this exact quest instance.
                    // Reject the catch-up payload without overwriting progress or
                    // replaying any StartPacket item/reward side effects.
                    RegisterClientQuestMapping(pkt.instanceId, bound);
                    if (Debug.isDebugBuild)
                        Debug.Log($"[QuestNetSync][MissingOnlyShare] Ignored one-time share for existing inst={pkt.instanceId} localUid={bound.UID} quest='{pkt.questName}'");
                    return;
                }

                // Manual re-share should be an idempotent "you already have this quest" bind.
                // Do not push remote item/task/log state over a local copy that may be at a
                // different step, and never replay StartPacket GetItem grants/popups.
                if (IsManualShareInstanceId(pkt.instanceId))
                {
                    RegisterClientQuestMapping(pkt.instanceId, bound);
                    if (Debug.isDebugBuild)
                        Debug.Log($"[QuestNetSync][ManualShareDuplicateGuard] Ignored already-mapped manual share inst={pkt.instanceId} localUid={bound.UID} sourceUid={pkt.uid} quest='{pkt.questName}'");
                    return;
                }

                // Normal MP start/resume still applies state, but does not replay GetItem.
                ApplyStartStateToQuest(pkt, bound, true, false);
                RegisterClientQuestMapping(pkt.instanceId, bound);
            }
            return;
        }

        if (pkt.shareOnlyIfMissing)
        {
            Quest existingLocalQuest;
            if (TryFindLocalQuestForMissingOnlyShare(pkt, out existingLocalQuest) && existingLocalQuest != null)
            {
                string otherInstance;
                bool mappedElsewhere = _cliUid2Inst.TryGetValue(existingLocalQuest.UID, out otherInstance) &&
                    !string.Equals(otherInstance, pkt.instanceId, StringComparison.Ordinal);

                // If this local quest was not already attached to another network
                // instance, bind it so future sparse progress works normally. Either
                // way, never apply this one-time snapshot to an existing quest.
                if (!mappedElsewhere)
                {
                    RegisterClientQuestMapping(pkt.instanceId, existingLocalQuest);
                    if (pkt.takerNetId != 0U)
                        _questTakerByUid[existingLocalQuest.UID] = pkt.takerNetId;
                }

                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync][MissingOnlyShare] Existing local quest rejected catch-up state localUid={existingLocalQuest.UID} inst={pkt.instanceId} previousInst='{otherInstance ?? "<none>"}' quest='{pkt.questName}'");
                return;
            }
        }

        if (_startingInst.Contains(pkt.instanceId)) return;
        _startingInst.Add(pkt.instanceId);

        try
        {
            // Host mode shares one QuestMachine for server and local client. If the server
            // already imported this instance under a different local UID, bind the local
            // client view to that server-local UID. Never bind by quest name.
            if (isServer)
            {
                ulong serverLocalUid;
                if (_srvInst2Uid.TryGetValue(pkt.instanceId, out serverLocalUid))
                {
                    Quest hostQ = QuestMachine.Instance.GetQuest(serverLocalUid);
                    if (hostQ != null)
                    {
                        RegisterClientQuestMapping(pkt.instanceId, hostQ);
                        if (Debug.isDebugBuild && serverLocalUid != pkt.uid)
                            Debug.Log($"[QuestNetSync][LocalUidImport] Host client bound inst={pkt.instanceId} sourceUid={pkt.uid} localUid={serverLocalUid} quest='{pkt.questName}'");
                    }
                }
                return;
            }

            Quest chosenQuest = null;
            bool chosenQuestWasNewlyImported = false;
            bool allowStartPacketGetItemReplication = false;
            Quest atSourceUid = pkt.uid != 0UL ? QuestMachine.Instance.GetQuest(pkt.uid) : null;
            string mappedInstAtSourceUid;
            bool sourceUidMapped = _cliUid2Inst.TryGetValue(pkt.uid, out mappedInstAtSourceUid);
            bool sourceUidSameInstance = sourceUidMapped && string.Equals(mappedInstAtSourceUid, pkt.instanceId, StringComparison.Ordinal);
            bool sourceUidDifferentInstance = sourceUidMapped && !sourceUidSameInstance;
            bool sourceEcho = (_localNetId != 0U && pkt.takerNetId == _localNetId);

            if (atSourceUid != null && sourceUidSameInstance)
            {
                chosenQuest = atSourceUid;
            }
            else if (IsManualShareInstanceId(pkt.instanceId) && atSourceUid != null && !sourceUidMapped && ManualShareFingerprintMatches(pkt, atSourceUid))
            {
                // Same generated manual-shared quest is already present under the sender UID,
                // but runtime mapping was lost by load/reconnect. Bind only. Do not import a
                // second copy and do not force the sharer's progress/items onto this local quest.
                RegisterClientQuestMapping(pkt.instanceId, atSourceUid);
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync][ManualShareDuplicateGuard] Ignored same local manual quest at sourceUid={pkt.uid} inst={pkt.instanceId} quest='{pkt.questName}'");
                return;
            }
            else if (atSourceUid != null && !sourceUidMapped && sourceEcho && PacketMatchesLocalQuestGeneratedState(pkt, atSourceUid))
            {
                // Original sharer receiving their own StartPacket echo after the server accepts it.
                // Keep the original local UID and bind it to the network instance.
                chosenQuest = atSourceUid;
                allowStartPacketGetItemReplication = false;
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync][LocalUidImport] Source echo bound existing local quest uid={pkt.uid} inst={pkt.instanceId} quest='{pkt.questName}'");
            }
            else if (atSourceUid != null && !sourceUidMapped && PacketMatchesLocalQuestGeneratedState(pkt, atSourceUid))
            {
                // Reconnect/re-share case: this machine already has the same generated quest
                // from a previous session, but runtime mappings were cleared.
                chosenQuest = atSourceUid;
                allowStartPacketGetItemReplication = false;
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync][LocalUidImport] Rebound existing same generated quest uid={pkt.uid} inst={pkt.instanceId} quest='{pkt.questName}'");
            }
            else
            {
                Quest sameGeneratedQuest;
                string sameGeneratedMappedInst;
                if (IsManualShareInstanceId(pkt.instanceId) &&
                    (TryFindAnyLocalQuestByManualShareFingerprint(pkt, out sameGeneratedQuest) ||
                     TryFindAnyLocalQuestByFingerprint(pkt, out sameGeneratedQuest)) &&
                    sameGeneratedQuest != null &&
                    (!_cliUid2Inst.TryGetValue(sameGeneratedQuest.UID, out sameGeneratedMappedInst) ||
                     string.Equals(sameGeneratedMappedInst, pkt.instanceId, StringComparison.Ordinal) ||
                     IsManualShareInstanceId(sameGeneratedMappedInst)))
                {
                    // Runtime mapping was lost by disconnect/load, or a new manual share
                    // instanceId was generated after load. The same generated quest is already
                    // present locally. Bind only and stop here: do not import a duplicate, do not
                    // grant delivery/fake-gold items again, and do not overwrite local progress.
                    RegisterClientQuestMapping(pkt.instanceId, sameGeneratedQuest);
                    if (Debug.isDebugBuild)
                        Debug.Log($"[QuestNetSync][ManualShareDuplicateGuard] Ignored duplicate manual share; existing localUid={sameGeneratedQuest.UID} sourceUid={pkt.uid} inst={pkt.instanceId} previousInst='{sameGeneratedMappedInst ?? "<none>"}' quest='{pkt.questName}'");
                    return;
                }
                else
                {
                    ulong localUidToUse = pkt.uid;
                    bool sourceUidOccupiedByDifferentQuest = atSourceUid != null && !sourceUidSameInstance;

                    if (localUidToUse == 0UL || sourceUidOccupiedByDifferentQuest || sourceUidDifferentInstance)
                        localUidToUse = AllocateFreshLocalQuestUid(pkt.uid);

                    if (sourceUidOccupiedByDifferentQuest && Debug.isDebugBuild)
                    {
                        Debug.Log($"[QuestNetSync][LocalUidImport] Incoming quest UID collision. Keeping local uid={pkt.uid} quest='{atSourceUid.QuestName}', importing shared quest='{pkt.questName}' inst={pkt.instanceId} as localUid={localUidToUse}");
                    }

                    chosenQuest = StartQuestFromPacketWithLocalUid(pkt, localUidToUse, false);
                    chosenQuestWasNewlyImported = chosenQuest != null;
                    allowStartPacketGetItemReplication = chosenQuestWasNewlyImported && (_localNetId == 0U || pkt.takerNetId != _localNetId);
                }
            }

            if (chosenQuest != null)
            {
                RegisterClientQuestMapping(pkt.instanceId, chosenQuest);

                if (chosenQuestWasNewlyImported && pkt.shareOnlyIfMissing)
                {
                    // A reconstructed quest is not allowed to report completion while
                    // its one-time catch-up snapshot is settling. This is a defensive
                    // backstop: a malformed/older packet must never end the quest for
                    // players who already had it.
                    _clientCatchupEndSuppressUntil[chosenQuest.UID] = Time.realtimeSinceStartup + 5f;
                }

                ApplyStartStateToQuest(
                    pkt,
                    chosenQuest,
                    true,
                    allowStartPacketGetItemReplication,
                    chosenQuestWasNewlyImported);
                RegisterClientQuestMapping(pkt.instanceId, chosenQuest);

                // Starting a child from a packet can inject its Person into an
                // already-loaded scene even though an older quest copy of that same
                // named person still occupies the marker. Suppress only an overlapping
                // current-scene duplicate; the placed resource itself remains active
                // and is recreated normally after a scene reload.
                if (chosenQuestWasNewlyImported)
                    SuppressOverlappingImportedQuestPersons(chosenQuest);

                MarkQuestRemoteStarted(chosenQuest.UID);
            }
        }
        finally
        {
            _startingInst.Remove(pkt.instanceId);
        }
    }

    private static void QueuePendingRoutineUpdate(UpdatePacket up, string reason)
    {
        if (string.IsNullOrEmpty(up.instanceId))
            return;

        _pendingRoutineUpdatesByInstance[up.instanceId] = up;

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][LoadResume] Queued live update inst='{up.instanceId}' " +
                $"reason={reason}");
        }
    }

    private void TryApplyPendingRoutineUpdate(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId) ||
            IsQuestNetSyncPausedForLoad())
            return;

        UpdatePacket pending;
        if (!_pendingRoutineUpdatesByInstance.TryGetValue(instanceId, out pending))
            return;

        _pendingRoutineUpdatesByInstance.Remove(instanceId);

        Debug.Log(
            $"[QuestNetSync][LoadResume] Applying queued live update inst='{instanceId}'");

        // Direct local call intentionally reuses the same validation/application path.
        RpcUpdate(pending);
    }

    [ClientRpc]
    private void RpcUpdate(UpdatePacket up)
    {
        if (!isClient) return;

        // Do not apply our own network echo back onto the same local quest.
        // This was re-starting already-completed reward tasks in host mode and
        // could grant gold/reputation twice.
        if (_localNetId != 0 && up.sourceNetId == _localNetId)
            return;

        // In host mode, the server/Command path already applied this state to the
        // authoritative local quest object. Applying the ClientRpc again is always
        // a duplicate and is especially dangerous for reward tasks.
        if (isServer)
            return;

        // Save/load can temporarily leave a pure client without the server's newest
        // runtime instance mapping. UpdatePacket is a full current-state snapshot, so
        // retain the newest one until load pause ends and/or TargetResume binds it.
        if (IsQuestNetSyncPausedForLoad())
        {
            QueuePendingRoutineUpdate(up, "load-paused");
            return;
        }

        ulong uid;
        if (!_cliInst2Uid.TryGetValue(up.instanceId, out uid))
        {
            QueuePendingRoutineUpdate(up, "mapping-not-ready");
            return;
        }

        Quest q = QuestMachine.Instance.GetQuest(uid);
        if (q == null) return;

        PlaceDTO[] placesBeforeApply = BuildPlaces(q);
        bool placeStateChanged = !SamePlaces(placesBeforeApply, up.places);

        _applying.Add(up.instanceId);
        try
        {
            ApplyPlaces(q, up.places);
            ApplyPersons(q, up.persons);
            ApplyFoes(q, up.foes, true);
            // Do not sync QuestSuccess during routine deltas. In DFU this flag is
            // affected by GivePc actions and can be true before the quest actually
            // succeeds (e.g. delivery/fake item grants). Let the local quest script
            // and the final EndPacket decide success/failure so timeouts still fail
            // with the same reputation behavior as SP.
            ApplyDesiredState(q, up.tasks, up.logs);
            _cliLastTasks[up.instanceId] = q.GetTaskStates();
            _cliLastLogs[up.instanceId]  = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
            ApplyItems(q, up.items);

            // PlaceItem only assigns a resource symbol to a Place marker. When that
            // assignment arrives after this site is already loaded, the layout-time
            // injector has already run. Re-run only its item branch.
            if (placeStateChanged)
                RefreshCurrentSiteQuestItemObjects();

            _cliLastItems[up.instanceId] = CaptureItemStates(q);
            _cliLastPersons[up.instanceId] = BuildPersons(q);
            _cliLastFoes[up.instanceId] = BuildFoes(q);
        }
        finally
        {
            _applying.Remove(up.instanceId);
        }
    }

    [TargetRpc]
    private void TargetEndPacket(
        NetworkConnection target,
        EndPacket ep)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient) return;

        Debug.Log(
            $"[QuestNetSync][EndFanout] Received final quest state " +
            $"uid={ep.uid} inst='{ep.instanceId}' localNet={_localNetId} " +
            $"source={ep.sourceNetId} hostMode={isServer}");

        // The player who completed the quest already ran vanilla GivePc locally.
        // Never replay the remote reward on that same player.
        if (_localNetId != 0 && ep.sourceNetId == _localNetId)
        {
            CleanupClientMapping(ep.instanceId, 0UL);
            return;
        }

        // In host mode, client completions are applied in CmdClientEnded() and host
        // rewards are replayed there exactly once. The host must not also process
        // the targeted EndPacket reward replay.
        if (isServer)
        {
            CleanupClientMapping(ep.instanceId, 0UL);
            return;
        }

        // Manual journal shares are current-players-only. A client who joined later
        // but was not explicitly shared into this quest will not have this instance
        // mapping. Ignore the end packet so we do not tombstone an unrelated/local
        // quest that happens to have the same UID after save/load.
        ulong mappedEndUid;
        if (!_cliInst2Uid.TryGetValue(ep.instanceId, out mappedEndUid))
        {
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][EndFanout] Ignored final state without local mapping " +
                    $"remoteUid={ep.uid} inst='{ep.instanceId}'");
            }
            return;
        }

        // ep.uid is the sender/server local UID. This machine can legitimately use a
        // different local UID for the same instance after UID-collision-safe import.
        if (Debug.isDebugBuild && mappedEndUid != ep.uid)
            Debug.Log($"[QuestNetSync][LocalUidImport] EndPacket using localUid={mappedEndUid} for inst={ep.instanceId} remoteUid={ep.uid}");

        // This End came from another machine. Any local OnQuestEnded raised while
        // applying/replaying/tombstoning it must NOT be reported back as a new local
        // completion, or the host will replay GivePc a second time.
        _suppressClientQuestEndReportUids.Add(mappedEndUid);

        Quest q = QuestMachine.Instance.GetQuest(mappedEndUid);
        if (q != null && !q.QuestComplete)
        {
            _applying.Add(ep.instanceId);
            try
            {
                ApplyPlaces(q, ep.places);
                // This is an authoritative final snapshot. Preserve Person data, but
                // never make scene NPCs visible again while the ending quest's reward
                // chain is still draining.
                ApplyPersons(q, ep.persons, true);
                ApplyFoes(q, ep.foes);
                q.QuestSuccess = ep.questSuccess;
                ApplyDesiredStateForRemoteEnd(q, ep.tasks, ep.logs);

                // ApplyDesiredStateForRemoteEnd deliberately skips action tasks. For a
                // successful end, restore only authoritative GetItem + MakePermanent
                // reward tasks that this slower participant has not triggered yet.
                if (ep.questSuccess)
                {
                    PrimePermanentGetItemRewardsFromRemoteEnd(
                        q,
                        ep.tasks,
                        "client-remote-end");
                }

                // Remote-end task application deliberately skips action tasks. That is
                // correct for GivePc/EndQuest/reputation, but a triggered MakePermanent
                // task is a durable item-state transition and must be completed before
                // item reconstruction and delayed end cleanup.
                ApplyTriggeredMakePermanentActionsForEndingQuest(
                    q,
                    ep.tasks,
                    "client-endpacket");

                ApplyItems(q, ep.items);

                // Do not remove non-permanent quest inventory here. A local GetItem
                // reward can already be in inventory while its following Say window is
                // open and MakePermanent has not run yet. CoFinishRemoteEndedQuest()
                // performs cleanup only after the local reward chain has drained.

                // If the server already completed the final GivePc reward task before
                // a normal delta could be sent, replay that task locally on clients
                // that have not already completed its GivePc action. This is what
                // opens the local reward popup/trade window instead of just tombstoning.
                if (ep.questSuccess)
                    ForceReplayRewardTasksIfNeeded(q, ep.replayRewardTasks);
                else if (Debug.isDebugBuild && ep.replayRewardTasks != null && ep.replayRewardTasks.Length > 0)
                    Debug.Log($"[QuestNetSync][QuestFailure] Suppressed remote reward replay on failed quest uid={mappedEndUid} inst={ep.instanceId} tasks={string.Join(",", ep.replayRewardTasks)}");
            }
            finally
            {
                _applying.Remove(ep.instanceId);
            }

            // Do not call EndQuest() immediately. The vanilla EndQuest action gives
            // final reward/message tasks a couple of ticks to run. Calling it here
            // immediately can tombstone remote quests before GivePc opens its UI.
            StartCoroutine(CoFinishRemoteEndedQuest(mappedEndUid, ep.questSuccess));
        }

        CleanupClientMapping(ep.instanceId, mappedEndUid);
    }

    private void CleanupClientMapping(string instanceId, ulong uid)
    {
        ulong actualUid;
        if (!string.IsNullOrEmpty(instanceId) && _cliInst2Uid.TryGetValue(instanceId, out actualUid))
            uid = actualUid;

        _cliInst2Uid.Remove(instanceId);
        if (uid != 0UL)
        {
            _cliUid2Inst.Remove(uid);
            _cliQuestObjectByUid.Remove(uid);
        }
        _cliLastTasks.Remove(instanceId);
        _cliLastLogs.Remove(instanceId);
        _cliLastItems.Remove(instanceId);
        _cliLastPersons.Remove(instanceId);
        _cliLastFoes.Remove(instanceId);
    }

    private IEnumerator CoFinishRemoteEndedQuest(ulong uid, bool questSuccess)
    {
        // This coroutine is executing a remote quest end locally. Suppress the
        // QuestMachine.OnQuestEnded echo that DFU will raise from EndQuest()/Tombstone.
        _suppressClientQuestEndReportUids.Add(uid);

        // Remote quest completion is authoritative. Some remote machines cannot
        // satisfy the original local trigger anymore (for example, the other player
        // turned in a book/bodyguard quest while this player does not have the book
        // or did not perform the final click locally). In that case GivePc can be
        // replayed successfully, but the vanilla EndQuest action never runs on this
        // machine, leaving the quest active in the journal and its quest NPC/site link
        // alive. Give final reward/message replay a short window, then force the same
        // local EndQuest/Tombstone cleanup path.
        for (int i = 0; i < 15; i++)
        {
            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(uid) : null;
            if (q == null || q.QuestTombstoned)
                yield break;

            q.QuestSuccess = questSuccess;
            yield return null;
        }

        // Do not end or clean this local quest while its reward chain is still running.
        // A permanent GetItem reward is non-permanent until its later MakePermanent
        // action executes, and K0C00Y02 places a modal Say between those two actions.
        // Require several completely clear frames so closing one reward window has time
        // to let the next triggered reward task open its own window.
        int rewardClearFrames = 0;
        bool loggedPermanentRewardWait = false;
        while (rewardClearFrames < 3)
        {
            Quest pendingQ = QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(uid)
                : null;
            if (pendingQ == null || pendingQ.QuestTombstoned)
                yield break;

            pendingQ.QuestSuccess = questSuccess;

            bool pendingGivePc =
                HasPendingRewardReplayForQuest(uid);
            bool pendingPermanentGetItem =
                HasPendingTriggeredPermanentGetItemRewards(pendingQ);
            bool modalWindowOpen =
                ShouldDeferQuestInventoryApplyNow();

            if (pendingPermanentGetItem &&
                !loggedPermanentRewardWait)
            {
                loggedPermanentRewardWait = true;
                Debug.Log(
                    $"[QuestNetSync][GetItemEndBarrier] Waiting for local reward chain " +
                    $"uid={uid} quest='{pendingQ.QuestName}'");
            }

            if (pendingGivePc ||
                pendingPermanentGetItem ||
                modalWindowOpen)
            {
                rewardClearFrames = 0;
            }
            else
            {
                rewardClearFrames++;
            }

            yield return null;
        }

        Quest finalQ = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(uid) : null;
        if (finalQ == null)
            yield break;

        finalQ.QuestSuccess = questSuccess;

        // Cleanup belongs after reward completion. The old immediate EndPacket cleanup
        // removed freshly granted but not-yet-permanent rewards from slow participants.
        RemoveNonPermanentQuestInventoryItems(finalQ);

        if (!finalQ.QuestComplete)
        {
            try
            {
                finalQ.EndQuest();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestNetSync] Remote EndQuest failed uid={uid}: {ex.Message}");
            }

            // Give QuestMachine a few frames to tick the normal EndQuest countdown.
            for (int i = 0; i < 30; i++)
            {
                if (finalQ.QuestComplete || finalQ.QuestTombstoned)
                    break;
                yield return null;
            }
        }

        // If the local trigger path still did not tombstone the quest, force the
        // cleanup now. This removes site links/questors and makes re-entering the
        // building/dungeon stop respawning the quest NPC.
        if (QuestMachine.Instance != null && !finalQ.QuestTombstoned)
        {
            try
            {
                QuestMachine.Instance.TombstoneQuest(finalQ);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestNetSync] Remote quest tombstone failed uid={uid}: {ex.Message}");
            }
        }

        RemoveNonPermanentQuestInventoryItems(finalQ);
        DisableActiveQuestResourceObjectsForEndedQuest(uid);
    }

    private static void DisableActiveQuestResourceObjectsForEndedQuest(ulong uid)
    {
        int matched = 0;
        int hidden = 0;
        try
        {
            QuestResourceBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<QuestResourceBehaviour>();
            if (behaviours == null)
                return;

            for (int i = 0; i < behaviours.Length; i++)
            {
                QuestResourceBehaviour qrb = behaviours[i];
                if (qrb == null || qrb.QuestUID != uid || qrb.gameObject == null)
                    continue;

                // Only clean up visible quest scene resources. Do not touch quest foes here;
                // enemy cleanup is handled by the normal enemy/network systems.
                QuestResource res = qrb.TargetResource;
                if (res is Person || res is Item)
                {
                    matched++;
                    if (qrb.gameObject.activeSelf)
                    {
                        qrb.gameObject.SetActive(false);
                        hidden++;
                    }
                }
            }

            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][EndedResourceCleanup] uid={uid} " +
                    $"matched={matched} hidden={hidden}");
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] DisableActiveQuestResourceObjectsForEndedQuest failed: " + ex.Message);
        }
    }

    private static string GetQuestPersonSceneIdentity(Person person)
    {
        if (person == null)
            return string.Empty;

        string[] memberNames =
        {
            "DisplayName",
            "Name",
            "QuestorName",
            "displayName",
            "name",
            "questorName",
        };
        BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        Type type = person.GetType();
        for (int i = 0; i < memberNames.Length; i++)
        {
            try
            {
                PropertyInfo property =
                    type.GetProperty(memberNames[i], flags);
                if (property != null && property.PropertyType == typeof(string))
                {
                    string value = property.GetValue(person, null) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value.Trim().ToLowerInvariant();
                }

                FieldInfo field = type.GetField(memberNames[i], flags);
                if (field != null && field.FieldType == typeof(string))
                {
                    string value = field.GetValue(person) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value.Trim().ToLowerInvariant();
                }
            }
            catch { }
        }

        return string.Empty;
    }

    private static int RegisterPendingScenePersonHandoffs(
        Quest parentQuest,
        string childQuestName)
    {
        if (parentQuest == null || string.IsNullOrEmpty(childQuestName))
            return 0;

        string childTemplate =
            NormalizeQuestTemplateName(childQuestName);
        if (string.IsNullOrEmpty(childTemplate))
            return 0;

        QuestResourceBehaviour[] behaviours =
            Resources.FindObjectsOfTypeAll<QuestResourceBehaviour>();
        if (behaviours == null || behaviours.Length == 0)
            return 0;

        List<PendingScenePersonHandoff> handoffs;
        if (!_pendingScenePersonHandoffsByChild.TryGetValue(
                childTemplate,
                out handoffs))
        {
            handoffs = new List<PendingScenePersonHandoff>();
            _pendingScenePersonHandoffsByChild[childTemplate] = handoffs;
        }

        int added = 0;
        for (int i = 0; i < behaviours.Length; i++)
        {
            QuestResourceBehaviour qrb = behaviours[i];
            if (qrb == null ||
                qrb.QuestUID != parentQuest.UID ||
                qrb.gameObject == null ||
                !qrb.gameObject.activeSelf ||
                !qrb.gameObject.scene.IsValid() ||
                !qrb.gameObject.scene.isLoaded)
                continue;

            string identity = GetQuestPersonSceneIdentity(
                qrb.TargetResource as Person);
            if (string.IsNullOrEmpty(identity))
                continue;

            int sceneHandle = qrb.gameObject.scene.handle;
            Vector3 position = qrb.transform.position;
            bool duplicate = false;
            for (int j = 0; j < handoffs.Count; j++)
            {
                PendingScenePersonHandoff existing = handoffs[j];
                if (existing != null &&
                    existing.sceneHandle == sceneHandle &&
                    string.Equals(
                        existing.identity,
                        identity,
                        StringComparison.OrdinalIgnoreCase) &&
                    (existing.position - position).sqrMagnitude <= 0.01f)
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
                continue;

            handoffs.Add(
                new PendingScenePersonHandoff
                {
                    identity = identity,
                    sceneHandle = sceneHandle,
                    position = position,
                });
            added++;
        }

        if (handoffs.Count == 0)
            _pendingScenePersonHandoffsByChild.Remove(childTemplate);

        return added;
    }

    private static int SuppressOverlappingImportedQuestPersons(Quest importedQuest)
    {
        if (importedQuest == null)
            return 0;

        QuestResourceBehaviour[] behaviours =
            Resources.FindObjectsOfTypeAll<QuestResourceBehaviour>();
        if (behaviours == null || behaviours.Length == 0)
            return 0;

        string importedTemplate =
            NormalizeQuestTemplateName(importedQuest.QuestName);
        List<PendingScenePersonHandoff> pendingHandoffs;
        _pendingScenePersonHandoffsByChild.TryGetValue(
            importedTemplate,
            out pendingHandoffs);

        int hidden = 0;
        int hiddenFromSnapshot = 0;
        for (int i = 0; i < behaviours.Length; i++)
        {
            QuestResourceBehaviour incoming = behaviours[i];
            if (incoming == null ||
                incoming.QuestUID != importedQuest.UID ||
                incoming.gameObject == null ||
                !incoming.gameObject.activeSelf)
                continue;

            Person incomingPerson = incoming.TargetResource as Person;
            string incomingIdentity =
                GetQuestPersonSceneIdentity(incomingPerson);
            if (string.IsNullOrEmpty(incomingIdentity) ||
                !incoming.gameObject.scene.IsValid() ||
                !incoming.gameObject.scene.isLoaded)
                continue;

            bool overlapsExisting = false;
            for (int j = 0; j < behaviours.Length; j++)
            {
                QuestResourceBehaviour existing = behaviours[j];
                if (existing == null ||
                    existing == incoming ||
                    existing.QuestUID == importedQuest.UID ||
                    existing.gameObject == null ||
                    existing.gameObject.scene != incoming.gameObject.scene)
                    continue;

                Person existingPerson = existing.TargetResource as Person;
                string existingIdentity =
                    GetQuestPersonSceneIdentity(existingPerson);
                if (!string.Equals(
                        incomingIdentity,
                        existingIdentity,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if ((incoming.transform.position -
                     existing.transform.position).sqrMagnitude > 2.25f)
                    continue;

                overlapsExisting = true;
                break;
            }

            bool overlapsSnapshot = false;
            if (!overlapsExisting && pendingHandoffs != null)
            {
                for (int j = 0; j < pendingHandoffs.Count; j++)
                {
                    PendingScenePersonHandoff snapshot =
                        pendingHandoffs[j];
                    if (snapshot == null ||
                        snapshot.sceneHandle != incoming.gameObject.scene.handle ||
                        !string.Equals(
                            snapshot.identity,
                            incomingIdentity,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    overlapsSnapshot = true;
                    break;
                }
            }

            if (overlapsExisting || overlapsSnapshot)
            {
                incoming.gameObject.SetActive(false);
                _suppressedScenePersonHandoffBehaviours.Add(incoming);
                hidden++;
                if (overlapsSnapshot)
                    hiddenFromSnapshot++;
            }
        }

        if (pendingHandoffs != null)
            _pendingScenePersonHandoffsByChild.Remove(importedTemplate);

        if (hidden > 0)
        {
            Debug.Log(
                $"[QuestNetSync][SceneResourceHandoff] Suppressed {hidden} " +
                $"overlapping imported person object(s) uid={importedQuest.UID} " +
                $"snapshots={hiddenFromSnapshot}");
        }

        return hidden;
    }

    private static bool IsScenePersonHandoffBehaviourSuppressed(
        QuestResourceBehaviour behaviour)
    {
        if (_suppressedScenePersonHandoffBehaviours.Count == 0)
            return false;

        _suppressedScenePersonHandoffBehaviours.RemoveWhere(
            candidate => candidate == null);

        return behaviour != null &&
            _suppressedScenePersonHandoffBehaviours.Contains(behaviour);
    }

    private void ApplyGetItemReplicationFromStartPacket(Quest q, StartPacket pkt)
    {
        if (q == null) return;

        // Track taker
        if (pkt.takerNetId != 0)
            _questTakerByUid[q.UID] = pkt.takerNetId;

        bool localPlayerIsStartSource = (_localNetId != 0U && pkt.takerNetId == _localNetId);

        // Ensure inventory matches granted quest symbols only for non-source players receiving
        // this quest for the first time. The StartPacket source already received these items
        // locally from vanilla GetItem, and re-share StartPackets must not add them again.
        if (!localPlayerIsStartSource && pkt.grantedSymbols != null && pkt.grantedSymbols.Length > 0)
        {
            EnsureGrantedQuestItemsInInventory(q, pkt.grantedSymbols);
        }

        // Replay the initial "saying" popup on non-source players once.
        if (!localPlayerIsStartSource && pkt.grantedPopupIds != null && pkt.grantedPopupIds.Length > 0)
        {
            if (!_shownGetItemPopup.Contains(q.UID))
            {
                for (int i = 0; i < pkt.grantedPopupIds.Length; i++)
                {
                    int id = pkt.grantedPopupIds[i];
                    if (id != 0)
                    {
                        try { q.ShowMessagePopup(id); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[QuestNetSync] Suppressed GetItem popup exception during StartPacket apply uid={q.UID} quest='{q.QuestName}' msg={id}: {ex.Message}");
                        }
                    }
                }
                _shownGetItemPopup.Add(q.UID);
                if (Debug.isDebugBuild) Debug.Log($"[QuestNetSync] Replayed initial GetItem popup(s) for non-source player. uid={q.UID} count={pkt.grantedPopupIds.Length}");
            }
        }
    }



    // ─────────────────────────────────────────────────────────────────────────────
    // Person click + escort-face replication
    // ─────────────────────────────────────────────────────────────────────────────
    private static QuestNetSync GetActualLocalQuestSync()
    {
        try
        {
            if (NetworkClient.active &&
                NetworkClient.localPlayer != null)
            {
                QuestNetSync sync =
                    NetworkClient.localPlayer.GetComponent<QuestNetSync>();
                if (sync == null)
                {
                    sync =
                        NetworkClient.localPlayer
                            .GetComponentInChildren<QuestNetSync>(true);
                }

                if (sync != null &&
                    sync.isClient &&
                    sync.isLocalPlayer)
                    return sync;
            }
        }
        catch { }

        QuestNetSync fallback = LocalInstance;
        return fallback != null &&
               fallback.isClient &&
               fallback.isLocalPlayer
            ? fallback
            : null;
    }

    private static uint GetActualLocalQuestNetId()
    {
        QuestNetSync sync = GetActualLocalQuestSync();
        return sync != null ? sync.netId : _localNetId;
    }

    private static QuestNetSync GetServerQuestSync(
        NetworkConnection connection)
    {
        if (connection == null ||
            connection.identity == null)
            return null;

        QuestNetSync sync =
            connection.identity.GetComponent<QuestNetSync>();
        if (sync == null)
        {
            sync =
                connection.identity
                    .GetComponentInChildren<QuestNetSync>(true);
        }

        return sync != null && sync.isServer
            ? sync
            : null;
    }

    private static string MakePersonClickTaskChainCacheKey(
        Quest q,
        string personSymbol,
        string triggerTaskSymbol)
    {
        return (q != null ? q.UID.ToString() : "0") +
            "|person-click-chain|" +
            (personSymbol ?? string.Empty) + "|" +
            (triggerTaskSymbol ?? string.Empty);
    }

    private static string NormalizePersonClickTaskSymbol(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // QBN task references are commonly wrapped in underscores. Runtime Symbol
        // instances and serialized/debug text do not always retain those wrappers in
        // the same form, so compare a wrapper-neutral value without changing dots or
        // other meaningful characters inside the task name.
        return value.Trim().Trim('_');
    }

    private static bool PersonClickTaskSymbolSetContains(
        HashSet<string> taskSymbols,
        string candidate)
    {
        if (taskSymbols == null ||
            taskSymbols.Count == 0 ||
            string.IsNullOrEmpty(candidate))
            return false;

        if (taskSymbols.Contains(candidate))
            return true;

        string normalizedCandidate =
            NormalizePersonClickTaskSymbol(candidate);
        if (string.IsNullOrEmpty(normalizedCandidate))
            return false;

        foreach (string taskSymbol in taskSymbols)
        {
            if (string.Equals(
                    NormalizePersonClickTaskSymbol(taskSymbol),
                    normalizedCandidate,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool PersonClickTextReferencesAnyTaskSymbol(
        string text,
        HashSet<string> taskSymbols)
    {
        if (string.IsNullOrEmpty(text) ||
            taskSymbols == null ||
            taskSymbols.Count == 0)
            return false;

        // Serialized When data can contain either a complete expression or just one
        // task name. Tokenize both forms and use the same wrapper-neutral comparison.
        if (PersonClickTaskSymbolSetContains(taskSymbols, text))
            return true;

        System.Text.RegularExpressions.MatchCollection tokens =
            System.Text.RegularExpressions.Regex.Matches(
                text,
                @"[A-Za-z][A-Za-z0-9_.]*");

        for (int i = 0; i < tokens.Count; i++)
        {
            if (PersonClickTaskSymbolSetContains(
                    taskSymbols,
                    tokens[i].Value))
                return true;
        }

        return false;
    }

    private static bool ObjectGraphReferencesAnyTaskSymbolForPersonClick(
        object value,
        HashSet<string> taskSymbols,
        int depth)
    {
        return ObjectGraphReferencesAnyTaskSymbolForPersonClick(
            value,
            taskSymbols,
            depth,
            new List<object>());
    }

    private static bool ObjectGraphReferencesAnyTaskSymbolForPersonClick(
        object value,
        HashSet<string> taskSymbols,
        int depth,
        List<object> visitedReferences)
    {
        if (value == null ||
            taskSymbols == null ||
            taskSymbols.Count == 0 ||
            depth > 32)
            return false;

        Symbol symbol = value as Symbol;
        if (symbol != null)
        {
            return PersonClickTaskSymbolSetContains(
                taskSymbols,
                symbol.Name);
        }

        string text = value as string;
        if (text != null)
            return PersonClickTextReferencesAnyTaskSymbol(
                text,
                taskSymbols);

        Type type = value.GetType();
        if (type.IsPrimitive ||
            type.IsEnum ||
            type == typeof(decimal))
            return false;

        // Deep When expressions are often nested well beyond six levels. Track
        // reference objects while walking to permit that depth without looping on a
        // parent/back-reference in runtime or save-data condition graphs.
        if (!type.IsValueType)
        {
            if (visitedReferences == null)
                visitedReferences = new List<object>();

            for (int i = 0; i < visitedReferences.Count; i++)
            {
                if (object.ReferenceEquals(
                        visitedReferences[i],
                        value))
                    return false;
            }

            visitedReferences.Add(value);
        }

        System.Collections.IEnumerable enumerable =
            value as System.Collections.IEnumerable;
        if (enumerable != null)
        {
            try
            {
                foreach (object entry in enumerable)
                {
                    if (ObjectGraphReferencesAnyTaskSymbolForPersonClick(
                            entry,
                            taskSymbols,
                            depth + 1,
                            visitedReferences))
                        return true;
                }
            }
            catch { }

            return false;
        }

        try
        {
            FieldInfo[] fields =
                type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            for (int i = 0; i < fields.Length; i++)
            {
                Type fieldType = fields[i].FieldType;

                // Do not walk ownership graphs back into the whole Quest/Task tree.
                if (typeof(Quest).IsAssignableFrom(fieldType) ||
                    typeof(DaggerfallWorkshop.Game.Questing.Task)
                        .IsAssignableFrom(fieldType) ||
                    typeof(IQuestAction).IsAssignableFrom(fieldType))
                    continue;

                object child = fields[i].GetValue(value);
                if (ObjectGraphReferencesAnyTaskSymbolForPersonClick(
                        child,
                        taskSymbols,
                        depth + 1,
                        visitedReferences))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool PersonClickWhenReferencesAnyTask(
        IQuestAction action,
        HashSet<string> taskSymbols)
    {
        if (action == null ||
            taskSymbols == null ||
            taskSymbols.Count == 0)
            return false;

        // The runtime action class is WhenTask in current DFU. Older QNS code looked
        // only for "When", so ClickedNpc -> WhenTask -> Say chains were never
        // discovered and could fall into the generic first-Say fallback. Accept both
        // names for compatibility, but otherwise leave shared-click lifetime unchanged.
        string whenTypeName = action.GetType().Name;
        if (!string.Equals(whenTypeName, "WhenTask", StringComparison.Ordinal) &&
            !string.Equals(whenTypeName, "When", StringComparison.Ordinal))
            return false;

        // DebugSource exists only in quest debug mode. Prefer serialized runtime data.
        try
        {
            object saveData = action.GetSaveData();
            if (ObjectGraphReferencesAnyTaskSymbolForPersonClick(
                    saveData,
                    taskSymbols,
                    0))
                return true;
        }
        catch { }

        try
        {
            FieldInfo[] fields =
                action.GetType().GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

            for (int i = 0; i < fields.Length; i++)
            {
                if (ObjectGraphReferencesAnyTaskSymbolForPersonClick(
                        fields[i].GetValue(action),
                        taskSymbols,
                        0))
                    return true;
            }
        }
        catch { }

        // Debug-build fallback. Use the same comparison as serialized/runtime data;
        // the old fallback stripped underscores from only the expression tokens, so
        // `_clickpriest_` could never match its cached task Symbol.
        string source = action.DebugSource ?? string.Empty;
        if (PersonClickTextReferencesAnyTaskSymbol(
                source,
                taskSymbols))
            return true;

        return false;
    }

    private static string GetActionSymbolFieldName(
        IQuestAction action,
        string fieldName)
    {
        if (action == null || string.IsNullOrEmpty(fieldName))
            return string.Empty;

        try
        {
            FieldInfo field =
                action.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            return field != null
                ? GetSymbolName(field.GetValue(action))
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HashSet<string> GetPersonClickTaskChain(
        Quest q,
        string personSymbol,
        string triggerTaskSymbol)
    {
        HashSet<string> empty =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        if (q == null || string.IsNullOrEmpty(personSymbol))
            return empty;

        string cacheKey =
            MakePersonClickTaskChainCacheKey(
                q,
                personSymbol,
                triggerTaskSymbol);

        HashSet<string> cached;
        if (_personClickTaskChainCache.TryGetValue(
                cacheKey,
                out cached))
            return cached;

        cached =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            // Seed from the exact ClickedNpc task accepted by the source player. A
            // Person can be watched by several mutually-exclusive ClickedNpc tasks;
            // seeding all of them lets an unrelated branch consume this interaction.
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task =
                    tasks[i];
                if (task == null ||
                    task.Symbol == null ||
                    task.Actions == null ||
                    (!string.IsNullOrEmpty(triggerTaskSymbol) &&
                     !string.Equals(
                         task.Symbol.Name,
                         triggerTaskSymbol,
                         StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "ClickedNpc",
                            StringComparison.Ordinal))
                        continue;

                    string clickedPerson =
                        GetActionSymbolFieldName(
                            action,
                            "npcSymbol");

                    if (string.Equals(
                            clickedPerson,
                            personSymbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        cached.Add(task.Symbol.Name);
                        break;
                    }
                }
            }

            // Recursively include only When tasks depending on that exact click chain.
            bool added;
            do
            {
                added = false;

                for (int i = 0; i < tasks.Count; i++)
                {
                    DaggerfallWorkshop.Game.Questing.Task task =
                        tasks[i];
                    if (task == null ||
                        task.Symbol == null ||
                        task.Actions == null ||
                        cached.Contains(task.Symbol.Name))
                        continue;

                    foreach (IQuestAction action in task.Actions)
                    {
                        if (!PersonClickWhenReferencesAnyTask(
                                action,
                                cached))
                            continue;

                        cached.Add(task.Symbol.Name);
                        added = true;
                        break;
                    }
                }
            }
            while (added);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][PersonClickPause] Could not build task chain " +
                $"uid={q.UID} person='{personSymbol}': {ex.Message}");
        }

        _personClickTaskChainCache[cacheKey] = cached;
        return cached;
    }

    private static bool PersonClickChainHasSay(
        Quest q,
        string personSymbol,
        string triggerTaskSymbol)
    {
        if (q == null || string.IsNullOrEmpty(personSymbol))
            return false;

        HashSet<string> chain =
            GetPersonClickTaskChain(
                q,
                personSymbol,
                triggerTaskSymbol);
        if (chain.Count == 0)
            return false;

        try
        {
            foreach (string taskSymbol in chain)
            {
                DaggerfallWorkshop.Game.Questing.Task task =
                    q.GetTask(new Symbol(taskSymbol));
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action != null &&
                        string.Equals(
                            action.GetType().Name,
                            "Say",
                            StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void RegisterSharedPersonClickInteraction(
        ulong questUID,
        string personSymbol,
        string triggerTaskSymbol,
        int directMessageId,
        string interactionId,
        uint sourceNetId,
        bool localIsOwner,
        bool allowSingleUndiscoveredSay,
        NetworkConnection serverSourceConnection)
    {
        if (questUID == 0UL ||
            string.IsNullOrEmpty(personSymbol) ||
            string.IsNullOrEmpty(triggerTaskSymbol) ||
            string.IsNullOrEmpty(interactionId))
            return;

        SharedPersonClickInteraction existing;
        if (_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out existing) &&
            existing != null &&
            string.Equals(
                existing.interactionId,
                interactionId,
                StringComparison.Ordinal))
        {
            existing.localIsOwner |= localIsOwner;
            existing.allowSingleUndiscoveredSay |=
                allowSingleUndiscoveredSay;
            if (serverSourceConnection != null)
                existing.serverSourceConnection =
                    serverSourceConnection;

            if (string.IsNullOrEmpty(existing.triggerTaskSymbol))
                existing.triggerTaskSymbol = triggerTaskSymbol;

            if (directMessageId != 0)
            {
                AddSharedPersonClickPopupStage(
                    existing,
                    1,
                    true,
                    triggerTaskSymbol,
                    directMessageId);
            }
            return;
        }

        // A normal modal interaction prevents another physical NPC click. Replacing a
        // stale entry here is safer than allowing an old popup to own the next branch.
        SharedPersonClickInteraction context =
            new SharedPersonClickInteraction
            {
                interactionId = interactionId,
                questUID = questUID,
                personSymbol = personSymbol,
                triggerTaskSymbol = triggerTaskSymbol,
                sourceNetId = sourceNetId,
                localIsOwner = localIsOwner,
                localTriggerClaimed = false,
                allowSingleUndiscoveredSay =
                    allowSingleUndiscoveredSay,
                nextStageSequence = 0,
                serverSourceConnection =
                    serverSourceConnection,
            };

        if (directMessageId != 0)
        {
            AddSharedPersonClickPopupStage(
                context,
                1,
                true,
                triggerTaskSymbol,
                directMessageId);
        }

        _sharedPersonClickInteractions[questUID] =
            context;
    }

    private static SharedPersonClickPopupStage AddSharedPersonClickPopupStage(
        SharedPersonClickInteraction context,
        int sequence,
        bool isDirectClickedNpcPopup,
        string taskSymbol,
        int messageId)
    {
        if (context == null ||
            sequence <= 0 ||
            string.IsNullOrEmpty(taskSymbol) ||
            messageId == 0)
            return null;

        for (int i = 0; i < context.stages.Count; i++)
        {
            SharedPersonClickPopupStage existing =
                context.stages[i];
            if (existing.sequence != sequence)
                continue;

            if (existing.isDirectClickedNpcPopup != isDirectClickedNpcPopup ||
                !string.Equals(
                    existing.taskSymbol,
                    taskSymbol,
                    StringComparison.OrdinalIgnoreCase) ||
                existing.messageId != messageId)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][PersonClickPause] Rejected conflicting popup stage " +
                    $"uid={context.questUID} interaction='{context.interactionId}' " +
                    $"stage={sequence}");
                return null;
            }

            return existing;
        }

        SharedPersonClickPopupStage stage =
            new SharedPersonClickPopupStage
            {
                sequence = sequence,
                isDirectClickedNpcPopup = isDirectClickedNpcPopup,
                taskSymbol = taskSymbol,
                messageId = messageId,
                released = false,
                popupShown = false,
                localConsumed = false,
                popupWindow = null,
            };

        context.stages.Add(stage);
        context.nextStageSequence =
            Math.Max(context.nextStageSequence, sequence);
        return stage;
    }

    private static SharedPersonClickPopupStage FindSharedPersonClickPopupStage(
        SharedPersonClickInteraction context,
        int sequence)
    {
        if (context == null || sequence <= 0)
            return null;

        for (int i = 0; i < context.stages.Count; i++)
        {
            if (context.stages[i].sequence == sequence)
                return context.stages[i];
        }

        return null;
    }

    private static SharedPersonClickPopupStage FindPendingSharedPersonClickPopupStage(
        SharedPersonClickInteraction context,
        bool isDirectClickedNpcPopup,
        string taskSymbol,
        int messageId)
    {
        SharedPersonClickPopupStage best = null;
        if (context == null)
            return null;

        for (int i = 0; i < context.stages.Count; i++)
        {
            SharedPersonClickPopupStage candidate =
                context.stages[i];
            if (candidate.localConsumed ||
                candidate.isDirectClickedNpcPopup != isDirectClickedNpcPopup ||
                candidate.messageId != messageId ||
                !string.Equals(
                    candidate.taskSymbol,
                    taskSymbol,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (best == null || candidate.sequence < best.sequence)
                best = candidate;
        }

        return best;
    }

    private static SharedPersonClickPopupStage FindEarliestPendingSharedPersonClickPopupStage(
        SharedPersonClickInteraction context)
    {
        SharedPersonClickPopupStage best = null;
        if (context == null)
            return null;

        for (int i = 0; i < context.stages.Count; i++)
        {
            SharedPersonClickPopupStage candidate =
                context.stages[i];
            if (candidate.localConsumed)
                continue;

            if (best == null || candidate.sequence < best.sequence)
                best = candidate;
        }

        return best;
    }

    private static void CloseSharedPersonClickPopup(
        SharedPersonClickPopupStage stage)
    {
        if (stage == null || stage.popupWindow == null)
            return;

        DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMessageBox box =
            stage.popupWindow;
        stage.popupWindow = null;

        try
        {
            box.CloseWindow();
        }
        catch { }
    }

    private static void MarkSharedPersonClickReleased(
        ulong questUID,
        string interactionId,
        int stageSequence)
    {
        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !string.Equals(
                context.interactionId,
                interactionId,
                StringComparison.Ordinal))
            return;

        SharedPersonClickPopupStage stage =
            FindSharedPersonClickPopupStage(
                context,
                stageSequence);
        if (stage == null)
            return;

        stage.released = true;

        // The source already closed its own copy. Close informational copies so no
        // observer has to dismiss a stale window after the shared branch resumes.
        if (!context.localIsOwner)
            CloseSharedPersonClickPopup(stage);
    }

    private static void LocalSharedPersonClickPopupClosed(
        ulong questUID,
        string interactionId,
        int stageSequence)
    {
        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !context.localIsOwner ||
            !string.Equals(
                context.interactionId,
                interactionId,
                StringComparison.Ordinal))
            return;

        SharedPersonClickPopupStage stage =
            FindSharedPersonClickPopupStage(
                context,
                stageSequence);
        if (stage == null || stage.localConsumed)
            return;

        stage.popupWindow = null;

        QuestNetSync local =
            GetActualLocalQuestSync();
        if (local == null)
            return;

        if (local.isServer && NetworkServer.active)
        {
            local.ServerReleaseSharedPersonClick(
                questUID,
                interactionId,
                stageSequence,
                NetworkServer.localConnection,
                true);
        }
        else
        {
            local.CmdReleaseSharedPersonClick(
                questUID,
                interactionId,
                stageSequence);
        }
    }

    [Command]
    private void CmdReleaseSharedPersonClick(
        ulong questUID,
        string interactionId,
        int stageSequence)
    {
        if (!isServer ||
            questUID == 0UL ||
            string.IsNullOrEmpty(interactionId) ||
            stageSequence <= 0)
            return;

        ServerReleaseSharedPersonClick(
            questUID,
            interactionId,
            stageSequence,
            connectionToClient,
            false);
    }

    private static bool IsServerSharedPersonClickOwner(
        SharedPersonClickInteraction context,
        NetworkConnection connection,
        bool fromServerLocalPlayer)
    {
        if (context == null)
            return false;

        if (fromServerLocalPlayer)
            return context.localIsOwner;

        return context.serverSourceConnection != null &&
            connection == context.serverSourceConnection;
    }

    private void ServerReleaseSharedPersonClick(
        ulong questUID,
        string interactionId,
        int stageSequence,
        NetworkConnection releasingConnection,
        bool fromServerLocalPlayer)
    {
        if (!isServer)
            return;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !string.Equals(
                context.interactionId,
                interactionId,
                StringComparison.Ordinal))
            return;

        if (!IsServerSharedPersonClickOwner(
                context,
                releasingConnection,
                fromServerLocalPlayer))
        {
            Debug.LogWarning(
                $"[QuestNetSync][PersonClickPause] Ignored non-owner close " +
                $"uid={questUID} interaction='{interactionId}' stage={stageSequence}");
            return;
        }

        SharedPersonClickPopupStage stage =
            FindSharedPersonClickPopupStage(
                context,
                stageSequence);
        if (stage == null)
            return;

        MarkSharedPersonClickReleased(
            questUID,
            interactionId,
            stageSequence);

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection conn = entry.Value;
            if (conn == null ||
                conn == NetworkServer.localConnection)
                continue;

            QuestNetSync sync =
                GetServerQuestSync(conn);
            if (sync == null || !sync.isServer)
                continue;

            sync.TargetReleaseSharedPersonClick(
                conn,
                questUID,
                interactionId,
                stageSequence);
        }

        Debug.Log(
            $"[QuestNetSync][PersonClickPause] Owner released shared Say " +
            $"uid={questUID} interaction='{interactionId}' stage={stageSequence}");
    }

    [TargetRpc]
    private void TargetReleaseSharedPersonClick(
        NetworkConnection target,
        ulong questUID,
        string interactionId,
        int stageSequence)
    {
        if (!isClient)
            return;

        MarkSharedPersonClickReleased(
            questUID,
            interactionId,
            stageSequence);
    }

    private static void LocalAnnounceSharedPersonClickSayStage(
        SharedPersonClickInteraction context,
        SharedPersonClickPopupStage stage)
    {
        if (context == null || stage == null || !context.localIsOwner)
            return;

        QuestNetSync local =
            GetActualLocalQuestSync();
        if (local == null)
            return;

        if (local.isServer && NetworkServer.active)
        {
            local.ServerAnnounceSharedPersonClickSayStage(
                context.questUID,
                context.interactionId,
                stage.sequence,
                stage.taskSymbol,
                stage.messageId,
                NetworkServer.localConnection,
                true);
        }
        else
        {
            local.CmdAnnounceSharedPersonClickSayStage(
                context.questUID,
                context.interactionId,
                stage.sequence,
                stage.taskSymbol,
                stage.messageId);
        }
    }

    [Command]
    private void CmdAnnounceSharedPersonClickSayStage(
        ulong questUID,
        string interactionId,
        int stageSequence,
        string taskSymbol,
        int messageId)
    {
        if (!isServer)
            return;

        ServerAnnounceSharedPersonClickSayStage(
            questUID,
            interactionId,
            stageSequence,
            taskSymbol,
            messageId,
            connectionToClient,
            false);
    }

    private void ServerAnnounceSharedPersonClickSayStage(
        ulong questUID,
        string interactionId,
        int stageSequence,
        string taskSymbol,
        int messageId,
        NetworkConnection announcingConnection,
        bool fromServerLocalPlayer)
    {
        if (!isServer ||
            questUID == 0UL ||
            string.IsNullOrEmpty(interactionId) ||
            stageSequence <= 0 ||
            string.IsNullOrEmpty(taskSymbol) ||
            messageId == 0)
            return;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !string.Equals(
                context.interactionId,
                interactionId,
                StringComparison.Ordinal) ||
            !IsServerSharedPersonClickOwner(
                context,
                announcingConnection,
                fromServerLocalPlayer))
            return;

        SharedPersonClickPopupStage stage =
            AddSharedPersonClickPopupStage(
                context,
                stageSequence,
                false,
                taskSymbol,
                messageId);
        if (stage == null)
            return;

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection conn = entry.Value;
            if (conn == null ||
                conn == announcingConnection ||
                conn == NetworkServer.localConnection)
                continue;

            QuestNetSync sync =
                GetServerQuestSync(conn);
            if (sync == null || !sync.isServer)
                continue;

            sync.TargetAnnounceSharedPersonClickSayStage(
                conn,
                questUID,
                interactionId,
                stageSequence,
                taskSymbol,
                messageId);
        }
    }

    [TargetRpc]
    private void TargetAnnounceSharedPersonClickSayStage(
        NetworkConnection target,
        ulong questUID,
        string interactionId,
        int stageSequence,
        string taskSymbol,
        int messageId)
    {
        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !string.Equals(
                context.interactionId,
                interactionId,
                StringComparison.Ordinal))
            return;

        AddSharedPersonClickPopupStage(
            context,
            stageSequence,
            false,
            taskSymbol,
            messageId);
    }

    private static bool HandleSharedPersonClickPopupStage(
        Quest q,
        SharedPersonClickInteraction context,
        SharedPersonClickPopupStage stage,
        out bool completeNow)
    {
        completeNow = false;
        if (q == null || context == null || stage == null)
            return false;

        if (!stage.popupShown)
        {
            stage.popupShown = true;
            DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMessageBox box =
                q.ShowMessagePopup(
                    stage.messageId,
                    true);
            stage.popupWindow = box;

            if (context.localIsOwner)
            {
                string capturedInteractionId =
                    context.interactionId;
                ulong capturedQuestUid =
                    context.questUID;
                int capturedStageSequence =
                    stage.sequence;

                if (box != null)
                {
                    box.OnClose += delegate
                    {
                        LocalSharedPersonClickPopupClosed(
                            capturedQuestUid,
                            capturedInteractionId,
                            capturedStageSequence);
                    };
                }
                else
                {
                    LocalSharedPersonClickPopupClosed(
                        capturedQuestUid,
                        capturedInteractionId,
                        capturedStageSequence);
                }
            }

            Debug.Log(
                $"[QuestNetSync][PersonClickPause] Waiting at popup " +
                $"uid={q.UID} person='{context.personSymbol}' " +
                $"task='{stage.taskSymbol}' msg={stage.messageId} " +
                $"stage={stage.sequence} owner={context.localIsOwner}");
            return true;
        }

        if (!stage.released)
        {
            q.QuestBreak = true;
            return true;
        }

        CloseSharedPersonClickPopup(stage);
        stage.localConsumed = true;
        completeNow = true;

        // Serialize popup stages. This is a quest tick barrier, not a timed delay;
        // every participant resumes this exact action on its next local quest tick.
        q.QuestBreak = true;
        return true;
    }

    public static bool TryHandleSharedPersonClickTriggerPopup(
        Quest q,
        DaggerfallWorkshop.Game.Questing.Task caller,
        string personSymbol,
        int messageId,
        out bool triggerNow)
    {
        triggerNow = false;
        if (q == null ||
            caller == null ||
            caller.Symbol == null ||
            string.IsNullOrEmpty(personSymbol) ||
            messageId == 0)
            return false;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                q.UID,
                out context) ||
            context == null ||
            !string.Equals(
                context.personSymbol,
                personSymbol,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(
                context.triggerTaskSymbol,
                caller.Symbol.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            // This Person can have several ClickedNpc listeners. The source task is
            // authoritative for this physical click; a different listener must simply
            // decline it so the exact task can claim the still-set Person click.
            triggerNow = false;
            return true;
        }

        // This ClickedNpc is consuming a network-applied click through the shared
        // barrier, so its old one-shot echo guard must not survive into the next click.
        _remotePersonClicksApplied.Remove(
            MakePersonClickApplyKey(
                q.UID,
                personSymbol));

        SharedPersonClickPopupStage stage =
            FindPendingSharedPersonClickPopupStage(
                context,
                true,
                caller.Symbol.Name,
                messageId);
        if (stage == null)
            return false;

        return HandleSharedPersonClickPopupStage(
            q,
            context,
            stage,
            out triggerNow);
    }

    public static bool TryClaimSharedPersonClickTrigger(
        Quest q,
        DaggerfallWorkshop.Game.Questing.Task caller,
        string personSymbol,
        out bool ownsThisClick)
    {
        ownsThisClick = false;
        if (q == null ||
            caller == null ||
            caller.Symbol == null ||
            string.IsNullOrEmpty(personSymbol))
            return false;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                q.UID,
                out context) ||
            context == null ||
            !string.Equals(
                context.personSymbol,
                personSymbol,
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Retire a fully-consumed interaction once its exact ClickedNpc task has been
        // cleared. A later physical click on the same Person can then create a fresh ID.
        bool hasPendingStage =
            FindEarliestPendingSharedPersonClickPopupStage(context) != null;
        DaggerfallWorkshop.Game.Questing.Task triggerTask =
            q.GetTask(new Symbol(context.triggerTaskSymbol));
        if (context.localTriggerClaimed &&
            !hasPendingStage &&
            (triggerTask == null || !triggerTask.IsTriggered))
        {
            _sharedPersonClickInteractions.Remove(q.UID);
            return false;
        }

        ownsThisClick =
            string.Equals(
                context.triggerTaskSymbol,
                caller.Symbol.Name,
                StringComparison.OrdinalIgnoreCase);

        if (ownsThisClick)
        {
            context.localTriggerClaimed = true;
            _remotePersonClicksApplied.Remove(
                MakePersonClickApplyKey(
                    q.UID,
                    personSymbol));
        }

        return true;
    }

    // A generic replicated ClickedNpc event is reserved for the exact ClickedNpc task
    // that accepted the source player's physical click. A TotingItemAndClickedNpc task
    // for the same Person must not reinterpret that network-applied click after an item
    // grant arrives on this machine. Doing so turns one physical click into a synthetic
    // second click and can echo a false toting event back to the server.
    private static bool IsRemotePersonClickReservedForDifferentTask(
        Quest q,
        string personSymbol,
        string candidateTaskSymbol)
    {
        if (q == null ||
            string.IsNullOrEmpty(personSymbol) ||
            string.IsNullOrEmpty(candidateTaskSymbol))
            return false;

        string appliedKey =
            MakePersonClickApplyKey(
                q.UID,
                personSymbol);

        // This flag exists only while a Person.SetPlayerClicked() came from
        // ApplyRemotePersonClick(). Local physical clicks never set it.
        if (!_remotePersonClicksApplied.Contains(appliedKey))
            return false;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                q.UID,
                out context) ||
            context == null ||
            !string.Equals(
                context.personSymbol,
                personSymbol,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(context.triggerTaskSymbol))
            return false;

        return !string.Equals(
            context.triggerTaskSymbol,
            candidateTaskSymbol,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldSuppressTotingItemFromRemotePersonClick(
        Quest q,
        DaggerfallWorkshop.Game.Questing.Task caller,
        string personSymbol)
    {
        if (q == null ||
            caller == null ||
            caller.Symbol == null ||
            string.IsNullOrEmpty(personSymbol))
            return false;

        bool suppress =
            IsRemotePersonClickReservedForDifferentTask(
                q,
                personSymbol,
                caller.Symbol.Name);

        if (suppress && Debug.isDebugBuild)
        {
            SharedPersonClickInteraction context;
            _sharedPersonClickInteractions.TryGetValue(
                q.UID,
                out context);

            Debug.Log(
                $"[QuestNetSync][PersonClickOwnership] Suppressed toting trigger from " +
                $"replicated Person click uid={q.UID} person='{personSymbol}' " +
                $"candidateTask='{caller.Symbol.Name}' reservedTask='" +
                $"{(context != null ? context.triggerTaskSymbol : string.Empty)}'");
        }

        return suppress;
    }

    public static bool TryHandleSharedPersonClickSay(
        Quest q,
        DaggerfallWorkshop.Game.Questing.Task caller,
        int messageId,
        out bool completeNow)
    {
        completeNow = false;

        if (q == null ||
            caller == null ||
            caller.Symbol == null ||
            messageId == 0)
            return false;

        // A TotingItemAndClickedNpc task is an independent item hand-in, not a Say
        // branch owned by the generic ClickedNpc task for the same Person. It already
        // arrives through the exact toting-event RPC. Let its local Say execute and
        // complete normally; otherwise a late non-owner participant waits forever for
        // a popup release belonging to another player's generic NPC interaction.
        if (TaskHasActionType(
                q,
                caller.Symbol.Name,
                "TotingItemAndClickedNpc"))
        {
            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][PersonClickPause] Bypassed generic click owner for toting Say " +
                    $"uid={q.UID} task='{caller.Symbol.Name}' msg={messageId}");
            }

            return false;
        }

        // This Say belongs to an explicitly replicated prompt branch. Do not let a
        // parallel ClickedNpc condition claim it as another popup interaction.
        if (_promptChoiceSayBypassTasks.Contains(
                MakePromptChoiceSayBypassKey(
                    q.UID,
                    caller.Symbol.Name)))
        {
            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][PromptChoice] Bypassed generic click owner " +
                    $"uid={q.UID} task='{caller.Symbol.Name}' msg={messageId}");
            }

            return false;
        }

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(
                q.UID,
                out context) ||
            context == null)
            return false;

        HashSet<string> chain =
            GetPersonClickTaskChain(
                q,
                context.personSymbol,
                context.triggerTaskSymbol);
        bool belongsToDiscoveredChain =
            chain.Contains(caller.Symbol.Name);
        if (!belongsToDiscoveredChain &&
            !context.allowSingleUndiscoveredSay)
            return false;

        SharedPersonClickPopupStage stage =
            FindPendingSharedPersonClickPopupStage(
                context,
                false,
                caller.Symbol.Name,
                messageId);

        if (stage == null)
        {
            SharedPersonClickPopupStage pending =
                FindEarliestPendingSharedPersonClickPopupStage(
                    context);
            if (pending != null)
            {
                // An earlier exact popup stage has not been consumed locally yet.
                q.QuestBreak = true;
                return true;
            }

            if (!context.localIsOwner)
            {
                // Observers never choose which Say belongs to this interaction. Wait
                // for the physical-click owner to announce the exact task/message.
                q.QuestBreak = true;
                return true;
            }

            stage =
                AddSharedPersonClickPopupStage(
                    context,
                    context.nextStageSequence + 1,
                    false,
                    caller.Symbol.Name,
                    messageId);
            if (stage == null)
                return false;

            LocalAnnounceSharedPersonClickSayStage(
                context,
                stage);
        }

        SharedPersonClickPopupStage earliestPending =
            FindEarliestPendingSharedPersonClickPopupStage(
                context);
        if (earliestPending != null && earliestPending != stage)
        {
            q.QuestBreak = true;
            return true;
        }

        bool handled = HandleSharedPersonClickPopupStage(
            q,
            context,
            stage,
            out completeNow);

        if (handled &&
            completeNow &&
            !belongsToDiscoveredChain &&
            context.allowSingleUndiscoveredSay)
        {
            // The dynamic fallback is deliberately single-use. It exists only to
            // bridge an unreflectable ClickedNpc -> When -> Say edge, then retires so
            // a later unrelated quest popup can never attach to this interaction.
            context.allowSingleUndiscoveredSay = false;
            _sharedPersonClickInteractions.Remove(q.UID);
        }

        return handled;
    }

    private static string MakePersonClickMessageKey(ulong questUID, string personSymbol, int messageId)
    {
        return questUID.ToString() + "|person-click|" + (personSymbol ?? string.Empty) + "|" + messageId.ToString();
    }

    private static string MakePersonClickApplyKey(ulong questUID, string personSymbol)
    {
        return questUID.ToString() + "|person-click-applied|" + (personSymbol ?? string.Empty);
    }

    private static string MakeEscortFaceKey(ulong questUID, string personSymbol, string foeSymbol)
    {
        string p = personSymbol ?? string.Empty;
        string f = foeSymbol ?? string.Empty;
        return questUID.ToString() + "|escort-face|" + p + "|" + f;
    }

    public static bool ConsumeRemotePersonClickMessage(ulong questUID, string personSymbol, int messageId)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(personSymbol) || messageId == 0)
            return false;

        string key = MakePersonClickMessageKey(questUID, personSymbol, messageId);
        if (!_remotePersonClickMessagesShown.Contains(key))
            return false;

        // Only suppress when ReportLocalPersonClicked() just confirmed this local
        // action is echoing a freshly-applied remote SetPlayerClicked(). A stale
        // message key can survive save/load in the same process, especially after
        // remote toting clicks that directly start the task and never run the local
        // ClickedNpc/Toting duplicate path. In that case clear the stale key but
        // allow the real local popup to show.
        if (!_remotePersonClickMessageConsumeAllowed.Remove(key))
        {
            _remotePersonClickMessagesShown.Remove(key);
            return false;
        }

        // One-shot suppression. The remote replay already showed this message; if
        // ClickedNpc.Update() later runs locally from SetPlayerClicked(), do not show it again.
        _remotePersonClickMessagesShown.Remove(key);
        return true;
    }

    public static bool WasEscortFaceAppliedFromNetwork(ulong questUID, string personSymbol, string foeSymbol)
    {
        if (questUID == 0UL)
            return false;

        string key = MakeEscortFaceKey(questUID, personSymbol, foeSymbol);
        return _remoteEscortFacesApplied.Contains(key);
    }

    public static void ReportLocalPersonClicked(
        ulong questUID,
        string personSymbol,
        int messageId = 0,
        string triggerTaskSymbol = null)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (_suppressPersonClickReportDepth > 0)
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(personSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        // If this local ClickedNpc was caused by a remote SetPlayerClicked() replay,
        // do not echo the click back to the network.
        string appliedKey =
            MakePersonClickApplyKey(
                questUID,
                personSymbol);
        if (_remotePersonClicksApplied.Remove(appliedKey))
        {
            if (messageId != 0)
            {
                _remotePersonClickMessageConsumeAllowed.Add(
                    MakePersonClickMessageKey(
                        questUID,
                        personSymbol,
                        messageId));
            }
            return;
        }

        QuestNetSync inst =
            GetActualLocalQuestSync();
        if (inst == null)
            return;

        uint sourceNetId =
            inst.netId;
        string interactionId =
            string.Empty;

        Quest q =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUID)
                : null;
        bool discoveredSharedPopup =
            messageId != 0 ||
            PersonClickChainHasSay(
                q,
                personSymbol,
                triggerTaskSymbol);
        bool allowSingleUndiscoveredSay =
            messageId == 0 &&
            !discoveredSharedPopup;
        if (!string.IsNullOrEmpty(triggerTaskSymbol))
        {
            interactionId =
                Guid.NewGuid().ToString("N");

            RegisterSharedPersonClickInteraction(
                questUID,
                personSymbol,
                triggerTaskSymbol,
                messageId,
                interactionId,
                sourceNetId,
                true,
                allowSingleUndiscoveredSay,
                inst.isServer
                    ? NetworkServer.localConnection
                    : null);

            Debug.Log(
                $"[QuestNetSync][PersonClickPause] Registered source click " +
                $"uid={questUID} person='{personSymbol}' task='{triggerTaskSymbol}' " +
                $"directMsg={messageId} discovered={discoveredSharedPopup} " +
                $"dynamicFallback={allowSingleUndiscoveredSay}");
        }

        inst.CmdPersonClicked(
            questUID,
            personSymbol,
            messageId,
            triggerTaskSymbol ?? string.Empty,
            sourceNetId,
            interactionId,
            allowSingleUndiscoveredSay);
    }

    [Command]
    private void CmdPersonClicked(
        ulong questUID,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId,
        string interactionId,
        bool allowSingleUndiscoveredSay)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer ||
            questUID == 0UL ||
            string.IsNullOrEmpty(personSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        NetworkConnection sourceConnection =
            connectionToClient;
        NetworkConnection hostConnection =
            NetworkServer.localConnection;
        bool sourceIsHost =
            sourceConnection != null &&
            hostConnection != null &&
            sourceConnection == hostConnection;

        if (!string.IsNullOrEmpty(interactionId))
        {
            RegisterSharedPersonClickInteraction(
                questUID,
                personSymbol,
                triggerTaskSymbol,
                messageId,
                interactionId,
                sourceNetId,
                sourceIsHost,
                allowSingleUndiscoveredSay,
                sourceConnection);
        }

        // Source already executed the click locally. Every other participant,
        // including the host for a pure-client source, executes the same local branch.
        if (!sourceIsHost)
        {
            ApplyRemotePersonClick(
                questUID,
                personSymbol,
                messageId,
                sourceNetId);
        }

        ServerBroadcastPersonClicked(
            sourceConnection,
            hostConnection,
            questUID,
            personSymbol,
            messageId,
            triggerTaskSymbol,
            sourceNetId,
            interactionId,
            allowSingleUndiscoveredSay);

        // Nested StartQuest recovery is no longer guessed from every incomplete
        // StartQuest in this quest. Task.Update() reports the exact task/target reached
        // by a pure client through CmdClientReachedNestedQuestStart().
    }

    private static void ServerBroadcastPersonClicked(
        NetworkConnection sourceConnection,
        NetworkConnection hostConnection,
        ulong questUID,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId,
        string interactionId,
        bool allowSingleUndiscoveredSay)
    {
        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection conn =
                entry.Value;
            if (conn == null ||
                conn == sourceConnection ||
                conn == hostConnection)
                continue;

            QuestNetSync sync =
                GetServerQuestSync(conn);
            if (sync == null || !sync.isServer)
                continue;

            sync.TargetPersonClicked(
                conn,
                questUID,
                personSymbol,
                messageId,
                triggerTaskSymbol,
                sourceNetId,
                interactionId ?? string.Empty,
                allowSingleUndiscoveredSay);
        }
    }

    [TargetRpc]
    private void TargetPersonClicked(
        NetworkConnection target,
        ulong questUID,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId,
        string interactionId,
        bool allowSingleUndiscoveredSay)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient ||
            questUID == 0UL ||
            string.IsNullOrEmpty(personSymbol))
            return;

        if (!string.IsNullOrEmpty(interactionId))
        {
            RegisterSharedPersonClickInteraction(
                questUID,
                personSymbol,
                triggerTaskSymbol,
                messageId,
                interactionId,
                sourceNetId,
                false,
                allowSingleUndiscoveredSay,
                null);
        }

        ApplyRemotePersonClick(
            questUID,
            personSymbol,
            messageId,
            sourceNetId);
    }


    private static void ApplyRemotePersonClick(ulong questUID, string personSymbol, int messageId, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        Person p = q.GetPerson(new Symbol(personSymbol));
        if (p == null)
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(questUID, "remote-person-click");

        string appliedKey = MakePersonClickApplyKey(questUID, personSymbol);
        if (_remotePersonClicksApplied.Contains(appliedKey))
            return;

        _remotePersonClicksApplied.Add(appliedKey);

        _suppressPersonClickReportDepth++;
        try
        {
            p.SetPlayerClicked();

            // Direct ClickedNpc dialogue is displayed by its exact shared popup stage.
            // The source task cannot complete until the owner closes that popup, so a
            // completed-task delta cannot overtake and suppress this local branch.

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Applied remote person click uid={questUID} person='{personSymbol}' msg={messageId} source={sourceNetId}");
        }
        finally
        {
            _suppressPersonClickReportDepth--;
        }
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // Prompt choice replication
    // ─────────────────────────────────────────────────────────────────────────────
    private static string MakePromptChoiceSayBypassKey(
        ulong questUID,
        string taskSymbol)
    {
        return questUID.ToString() + "|prompt-choice-say|" +
            NormalizePersonClickTaskSymbol(taskSymbol);
    }

    private static bool TaskContainsPrompt(
        Quest q,
        string taskSymbol)
    {
        return TaskHasActionType(q, taskSymbol, "Prompt") ||
            TaskHasActionType(q, taskSymbol, "PromptMulti");
    }

    private static void RegisterSharedTotingPromptContext(
        Quest q,
        string triggerTaskSymbol,
        uint sourceNetId,
        bool localIsOwner)
    {
        if (q == null ||
            q.UID == 0UL ||
            string.IsNullOrEmpty(triggerTaskSymbol) ||
            !TaskContainsPrompt(q, triggerTaskSymbol))
            return;

        SharedTotingPromptContext context =
            new SharedTotingPromptContext
            {
                localIsOwner = localIsOwner,
                sourceNetId = sourceNetId,
            };
        context.allowedPromptTasks.Add(
            NormalizePersonClickTaskSymbol(triggerTaskSymbol));
        _sharedTotingPromptContexts[q.UID] = context;

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][PromptOwner] Registered toting prompt " +
                $"uid={q.UID} task='{triggerTaskSymbol}' " +
                $"localOwner={localIsOwner} source={sourceNetId}");
        }
    }

    /// <summary>
    /// Prompt and PromptMulti call this before accepting a local button. A prompt
    /// reached from a replicated toting interaction is answered only by the player
    /// who physically performed that interaction.
    /// </summary>
    public static bool CanLocalPlayerAnswerPrompt(
        ulong questUID,
        string ownerTaskSymbol,
        int promptMessageId)
    {
        if (questUID == 0UL)
            return true;

        SharedTotingPromptContext context;
        if (!_sharedTotingPromptContexts.TryGetValue(
                questUID,
                out context) ||
            context == null ||
            !context.allowedPromptTasks.Contains(
                NormalizePersonClickTaskSymbol(ownerTaskSymbol)))
            return true;

        if (!context.localIsOwner && Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][PromptOwner] Ignored observer answer " +
                $"uid={questUID} owner='{ownerTaskSymbol}' " +
                $"msg={promptMessageId} source={context.sourceNetId}");
        }

        return context.localIsOwner;
    }

    private static HashSet<string> GetPromptBranchTaskClosure(
        Quest q,
        string selectedTaskSymbol)
    {
        HashSet<string> closure =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (q == null || string.IsNullOrEmpty(selectedTaskSymbol))
            return closure;

        closure.Add(NormalizePersonClickTaskSymbol(selectedTaskSymbol));
        List<DaggerfallWorkshop.Game.Questing.Task> tasks =
            GetQuestTasksForActionScan(q);

        bool added;
        do
        {
            added = false;
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Symbol == null || task.Actions == null)
                    continue;

                string normalizedTask =
                    NormalizePersonClickTaskSymbol(task.Symbol.Name);
                if (closure.Contains(normalizedTask))
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (!WhenConditionReferencesAnyTask(action, closure))
                        continue;

                    closure.Add(normalizedTask);
                    added = true;
                    break;
                }
            }
        }
        while (added);

        return closure;
    }

    private static void PrepareReplicatedPromptBranch(
        Quest q,
        string ownerTaskSymbol,
        string selectedTaskSymbol)
    {
        if (q == null)
            return;

        HashSet<string> closure =
            GetPromptBranchTaskClosure(q, selectedTaskSymbol);
        SharedTotingPromptContext context;
        bool hasContext =
            _sharedTotingPromptContexts.TryGetValue(q.UID, out context) &&
            context != null;

        if (hasContext)
        {
            context.allowedPromptTasks.Remove(
                NormalizePersonClickTaskSymbol(ownerTaskSymbol));
        }

        foreach (string taskSymbol in closure)
        {
            if (TaskHasActionType(q, taskSymbol, "Say"))
            {
                _promptChoiceSayBypassTasks.Add(
                    MakePromptChoiceSayBypassKey(q.UID, taskSymbol));
            }

            if (hasContext && TaskContainsPrompt(q, taskSymbol))
                context.allowedPromptTasks.Add(taskSymbol);
        }

        if (hasContext && context.allowedPromptTasks.Count == 0)
            _sharedTotingPromptContexts.Remove(q.UID);
    }

    private static int ApproveNestedStartsInReplicatedPromptBranch(
        Quest q,
        string selectedTaskSymbol)
    {
        QuestNetSync local = LocalInstance;
        if (q == null ||
            local == null ||
            !local.isLocalPlayer ||
            !local.isClientOnly)
            return 0;

        HashSet<string> closure =
            GetPromptBranchTaskClosure(q, selectedTaskSymbol);
        int approved = 0;

        foreach (string taskSymbol in closure)
        {
            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                string targetName;
                if (!TryGetStartQuestTargetName(action, out targetName))
                    continue;

                RegisterPendingScenePersonHandoffs(
                    q,
                    targetName);

                string key =
                    MakeDeferredNestedStartKey(
                        q.UID,
                        taskSymbol,
                        targetName);
                _clientDeferredNestedStarts.Add(key);
                if (_clientApprovedNestedStarts.Add(key))
                    approved++;
            }
        }

        if (approved > 0 && Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][QuestChain] Approved {approved} scheduled nested " +
                $"start(s) uid={q.UID} selected='{selectedTaskSymbol}'");
        }

        return approved;
    }

    private static string MakePromptChoiceKey(
        string instanceId,
        ulong questUID,
        string ownerTaskSymbol,
        int promptMessageId)
    {
        string questKey = !string.IsNullOrEmpty(instanceId)
            ? "inst=" + instanceId
            : "uid=" + questUID.ToString();

        return questKey +
            ":prompt:" + (ownerTaskSymbol ?? string.Empty) +
            ":" + promptMessageId.ToString();
    }

    public static bool HasAppliedPromptChoice(
        ulong questUID,
        string ownerTaskSymbol,
        int promptMessageId)
    {
        if (questUID == 0UL)
            return false;

        string instanceId = GetLocalQuestInstanceId(questUID);
        return _appliedPromptChoices.Contains(
            MakePromptChoiceKey(
                instanceId,
                questUID,
                ownerTaskSymbol,
                promptMessageId));
    }

    public static void ReportLocalPromptChoice(
        ulong questUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            _suppressPromptChoiceReportDepth > 0 ||
            questUID == 0UL ||
            string.IsNullOrEmpty(ownerTaskSymbol) ||
            string.IsNullOrEmpty(selectedTaskSymbol) ||
            IsQuestSharingBlacklistedUid(questUID))
            return;

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        string instanceId = GetLocalQuestInstanceId(questUID);
        string key = MakePromptChoiceKey(
            instanceId,
            questUID,
            ownerTaskSymbol,
            promptMessageId);

        if (!_appliedPromptChoices.Add(key))
            return;

        Quest localQuest = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(questUID)
            : null;
        if (localQuest == null ||
            !ApplyPromptChoiceToQuest(
                localQuest,
                ownerTaskSymbol,
                promptMessageId,
                selectedTaskSymbol))
        {
            _appliedPromptChoices.Remove(key);
            Debug.LogWarning(
                $"[QuestNetSync][PromptChoice] Source failed to execute branch " +
                $"uid={questUID} owner='{ownerTaskSymbol}' " +
                $"msg={promptMessageId} selected='{selectedTaskSymbol}'");
            return;
        }

        inst.CmdPromptChoice(
            instanceId,
            questUID,
            ownerTaskSymbol,
            promptMessageId,
            selectedTaskSymbol,
            _localNetId);

        inst.StartCoroutine(
            inst.CoRefreshPromptChoicePlacedItems(questUID));
    }

    private static bool TryFindPromptChoiceAction(
        Quest q,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        out IQuestAction promptAction)
    {
        promptAction = null;
        if (q == null ||
            string.IsNullOrEmpty(ownerTaskSymbol) ||
            string.IsNullOrEmpty(selectedTaskSymbol))
            return false;

        DaggerfallWorkshop.Game.Questing.Task ownerTask =
            q.GetTask(new Symbol(ownerTaskSymbol));
        if (ownerTask == null || ownerTask.Actions == null)
            return false;

        foreach (IQuestAction action in ownerTask.Actions)
        {
            if (action == null)
                continue;

            string actionTypeName = action.GetType().Name;
            bool normalPrompt =
                string.Equals(
                    actionTypeName,
                    "Prompt",
                    StringComparison.Ordinal);
            bool multiPrompt =
                string.Equals(
                    actionTypeName,
                    "PromptMulti",
                    StringComparison.Ordinal);
            if (!normalPrompt && !multiPrompt)
                continue;

            try
            {
                object saveData = action.GetSaveData();
                if (saveData == null)
                    continue;

                Type saveType = saveData.GetType();
                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                FieldInfo idField =
                    saveType.GetField("id", flags);
                int actualMessageId = idField != null
                    ? Convert.ToInt32(idField.GetValue(saveData))
                    : 0;

                if (promptMessageId != 0 &&
                    actualMessageId != promptMessageId)
                    continue;

                List<string> validSelectedTasks =
                    new List<string>(4);

                if (normalPrompt)
                {
                    FieldInfo yesField =
                        saveType.GetField("yesTaskSymbol", flags);
                    FieldInfo noField =
                        saveType.GetField("noTaskSymbol", flags);

                    string yesTask = yesField != null
                        ? GetSymbolName(yesField.GetValue(saveData))
                        : string.Empty;
                    string noTask = noField != null
                        ? GetSymbolName(noField.GetValue(saveData))
                        : string.Empty;

                    if (!string.IsNullOrEmpty(yesTask))
                        validSelectedTasks.Add(yesTask);
                    if (!string.IsNullOrEmpty(noTask))
                        validSelectedTasks.Add(noTask);
                }
                else
                {
                    for (int optionIndex = 1;
                         optionIndex <= 4;
                         optionIndex++)
                    {
                        FieldInfo optionField =
                            saveType.GetField(
                                "opt" + optionIndex.ToString() +
                                "TaskSymbol",
                                flags);
                        string optionTask = optionField != null
                            ? GetSymbolName(
                                optionField.GetValue(saveData))
                            : string.Empty;
                        if (!string.IsNullOrEmpty(optionTask))
                            validSelectedTasks.Add(optionTask);
                    }
                }

                for (int i = 0; i < validSelectedTasks.Count; i++)
                {
                    if (string.Equals(
                            selectedTaskSymbol,
                            validSelectedTasks[i],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        promptAction = action;
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private static bool ApplyPromptChoiceToQuest(
        Quest q,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol)
    {
        IQuestAction promptAction;
        if (!TryFindPromptChoiceAction(
                q,
                ownerTaskSymbol,
                promptMessageId,
                selectedTaskSymbol,
                out promptAction))
            return false;

        DaggerfallWorkshop.Game.Questing.Task selectedTask =
            q.GetTask(new Symbol(selectedTaskSymbol));
        if (selectedTask == null)
            return false;

        // A remote NPC click can open the same modal prompt on every machine. Starting
        // the selected task while that stale window remains open leaves QuestMachine
        // paused, so PlaceItem/StartTimer/Log/Say never run until that player manually
        // answers as well. Close the matching local prompt before applying the branch.
        try
        {
            DaggerfallWorkshop.Game.Questing.Actions.Prompt.CloseNetworkPrompt(
                q.UID,
                ownerTaskSymbol,
                promptMessageId);
        }
        catch { }

        try
        {
            DaggerfallWorkshop.Game.Questing.Actions.PromptMulti.CloseNetworkPrompt(
                q.UID,
                ownerTaskSymbol,
                promptMessageId);
        }
        catch { }

        // If this answer arrives before the local prompt tick, do not open a second
        // interactive prompt on this non-choosing participant.
        if (!promptAction.IsComplete)
            promptAction.SetComplete();

        if (!selectedTask.IsTriggered)
            q.StartTask(new Symbol(selectedTaskSymbol));

        // Discover Say actions and any later Prompt/PromptMulti tasks reached through
        // When dependencies from this selected branch.
        PrepareReplicatedPromptBranch(
            q,
            ownerTaskSymbol,
            selectedTaskSymbol);

        // StartTask() only flips the task boolean. Previously the packet was logged as
        // "Applied" while the actual branch actions were deferred to a later quest tick.
        // A modal prompt, pause state, or intervening snapshot could clear/delay that task
        // before PlaceItem executed. Run this one selected task immediately, in its normal
        // scripted action order. Completed actions are skipped on the next regular tick,
        // so this does not execute the branch twice.
        _promptChoiceBranchExecutionDepth++;
        try
        {
            selectedTask.Update();
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][PromptChoice] Branch execution failed uid={q.UID} " +
                $"quest='{q.QuestName}' owner='{ownerTaskSymbol}' " +
                $"selected='{selectedTaskSymbol}': {ex.Message}");
            return false;
        }
        finally
        {
            _promptChoiceBranchExecutionDepth--;
        }

        ReassertClientQuestChainAuthorityAfterTaskState(
            q,
            "prompt-choice");

        return true;
    }

    [Command]
    private void CmdPromptChoice(
        string instanceId,
        ulong questUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            !isServer ||
            string.IsNullOrEmpty(ownerTaskSymbol) ||
            string.IsNullOrEmpty(selectedTaskSymbol))
            return;

        ulong serverQuestUID = questUID;
        ulong mappedUID;
        if (!string.IsNullOrEmpty(instanceId) &&
            _srvInst2Uid.TryGetValue(instanceId, out mappedUID) &&
            mappedUID != 0UL)
            serverQuestUID = mappedUID;

        Quest q = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(serverQuestUID)
            : null;
        if (q == null ||
            q.QuestComplete ||
            q.QuestTombstoned ||
            IsQuestSharingBlacklisted(q))
            return;

        IQuestAction validatedAction;
        if (!TryFindPromptChoiceAction(
                q,
                ownerTaskSymbol,
                promptMessageId,
                selectedTaskSymbol,
                out validatedAction))
        {
            Debug.LogWarning(
                $"[QuestNetSync][PromptChoice] Rejected uid={serverQuestUID} " +
                $"quest='{q.QuestName}' owner='{ownerTaskSymbol}' " +
                $"msg={promptMessageId} selected='{selectedTaskSymbol}'");
            return;
        }

        if (string.IsNullOrEmpty(instanceId))
            _srvUid2Inst.TryGetValue(serverQuestUID, out instanceId);

        string key = MakePromptChoiceKey(
            instanceId,
            serverQuestUID,
            ownerTaskSymbol,
            promptMessageId);
        if (!_serverAcceptedPromptChoices.Add(key))
            return;

        _suppressPromptChoiceReportDepth++;
        try
        {
            if (!ApplyPromptChoiceToQuest(
                    q,
                    ownerTaskSymbol,
                    promptMessageId,
                    selectedTaskSymbol))
            {
                _serverAcceptedPromptChoices.Remove(key);
                return;
            }
        }
        finally
        {
            _suppressPromptChoiceReportDepth--;
        }

        StartCoroutine(
            CoRefreshPromptChoicePlacedItems(serverQuestUID));

        ServerBroadcastPromptChoice(
            instanceId,
            serverQuestUID,
            ownerTaskSymbol,
            promptMessageId,
            selectedTaskSymbol,
            sourceNetId);

        Debug.Log(
            $"[QuestNetSync][PromptChoice] Accepted+executed uid={serverQuestUID} " +
            $"quest='{q.QuestName}' owner='{ownerTaskSymbol}' " +
            $"msg={promptMessageId} selected='{selectedTaskSymbol}' " +
            $"source={sourceNetId}");
    }

    [TargetRpc]
    private void TargetPromptChoice(
        NetworkConnection target,
        string instanceId,
        ulong serverQuestUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        ApplyPromptChoicePacket(
            instanceId,
            serverQuestUID,
            ownerTaskSymbol,
            promptMessageId,
            selectedTaskSymbol,
            sourceNetId);
    }

    [ClientRpc]
    private void RpcPromptChoice(
        string instanceId,
        ulong serverQuestUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        ApplyPromptChoicePacket(
            instanceId,
            serverQuestUID,
            ownerTaskSymbol,
            promptMessageId,
            selectedTaskSymbol,
            sourceNetId);
    }

    private void ApplyPromptChoicePacket(
        string instanceId,
        ulong serverQuestUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            !isClient ||
            isServer)
            return;

        ulong localQuestUID;
        if (!TryResolveLocalQuestUidForInstance(
                instanceId,
                serverQuestUID,
                out localQuestUID))
            return;

        Quest q = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(localQuestUID)
            : null;
        if (q == null || q.QuestComplete || q.QuestTombstoned)
            return;

        // This packet is the server's proof that any StartQuest in the selected
        // branch has been accepted/scheduled. Clients may now skip those exact
        // StartQuest actions at runtime and execute the following actions without
        // persisting premature completion.
        ApproveNestedStartsInReplicatedPromptBranch(
            q,
            selectedTaskSymbol);

        // The choosing client already selected and triggered this branch locally.
        // Resume it now under the server approval instead of applying the prompt a
        // second time.
        if (_localNetId != 0U && sourceNetId == _localNetId)
        {
            DaggerfallWorkshop.Game.Questing.Task sourceTask =
                q.GetTask(new Symbol(selectedTaskSymbol));
            if (sourceTask != null && sourceTask.IsTriggered)
            {
                try
                {
                    sourceTask.Update();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[QuestNetSync][QuestChain] Approved source continuation " +
                        $"failed uid={q.UID} task='{selectedTaskSymbol}': {ex.Message}");
                }
            }
            return;
        }

        string key = MakePromptChoiceKey(
            instanceId,
            localQuestUID,
            ownerTaskSymbol,
            promptMessageId);
        if (!_appliedPromptChoices.Add(key))
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(
            localQuestUID,
            "remote-prompt-choice");

        _suppressPromptChoiceReportDepth++;
        try
        {
            if (!ApplyPromptChoiceToQuest(
                    q,
                    ownerTaskSymbol,
                    promptMessageId,
                    selectedTaskSymbol))
            {
                _appliedPromptChoices.Remove(key);
                Debug.LogWarning(
                    $"[QuestNetSync][PromptChoice] Client failed uid={localQuestUID} " +
                    $"quest='{q.QuestName}' owner='{ownerTaskSymbol}' " +
                    $"msg={promptMessageId} selected='{selectedTaskSymbol}'");
                return;
            }
        }
        finally
        {
            _suppressPromptChoiceReportDepth--;
        }

        StartCoroutine(
            CoRefreshPromptChoicePlacedItems(localQuestUID));

        Debug.Log(
            $"[QuestNetSync][PromptChoice] Applied+executed uid={localQuestUID} " +
            $"quest='{q.QuestName}' owner='{ownerTaskSymbol}' " +
            $"msg={promptMessageId} selected='{selectedTaskSymbol}' " +
            $"source={sourceNetId}");
    }

    private static void ServerBroadcastPromptChoice(
        string instanceId,
        ulong serverQuestUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        // Connection-based fanout reaches inactive/distant player objects too.
        ServerBroadcastOwnedPromptChoice(
            instanceId,
            serverQuestUID,
            ownerTaskSymbol,
            promptMessageId,
            selectedTaskSymbol,
            sourceNetId);
    }

    private static void ServerBroadcastOwnedPromptChoice(
        string instanceId,
        ulong serverQuestUID,
        string ownerTaskSymbol,
        int promptMessageId,
        string selectedTaskSymbol,
        uint sourceNetId)
    {
        int connectedCount = 0;
        int sentCount = 0;
        int missingIdentityCount = 0;
        int missingSyncCount = 0;

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection connection = entry.Value;
            if (connection == null)
                continue;

            connectedCount++;

            NetworkIdentity identity = connection.identity;
            if (identity == null)
            {
                missingIdentityCount++;
                continue;
            }

            QuestNetSync recipientSync =
                identity.GetComponent<QuestNetSync>();
            if (recipientSync == null)
            {
                recipientSync =
                    identity.GetComponentInChildren<QuestNetSync>(true);
            }

            if (recipientSync == null || !recipientSync.isServer)
            {
                missingSyncCount++;
                continue;
            }

            recipientSync.TargetPromptChoice(
                connection,
                instanceId,
                serverQuestUID,
                ownerTaskSymbol,
                promptMessageId,
                selectedTaskSymbol,
                sourceNetId);
            sentCount++;
        }

        Debug.Log(
            $"[QuestNetSync][PromptOwnerFanout] Sent selected branch " +
            $"uid={serverQuestUID} inst='{instanceId}' owner='{ownerTaskSymbol}' " +
            $"msg={promptMessageId} selected='{selectedTaskSymbol}' " +
            $"connected={connectedCount} recipients={sentCount} " +
            $"missingIdentity={missingIdentityCount} missingSync={missingSyncCount} " +
            $"source={sourceNetId}");
    }

    private IEnumerator CoRefreshPromptChoicePlacedItems(
        ulong questUID)
    {
        // Let the selected task execute PlaceItem/Log/Say first.
        yield return new WaitForSecondsRealtime(0.25f);

        Quest q = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(questUID)
            : null;
        if (q == null || q.QuestComplete || q.QuestTombstoned)
            yield break;

        RefreshCurrentSiteQuestItemObjects();
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // Item click + quest-item inventory replication
    // ─────────────────────────────────────────────────────────────────────────────
    private static string MakeItemClickMessageKey(ulong questUID, string itemSymbol, int messageId)
    {
        return questUID.ToString() + "|item-click|" + (itemSymbol ?? string.Empty) + "|" + messageId.ToString();
    }

    private static string MakeItemClickApplyKey(ulong questUID, string itemSymbol)
    {
        return questUID.ToString() + "|item-click-applied|" + (itemSymbol ?? string.Empty);
    }

    private static bool CompleteRemoteClickedItemTrigger(Quest q, string itemSymbol, string triggerTaskSymbol, out int inlineMessageId)
    {
        inlineMessageId = 0;
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = new List<DaggerfallWorkshop.Game.Questing.Task>();
            if (!string.IsNullOrEmpty(triggerTaskSymbol))
            {
                DaggerfallWorkshop.Game.Questing.Task triggerTask = q.GetTask(new Symbol(triggerTaskSymbol));
                if (triggerTask != null)
                    tasks.Add(triggerTask);
            }

            // Fallback for older action patches that do not report their parent task.
            if (tasks.Count == 0)
                tasks.AddRange(GetQuestTasksForActionScan(q));

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(action.GetType().Name, "ClickedItem", StringComparison.Ordinal))
                        continue;

                    FieldInfo itemField = action.GetType().GetField(
                        "itemSymbol",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (itemField == null)
                        continue;

                    string actionItemSymbol = GetSymbolName(itemField.GetValue(action));
                    if (!string.Equals(actionItemSymbol, itemSymbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Read the ID from ClickedItem itself. The reporting hook can also
                    // supply the ID of a standalone Say action in the same task, which
                    // must not be displayed explicitly before that Say action runs.
                    FieldInfo idField = action.GetType().GetField(
                        "id",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (idField != null)
                    {
                        object idValue = idField.GetValue(action);
                        if (idValue != null)
                            inlineMessageId = Convert.ToInt32(idValue);
                    }

                    // The explicit network event now owns this trigger. Completing only
                    // ClickedItem prevents its inline message and report callback from
                    // running again. Other actions in the task (including a standalone
                    // Say action) remain untouched and execute normally after StartTask().
                    action.SetComplete();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] Failed to complete replicated ClickedItem trigger: " + ex.Message);
        }

        return false;
    }

    private static string MakeLocationRevealKey(ulong questUID, string placeSymbol)
    {
        return questUID.ToString() + "|location-reveal|" + (placeSymbol ?? string.Empty);
    }

    private static string MakePcAtKey(ulong questUID, string taskSymbol)
    {
        return questUID.ToString() + "|pcat|" + (taskSymbol ?? string.Empty);
    }

    private static string MakeRewardReplayKey(ulong questUID, string taskSymbol)
    {
        return questUID.ToString() + "|reward-replay|" + (taskSymbol ?? string.Empty);
    }

    private static void SuppressClientQuestEndReportFromRemoteTrigger(ulong questUID, string reason)
    {
        if (questUID == 0UL)
            return;

        // Only pure clients should suppress this. On the host, the local QuestMachine
        // is authoritative and must still be allowed to report its own real quest end.
        QuestNetSync inst = LocalInstance;
        if (inst != null && inst.isClientOnly)
        {
            _suppressClientQuestEndReportUids.Add(questUID);
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Armed client quest-end echo suppression uid={questUID} reason={reason}");
        }
    }


    private static string[] GetQuestTaskNames(Quest q)
    {
        if (q == null)
            return new string[0];

        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
            if (tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                    if (task == null || task.Symbol == null || string.IsNullOrEmpty(task.Symbol.Name))
                        continue;

                    if (seen.Add(task.Symbol.Name))
                        result.Add(task.Symbol.Name);
                }
            }
        }
        catch { }

        try
        {
            Quest.TaskState[] states = q.GetTaskStates();
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].symbol == null || string.IsNullOrEmpty(states[i].symbol.Name))
                        continue;

                    if (seen.Add(states[i].symbol.Name))
                        result.Add(states[i].symbol.Name);
                }
            }
        }
        catch { }

        return result.ToArray();
    }

    private static HashSet<string> CaptureCompletedGivePcTaskSymbols(Quest q)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (q == null)
            return result;

        try
        {
            string[] names = GetQuestTaskNames(q);
            if (names == null)
                return result;

            for (int i = 0; i < names.Length; i++)
            {
                string taskName = names[i];
                if (string.IsNullOrEmpty(taskName))
                    continue;

                DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskName));
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null)
                        continue;

                    if (string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal) &&
                        !IsGivePcNothingAction(action) && action.IsComplete)
                    {
                        result.Add(taskName);
                        break;
                    }
                }
            }
        }
        catch { }

        return result;
    }

    private static string[] FilterOutGivePcTasksCompletedBeforeRemoteEnd(string[] taskSymbols, HashSet<string> completedBefore)
    {
        if (taskSymbols == null || taskSymbols.Length == 0)
            return taskSymbols;

        if (completedBefore == null || completedBefore.Count == 0)
            return taskSymbols;

        List<string> result = new List<string>();
        for (int i = 0; i < taskSymbols.Length; i++)
        {
            string taskSymbol = taskSymbols[i];
            if (string.IsNullOrEmpty(taskSymbol))
                continue;

            // If GivePc was already complete on the host before this client Command was
            // applied, then the host already ran vanilla reward locally. Replaying it
            // here is exactly the double-gold/drop bug.
            if (completedBefore.Contains(taskSymbol))
                continue;

            result.Add(taskSymbol);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool ConsumeRemoteItemClickMessage(ulong questUID, string itemSymbol, int messageId)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || messageId == 0)
            return false;

        string key = MakeItemClickMessageKey(questUID, itemSymbol, messageId);
        if (!_remoteItemClickMessagesShown.Contains(key))
            return false;

        // Only suppress while this machine still knows it applied the corresponding
        // remote click. A message-only key can outlive a save reload; treating that
        // stale key as authoritative can swallow the next genuine local pickup popup.
        // For an active remote replay, keep both guards for the lifetime of the picked
        // item so later task/delta evaluation cannot display the popup again.
        if (!_remoteItemClicksApplied.Contains(MakeItemClickApplyKey(questUID, itemSymbol)))
        {
            _remoteItemClickMessagesShown.Remove(key);
            return false;
        }

        return true;
    }

    public static bool ShouldSuppressClickedItemWithoutInventory(ulong questUID, string itemSymbol)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return false;

        // Singleplayer must keep vanilla behaviour. This guard is only for MP
        // save/load or replay state where stale HasPlayerClicked can survive in
        // memory and fire ClickedItem again without the physical pickup.
        try
        {
            if (!NetworkClient.active && !NetworkServer.active)
                return false;
        }
        catch { return false; }

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return false;

        // Only apply to placed pickup-style quest items, like Rare Book's _book_,
        // not to arbitrary ClickedItem conditions.
        if (!HasPickupActionForItem(q, itemSymbol))
            return false;

        if (IsQuestItemInPlayerInventory(q, itemSymbol))
            return false;

        try
        {
            Item item = q.GetItem(new Symbol(itemSymbol));
            if (item != null)
            {
                QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
                rsd.hasPlayerClicked = false;
                item.RestoreResourceSaveData(rsd);
            }
        }
        catch { }

        try
        {
            _remoteItemClicksApplied.Remove(MakeItemClickApplyKey(questUID, itemSymbol));
            string msgPrefix = questUID.ToString() + "|item-click|" + (itemSymbol ?? string.Empty) + "|";
            _remoteItemClickMessagesShown.RemoveWhere(k => k.StartsWith(msgPrefix));
        }
        catch { }

        Debug.Log($"[QuestItemPickupDbg] Suppressed stale ClickedItem without inventory uid={questUID} symbol='{itemSymbol}'");
        return true;
    }

    public static void ReportLocalItemClicked(ulong questUID, string itemSymbol, int messageId = 0, string triggerTaskSymbol = null)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (_suppressItemClickReportDepth > 0)
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        string appliedKey = MakeItemClickApplyKey(questUID, itemSymbol);

        // ApplyRemoteItemClick() can start a task whose ClickedItem action reports on
        // a later quest tick, after _suppressItemClickReportDepth has returned to zero.
        // That delayed callback is still the same remote pickup, not a new local click.
        // Reporting it again makes every client echo the event through the server and
        // is why pickup popups scale with player count. Keep the applied guard and stop
        // the echo here. Save/load hygiene and an authoritative unpicked item state
        // clear the guard before a genuinely new click is possible.
        if (_remoteItemClicksApplied.Contains(appliedKey))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Suppressed delayed remote item-click echo uid={questUID} item='{itemSymbol}' msg={messageId} task='{triggerTaskSymbol}'");
            return;
        }

        // This is a genuine local click, not a delayed remote callback. Discard any
        // stale message guard left by an earlier load of the same quest UID so the
        // local ClickedItem action is allowed to show its inline message normally.
        if (messageId != 0)
            _remoteItemClickMessagesShown.Remove(MakeItemClickMessageKey(questUID, itemSymbol, messageId));

        // This proves that a following inventory add for this quest symbol came from a
        // real clicked world item, not from taking reward loot out of the reward window.
        MarkQuestItemClickPickupAllowed(questUID, itemSymbol);

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        inst.CmdItemClicked(questUID, itemSymbol, messageId, triggerTaskSymbol ?? string.Empty, _localNetId);
    }

    private static string MakeQuestItemInventoryKey(ulong questUID, string itemSymbol)
    {
        return questUID.ToString() + "|quest-item-inventory|" + (itemSymbol ?? string.Empty);
    }

    private static void MarkTotingQuestItemConsumed(
        ulong questUID,
        string itemSymbol,
        string reason)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        string key = MakeQuestItemInventoryKey(questUID, itemSymbol);
        if (_consumedTotingQuestItemKeys.Add(key))
        {
            Debug.Log(
                $"[QuestNetSync][TotingConsumed] Marked consumed " +
                $"uid={questUID} item='{itemSymbol}' reason='{reason}'");
        }

        RemovePickedQuestItemKey(key);
    }

    private static void ClearTotingQuestItemConsumed(
        ulong questUID,
        string itemSymbol,
        string reason)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        string key = MakeQuestItemInventoryKey(questUID, itemSymbol);
        if (_consumedTotingQuestItemKeys.Remove(key) &&
            Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][TotingConsumed] Cleared consumed guard " +
                $"uid={questUID} item='{itemSymbol}' reason='{reason}'");
        }
    }

    private static bool IsTotingQuestItemConsumed(
        ulong questUID,
        string itemSymbol)
    {
        return questUID != 0UL &&
               !string.IsNullOrEmpty(itemSymbol) &&
               _consumedTotingQuestItemKeys.Contains(
                   MakeQuestItemInventoryKey(questUID, itemSymbol));
    }

    private static string MakeQuestItemClickPickupKey(ulong questUID, string itemSymbol)
    {
        return questUID.ToString() + "|quest-item-click-pickup|" + (itemSymbol ?? string.Empty);
    }

    private static void MarkQuestItemClickPickupAllowed(ulong questUID, string itemSymbol)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        _recentQuestItemClickPickupUntil[MakeQuestItemClickPickupKey(questUID, itemSymbol)] =
            Time.realtimeSinceStartup + QuestItemClickPickupAllowSeconds;
    }

    private static bool IsQuestItemClickPickupAllowed(ulong questUID, string itemSymbol)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return false;

        string key = MakeQuestItemClickPickupKey(questUID, itemSymbol);
        float until;
        if (!_recentQuestItemClickPickupUntil.TryGetValue(key, out until))
            return false;

        if (Time.realtimeSinceStartup > until)
        {
            _recentQuestItemClickPickupUntil.Remove(key);
            return false;
        }

        return true;
    }

    public static void ReportLocalQuestItemInventoryChanged(ulong questUID, string itemSymbol, bool inInventory)
    {
        ReportLocalQuestItemInventoryChanged(questUID, itemSymbol, inInventory, null);
    }

    public static void ReportLocalQuestItemInventoryChanged(ulong questUID, string itemSymbol, bool inInventory, DaggerfallUnityItem sourceItem)
    {
        // This entire method is multiplayer inventory mirroring/canonicalization.
        // QuestResourceBehaviour also calls it from the vanilla physical world-item
        // pickup path, including in singleplayer. Do not let the MP repair code clone,
        // relink, or rebind a normal SP quest item after vanilla MakePermanent has
        // converted it into a permanent (white) item.
        try
        {
            if (!NetworkClient.active && !NetworkServer.active)
                return;
        }
        catch { return; }

        if (IsQuestNetSyncPausedForLoad())
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        bool realClickedPickup = IsQuestItemClickPickupAllowed(questUID, itemSymbol);
        bool totingPickupItem = q != null && HasTotingTaskForItem(q, itemSymbol);
        bool foeLootPickupItem = q != null && HasFoeLootAssignmentForItem(q, itemSymbol);
        if (q == null ||
            (!HasPhysicalPickupActionForItem(q, itemSymbol) &&
             !realClickedPickup &&
             !totingPickupItem &&
             !foeLootPickupItem))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Suppressed inventory-change sync for non-physical/non-toting/non-foe-loot quest item uid={questUID} symbol='{itemSymbol}' inInventory={inInventory}");
            return;
        }

        string key = MakeQuestItemInventoryKey(questUID, itemSymbol);
        if (inInventory)
        {
            ClearTotingQuestItemConsumed(
                questUID,
                itemSymbol,
                "local-explicit-acquisition");
            ProtectPickedQuestItemKey(key);
        }
        else
            RemovePickedQuestItemKey(key);

        // This is the *source* player path. For corpse-looted quest items the item is
        // already in the local inventory, but it may only be a serialized loot item with
        // a green quest link. TotingItemAndClickedNpc is stricter than the inventory UI,
        // so repair the local quest-resource state immediately instead of deferring while
        // the loot window is open. Remote receivers still use the deferred path below.
        RelinkSourceQuestItemForLocalQuest(q, itemSymbol, sourceItem);
        string itemDataJson = BuildQuestItemDataJson(sourceItem);

        // Do not canonicalize/source-repair while the loot/inventory window is still open.
        // DFU's loot window can still be in the middle of moving the clicked item from the
        // corpse collection to PlayerEntity.Items. If we remove/re-add the item right here,
        // the window's stale item transaction can leave the source player with a green item
        // that still fails TotingItemAndClickedNpc. Queue the repair and let the same
        // canonical path run after the window/popup/time-stop state is gone.
        if (ShouldDeferQuestInventoryApplyNow())
        {
            QueuePendingQuestInventoryChange(questUID, itemSymbol, inInventory, itemDataJson, "local-source-ui-paused");
        }
        else if (!TryApplyQuestItemInventoryChangedImmediate(questUID, itemSymbol, inInventory, itemDataJson))
        {
            QueuePendingQuestInventoryChange(questUID, itemSymbol, inInventory, itemDataJson, "local-source-quest-not-ready");
        }

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        inst.CmdQuestItemInventoryChanged(questUID, itemSymbol, inInventory, itemDataJson, _localNetId);
    }

    private static string BuildQuestItemDataJson(DaggerfallUnityItem sourceItem)
    {
        if (sourceItem == null)
            return string.Empty;

        try
        {
            ItemData_v1 data = sourceItem.GetSaveData();
            return ToJson(data);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[QuestNetSync] Failed to serialize picked quest item data: " + ex.Message);
            return string.Empty;
        }
    }


    private static void RelinkSourceQuestItemForLocalQuest(Quest q, string itemSymbol, DaggerfallUnityItem sourceItem)
    {
        if (q == null || sourceItem == null || string.IsNullOrEmpty(itemSymbol))
            return;

        TrackQuestInventoryObject(q, itemSymbol, sourceItem);

        try
        {
            // Once this symbol has become permanent, a delayed pickup/inventory repair
            // must never turn the exact carried object green again.
            if (IsQuestItemPermanenceLatched(q, itemSymbol))
            {
                sourceItem.MakePermanent();
                return;
            }

            // Corpse loot can be rebuilt from server-authoritative item data. It can look
            // like a valid green quest item but still not be tied to this local quest
            // resource in the way TotingItemAndClickedNpc expects. Force the local link
            // before saving/sending itemDataJson and before SetQuestItemInventory() scans
            // the player inventory for an already-held quest item.
            sourceItem.LinkQuestItem(q.UID, new Symbol(itemSymbol));
        }
        catch { }
    }


    private static bool ForceCanonicalSourceTotingQuestItem(Quest q, string itemSymbol, DaggerfallUnityItem sourceItem, string itemDataJson)
    {
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return false;

        if (!HasTotingTaskForItem(q, itemSymbol))
            return false;

        var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
        if (pe == null || pe.Items == null)
            return false;

        Item questItem = q.GetItem(new Symbol(itemSymbol));
        if ((questItem == null || questItem.DaggerfallUnityItem == null) && string.IsNullOrEmpty(itemDataJson) && sourceItem == null)
            return false;

        try
        {
            string protectKey = MakeQuestItemInventoryKey(q.UID, itemSymbol);
            ProtectPickedQuestItemKey(protectKey);

            List<DaggerfallUnityItem> remove = new List<DaggerfallUnityItem>();
            for (int i = 0; i < pe.Items.Count; i++)
            {
                DaggerfallUnityItem it = pe.Items.GetItem(i);
                if (it == null)
                    continue;

                if (object.ReferenceEquals(it, sourceItem) ||
                    IsInventoryEntryForQuestItem(q, itemSymbol, it) ||
                    LooksLikeSameSourceQuestLootItem(it, sourceItem))
                {
                    remove.Add(it);
                }
            }

            for (int i = 0; i < remove.Count; i++)
                pe.Items.RemoveItem(remove[i]);

            DaggerfallUnityItem canonical = CloneQuestItemForInventory(q, questItem, itemSymbol, itemDataJson);

            // Fallback: if itemDataJson was unavailable but we have the physical loot item,
            // clone that exact item data and then relink it to this local quest.
            if (canonical == null && sourceItem != null)
            {
                try
                {
                    canonical = new DaggerfallUnityItem(sourceItem.GetSaveData());
                    canonical.stackCount = NormalizeQuestItemStackCount(canonical);
                    if (IsQuestItemPermanenceLatched(q, itemSymbol))
                        canonical.MakePermanent();
                    else
                        canonical.LinkQuestItem(q.UID, new Symbol(itemSymbol));
                }
                catch { canonical = null; }
            }

            if (canonical == null)
                return false;

            TryAssignFreshItemUid(canonical);
            pe.Items.AddItem(canonical, ItemCollection.AddPosition.Front);
            TrackQuestInventoryObject(q, itemSymbol, canonical);
            BindQuestItemResourceToInventoryItem(q, itemSymbol, canonical, "source-canonical");

            if (questItem != null)
            {
                QuestResource.ResourceSaveData_v1 rsd = questItem.GetResourceSaveData();
                rsd.hasPlayerClicked = true;
                rsd.isHidden = true;
                questItem.RestoreResourceSaveData(rsd);
                if (questItem.QuestResourceBehaviour != null)
                    questItem.QuestResourceBehaviour.gameObject.SetActive(false);
            }

            Debug.Log($"[QuestItemPickupDbg] Source toting quest item canonicalized uid={q.UID} symbol='{itemSymbol}' removed={remove.Count} group={(int)canonical.ItemGroup} index={canonical.GroupIndex} msg={canonical.message} json={!string.IsNullOrEmpty(itemDataJson)}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[QuestNetSync] ForceCanonicalSourceTotingQuestItem failed uid={q.UID} symbol='{itemSymbol}': {ex.Message}");
            return false;
        }
    }

    private static bool LooksLikeSameSourceQuestLootItem(DaggerfallUnityItem inventoryItem, DaggerfallUnityItem sourceItem)
    {
        if (inventoryItem == null || sourceItem == null)
            return false;

        try
        {
            if (inventoryItem.UID == sourceItem.UID)
                return true;
        }
        catch { }

        try
        {
            if (inventoryItem.ItemGroup == sourceItem.ItemGroup &&
                inventoryItem.TemplateIndex == sourceItem.TemplateIndex &&
                inventoryItem.message == sourceItem.message &&
                inventoryItem.stackCount == sourceItem.stackCount)
                return true;
        }
        catch { }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ItemUsedDo replication
    // A quest Item "used do <task>" is a local inventory-input edge. Routine task
    // snapshots are too late/ambiguous for this because the host may never observe the
    // source player's UseClicked flag. Replicate the exact validated action instead.
    // ─────────────────────────────────────────────────────────────────────────────
    public static void ReportLocalItemUsedDo(
        ulong questUID,
        string itemSymbol,
        string targetTaskSymbol,
        int textId,
        string ownerTaskSymbol)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (questUID == 0UL ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(targetTaskSymbol) ||
            IsQuestSharingBlacklistedUid(questUID))
            return;

        QuestNetSync inst = GetActualLocalQuestSync();
        if (inst == null)
            return;

        inst.CmdItemUsedDo(
            questUID,
            itemSymbol,
            targetTaskSymbol,
            textId,
            ownerTaskSymbol ?? string.Empty,
            inst.netId);
    }

    [Command]
    private void CmdItemUsedDo(
        ulong questUID,
        string itemSymbol,
        string targetTaskSymbol,
        int textId,
        string ownerTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer ||
            questUID == 0UL ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(targetTaskSymbol) ||
            IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest q = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(questUID)
            : null;
        if (q == null)
            return;

        if (!TryValidateItemUsedDoEvent(
                q,
                ownerTaskSymbol,
                itemSymbol,
                targetTaskSymbol,
                textId))
        {
            Debug.LogWarning(
                $"[QuestNetSync][ItemUsedDo] Rejected invalid event uid={questUID} " +
                $"owner='{ownerTaskSymbol}' item='{itemSymbol}' target='{targetTaskSymbol}' text={textId} source={sourceNetId}");
            return;
        }

        NetworkConnection sourceConnection = connectionToClient;
        NetworkConnection hostConnection = NetworkServer.localConnection;
        bool sourceIsHost =
            sourceConnection != null &&
            hostConnection != null &&
            sourceConnection == hostConnection;

        // The source has already executed ItemUsedDo locally. A pure-client source
        // must advance the authoritative host copy here before fanout.
        if (!sourceIsHost)
        {
            ApplyRemoteItemUsedDo(
                questUID,
                itemSymbol,
                targetTaskSymbol,
                textId,
                ownerTaskSymbol,
                sourceNetId);
        }

        RefreshServerSnapshotAfterExplicitQuestItemEvent(
            questUID,
            "item-used-do");

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection recipient = entry.Value;
            if (recipient == null ||
                recipient == sourceConnection ||
                recipient == hostConnection)
                continue;

            QuestNetSync sync = GetServerQuestSync(recipient);
            if (sync == null || !sync.isServer)
                continue;

            sync.TargetItemUsedDo(
                recipient,
                questUID,
                itemSymbol,
                targetTaskSymbol,
                textId,
                ownerTaskSymbol ?? string.Empty,
                sourceNetId);
        }

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][ItemUsedDo] Replicated uid={questUID} owner='{ownerTaskSymbol}' " +
                $"item='{itemSymbol}' target='{targetTaskSymbol}' text={textId} source={sourceNetId}");
        }
    }

    [TargetRpc]
    private void TargetItemUsedDo(
        NetworkConnection target,
        ulong questUID,
        string itemSymbol,
        string targetTaskSymbol,
        int textId,
        string ownerTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient ||
            questUID == 0UL ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(targetTaskSymbol))
            return;

        ApplyRemoteItemUsedDo(
            questUID,
            itemSymbol,
            targetTaskSymbol,
            textId,
            ownerTaskSymbol,
            sourceNetId);
    }

    private static void ApplyRemoteItemUsedDo(
        ulong questUID,
        string itemSymbol,
        string targetTaskSymbol,
        int textId,
        string ownerTaskSymbol,
        uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(questUID)
            : null;
        if (q == null)
            return;

        if (!TryValidateItemUsedDoEvent(
                q,
                ownerTaskSymbol,
                itemSymbol,
                targetTaskSymbol,
                textId))
            return;

        DaggerfallWorkshop.Game.Questing.Task targetTask =
            q.GetTask(new Symbol(targetTaskSymbol));
        if (targetTask == null)
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(
            questUID,
            "remote-item-used-do");

        bool newlyTriggered = !targetTask.IsTriggered;
        if (newlyTriggered)
            q.StartTask(new Symbol(targetTaskSymbol));

        // Inline "used saying <id> do" text belongs to the use edge itself, not the
        // target task. The source already showed it; remote participants should too.
        if (textId != 0)
            q.ShowMessagePopup(textId, oncePerQuest: true);

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][ItemUsedDo] Applied remote uid={questUID} owner='{ownerTaskSymbol}' " +
                $"item='{itemSymbol}' target='{targetTaskSymbol}' newlyTriggered={newlyTriggered} source={sourceNetId}");
        }
    }

    private static bool TryValidateItemUsedDoEvent(
        Quest q,
        string ownerTaskSymbol,
        string itemSymbol,
        string targetTaskSymbol,
        int textId)
    {
        if (q == null ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(targetTaskSymbol))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Symbol == null || task.Actions == null)
                    continue;

                if (!string.IsNullOrEmpty(ownerTaskSymbol) &&
                    !string.Equals(
                        task.Symbol.Name,
                        ownerTaskSymbol,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "ItemUsedDo",
                            StringComparison.Ordinal))
                        continue;

                    BindingFlags flags =
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public;

                    FieldInfo itemField =
                        action.GetType().GetField("itemSymbol", flags);
                    FieldInfo taskField =
                        action.GetType().GetField("taskSymbol", flags);
                    FieldInfo textField =
                        action.GetType().GetField("textID", flags);

                    if (itemField == null || taskField == null)
                        continue;

                    string actualItem =
                        GetSymbolName(itemField.GetValue(action));
                    string actualTarget =
                        GetSymbolName(taskField.GetValue(action));
                    int actualText = 0;
                    if (textField != null)
                    {
                        object rawText = textField.GetValue(action);
                        if (rawText != null)
                            actualText = Convert.ToInt32(rawText);
                    }

                    if (string.Equals(
                            actualItem,
                            itemSymbol,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            actualTarget,
                            targetTaskSymbol,
                            StringComparison.OrdinalIgnoreCase) &&
                        actualText == textId)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    [Command]
    private void CmdItemClicked(ulong questUID, string itemSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        // Host source already did the local click in the same process.
        if (_localNetId == 0 || sourceNetId != _localNetId)
            ApplyRemoteItemClick(questUID, itemSymbol, messageId, triggerTaskSymbol, sourceNetId);

        // ApplyRemoteItemClick() changes the authoritative host quest immediately.
        // Acknowledge that exact state now so Srv_OnTick() does not discover it again
        // and broadcast it as a new host-originated RpcUpdate. That redundant update
        // was replaying the pickup task/message on the client that originally clicked.
        RefreshServerSnapshotAfterExplicitQuestItemEvent(questUID, "item-click");

        RpcItemClicked(questUID, itemSymbol, messageId, triggerTaskSymbol, sourceNetId);
    }

    private void RefreshServerSnapshotAfterExplicitQuestItemEvent(ulong questUID, string reason)
    {
        if (!isServer || questUID == 0UL || QuestMachine.Instance == null)
            return;

        string instanceId;
        if (!_srvUid2Inst.TryGetValue(questUID, out instanceId) || string.IsNullOrEmpty(instanceId))
            return;

        Quest q = QuestMachine.Instance.GetQuest(questUID);
        if (q == null || q.QuestComplete || q.QuestTombstoned)
            return;

        _srvLastTasks[instanceId] = q.GetTaskStates();
        _srvLastLogs[instanceId] = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _srvLastItems[instanceId] = CaptureItemStates(q);
        _srvLastPersons[instanceId] = BuildPersons(q);
        _srvLastPlaces[instanceId] = BuildPlaces(q);
        _srvLastFoes[instanceId] = BuildFoes(q);

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] Acknowledged explicit quest-item snapshot uid={questUID} inst={instanceId} reason={reason}");
    }

    [ClientRpc]
    private void RpcItemClicked(ulong questUID, string itemSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        if (_localNetId != 0 && sourceNetId == _localNetId)
        {
            // The source client must not replay its own ClickedItem task, but it still
            // needs the authoritative physical-world cleanup. A placed quest Item can
            // temporarily have more than one QuestResourceBehaviour in the loaded scene
            // (for example after dungeon resource reinjection). Item.QuestResourceBehaviour
            // only points at one of them, so hiding just that back-reference can leave a
            // duplicate dagger/letter/book visibly sitting in the dungeon.
            //
            // This changes only physical scene objects. It does not add/remove inventory
            // items and does not replay the quest click or popup.
            HideLocalPhysicalQuestItemCopies(
                questUID,
                itemSymbol,
                "source-item-click-rpc");
            return;
        }

        ApplyRemoteItemClick(questUID, itemSymbol, messageId, triggerTaskSymbol, sourceNetId);
    }

    private static void HideLocalPhysicalQuestItemCopies(
        ulong questUID,
        string itemSymbol,
        string reason)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        int matched = 0;
        int hidden = 0;

        try
        {
            QuestResourceBehaviour[] behaviours =
                Resources.FindObjectsOfTypeAll<QuestResourceBehaviour>();

            if (behaviours == null)
                return;

            for (int i = 0; i < behaviours.Length; i++)
            {
                QuestResourceBehaviour qrb = behaviours[i];
                if (qrb == null ||
                    qrb.QuestUID != questUID ||
                    qrb.TargetSymbol == null ||
                    !string.Equals(
                        qrb.TargetSymbol.Name,
                        itemSymbol,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                matched++;

                GameObject go = qrb.gameObject;
                if (go != null && go.activeSelf)
                {
                    go.SetActive(false);
                    hidden++;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][WorldItemCleanup] Failed uid={questUID} " +
                $"item='{itemSymbol}' reason='{reason}': {ex.Message}");
            return;
        }

        Debug.Log(
            $"[QuestNetSync][WorldItemCleanup] uid={questUID} item='{itemSymbol}' " +
            $"matched={matched} newlyHidden={hidden} reason='{reason}'");
    }

    private static void ApplyRemoteItemClick(ulong questUID, string itemSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        Item item = q.GetItem(new Symbol(itemSymbol));
        if (item == null)
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(questUID, "remote-item-click");

        string appliedKey = MakeItemClickApplyKey(questUID, itemSymbol);
        if (_remoteItemClicksApplied.Contains(appliedKey))
            return;

        _remoteItemClicksApplied.Add(appliedKey);
        ProtectPickedQuestItemKey(MakeQuestItemInventoryKey(questUID, itemSymbol));

        // Allow SetQuestItemInventory() below even for clicked letter-style pickups
        // whose scripts are not detected by HasPhysicalPickupActionForItem().
        MarkQuestItemClickPickupAllowed(questUID, itemSymbol);

        _suppressItemClickReportDepth++;
        try
        {
            // Claim the ClickedItem condition before mutating its item resource. This
            // prevents the normal quest tick from racing the explicit event and showing
            // or reporting the same pickup once more on every receiving player.
            int inlineMessageId;
            bool foundClickedItemTrigger = CompleteRemoteClickedItemTrigger(
                q,
                itemSymbol,
                triggerTaskSymbol,
                out inlineMessageId);

            // A clicked quest item is usually physically picked up by the clicking player.
            // Mirror that inventory side effect first so later TotingItemAndClickedNpc/HAVE
            // checks can pass on the other machine too.
            SetQuestItemInventory(q, itemSymbol, true);

            QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
            rsd.hasPlayerClicked = true;
            rsd.isHidden = true;
            item.RestoreResourceSaveData(rsd);
            if (item.QuestResourceBehaviour != null)
                item.QuestResourceBehaviour.gameObject.SetActive(false);

            // ClickedItem supports two different quest-script forms:
            //   clicked item _item_ say 1011  -> popup belongs to ClickedItem itself
            //   clicked item _item_           -> a later standalone Say action may run
            // The event's messageId is not sufficient to distinguish these: older action
            // hooks can report the standalone Say ID. Trust the actual ClickedItem.id
            // whenever the trigger action was found. Only the inline form is explicit.
            int explicitPopupId = foundClickedItemTrigger ? inlineMessageId : messageId;
            if (explicitPopupId != 0)
            {
                string key = MakeItemClickMessageKey(questUID, itemSymbol, explicitPopupId);
                if (!_remoteItemClickMessagesShown.Contains(key))
                {
                    q.ShowMessagePopup(explicitPopupId);
                    _remoteItemClickMessagesShown.Add(key);
                }
            }

            // Start the same task for shared quest progression. When ClickedItem has no
            // inline ID, a standalone Say action (such as A Rare Book) is the producer.
            if (!string.IsNullOrEmpty(triggerTaskSymbol))
            {
                q.StartTask(new Symbol(triggerTaskSymbol));
                ReassertClientQuestChainAuthorityAfterTaskState(q, "remote-item-click");
            }

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Applied remote item click uid={questUID} item='{itemSymbol}' task='{triggerTaskSymbol}' reportedMsg={messageId} inlineMsg={inlineMessageId} explicitMsg={explicitPopupId} triggerFound={foundClickedItemTrigger} source={sourceNetId}");
        }
        finally
        {
            _suppressItemClickReportDepth--;
        }
    }

    private static NetworkConnection GetServerHostLocalConnection()
    {
        try
        {
            QuestNetSync local = LocalInstance;
            if (local != null && local.isServer && local.isLocalPlayer)
                return local.connectionToClient;
        }
        catch { }

        return null;
    }

    [Command]
    private void CmdQuestItemInventoryChanged(ulong questUID, string itemSymbol, bool inInventory, string itemDataJson, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        NetworkConnection sourceConnection = connectionToClient;
        NetworkConnection hostConnection = GetServerHostLocalConnection();

        // A pure-client source changed only its own inventory. Apply to the listen-host
        // immediately, then target every remaining connection through that recipient's
        // own player identity. This avoids observer-dependent ClientRpc loss.
        if (hostConnection != null && hostConnection != sourceConnection)
        {
            if (inInventory)
            {
                _remoteFoeLootInventoryEchoGuards.Add(
                    MakeQuestItemInventoryKey(questUID, itemSymbol));
            }

            ApplyQuestItemInventoryChanged(
                questUID,
                itemSymbol,
                inInventory,
                itemDataJson);
        }

        RefreshServerSnapshotAfterExplicitQuestItemEvent(
            questUID,
            "item-inventory");

        ServerBroadcastQuestItemInventoryChanged(
            sourceConnection,
            hostConnection,
            questUID,
            itemSymbol,
            inInventory,
            itemDataJson,
            sourceNetId,
            false);
    }

    private static void ServerBroadcastQuestItemInventoryChanged(
        NetworkConnection sourceConnection,
        NetworkConnection alreadyAppliedHostConnection,
        ulong questUID,
        string itemSymbol,
        bool inInventory,
        string itemDataJson,
        uint sourceNetId,
        bool consumedByToting)
    {
        int connectedCount = 0;
        int sentCount = 0;
        int missingIdentityCount = 0;
        int missingSyncCount = 0;

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection recipient = entry.Value;
            if (recipient == null)
                continue;

            connectedCount++;

            if (recipient == sourceConnection ||
                recipient == alreadyAppliedHostConnection)
                continue;

            NetworkIdentity identity = recipient.identity;
            if (identity == null)
            {
                missingIdentityCount++;
                continue;
            }

            QuestNetSync sync = identity.GetComponent<QuestNetSync>();
            if (sync == null)
                sync = identity.GetComponentInChildren<QuestNetSync>(true);

            if (sync == null || !sync.isServer)
            {
                missingSyncCount++;
                continue;
            }

            sync.TargetQuestItemInventoryChanged(
                recipient,
                questUID,
                itemSymbol,
                inInventory,
                itemDataJson,
                sourceNetId,
                consumedByToting);
            sentCount++;
        }

        Debug.Log(
            $"[QuestNetSync][QuestItemFanout] Sent inventory edge " +
            $"uid={questUID} item='{itemSymbol}' inInventory={inInventory} " +
            $"connected={connectedCount} recipients={sentCount} " +
            $"missingIdentity={missingIdentityCount} missingSync={missingSyncCount} " +
            $"source={sourceNetId}");
    }

    [TargetRpc]
    private void TargetQuestItemInventoryChanged(
        NetworkConnection target,
        ulong questUID,
        string itemSymbol,
        bool inInventory,
        string itemDataJson,
        uint sourceNetId,
        bool consumedByToting)
    {
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        if (inInventory)
        {
            ClearTotingQuestItemConsumed(
                questUID,
                itemSymbol,
                "remote-explicit-acquisition");
            _remoteFoeLootInventoryEchoGuards.Add(
                MakeQuestItemInventoryKey(questUID, itemSymbol));
        }
        else if (consumedByToting)
        {
            MarkTotingQuestItemConsumed(
                questUID,
                itemSymbol,
                "server-toting-removal");
        }

        // This method queues the exact event if the recipient is paused or has a
        // loot/inventory/popup window open. Do not drop the TargetRpc for local UI pause.
        ApplyQuestItemInventoryChanged(
            questUID,
            itemSymbol,
            inInventory,
            itemDataJson);
    }

    private static void ApplyQuestItemInventoryChanged(ulong questUID, string itemSymbol, bool inInventory)
    {
        ApplyQuestItemInventoryChanged(questUID, itemSymbol, inInventory, string.Empty);
    }

    private static void ApplyQuestItemInventoryChanged(ulong questUID, string itemSymbol, bool inInventory, string itemDataJson)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        // If a menu/popup/loot window is active, defer. This is the exact case where
        // remote inventory grants could be swallowed: the item is added to PlayerEntity,
        // but the open inventory/loot UI later writes its stale view back over it.
        if (ShouldDeferQuestInventoryApplyNow())
        {
            QueuePendingQuestInventoryChange(questUID, itemSymbol, inInventory, itemDataJson, "ui-paused");
            return;
        }

        // If the quest is momentarily unavailable (late resume/load ordering), keep it.
        if (!TryApplyQuestItemInventoryChangedImmediate(questUID, itemSymbol, inInventory, itemDataJson))
            QueuePendingQuestInventoryChange(questUID, itemSymbol, inInventory, itemDataJson, "quest-not-ready");
    }

    private static bool TryApplyQuestItemInventoryChangedImmediate(ulong questUID, string itemSymbol, bool inInventory, string itemDataJson)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return false;

        if (!AllowQuestInventoryRepair(q, itemSymbol))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Ignored remote inventory-change for non-repair quest item uid={questUID} symbol='{itemSymbol}' inInventory={inInventory}");
            return true; // handled by intentionally dropping it
        }

        if (inInventory &&
            IsTotingQuestItemConsumed(
                questUID,
                itemSymbol))
        {
            Debug.Log(
                $"[QuestNetSync][TotingConsumed] Suppressed stale positive " +
                $"inventory repair uid={questUID} item='{itemSymbol}'");
            return true;
        }

        // A physical DoClick sets HasPlayerClicked and then transfers the item. The
        // inventory notification therefore reaches remote machines before ClickedItem
        // reports its explicit quest event on the next quest tick. Inventory repair
        // must not manufacture that click flag early or vanilla ClickedItem will show
        // its popup once before the explicit event shows it again.
        Item physicalQuestItem = q.GetItem(new Symbol(itemSymbol));
        bool deferPhysicalClickFlag = false;
        if (inInventory && physicalQuestItem != null && HasPhysicalPickupActionForItem(q, itemSymbol) &&
            !_remoteItemClicksApplied.Contains(MakeItemClickApplyKey(questUID, itemSymbol)))
        {
            try { deferPhysicalClickFlag = !physicalQuestItem.HasPlayerClicked; }
            catch { deferPhysicalClickFlag = false; }
        }

        string key = MakeQuestItemInventoryKey(questUID, itemSymbol);
        if (inInventory)
            ProtectPickedQuestItemKey(key);
        else
            RemovePickedQuestItemKey(key);

        // Toting-style quest pickups (e.g. A0C00Y12 corpse book) are stricter than
        // the inventory UI. A directly looted corpse item can look green/correct but
        // still fail the local TotingItemAndClickedNpc check. Always canonicalize these
        // handoff items when the queued/immediate inventory repair finally runs. This is
        // safe for remote receivers too, and reward-window items are still filtered out by
        // AllowQuestInventoryRepair()/HasTotingTaskForItem().
        if (inInventory && HasTotingTaskForItem(q, itemSymbol))
        {
            if (ForceCanonicalSourceTotingQuestItem(q, itemSymbol, null, itemDataJson))
            {
                if (deferPhysicalClickFlag)
                    RestoreInventoryOnlyQuestItemState(q, itemSymbol);
                return true;
            }
        }

        SetQuestItemInventory(q, itemSymbol, inInventory, itemDataJson);
        if (deferPhysicalClickFlag)
            RestoreInventoryOnlyQuestItemState(q, itemSymbol);
        return true;
    }

    private static void RestoreInventoryOnlyQuestItemState(Quest q, string itemSymbol)
    {
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return;

        try
        {
            Item item = q.GetItem(new Symbol(itemSymbol));
            if (item == null)
                return;

            QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
            rsd.hasPlayerClicked = false;
            // The object was physically taken by another player, so it must remain
            // hidden even though the ClickedItem trigger is deliberately deferred.
            rsd.isHidden = true;
            item.RestoreResourceSaveData(rsd);

            if (item.QuestResourceBehaviour != null)
                item.QuestResourceBehaviour.gameObject.SetActive(false);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Deferred remote ClickedItem flag until explicit event uid={q.UID} item='{itemSymbol}'");
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] Failed to defer inventory-only ClickedItem state: " + ex.Message);
        }
    }

    private static string MakePendingQuestInventoryChangeKey(ulong questUID, string itemSymbol)
    {
        return questUID.ToString() + "|pending-quest-inventory|" + (itemSymbol ?? string.Empty);
    }

    private static void QueuePendingQuestInventoryChange(ulong questUID, string itemSymbol, bool inInventory, string itemDataJson, string reason)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        string key = MakePendingQuestInventoryChangeKey(questUID, itemSymbol);
        PendingQuestInventoryChange pending;
        if (!_pendingQuestInventoryChanges.TryGetValue(key, out pending))
        {
            pending = new PendingQuestInventoryChange
            {
                questUID = questUID,
                itemSymbol = itemSymbol,
                queuedAtRealtime = Time.realtimeSinceStartup,
                nextDebugLogRealtime = 0f,
            };
        }

        pending.inInventory = inInventory;
        pending.itemDataJson = itemDataJson ?? string.Empty;

        _pendingQuestInventoryChanges[key] = pending;

        if (Debug.isDebugBuild && Time.realtimeSinceStartup >= pending.nextDebugLogRealtime)
        {
            pending.nextDebugLogRealtime = Time.realtimeSinceStartup + 3f;
            _pendingQuestInventoryChanges[key] = pending;
            Debug.Log($"[QuestNetSync] Queued quest inventory change until UI/time resumes. reason={reason} uid={questUID} symbol='{itemSymbol}' inInventory={inInventory}");
        }
    }

    private static void ProcessPendingQuestInventoryChanges()
    {
        if (_pendingQuestInventoryChanges.Count == 0)
            return;

        if (IsQuestNetSyncPausedForLoad() || ShouldDeferQuestInventoryApplyNow())
            return;

        List<string> keys = new List<string>(_pendingQuestInventoryChanges.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            PendingQuestInventoryChange pending;
            if (!_pendingQuestInventoryChanges.TryGetValue(keys[i], out pending))
                continue;

            if (TryApplyQuestItemInventoryChangedImmediate(pending.questUID, pending.itemSymbol, pending.inInventory, pending.itemDataJson))
            {
                _pendingQuestInventoryChanges.Remove(keys[i]);
                Debug.Log($"[QuestNetSync] Applied queued quest inventory change uid={pending.questUID} symbol='{pending.itemSymbol}' inInventory={pending.inInventory}");
            }
        }
    }

    private static bool ShouldDeferQuestInventoryApplyNow()
    {
        if (IsQuestNetSyncPausedForLoad())
            return true;

        // DFU menus/popups/inventory/loot windows can keep their own live item view.
        // Do not mutate PlayerEntity.Items from a remote RPC while any top window is active.
        try
        {
            if (DaggerfallUI.Instance != null &&
                DaggerfallUI.Instance.UserInterfaceManager != null)
            {
                object topWindow = DaggerfallUI.Instance.UserInterfaceManager.TopWindow;
                if (topWindow != null)
                {
                    // Normal gameplay can have the HUD active. Only defer for real modal/menu windows.
                    object hud = DaggerfallUI.Instance.DaggerfallHUD;
                    if (hud == null || !object.ReferenceEquals(topWindow, hud))
                    {
                        string topTypeName = topWindow.GetType() != null ? topWindow.GetType().Name : string.Empty;
                        if (string.IsNullOrEmpty(topTypeName) || topTypeName.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) < 0)
                            return true;
                    }
                }
            }
        }
        catch { }

        // Some DFU pause states do not always manifest as a TopWindow. Use reflection
        // so this compiles across DFU versions/modded GameManager variants.
        try
        {
            object gm = GameManager.Instance;
            if (gm != null)
            {
                Type t = gm.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo prop = t.GetProperty("PauseGame", flags);
                if (prop != null && prop.PropertyType == typeof(bool) && (bool)prop.GetValue(gm, null))
                    return true;

                FieldInfo field = t.GetField("PauseGame", flags);
                if (field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(gm))
                    return true;
            }
        }
        catch { }

        return false;
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // DroppedItemAtPlace replication
    // ─────────────────────────────────────────────────────────────────────────────
    public static void ReportLocalDroppedItemAtPlace(ulong questUID, string itemSymbol, string placeSymbol, int messageId = 0, string triggerTaskSymbol = null)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (_suppressDroppedItemReportDepth > 0)
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || string.IsNullOrEmpty(placeSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        // The local player has already physically dropped the quest item. Stop protecting
        // this inventory entry and send a dedicated quest-drop side-effect to the others.
        RemovePickedQuestItemKey(MakeQuestItemInventoryKey(questUID, itemSymbol));

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        inst.CmdDroppedItemAtPlace(questUID, itemSymbol, placeSymbol, messageId, triggerTaskSymbol ?? string.Empty, _localNetId);
    }

    [Command]
    private void CmdDroppedItemAtPlace(ulong questUID, string itemSymbol, string placeSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || string.IsNullOrEmpty(placeSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        // Like toting-click replay, let the ClientRpc be the single apply path for all
        // non-source players, including the host client when a remote client is the source.
        RpcDroppedItemAtPlace(questUID, itemSymbol, placeSymbol, messageId, triggerTaskSymbol, sourceNetId);
    }

    [ClientRpc]
    private void RpcDroppedItemAtPlace(ulong questUID, string itemSymbol, string placeSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || string.IsNullOrEmpty(placeSymbol))
            return;

        // Source already completed the physical drop locally.
        if (_localNetId != 0 && sourceNetId == _localNetId)
            return;

        ApplyRemoteDroppedItemAtPlace(questUID, itemSymbol, placeSymbol, messageId, triggerTaskSymbol, sourceNetId);
    }

    private static void ApplyRemoteDroppedItemAtPlace(ulong questUID, string itemSymbol, string placeSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        Item item = q.GetItem(new Symbol(itemSymbol));
        if (item == null)
            return;

        // The source machine already verified the correct Place with place.IsPlayerHere()
        // inside DroppedItemAtPlace.CheckTrigger(). The remote machine might not be in
        // that building/interior, so do not require place.IsPlayerHere() here.
        if (q.GetPlace(new Symbol(placeSymbol)) == null)
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(questUID, "remote-dropped-item");

        _suppressDroppedItemReportDepth++;
        try
        {
            // Mirror the important side effects of dropping the quest item:
            // remove the synced inventory copy, mark the quest Item as dropped, and
            // start the same task so actions like "clear _S.15_" can run locally.
            SetQuestItemInventory(q, itemSymbol, false);

            try
            {
                item.AllowDrop = false;
                item.PlayerDropped = true;
            }
            catch { }

            if (messageId != 0)
                q.ShowMessagePopup(messageId);

            if (!string.IsNullOrEmpty(triggerTaskSymbol))
            {
                q.StartTask(new Symbol(triggerTaskSymbol));
                ReassertClientQuestChainAuthorityAfterTaskState(q, "remote-dropped-item");
            }

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Applied remote dropped item uid={questUID} item='{itemSymbol}' place='{placeSymbol}' task='{triggerTaskSymbol}' msg={messageId} source={sourceNetId}");
        }
        finally
        {
            _suppressDroppedItemReportDepth--;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TotingItemAndClickedNpc replication
    // ─────────────────────────────────────────────────────────────────────────────
    private static string MakeTotingClickApplyKey(ulong questUID, string itemSymbol, string personSymbol)
    {
        return questUID.ToString() + "|toting-click-applied|" + (itemSymbol ?? string.Empty) + "|" + (personSymbol ?? string.Empty);
    }

    private static bool TaskMatchesTotingInteraction(
        Quest q,
        string triggerTaskSymbol,
        string itemSymbol,
        string personSymbol)
    {
        if (q == null ||
            string.IsNullOrEmpty(triggerTaskSymbol) ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(personSymbol))
            return false;

        DaggerfallWorkshop.Game.Questing.Task task =
            q.GetTask(new Symbol(triggerTaskSymbol));
        if (task == null || task.Actions == null)
            return false;

        foreach (IQuestAction action in task.Actions)
        {
            if (action == null ||
                !string.Equals(
                    action.GetType().Name,
                    "TotingItemAndClickedNpc",
                    StringComparison.Ordinal))
                continue;

            try
            {
                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.Public;

                FieldInfo itemField =
                    action.GetType().GetField(
                        "itemSymbol",
                        flags);
                FieldInfo personField =
                    action.GetType().GetField(
                        "npcSymbol",
                        flags);

                string actionItem =
                    itemField != null
                        ? GetSymbolName(itemField.GetValue(action))
                        : string.Empty;
                string actionPerson =
                    personField != null
                        ? GetSymbolName(personField.GetValue(action))
                        : string.Empty;

                if (string.Equals(
                        actionItem,
                        itemSymbol,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        actionPerson,
                        personSymbol,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static bool QuestMatchesTotingInteraction(
        Quest q,
        string questName,
        string itemSymbol,
        string personSymbol,
        string triggerTaskSymbol)
    {
        if (q == null ||
            q.QuestComplete ||
            q.QuestTombstoned ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(personSymbol) ||
            string.IsNullOrEmpty(triggerTaskSymbol))
            return false;

        if (!string.IsNullOrEmpty(questName) &&
            !string.Equals(
                q.QuestName,
                questName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (q.GetItem(new Symbol(itemSymbol)) == null ||
                q.GetPerson(new Symbol(personSymbol)) == null)
                return false;
        }
        catch
        {
            return false;
        }

        return TaskMatchesTotingInteraction(
            q,
            triggerTaskSymbol,
            itemSymbol,
            personSymbol);
    }

    private static bool TryResolveQuestForTotingInteraction(
        string instanceId,
        ulong remoteQuestUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        string triggerTaskSymbol,
        out ulong localQuestUID)
    {
        localQuestUID = 0UL;
        if (QuestMachine.Instance == null)
            return false;

        // Normal live path: use the shared runtime instance mapping.
        ulong mappedUID;
        if (TryResolveLocalQuestUidForInstance(
                instanceId,
                remoteQuestUID,
                out mappedUID))
        {
            Quest mappedQuest =
                QuestMachine.Instance.GetQuest(mappedUID);
            if (QuestMatchesTotingInteraction(
                    mappedQuest,
                    questName,
                    itemSymbol,
                    personSymbol,
                    triggerTaskSymbol))
            {
                localQuestUID = mappedUID;
                return true;
            }
        }

        // Safe-resume/load path: the same quest can still exist under the packet UID
        // even when its runtime instance map has just been rebuilt.
        if (remoteQuestUID != 0UL)
        {
            Quest sameUIDQuest =
                QuestMachine.Instance.GetQuest(remoteQuestUID);
            if (QuestMatchesTotingInteraction(
                    sameUIDQuest,
                    questName,
                    itemSymbol,
                    personSymbol,
                    triggerTaskSymbol))
            {
                localQuestUID = remoteQuestUID;
                return true;
            }
        }

        // UID-collision-safe imports can use a different local UID. Fall back by
        // template only when exactly one active quest also contains the exact item,
        // person, and owning TotingItemAndClickedNpc task. Never guess between two
        // otherwise-identical live quest instances.
        if (!string.IsNullOrEmpty(questName))
        {
            ulong[] candidates =
                QuestMachine.Instance.FindQuests(
                    questName,
                    true);

            ulong uniqueUID = 0UL;
            int matchCount = 0;

            if (candidates != null)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    Quest candidate =
                        QuestMachine.Instance.GetQuest(
                            candidates[i]);
                    if (!QuestMatchesTotingInteraction(
                            candidate,
                            questName,
                            itemSymbol,
                            personSymbol,
                            triggerTaskSymbol))
                        continue;

                    uniqueUID = candidate.UID;
                    matchCount++;
                    if (matchCount > 1)
                        break;
                }
            }

            if (matchCount == 1)
            {
                localQuestUID = uniqueUID;
                return true;
            }
        }

        return false;
    }

    private static string MakePendingTotingClickKey(
        string instanceId,
        ulong remoteQuestUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        string questKey =
            !string.IsNullOrEmpty(instanceId)
                ? "inst=" + instanceId
                : "uid=" + remoteQuestUID.ToString() +
                  ":name=" + (questName ?? string.Empty);

        return questKey +
            ":pending-toting:item=" + (itemSymbol ?? string.Empty) +
            ":person=" + (personSymbol ?? string.Empty) +
            ":task=" + (triggerTaskSymbol ?? string.Empty) +
            ":source=" + sourceNetId.ToString();
    }

    private static void QueuePendingTotingClickPacket(
        string instanceId,
        ulong remoteQuestUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        string key =
            MakePendingTotingClickKey(
                instanceId,
                remoteQuestUID,
                questName,
                itemSymbol,
                personSymbol,
                triggerTaskSymbol,
                sourceNetId);

        if (_pendingTotingClickPackets.ContainsKey(key))
            return;

        _pendingTotingClickPackets[key] =
            new PendingTotingClickPacket
            {
                instanceId = instanceId ?? string.Empty,
                remoteQuestUid = remoteQuestUID,
                questName = questName ?? string.Empty,
                itemSymbol = itemSymbol ?? string.Empty,
                personSymbol = personSymbol ?? string.Empty,
                messageId = messageId,
                triggerTaskSymbol = triggerTaskSymbol ?? string.Empty,
                sourceNetId = sourceNetId,
            };

        Debug.Log(
            $"[QuestNetSync][TotingResolve] Queued unresolved exact interaction " +
            $"inst='{instanceId}' remoteUid={remoteQuestUID} quest='{questName}' " +
            $"item='{itemSymbol}' person='{personSymbol}' task='{triggerTaskSymbol}' " +
            $"source={sourceNetId}");
    }

    private static bool TryApplyTotingClickPacketNow(
        string instanceId,
        ulong remoteQuestUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            QuestMachine.Instance == null)
            return false;

        ulong localQuestUID;
        if (!TryResolveQuestForTotingInteraction(
                instanceId,
                remoteQuestUID,
                questName,
                itemSymbol,
                personSymbol,
                triggerTaskSymbol,
                out localQuestUID))
            return false;

        Quest localQuest =
            QuestMachine.Instance.GetQuest(localQuestUID);
        if (localQuest == null)
            return false;

        RegisterSharedTotingPromptContext(
            localQuest,
            triggerTaskSymbol,
            sourceNetId,
            false);

        ApplyRemoteTotingItemAndPersonClicked(
            localQuestUID,
            itemSymbol,
            personSymbol,
            messageId,
            triggerTaskSymbol,
            sourceNetId);

        Debug.Log(
            $"[QuestNetSync][TotingResolve] Applied exact interaction " +
            $"remoteUid={remoteQuestUID} localUid={localQuestUID} quest='{localQuest.QuestName}' " +
            $"item='{itemSymbol}' person='{personSymbol}' task='{triggerTaskSymbol}' " +
            $"source={sourceNetId}");
        return true;
    }

    private void ProcessPendingTotingClickPackets()
    {
        if (_pendingTotingClickPackets.Count == 0 ||
            IsQuestNetSyncPausedForLoad() ||
            QuestMachine.Instance == null)
            return;

        string[] keys =
            _pendingTotingClickPackets.Keys.ToArray();

        for (int i = 0; i < keys.Length; i++)
        {
            PendingTotingClickPacket pending;
            if (!_pendingTotingClickPackets.TryGetValue(
                    keys[i],
                    out pending) ||
                pending == null)
                continue;

            if (!TryApplyTotingClickPacketNow(
                    pending.instanceId,
                    pending.remoteQuestUid,
                    pending.questName,
                    pending.itemSymbol,
                    pending.personSymbol,
                    pending.messageId,
                    pending.triggerTaskSymbol,
                    pending.sourceNetId))
                continue;

            _pendingTotingClickPackets.Remove(keys[i]);
        }
    }

    public static void ReportLocalTotingItemAndPersonClicked(ulong questUID, string itemSymbol, string personSymbol, int messageId = 0, string triggerTaskSymbol = null)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (_suppressPersonClickReportDepth > 0)
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol) || string.IsNullOrEmpty(personSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest localQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUID)
                : null;

        // Defensive echo fence: if this callback somehow runs from a network-applied
        // generic Person click that is reserved for a different ClickedNpc task, it is
        // not a new physical toting interaction. Do not consume/reoffer the item and,
        // most importantly, do not send a second Command back to the server.
        if (IsRemotePersonClickReservedForDifferentTask(
                localQuest,
                personSymbol,
                triggerTaskSymbol ?? string.Empty))
        {
            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][PersonClickOwnership] Suppressed synthetic toting report " +
                    $"uid={questUID} item='{itemSymbol}' person='{personSymbol}' " +
                    $"task='{triggerTaskSymbol}'");
            }
            return;
        }

        bool reoffersSameItem =
            localQuest != null &&
            TriggerTaskReoffersTotingItemAsReward(
                localQuest,
                triggerTaskSymbol ?? string.Empty,
                itemSymbol);

        // Local turn-in consumes the item. Record that edge so a later passive
        // snapshot from another participant cannot resurrect it.
        if (!reoffersSameItem)
        {
            MarkTotingQuestItemConsumed(
                questUID,
                itemSymbol,
                "local-toting-turn-in");
        }
        else
        {
            RemovePickedQuestItemKey(
                MakeQuestItemInventoryKey(
                    questUID,
                    itemSymbol));
        }

        // Clear stale toting-click guard but do not swallow a real local turn-in.
        // After loading an older save in the same process, this guard can still exist
        // from the previously completed run.
        string appliedKey = MakeTotingClickApplyKey(questUID, itemSymbol, personSymbol);
        _remotePersonClicksApplied.Remove(appliedKey);

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        RegisterSharedTotingPromptContext(
            localQuest,
            triggerTaskSymbol ?? string.Empty,
            _localNetId,
            true);

        inst.CmdTotingItemAndPersonClicked(
            GetLocalQuestInstanceId(questUID),
            questUID,
            localQuest != null ? (localQuest.QuestName ?? string.Empty) : string.Empty,
            itemSymbol,
            personSymbol,
            messageId,
            triggerTaskSymbol ?? string.Empty,
            _localNetId);
    }

    [Command]
    private void CmdTotingItemAndPersonClicked(
        string instanceId,
        ulong questUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;
        if (!isServer ||
            questUID == 0UL ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(personSymbol) ||
            string.IsNullOrEmpty(triggerTaskSymbol))
            return;

        ulong serverQuestUID;
        if (!TryResolveQuestForTotingInteraction(
                instanceId,
                questUID,
                questName,
                itemSymbol,
                personSymbol,
                triggerTaskSymbol,
                out serverQuestUID))
        {
            Debug.LogWarning(
                $"[QuestNetSync][TotingResolve] Server rejected unresolved exact interaction " +
                $"inst='{instanceId}' remoteUid={questUID} quest='{questName}' " +
                $"item='{itemSymbol}' person='{personSymbol}' task='{triggerTaskSymbol}' " +
                $"source={sourceNetId}");
            return;
        }

        Quest serverQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(serverQuestUID)
                : null;
        if (serverQuest == null ||
            IsQuestSharingBlacklistedUid(serverQuestUID))
            return;

        // ClientRpc delivery is observer-based in Mirror. This Command runs on the
        // source client's QuestNetSync object, so a third player can miss the event
        // when that player is not observing the source player object. Deliver through
        // every recipient's own QuestNetSync object instead.
        ServerBroadcastTotingItemAndPersonClicked(
            connectionToClient,
            instanceId,
            serverQuestUID,
            serverQuest.QuestName ?? questName ?? string.Empty,
            itemSymbol,
            personSymbol,
            messageId,
            triggerTaskSymbol,
            sourceNetId);
    }

    private static void ServerBroadcastTotingItemAndPersonClicked(
        NetworkConnection sourceConnection,
        string instanceId,
        ulong questUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        int connectedCount = 0;
        int sentCount = 0;
        int missingIdentityCount = 0;
        int missingSyncCount = 0;

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection recipient = entry.Value;
            if (recipient == null)
                continue;

            connectedCount++;

            if (recipient == sourceConnection)
                continue;

            NetworkIdentity recipientIdentity = recipient.identity;
            if (recipientIdentity == null)
            {
                missingIdentityCount++;
                continue;
            }

            QuestNetSync recipientSync =
                recipientIdentity.GetComponent<QuestNetSync>();
            if (recipientSync == null)
            {
                recipientSync =
                    recipientIdentity.GetComponentInChildren<QuestNetSync>(true);
            }

            if (recipientSync == null || !recipientSync.isServer)
            {
                missingSyncCount++;
                continue;
            }

            recipientSync.TargetTotingItemAndPersonClicked(
                recipient,
                instanceId,
                questUID,
                questName,
                itemSymbol,
                personSymbol,
                messageId,
                triggerTaskSymbol,
                sourceNetId);
            sentCount++;
        }

        Debug.Log(
            $"[QuestNetSync][TotingFanout] Sent turn-in " +
            $"uid={questUID} quest='{questName}' item='{itemSymbol}' person='{personSymbol}' " +
            $"task='{triggerTaskSymbol}' connected={connectedCount} " +
            $"recipients={sentCount} missingIdentity={missingIdentityCount} " +
            $"missingSync={missingSyncCount} source={sourceNetId}");
    }

    [TargetRpc]
    private void TargetTotingItemAndPersonClicked(
        NetworkConnection target,
        string instanceId,
        ulong questUID,
        string questName,
        string itemSymbol,
        string personSymbol,
        int messageId,
        string triggerTaskSymbol,
        uint sourceNetId)
    {
        if (!isClient ||
            questUID == 0UL ||
            string.IsNullOrEmpty(itemSymbol) ||
            string.IsNullOrEmpty(personSymbol) ||
            string.IsNullOrEmpty(triggerTaskSymbol))
            return;

        if (TryApplyTotingClickPacketNow(
                instanceId,
                questUID,
                questName,
                itemSymbol,
                personSymbol,
                messageId,
                triggerTaskSymbol,
                sourceNetId))
            return;

        // Do not silently discard a real player interaction just because this
        // participant has not rebuilt its instance -> local quest mapping yet.
        // Retry every local Update until the exact quest identity is available.
        QueuePendingTotingClickPacket(
            instanceId,
            questUID,
            questName,
            itemSymbol,
            personSymbol,
            messageId,
            triggerTaskSymbol,
            sourceNetId);
    }


    private static Dictionary<string, bool> BuildRawTaskStateMap(
        Quest.TaskState[] states)
    {
        Dictionary<string, bool> result =
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);

        if (states == null)
            return result;

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null ||
                string.IsNullOrEmpty(states[i].symbol.Name))
                continue;

            result[states[i].symbol.Name] = states[i].set;
        }

        return result;
    }

    private static HashSet<string> GetTotingItemSymbolsFromTask(
        DaggerfallWorkshop.Game.Questing.Task task)
    {
        HashSet<string> result =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        if (task == null || task.Actions == null)
            return result;

        foreach (IQuestAction action in task.Actions)
        {
            if (action == null ||
                !string.Equals(
                    action.GetType().Name,
                    "TotingItemAndClickedNpc",
                    StringComparison.Ordinal))
                continue;

            FieldInfo itemField = action.GetType().GetField(
                "itemSymbol",
                BindingFlags.Instance |
                BindingFlags.NonPublic |
                BindingFlags.Public);
            if (itemField == null)
                continue;

            string itemSymbol =
                GetSymbolName(itemField.GetValue(action));
            if (!string.IsNullOrEmpty(itemSymbol))
                result.Add(itemSymbol);
        }

        return result;
    }

    private void BroadcastServerTotingInventoryRemovals(
        Quest q,
        Quest.TaskState[] previousTasks,
        Quest.TaskState[] currentTasks,
        Dictionary<string, ItemState> previousItems,
        Dictionary<string, ItemState> currentItems)
    {
        if (!isServer ||
            q == null ||
            previousTasks == null ||
            currentTasks == null ||
            previousItems == null ||
            currentItems == null)
            return;

        Dictionary<string, bool> oldTaskState =
            BuildRawTaskStateMap(previousTasks);

        for (int i = 0; i < currentTasks.Length; i++)
        {
            Quest.TaskState state = currentTasks[i];
            if (state.symbol == null ||
                string.IsNullOrEmpty(state.symbol.Name) ||
                !state.set)
                continue;

            bool wasSet;
            if (oldTaskState.TryGetValue(
                    state.symbol.Name,
                    out wasSet) &&
                wasSet)
                continue;

            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(state.symbol);
            HashSet<string> totingItems =
                GetTotingItemSymbolsFromTask(task);
            if (totingItems.Count == 0)
                continue;

            foreach (string itemSymbol in totingItems)
            {
                ItemState before;
                ItemState after;
                bool hadBefore =
                    previousItems.TryGetValue(
                        itemSymbol,
                        out before) &&
                    before.inPlayerInventory;
                bool hasAfter =
                    currentItems.TryGetValue(
                        itemSymbol,
                        out after) &&
                    after.inPlayerInventory;

                if (!hadBefore || hasAfter)
                    continue;

                // S0000012 deliberately gives the same carried item back as its
                // permanent reward. Its dedicated replay path owns that special case;
                // do not broadcast a generic removal after the reward was created.
                if (TriggerTaskReoffersTotingItemAsReward(
                        q,
                        state.symbol.Name,
                        itemSymbol))
                    continue;

                RemovePickedQuestItemKey(
                    MakeQuestItemInventoryKey(
                        q.UID,
                        itemSymbol));

                // This is an authoritative server-side removal edge, not passive
                // ItemDTO state. Use the existing explicit inventory RPC so every
                // remote participant removes the exact quest UID + item symbol.
                // sourceNetId=0 intentionally means no pure client skips the update.
                NetworkConnection hostConnection =
                    GetServerHostLocalConnection();
                ServerBroadcastQuestItemInventoryChanged(
                    hostConnection,
                    hostConnection,
                    q.UID,
                    itemSymbol,
                    false,
                    string.Empty,
                    0U,
                    true);

                Debug.Log(
                    $"[QuestNetSync][TotingRemoval] Server broadcast consumed " +
                    $"quest item uid={q.UID} quest='{q.QuestName}' " +
                    $"task='{state.symbol.Name}' item='{itemSymbol}'");
            }
        }
    }

    private static bool TriggerTaskReoffersTotingItemAsReward(
        Quest q,
        string triggerTaskSymbol,
        string itemSymbol)
    {
        if (q == null || string.IsNullOrEmpty(triggerTaskSymbol) || string.IsNullOrEmpty(itemSymbol))
            return false;

        DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(triggerTaskSymbol));
        if (task == null || task.Actions == null)
            return false;

        foreach (IQuestAction action in task.Actions)
        {
            if (action == null || !string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal))
                continue;

            FieldInfo itemField = action.GetType().GetField(
                "itemSymbol",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (itemField == null)
                continue;

            string rewardItemSymbol = GetSymbolName(itemField.GetValue(action));
            if (string.Equals(rewardItemSymbol, itemSymbol, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool SeparateSameItemRewardPrototypeFromCarriedCopy(
        Quest q,
        string itemSymbol)
    {
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return false;

        try
        {
            var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
            if (pe == null || pe.Items == null)
                return false;

            Item questItem = q.GetItem(new Symbol(itemSymbol));
            if (questItem == null || questItem.DaggerfallUnityItem == null)
                return false;

            DaggerfallUnityItem currentPrototype = questItem.DaggerfallUnityItem;
            bool prototypeIsCarried = false;
            for (int i = 0; i < pe.Items.Count; i++)
            {
                if (object.ReferenceEquals(pe.Items.GetItem(i), currentPrototype))
                {
                    prototypeIsCarried = true;
                    break;
                }
            }

            // Normal DFU invariant: the Questing.Item prototype and the carried quest
            // item are different objects. GivePc makes the prototype permanent, removes
            // the carried copy, then offers the prototype in its reward container.
            // QuestNetSync's inventory repair deliberately binds the prototype to the
            // live carried object, which breaks ReleaseQuestItemForReoffer() for quests
            // that give the same item back as the reward (S0000012). Restore the normal
            // two-object layout immediately before that task executes.
            if (!prototypeIsCarried)
                return true;

            DaggerfallUnityItem rewardPrototype =
                new DaggerfallUnityItem(currentPrototype.GetSaveData());
            rewardPrototype.stackCount = NormalizeQuestItemStackCount(rewardPrototype);
            rewardPrototype.LinkQuestItem(
                q.UID,
                questItem.Symbol != null ? questItem.Symbol.Clone() : new Symbol(itemSymbol));
            TryAssignFreshItemUid(rewardPrototype);

            FieldInfo itemField = typeof(Item).GetField(
                "item",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (itemField == null)
                return false;

            itemField.SetValue(questItem, rewardPrototype);

            Debug.Log(
                $"[QuestNetSync][TotingRewardFix] Separated carried item from reward prototype " +
                $"uid={q.UID} item='{itemSymbol}' carriedUid={currentPrototype.UID} " +
                $"rewardUid={rewardPrototype.UID}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][TotingRewardFix] Failed to separate reward prototype " +
                $"uid={(q != null ? q.UID : 0UL)} item='{itemSymbol}': {ex.Message}");
            return false;
        }
    }

    private static void ApplyRemoteTotingItemAndPersonClicked(ulong questUID, string itemSymbol, string personSymbol, int messageId, string triggerTaskSymbol, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        Person p = q.GetPerson(new Symbol(personSymbol));
        if (p == null)
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(questUID, "remote-toting-click");

        // Do not use a persistent "already applied" guard for toting clicks.
        // Loading an older save in the same process can reuse the same quest UID +
        // item + person key, and that stale key was skipping the next real turn-in
        // popup on whichever player happened to be the remote recipient before load.
        // The source player is already skipped by RpcTotingItemAndPersonClicked(),
        // and CmdTotingItemAndPersonClicked() no longer applies before the Rpc, so
        // each network event is naturally applied once per non-source player.

        bool reoffersTotingItemAsReward =
            TriggerTaskReoffersTotingItemAsReward(q, triggerTaskSymbol, itemSymbol);

        _suppressPersonClickReportDepth++;
        try
        {
            // Make the remote machine temporarily carry the item for any branch actions
            // that inspect it, then explicitly start the exact trigger task reported by
            // TotingItemAndClickedNpc. Do NOT synthesize Person.SetPlayerClicked() here:
            // the same Person can own unrelated ClickedNpc tasks later in the quest.
            SetQuestItemInventory(q, itemSymbol, true);

            if (!reoffersTotingItemAsReward)
            {
                MarkTotingQuestItemConsumed(
                    questUID,
                    itemSymbol,
                    "remote-toting-turn-in");
            }

            if (reoffersTotingItemAsReward &&
                !SeparateSameItemRewardPrototypeFromCarriedCopy(q, itemSymbol))
            {
                Debug.LogWarning(
                    $"[QuestNetSync][TotingRewardFix] Same-item reward preparation failed " +
                    $"uid={questUID} task='{triggerTaskSymbol}' item='{itemSymbol}'");
            }

            // The exact trigger task below is authoritative for this network turn-in.
            // Person.HasPlayerClicked remains a local physical-input flag only.

            // Toting-click replay is an explicit one-shot network event. Show the
            // popup for this event directly instead of caching it in the generic
            // person-click suppression table. That table is useful for ClickedNpc
            // echo suppression, but it is exactly what caused reloads to randomly
            // suppress the wrong player's Defamation hand-in popup.
            if (messageId != 0)
                q.ShowMessagePopup(messageId);

            if (!string.IsNullOrEmpty(triggerTaskSymbol))
            {
                q.StartTask(new Symbol(triggerTaskSymbol));
                ReassertClientQuestChainAuthorityAfterTaskState(q, "remote-toting-click");
            }

            // Most toting turn-ins consume the carried letter/book. S0000012 is
            // different: its final task gives the SAME _jewelry_ item back as the
            // permanent reward. Do not delete that reward after GivePc has created it.
            if (!reoffersTotingItemAsReward)
            {
                SetQuestItemInventory(q, itemSymbol, false);
            }
            else
            {
                Debug.Log(
                    $"[QuestNetSync][TotingRewardFix] Completed same-item reward path " +
                    $"uid={questUID} task='{triggerTaskSymbol}' item='{itemSymbol}'");
            }

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Applied remote toting click uid={questUID} item='{itemSymbol}' person='{personSymbol}' task='{triggerTaskSymbol}' msg={messageId} source={sourceNetId}");
        }
        finally
        {
            _suppressPersonClickReportDepth--;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // RevealLocation + PcAt side-effect replication
    // ─────────────────────────────────────────────────────────────────────────────
    public static void ReportLocalLocationRevealed(ulong questUID, string placeSymbol, bool readMap)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(placeSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        // Clear any stale guard left by an earlier save/load, but do not swallow
        // this real local reveal report.
        string key = MakeLocationRevealKey(questUID, placeSymbol);
        _remoteLocationRevealsApplied.Remove(key);

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        inst.CmdLocationRevealed(questUID, placeSymbol, readMap, _localNetId);
    }

    [Command]
    private void CmdLocationRevealed(ulong questUID, string placeSymbol, bool readMap, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(placeSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        if (_localNetId == 0 || sourceNetId != _localNetId)
            ApplyRemoteLocationReveal(questUID, placeSymbol, readMap, sourceNetId);

        RpcLocationRevealed(questUID, placeSymbol, readMap, sourceNetId);
    }

    [ClientRpc]
    private void RpcLocationRevealed(ulong questUID, string placeSymbol, bool readMap, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(placeSymbol))
            return;

        if (_localNetId != 0 && sourceNetId == _localNetId)
            return;

        ApplyRemoteLocationReveal(questUID, placeSymbol, readMap, sourceNetId);
    }

    private static void ApplyRemoteLocationReveal(ulong questUID, string placeSymbol, bool readMap, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        Place place = q.GetPlace(new Symbol(placeSymbol));
        if (place == null)
            return;

        // Do not suppress location reveal by a static guard. DiscoverLocation is
        // idempotent, while the guard survives save/load and can block reveal forever.
        string key = MakeLocationRevealKey(questUID, placeSymbol);
        _remoteLocationRevealsApplied.Remove(key);

        _remoteLocationRevealsApplied.Add(key);

        GameManager.Instance.PlayerGPS.DiscoverLocation(place.SiteDetails.regionName, place.SiteDetails.locationName);

        if (readMap)
            GameManager.Instance.PlayerEntity.Notebook.AddNote(
                TextManager.Instance.GetLocalizedText("readMap").Replace("%map", place.SiteDetails.locationName));

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] Applied remote location reveal uid={questUID} place='{placeSymbol}' source={sourceNetId}");
    }

    public static void ReportLocalPcAtTriggered(ulong questUID, string taskSymbol, int messageId = 0)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (_suppressPcAtReportDepth > 0)
            return;

        if (questUID == 0UL || string.IsNullOrEmpty(taskSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest localQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUID)
                : null;
        if (IsPureLocalPcAtSensorTask(
                localQuest,
                taskSymbol))
        {
            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][PcAtTraffic] Kept pure PcAt sensor local " +
                    $"uid={questUID} quest='{localQuest.QuestName}' " +
                    $"task='{taskSymbol}'");
            }
            return;
        }

        // Do not suppress local PcAt reports based on previous remote PcAt state.
        // PcAt is player-position driven and save/load can roll the quest back to
        // before this trigger while static HashSets remain alive in the process.
        // A duplicate StartTask on the remote side is harmless; a stale suppression
        // breaks quest completion until full relaunch.
        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        // PcAt triggers are persistent quest state (e.g. "pc at _inn_ set _S.00_ saying 1013").
        // Report each quest/task once per loaded quest. Otherwise every local/remote
        // entry can replay the same popup and StartTask back and forth. This guard is
        // cleared by load hygiene and per-quest reset.
        string key = MakePcAtKey(questUID, taskSymbol);
        if (_remotePcAtApplied.Contains(key))
            return;
        _remotePcAtApplied.Add(key);

        inst.CmdPcAtTriggered(questUID, taskSymbol, messageId, _localNetId);
    }

    [Command]
    private void CmdPcAtTriggered(ulong questUID, string taskSymbol, int messageId, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(taskSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest serverQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUID)
                : null;
        if (IsPureLocalPcAtSensorTask(
                serverQuest,
                taskSymbol))
            return;

        if (_localNetId == 0 || sourceNetId != _localNetId)
            ApplyRemotePcAtTriggered(questUID, taskSymbol, messageId, sourceNetId);

        RpcPcAtTriggered(questUID, taskSymbol, messageId, sourceNetId);
    }

    [ClientRpc]
    private void RpcPcAtTriggered(ulong questUID, string taskSymbol, int messageId, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(taskSymbol))
            return;

        if (_localNetId != 0 && sourceNetId == _localNetId)
            return;

        ApplyRemotePcAtTriggered(questUID, taskSymbol, messageId, sourceNetId);
    }

    private static void ApplyRemotePcAtTriggered(ulong questUID, string taskSymbol, int messageId, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        if (IsPureLocalPcAtSensorTask(q, taskSymbol))
            return;

        SuppressClientQuestEndReportFromRemoteTrigger(questUID, "remote-pcat");

        // PcAt triggers are one-shot persistent quest state. Do not replay the same
        // remote PcAt popup/task every time either player enters the same site.
        string key = MakePcAtKey(questUID, taskSymbol);
        if (_remotePcAtApplied.Contains(key))
            return;
        _remotePcAtApplied.Add(key);

        _suppressPcAtReportDepth++;
        try
        {
            if (messageId != 0)
                q.ShowMessagePopup(messageId);

            q.StartTask(new Symbol(taskSymbol));
            ReassertClientQuestChainAuthorityAfterTaskState(q, "remote-pcat");

            // If this PcAt task is the final reward/end task, replay GivePc immediately
            // instead of requiring the other player to physically enter the same house.
            ForceReplayRewardTasksIfNeeded(q, new string[] { taskSymbol }, true);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Applied remote PcAt trigger uid={questUID} task='{taskSymbol}' msg={messageId} source={sourceNetId}");
        }
        finally
        {
            _suppressPcAtReportDepth--;
        }
    }

    public static void ReportLocalEscortFaceAdded(ulong questUID, string personSymbol, string foeSymbol, int sayingId)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (questUID == 0UL)
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        string key = MakeEscortFaceKey(questUID, personSymbol, foeSymbol);
        if (_remoteEscortFacesApplied.Contains(key))
            return;

        // Mark local action as handled too, so a rearmed/echoed task cannot spam faces.
        _remoteEscortFacesApplied.Add(key);

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        inst.CmdEscortFaceAdded(questUID, personSymbol ?? string.Empty, foeSymbol ?? string.Empty, sayingId, _localNetId);
    }

    [Command]
    private void CmdEscortFaceAdded(ulong questUID, string personSymbol, string foeSymbol, int sayingId, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer || questUID == 0UL)
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        ApplyRemoteEscortFace(questUID, personSymbol, foeSymbol, sayingId, sourceNetId);
        RpcEscortFaceAdded(questUID, personSymbol, foeSymbol, sayingId, sourceNetId);
    }

    [ClientRpc]
    private void RpcEscortFaceAdded(ulong questUID, string personSymbol, string foeSymbol, int sayingId, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClient || questUID == 0UL)
            return;

        if (_localNetId != 0 && sourceNetId == _localNetId)
            return;

        ApplyRemoteEscortFace(questUID, personSymbol, foeSymbol, sayingId, sourceNetId);
    }

    private static void ApplyRemoteEscortFace(ulong questUID, string personSymbol, string foeSymbol, int sayingId, uint sourceNetId)
    {
        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        string key = MakeEscortFaceKey(questUID, personSymbol, foeSymbol);
        if (_remoteEscortFacesApplied.Contains(key))
            return;

        _remoteEscortFacesApplied.Add(key);

        if (sayingId != 0)
            q.ShowMessagePopup(sayingId, true);

        if (!string.IsNullOrEmpty(personSymbol))
        {
            Person person = q.GetPerson(new Symbol(personSymbol));
            if (person != null && DaggerfallUI.Instance != null && DaggerfallUI.Instance.DaggerfallHUD != null)
                DaggerfallUI.Instance.DaggerfallHUD.EscortingFaces.AddFace(person);
        }
        else if (!string.IsNullOrEmpty(foeSymbol))
        {
            Foe foe = q.GetFoe(new Symbol(foeSymbol));
            if (foe != null && DaggerfallUI.Instance != null && DaggerfallUI.Instance.DaggerfallHUD != null)
                DaggerfallUI.Instance.DaggerfallHUD.EscortingFaces.AddFace(foe);
        }

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] Applied remote escort face uid={questUID} person='{personSymbol}' foe='{foeSymbol}' saying={sayingId} source={sourceNetId}");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CLIENT → SERVER (report local)
    // ─────────────────────────────────────────────────────────────────────────────
    private void Cli_OnQuestStarted(Quest q)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (q == null) return;

        if (IsQuestSharingBlacklisted(q))
        {
            LogQuestSharingBlacklisted(q, "Cli_OnQuestStarted");
            return;
        }

        // Clear random-delivery selection/replication guards for reused quest UIDs.
        ResetRandomDeliveryForQuest(q.UID);
        _suppressClientQuestEndReportUids.Remove(q.UID);
        _clientQuestEndReportedUids.Remove(q.UID);

        // Even when this quest was started by a local DFU parent action, every other
        // matching parent branch must now regard the child as already started.
        AcknowledgeNetworkQuestStartInLocalParents(q, "client-quest-start");

if (!isClientOnly) return;
        MarkQuestLocalStarted(q.UID);

        // hard echo suppression while applying network start
        if (_suppressStartDepth > 0) return;

        // legacy one-shot by name
        if (_suppressStartByName.Remove(q.QuestName)) return;

        // NEW: client generates quest locally (correct area/context) and sends the full start state to the server.
        // Server will reconstruct and rebroadcast to everyone, including this client (which will just bind).
        string inst = Guid.NewGuid().ToString("N");

        // Pre-register mapping so our own RpcStart echo never triggers a local StartQuest.
        _cliInst2Uid[inst] = q.UID;
        _cliUid2Inst[q.UID] = inst;
        _cliLastTasks[inst] = q.GetTaskStates();
        _cliLastLogs[inst]  = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _cliLastItems[inst] = CaptureItemStates(q);
        _cliLastPersons[inst] = BuildPersons(q);
        _cliLastFoes[inst] = BuildFoes(q);
        _cliQuestObjectByUid[q.UID] = q;

        StartCoroutine(Cli_SendStartPacketAfterDelay(q, inst));
    }

private void Cli_OnQuestEnded(Quest q)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClientOnly || q == null) return;
        if (IsQuestSharingBlacklisted(q)) return;

        // The local client can finish a quest on the same tick that a triggered
        // MakePermanent owner task becomes true. Complete that durable side effect
        // before any MP end cleanup or final-state capture.
        ApplyTriggeredMakePermanentActionsForEndingQuest(
            q,
            null,
            "client-local-end");

        // DFU normally removes loaded scene resources when a quest ends. A remote or
        // deferred client path can tombstone first and skip that final scene pass.
        // Clean only objects owned by this ended local quest UID; child quests use
        // different UIDs and resources and are therefore untouched.
        DisableActiveQuestResourceObjectsForEndedQuest(q.UID);
        _sharedTotingPromptContexts.Remove(q.UID);
        string endedPromptPrefix = q.UID.ToString() + "|prompt-choice-say|";
        _promptChoiceSayBypassTasks.RemoveWhere(
            key => key.StartsWith(
                endedPromptPrefix,
                StringComparison.OrdinalIgnoreCase));

        float catchupSuppressUntil;
        if (_clientCatchupEndSuppressUntil.TryGetValue(q.UID, out catchupSuppressUntil))
        {
            _clientCatchupEndSuppressUntil.Remove(q.UID);
            if (Time.realtimeSinceStartup <= catchupSuppressUntil)
            {
                // This end was produced by reconstructing/applying a one-time share,
                // not by a player turn-in. Never let it become authoritative.
                string catchupInst;
                if (_cliUid2Inst.TryGetValue(q.UID, out catchupInst))
                    CleanupClientMapping(catchupInst, q.UID);

                Debug.LogWarning($"[QuestNetSync][MissingOnlyShare] Suppressed synthetic quest end during catch-up uid={q.UID} name='{q.QuestName}'");
                return;
            }
        }

        // If this OnQuestEnded was caused by EndPacket/CoFinishRemoteEndedQuest, do not
        // echo CmdClientEnded back to the host. That echo was making the host replay
        // rewards a second time, often randomly depending on timing.
        if (_suppressClientQuestEndReportUids.Contains(q.UID))
        {
            RemoveNonPermanentQuestInventoryItems(q);
            string suppressedInst;
            if (_cliUid2Inst.TryGetValue(q.UID, out suppressedInst))
                CleanupClientMapping(suppressedInst, q.UID);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Suppressed remote quest-end echo uid={q.UID} name={q.QuestName}");
            return;
        }

        // DFU can raise OnQuestEnded more than once while its delayed end/tombstone
        // flow is still running. Only the first local completion may reach the host.
        if (!_clientQuestEndReportedUids.Add(q.UID))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Ignored duplicate local quest-end report uid={q.UID} name={q.QuestName}");
            return;
        }

        MarkQuestLocalStarted(q.UID);

        RemoveNonPermanentQuestInventoryItems(q);

        string inst;
        if (_cliUid2Inst.TryGetValue(q.UID, out inst))
        {
            Quest.TaskState[] finalTasks = q.GetTaskStates();
            Quest.LogEntry[] finalLogs = q.GetLogMessages() ?? new Quest.LogEntry[0];
            Dictionary<string, ItemState> finalItems = CaptureItemStates(q);
            PlaceDTO[] finalPlaces = BuildPlaces(q);
            PersonDTO[] finalPersons = BuildPersons(q);
            FoeDTO[] finalFoes = BuildFoes(q);

            CmdClientEnded(
                inst,
                q.UID,
                q.QuestSuccess,
                ToTaskDTOs(q, finalTasks),
                finalLogs.Select(L => new LogEntryDTO { stepID = L.stepID, messageID = L.messageID }).ToArray(),
                ToItemDTOs(finalItems),
                finalPlaces,
                finalPersons,
                finalFoes,
                q.QuestSuccess ? FindTasksWithRewardActionToReplay(q) : new string[0]);
        }
    }

    private void Cli_OnTick()
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isClientOnly) return;

        RefreshCurrentSiteQuestResourceObjects();
        TryRequestLoadedQuestResume();

        ulong[] actives = QuestMachine.Instance.GetAllActiveQuests();
        for (int i = 0; i < actives.Length; i++)
        {
            ulong uid = actives[i];
            string inst;
            if (!_cliUid2Inst.TryGetValue(uid, out inst)) continue;
            if (_applying.Contains(inst)) continue;

            Quest q = QuestMachine.Instance.GetQuest(uid);
            if (q == null) continue;
            if (IsQuestSharingBlacklisted(q))
            {
                CleanupClientMapping(inst, uid);
                continue;
            }

            Quest oldQuestObj;
            if (_cliQuestObjectByUid.TryGetValue(uid, out oldQuestObj) &&
                oldQuestObj != null && !object.ReferenceEquals(oldQuestObj, q))
            {
                // Save/load replaced this quest object while static network mappings
                // survived. Do not let the newly-loaded old quest send deltas that
                // can roll back the server/host inventory or task state.
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync] Dropping stale client quest mapping after load uid={uid} name={q.QuestName}");
                CleanupClientMapping(inst, uid);
                ResetRandomDeliveryForQuest(uid);
                continue;
            }

            Quest.TaskState[] nowTasks = q.GetTaskStates();
            Quest.LogEntry[] nowLogs = q.GetLogMessages() ?? new Quest.LogEntry[0];
            FoeDTO[] nowFoes = BuildFoes(q);
            Dictionary<string, ItemState> nowItems = CaptureItemStates(q);

            Quest.TaskState[] oldTasks;
            HashSet<int> oldLogs;
            FoeDTO[] oldFoes;
            Dictionary<string, ItemState> oldItems;
            _cliLastTasks.TryGetValue(inst, out oldTasks);
            _cliLastLogs.TryGetValue(inst, out oldLogs);
            _cliLastFoes.TryGetValue(inst, out oldFoes);
            _cliLastItems.TryGetValue(inst, out oldItems);

            // Client item snapshots were intentionally removed as generic packet
            // triggers after K0C00Y02 gold stack churn caused high traffic. Restore only
            // the missing safe case: an item that the quest script assigned to a Foe
            // has actually appeared in this client's PlayerEntity inventory.
            ReportNewLocalFoeLootInventoryPickups(q, oldItems, nowItems);

            // The explicit pickup path can relink/canonicalize the local item.
            nowItems = CaptureItemStates(q);

            TaskStateDTO[] taskChanges = BuildTaskProgressDeltas(q, oldTasks, nowTasks);
            LogDeltaDTO[] logChanges = BuildLogProgressDeltas(oldLogs, nowLogs);
            FoeProgressDeltaDTO[] foeChanges = BuildFoeProgressDeltas(q, oldFoes, nowFoes);

            bool changed = taskChanges.Length > 0 || logChanges.Length > 0 || foeChanges.Length > 0;

            if (changed)
            {
                TraceClientQuestTraffic(
                    q,
                    inst,
                    taskChanges,
                    logChanges,
                    foeChanges);

                // Stack quantities are sampled only beside a real progress edge. They
                // are never allowed to trigger a packet themselves; generated gold
                // stacks can legitimately differ between character saves.
                CmdClientDelta(
                    inst,
                    taskChanges,
                    logChanges,
                    ToItemDTOs(CaptureItemStates(q)),
                    foeChanges
                );
            }

            // Reliable Commands make this the acknowledgement baseline for local
            // progress. Explicit click/pickup/location RPCs carry those side effects;
            // private item/person/place snapshots are not competing writers anymore.
            _cliLastTasks[inst] = nowTasks;
            _cliLastLogs[inst]  = new HashSet<int>(nowLogs.Select(l => l.stepID));
            _cliLastFoes[inst] = nowFoes;
            _cliLastItems[inst] = nowItems;
        }
    }

    private const string ManualShareInstancePrefix = "manualshare_";

    private static bool IsManualShareInstanceId(string instanceId)
    {
        return !string.IsNullOrEmpty(instanceId) && instanceId.StartsWith(ManualShareInstancePrefix, StringComparison.Ordinal);
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // Scripted quest-chain authority
    //
    // DFU's "start quest" action is an ordinary quest action. When every multiplayer
    // participant owns a local copy of the same quest, every copy can otherwise start
    // the same follow-up quest independently. The server/host is the only machine that
    // may execute these nested starts. Pure clients now defer at Task.Update() without
    // changing action completion state. Completion is written only after the child
    // genuinely exists. This is discovered at runtime; no main-quest filename list is
    // required.
    // ─────────────────────────────────────────────────────────────────────────────
    private static bool TryGetStartQuestTargetName(IQuestAction action, out string questName)
    {
        questName = string.Empty;
        if (action == null || !string.Equals(action.GetType().Name, "StartQuest", StringComparison.Ordinal))
            return false;

        try
        {
            object saveData = action.GetSaveData();
            if (saveData != null)
            {
                FieldInfo nameField = saveData.GetType().GetField(
                    "questName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (nameField != null)
                {
                    string explicitName = nameField.GetValue(saveData) as string;
                    if (!string.IsNullOrEmpty(explicitName))
                    {
                        questName = NormalizeQuestTemplateName(explicitName);
                        return !string.IsNullOrEmpty(questName);
                    }
                }

                FieldInfo indexField = saveData.GetType().GetField(
                    "questIndex1",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (indexField != null)
                {
                    object indexValue = indexField.GetValue(saveData);
                    int questIndex = indexValue != null ? Convert.ToInt32(indexValue) : 0;
                    if (questIndex > 0)
                    {
                        questName = string.Format("S{0:0000000}", questIndex);
                        return true;
                    }
                }
            }

            // Fallback for DFU builds where action-specific save data changed shape.
            FieldInfo directNameField = action.GetType().GetField(
                "questName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (directNameField != null)
            {
                string explicitName = directNameField.GetValue(action) as string;
                if (!string.IsNullOrEmpty(explicitName))
                {
                    questName = NormalizeQuestTemplateName(explicitName);
                    return !string.IsNullOrEmpty(questName);
                }
            }

            FieldInfo directIndexField = action.GetType().GetField(
                "questIndex1",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (directIndexField != null)
            {
                object indexValue = directIndexField.GetValue(action);
                int questIndex = indexValue != null ? Convert.ToInt32(indexValue) : 0;
                if (questIndex > 0)
                {
                    questName = string.Format("S{0:0000000}", questIndex);
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static string NormalizeQuestTemplateName(string questName)
    {
        if (string.IsNullOrEmpty(questName))
            return string.Empty;

        questName = questName.Trim();
        if (questName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            questName = questName.Substring(0, questName.Length - 4);

        return questName;
    }

    private static bool QuestMachineContainsQuestTemplate(string questName)
    {
        if (QuestMachine.Instance == null || string.IsNullOrEmpty(questName))
            return false;

        string wanted = NormalizeQuestTemplateName(questName);
        ulong[] all = QuestMachine.Instance.GetAllQuests() ?? new ulong[0];
        for (int i = 0; i < all.Length; i++)
        {
            Quest q = QuestMachine.Instance.GetQuest(all[i]);
            if (q != null &&
                string.Equals(NormalizeQuestTemplateName(q.QuestName), wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runtime-only nested quest authority hook called by Task.Update(). Returning true
    /// pauses the task at its StartQuest action, including all later EndQuest/reward
    /// actions, but deliberately leaves StartQuest incomplete and therefore unsaved.
    /// </summary>
    public static bool ShouldDeferNestedQuestStart(
        Quest parentQuest,
        DaggerfallWorkshop.Game.Questing.Task parentTask,
        IQuestAction action)
    {
        string targetName;
        if (parentQuest == null || action == null ||
            !TryGetStartQuestTargetName(action, out targetName))
            return false;

        // DFU's hidden backbone was already intentionally client-originatable. Keep
        // that behaviour; the server-side name/UID duplicate guard remains in charge.
        if (string.Equals(
                NormalizeQuestTemplateName(parentQuest.QuestName),
                "S0000999",
                StringComparison.OrdinalIgnoreCase))
            return false;

        QuestNetSync local = LocalInstance;
        if (local == null || !local.isLocalPlayer || !local.isClientOnly)
            return false;

        // Do not interfere with private/unshared local quests. Once a quest has a
        // client network mapping, the authoritative host owns its nested starts.
        string parentInstanceId;
        if (!_cliUid2Inst.TryGetValue(parentQuest.UID, out parentInstanceId) ||
            string.IsNullOrEmpty(parentInstanceId))
            return false;

        string taskSymbol =
            parentTask != null && parentTask.Symbol != null
                ? parentTask.Symbol.Name
                : string.Empty;
        string deferredKey =
            MakeDeferredNestedStartKey(
                parentQuest.UID,
                taskSymbol,
                targetName);
        bool childExists = QuestMachineContainsQuestTemplate(targetName);
        if (childExists)
        {
            // This is the only safe persistent completion: the child really exists.
            if (!action.IsComplete)
                action.SetComplete();

            _clientDeferredNestedStarts.Remove(deferredKey);
            _clientApprovedNestedStarts.Remove(deferredKey);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][QuestChain] Client acknowledged existing child target='{targetName}' parentUid={parentQuest.UID} task='{(parentTask != null && parentTask.Symbol != null ? parentTask.Symbol.Name : string.Empty)}'");
        }
        else if (Debug.isDebugBuild)
        {
            Debug.Log($"[QuestNetSync][QuestChain] Client deferred nested StartQuest target='{targetName}' parentUid={parentQuest.UID} task='{(parentTask != null && parentTask.Symbol != null ? parentTask.Symbol.Name : string.Empty)}'");
        }

        if (!childExists)
        {
            if (!string.IsNullOrEmpty(taskSymbol))
            {
                _clientDeferredNestedStarts.Add(deferredKey);
            }

            // The server already confirmed this exact StartQuest was scheduled.
            // Task.Update will skip only this action through the companion hook and
            // continue the later popup/reward actions immediately.
            if (_clientApprovedNestedStarts.Contains(deferredKey))
                return false;

            // CmdPromptChoice is already the authoritative request for a selected
            // prompt branch. Sending a second nested-start Command from that same
            // branch creates a scheduled-but-not-yet-visible duplicate window.
            if (_promptChoiceBranchExecutionDepth > 0)
                return true;

            string requestKey =
                parentInstanceId + "|" + taskSymbol + "|" + targetName;

            // Report the exact action reached once. This replaces the old broad
            // post-click guess that could not distinguish one future StartQuest branch
            // from another and did not fire when the host branch stayed incomplete.
            if (!string.IsNullOrEmpty(taskSymbol) &&
                _clientNestedStartRequestsSent.Add(requestKey))
            {
                local.CmdClientReachedNestedQuestStart(
                    parentInstanceId,
                    parentQuest.UID,
                    taskSymbol,
                    targetName);
            }
        }

        return true;
    }

    private static string MakeDeferredNestedStartKey(
        ulong parentQuestUid,
        string taskSymbol,
        string targetQuestName)
    {
        return parentQuestUid.ToString() + "|" +
            NormalizePersonClickTaskSymbol(taskSymbol) + "|" +
            NormalizeQuestTemplateName(targetQuestName);
    }

    /// <summary>
    /// Companion Task.Update hook. Once the server has acknowledged this exact
    /// nested start, pure clients skip only StartQuest at runtime and continue the
    /// actions after it. The action is saved complete only after the child exists.
    /// </summary>
    public static bool ShouldSkipApprovedNestedQuestStart(
        Quest parentQuest,
        DaggerfallWorkshop.Game.Questing.Task parentTask,
        IQuestAction action)
    {
        string targetName;
        if (parentQuest == null ||
            parentTask == null ||
            parentTask.Symbol == null ||
            action == null ||
            !TryGetStartQuestTargetName(action, out targetName))
            return false;

        QuestNetSync local = LocalInstance;
        if (local == null || !local.isLocalPlayer || !local.isClientOnly)
            return false;

        string key =
            MakeDeferredNestedStartKey(
                parentQuest.UID,
                parentTask.Symbol.Name,
                targetName);
        if (!_clientApprovedNestedStarts.Contains(key))
            return false;

        if (QuestMachineContainsQuestTemplate(targetName))
        {
            if (!action.IsComplete)
                action.SetComplete();
            _clientApprovedNestedStarts.Remove(key);
            _clientDeferredNestedStarts.Remove(key);
        }

        return true;
    }

    [Command]
    private void CmdClientReachedNestedQuestStart(
        string parentInstanceId,
        ulong clientParentUid,
        string taskSymbol,
        string targetQuestName)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            !isServer ||
            string.IsNullOrEmpty(parentInstanceId) ||
            string.IsNullOrEmpty(taskSymbol) ||
            string.IsNullOrEmpty(targetQuestName) ||
            parentInstanceId.Length > 128 ||
            taskSymbol.Length > 128 ||
            targetQuestName.Length > 128 ||
            QuestMachine.Instance == null)
            return;

        ulong serverParentUid;
        if (!_srvInst2Uid.TryGetValue(parentInstanceId, out serverParentUid))
            return;

        Quest parentQuest = QuestMachine.Instance.GetQuest(serverParentUid);
        if (parentQuest == null || IsQuestSharingBlacklisted(parentQuest))
            return;

        targetQuestName = NormalizeQuestTemplateName(targetQuestName);
        DaggerfallWorkshop.Game.Questing.Task task =
            parentQuest.GetTask(new Symbol(taskSymbol));
        if (task == null || task.Actions == null)
            return;

        IQuestAction startAction = null;
        foreach (IQuestAction candidate in task.Actions)
        {
            string candidateTarget;
            if (TryGetStartQuestTargetName(candidate, out candidateTarget) &&
                string.Equals(
                    candidateTarget,
                    targetQuestName,
                    StringComparison.OrdinalIgnoreCase))
            {
                startAction = candidate;
                break;
            }
        }

        if (startAction == null)
            return;

        string requestKey =
            parentInstanceId + "|" + taskSymbol + "|" + targetQuestName;
        if (!_serverNestedStartRequestsHandled.Add(requestKey))
            return;

        if (QuestMachineContainsQuestTemplate(targetQuestName))
        {
            if (!startAction.IsComplete)
                startAction.SetComplete();
            return;
        }

        // If the host already reached this action, do not execute it a second time
        // while its scheduled child is between ticks—or after a short-lived helper
        // child (such as S0000101/S0000102) has already ended and disappeared.
        bool executedHere = false;
        if (!startAction.IsComplete)
        {
            try
            {
                if (!task.IsTriggered)
                    parentQuest.StartTask(new Symbol(taskSymbol));

                startAction.Update(task);
                executedHere = true;
            }
            catch (Exception ex)
            {
                _serverNestedStartRequestsHandled.Remove(requestKey);
                Debug.LogWarning(
                    $"[QuestNetSync][QuestChainRequest] Host failed exact nested start " +
                    $"parentUid={serverParentUid} clientParentUid={clientParentUid} " +
                    $"task='{taskSymbol}' target='{targetQuestName}': {ex.Message}");
                return;
            }
        }

        if (!executedHere)
            return;

        Dictionary<string, IQuestAction> exactProbe =
            new Dictionary<string, IQuestAction>(StringComparer.OrdinalIgnoreCase);
        exactProbe[targetQuestName] = startAction;
        StartCoroutine(
            CoRecoverNestedQuestStartAfterExactClientReach(
                serverParentUid,
                connectionToClient,
                exactProbe));

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync][QuestChainRequest] Host accepted exact nested start " +
                $"parentUid={serverParentUid} clientParentUid={clientParentUid} " +
                $"task='{taskSymbol}' target='{targetQuestName}'");
        }
    }

    private static int AcknowledgeNetworkQuestStartInLocalParents(Quest startedQuest, string reason)
    {
        if (startedQuest == null || QuestMachine.Instance == null || string.IsNullOrEmpty(startedQuest.QuestName))
            return 0;

        string startedName = NormalizeQuestTemplateName(startedQuest.QuestName);
        int completedActions = 0;
        ulong[] all = QuestMachine.Instance.GetAllQuests() ?? new ulong[0];

        for (int i = 0; i < all.Length; i++)
        {
            Quest parentQuest = QuestMachine.Instance.GetQuest(all[i]);
            if (parentQuest == null || parentQuest.UID == startedQuest.UID)
                continue;

            List<DaggerfallWorkshop.Game.Questing.Task> parentTasks = GetQuestTasksForActionScan(parentQuest);
            for (int t = 0; t < parentTasks.Count; t++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = parentTasks[t];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    string targetName;
                    if (!TryGetStartQuestTargetName(action, out targetName) ||
                        !string.Equals(targetName, startedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // The child already exists through multiplayer. Consume every local
                    // parent branch that could start the same child, including alternate
                    // main-quest branches, so a later NPC click cannot offer it again.
                    if (!action.IsComplete)
                    {
                        action.SetComplete();
                        completedActions++;
                    }

                    // Continue only a task this machine actually paused at this exact
                    // StartQuest action. This is independent of quest names and still
                    // works when another acknowledgement already completed the action.
                    if (task.Symbol != null && task.IsTriggered)
                    {
                        string continuationKey =
                            MakeDeferredNestedStartKey(
                                parentQuest.UID,
                                task.Symbol.Name,
                                startedName);
                        if (_clientDeferredNestedStarts.Remove(
                                continuationKey))
                        {
                            _clientApprovedNestedStarts.Remove(
                                continuationKey);
                            try
                            {
                                task.Update();
                                Debug.Log(
                                    $"[QuestNetSync][QuestChain] Continued deferred parent " +
                                    $"uid={parentQuest.UID} task='{task.Symbol.Name}' " +
                                    $"child='{startedName}' reason='{reason}'");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning(
                                    $"[QuestNetSync][QuestChain] Deferred parent continuation " +
                                    $"failed uid={parentQuest.UID} " +
                                    $"task='{task.Symbol.Name}': {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        if (completedActions > 0 && Debug.isDebugBuild)
        {
            Debug.Log($"[QuestNetSync][QuestChain] Acknowledged started quest='{startedName}' uid={startedQuest.UID}; completed {completedActions} local parent StartQuest action(s), reason={reason}");
        }

        return completedActions;
    }

    private static int ReconcileNestedQuestStartActions(
        Quest q,
        bool repairMissingChildren,
        bool executeTriggeredRepairs,
        string reason,
        out int executed)
    {
        executed = 0;
        if (q == null)
            return 0;

        bool isBackbone = string.Equals(
            NormalizeQuestTemplateName(q.QuestName),
            "S0000999",
            StringComparison.OrdinalIgnoreCase);
        int changed = 0;
        List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
        for (int i = 0; i < tasks.Count; i++)
        {
            DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
            if (task == null || task.Actions == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                string targetName;
                if (!TryGetStartQuestTargetName(action, out targetName))
                    continue;

                if (QuestMachineContainsQuestTemplate(targetName))
                {
                    // Child presence is authoritative proof that completing this
                    // parent action cannot lose progress or create a duplicate.
                    if (!action.IsComplete)
                    {
                        action.SetComplete();
                        changed++;
                    }
                    continue;
                }

                // S0000999 was never suppressed by the broken implementation, so do
                // not reinterpret its historical completion state as corruption.
                if (!repairMissingChildren || isBackbone || !action.IsComplete)
                    continue;

                ActionTemplate template = action as ActionTemplate;
                if (template == null)
                    continue;

                bool taskTriggered = false;
                try { taskTriggered = task.IsTriggered; }
                catch { }

                bool parentEnded = q.QuestComplete || q.QuestTombstoned;

                // A triggered StartQuest in a still-running parent can be completely
                // legitimate even when its short-lived child already ended and left
                // QuestMachine. S0000016's S0000101/S0000102 helper quests do exactly
                // this. Do not mistake those for old client-save corruption.
                //
                // For a triggered, ended parent, preserve the completed signature on a
                // pure client until an authoritative host/SP repair can execute it.
                if (taskTriggered &&
                    (!parentEnded || !executeTriggeredRepairs))
                    continue;

                // Safe legacy cases:
                //  - dormant task: old suppression completed it before it ever ran;
                //  - triggered + ended parent: the transition was consumed without child.
                template.IsComplete = false;
                changed++;

                if (taskTriggered && executeTriggeredRepairs)
                {
                    string repairKey =
                        q.UID.ToString() + "|" +
                        (task.Symbol != null ? task.Symbol.Name : i.ToString()) + "|" +
                        targetName;

                    if (_legacyQuestChainRepairExecuted.Add(repairKey))
                    {
                        try
                        {
                            // The poisoned parent can already be completed/tombstoned,
                            // so execute only its repaired StartQuest action directly.
                            // Never replay its EndQuest, reward, or reputation actions.
                            action.Update(task);
                            executed++;
                        }
                        catch (Exception ex)
                        {
                            _legacyQuestChainRepairExecuted.Remove(repairKey);
                            Debug.LogWarning($"[QuestNetSync][QuestChainRepair] Failed target='{targetName}' parentUid={q.UID}: {ex.Message}");
                        }
                    }
                }
            }
        }

        if ((changed > 0 || executed > 0) && Debug.isDebugBuild)
        {
            Debug.Log($"[QuestNetSync][QuestChainRepair] Reconciled uid={q.UID} quest='{q.QuestName}', changed={changed}, executed={executed}, reason={reason}");
        }

        return changed;
    }

    private void ApplyClientQuestChainAuthority(Quest q, string reason)
    {
        if (q == null)
            return;

        AcknowledgeNetworkQuestStartInLocalParents(q, reason);
        int ignoredExecuted;
        ReconcileNestedQuestStartActions(q, true, false, reason, out ignoredExecuted);
    }

    // Network task mutation can rearm a parent StartQuest action. Reconcile only
    // against real child presence; a missing child stays incomplete and is deferred
    // at runtime by Task.Update().
    private static void ReassertClientQuestChainAuthorityAfterTaskState(Quest q, string reason)
    {
        QuestNetSync local = LocalInstance;
        if (local == null || !local.isClientOnly || q == null)
            return;

        int ignoredExecuted;
        ReconcileNestedQuestStartActions(q, true, false, reason, out ignoredExecuted);
    }

    /// <summary>
    /// Called by Quest.RestoreSaveData(). Waits until the complete quest collection has
    /// settled, then repairs v15-and-earlier saved client suppression. This entry point
    /// intentionally works without a spawned QuestNetSync object so old saves recover
    /// in ordinary single-player too.
    /// </summary>
    public static void ScheduleLegacyQuestChainSaveRepair(string reason)
    {
        if (_legacyQuestChainRepairCoroutineRunning)
            return;

        GameManager runner = GameManager.Instance;
        if (runner == null)
            return;

        if (string.Equals(reason, "quest-save-restore", StringComparison.Ordinal))
            _legacyQuestChainRepairExecuted.Clear();

        _legacyQuestChainRepairCoroutineRunning = true;
        try
        {
            runner.StartCoroutine(CoLegacyQuestChainSaveRepair(reason));
        }
        catch
        {
            _legacyQuestChainRepairCoroutineRunning = false;
        }
    }

    private static IEnumerator CoLegacyQuestChainSaveRepair(string reason)
    {
        // Require a short stable post-load window so target-child checks see the whole
        // saved quest collection rather than whichever quest happened to deserialize first.
        int stableFrames = 0;
        while (stableFrames < 12)
        {
            if (IsSaveLoadInProgressNow())
                stableFrames = 0;
            else
                stableFrames++;

            yield return null;
        }

        try
        {
            RepairLegacyQuestChainSaveState(reason);
        }
        finally
        {
            _legacyQuestChainRepairCoroutineRunning = false;
        }
    }

    private static void RepairLegacyQuestChainSaveState(string reason)
    {
        if (QuestMachine.Instance == null)
            return;

        QuestNetSync local = LocalInstance;
        bool pureClient = local != null && local.isLocalPlayer && local.isClientOnly;
        int changed = 0;
        int executed = 0;

        ulong[] all = QuestMachine.Instance.GetAllQuests() ?? new ulong[0];
        for (int i = 0; i < all.Length; i++)
        {
            Quest q = QuestMachine.Instance.GetQuest(all[i]);
            if (q == null)
                continue;

            int questExecuted;
            changed += ReconcileNestedQuestStartActions(
                q,
                true,
                !pureClient,
                reason,
                out questExecuted);
            executed += questExecuted;
        }

        if ((changed > 0 || executed > 0) && Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][QuestChainRepair] Save repair complete changed={changed}, executed={executed}, pureClient={pureClient}, reason={reason}");
    }

    private IEnumerator CoRecoverNestedQuestStartAfterExactClientReach(
        ulong parentQuestUid,
        NetworkConnection sourceConnection,
        Dictionary<string, IQuestAction> startActionsBeforeClick)
    {
        // StartQuest schedules the child and QuestMachine starts it on a later 10 Hz
        // tick. Let the normal host path finish before deciding it failed.
        yield return new WaitForSecondsRealtime(0.5f);

        if (!isServer ||
            sourceConnection == null ||
            startActionsBeforeClick == null ||
            startActionsBeforeClick.Count == 0)
            yield break;

        foreach (KeyValuePair<string, IQuestAction> pair in startActionsBeforeClick)
        {
            string targetQuestName = NormalizeQuestTemplateName(pair.Key);
            IQuestAction hostStartAction = pair.Value;

            // StartQuest.SetComplete() runs even when GetQuest() returns null. That exact
            // state—action completed, child absent—is the failed host-context signature.
            if (hostStartAction == null ||
                !hostStartAction.IsComplete ||
                string.IsNullOrEmpty(targetQuestName) ||
                QuestMachineContainsQuestTemplate(targetQuestName))
                continue;

            string fallbackKey =
                parentQuestUid.ToString() + "|" + targetQuestName;
            if (!_serverClientContextChainFallbackSent.Add(fallbackKey))
                continue;

            Debug.LogWarning(
                $"[QuestNetSync][QuestChainFallback] Host completed nested StartQuest " +
                $"but no child exists. Asking source client to generate " +
                $"parentUid={parentQuestUid} target='{targetQuestName}'");

            TargetScheduleNestedQuestFromSourceContext(
                sourceConnection,
                parentQuestUid,
                targetQuestName);
        }
    }

    [TargetRpc]
    private void TargetScheduleNestedQuestFromSourceContext(
        NetworkConnection target,
        ulong parentQuestUid,
        string targetQuestName)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;

        if (!isClientOnly ||
            !isLocalPlayer ||
            QuestMachine.Instance == null ||
            GameManager.Instance == null ||
            GameManager.Instance.QuestListsManager == null)
            return;

        targetQuestName = NormalizeQuestTemplateName(targetQuestName);
        if (string.IsNullOrEmpty(targetQuestName) ||
            QuestMachineContainsQuestTemplate(targetQuestName))
            return;

        string fallbackKey =
            parentQuestUid.ToString() + "|" + targetQuestName;
        if (!_clientContextChainFallbackScheduled.Add(fallbackKey))
            return;

        // Generate under an explicitly free local quest UID. Imported/shared quests can
        // leave DaggerfallUnity.CurrentUID behind an already-used quest UID on a pure
        // client. In that case ScheduleQuest() logs success here, but the next
        // QuestMachine tick throws at quests.Add(childQuest.UID, childQuest) and silently
        // drops the queued child from normal gameplay.
        ulong freshLocalUid = AllocateFreshLocalQuestUid(0UL);
        ulong uidSeedBeforeParse = DaggerfallUnity.CurrentUID;
        Quest childQuest = null;

        try
        {
            DaggerfallUnity.CurrentUID = freshLocalUid;
            childQuest =
                GameManager.Instance.QuestListsManager.GetQuest(targetQuestName);
        }
        catch (Exception ex)
        {
            _clientContextChainFallbackScheduled.Remove(fallbackKey);
            Debug.LogWarning(
                $"[QuestNetSync][QuestChainFallback] Source client threw while generating " +
                $"target='{targetQuestName}' parentUid={parentQuestUid} " +
                $"freshUid={freshLocalUid}: {ex.Message}");
            return;
        }
        finally
        {
            // Parsing consumes UIDs for the quest and all generated resources. Never roll
            // that global counter backwards after temporarily selecting a free quest UID.
            DaggerfallUnity.CurrentUID =
                Math.Max(uidSeedBeforeParse, DaggerfallUnity.CurrentUID);
        }

        if (childQuest == null)
        {
            _clientContextChainFallbackScheduled.Remove(fallbackKey);
            Debug.LogWarning(
                $"[QuestNetSync][QuestChainFallback] Source client failed to generate " +
                $"target='{targetQuestName}' parentUid={parentQuestUid} " +
                $"freshUid={freshLocalUid}");
            return;
        }

        try
        {
            // This TargetRpc is not running inside QuestMachine's quest iteration, so start
            // the already-parsed child immediately rather than enqueueing it and losing any
            // exception inside QuestMachine.Tick(). Cli_OnQuestStarted will send the full
            // child StartPacket through the existing authoritative server path.
            QuestMachine.Instance.StartQuest(childQuest);

            Quest startedQuest = QuestMachine.Instance.GetQuest(childQuest.UID);
            if (startedQuest == null)
                throw new InvalidOperationException("QuestMachine did not retain the started child quest.");

            string mappedChildInstance;
            if (!_cliUid2Inst.TryGetValue(childQuest.UID, out mappedChildInstance))
            {
                mappedChildInstance = Guid.NewGuid().ToString("N");

                _cliInst2Uid[mappedChildInstance] = childQuest.UID;
                _cliUid2Inst[childQuest.UID] = mappedChildInstance;
                _cliLastTasks[mappedChildInstance] = childQuest.GetTaskStates();
                _cliLastLogs[mappedChildInstance] =
                    new HashSet<int>((childQuest.GetLogMessages() ?? new Quest.LogEntry[0])
                        .Select(l => l.stepID));
                _cliLastItems[mappedChildInstance] = CaptureItemStates(childQuest);
                _cliLastPersons[mappedChildInstance] = BuildPersons(childQuest);
                _cliLastFoes[mappedChildInstance] = BuildFoes(childQuest);
                _cliQuestObjectByUid[childQuest.UID] = childQuest;

                StartCoroutine(
                    Cli_SendStartPacketAfterDelay(childQuest, mappedChildInstance));

                Debug.LogWarning(
                    $"[QuestNetSync][QuestChainFallback] Local quest-start hook did not " +
                    $"register the child; queued StartPacket manually " +
                    $"target='{targetQuestName}' childUid={childQuest.UID}");
            }

            Debug.Log(
                $"[QuestNetSync][QuestChainFallback] Source client STARTED " +
                $"target='{targetQuestName}' childUid={childQuest.UID} " +
                $"parentUid={parentQuestUid} inst='{mappedChildInstance}'");
        }
        catch (Exception ex)
        {
            _clientContextChainFallbackScheduled.Remove(fallbackKey);
            Debug.LogWarning(
                $"[QuestNetSync][QuestChainFallback] Source client failed to START " +
                $"target='{targetQuestName}' childUid={childQuest.UID} " +
                $"parentUid={parentQuestUid}: {ex.Message}");
        }
    }

    private void PrepareNewServerImportForQuestChainAuthority(Quest q, string reason)
    {
        if (!isServer || q == null)
            return;

        int rearmed = 0;
        int executed = 0;
        HashSet<string> scheduledTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);

        for (int i = 0; i < tasks.Count; i++)
        {
            DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
            if (task == null || task.Actions == null)
                continue;

            bool taskTriggered = false;
            try { taskTriggered = task.IsTriggered; }
            catch { }

            foreach (IQuestAction action in task.Actions)
            {
                string targetName;
                if (!TryGetStartQuestTargetName(action, out targetName))
                    continue;

                if (QuestMachineContainsQuestTemplate(targetName))
                {
                    if (!action.IsComplete)
                        action.SetComplete();
                    continue;
                }

                // Legacy packets/saves from v15 and earlier can contain StartQuest
                // actions that were completed only to suppress local client execution.
                // The newly imported server copy must rearm those actions instead.
                ActionTemplate template = action as ActionTemplate;
                if (action.IsComplete && template != null)
                {
                    template.IsComplete = false;
                    rearmed++;
                }

                // If the source snapshot already has this branch active, execute only
                // its StartQuest action now. Other reward/reputation/end actions remain
                // under the existing controlled quest-state paths.
                if (taskTriggered && !action.IsComplete && scheduledTargets.Add(targetName))
                {
                    try
                    {
                        action.Update(task);
                        executed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[QuestNetSync][QuestChain] Server failed to execute imported StartQuest target='{targetName}' parentUid={q.UID}: {ex.Message}");
                    }
                }
            }
        }

        if ((rearmed > 0 || executed > 0) && Debug.isDebugBuild)
        {
            Debug.Log($"[QuestNetSync][QuestChain] Prepared new server import uid={q.UID} quest='{q.QuestName}', rearmed={rearmed}, executedActive={executed}, reason={reason}");
        }
    }

    private void ReplayServerQuestChainStartsFromRemoteTaskState(Quest q, TaskStateDTO[] taskStates, string reason)
    {
        if (!isServer || q == null || taskStates == null || taskStates.Length == 0)
            return;

        HashSet<string> activeTaskSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < taskStates.Length; i++)
        {
            if (taskStates[i].set && !string.IsNullOrEmpty(taskStates[i].symbol))
                activeTaskSymbols.Add(taskStates[i].symbol);
        }

        if (activeTaskSymbols.Count == 0)
            return;

        HashSet<string> scheduledTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int executed = 0;

        foreach (string taskSymbol in activeTaskSymbols)
        {
            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                string targetName;
                if (!TryGetStartQuestTargetName(action, out targetName))
                    continue;

                if (QuestMachineContainsQuestTemplate(targetName))
                {
                    if (!action.IsComplete)
                        action.SetComplete();
                    continue;
                }

                // Normally CmdClientDelta has already activated this task on the server.
                // This is the end-packet fallback for a final task that started and ended
                // the quest inside one client tick before a sparse delta could arrive.
                if (action.IsComplete || !scheduledTargets.Add(targetName))
                    continue;

                try
                {
                    action.Update(task);
                    executed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[QuestNetSync][QuestChain] Server failed to replay StartQuest target='{targetName}' parentUid={q.UID}: {ex.Message}");
                }
            }
        }

        if (executed > 0 && Debug.isDebugBuild)
        {
            Debug.Log($"[QuestNetSync][QuestChain] Server replayed {executed} nested quest start(s) from remote task state parentUid={q.UID} quest='{q.QuestName}', reason={reason}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // UID-collision safe import helpers
    // Network identity is instanceId. DFU Quest.UID is only local to this process.
    // If an incoming StartPacket's sender UID is already occupied locally by a different
    // quest, start the incoming quest under a fresh local UID and map instanceId -> local UID.
    // Never rekey/delete existing local quests to resolve sharing collisions.
    // ─────────────────────────────────────────────────────────────────────────────
    private static bool IsUidMappedToSameInstance(Dictionary<ulong, string> map, ulong uid, string instanceId)
    {
        string mapped;
        return uid != 0UL && !string.IsNullOrEmpty(instanceId) &&
               map != null && map.TryGetValue(uid, out mapped) &&
               string.Equals(mapped, instanceId, StringComparison.Ordinal);
    }

    private static bool IsUidMappedToDifferentInstance(Dictionary<ulong, string> map, ulong uid, string instanceId)
    {
        string mapped;
        return uid != 0UL && map != null && map.TryGetValue(uid, out mapped) &&
               !string.Equals(mapped, instanceId, StringComparison.Ordinal);
    }

    private static ulong AllocateFreshLocalQuestUid(ulong avoidUid)
    {
        ulong candidate = DaggerfallUnity.CurrentUID;
        if (candidate == 0UL)
            candidate = avoidUid + 1UL;
        if (candidate == avoidUid)
            candidate++;

        for (int i = 0; i < 100000; i++)
        {
            if (candidate == 0UL)
                candidate++;

            bool questFree = QuestMachine.Instance == null || QuestMachine.Instance.GetQuest(candidate) == null;
            bool serverMapFree = !_srvUid2Inst.ContainsKey(candidate);
            bool clientMapFree = !_cliUid2Inst.ContainsKey(candidate);

            if (candidate != avoidUid && questFree && serverMapFree && clientMapFree)
                return candidate;

            candidate++;
        }

        // Fallback: consume global UIDs until a quest-free one appears.
        for (int i = 0; i < 100000; i++)
        {
            candidate = DaggerfallUnity.NextUID;
            if (candidate != avoidUid &&
                (QuestMachine.Instance == null || QuestMachine.Instance.GetQuest(candidate) == null) &&
                !_srvUid2Inst.ContainsKey(candidate) && !_cliUid2Inst.ContainsKey(candidate))
                return candidate;
        }

        return DaggerfallUnity.NextUID;
    }

    private Quest StartQuestFromPacketWithLocalUid(StartPacket pkt, ulong localUid, bool serverSide)
    {
        if (QuestMachine.Instance == null || string.IsNullOrEmpty(pkt.questName) || localUid == 0UL)
            return null;

        if (serverSide)
            _srvSuppressStartDepth++;
        else
            _suppressStartDepth++;

        ulong beforeSeed = DaggerfallUnity.CurrentUID;
        try
        {
            DaggerfallUnity.CurrentUID = localUid;
            BeginRemoteParseWindow(localUid);
            if (!serverSide)
                _suppressStartByName.Add(pkt.questName);

            // Keep the established path first. Most quests can be parsed normally in
            // every participant's current context.
            WithQuestLogMutex(() =>
                QuestMachine.Instance.StartQuest(pkt.questName, pkt.factionId));

            Quest parsedQuest = QuestMachine.Instance.GetQuest(localUid);
            if (parsedQuest != null)
                return parsedQuest;

            // Context-sensitive chained quests can fail to parse when the receiver is
            // still in another dungeon/region. Restore the source client's complete
            // generated quest instead of trying to regenerate random/remote resources.
            Quest.QuestSaveData_v1 savedQuest;
            if (string.IsNullOrEmpty(pkt.questSaveDataJson) ||
                !FromJson(pkt.questSaveDataJson, out savedQuest))
            {
                return null;
            }

            savedQuest.uid = localUid;
            Quest restoredQuest = new Quest();
            restoredQuest.RestoreSaveData(savedQuest);
            restoredQuest.FactionId = pkt.factionId;

            WithQuestLogMutex(() =>
                QuestMachine.Instance.StartQuest(restoredQuest));

            Quest retained = QuestMachine.Instance.GetQuest(localUid);
            if (retained != null)
            {
                Debug.Log(
                    $"[QuestNetSync][QuestChainImport] Restored generated quest snapshot " +
                    $"quest='{pkt.questName}' sourceUid={pkt.uid} localUid={localUid} " +
                    $"serverSide={serverSide}");
            }

            return retained;
        }
        finally
        {
            ulong afterSeed = DaggerfallUnity.CurrentUID;
            DaggerfallUnity.CurrentUID =
                Math.Max(Math.Max(beforeSeed, afterSeed), localUid + 1UL);
            EndRemoteParseWindow(localUid);

            if (serverSide)
                _srvSuppressStartDepth--;
            else
                _suppressStartDepth--;
        }
    }

    private void RegisterClientQuestMapping(string instanceId, Quest q)
    {
        if (q == null || string.IsNullOrEmpty(instanceId))
            return;

        _cliInst2Uid[instanceId] = q.UID;
        _cliUid2Inst[q.UID] = instanceId;
        _startedInst.Add(instanceId);
        _cliLastTasks[instanceId] = q.GetTaskStates();
        _cliLastLogs[instanceId] = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _cliLastItems[instanceId] = CaptureItemStates(q);
        _cliLastPersons[instanceId] = BuildPersons(q);
        _cliLastFoes[instanceId] = BuildFoes(q);
        _cliQuestObjectByUid[q.UID] = q;

        ApplyClientQuestChainAuthority(q, "client-mapping");
    }

    private void RegisterServerQuestMapping(string instanceId, Quest q)
    {
        if (q == null || string.IsNullOrEmpty(instanceId))
            return;

        _srvUid2Inst[q.UID] = instanceId;
        _srvInst2Uid[instanceId] = q.UID;
        _srvQuestObjectByUid[q.UID] = q;
        _srvLastTasks[instanceId] = q.GetTaskStates();
        _srvLastLogs[instanceId] = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _srvLastItems[instanceId] = CaptureItemStates(q);
        _srvLastPersons[instanceId] = BuildPersons(q);
        _srvLastPlaces[instanceId] = BuildPlaces(q);
        _srvLastFoes[instanceId] = BuildFoes(q);

        AcknowledgeNetworkQuestStartInLocalParents(q, "server-mapping");
    }

    private void ApplyStartStateToQuest(StartPacket pkt, Quest q, bool clientSide, bool applyStartPacketGetItems = true, bool restoreFullTaskState = false)
    {
        if (q == null || string.IsNullOrEmpty(pkt.instanceId))
            return;

        _applying.Add(pkt.instanceId);
        try
        {
            ApplyPlaces(q, pkt.places);
            ApplyPersons(q, pkt.persons);
            ApplyFoes(q, pkt.foes);
            // Do not copy QuestSuccess from StartPacket. StartPacket is used for
            // live quest sharing/catch-up and can include quests that already ran
            // a GivePc item-grant action, but have not actually succeeded. Copying
            // that flag makes later timeouts/end-failure behave like success.
            ApplyClocks(q, pkt.clocks);
            if (pkt.itemsFull != null && pkt.itemsFull.Length > 0)
                ApplyItemsFull(q, pkt.itemsFull);
            ApplyItems(q, pkt.items);

            // For a newly imported quest, restore DFU's complete task/action state.
            // Applying only task booleans leaves every action "not completed" and can
            // replay EndQuest/GivePc/timeout actions immediately on the new player.
            bool restoredFullTaskState = restoreFullTaskState &&
                TryRestoreTaskSaveData(q, pkt.taskSaveDataJson);
            if (restoredFullTaskState)
                ApplyDesiredLogsOnly(q, pkt.logs);
            else
                ApplyDesiredState(q, pkt.tasks, pkt.logs);

            // A completed RevealLocation action is restored as already complete, so
            // DFU will not execute its PlayerGPS.DiscoverLocation() side effect again.
            // Restore that external state once, only when this quest was newly imported.
            if (restoreFullTaskState)
            {
                ApplyStartPacketRevealedLocations(q, pkt.revealedPlaceSymbols);
                ApplyStartPacketSayMessages(pkt, q);
            }

            if (clientSide && applyStartPacketGetItems)
                ApplyGetItemReplicationFromStartPacket(q, pkt);

            // Full task restore can rearm StartQuest actions. Reconcile after all packet
            // state has settled; missing children stay incomplete and are runtime-deferred.
            if (clientSide)
                ApplyClientQuestChainAuthority(q, "start-packet-state");
        }
        finally
        {
            _applying.Remove(pkt.instanceId);
        }
    }

    private bool TryFindExistingManualShareByFingerprint(StartPacket pkt, bool serverSide, out string existingInst, out ulong existingUid)
    {
        existingInst = null;
        existingUid = 0UL;

        Dictionary<string, ulong> inst2Uid = serverSide ? _srvInst2Uid : _cliInst2Uid;
        if (inst2Uid == null || inst2Uid.Count == 0)
            return false;

        foreach (KeyValuePair<string, ulong> kv in inst2Uid)
        {
            if (!IsManualShareInstanceId(kv.Key))
                continue;

            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(kv.Value) : null;
            if (q == null)
                continue;

            if (ManualShareFingerprintMatches(pkt, q) || PacketMatchesLocalQuestGeneratedState(pkt, q))
            {
                existingInst = kv.Key;
                existingUid = kv.Value;
                return true;
            }
        }

        return false;
    }


    private string BuildManualShareFingerprint(Quest q)
    {
        // Loose/stable manual-share identity. This is intentionally NOT a full quest-state
        // fingerprint. It must still match when players are at different steps (e.g. one has
        // 2 fake-gold items and another has 4), so we avoid tasks/logs/items/clicked/hidden
        // state. Identity is quest template + questor/giver + generated places.
        if (q == null)
            return string.Empty;

        List<string> parts = new List<string>();
        parts.Add("quest=" + NormalizeManualShareFingerprintPart(q.QuestName));
        parts.Add("faction=" + q.FactionId.ToString());

        PersonDTO[] persons = BuildPersons(q) ?? new PersonDTO[0];
        PersonDTO[] questors = persons.Where(p => p.isQuestor).ToArray();
        PersonDTO[] identityPersons = questors.Length > 0 ? questors : persons;
        foreach (PersonDTO p in identityPersons.OrderBy(p => p.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("giver=" + NormalizeManualShareFingerprintPart(p.symbol) + ":" +
                      NormalizeManualShareFingerprintPart(p.displayName) + ":" +
                      p.nameSeed.ToString() + ":" + p.factionID.ToString() + ":" +
                      NormalizeManualShareFingerprintPart(p.homePlaceSymbol));
        }

        PlaceDTO[] places = BuildPlaces(q) ?? new PlaceDTO[0];
        foreach (PlaceDTO p in places.OrderBy(p => p.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("place=" + NormalizeManualShareFingerprintPart(p.symbol) + ":" +
                      p.siteType.ToString() + ":" + p.mapId.ToString() + ":" +
                      p.locationId.ToString() + ":" + p.regionIndex.ToString() + ":" +
                      p.buildingKey.ToString() + ":" + p.magicNumberIndex.ToString() + ":" +
                      NormalizeManualShareFingerprintPart(p.regionName) + ":" +
                      NormalizeManualShareFingerprintPart(p.locationName) + ":" +
                      NormalizeManualShareFingerprintPart(p.buildingName));
        }

        return string.Join("|", parts.ToArray());
    }

    private static string BuildManualShareFingerprintFromPacket(StartPacket pkt)
    {
        // Packet-side copy of the same loose/stable identity. Do not include runtime state
        // such as tasks/logs, item stack counts, clicked flags, hidden flags, or kill counts.
        List<string> parts = new List<string>();
        parts.Add("quest=" + NormalizeManualShareFingerprintPart(pkt.questName));
        parts.Add("faction=" + pkt.factionId.ToString());

        PersonDTO[] persons = pkt.persons ?? new PersonDTO[0];
        PersonDTO[] questors = persons.Where(p => p.isQuestor).ToArray();
        PersonDTO[] identityPersons = questors.Length > 0 ? questors : persons;
        foreach (PersonDTO p in identityPersons.OrderBy(p => p.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("giver=" + NormalizeManualShareFingerprintPart(p.symbol) + ":" +
                      NormalizeManualShareFingerprintPart(p.displayName) + ":" +
                      p.nameSeed.ToString() + ":" + p.factionID.ToString() + ":" +
                      NormalizeManualShareFingerprintPart(p.homePlaceSymbol));
        }

        PlaceDTO[] places = pkt.places ?? new PlaceDTO[0];
        foreach (PlaceDTO p in places.OrderBy(p => p.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add("place=" + NormalizeManualShareFingerprintPart(p.symbol) + ":" +
                      p.siteType.ToString() + ":" + p.mapId.ToString() + ":" +
                      p.locationId.ToString() + ":" + p.regionIndex.ToString() + ":" +
                      p.buildingKey.ToString() + ":" + p.magicNumberIndex.ToString() + ":" +
                      NormalizeManualShareFingerprintPart(p.regionName) + ":" +
                      NormalizeManualShareFingerprintPart(p.locationName) + ":" +
                      NormalizeManualShareFingerprintPart(p.buildingName));
        }

        return string.Join("|", parts.ToArray());
    }

    private static string NormalizeManualShareFingerprintPart(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Trim().ToLowerInvariant();
    }

    private bool ManualShareFingerprintMatches(StartPacket pkt, Quest q)
    {
        if (q == null || string.IsNullOrEmpty(pkt.questName))
            return false;
        if (!IsManualShareInstanceId(pkt.instanceId))
            return false;

        string packetKey = pkt.manualShareFingerprint;
        if (string.IsNullOrEmpty(packetKey))
            packetKey = BuildManualShareFingerprintFromPacket(pkt);

        if (string.IsNullOrEmpty(packetKey))
            return false;

        string localKey = BuildManualShareFingerprint(q);
        if (string.IsNullOrEmpty(localKey))
            return false;

        // Compare only generated quest resources, not runtime UID or instanceId.
        // This allows manual re-share after save/load when the old runtime mapping is gone.
        if (string.Equals(packetKey, localKey, StringComparison.Ordinal))
            return true;

        string localPacketStyleKey = BuildManualShareFingerprintFromPacket(new StartPacket
        {
            instanceId = pkt.instanceId,
            questName = q.QuestName,
            factionId = q.FactionId,
            persons = BuildPersons(q),
            places = BuildPlaces(q),
            foes = BuildFoes(q),
            itemsFull = BuildFullItems(q),
            items = BuildItems(q)
        });

        return string.Equals(packetKey, localPacketStyleKey, StringComparison.Ordinal);
    }

    private bool TryFindAnyLocalQuestByManualShareFingerprint(StartPacket pkt, out Quest quest)
    {
        quest = null;
        if (QuestMachine.Instance == null || !IsManualShareInstanceId(pkt.instanceId))
            return false;

        string packetKey = pkt.manualShareFingerprint;
        if (string.IsNullOrEmpty(packetKey))
            packetKey = BuildManualShareFingerprintFromPacket(pkt);
        if (string.IsNullOrEmpty(packetKey))
            return false;

        ulong[] active = QuestMachine.Instance.GetAllActiveQuests();
        if (active == null || active.Length == 0)
            return false;

        for (int i = 0; i < active.Length; i++)
        {
            Quest q = QuestMachine.Instance.GetQuest(active[i]);
            if (q == null || q.QuestComplete || q.QuestTombstoned)
                continue;
            if (ManualShareFingerprintMatches(pkt, q))
            {
                quest = q;
                return true;
            }
        }

        return false;
    }


    private bool TryFindAnyLocalQuestByFingerprint(StartPacket pkt, out Quest quest)
    {
        quest = null;
        if (QuestMachine.Instance == null)
            return false;

        ulong[] active = QuestMachine.Instance.GetAllActiveQuests();
        if (active == null || active.Length == 0)
            return false;

        for (int i = 0; i < active.Length; i++)
        {
            Quest q = QuestMachine.Instance.GetQuest(active[i]);
            if (q == null || q.QuestComplete || q.QuestTombstoned)
                continue;

            if (PacketMatchesLocalQuestGeneratedState(pkt, q))
            {
                quest = q;
                return true;
            }
        }

        return false;
    }

    private bool TryFindLocalQuestForMissingOnlyShare(StartPacket pkt, out Quest quest)
    {
        quest = null;
        if (QuestMachine.Instance == null || string.IsNullOrEmpty(pkt.questName))
            return false;

        // Strongest match: the sender UID is free on this machine and already holds
        // the same active quest template/faction.
        if (pkt.uid != 0UL)
        {
            Quest atSourceUid = QuestMachine.Instance.GetQuest(pkt.uid);
            if (atSourceUid != null && !atSourceUid.QuestComplete && !atSourceUid.QuestTombstoned &&
                string.Equals(atSourceUid.QuestName ?? string.Empty, pkt.questName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                atSourceUid.FactionId == pkt.factionId)
            {
                quest = atSourceUid;
                return true;
            }
        }

        // Generated quest resources identify the same quest even when local UIDs differ.
        if (TryFindAnyLocalQuestByFingerprint(pkt, out quest) && quest != null)
            return true;

        // Last-resort identity for loaded saves: accept a name/faction match only when
        // it is unambiguous. This prevents importing a duplicate after load while still
        // allowing separate repeated instances when several are already active.
        Quest onlyMatch = null;
        int matchCount = 0;
        ulong[] active = QuestMachine.Instance.GetAllActiveQuests();
        if (active != null)
        {
            for (int i = 0; i < active.Length; i++)
            {
                Quest q = QuestMachine.Instance.GetQuest(active[i]);
                if (q == null || q.QuestComplete || q.QuestTombstoned)
                    continue;
                if (!string.Equals(q.QuestName ?? string.Empty, pkt.questName ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    q.FactionId != pkt.factionId)
                    continue;

                onlyMatch = q;
                matchCount++;
                if (matchCount > 1)
                    break;
            }
        }

        if (matchCount == 1)
        {
            quest = onlyMatch;
            return true;
        }

        quest = null;
        return false;
    }

    private bool PacketMatchesLocalQuestGeneratedState(StartPacket pkt, Quest q)
    {
        if (q == null || string.IsNullOrEmpty(pkt.questName))
            return false;
        if (!string.Equals(q.QuestName ?? string.Empty, pkt.questName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return false;
        if (q.FactionId != pkt.factionId)
            return false;

        // Compare generated resources, not mutable task/log state. This lets a player
        // reconnect and re-share the same already-shared quest even if progress changed.
        if (!SamePlaceFingerprints(pkt.places, BuildPlaces(q)))
            return false;
        if (!SamePersonFingerprints(pkt.persons, BuildPersons(q)))
            return false;
        if (!SameFoeFingerprints(pkt.foes, BuildFoes(q)))
            return false;
        if (!SameItemStartFingerprints(pkt.itemsFull, BuildFullItems(q)))
            return false;

        return true;
    }

    private static bool SamePlaceFingerprints(PlaceDTO[] a, PlaceDTO[] b)
    {
        a = a ?? new PlaceDTO[0];
        b = b ?? new PlaceDTO[0];
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].symbol ?? string.Empty, b[i].symbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (a[i].scope != b[i].scope) return false;
            if (a[i].siteType != b[i].siteType) return false;
            if (a[i].mapId != b[i].mapId) return false;
            if (a[i].locationId != b[i].locationId) return false;
            if (a[i].regionIndex != b[i].regionIndex) return false;
            if (a[i].buildingKey != b[i].buildingKey) return false;
            if (a[i].magicNumberIndex != b[i].magicNumberIndex) return false;
            if (!string.Equals(a[i].locationName ?? string.Empty, b[i].locationName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].buildingName ?? string.Empty, b[i].buildingName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SamePersonFingerprints(PersonDTO[] a, PersonDTO[] b)
    {
        a = a ?? new PersonDTO[0];
        b = b ?? new PersonDTO[0];
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].symbol ?? string.Empty, b[i].symbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (a[i].race != b[i].race) return false;
            if (a[i].gender != b[i].gender) return false;
            if (a[i].faceIndex != b[i].faceIndex) return false;
            if (a[i].nameSeed != b[i].nameSeed) return false;
            if (a[i].isQuestor != b[i].isQuestor) return false;
            if (a[i].isIndividualNPC != b[i].isIndividualNPC) return false;
            if (a[i].factionID != b[i].factionID) return false;
            if (!string.Equals(a[i].displayName ?? string.Empty, b[i].displayName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].homePlaceSymbol ?? string.Empty, b[i].homePlaceSymbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].lastAssignedPlaceSymbol ?? string.Empty, b[i].lastAssignedPlaceSymbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SameFoeFingerprints(FoeDTO[] a, FoeDTO[] b)
    {
        a = a ?? new FoeDTO[0];
        b = b ?? new FoeDTO[0];
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].symbol ?? string.Empty, b[i].symbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (a[i].foeId != b[i].foeId) return false;
            if (a[i].spawnCount != b[i].spawnCount) return false;
            if (a[i].humanoidGender != b[i].humanoidGender) return false;
            if (!string.Equals(a[i].displayName ?? string.Empty, b[i].displayName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].typeName ?? string.Empty, b[i].typeName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SameItemStartFingerprints(ItemStartDTO[] a, ItemStartDTO[] b)
    {
        a = a ?? new ItemStartDTO[0];
        b = b ?? new ItemStartDTO[0];
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].symbol ?? string.Empty, b[i].symbol ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            // Do not compare stackCount here. Delivery/fake-gold quest resources can be
            // normalized differently after import or after inventory repair. Symbol identity
            // is enough for manual-share rebind; tasks/logs still come from the packet.
            // if (a[i].stackCount != b[i].stackCount) return false;
            // Do not compare itemDataJson here. Quest item links can be re-written to
            // the local UID during import, and that should not make the same generated
            // quest look different after reconnect.
        }
        return true;
    }

    private static bool TryBuildStartPacketTransferPayload(StartPacket pkt, out byte[] compressedPayload, out int rawUtf8Bytes)
    {
        compressedPayload = null;
        rawUtf8Bytes = 0;

        try
        {
            string json = ToJson(pkt);
            if (string.IsNullOrEmpty(json))
                return false;

            byte[] raw = Encoding.UTF8.GetBytes(json);
            rawUtf8Bytes = raw.Length;

            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress, true))
                    gzip.Write(raw, 0, raw.Length);

                compressedPayload = output.ToArray();
            }

            return compressedPayload != null && compressedPayload.Length > 0;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[QuestNetSync][StartPacketChunk] Failed to serialize/compress StartPacket: " + e);
            compressedPayload = null;
            rawUtf8Bytes = 0;
            return false;
        }
    }

    private static bool TryReadStartPacketTransferPayload(byte[] compressedPayload, out StartPacket pkt)
    {
        pkt = default(StartPacket);
        if (compressedPayload == null || compressedPayload.Length <= 0 ||
            compressedPayload.Length > StartPacketMaxTransferBytes)
            return false;

        try
        {
            byte[] raw;
            using (MemoryStream input = new MemoryStream(compressedPayload, false))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > StartPacketMaxTransferBytes)
                        throw new InvalidDataException("Decompressed StartPacket exceeded safety limit.");
                }
                raw = output.ToArray();
            }

            string json = Encoding.UTF8.GetString(raw);
            return FromJson(json, out pkt);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[QuestNetSync][StartPacketChunk] Failed to decompress/deserialize StartPacket: " + e);
            return false;
        }
    }

    private bool SendStartPacketToServerSmart(StartPacket pkt, out string error)
    {
        error = string.Empty;

        byte[] compressedPayload;
        int rawUtf8Bytes;
        if (!TryBuildStartPacketTransferPayload(pkt, out compressedPayload, out rawUtf8Bytes))
        {
            error = "could not serialize the quest snapshot";
            return false;
        }

        bool useChunked = IsManualShareInstanceId(pkt.instanceId) ||
            rawUtf8Bytes > StartPacketDirectUtf8Limit;

        if (!useChunked)
        {
            CmdClientStartedPacket(pkt);
            return true;
        }

        if (compressedPayload.Length > StartPacketMaxTransferBytes)
        {
            error = "compressed quest snapshot is too large (" + compressedPayload.Length + " bytes)";
            return false;
        }

        StartCoroutine(CoSendStartPacketToServerChunked(compressedPayload, rawUtf8Bytes, pkt.questName));
        return true;
    }

    private IEnumerator CoSendStartPacketToServerChunked(byte[] compressedPayload, int rawUtf8Bytes, string questName)
    {
        if (compressedPayload == null || compressedPayload.Length <= 0)
            yield break;

        string transferId = Guid.NewGuid().ToString("N");
        int chunkCount = (compressedPayload.Length + StartPacketChunkBytes - 1) / StartPacketChunkBytes;
        if (chunkCount <= 0 || chunkCount > StartPacketMaxChunks)
        {
            Debug.LogWarning($"[QuestNetSync][StartPacketChunk] Invalid client->server chunk count={chunkCount} quest='{questName}'.");
            yield break;
        }

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][StartPacketChunk] Client sending quest='{questName}' raw={rawUtf8Bytes} compressed={compressedPayload.Length} chunks={chunkCount}");

        CmdStartPacketChunkBegin(transferId, chunkCount, compressedPayload.Length, rawUtf8Bytes);

        for (int i = 0; i < chunkCount; i++)
        {
            int offset = i * StartPacketChunkBytes;
            int count = Math.Min(StartPacketChunkBytes, compressedPayload.Length - offset);
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(compressedPayload, offset, chunk, 0, count);
            CmdStartPacketChunk(transferId, i, chunk);

            // Avoid placing a large main-quest snapshot into one frame's outgoing queue.
            if ((i & 3) == 3)
                yield return null;
        }

        CmdStartPacketChunkEnd(transferId);
    }

    private IEnumerator CoSendStartPacketToClientChunked(NetworkConnection conn, byte[] compressedPayload, int rawUtf8Bytes, string questName)
    {
        if (!isServer || conn == null || compressedPayload == null || compressedPayload.Length <= 0)
            yield break;

        string transferId = Guid.NewGuid().ToString("N");
        int chunkCount = (compressedPayload.Length + StartPacketChunkBytes - 1) / StartPacketChunkBytes;
        if (chunkCount <= 0 || chunkCount > StartPacketMaxChunks)
        {
            Debug.LogWarning($"[QuestNetSync][StartPacketChunk] Invalid server->client chunk count={chunkCount} quest='{questName}'.");
            yield break;
        }

        TargetStartPacketChunkBegin(conn, transferId, chunkCount, compressedPayload.Length, rawUtf8Bytes);

        for (int i = 0; i < chunkCount; i++)
        {
            int offset = i * StartPacketChunkBytes;
            int count = Math.Min(StartPacketChunkBytes, compressedPayload.Length - offset);
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(compressedPayload, offset, chunk, 0, count);
            TargetStartPacketChunk(conn, transferId, i, chunk);

            if ((i & 3) == 3)
                yield return null;
        }

        TargetStartPacketChunkEnd(conn, transferId);
    }

    [Command]
    private void CmdStartPacketChunkBegin(string transferId, int chunkCount, int totalBytes, int rawUtf8Bytes)
    {
        if (!isServer)
            return;

        CleanupStartPacketAssemblies(_serverStartPacketAssemblies);
        if (!IsValidStartPacketTransferHeader(transferId, chunkCount, totalBytes))
            return;

        _serverStartPacketAssemblies[transferId] =
            new StartPacketChunkAssembly(chunkCount, totalBytes, rawUtf8Bytes);
    }

    [Command]
    private void CmdStartPacketChunk(string transferId, int chunkIndex, byte[] chunk)
    {
        if (!isServer)
            return;

        AddStartPacketChunk(_serverStartPacketAssemblies, transferId, chunkIndex, chunk);
    }

    [Command]
    private void CmdStartPacketChunkEnd(string transferId)
    {
        if (!isServer)
            return;

        StartPacket pkt;
        if (!TryFinishStartPacketAssembly(_serverStartPacketAssemblies, transferId, out pkt))
            return;

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][StartPacketChunk] Server reconstructed quest='{pkt.questName}' inst={pkt.instanceId}");

        ServerHandleClientStartedPacket(pkt);
    }

    [TargetRpc]
    private void TargetStartPacketChunkBegin(NetworkConnection conn, string transferId, int chunkCount, int totalBytes, int rawUtf8Bytes)
    {
        CleanupStartPacketAssemblies(_clientStartPacketAssemblies);
        if (!IsValidStartPacketTransferHeader(transferId, chunkCount, totalBytes))
            return;

        _clientStartPacketAssemblies[transferId] =
            new StartPacketChunkAssembly(chunkCount, totalBytes, rawUtf8Bytes);
    }

    [TargetRpc]
    private void TargetStartPacketChunk(NetworkConnection conn, string transferId, int chunkIndex, byte[] chunk)
    {
        AddStartPacketChunk(_clientStartPacketAssemblies, transferId, chunkIndex, chunk);
    }

    [TargetRpc]
    private void TargetStartPacketChunkEnd(NetworkConnection conn, string transferId)
    {
        StartPacket pkt;
        if (!TryFinishStartPacketAssembly(_clientStartPacketAssemblies, transferId, out pkt))
            return;

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][StartPacketChunk] Client reconstructed quest='{pkt.questName}' inst={pkt.instanceId}");

        ApplyStartPacket(pkt);
    }

    private static bool IsValidStartPacketTransferHeader(string transferId, int chunkCount, int totalBytes)
    {
        if (string.IsNullOrEmpty(transferId) || transferId.Length > 64)
            return false;
        if (chunkCount <= 0 || chunkCount > StartPacketMaxChunks)
            return false;
        if (totalBytes <= 0 || totalBytes > StartPacketMaxTransferBytes)
            return false;

        int expectedChunks = (totalBytes + StartPacketChunkBytes - 1) / StartPacketChunkBytes;
        return expectedChunks == chunkCount;
    }

    private static void AddStartPacketChunk(
        Dictionary<string, StartPacketChunkAssembly> assemblies,
        string transferId,
        int chunkIndex,
        byte[] chunk)
    {
        if (assemblies == null || string.IsNullOrEmpty(transferId) || chunk == null ||
            chunk.Length <= 0 || chunk.Length > StartPacketChunkBytes)
            return;

        StartPacketChunkAssembly assembly;
        if (!assemblies.TryGetValue(transferId, out assembly) || assembly == null)
            return;
        if (chunkIndex < 0 || chunkIndex >= assembly.chunks.Length)
            return;
        if (assembly.chunks[chunkIndex] != null)
            return;
        if (assembly.receivedBytes + chunk.Length > assembly.totalBytes)
        {
            assemblies.Remove(transferId);
            return;
        }

        assembly.chunks[chunkIndex] = chunk;
        assembly.receivedChunks++;
        assembly.receivedBytes += chunk.Length;
        assembly.lastTouchedRealtime = Time.realtimeSinceStartup;
    }

    private static bool TryFinishStartPacketAssembly(
        Dictionary<string, StartPacketChunkAssembly> assemblies,
        string transferId,
        out StartPacket pkt)
    {
        pkt = default(StartPacket);
        if (assemblies == null || string.IsNullOrEmpty(transferId))
            return false;

        StartPacketChunkAssembly assembly;
        if (!assemblies.TryGetValue(transferId, out assembly) || assembly == null)
            return false;

        assemblies.Remove(transferId);
        if (assembly.receivedChunks != assembly.chunks.Length ||
            assembly.receivedBytes != assembly.totalBytes)
        {
            Debug.LogWarning($"[QuestNetSync][StartPacketChunk] Incomplete transfer id={transferId} chunks={assembly.receivedChunks}/{assembly.chunks.Length} bytes={assembly.receivedBytes}/{assembly.totalBytes}");
            return false;
        }

        byte[] compressedPayload = new byte[assembly.totalBytes];
        int offset = 0;
        for (int i = 0; i < assembly.chunks.Length; i++)
        {
            byte[] chunk = assembly.chunks[i];
            if (chunk == null)
                return false;
            Buffer.BlockCopy(chunk, 0, compressedPayload, offset, chunk.Length);
            offset += chunk.Length;
        }

        if (!TryReadStartPacketTransferPayload(compressedPayload, out pkt))
            return false;

        return true;
    }

    private static void CleanupStartPacketAssemblies(Dictionary<string, StartPacketChunkAssembly> assemblies)
    {
        if (assemblies == null || assemblies.Count == 0)
            return;

        float now = Time.realtimeSinceStartup;
        List<string> stale = null;
        foreach (KeyValuePair<string, StartPacketChunkAssembly> kv in assemblies)
        {
            if (kv.Value == null || now - kv.Value.lastTouchedRealtime > StartPacketAssemblyTimeoutSeconds)
            {
                if (stale == null)
                    stale = new List<string>();
                stale.Add(kv.Key);
            }
        }

        if (stale != null)
            for (int i = 0; i < stale.Count; i++)
                assemblies.Remove(stale[i]);
    }

    [Command]
    private void CmdClientStartedPacket(StartPacket pkt)
    {
        ServerHandleClientStartedPacket(pkt);
    }

    // Shared server handler used by both the original small-packet Command and the
    // reconstructed large-packet chunk transfer. Keeping the old logic in one place
    // prevents the two transport paths from drifting apart.
    private void ServerHandleClientStartedPacket(StartPacket pkt)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer) return;
        if (string.IsNullOrEmpty(pkt.instanceId) || string.IsNullOrEmpty(pkt.questName) || pkt.uid == 0)
            return;
        if (IsQuestSharingBlacklistedName(pkt.questName))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][LocalOnly] Ignored client start packet uid={pkt.uid} name='{pkt.questName}'");
            return;
        }

        Srv_PruneToActive();
        bool isManualSharePacket = IsManualShareInstanceId(pkt.instanceId);
        bool serverManualShareDuplicateBindOnly = false;

        // Same network instance already known. Manual shares and explicit missing-only
        // journal shares are allowed to be re-sent to currently connected players;
        // late joiners are still never auto-sent manual quests by CmdRequestCurrent.
        if (_srvInst2Uid.ContainsKey(pkt.instanceId))
        {
            if (pkt.shareOnlyIfMissing)
            {
                // Rebuild from the server's canonical quest so a late joiner receives
                // the current shared state, not a possibly one-tick-stale client copy.
                ulong canonicalUid;
                Quest canonicalQuest = null;
                if (_srvInst2Uid.TryGetValue(pkt.instanceId, out canonicalUid))
                    canonicalQuest = QuestMachine.Instance.GetQuest(canonicalUid);

                if (canonicalQuest != null)
                {
                    uint canonicalTaker = pkt.takerNetId;
                    uint mappedTaker;
                    if (_questTakerByUid.TryGetValue(canonicalQuest.UID, out mappedTaker) && mappedTaker != 0U)
                        canonicalTaker = mappedTaker;

                    pkt = BuildStartPacket(
                        canonicalQuest,
                        pkt.instanceId,
                        canonicalTaker,
                        pkt.grantedSymbols,
                        pkt.grantedPopupIds,
                        true);
                }

                ServerBroadcastStartPacket(pkt);
            }
            else if (isManualSharePacket)
            {
                ServerBroadcastStartPacket(pkt);
            }
            return;
        }

        // If this is a manual re-share after disconnect/load and the sender lost their
        // runtime instance mapping, try to find the existing server-side manual instance
        // by generated quest fingerprint. This does not use quest name alone.
        string existingManualInst;
        ulong existingManualUid;
        if (isManualSharePacket && TryFindExistingManualShareByFingerprint(pkt, true, out existingManualInst, out existingManualUid))
        {
            pkt.instanceId = existingManualInst;
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][LocalUidImport] Server recognized re-shared manual quest sourceUid={pkt.uid} localUid={existingManualUid} inst={existingManualInst} quest='{pkt.questName}'");
            ServerBroadcastStartPacket(pkt);
            return;
        }

        Quest q = null;
        bool serverQuestWasNewlyImported = false;
        ulong serverLocalUid = pkt.uid;

        // Server mapping can be cleared by load hygiene while the actual quest remains
        // active in QuestMachine. If the same generated manual quest is already present,
        // re-bind it instead of importing a duplicate.
        Quest existingGeneratedQuest;
        if (isManualSharePacket &&
            (TryFindAnyLocalQuestByManualShareFingerprint(pkt, out existingGeneratedQuest) ||
             TryFindAnyLocalQuestByFingerprint(pkt, out existingGeneratedQuest)) &&
            existingGeneratedQuest != null)
        {
            string mappedExistingInst;
            if (_srvUid2Inst.TryGetValue(existingGeneratedQuest.UID, out mappedExistingInst) && !string.IsNullOrEmpty(mappedExistingInst))
            {
                pkt.instanceId = mappedExistingInst;
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync][LocalUidImport] Server reusing mapped generated manual quest localUid={existingGeneratedQuest.UID} sourceUid={pkt.uid} inst={mappedExistingInst} quest='{pkt.questName}'");
                ServerBroadcastStartPacket(pkt);
                return;
            }

            q = existingGeneratedQuest;
            serverManualShareDuplicateBindOnly = true;
            serverLocalUid = q.UID;
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][ManualShareDuplicateGuard] Server bound duplicate manual share to existing localUid={serverLocalUid} sourceUid={pkt.uid} inst={pkt.instanceId} quest='{pkt.questName}' without applying remote progress/items");
        }

        Quest atSourceUid = QuestMachine.Instance.GetQuest(pkt.uid);
        string mappedInstAtSourceUid;
        bool sourceUidMapped = _srvUid2Inst.TryGetValue(pkt.uid, out mappedInstAtSourceUid);
        bool sourceUidSameInstance = sourceUidMapped && string.Equals(mappedInstAtSourceUid, pkt.instanceId, StringComparison.Ordinal);
        bool sourceUidDifferentInstance = sourceUidMapped && !sourceUidSameInstance;

        if (q == null && atSourceUid != null && sourceUidSameInstance)
        {
            q = atSourceUid;
        }
        else if (q == null && atSourceUid != null && !sourceUidMapped && isManualSharePacket && ManualShareFingerprintMatches(pkt, atSourceUid))
        {
            // Server already has the same generated manual quest locally, but no runtime mapping.
            // Bind only. Do not overwrite the server's current quest step/items from this share packet.
            q = atSourceUid;
            serverManualShareDuplicateBindOnly = true;
        }
        else if (q == null)
        {
            bool sourceUidOccupiedByDifferentQuest = atSourceUid != null && !sourceUidSameInstance;
            if (sourceUidOccupiedByDifferentQuest || sourceUidDifferentInstance)
                serverLocalUid = AllocateFreshLocalQuestUid(pkt.uid);

            if (sourceUidOccupiedByDifferentQuest && Debug.isDebugBuild)
            {
                Debug.Log($"[QuestNetSync][LocalUidImport] Server UID collision. Keeping server uid={pkt.uid} quest='{atSourceUid.QuestName}', importing client quest='{pkt.questName}' inst={pkt.instanceId} as localUid={serverLocalUid}");
            }

            q = StartQuestFromPacketWithLocalUid(pkt, serverLocalUid, true);
            serverQuestWasNewlyImported = q != null;
        }

        if (q == null)
        {
            Debug.LogWarning($"[QuestNetSync] Server failed to reconstruct quest '{pkt.questName}' (sourceUid={pkt.uid}, localUid={serverLocalUid}).");
            return;
        }

        RegisterServerQuestMapping(pkt.instanceId, q);

        // Force server quest state to match what the client generated in their local area only
        // for a newly imported quest. If this was a manual re-share of a quest already present
        // on the server, bind only and keep the server's current progress/items unchanged.
        if (!serverManualShareDuplicateBindOnly)
            ApplyStartStateToQuest(pkt, q, false, true, serverQuestWasNewlyImported);

        if (serverQuestWasNewlyImported)
            PrepareNewServerImportForQuestChainAuthority(q, "client-start-import");

        RegisterServerQuestMapping(pkt.instanceId, q);

        // Broadcast the original packet. pkt.uid remains the sender/source UID. Each receiver
        // decides its own local UID if that source UID is occupied locally.
        ServerBroadcastStartPacket(pkt);
    }

    [Command]
    private void CmdClientStarted(string questName, int factionId)
    {
        if (!isServer) return;

        // If already active, do NOT start again; map & broadcast once.
        ulong[] found = QuestMachine.Instance.FindQuests(questName, true);
        if (found != null && found.Length > 0)
        {
            Quest best = ChooseMostRecent(found);
            if (best != null)
            {
                if (_srvUid2Inst.ContainsKey(best.UID)) return; // already mapped
                Srv_OnQuestStarted(best);
                return;
            }
        }

        // None exist yet: start one; Srv_OnQuestStarted will broadcast via DFU event.
        WithQuestLogMutex(() => QuestMachine.Instance.StartQuest(questName, factionId));
    }

    [Command]
    private void CmdClientEnded(string instanceId, ulong uid, bool questSuccess, TaskStateDTO[] tasks, LogEntryDTO[] logs, ItemDTO[] items, PlaceDTO[] places, PersonDTO[] persons, FoeDTO[] foes, string[] replayRewardTasks)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer) return;

        // uid is the client's local UID. The server can use a different local UID for
        // the same network instance after UID-collision-safe import.
        ulong serverLocalUid;
        if (!_srvInst2Uid.TryGetValue(instanceId, out serverLocalUid))
            serverLocalUid = uid;

        // Ignore client echoes for a quest the server has already ended/broadcast.
        // These are not real client turn-ins; they are produced when a remote client
        // locally runs EndQuest() after receiving the final EndPacket.
        if (_serverRecentlyEndedQuestUids.Contains(serverLocalUid) ||
            _serverQuestEndInProgressUids.Contains(serverLocalUid))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Ignored duplicate CmdClientEnded for ending/ended uid={serverLocalUid} clientUid={uid}");
            return;
        }

        Quest q = QuestMachine.Instance.GetQuest(serverLocalUid);
        if (q == null || q.QuestComplete)
            return;
        if (IsQuestSharingBlacklisted(q))
            return;

        // Latch before applying final state or replaying GivePc. Two Commands can
        // otherwise arrive before OnQuestEnded populates _serverRecentlyEndedQuestUids.
        if (!_serverQuestEndInProgressUids.Add(serverLocalUid))
            return;

        // Remember who completed the quest so EndPacket apply can skip reward replay on the
        // finisher. In this Command, netId is the player's QuestNetSync NetworkIdentity.
        _questEndSourceNetIdByUid[serverLocalUid] = netId;

        if (!q.QuestComplete)
        {
            // Snapshot before applying the client's final DTOs. If this Command is only
            // an echo of the host's own turn-in, the host's GivePc is already complete
            // here even though QuestComplete may still be false for a few frames.
            HashSet<string> hostGivePcCompletedBeforeRemoteEnd = CaptureCompletedGivePcTaskSymbols(q);

            ApplyPlaces(q, places);
            // A remote client has already completed this quest. Applying its final
            // Person DTOs must not resurrect scene NPCs on the host before the delayed
            // reward/end cleanup finishes.
            ApplyPersons(q, persons, true);
            ApplyFoes(q, foes);
            q.QuestSuccess = questSuccess;
            // Remote client completion: do not StartTask() full final/action tasks from DTOs.
            // Those tasks can contain EndQuest/ChangeReputeWith/GivePc and would apply
            // quest-end reputation before the controlled local end flow below.
            ApplyDesiredStateForRemoteEnd(q, tasks, logs);

            if (questSuccess)
            {
                PrimePermanentGetItemRewardsFromRemoteEnd(
                    q,
                    tasks,
                    "server-remote-client-end");
            }

            // A final task can start the next scripted quest and end this quest inside
            // the same client tick. If its sparse delta did not arrive first, execute
            // only the nested StartQuest action on the authoritative server now.
            ReplayServerQuestChainStartsFromRemoteTaskState(q, tasks, "remote-client-end");

            // ApplyDesiredStateForRemoteEnd intentionally refuses to execute arbitrary
            // action tasks. Recover the one safe durable side effect needed by final
            // physical quest-item rewards: MakePermanent from an authoritatively
            // triggered final task.
            ApplyTriggeredMakePermanentActionsForEndingQuest(
                q,
                tasks,
                "server-client-end");

            ApplyItems(q, items);
            q.QuestSuccess = questSuccess;

            string[] hostReplayRewardTasks = new string[0];

            if (questSuccess)
            {
                // Host-mode local reward replay:
                // When a remote client turns in a quest successfully, this Command runs
                // on the host/server. Use the reward-task list captured on the finishing
                // client, plus any matching local task state after applying the final DTOs.
                // For failed quests this entire reward-replay path must be skipped so
                // failure reputation/cleanup matches SP and no GivePc reward is forced.
                hostReplayRewardTasks = MergeTaskSymbolArrays(
                    replayRewardTasks,
                    FindRewardTasksFromClientTaskStates(q, tasks),
                    FindTasksWithRewardActionToReplay(q));

                // Some successful quest turn-ins complete the reward flow on the finishing
                // client without leaving a clear task-state hint for the host. In that
                // case, fall back to every not-yet-complete GivePc task on the host quest.
                if (hostReplayRewardTasks == null || hostReplayRewardTasks.Length == 0)
                    hostReplayRewardTasks = FindIncompleteGivePcTasks(q);

                // If applying the client's final task states already marked GivePc complete
                // on the host, the "incomplete" fallback can still be empty even though the
                // host never actually received the reward. In that case, force all GivePc
                // tasks to replay on the host.
                if (hostReplayRewardTasks == null || hostReplayRewardTasks.Length == 0)
                    hostReplayRewardTasks = FindAllGivePcTasks(q);
            }
            else if (Debug.isDebugBuild && replayRewardTasks != null && replayRewardTasks.Length > 0)
            {
                Debug.Log($"[QuestNetSync][QuestFailure] Suppressed host reward replay for failed remote end uid={serverLocalUid}, clientUid={uid}, tasks={string.Join(",", replayRewardTasks)}");
            }

            if (NetworkClient.active)
            {
                if (questSuccess)
                {
                    string[] beforeFilterRewardTasks = hostReplayRewardTasks;
                    hostReplayRewardTasks = FilterOutGivePcTasksCompletedBeforeRemoteEnd(hostReplayRewardTasks, hostGivePcCompletedBeforeRemoteEnd);

                    if ((beforeFilterRewardTasks != null && beforeFilterRewardTasks.Length > 0) &&
                        (hostReplayRewardTasks == null || hostReplayRewardTasks.Length == 0))
                    {
                        if (Debug.isDebugBuild)
                            Debug.Log($"[QuestNetSync] Suppressed duplicate host reward replay from client end echo uid={serverLocalUid}, clientUid={uid}, alreadyComplete={string.Join(",", hostGivePcCompletedBeforeRemoteEnd.ToArray())}");
                    }

                    if (Debug.isDebugBuild)
                        Debug.Log($"[QuestNetSync] Host immediate FORCE reward replay for remote completion uid={serverLocalUid}, clientUid={uid}, tasks={(hostReplayRewardTasks != null ? string.Join(",", hostReplayRewardTasks) : "<null>")}");

                    ForceReplayRewardTasksIfNeeded(q, hostReplayRewardTasks, true);
                }
                else if (Debug.isDebugBuild)
                {
                    Debug.Log($"[QuestNetSync][QuestFailure] Host skipped GivePc replay for failed quest uid={serverLocalUid}, clientUid={uid}");
                }

                // Do not tombstone immediately in host mode. Success rewards need a few
                // frames after GivePc.Update(); failures use the same delayed end path
                // but with QuestSuccess=false and no reward replay.
                StartCoroutine(CoFinishRemoteEndedQuest(serverLocalUid, questSuccess));
            }
            else
            {
                q.EndQuest();
            }
        }
        // Srv_OnQuestEnded will RPC the end for remote clients
    }

    [Command]
    private void CmdClientDelta(string instanceId, TaskStateDTO[] taskChanges, LogDeltaDTO[] logChanges, ItemDTO[] itemStacks, FoeProgressDeltaDTO[] foeChanges)
    {
        if (IsQuestNetSyncPausedForLoad()) return;
        if (!isServer) return;

        ulong uid;
        if (!_srvInst2Uid.TryGetValue(instanceId, out uid)) return;
        Quest q = QuestMachine.Instance.GetQuest(uid);
        if (q == null) return;
        if (IsQuestSharingBlacklisted(q)) return;

        // Clients contribute monotonic progress, not complete private quest snapshots.
        // In particular, client task clears are rejected: local PcAt/When/Daily sensors
        // can legitimately be false on one player and true on another, and accepting a
        // remote false can rearm side-effect actions such as PlaceFoe every quest tick.
        // A full snapshot also makes generated gold/person/place values bounce between
        // character saves. These sparse merges keep real progress available to everyone.
        ApplyItemStackCounts(q, itemStacks);
        ApplyFoeProgressDeltas(q, foeChanges);
        ApplyTaskAndLogProgressDeltas(q, taskChanges, logChanges);

        // A contributed task can synchronously execute EndQuest and remove mappings.
        if (q.QuestComplete || q.QuestTombstoned || !_srvInst2Uid.ContainsKey(instanceId))
            return;

        Quest.TaskState[] nowTasks = q.GetTaskStates();
        Quest.LogEntry[]  nowLogs  = q.GetLogMessages() ?? new Quest.LogEntry[0];
        Dictionary<string, ItemState> nowItems = CaptureItemStates(q);
        PlaceDTO[] nowPlaces = BuildPlaces(q);
        PersonDTO[] nowPersons = BuildPersons(q);
        FoeDTO[] nowFoes = BuildFoes(q);
        FoeDTO[] previousFoes;
        _srvLastFoes.TryGetValue(instanceId, out previousFoes);
        nowFoes = SanitizeVolatileFoeStateForNetwork(q, previousFoes, nowFoes, "client-delta-merge");

        bool tasksChanged =
            !_srvLastTasks.ContainsKey(instanceId) ||
            !SameTasks(q, _srvLastTasks[instanceId], nowTasks);
        bool logsChanged =
            !_srvLastLogs.ContainsKey(instanceId) ||
            !_srvLastLogs[instanceId].SetEquals(nowLogs.Select(l => l.stepID));
        bool itemsChanged =
            !_srvLastItems.ContainsKey(instanceId) ||
            !SameItems(_srvLastItems[instanceId], nowItems);
        bool placesChanged =
            !_srvLastPlaces.ContainsKey(instanceId) ||
            !SamePlaces(_srvLastPlaces[instanceId], nowPlaces);
        bool personsChanged =
            !_srvLastPersons.ContainsKey(instanceId) ||
            !SamePersons(_srvLastPersons[instanceId], nowPersons);
        bool foesChanged =
            !_srvLastFoes.ContainsKey(instanceId) ||
            !SameFoes(_srvLastFoes[instanceId], nowFoes);

        bool canonicalChanged =
            tasksChanged || logsChanged || itemsChanged ||
            placesChanged || personsChanged || foesChanged;

        if (canonicalChanged)
        {
            TraceServerQuestTraffic(
                q,
                instanceId,
                "client-delta-merge",
                nowTasks,
                nowLogs,
                nowItems,
                nowPlaces,
                nowPersons,
                nowFoes,
                tasksChanged,
                logsChanged,
                itemsChanged,
                placesChanged,
                personsChanged,
                foesChanged);
        }

        _srvLastTasks[instanceId] = nowTasks;
        _srvLastLogs[instanceId]  = new HashSet<int>(nowLogs.Select(l => l.stepID));
        _srvLastItems[instanceId] = nowItems;
        _srvLastPersons[instanceId] = nowPersons;
        _srvLastPlaces[instanceId] = nowPlaces;
        _srvLastFoes[instanceId] = nowFoes;

        // A duplicate or derived report needs no network fan-out.
        if (!canonicalChanged)
            return;

        RpcUpdate(new UpdatePacket
        {
            instanceId = instanceId,
            sourceNetId = netId,
            tasks = ToTaskDTOs(q, nowTasks),
            logs  = nowLogs.Select(L => new LogEntryDTO { stepID = L.stepID, messageID = L.messageID }).ToArray(),
            items = ToItemDTOs(nowItems),
            places = nowPlaces,
            persons = nowPersons,
            foes = nowFoes,
            questSuccess = q.QuestSuccess,
        });
    }

    private static bool ServerBroadcastStartPacket(StartPacket pkt)
    {
        byte[] compressedPayload;
        int rawUtf8Bytes;
        bool canChunk = TryBuildStartPacketTransferPayload(pkt, out compressedPayload, out rawUtf8Bytes);
        bool useChunked = canChunk &&
            (IsManualShareInstanceId(pkt.instanceId) || rawUtf8Bytes > StartPacketDirectUtf8Limit);

        if (useChunked && (compressedPayload == null || compressedPayload.Length <= 0 ||
            compressedPayload.Length > StartPacketMaxTransferBytes))
        {
            Debug.LogWarning($"[QuestNetSync][StartPacketChunk] Refusing invalid/oversized transfer quest='{pkt.questName}' compressed={(compressedPayload != null ? compressedPayload.Length : 0)} bytes.");
            return false;
        }

        // ClientRpc is observer-based in Mirror. Quest sync is global, but a StartPacket
        // sent from a client-owned QuestNetSync object can be missed by clients that are
        // not observing that specific player object. Send through each player's own
        // QuestNetSync object as a TargetRpc so every connected player receives it.
        QuestNetSync[] syncs = UnityEngine.Object.FindObjectsOfType<QuestNetSync>();
        HashSet<NetworkConnection> sentConnections = new HashSet<NetworkConnection>();
        bool sentAny = false;

        for (int i = 0; i < syncs.Length; i++)
        {
            QuestNetSync sync = syncs[i];
            if (sync == null || !sync.isServer)
                continue;

            NetworkConnection conn = sync.connectionToClient;
            if (conn == null || !sentConnections.Add(conn))
                continue;

            if (useChunked)
                sync.StartCoroutine(sync.CoSendStartPacketToClientChunked(conn, compressedPayload, rawUtf8Bytes, pkt.questName));
            else
                sync.TargetStart(conn, pkt);

            sentAny = true;
        }

        if (useChunked && Debug.isDebugBuild)
        {
            int chunks = (compressedPayload.Length + StartPacketChunkBytes - 1) / StartPacketChunkBytes;
            Debug.Log($"[QuestNetSync][StartPacketChunk] Server broadcast quest='{pkt.questName}' raw={rawUtf8Bytes} compressed={compressedPayload.Length} chunks={chunks} recipients={sentConnections.Count}");
        }

        // Fallback for unusual host/server setups where owner connections were not found.
        // The direct fallback is safe only for a packet that was already classified small.
        if (!sentAny && !useChunked)
        {
            for (int i = 0; i < syncs.Length; i++)
            {
                QuestNetSync sync = syncs[i];
                if (sync != null && sync.isServer)
                {
                    sync.RpcStart(pkt);
                    return true;
                }
            }
        }

        // No recipients is not an encoding failure (for example, host is currently alone).
        return sentAny || syncs.Length > 0;
    }

    [Command]
    private void CmdRequestCurrent()
    {
        // Intentionally disabled. Connection is not consent to import the host's
        // active quests. Explicit one-time Share is the only missing-player import.
        if (Debug.isDebugBuild)
            Debug.Log("[QuestNetSync] Automatic late-join quest import is disabled; use Share Quest for missing players.");
    }

    [TargetRpc]
    private void TargetStart(NetworkConnection conn, StartPacket pkt)
    {
        ApplyStartPacket(pkt);
    }


    // Server can ask a client to send a full StartPacket for an already-running quest (e.g. client took quest in SP before connecting).
    [TargetRpc]
    public void TargetRequestQuestStartPacket(NetworkConnection conn, ulong questUID)
    {
        if (!isClient) return;

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning($"[QuestNetSync] TargetRequestQuestStartPacket: client has no quest uid={questUID}");
            return;
        }

        // Ensure we have an instanceId mapping for this already-running quest so our own RpcStart echo won't re-StartQuest locally.
        string inst;
        if (!_cliUid2Inst.TryGetValue(questUID, out inst) || string.IsNullOrEmpty(inst))
        {
            inst = Guid.NewGuid().ToString("N");
            _cliInst2Uid[inst] = questUID;
            _cliUid2Inst[questUID] = inst;
            _cliLastTasks[inst] = q.GetTaskStates();
            _cliLastLogs[inst]  = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
            _cliLastItems[inst] = CaptureItemStates(q);
            _cliLastPersons[inst] = BuildPersons(q);
            _cliLastFoes[inst] = BuildFoes(q);
            _cliQuestObjectByUid[questUID] = q;
        }

        string[] granted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
        int[] popups = CaptureGetItemPopupIdsForSymbols(q, granted);

        StartPacket pkt = BuildStartPacket(q, inst, _localNetId, granted, popups);
        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] TargetRequestQuestStartPacket: sending StartPacket uid={pkt.uid} taker={pkt.takerNetId} inst={pkt.instanceId}");

        // Reuse existing server reconstruction path, but chunk large main/generated quests.
        string sendError;
        if (!SendStartPacketToServerSmart(pkt, out sendError))
        {
            Debug.LogWarning("[QuestNetSync][StartPacketChunk] Requested quest start packet was not sent: " + sendError);
        }
        else
        {
            ApplyClientQuestChainAuthority(q, "requested-start-packet-sent");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Manual journal quest sharing
    // ─────────────────────────────────────────────────────────────────────────────
    public static bool IsQuestShareAvailableForJournal()
    {
        QuestNetSync local = LocalInstance;
        if (local == null)
            return false;

        return local.isLocalPlayer && (local.isServer || local.isClient) &&
               !IsQuestNetSyncPausedForLoad() && IsAuthoritativeTimeReadyForQuestSharing();
    }

    public static bool TryShareQuestFromJournal(ulong questUID)
    {
        string ignored;
        return TryShareQuestFromJournal(questUID, out ignored);
    }

    public static bool TryShareQuestFromJournal(ulong questUID, out string statusMessage)
    {
        statusMessage = string.Empty;

        QuestNetSync local = LocalInstance;
        if (local == null)
        {
            statusMessage = "Quest sharing is only available while multiplayer is active.";
            return false;
        }

        return local.ShareQuestFromJournalInternal(questUID, out statusMessage);
    }

    private bool ShareQuestFromJournalInternal(ulong questUID, out string statusMessage)
    {
        statusMessage = string.Empty;

        if (!isLocalPlayer)
        {
            statusMessage = "Quest sharing must be started by the local player.";
            return false;
        }

        if (IsQuestNetSyncPausedForLoad())
        {
            statusMessage = "Quest sharing is temporarily paused while the save/load state settles.";
            return false;
        }

        if (!IsAuthoritativeTimeReadyForQuestSharing())
        {
            statusMessage = "Quest sharing is temporarily paused until authoritative host time has synchronized.";
            return false;
        }

        if (QuestMachine.Instance == null)
        {
            statusMessage = "Quest sharing failed because the quest machine is not ready.";
            return false;
        }

        Quest q = QuestMachine.Instance.GetQuest(questUID);
        if (q == null)
        {
            statusMessage = "Quest sharing failed because this quest no longer exists locally.";
            return false;
        }

        if (q.QuestComplete || q.QuestTombstoned)
        {
            statusMessage = "Finished quests cannot be shared.";
            return false;
        }

        if (IsQuestSharingBlacklisted(q))
        {
            statusMessage = "This quest is local-only and cannot be shared in multiplayer.";
            LogQuestSharingBlacklisted(q, "TryShareQuestFromJournal");
            return false;
        }

        if (!isServer && !isClient)
        {
            statusMessage = "Quest sharing is only available while connected to multiplayer.";
            return false;
        }

        if (isServer)
            return ShareQuestFromJournalAsServer(q, out statusMessage);

        return ShareQuestFromJournalAsClient(q, out statusMessage);
    }

    private bool ShareQuestFromJournalAsServer(Quest q, out string statusMessage)
    {
        statusMessage = string.Empty;

        if (q == null)
        {
            statusMessage = "Quest sharing failed because the quest was missing.";
            return false;
        }

        Srv_PruneToActive();

        string existingInst;
        if (_srvUid2Inst.TryGetValue(q.UID, out existingInst) && !string.IsNullOrEmpty(existingInst))
        {
            uint existingTaker;
            if (!_questTakerByUid.TryGetValue(q.UID, out existingTaker) || existingTaker == 0U)
                existingTaker = _localNetId;
            string[] existingGranted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
            int[] existingPopups = CaptureGetItemPopupIdsForSymbols(q, existingGranted);
            StartPacket existingPkt = BuildStartPacket(q, existingInst, existingTaker, existingGranted, existingPopups, true);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][MissingOnlyShare] Host re-shared quest once uid={q.UID} name={q.QuestName} inst={existingInst}");

            if (!ServerBroadcastStartPacket(existingPkt))
            {
                statusMessage = "Quest sharing failed while preparing the network packet.";
                return false;
            }
            statusMessage = "Quest shared once with connected players who did not already have it.";
            return true;
        }

        string inst = ManualShareInstancePrefix + Guid.NewGuid().ToString("N");
        _srvUid2Inst[q.UID] = inst;
        _srvInst2Uid[inst] = q.UID;
        _srvQuestObjectByUid[q.UID] = q;

        _srvLastTasks[inst] = q.GetTaskStates();
        _srvLastLogs[inst] = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        _srvLastItems[inst] = CaptureItemStates(q);
        _srvLastPersons[inst] = BuildPersons(q);
        _srvLastPlaces[inst] = BuildPlaces(q);
        _srvLastFoes[inst] = BuildFoes(q);

        // Host mode also has a local client copy. Pre-bind the exact server instance so the
        // host's own RpcStart echo only applies state and never starts/binds a duplicate.
        if (isClient && !_cliUid2Inst.ContainsKey(q.UID))
        {
            _cliInst2Uid[inst] = q.UID;
            _cliUid2Inst[q.UID] = inst;
            _cliLastTasks[inst] = q.GetTaskStates();
            _cliLastLogs[inst] = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
            _cliLastItems[inst] = CaptureItemStates(q);
            _cliLastPersons[inst] = BuildPersons(q);
            _cliLastFoes[inst] = BuildFoes(q);
            _cliQuestObjectByUid[q.UID] = q;
            _startedInst.Add(inst);
        }

        _questTakerByUid[q.UID] = _localNetId;
        string[] granted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
        int[] popups = CaptureGetItemPopupIdsForSymbols(q, granted);
        StartPacket pkt = BuildStartPacket(q, inst, _localNetId, granted, popups, true);

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][ManualShare] Host shared quest uid={q.UID} name={q.QuestName} inst={inst}");

        if (!ServerBroadcastStartPacket(pkt))
        {
            statusMessage = "Quest sharing failed while preparing the network packet.";
            return false;
        }
        statusMessage = "Quest shared once with connected players who did not already have it.";
        return true;
    }

    private bool ShareQuestFromJournalAsClient(Quest q, out string statusMessage)
    {
        statusMessage = string.Empty;

        if (q == null)
        {
            statusMessage = "Quest sharing failed because the quest was missing.";
            return false;
        }

        string existingInst;
        bool alreadyMapped = _cliUid2Inst.TryGetValue(q.UID, out existingInst) && !string.IsNullOrEmpty(existingInst);
        string inst = alreadyMapped ? existingInst : (ManualShareInstancePrefix + Guid.NewGuid().ToString("N"));
        uint shareTaker = _localNetId;
        if (alreadyMapped)
        {
            uint existingTaker;
            if (_questTakerByUid.TryGetValue(q.UID, out existingTaker) && existingTaker != 0U)
                shareTaker = existingTaker;
        }
        else
        {
            _questTakerByUid[q.UID] = _localNetId;
        }

        string[] granted = CaptureGrantedQuestSymbolsFromInventory(q.UID);
        int[] popups = CaptureGetItemPopupIdsForSymbols(q, granted);
        StartPacket pkt = BuildStartPacket(q, inst, shareTaker, granted, popups, true);

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][ManualShare] Client requested quest share uid={q.UID} name={q.QuestName} inst={inst} alreadyMapped={alreadyMapped}");

        // First share: do not pre-register the client mapping here. If the server accepts
        // this packet, our own TargetStart echo will bind to the existing local quest by UID.
        // Re-share: the existing manual-share instance is already mapped locally and the
        // server will only re-broadcast it to currently connected players. Large/manual
        // packets use compressed chunks so Mirror never has to UTF-8 encode one huge RPC.
        string sendError;
        if (!SendStartPacketToServerSmart(pkt, out sendError))
        {
            statusMessage = "Quest sharing failed while preparing the network packet: " + sendError;
            return false;
        }

        ApplyClientQuestChainAuthority(q, "manual-share-packet-sent");

        statusMessage = alreadyMapped
            ? "Quest shared once with connected players who did not already have it."
            : "Quest share request sent to connected players who do not already have it.";
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Build & Apply helpers
    // ─────────────────────────────────────────────────────────────────────────────
    private static bool IsNetworkControlTask(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return false;
        if (symbol.Equals("pickspawn", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(symbol, @"^spawn\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static HashSet<string> GetLocalPcAtTargetTasks(Quest q)
    {
        if (q == null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> cached;
        if (_localPcAtTargetTasksByQuest.TryGetValue(q, out cached))
            return cached;

        cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "PcAt",
                            StringComparison.Ordinal))
                        continue;

                    FieldInfo taskField = action.GetType().GetField(
                        "taskSymbol",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);
                    if (taskField == null)
                        continue;

                    string targetTask =
                        GetSymbolName(taskField.GetValue(action));
                    if (!string.IsNullOrEmpty(targetTask))
                        cached.Add(targetTask);
                }
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][PcAtTraffic] Could not scan PcAt targets " +
                    $"uid={q.UID} quest='{q.QuestName}': {ex.Message}");
            }
        }

        _localPcAtTargetTasksByQuest[q] = cached;

        if (Debug.isDebugBuild && cached.Count > 0)
        {
            Debug.Log(
                $"[QuestNetSync][PcAtTraffic] Routine task mirroring ignores local " +
                $"PcAt target task(s) uid={q.UID} quest='{q.QuestName}': " +
                string.Join(",", cached.OrderBy(x => x).ToArray()));
        }

        return cached;
    }

    private static bool TaskHasAnyRuntimeAction(
        Quest q,
        string taskSymbol)
    {
        if (q == null || string.IsNullOrEmpty(taskSymbol))
            return false;

        try
        {
            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                return false;

            foreach (IQuestAction action in task.Actions)
            {
                if (action != null)
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TaskHasActionType(
        Quest q,
        string taskSymbol,
        string actionType)
    {
        if (q == null ||
            string.IsNullOrEmpty(taskSymbol) ||
            string.IsNullOrEmpty(actionType))
            return false;

        try
        {
            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                return false;

            foreach (IQuestAction action in task.Actions)
            {
                if (action != null &&
                    string.Equals(
                        action.GetType().Name,
                        actionType,
                        StringComparison.Ordinal))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool IsPureLocalPcAtSensorTask(
        Quest q,
        string taskSymbol)
    {
        if (q == null ||
            string.IsNullOrEmpty(taskSymbol) ||
            !GetLocalPcAtTargetTasks(q).Contains(taskSymbol))
            return false;

        // A QBN "variable" target has no runtime actions. It is controlled solely
        // by this machine's local PcAt evaluation and must never be broadcast.
        return !TaskHasAnyRuntimeAction(q, taskSymbol);
    }

    private static bool WhenConditionReferencesAnyTask(
        IQuestAction action,
        HashSet<string> taskSymbols)
    {
        if (action == null ||
            taskSymbols == null ||
            taskSymbols.Count == 0)
            return false;

        string source = action.DebugSource ?? string.Empty;
        bool isWhenAction =
            string.Equals(
                action.GetType().Name,
                "When",
                StringComparison.Ordinal);
        bool looksLikeWhen =
            source.TrimStart().StartsWith(
                "when ",
                StringComparison.OrdinalIgnoreCase);

        if (!isWhenAction && !looksLikeWhen)
            return false;

        string normalized = source.Replace("_", string.Empty);
        System.Text.RegularExpressions.MatchCollection tokens =
            System.Text.RegularExpressions.Regex.Matches(
                normalized,
                @"[A-Za-z][A-Za-z0-9.]*");

        for (int i = 0; i < tokens.Count; i++)
        {
            if (taskSymbols.Contains(tokens[i].Value))
                return true;
        }

        return false;
    }

    private static HashSet<string>
        GetLocalConditionDependentTasks(Quest q)
    {
        if (q == null)
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        HashSet<string> cached;
        if (_localConditionDependentTasksByQuest.TryGetValue(
                q,
                out cached))
            return cached;

        HashSet<string> direct =
            GetLocalPcAtTargetTasks(q);
        cached = new HashSet<string>(
            direct,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            bool added;
            do
            {
                added = false;

                for (int i = 0; i < tasks.Count; i++)
                {
                    DaggerfallWorkshop.Game.Questing.Task task =
                        tasks[i];
                    if (task == null ||
                        task.Symbol == null ||
                        task.Actions == null)
                        continue;

                    string ownerTask = task.Symbol.Name;
                    if (string.IsNullOrEmpty(ownerTask) ||
                        cached.Contains(ownerTask))
                        continue;

                    foreach (IQuestAction action in task.Actions)
                    {
                        if (!WhenConditionReferencesAnyTask(
                                action,
                                cached))
                            continue;

                        cached.Add(ownerTask);
                        added = true;
                        break;
                    }
                }
            }
            while (added);
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][PcAtTraffic] Could not scan local condition " +
                    $"dependency chain uid={q.UID} quest='{q.QuestName}': " +
                    ex.Message);
            }
        }

        _localConditionDependentTasksByQuest[q] = cached;

        if (Debug.isDebugBuild && cached.Count > 0)
        {
            Debug.Log(
                $"[QuestNetSync][PcAtTraffic] Local-only condition chain " +
                $"uid={q.UID} quest='{q.QuestName}' direct=[" +
                string.Join(",", direct.OrderBy(x => x).ToArray()) +
                "] all=[" +
                string.Join(",", cached.OrderBy(x => x).ToArray()) +
                "]");
        }

        return cached;
    }

    private static bool IsLocalPcAtTargetTask(Quest q, string symbol)
    {
        if (q == null || string.IsNullOrEmpty(symbol))
            return false;

        return GetLocalPcAtTargetTasks(q).Contains(symbol);
    }

    private static bool IsRoutineTaskSyncExcluded(Quest q, string symbol)
    {
        if (IsNetworkControlTask(symbol))
            return true;

        if (q == null || string.IsNullOrEmpty(symbol))
            return false;

        // TotingItemAndClickedNpc is replicated by an exact interaction packet that
        // carries the item, NPC, owning task, popup, and source player. Mirroring the
        // same task boolean through routine snapshots can arrive before that packet and
        // execute GetItem/Say/Prompt without the interaction context, or execute it a
        // second time afterwards. Let the exact event own live progression; full quest
        // save-state import still preserves completed task/action state for catch-up.
        if (TaskHasActionType(q, symbol, "TotingItemAndClickedNpc"))
            return true;

        return GetLocalConditionDependentTasks(q).Contains(symbol);
    }

    private static Dictionary<string, bool> BuildRoutineTaskStateMap(
        Quest q,
        Quest.TaskState[] states)
    {
        Dictionary<string, bool> result =
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);

        if (states == null)
            return result;

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null)
                continue;

            string symbol = states[i].symbol.Name;
            if (IsRoutineTaskSyncExcluded(q, symbol))
                continue;

            result[symbol] = states[i].set;
        }

        return result;
    }

    private static TaskStateDTO[] ToTaskDTOs(
        Quest q,
        Quest.TaskState[] states)
    {
        if (states == null)
            return new TaskStateDTO[0];

        List<TaskStateDTO> list =
            new List<TaskStateDTO>(states.Length);
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null)
                continue;

            string symbol = states[i].symbol.Name;
            if (IsRoutineTaskSyncExcluded(q, symbol))
                continue;

            list.Add(new TaskStateDTO
            {
                symbol = symbol,
                set = states[i].set,
            });
        }

        return list.ToArray();
    }

    private static TaskStateDTO[] BuildTaskProgressDeltas(
        Quest q,
        Quest.TaskState[] before,
        Quest.TaskState[] after)
    {
        if (before == null || after == null)
            return new TaskStateDTO[0];

        Dictionary<string, bool> oldBySymbol =
            BuildRoutineTaskStateMap(q, before);

        List<TaskStateDTO> changes =
            new List<TaskStateDTO>();
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i].symbol == null)
                continue;

            string symbol = after[i].symbol.Name;
            if (IsRoutineTaskSyncExcluded(q, symbol))
                continue;

            bool oldValue;
            if (!oldBySymbol.TryGetValue(symbol, out oldValue) ||
                oldValue == after[i].set)
                continue;

            // A pure client contributes progress edges, not rollback/continuous local
            // condition state. Sending true->false clears to the host is unsafe:
            // PcAt/When/Daily actions intentionally evaluate differently for players
            // in different places. Clearing an already-triggered host action task can
            // rearm PlaceFoe, GivePc, Say, or EndQuest and execute it again every tick.
            //
            // Explicit multiplayer event paths (Prompt, PcAt, clicks, pickups, foe
            // progress, and quest-end packets) already replay the meaningful action on
            // the server. Raw false task state is therefore never a valid client
            // contribution.
            if (!after[i].set)
                continue;

            changes.Add(new TaskStateDTO
            {
                symbol = symbol,
                set = true,
            });
        }

        return changes.ToArray();
    }

    private static LogDeltaDTO[] BuildLogProgressDeltas(HashSet<int> beforeSteps, Quest.LogEntry[] after)
    {
        if (beforeSteps == null || after == null)
            return new LogDeltaDTO[0];

        Dictionary<int, int> afterMessages = new Dictionary<int, int>();
        for (int i = 0; i < after.Length; i++)
            afterMessages[after[i].stepID] = after[i].messageID;

        List<LogDeltaDTO> changes = new List<LogDeltaDTO>();
        foreach (int oldStep in beforeSteps)
        {
            if (!afterMessages.ContainsKey(oldStep))
                changes.Add(new LogDeltaDTO { stepID = oldStep, messageID = 0, present = false });
        }

        foreach (KeyValuePair<int, int> entry in afterMessages)
        {
            if (!beforeSteps.Contains(entry.Key))
                changes.Add(new LogDeltaDTO { stepID = entry.Key, messageID = entry.Value, present = true });
        }

        return changes.ToArray();
    }

    private static bool QuestHasFoeAction(
        Quest q,
        string actionTypeName,
        string foeSymbol,
        bool requireComplete)
    {
        if (q == null || string.IsNullOrEmpty(actionTypeName) || string.IsNullOrEmpty(foeSymbol))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(action.GetType().Name, actionTypeName, StringComparison.Ordinal))
                        continue;

                    if (requireComplete && !action.IsComplete)
                        continue;

                    FieldInfo foeField = action.GetType().GetField(
                        "foeSymbol",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (foeField == null)
                        continue;

                    string actionFoeSymbol = GetSymbolName(foeField.GetValue(action));
                    if (string.Equals(actionFoeSymbol, foeSymbol, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void LogIgnoredVolatileFoeState(
        Quest q,
        string foeSymbol,
        string fieldName,
        bool oldValue,
        bool newValue,
        string reason)
    {
        if (!Debug.isDebugBuild)
            return;

        string key =
            (q != null ? q.UID.ToString() : "0") + "|" +
            (foeSymbol ?? string.Empty) + "|" +
            (fieldName ?? string.Empty) + "|" +
            oldValue.ToString() + ">" + newValue.ToString() + "|" +
            (reason ?? string.Empty);

        if (!_ignoredVolatileFoeStateLogs.Add(key))
            return;

        Debug.LogWarning(
            $"[QuestNetSync][FoeTraffic] Ignored unscripted volatile foe state " +
            $"uid={(q != null ? q.UID : 0UL)} quest='{(q != null ? q.QuestName : "<null>")}' " +
            $"foe='{foeSymbol}' field={fieldName} {oldValue}->{newValue} reason={reason}");
    }

    private static FoeDTO[] SanitizeVolatileFoeStateForNetwork(
        Quest q,
        FoeDTO[] baseline,
        FoeDTO[] current,
        string reason)
    {
        if (q == null || baseline == null || current == null ||
            baseline.Length == 0 || current.Length == 0)
            return current ?? new FoeDTO[0];

        Dictionary<string, FoeDTO> oldBySymbol =
            new Dictionary<string, FoeDTO>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < baseline.Length; i++)
        {
            if (!string.IsNullOrEmpty(baseline[i].symbol))
                oldBySymbol[baseline[i].symbol] = baseline[i];
        }

        for (int i = 0; i < current.Length; i++)
        {
            FoeDTO oldFoe;
            if (string.IsNullOrEmpty(current[i].symbol) ||
                !oldBySymbol.TryGetValue(current[i].symbol, out oldFoe))
                continue;

            // injuredTrigger is useful only to quests that actually contain an
            // InjuredFoe trigger for this symbol. Otherwise it is transient combat
            // state and must not drive the global quest snapshot.
            if (current[i].injuredTrigger != oldFoe.injuredTrigger &&
                !QuestHasFoeAction(q, "InjuredFoe", current[i].symbol, false))
            {
                LogIgnoredVolatileFoeState(
                    q,
                    current[i].symbol,
                    "injuredTrigger",
                    oldFoe.injuredTrigger,
                    current[i].injuredTrigger,
                    reason);
                FoeDTO fixedFoe = current[i];
                fixedFoe.injuredTrigger = oldFoe.injuredTrigger;
                current[i] = fixedFoe;
            }

            // Restrained is a scripted quest state. Accept only a direction backed by
            // a completed RestrainFoe/UnrestrainFoe action. This blocks dungeon enemy
            // authority/activation code from bouncing the flag between machines while
            // preserving quests that intentionally restrain or unrestrain a foe.
            if (current[i].restrained != oldFoe.restrained)
            {
                string requiredAction = current[i].restrained ? "RestrainFoe" : "UnrestrainFoe";
                if (!QuestHasFoeAction(q, requiredAction, current[i].symbol, true))
                {
                    LogIgnoredVolatileFoeState(
                        q,
                        current[i].symbol,
                        "restrained",
                        oldFoe.restrained,
                        current[i].restrained,
                        reason);
                    FoeDTO fixedFoe = current[i];
                    fixedFoe.restrained = oldFoe.restrained;
                    current[i] = fixedFoe;
                }
            }
        }

        return current;
    }

    private static FoeProgressDeltaDTO[] BuildFoeProgressDeltas(
        Quest q,
        FoeDTO[] before,
        FoeDTO[] after)
    {
        if (before == null || after == null)
            return new FoeProgressDeltaDTO[0];

        // Strip combat/authority noise before comparing and before storing the next
        // client baseline. Without this, one volatile bool can trigger CmdClientDelta
        // and a complete server RpcUpdate ten times per second.
        after = SanitizeVolatileFoeStateForNetwork(q, before, after, "client-delta");

        Dictionary<string, FoeDTO> oldBySymbol = new Dictionary<string, FoeDTO>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < before.Length; i++)
        {
            if (!string.IsNullOrEmpty(before[i].symbol))
                oldBySymbol[before[i].symbol] = before[i];
        }

        List<FoeProgressDeltaDTO> changes = new List<FoeProgressDeltaDTO>();
        for (int i = 0; i < after.Length; i++)
        {
            FoeDTO oldFoe;
            if (string.IsNullOrEmpty(after[i].symbol) || !oldBySymbol.TryGetValue(after[i].symbol, out oldFoe))
                continue;

            int killDelta = after[i].killCount - oldFoe.killCount;
            bool injuredChanged = after[i].injuredTrigger && !oldFoe.injuredTrigger;
            bool restrainedChanged = after[i].restrained != oldFoe.restrained;

            // A lower count is a save/load rollback, not multiplayer progress.
            if (killDelta <= 0 && !injuredChanged && !restrainedChanged)
                continue;

            changes.Add(new FoeProgressDeltaDTO
            {
                symbol = after[i].symbol,
                killCountCandidate = killDelta > 0 ? after[i].killCount : oldFoe.killCount,
                injuredChanged = injuredChanged,
                injuredTrigger = after[i].injuredTrigger,
                restrainedChanged = restrainedChanged,
                restrained = after[i].restrained,
            });
        }

        return changes.ToArray();
    }

    private static void ApplyTaskAndLogProgressDeltas(Quest q, TaskStateDTO[] taskChanges, LogDeltaDTO[] logChanges)
    {
        if (q == null)
            return;

        Dictionary<string, bool> current = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Quest.TaskState[] states = q.GetTaskStates() ?? new Quest.TaskState[0];
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol != null)
                current[states[i].symbol.Name] = states[i].set;
        }

        if (taskChanges != null)
        {
            for (int i = 0; i < taskChanges.Length; i++)
            {
                string symbol = taskChanges[i].symbol;
                if (string.IsNullOrEmpty(symbol) ||
                    IsRoutineTaskSyncExcluded(q, symbol) ||
                    TaskHasActionType(q, symbol, "EndQuest"))
                    continue;

                // Server-side safety for mixed/older clients: client task packets are
                // progress-only. Never let a client clear an authoritative task. In S7,
                // the away client repeatedly sent S.20=false while the host's local
                // PcAt/When condition kept S.20 true. Every accepted clear rearmed
                // PlaceFoe and produced an unbounded Wereboar quest-foe spawn loop.
                if (!taskChanges[i].set)
                    continue;

                if (IsPrematureLordKavarRulerClickTask(q, symbol))
                {
                    Debug.Log(
                        $"[QuestNetSync][KavarStateRepair] Rejected premature client-delta " +
                        $"_S.30_ uid={q.UID} before K'avar outcome.");
                    continue;
                }

                bool have;
                if (!current.TryGetValue(symbol, out have) || have)
                    continue;

                Symbol taskSymbol = new Symbol(symbol);
                q.StartTask(taskSymbol);
                current[symbol] = true;
            }
        }

        if (logChanges == null)
            return;

        HashSet<int> haveSteps = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
        for (int i = 0; i < logChanges.Length; i++)
        {
            LogDeltaDTO change = logChanges[i];
            if (change.present)
            {
                if (haveSteps.Add(change.stepID))
                    q.AddLogStep(change.stepID, change.messageID);
            }
            else if (haveSteps.Remove(change.stepID))
            {
                q.RemoveLogStep(change.stepID);
            }
        }
    }

    private static void ApplyFoeProgressDeltas(Quest q, FoeProgressDeltaDTO[] changes)
    {
        if (q == null || changes == null || changes.Length == 0)
            return;

        Type foeType = typeof(Foe);
        FieldInfo injuredField = foeType.GetField("injuredTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo restrainedField = foeType.GetField("restrained", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo killField = foeType.GetField("killCount", BindingFlags.Instance | BindingFlags.NonPublic);

        for (int i = 0; i < changes.Length; i++)
        {
            FoeProgressDeltaDTO change = changes[i];
            if (string.IsNullOrEmpty(change.symbol))
                continue;

            Foe foe = FindResourceBySymbol<Foe>(q, change.symbol);
            if (foe == null)
                continue;

            if (killField != null && change.killCountCandidate > 0)
            {
                int currentKills = (int)killField.GetValue(foe);
                if (change.killCountCandidate > currentKills)
                    killField.SetValue(foe, change.killCountCandidate);
            }

            if (injuredField != null && change.injuredChanged)
            {
                bool wasInjured = (bool)injuredField.GetValue(foe);
                injuredField.SetValue(foe, change.injuredTrigger);
                if (!wasInjured && change.injuredTrigger)
                    ReplayFoeInjuredMessageIfNeeded(q, change.symbol);
            }

            if (restrainedField != null && change.restrainedChanged)
                restrainedField.SetValue(foe, change.restrained);
        }
    }

    private static void ApplyItemStackCounts(Quest q, ItemDTO[] itemStacks)
    {
        if (q == null || itemStacks == null)
            return;

        for (int i = 0; i < itemStacks.Length; i++)
        {
            ItemDTO dto = itemStacks[i];
            if (string.IsNullOrEmpty(dto.symbol))
                continue;

            Item item = q.GetItem(new Symbol(dto.symbol));
            if (item == null || item.DaggerfallUnityItem == null)
                continue;

            item.DaggerfallUnityItem.stackCount =
                NormalizeQuestItemStackCountForDto(item.DaggerfallUnityItem, dto.stackCount);

            // Client deltas are otherwise sparse/monotonic. MakePermanent is also
            // monotonic: once a quest item becomes permanent it must never be turned
            // back into a quest item by another participant's older state.
            if (dto.madePermanent && !item.MadePermanent)
                ApplyQuestItemMadePermanent(q, dto.symbol, "client-item-delta");
        }
    }


    private static string[] FindTasksWithCompletedAction(Quest q, string actionTypeName)
    {
        if (q == null || string.IsNullOrEmpty(actionTypeName))
            return new string[0];

        List<string> result = new List<string>();
        Quest.TaskState[] states = q.GetTaskStates();
        if (states == null)
            return new string[0];

        for (int i = 0; i < states.Length; i++)
        {
            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(states[i].symbol);
            if (task == null || task.Symbol == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null)
                    continue;

                if (string.Equals(action.GetType().Name, actionTypeName, StringComparison.Ordinal) && action.IsComplete)
                {
                    result.Add(task.Symbol.Name);
                    break;
                }
            }
        }

        return result.Distinct().ToArray();
    }

    private static string[] MergeTaskSymbolArrays(params string[][] arrays)
    {
        List<string> result = new List<string>();
        if (arrays == null)
            return new string[0];

        for (int i = 0; i < arrays.Length; i++)
        {
            string[] arr = arrays[i];
            if (arr == null)
                continue;

            for (int j = 0; j < arr.Length; j++)
            {
                string symbol = arr[j];
                if (!string.IsNullOrEmpty(symbol))
                    result.Add(symbol);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsGivePcNothingAction(IQuestAction action)
    {
        if (action == null || !string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal))
            return false;

        try
        {
            FieldInfo f = action.GetType().GetField("isNothing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
            {
                object value = f.GetValue(action);
                if (value is bool)
                    return (bool)value;
            }
        }
        catch { }

        return false;
    }

    private static string[] FindRewardTasksFromClientTaskStates(Quest q, TaskStateDTO[] taskStates)
    {
        // The finishing client knows which final task state was active/completed when
        // it received the reward. Mirror that hint on the host so the host can replay
        // GivePc immediately without needing to talk to the quest giver.
        if (q == null || taskStates == null || taskStates.Length == 0)
            return new string[0];

        List<string> result = new List<string>();
        for (int i = 0; i < taskStates.Length; i++)
        {
            if (!taskStates[i].set || string.IsNullOrEmpty(taskStates[i].symbol))
                continue;

            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskStates[i].symbol));
            if (task == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action != null &&
                    string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal) &&
                    !IsGivePcNothingAction(action))
                {
                    result.Add(taskStates[i].symbol);
                    break;
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] FindTasksWithRewardActionToReplay(Quest q)
    {
        // At quest end the reward task may already have completed its GivePc action,
        // or it may only be the currently-triggered final task depending on exact
        // quest timing. Send both categories so remote machines can reproduce the
        // local reward popup/trade flow.
        if (q == null)
            return new string[0];

        List<string> result = new List<string>();
        Quest.TaskState[] states = q.GetTaskStates();
        Dictionary<string, bool> taskTriggered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (states != null)
        {
            for (int i = 0; i < states.Length; i++)
                if (states[i].symbol != null)
                    taskTriggered[states[i].symbol.Name] = states[i].set;
        }

        Quest.TaskState[] allStates = states ?? new Quest.TaskState[0];
        for (int i = 0; i < allStates.Length; i++)
        {
            string taskName = allStates[i].symbol != null ? allStates[i].symbol.Name : null;
            if (string.IsNullOrEmpty(taskName))
                continue;

            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskName));
            if (task == null)
                continue;

            bool hasGivePc = false;
            bool givePcComplete = false;
            foreach (IQuestAction action in task.Actions)
            {
                if (action == null)
                    continue;

                if (string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal) &&
                    !IsGivePcNothingAction(action))
                {
                    hasGivePc = true;
                    if (action.IsComplete)
                        givePcComplete = true;
                }
            }

            bool triggered = false;
            taskTriggered.TryGetValue(taskName, out triggered);
            if (hasGivePc && (givePcComplete || triggered))
                result.Add(taskName);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }


    private static string[] FindIncompleteGivePcTasks(Quest q)
    {
        if (q == null)
            return new string[0];

        List<string> result = new List<string>();
        Quest.TaskState[] states = q.GetTaskStates();
        if (states == null)
            return new string[0];

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null)
                continue;

            string taskName = states[i].symbol.Name;
            if (string.IsNullOrEmpty(taskName))
                continue;

            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskName));
            if (task == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null)
                    continue;

                if (!string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal))
                    continue;

                if (IsGivePcNothingAction(action))
                    continue;

                if (!action.IsComplete)
                {
                    result.Add(taskName);
                    break;
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }


    private static string[] FindAllGivePcTasks(Quest q)
    {
        if (q == null)
            return new string[0];

        List<string> result = new List<string>();
        Quest.TaskState[] states = q.GetTaskStates();
        if (states == null)
            return new string[0];

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null)
                continue;

            string taskName = states[i].symbol.Name;
            if (string.IsNullOrEmpty(taskName))
                continue;

            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskName));
            if (task == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null)
                    continue;

                if (string.Equals(action.GetType().Name, "GivePc", StringComparison.Ordinal) &&
                    !IsGivePcNothingAction(action))
                {
                    result.Add(taskName);
                    break;
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasPendingRewardReplayForQuest(ulong questUID)
    {
        if (questUID == 0UL || _pendingRewardReplayKeys.Count == 0)
            return false;

        string prefix = questUID.ToString() + "|reward-replay|";
        foreach (string key in _pendingRewardReplayKeys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IQuestAction FindGivePcAction(
        DaggerfallWorkshop.Game.Questing.Task task)
    {
        if (task == null || task.Actions == null)
            return null;

        foreach (IQuestAction action in task.Actions)
        {
            if (action != null &&
                string.Equals(
                    action.GetType().Name,
                    "GivePc",
                    StringComparison.Ordinal))
                return action;
        }

        return null;
    }

    private IEnumerator CoReplayRewardTaskWhenUiReady(
        ulong questUID,
        string taskSymbol,
        bool forceEvenIfComplete,
        bool givePcWasCompleteWhenQueued)
    {
        string replayKey =
            MakeRewardReplayKey(
                questUID,
                taskSymbol);

        // Let the popup/reward window that caused this deferral become the stable top
        // window before polling it. Then wait without imposing an arbitrary frame count;
        // the player controls when modal dialogue is dismissed.
        yield return null;
        while (ShouldDeferQuestInventoryApplyNow())
        {
            Quest waitingQuest =
                QuestMachine.Instance != null
                    ? QuestMachine.Instance.GetQuest(questUID)
                    : null;
            if (waitingQuest == null ||
                waitingQuest.QuestTombstoned)
            {
                _pendingRewardReplayKeys.Remove(replayKey);
                yield break;
            }

            yield return null;
        }

        Quest q =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUID)
                : null;
        if (q == null || q.QuestTombstoned)
        {
            _pendingRewardReplayKeys.Remove(replayKey);
            yield break;
        }

        DaggerfallWorkshop.Game.Questing.Task task =
            q.GetTask(new Symbol(taskSymbol));
        IQuestAction givePc =
            FindGivePcAction(task);

        // The normal local task was still running while the remote end packet arrived.
        // If it has now completed GivePc naturally, that local reward is the one to keep;
        // do not replay a second reward after the popup closes.
        if (!givePcWasCompleteWhenQueued &&
            givePc != null &&
            givePc.IsComplete)
        {
            _pendingRewardReplayKeys.Remove(replayKey);
            _remoteRewardReplayApplied.Add(replayKey);

            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][RewardReplay] Natural GivePc completed while " +
                    $"waiting for UI uid={questUID} task='{taskSymbol}'; skipped replay.");
            }
            yield break;
        }

        _pendingRewardReplayKeys.Remove(replayKey);

        ForceReplayRewardTasksIfNeeded(
            q,
            new string[] { taskSymbol },
            forceEvenIfComplete);
    }

    private static void ForceReplayRewardTasksIfNeeded(Quest q, string[] taskSymbols, bool forceEvenIfComplete = false)
    {
        if (q == null || taskSymbols == null || taskSymbols.Length == 0)
            return;

        for (int i = 0; i < taskSymbols.Length; i++)
        {
            string taskSymbol = taskSymbols[i];
            if (string.IsNullOrEmpty(taskSymbol))
                continue;

            string rewardReplayKey = MakeRewardReplayKey(q.UID, taskSymbol);
            if (_remoteRewardReplayApplied.Contains(rewardReplayKey) ||
                _pendingRewardReplayKeys.Contains(rewardReplayKey))
                continue;

            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (task == null)
                continue;

            IQuestAction givePc =
                FindGivePcAction(task);
            if (givePc == null)
                continue;

            // Do not force-replay "give pc nothing" as a remote reward. Some vanilla quests
            // use it as an early quest-start side effect (A0C41Y18), and the old fallback
            // could replay that start popup when the quest ended.
            if (IsGivePcNothingAction(givePc))
                continue;

            // If the reward action already completed on this machine, normally do not replay it.
            // For remote-client completion on host, ApplyDesiredState can mark GivePc complete
            // without actually giving the host the reward, so forceEvenIfComplete overrides this.
            if (givePc.IsComplete && !forceEvenIfComplete)
                continue;

            // Never open/force a GivePc reward underneath another modal DFU window.
            // S0000011 can have Say 1014/1020/1030 active while the end packet arrives.
            // Immediate replay at that point is what produced missing popup sequences and
            // rewards falling to the ground.
            if (ShouldDeferQuestInventoryApplyNow() &&
                LocalInstance != null)
            {
                bool wasCompleteWhenQueued =
                    givePc.IsComplete;

                if (_pendingRewardReplayKeys.Add(rewardReplayKey))
                {
                    LocalInstance.StartCoroutine(
                        LocalInstance.CoReplayRewardTaskWhenUiReady(
                            q.UID,
                            taskSymbol,
                            forceEvenIfComplete,
                            wasCompleteWhenQueued));

                    if (Debug.isDebugBuild)
                    {
                        Debug.Log(
                            $"[QuestNetSync][RewardReplay] Deferred GivePc until UI clears " +
                            $"uid={q.UID} task='{taskSymbol}' force={forceEvenIfComplete} " +
                            $"wasComplete={wasCompleteWhenQueued}");
                    }
                }
                continue;
            }

            // GivePc disables normal RearmAction() (allowRearm=false), so we must force
            // the ActionTemplate completion flag directly. This is still safe because
            // we only do it when the local GivePc has not completed yet.
            ActionTemplate template = givePc as ActionTemplate;
            if (template != null)
                template.IsComplete = false;

            // For delayed/notify variants this bypasses town/hour waiting. For normal
            // quest rewards it is harmless, but still ensures immediate local playback.
            MethodInfo offerNow = givePc.GetType().GetMethod(
                "OfferImmediately",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            if (offerNow != null)
                offerNow.Invoke(givePc, null);

            // Execute ONLY the GivePc reward action here. Do NOT call task.Start().
            // Many final quest tasks contain both "give pc ..." and "end quest".
            // Starting the whole task remotely can let the vanilla EndQuest action run,
            // then CoFinishRemoteEndedQuest() can call EndQuest() again a few frames later.
            // That leaves item/gold mostly correct but can apply faction reputation twice.
            try
            {
                givePc.Update(task);
                _remoteRewardReplayApplied.Add(rewardReplayKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestNetSync] Remote GivePc replay failed for task '{taskSymbol}' uid={q.UID}: {ex.Message}");
            }

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Replayed remote GivePc reward task '{taskSymbol}' for quest uid={q.UID}");
        }
    }

    private static bool ShouldWriteQuestTrafficTrace(string key)
    {
        float now = Time.realtimeSinceStartup;
        float next;
        if (_nextQuestTrafficTraceTime.TryGetValue(key, out next) && now < next)
            return false;

        _nextQuestTrafficTraceTime[key] = now + 1.0f;
        return true;
    }

    private static string DescribeTaskDifference(
        Quest q,
        Quest.TaskState[] before,
        Quest.TaskState[] after)
    {
        if (before == null || after == null)
            return $"tasks:null before={(before == null)} after={(after == null)}";

        Dictionary<string, bool> oldBySymbol =
            BuildRoutineTaskStateMap(q, before);
        Dictionary<string, bool> nowBySymbol =
            BuildRoutineTaskStateMap(q, after);

        foreach (KeyValuePair<string, bool> entry in nowBySymbol)
        {
            bool oldValue;
            if (!oldBySymbol.TryGetValue(entry.Key, out oldValue))
                return $"task-added:{entry.Key}={entry.Value}";

            if (oldValue != entry.Value)
                return $"task:{entry.Key} {oldValue}->{entry.Value}";
        }

        foreach (KeyValuePair<string, bool> entry in oldBySymbol)
        {
            if (!nowBySymbol.ContainsKey(entry.Key))
                return $"task-removed:{entry.Key}={entry.Value}";
        }

        return "task-map-difference";
    }

    private static string DescribeLogDifference(
        HashSet<int> before,
        Quest.LogEntry[] after)
    {
        if (before == null || after == null)
            return $"logs:null before={(before == null)} after={(after == null)}";

        HashSet<int> now = new HashSet<int>(after.Select(x => x.stepID));
        foreach (int step in before)
        {
            if (!now.Contains(step))
                return $"log-removed:{step}";
        }

        foreach (int step in now)
        {
            if (!before.Contains(step))
                return $"log-added:{step}";
        }

        return "log-message-or-order";
    }

    private static string DescribeItemDifference(
        Dictionary<string, ItemState> before,
        Dictionary<string, ItemState> after)
    {
        if (before == null || after == null)
            return $"items:null before={(before == null)} after={(after == null)}";

        foreach (KeyValuePair<string, ItemState> entry in before)
        {
            ItemState now;
            if (!after.TryGetValue(entry.Key, out now))
                return $"item-removed:{entry.Key}";

            ItemState old = entry.Value;
            if (old.stackCount != now.stackCount)
                return $"item:{entry.Key}.stack {old.stackCount}->{now.stackCount}";
            if (old.hasPlayerClicked != now.hasPlayerClicked)
                return $"item:{entry.Key}.clicked {old.hasPlayerClicked}->{now.hasPlayerClicked}";
            if (old.isHidden != now.isHidden)
                return $"item:{entry.Key}.hidden {old.isHidden}->{now.isHidden}";
            if (old.inPlayerInventory != now.inPlayerInventory)
                return $"item:{entry.Key}.inventory {old.inPlayerInventory}->{now.inPlayerInventory}";
        }

        foreach (string symbol in after.Keys)
        {
            if (!before.ContainsKey(symbol))
                return $"item-added:{symbol}";
        }

        return "item-unknown";
    }

    private static string DescribePlaceDifference(
        PlaceDTO[] before,
        PlaceDTO[] after)
    {
        if (before == null || after == null)
            return $"places:null before={(before == null)} after={(after == null)}";

        Dictionary<string, PlaceDTO> oldBySymbol =
            new Dictionary<string, PlaceDTO>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < before.Length; i++)
        {
            if (!string.IsNullOrEmpty(before[i].symbol))
                oldBySymbol[before[i].symbol] = before[i];
        }

        for (int i = 0; i < after.Length; i++)
        {
            PlaceDTO old;
            PlaceDTO now = after[i];
            if (!oldBySymbol.TryGetValue(now.symbol ?? string.Empty, out old))
                return $"place-added:{now.symbol}";

            if (old.scope != now.scope) return $"place:{now.symbol}.scope {old.scope}->{now.scope}";
            if (!string.Equals(old.name, now.name, StringComparison.Ordinal)) return $"place:{now.symbol}.name";
            if (old.p1 != now.p1 || old.p2 != now.p2 || old.p3 != now.p3) return $"place:{now.symbol}.params";
            if (old.siteType != now.siteType) return $"place:{now.symbol}.siteType {old.siteType}->{now.siteType}";
            if (old.mapId != now.mapId) return $"place:{now.symbol}.mapId {old.mapId}->{now.mapId}";
            if (old.locationId != now.locationId) return $"place:{now.symbol}.locationId {old.locationId}->{now.locationId}";
            if (old.regionIndex != now.regionIndex) return $"place:{now.symbol}.region {old.regionIndex}->{now.regionIndex}";
            if (!string.Equals(old.regionName, now.regionName, StringComparison.Ordinal)) return $"place:{now.symbol}.regionName";
            if (!string.Equals(old.locationName, now.locationName, StringComparison.Ordinal)) return $"place:{now.symbol}.locationName";
            if (old.buildingKey != now.buildingKey) return $"place:{now.symbol}.buildingKey {old.buildingKey}->{now.buildingKey}";
            if (!string.Equals(old.buildingName, now.buildingName, StringComparison.Ordinal)) return $"place:{now.symbol}.buildingName";
            if (old.magicNumberIndex != now.magicNumberIndex) return $"place:{now.symbol}.magic {old.magicNumberIndex}->{now.magicNumberIndex}";
            if (!string.Equals(
                    old.markerTargetsFingerprint ?? string.Empty,
                    now.markerTargetsFingerprint ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return $"place:{now.symbol}.markerTargets " +
                    $"{(old.markerTargetsFingerprint ?? string.Empty).Length}->" +
                    $"{(now.markerTargetsFingerprint ?? string.Empty).Length}";
            }
        }

        if (before.Length != after.Length)
            return $"place-count:{before.Length}->{after.Length}";

        return "place-order-or-duplicate";
    }

    private static string DescribePersonDifference(
        PersonDTO[] before,
        PersonDTO[] after)
    {
        if (before == null || after == null)
            return $"persons:null before={(before == null)} after={(after == null)}";

        Dictionary<string, PersonDTO> oldBySymbol =
            new Dictionary<string, PersonDTO>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < before.Length; i++)
        {
            if (!string.IsNullOrEmpty(before[i].symbol))
                oldBySymbol[before[i].symbol] = before[i];
        }

        for (int i = 0; i < after.Length; i++)
        {
            PersonDTO old;
            PersonDTO now = after[i];
            if (!oldBySymbol.TryGetValue(now.symbol ?? string.Empty, out old))
                return $"person-added:{now.symbol}";

            if (old.race != now.race) return $"person:{now.symbol}.race {old.race}->{now.race}";
            if (old.gender != now.gender) return $"person:{now.symbol}.gender {old.gender}->{now.gender}";
            if (old.faceIndex != now.faceIndex) return $"person:{now.symbol}.face {old.faceIndex}->{now.faceIndex}";
            if (old.nameSeed != now.nameSeed) return $"person:{now.symbol}.nameSeed {old.nameSeed}->{now.nameSeed}";
            if (old.isQuestor != now.isQuestor) return $"person:{now.symbol}.questor {old.isQuestor}->{now.isQuestor}";
            if (old.isIndividualNPC != now.isIndividualNPC) return $"person:{now.symbol}.individual {old.isIndividualNPC}->{now.isIndividualNPC}";
            if (old.isIndividualAtHome != now.isIndividualAtHome) return $"person:{now.symbol}.atHome {old.isIndividualAtHome}->{now.isIndividualAtHome}";
            if (!string.Equals(old.displayName, now.displayName, StringComparison.Ordinal)) return $"person:{now.symbol}.displayName";
            if (!string.Equals(old.homePlaceSymbol, now.homePlaceSymbol, StringComparison.Ordinal)) return $"person:{now.symbol}.homePlace";
            if (!string.Equals(old.lastAssignedPlaceSymbol, now.lastAssignedPlaceSymbol, StringComparison.Ordinal)) return $"person:{now.symbol}.lastPlace";
            if (old.assignedToHome != now.assignedToHome) return $"person:{now.symbol}.assignedHome {old.assignedToHome}->{now.assignedToHome}";
            if (old.factionID != now.factionID) return $"person:{now.symbol}.faction {old.factionID}->{now.factionID}";
            if (!string.Equals(old.factionTableKey, now.factionTableKey, StringComparison.Ordinal)) return $"person:{now.symbol}.factionKey";
            if (old.discoveredThroughTalkManager != now.discoveredThroughTalkManager) return $"person:{now.symbol}.discovered {old.discoveredThroughTalkManager}->{now.discoveredThroughTalkManager}";
            if (old.isMuted != now.isMuted) return $"person:{now.symbol}.muted {old.isMuted}->{now.isMuted}";
            if (old.isDestroyed != now.isDestroyed) return $"person:{now.symbol}.destroyed {old.isDestroyed}->{now.isDestroyed}";
            if (old.isHidden != now.isHidden) return $"person:{now.symbol}.hidden {old.isHidden}->{now.isHidden}";
            if (old.hasPlayerClicked != now.hasPlayerClicked) return $"person:{now.symbol}.clicked {old.hasPlayerClicked}->{now.hasPlayerClicked}";
        }

        if (before.Length != after.Length)
            return $"person-count:{before.Length}->{after.Length}";

        return "person-order-or-duplicate";
    }

    private static string DescribeFoeDifference(
        FoeDTO[] before,
        FoeDTO[] after)
    {
        if (before == null || after == null)
            return $"foes:null before={(before == null)} after={(after == null)}";

        Dictionary<string, FoeDTO> oldBySymbol =
            new Dictionary<string, FoeDTO>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < before.Length; i++)
        {
            if (!string.IsNullOrEmpty(before[i].symbol))
                oldBySymbol[before[i].symbol] = before[i];
        }

        for (int i = 0; i < after.Length; i++)
        {
            FoeDTO old;
            FoeDTO now = after[i];
            if (!oldBySymbol.TryGetValue(now.symbol ?? string.Empty, out old))
                return $"foe-added:{now.symbol}";

            if (old.foeId != now.foeId) return $"foe:{now.symbol}.id {old.foeId}->{now.foeId}";
            if (old.spawnCount != now.spawnCount) return $"foe:{now.symbol}.spawn {old.spawnCount}->{now.spawnCount}";
            if (old.humanoidGender != now.humanoidGender) return $"foe:{now.symbol}.gender {old.humanoidGender}->{now.humanoidGender}";
            if (old.injuredTrigger != now.injuredTrigger) return $"foe:{now.symbol}.injured {old.injuredTrigger}->{now.injuredTrigger}";
            if (old.restrained != now.restrained) return $"foe:{now.symbol}.restrained {old.restrained}->{now.restrained}";
            if (old.killCount != now.killCount) return $"foe:{now.symbol}.kills {old.killCount}->{now.killCount}";
            if (!string.Equals(old.displayName, now.displayName, StringComparison.Ordinal)) return $"foe:{now.symbol}.displayName";
            if (!string.Equals(old.typeName, now.typeName, StringComparison.Ordinal)) return $"foe:{now.symbol}.typeName";
        }

        if (before.Length != after.Length)
            return $"foe-count:{before.Length}->{after.Length}";

        return "foe-order-or-duplicate";
    }

    private static void TraceServerQuestTraffic(
        Quest q,
        string instanceId,
        string path,
        Quest.TaskState[] nowTasks,
        Quest.LogEntry[] nowLogs,
        Dictionary<string, ItemState> nowItems,
        PlaceDTO[] nowPlaces,
        PersonDTO[] nowPersons,
        FoeDTO[] nowFoes,
        bool tasksChanged,
        bool logsChanged,
        bool itemsChanged,
        bool placesChanged,
        bool personsChanged,
        bool foesChanged)
    {
        if (q == null)
            return;

        string key = "server|" + q.UID.ToString() + "|" + (path ?? string.Empty);
        if (!ShouldWriteQuestTrafficTrace(key))
            return;

        List<string> categories = new List<string>();
        List<string> details = new List<string>();

        if (tasksChanged)
        {
            categories.Add("tasks");
            Quest.TaskState[] old;
            _srvLastTasks.TryGetValue(instanceId, out old);
            details.Add(DescribeTaskDifference(q, old, nowTasks));
        }
        if (logsChanged)
        {
            categories.Add("logs");
            HashSet<int> old;
            _srvLastLogs.TryGetValue(instanceId, out old);
            details.Add(DescribeLogDifference(old, nowLogs));
        }
        if (itemsChanged)
        {
            categories.Add("items");
            Dictionary<string, ItemState> old;
            _srvLastItems.TryGetValue(instanceId, out old);
            details.Add(DescribeItemDifference(old, nowItems));
        }
        if (placesChanged)
        {
            categories.Add("places");
            PlaceDTO[] old;
            _srvLastPlaces.TryGetValue(instanceId, out old);
            details.Add(DescribePlaceDifference(old, nowPlaces));
        }
        if (personsChanged)
        {
            categories.Add("persons");
            PersonDTO[] old;
            _srvLastPersons.TryGetValue(instanceId, out old);
            details.Add(DescribePersonDifference(old, nowPersons));
        }
        if (foesChanged)
        {
            categories.Add("foes");
            FoeDTO[] old;
            _srvLastFoes.TryGetValue(instanceId, out old);
            details.Add(DescribeFoeDifference(old, nowFoes));
        }

        UpdatePacket probe = new UpdatePacket
        {
            instanceId = instanceId,
            sourceNetId = _localNetId,
            tasks = ToTaskDTOs(q, nowTasks),
            logs = nowLogs != null
                ? nowLogs.Select(x => new LogEntryDTO
                    {
                        stepID = x.stepID,
                        messageID = x.messageID,
                    }).ToArray()
                : new LogEntryDTO[0],
            items = ToItemDTOs(nowItems),
            places = nowPlaces ?? new PlaceDTO[0],
            persons = nowPersons ?? new PersonDTO[0],
            foes = nowFoes ?? new FoeDTO[0],
            questSuccess = q.QuestSuccess,
        };

        int jsonBytes = 0;
        try { jsonBytes = Encoding.UTF8.GetByteCount(ToJson(probe)); } catch { }

        Debug.LogWarning(
            $"[QuestNetSync][TrafficTrace][Server:{path}] " +
            $"uid={q.UID} quest='{q.QuestName}' categories=" +
            $"{string.Join(",", categories.ToArray())} detail=" +
            $"{string.Join(" | ", details.ToArray())} " +
            $"fullUpdateJsonBytes={jsonBytes}");
    }

    private static void TraceClientQuestTraffic(
        Quest q,
        string instanceId,
        TaskStateDTO[] taskChanges,
        LogDeltaDTO[] logChanges,
        FoeProgressDeltaDTO[] foeChanges)
    {
        if (q == null)
            return;

        string key = "client|" + q.UID.ToString();
        if (!ShouldWriteQuestTrafficTrace(key))
            return;

        List<string> details = new List<string>();

        if (taskChanges != null)
        {
            for (int i = 0; i < taskChanges.Length; i++)
                details.Add($"task:{taskChanges[i].symbol}={taskChanges[i].set}");
        }

        if (logChanges != null)
        {
            for (int i = 0; i < logChanges.Length; i++)
                details.Add($"log:{logChanges[i].stepID} present={logChanges[i].present}");
        }

        if (foeChanges != null)
        {
            for (int i = 0; i < foeChanges.Length; i++)
            {
                FoeProgressDeltaDTO f = foeChanges[i];
                details.Add(
                    $"foe:{f.symbol} kills={f.killCountCandidate} " +
                    $"injuredChanged={f.injuredChanged}:{f.injuredTrigger} " +
                    $"restrainedChanged={f.restrainedChanged}:{f.restrained}");
            }
        }

        Debug.LogWarning(
            $"[QuestNetSync][TrafficTrace][ClientDelta] uid={q.UID} " +
            $"quest='{q.QuestName}' detail={string.Join(" | ", details.ToArray())}");
    }

    private static bool SameTasks(
        Quest q,
        Quest.TaskState[] a,
        Quest.TaskState[] b)
    {
        if (a == null || b == null)
            return false;

        Dictionary<string, bool> left =
            BuildRoutineTaskStateMap(q, a);
        Dictionary<string, bool> right =
            BuildRoutineTaskStateMap(q, b);

        if (left.Count != right.Count)
            return false;

        foreach (KeyValuePair<string, bool> entry in left)
        {
            bool otherValue;
            if (!right.TryGetValue(entry.Key, out otherValue) ||
                otherValue != entry.Value)
                return false;
        }

        return true;
    }


    private static bool TaskHasStartSideEffects(Quest q, string taskSymbol)
    {
        if (q == null || string.IsNullOrEmpty(taskSymbol))
            return false;

        try
        {
            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                return false;

            // A pure variable task has no runtime actions and is safe to mirror as state.
            // Any action task can show UI, give/take items, spawn foes, end quest, or change reputation.
            foreach (IQuestAction action in task.Actions)
            {
                if (action != null)
                    return true;
            }
            return false;
        }
        catch
        {
            // Unknown task shape: be conservative during remote quest-end application.
            return true;
        }
    }

    private static bool IsPermanentGetItemRewardTask(
        DaggerfallWorkshop.Game.Questing.Task task)
    {
        if (task == null || task.Actions == null)
            return false;

        bool hasGetItem = false;
        bool hasMakePermanent = false;

        foreach (IQuestAction action in task.Actions)
        {
            if (action == null)
                continue;

            string actionType = action.GetType().Name;
            if (string.Equals(
                    actionType,
                    "GetItem",
                    StringComparison.Ordinal))
            {
                hasGetItem = true;
            }
            else if (string.Equals(
                         actionType,
                         "MakePermanent",
                         StringComparison.Ordinal))
            {
                hasMakePermanent = true;
            }
        }

        return hasGetItem && hasMakePermanent;
    }

    private static bool HasPendingPermanentGetItemRewardActions(
        DaggerfallWorkshop.Game.Questing.Task task)
    {
        if (!IsPermanentGetItemRewardTask(task))
            return false;

        foreach (IQuestAction action in task.Actions)
        {
            if (action == null || action.IsTriggerCondition)
                continue;

            // Wait for the whole local reward chain, not just GetItem itself. This
            // includes a Say between GetItem and MakePermanent, as used by K0C00Y02.
            if (!action.IsComplete)
                return true;
        }

        return false;
    }

    private static bool HasPendingTriggeredPermanentGetItemRewards(Quest q)
    {
        if (q == null)
            return false;

        try
        {
            Quest.TaskState[] states =
                q.GetTaskStates() ?? new Quest.TaskState[0];

            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].symbol == null || !states[i].set)
                    continue;

                DaggerfallWorkshop.Game.Questing.Task task =
                    q.GetTask(states[i].symbol);
                if (HasPendingPermanentGetItemRewardActions(task))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static int PrimePermanentGetItemRewardsFromRemoteEnd(
        Quest q,
        TaskStateDTO[] remoteTasks,
        string reason)
    {
        if (q == null || remoteTasks == null)
            return 0;

        Dictionary<string, bool> localState =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Quest.TaskState[] states =
            q.GetTaskStates() ?? new Quest.TaskState[0];
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol != null)
                localState[states[i].symbol.Name] = states[i].set;
        }

        int primed = 0;
        for (int i = 0; i < remoteTasks.Length; i++)
        {
            string taskSymbol = remoteTasks[i].symbol;
            if (!remoteTasks[i].set ||
                string.IsNullOrEmpty(taskSymbol))
                continue;

            bool alreadyTriggered;
            if (localState.TryGetValue(
                    taskSymbol,
                    out alreadyTriggered) &&
                alreadyTriggered)
                continue;

            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (!HasPendingPermanentGetItemRewardActions(task))
                continue;

            // Remote-end state is authoritative, but only this narrow reward shape is
            // safe to start here. Other action tasks remain under the existing remote
            // end protections against duplicate reputation, EndQuest, and StartQuest.
            q.StartTask(new Symbol(taskSymbol));
            localState[taskSymbol] = true;
            primed++;

            Debug.Log(
                $"[QuestNetSync][GetItemEndBarrier] Primed local permanent reward " +
                $"uid={q.UID} quest='{q.QuestName}' task='{taskSymbol}' reason='{reason}'");
        }

        if (primed > 0)
            ReassertClientQuestChainAuthorityAfterTaskState(q, reason);

        return primed;
    }

    private static void ApplyDesiredStateForRemoteEnd(Quest q, TaskStateDTO[] desiredTasks, LogEntryDTO[] desiredLogs)
    {
        // During EndPacket/CmdClientEnded we must not StartTask() completed action tasks
        // from the remote machine's DTOs. Final tasks often contain GivePc + EndQuest,
        // and some quests/actions contain ChangeReputeWith. Starting those tasks here
        // can apply reputation once, then CoFinishRemoteEndedQuest()/EndQuest applies
        // quest completion reputation again.
        Dictionary<string, bool> want = new Dictionary<string, bool>();
        if (desiredTasks != null)
        {
            for (int i = 0; i < desiredTasks.Length; i++)
            {
                string symbol = desiredTasks[i].symbol;
                if (IsRoutineTaskSyncExcluded(q, symbol))
                    continue;
                want[symbol] = desiredTasks[i].set;
            }
        }

        Quest.TaskState[] curr = q.GetTaskStates();
        for (int i = 0; i < curr.Length; i++)
        {
            var s = curr[i];
            string taskName = s.symbol.Name;
            if (IsRoutineTaskSyncExcluded(q, taskName))
                continue;

            bool w;
            bool target = want.TryGetValue(taskName, out w) ? w : false;
            if (s.set == target)
                continue;

            if (TaskHasStartSideEffects(q, taskName))
            {
                if (Debug.isDebugBuild)
                    Debug.Log($"[QuestNetSync] RemoteEnd skipped action-task state apply uid={q.UID} task='{taskName}' target={target}");
                continue;
            }

            Symbol sym = new Symbol(taskName);
            if (target) q.StartTask(sym); else q.ClearTask(sym);
        }

        // Journal/log state is safe to mirror directly.
        if (desiredLogs != null)
        {
            HashSet<int> have = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
            HashSet<int> wantSteps = new HashSet<int>(desiredLogs.Select(l => l.stepID));

            foreach (int step in have) if (!wantSteps.Contains(step)) q.RemoveLogStep(step);
            for (int i = 0; i < desiredLogs.Length; i++) q.AddLogStep(desiredLogs[i].stepID, desiredLogs[i].messageID);
        }

        ReassertClientQuestChainAuthorityAfterTaskState(q, "remote-end-task-state");
    }

    private static bool ShouldProtectSharedClickSpawnTaskFromSnapshotClear(
        Quest q,
        string taskSymbol)
    {
        if (q == null || string.IsNullOrEmpty(taskSymbol))
            return false;

        SharedPersonClickInteraction context;
        if (!_sharedPersonClickInteractions.TryGetValue(q.UID, out context) ||
            context == null ||
            string.IsNullOrEmpty(context.triggerTaskSymbol) ||
            !string.Equals(
                context.triggerTaskSymbol,
                taskSymbol,
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Only protect tasks whose reinitialization can physically duplicate an
        // encounter. Later legitimate clears are allowed once this exact shared click
        // interaction has retired.
        return TaskHasActionType(q, taskSymbol, "CreateFoe") ||
               TaskHasActionType(q, taskSymbol, "PlaceFoe");
    }

    private static void ApplyDesiredState(Quest q, TaskStateDTO[] desiredTasks, LogEntryDTO[] desiredLogs)
    {
        // Tasks
        Dictionary<string, bool> want = new Dictionary<string, bool>();
        if (desiredTasks != null)
            for (int i = 0; i < desiredTasks.Length; i++)
            {
                string symbol = desiredTasks[i].symbol;
                if (IsRoutineTaskSyncExcluded(q, symbol))
                    continue;
                want[symbol] = desiredTasks[i].set;
            }

        Quest.TaskState[] curr = q.GetTaskStates();
        for (int i = 0; i < curr.Length; i++)
        {
            var s = curr[i];
            if (IsRoutineTaskSyncExcluded(q, s.symbol.Name) ||
                TaskHasActionType(q, s.symbol.Name, "EndQuest"))
                continue;
            bool w, target = want.TryGetValue(s.symbol.Name, out w) ? w : false;

            if (s.set && !target &&
                ShouldProtectSharedClickSpawnTaskFromSnapshotClear(q, s.symbol.Name))
            {
                Debug.Log(
                    $"[QuestNetSync][SharedClickSpawnGuard] Ignored stale snapshot clear " +
                    $"uid={q.UID} task='{s.symbol.Name}' while exact shared NPC click is active.");
                continue;
            }

            if (target &&
                IsPrematureLordKavarRulerClickTask(q, s.symbol.Name))
            {
                if (s.set)
                    q.ClearTask(s.symbol);

                Debug.Log(
                    $"[QuestNetSync][KavarStateRepair] Rejected premature RpcUpdate " +
                    $"_S.30_ uid={q.UID} before K'avar outcome.");
                continue;
            }

            if (s.set == target) continue;

            Symbol sym = new Symbol(s.symbol.Name);
            if (target) q.StartTask(sym); else q.ClearTask(sym);
        }

        // Journal
        if (desiredLogs != null)
        {
            HashSet<int> have = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
            HashSet<int> wantSteps = new HashSet<int>(desiredLogs.Select(l => l.stepID));

            foreach (int step in have) if (!wantSteps.Contains(step)) q.RemoveLogStep(step);
            for (int i = 0; i < desiredLogs.Length; i++) q.AddLogStep(desiredLogs[i].stepID, desiredLogs[i].messageID);
        }

        ReassertClientQuestChainAuthorityAfterTaskState(q, "network-task-state");
    }

    private ItemDTO[] BuildItems(Quest q)
    {
        QuestResource[] res = q.GetAllResources(typeof(Item));
        if (res == null || res.Length == 0) return new ItemDTO[0];

        List<ItemDTO> list = new List<ItemDTO>(res.Length);
        for (int i = 0; i < res.Length; i++)
        {
            Item item = res[i] as Item;
            if (item == null || item.Symbol == null) continue;

            DaggerfallUnityItem dfItem = item.DaggerfallUnityItem;

            QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
            bool behaviourHidden = item.QuestResourceBehaviour != null && !item.QuestResourceBehaviour.gameObject.activeInHierarchy;

            bool physicalPickup = HasPhysicalPickupActionForItem(q, item.Symbol.Name) ||
                                  HasTotingTaskForItem(q, item.Symbol.Name) ||
                                  HasFoeLootAssignmentForItem(q, item.Symbol.Name) ||
                                  IsQuestItemClickPickupAllowed(q.UID, item.Symbol.Name);
            bool inferredInventory = physicalPickup && IsQuestItemInPlayerInventory(q, item.Symbol.Name);
            if (!inferredInventory && physicalPickup && rsd.hasPlayerClicked && !HasTriggeredTotingTaskForItem(q, item.Symbol.Name))
                inferredInventory = true;

            list.Add(new ItemDTO
            {
                symbol = item.Symbol.Name,
                // After a placed quest item has been picked up locally, some DFU paths no
                // longer leave a usable DaggerfallUnityItem on the quest resource itself.
                // Do not drop the DTO in that case or the pickup will never be sent to the
                // other machine. The receiver still has its own local quest item prototype.
                stackCount = dfItem != null ? dfItem.stackCount : 1,
                hasPlayerClicked = rsd.hasPlayerClicked,
                isHidden = rsd.isHidden || behaviourHidden,
                inPlayerInventory = inferredInventory,
                madePermanent = item.MadePermanent,
            });
        }

        list.Sort((a, b) => string.CompareOrdinal(a.symbol, b.symbol));

        return list.ToArray();
    }

    private Dictionary<string, ItemState> CaptureItemStates(Quest q)
    {
        Dictionary<string, ItemState> map = new Dictionary<string, ItemState>();
        QuestResource[] res = q.GetAllResources(typeof(Item));
        if (res == null || res.Length == 0) return map;

        for (int i = 0; i < res.Length; i++)
        {
            Item item = res[i] as Item;
            if (item == null || item.Symbol == null) continue;

            DaggerfallUnityItem dfItem = item.DaggerfallUnityItem;

            QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
            bool behaviourHidden = item.QuestResourceBehaviour != null && !item.QuestResourceBehaviour.gameObject.activeInHierarchy;

            bool physicalPickup = HasPhysicalPickupActionForItem(q, item.Symbol.Name) ||
                                  HasTotingTaskForItem(q, item.Symbol.Name) ||
                                  HasFoeLootAssignmentForItem(q, item.Symbol.Name) ||
                                  IsQuestItemClickPickupAllowed(q.UID, item.Symbol.Name);
            bool inferredInventory = physicalPickup && IsQuestItemInPlayerInventory(q, item.Symbol.Name);
            if (!inferredInventory && physicalPickup && rsd.hasPlayerClicked && !HasTriggeredTotingTaskForItem(q, item.Symbol.Name))
                inferredInventory = true;

            ItemState state = new ItemState
            {
                symbol = item.Symbol.Name,
                // Keep reporting this resource even if its local DaggerfallUnityItem has
                // been moved into inventory and the prototype reference is temporarily null.
                stackCount = dfItem != null ? dfItem.stackCount : 1,
                hasPlayerClicked = rsd.hasPlayerClicked,
                isHidden = rsd.isHidden || behaviourHidden,
                inPlayerInventory = inferredInventory,
                madePermanent = item.MadePermanent,
            };
            map[state.symbol] = state;
        }

        return map;
    }

    private static ItemDTO[] ToItemDTOs(Dictionary<string, ItemState> items)
    {
        if (items == null || items.Count == 0) return new ItemDTO[0];

        ItemDTO[] arr = new ItemDTO[items.Count];
        int index = 0;
        foreach (var kvp in items.OrderBy(k => k.Key))
        {
            arr[index++] = new ItemDTO
            {
                symbol = kvp.Value.symbol,
                stackCount = kvp.Value.stackCount,
                hasPlayerClicked = kvp.Value.hasPlayerClicked,
                isHidden = kvp.Value.isHidden,
                inPlayerInventory = kvp.Value.inPlayerInventory,
                madePermanent = kvp.Value.madePermanent,
            };
        }

        return arr;
    }

    private static bool SameItems(Dictionary<string, ItemState> a, Dictionary<string, ItemState> b)
    {
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        foreach (var kv in a)
        {
            ItemState other;
            if (!b.TryGetValue(kv.Key, out other)) return false;
            if (kv.Value.stackCount != other.stackCount) return false;
            if (kv.Value.hasPlayerClicked != other.hasPlayerClicked) return false;
            if (kv.Value.isHidden != other.isHidden) return false;
            if (kv.Value.inPlayerInventory != other.inPlayerInventory) return false;
            if (kv.Value.madePermanent != other.madePermanent) return false;
        }

        return true;
    }

    private static void ApplyItems(Quest q, ItemDTO[] items)
    {
        if (items == null) return;

        for (int i = 0; i < items.Length; i++)
        {
            ItemDTO dto = items[i];
            if (string.IsNullOrEmpty(dto.symbol)) continue;

            Item questItem = q.GetItem(new Symbol(dto.symbol));
            if (questItem == null) continue;

            DaggerfallUnityItem dfItem = questItem.DaggerfallUnityItem;
            if (dfItem == null) continue;

            dfItem.stackCount = NormalizeQuestItemStackCountForDto(dfItem, dto.stackCount);

            bool hasPhysicalPickupAction = HasPhysicalPickupActionForItem(q, dto.symbol);
            bool hasPickupAction = hasPhysicalPickupAction ||
                                   IsQuestItemClickPickupAllowed(q.UID, dto.symbol) ||
                                   HasTotingTaskForItem(q, dto.symbol) ||
                                   HasFoeLootAssignmentForItem(q, dto.symbol);

            // Some placed quest items do not reliably expose the picked-up item as a
            // linked inventory item on the source machine, so dto.inPlayerInventory can
            // remain false even after the world item is gone. Only infer pickup from an
            // actual click, never from hidden alone.
            bool shouldHaveItem = hasPickupAction && (dto.inPlayerInventory || ShouldInferPickedUpQuestItemInInventory(q, dto));

            // A completed toting turn-in is authoritative for this local quest copy.
            // Another participant can still report the item as carried in a later
            // passive snapshot, but that state must not resurrect the consumed item.
            if (shouldHaveItem &&
                IsTotingQuestItemConsumed(
                    q.UID,
                    dto.symbol))
            {
                shouldHaveItem = false;
            }

            // Host/client can receive one stale pre-click item packet after the physical
            // click or after the remote pickup event. Do not let that packet remove a
            // just-granted placed quest item before the authoritative clicked state arrives.
            string pickupProtectKey = MakeQuestItemInventoryKey(q.UID, dto.symbol);
            bool localAlreadyPicked = false;
            try { localAlreadyPicked = hasPickupAction && (questItem.HasPlayerClicked || IsQuestItemInPlayerInventory(q, dto.symbol)); } catch { localAlreadyPicked = false; }

            if (!shouldHaveItem && IsPickedQuestItemKeyProtected(pickupProtectKey) && localAlreadyPicked &&
                !HasTriggeredTotingTaskForItem(q, dto.symbol))
                shouldHaveItem = true;

            // Durable world-consumption proof for ClickedItem resources.
            //
            // Some quests intentionally do:
            //   clicked item X
            //   ...
            //   take X from pc
            //
            // After TakeItem, the player no longer carries X and click flags can be
            // rearmed/normalized, but the triggered ClickedItem task still proves the
            // physical world object was consumed. Without this, passive ItemDTO apply
            // rewrites isHidden=false and GameObjectHelper resurrects the billboard.
            bool clickedItemTaskTriggered =
                hasPhysicalPickupAction &&
                HasTriggeredClickedItemTaskForItem(q, dto.symbol);

            // A passive inventory snapshot can precede the explicit ClickedItem event.
            // Owning the item is enough to hide/add it, but not enough to fire the
            // quest trigger and its popup. Keep HasPlayerClicked false until either
            // the DTO carries a real clicked flag or ApplyRemoteItemClick has run.
            bool deferPhysicalClickFlag = hasPhysicalPickupAction && shouldHaveItem &&
                !dto.hasPlayerClicked &&
                !_remoteItemClicksApplied.Contains(MakeItemClickApplyKey(q.UID, dto.symbol));

            if (shouldHaveItem)
                ProtectPickedQuestItemKey(pickupProtectKey);
            else if (!clickedItemTaskTriggered)
            {
                RemovePickedQuestItemKey(pickupProtectKey);

                // Only clear stale click guards when local quest progress does NOT say
                // this item was genuinely clicked. A triggered ClickedItem task means
                // "picked and possibly consumed later", not "old save before pickup".
                _remoteItemClicksApplied.Remove(MakeItemClickApplyKey(q.UID, dto.symbol));
                string msgPrefix = q.UID.ToString() + "|item-click|" + (dto.symbol ?? string.Empty) + "|";
                _remoteItemClickMessagesShown.RemoveWhere(k => k.StartsWith(msgPrefix));
            }

            // For placed pickup items, hidden-before-click can simply mean the dungeon
            // object is not instantiated/active yet, so dto.isHidden alone is not enough
            // before any real click. Once the owning ClickedItem task has triggered,
            // however, that quest progress is durable proof that the world object must
            // remain hidden even if the item was subsequently removed from inventory.
            bool stateHidden = hasPickupAction
                ? (dto.hasPlayerClicked || shouldHaveItem || clickedItemTaskTriggered)
                : (dto.isHidden || shouldHaveItem);

            if (clickedItemTaskTriggered &&
                !dto.hasPlayerClicked &&
                !shouldHaveItem &&
                Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][WorldItemConsumed] Preserved hidden world item from triggered ClickedItem task " +
                    $"uid={q.UID} item='{dto.symbol}'");
            }

            QuestResource.ResourceSaveData_v1 rsd = questItem.GetResourceSaveData();
            rsd.hasPlayerClicked = dto.hasPlayerClicked || (shouldHaveItem && !deferPhysicalClickFlag);
            rsd.isHidden = stateHidden;
            questItem.RestoreResourceSaveData(rsd);

            // Do not call questItem.SetPlayerClicked() from passive state sync. That fires
            // ClickedItem locally and was replaying the pickup popup on load. Real clicks
            // are handled by ReportLocalItemClicked/ApplyRemoteItemClick.

            // If one player picked up a placed quest item, the other player must not
            // continue seeing the world sprite/loot object.
            if (stateHidden && questItem.QuestResourceBehaviour != null)
                questItem.QuestResourceBehaviour.gameObject.SetActive(false);

            if (hasPickupAction)
            {
                // IMPORTANT: quest item inventory is per-player, not global quest state.
                // Passive ItemDTO state is sent with every quest delta, including from a
                // player who simply does not have this quest item in their own save.
                // If we apply shouldHaveItem=false here, Player2 entering a quest site
                // can wipe Player1's already-held delivery/book item. Only positive
                // pickup state is repaired from passive item sync. Real removals must
                // come from explicit side-effect paths like DroppedItemAtPlace,
                // TotingItemAndClickedNpc/turn-in, or quest-end cleanup.
                if (shouldHaveItem)
                {
                    // Ensure the physical inventory copy exists before applying
                    // MakePermanent. This is essential for world-pickup rewards: the
                    // remote player must keep the actual artifact, not just mark the
                    // quest resource permanent after EndQuest has already cleaned it up.
                    SetQuestItemInventory(q, dto.symbol, true);
                    if (deferPhysicalClickFlag)
                        RestoreInventoryOnlyQuestItemState(q, dto.symbol);
                }
            }

            // Permanence is monotonic durable state. Apply it after any required
            // physical inventory reconstruction so the carried copy itself has its
            // quest link removed before end-of-quest cleanup runs.
            if (dto.madePermanent)
                ApplyQuestItemMadePermanent(q, dto.symbol, "item-dto");
        }
    }


// ─────────────────────────────────────────────────────────────────────────────
// Extra resource sync: Items (full identity), Foes, Clocks
// ─────────────────────────────────────────────────────────────────────────────

private static readonly fsSerializer _fs = new fsSerializer();

private static string ToJson<T>(T obj)
{
    try
    {
        fsData data;
        var r = _fs.TrySerialize(typeof(T), obj, out data);
        if (r.Failed) throw new Exception(r.ToString());
        return fsJsonPrinter.CompressedJson(data);
    }
    catch (Exception e)
    {
        Debug.LogWarning($"[QuestNetSync] Failed to serialize {typeof(T).Name}: {e}");
        return string.Empty;
    }
}

private static bool FromJson<T>(string json, out T obj)
{
    obj = default(T);
    if (string.IsNullOrEmpty(json)) return false;

    try
    {
        fsData data = fsJsonParser.Parse(json);
        object boxed = null;
        var r = _fs.TryDeserialize(data, typeof(T), ref boxed);
        if (r.Failed) throw new Exception(r.ToString());
        obj = (T)boxed;
        return true;
    }
    catch (Exception e)
    {
        Debug.LogWarning($"[QuestNetSync] Failed to deserialize {typeof(T).Name}: {e}");
        return false;
    }
}

private static string BuildQuestSaveDataJson(Quest q)
{
    if (q == null)
        return string.Empty;

    try
    {
        return ToJson(q.GetSaveData());
    }
    catch (Exception e)
    {
        Debug.LogWarning(
            $"[QuestNetSync][StartPacket] Failed to capture full quest state " +
            $"uid={q.UID} quest='{q.QuestName}': {e.Message}");
        return string.Empty;
    }
}

private static string BuildTaskSaveDataJson(Quest q)
{
    if (q == null)
        return string.Empty;

    try
    {
        Quest.QuestSaveData_v1 save = q.GetSaveData();
        DaggerfallWorkshop.Game.Questing.Task.TaskSaveData_v1[] tasks =
            save.tasks ?? new DaggerfallWorkshop.Game.Questing.Task.TaskSaveData_v1[0];
        return ToJson(tasks);
    }
    catch (Exception e)
    {
        Debug.LogWarning($"[QuestNetSync][MissingOnlyShare] Failed to capture full task state for uid={q.UID}: {e.Message}");
        return string.Empty;
    }
}

private static bool TryRestoreTaskSaveData(Quest q, string json)
{
    if (q == null || string.IsNullOrEmpty(json))
        return false;

    DaggerfallWorkshop.Game.Questing.Task.TaskSaveData_v1[] savedTasks;
    if (!FromJson(json, out savedTasks) || savedTasks == null || savedTasks.Length == 0)
        return false;

    int restored = 0;
    for (int i = 0; i < savedTasks.Length; i++)
    {
        DaggerfallWorkshop.Game.Questing.Task.TaskSaveData_v1 saved = savedTasks[i];
        if (saved.symbol == null || string.IsNullOrEmpty(saved.symbol.Name))
            continue;

        DaggerfallWorkshop.Game.Questing.Task localTask = q.GetTask(saved.symbol);
        if (localTask == null)
            continue;

        try
        {
            // Restores trigger history and each action's completion state without
            // replaying actions that already ran on the source quest.
            localTask.RestoreSaveData(saved);
            restored++;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuestNetSync][MissingOnlyShare] Failed to restore task '{saved.symbol.Name}' uid={q.UID}: {e.Message}");
        }
    }

    return restored > 0;
}

private static void ApplyDesiredLogsOnly(Quest q, LogEntryDTO[] desiredLogs)
{
    if (q == null || desiredLogs == null)
        return;

    HashSet<int> have = new HashSet<int>((q.GetLogMessages() ?? new Quest.LogEntry[0]).Select(l => l.stepID));
    HashSet<int> wantSteps = new HashSet<int>(desiredLogs.Select(l => l.stepID));

    foreach (int step in have)
        if (!wantSteps.Contains(step))
            q.RemoveLogStep(step);

    for (int i = 0; i < desiredLogs.Length; i++)
        q.AddLogStep(desiredLogs[i].stepID, desiredLogs[i].messageID);
}

private static T FindResourceBySymbol<T>(Quest q, string symbol) where T : QuestResource
{
    if (q == null || string.IsNullOrEmpty(symbol)) return null;
    QuestResource[] res = q.GetAllResources(typeof(T));
    if (res == null) return null;
    for (int i = 0; i < res.Length; i++)
    {
        T t = res[i] as T;
        if (t != null && t.Symbol != null && t.Symbol.Name == symbol)
            return t;
    }
    return null;
}

private ItemStartDTO[] BuildFullItems(Quest q)
{
    QuestResource[] res = q.GetAllResources(typeof(Item));
    if (res == null || res.Length == 0) return new ItemStartDTO[0];

    List<ItemStartDTO> list = new List<ItemStartDTO>(res.Length);
    for (int i = 0; i < res.Length; i++)
    {
        Item qi = res[i] as Item;
        if (qi == null || qi.Symbol == null) continue;

        DaggerfallUnityItem dfItem = qi.DaggerfallUnityItem;
        if (dfItem == null) continue;

        ItemData_v1 data = dfItem.GetSaveData();
        list.Add(new ItemStartDTO
        {
            symbol = qi.Symbol.Name,
            stackCount = NormalizeQuestItemStackCountForDto(dfItem, dfItem.stackCount),
            itemDataJson = ToJson(data),
        });
    }

    list.Sort((a, b) => string.CompareOrdinal(a.symbol, b.symbol));
    return list.ToArray();
}

private static void ApplyItemsFull(Quest q, ItemStartDTO[] items)
{
    if (items == null) return;

    FieldInfo itemField = typeof(Item).GetField("item", BindingFlags.Instance | BindingFlags.NonPublic);
    if (itemField == null)
    {
        Debug.LogWarning("[QuestNetSync] Could not reflect Questing.Item.item field for full item sync.");
        return;
    }

    for (int i = 0; i < items.Length; i++)
    {
        ItemStartDTO dto = items[i];
        if (string.IsNullOrEmpty(dto.symbol)) continue;

        Item questItem = q.GetItem(new Symbol(dto.symbol));
        if (questItem == null) continue;

        // If this quest item was already given to the local player during startup tasks,
        // it will usually be the same object instance as questItem.DaggerfallUnityItem.
        // We'll only replace the inventory item if we can prove it exists (by reference),
        // to avoid accidentally granting extra items.
DaggerfallUnityItem oldItem = questItem.DaggerfallUnityItem;
bool oldWasInPlayerInventory = false;

try
{
    var gm = GameManager.Instance;
    var pe = gm != null ? gm.PlayerEntity : null;

    if (pe != null && pe.Items != null && oldItem != null)
    {
        var invItems = pe.Items; // ItemCollection
        for (int idx = 0; idx < invItems.Count; idx++)
        {
            DaggerfallUnityItem invItem = invItems.GetItem(idx);
            if (invItem == null)
                continue;

            if (ReferenceEquals(invItem, oldItem))
            {
                oldWasInPlayerInventory = true;
                break;
            }
        }
    }
}
catch { /* ignore */ }



        ItemData_v1 itemData;
        if (!FromJson(dto.itemDataJson, out itemData))
            continue;

        // Reconstruct exact item (fixes random subclass, dye colours, etc.)
        DaggerfallUnityItem rebuilt = new DaggerfallUnityItem(itemData);
        rebuilt.stackCount = NormalizeQuestItemStackCountForDto(rebuilt, dto.stackCount);

        // Ensure quest-link points at THIS quest UID + symbol, unless this symbol has
        // already crossed the one-way MakePermanent boundary locally.
        if (IsQuestItemPermanenceLatched(q, dto.symbol))
            rebuilt.MakePermanent();
        else
            rebuilt.LinkQuestItem(q.UID, questItem.Symbol.Clone());

        itemField.SetValue(questItem, rebuilt);
        if (oldWasInPlayerInventory)
            TrackQuestInventoryObject(q, dto.symbol, rebuilt);

        // Keep inventory consistent with quest text if the item was already granted.
        if (oldWasInPlayerInventory)
        {
            try
            {
                var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
                if (pe != null && pe.Items != null && oldItem != null)
                {
                    pe.Items.RemoveItem(oldItem);
                    pe.Items.AddItem(rebuilt);
                }
            }
            catch { /* ignore */ }
        }
    }
}

private ClockDTO[] BuildClocks(Quest q)
{
    QuestResource[] res = q.GetAllResources(typeof(Clock));
    if (res == null || res.Length == 0) return new ClockDTO[0];

    List<ClockDTO> list = new List<ClockDTO>(res.Length);
    for (int i = 0; i < res.Length; i++)
    {
        Clock c = res[i] as Clock;
        if (c == null || c.Symbol == null) continue;

        var sd = (Clock.SaveData_v1)c.GetSaveData();

        list.Add(new ClockDTO
        {
            symbol = c.Symbol.Name,
            startingTimeInSeconds = sd.startingTimeInSeconds,
            remainingTimeInSeconds = sd.remainingTimeInSeconds,
            flag = sd.flag,
            minRange = sd.minRange,
            maxRange = sd.maxRange,
            enabled = sd.clockEnabled,
            finished = sd.clockFinished,
        });
    }

    list.Sort((a, b) => string.CompareOrdinal(a.symbol, b.symbol));
    return list.ToArray();
}

private static void ApplyClocks(Quest q, ClockDTO[] clocks)
{
    if (clocks == null) return;

    Type t = typeof(Clock);
    FieldInfo fStart = t.GetField("startingTimeInSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fRemain = t.GetField("remainingTimeInSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fFlag = t.GetField("flag", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fMin = t.GetField("minRange", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fMax = t.GetField("maxRange", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fEnabled = t.GetField("clockEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fFinished = t.GetField("clockFinished", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fLast = t.GetField("lastWorldTimeSample", BindingFlags.Instance | BindingFlags.NonPublic);

    for (int i = 0; i < clocks.Length; i++)
    {
        ClockDTO dto = clocks[i];
        if (string.IsNullOrEmpty(dto.symbol)) continue;

        Clock c = FindResourceBySymbol<Clock>(q, dto.symbol);
        if (c == null) continue;

        if (fStart != null) fStart.SetValue(c, dto.startingTimeInSeconds);
        if (fRemain != null) fRemain.SetValue(c, dto.remainingTimeInSeconds);
        if (fFlag != null) fFlag.SetValue(c, dto.flag);
        if (fMin != null) fMin.SetValue(c, dto.minRange);
        if (fMax != null) fMax.SetValue(c, dto.maxRange);
        if (fEnabled != null) fEnabled.SetValue(c, dto.enabled);
        if (fFinished != null) fFinished.SetValue(c, dto.finished);

        // Reset sample to "now" so we don't instantly subtract a huge delta on first Tick().
        if (fLast != null)
            fLast.SetValue(c, DaggerfallUnity.Instance.WorldTime.Now.Clone());
    }
}

private static bool SameFoes(FoeDTO[] a, FoeDTO[] b)
{
    if (ReferenceEquals(a, b)) return true;
    if (a == null || b == null) return false;
    if (a.Length != b.Length) return false;

    for (int i = 0; i < a.Length; i++)
    {
        if (!string.Equals(a[i].symbol, b[i].symbol, StringComparison.Ordinal)) return false;
        if (a[i].foeId != b[i].foeId) return false;
        if (a[i].spawnCount != b[i].spawnCount) return false;
        if (a[i].humanoidGender != b[i].humanoidGender) return false;
        if (a[i].injuredTrigger != b[i].injuredTrigger) return false;
        if (a[i].restrained != b[i].restrained) return false;
        if (a[i].killCount != b[i].killCount) return false;
        if (!string.Equals(a[i].displayName ?? string.Empty, b[i].displayName ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(a[i].typeName ?? string.Empty, b[i].typeName ?? string.Empty, StringComparison.Ordinal)) return false;
    }

    return true;
}

private FoeDTO[] BuildFoes(Quest q)
{
    QuestResource[] res = q.GetAllResources(typeof(Foe));
    if (res == null || res.Length == 0) return new FoeDTO[0];

    List<FoeDTO> list = new List<FoeDTO>(res.Length);
    for (int i = 0; i < res.Length; i++)
    {
        Foe f = res[i] as Foe;
        if (f == null || f.Symbol == null) continue;

        var sd = (Foe.SaveData_v2)f.GetSaveData();

        list.Add(new FoeDTO
        {
            symbol = f.Symbol.Name,
            foeId = sd.foeId,
            spawnCount = sd.spawnCount,
            humanoidGender = (int)sd.humanoidGender,
            injuredTrigger = sd.injuredTrigger,
            restrained = sd.restrained,
            killCount = sd.killCount,
            displayName = sd.displayName ?? string.Empty,
            typeName = sd.typeName ?? string.Empty,
        });
    }

    list.Sort((a, b) => string.CompareOrdinal(a.symbol, b.symbol));
    return list.ToArray();
}

private static void ApplyFoes(Quest q, FoeDTO[] foes, bool replayInjuredPopups = false)
{
    if (foes == null) return;

    Type t = typeof(Foe);
    FieldInfo fSpawn = t.GetField("spawnCount", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fType  = t.GetField("foeType", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fGender = t.GetField("humanoidGender", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fInjured = t.GetField("injuredTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fRestrained = t.GetField("restrained", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fKill = t.GetField("killCount", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fDisp = t.GetField("displayName", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fTypeName = t.GetField("typeName", BindingFlags.Instance | BindingFlags.NonPublic);

    for (int i = 0; i < foes.Length; i++)
    {
        FoeDTO dto = foes[i];
        if (string.IsNullOrEmpty(dto.symbol)) continue;

        Foe f = FindResourceBySymbol<Foe>(q, dto.symbol);
        if (f == null) continue;

        bool oldInjuredTrigger = false;
        if (replayInjuredPopups && fInjured != null)
        {
            try { oldInjuredTrigger = (bool)fInjured.GetValue(f); }
            catch { oldInjuredTrigger = false; }
        }

        if (fSpawn != null) fSpawn.SetValue(f, dto.spawnCount);
        if (fType != null)  fType.SetValue(f, (MobileTypes)dto.foeId);
        if (fGender != null) fGender.SetValue(f, (Genders)dto.humanoidGender);
        if (fInjured != null) fInjured.SetValue(f, dto.injuredTrigger);

        if (replayInjuredPopups && !oldInjuredTrigger && dto.injuredTrigger)
            ReplayFoeInjuredMessageIfNeeded(q, dto.symbol);
        if (fRestrained != null) fRestrained.SetValue(f, dto.restrained);
        if (fKill != null) fKill.SetValue(f, dto.killCount);
        if (fDisp != null) fDisp.SetValue(f, dto.displayName ?? string.Empty);
        if (fTypeName != null) fTypeName.SetValue(f, dto.typeName ?? string.Empty);
    }
}


private static string MakeFoeInjuredMessageKey(ulong questUID, string foeSymbol, int messageId)
{
    return questUID.ToString() + "|foe-injured|" + (foeSymbol ?? string.Empty) + "|" + messageId.ToString();
}


private static string MakeFoePopupMessageKey(ulong questUID, string foeSymbol, int messageId, string reason)
{
    string cleanReason = string.IsNullOrEmpty(reason) ? "foe" : reason;
    if (string.Equals(cleanReason, "injured", StringComparison.OrdinalIgnoreCase))
        return MakeFoeInjuredMessageKey(questUID, foeSymbol, messageId);

    return questUID.ToString() + "|foe-" + cleanReason + "|" + (foeSymbol ?? string.Empty) + "|" + messageId.ToString();
}

private static uint GetLocalNetIdForFoePopupReport(QuestNetSync inst)
{
    if (_localNetId != 0)
        return _localNetId;

    try
    {
        if (inst != null && inst.netId != 0)
            return inst.netId;
    }
    catch { }

    return 0U;
}


private static bool IsApplyingQuestNetworkState(ulong questUID)
{
    if (questUID == 0UL)
        return false;

    string inst;
    if (_cliUid2Inst.TryGetValue(questUID, out inst) && !string.IsNullOrEmpty(inst) && _applying.Contains(inst))
        return true;

    if (_srvUid2Inst.TryGetValue(questUID, out inst) && !string.IsNullOrEmpty(inst) && _applying.Contains(inst))
        return true;

    return false;
}

/// <summary>
/// Called by local foe action templates before showing a foe "saying" popup.
/// This is a local one-shot guard, not quest state. It prevents the same popup being
/// shown once by vanilla task evaluation and once again by the MP side-channel echo.
/// </summary>
public static bool TryBeginLocalFoePopupMessage(ulong questUID, string foeSymbol, int messageId, string reason)
{
    if (questUID == 0UL || string.IsNullOrEmpty(foeSymbol) || messageId == 0)
        return true;

    string key = MakeFoePopupMessageKey(questUID, foeSymbol, messageId, reason);
    if (_remoteFoeInjuredMessagesShown.Contains(key))
        return false;

    _remoteFoeInjuredMessagesShown.Add(key);
    return true;
}

private static int _suppressFoeTriggerEventReportDepth = 0;

private static string NormalizeRuntimeQuestSymbol(string value)
{
    if (string.IsNullOrEmpty(value))
        return string.Empty;

    return value.Trim().Trim('_');
}

private static bool RuntimeQuestSymbolEquals(string value, string expected)
{
    return string.Equals(
        NormalizeRuntimeQuestSymbol(value),
        NormalizeRuntimeQuestSymbol(expected),
        StringComparison.OrdinalIgnoreCase);
}

private static DaggerfallWorkshop.Game.Questing.Task FindTaskByLooseSymbol(
    Quest q,
    string wantedSymbol)
{
    if (q == null || string.IsNullOrEmpty(wantedSymbol))
        return null;

    Quest.TaskState[] states = q.GetTaskStates();
    if (states == null)
        return null;

    for (int i = 0; i < states.Length; i++)
    {
        if (states[i].symbol == null ||
            !RuntimeQuestSymbolEquals(states[i].symbol.Name, wantedSymbol))
            continue;

        return q.GetTask(states[i].symbol);
    }

    return null;
}

private static bool IsTaskTriggeredByLooseSymbol(Quest q, string wantedSymbol)
{
    DaggerfallWorkshop.Game.Questing.Task task =
        FindTaskByLooseSymbol(q, wantedSymbol);
    return task != null && task.IsTriggered;
}

private static Person FindPersonByLooseSymbol(Quest q, string wantedSymbol)
{
    if (q == null || string.IsNullOrEmpty(wantedSymbol))
        return null;

    QuestResource[] resources = q.GetAllResources(typeof(Person));
    if (resources == null)
        return null;

    for (int i = 0; i < resources.Length; i++)
    {
        Person person = resources[i] as Person;
        if (person == null || person.Symbol == null)
            continue;

        if (RuntimeQuestSymbolEquals(person.Symbol.Name, wantedSymbol))
            return person;
    }

    return null;
}

private static Foe FindFoeByLooseSymbol(Quest q, string wantedSymbol)
{
    if (q == null || string.IsNullOrEmpty(wantedSymbol))
        return null;

    QuestResource[] resources = q.GetAllResources(typeof(Foe));
    if (resources == null)
        return null;

    for (int i = 0; i < resources.Length; i++)
    {
        Foe foe = resources[i] as Foe;
        if (foe == null || foe.Symbol == null)
            continue;

        if (RuntimeQuestSymbolEquals(foe.Symbol.Name, wantedSymbol))
            return foe;
    }

    return null;
}

private static bool IsLordKavarPartOneQuest(Quest q)
{
    return q != null &&
        string.Equals(
            NormalizeQuestTemplateName(q.QuestName),
            "M0B11Y18",
            StringComparison.OrdinalIgnoreCase);
}

private static bool IsLordKavarCombatFoe(string foeSymbol)
{
    return RuntimeQuestSymbolEquals(foeSymbol, "mtraitor");
}

private static bool HasLordKavarCombatOutcome(Quest q)
{
    return IsTaskTriggeredByLooseSymbol(q, "S.29") ||
           IsTaskTriggeredByLooseSymbol(q, "S.36") ||
           IsTaskTriggeredByLooseSymbol(q, "success") ||
           IsTaskTriggeredByLooseSymbol(q, "S.37");
}

private static bool IsPrematureLordKavarRulerClickTask(Quest q, string taskSymbol)
{
    return IsLordKavarPartOneQuest(q) &&
        RuntimeQuestSymbolEquals(taskSymbol, "S.30") &&
        !HasLordKavarCombatOutcome(q);
}

private static bool ShouldForceLordKavarFlatHidden(Quest q, string personSymbol)
{
    if (!IsLordKavarPartOneQuest(q) ||
        !RuntimeQuestSymbolEquals(personSymbol, "traitor"))
        return false;

    // _S.26_ is exactly: hide npc _traitor_; place foe _mtraitor_ at _stronghold_.
    // A loaded/catch-up quest can have these two representations disagree across
    // machines, so accept any durable sign that the combat phase has started.
    if (IsTaskTriggeredByLooseSymbol(q, "S.26") ||
        IsTaskTriggeredByLooseSymbol(q, "hittraitor") ||
        HasLordKavarCombatOutcome(q))
        return true;

    Foe foe = FindFoeByLooseSymbol(q, "mtraitor");
    return foe != null && (foe.InjuredTrigger || foe.KillCount > 0);
}

private static void RepairLordKavarCombatBoundaryState(
    Quest q,
    string foeSymbol,
    string reason,
    string source)
{
    if (!IsLordKavarPartOneQuest(q) ||
        !IsLordKavarCombatFoe(foeSymbol))
        return;

    bool hidFlat = false;
    bool clearedEarlyRulerClick = false;

    // During this combat _traitor_ must be the Ranger foe, never the old flat Person.
    Person traitor = FindPersonByLooseSymbol(q, "traitor");
    if (traitor != null)
    {
        if (!traitor.IsHidden)
        {
            traitor.IsHidden = true;
            hidFlat = true;
        }

        if (traitor.QuestResourceBehaviour != null &&
            traitor.QuestResourceBehaviour.gameObject.activeSelf)
        {
            traitor.QuestResourceBehaviour.gameObject.SetActive(false);
            hidFlat = true;
        }
    }

    // v36/v37 could persist _S.30_ from the *earlier* Queen letter hand-in. In this
    // quest _S.30_ means returning to Queen Akorithi AFTER K'avar dies/escapes, so it
    // cannot legitimately be true when combat begins and neither outcome exists yet.
    if (!HasLordKavarCombatOutcome(q))
    {
        DaggerfallWorkshop.Game.Questing.Task rulerClick =
            FindTaskByLooseSymbol(q, "S.30");
        if (rulerClick != null && rulerClick.IsTriggered)
        {
            q.ClearTask(rulerClick.Symbol);
            clearedEarlyRulerClick = true;
        }

        Person ruler = FindPersonByLooseSymbol(q, "ruler");
        if (ruler != null)
            ruler.RearmPlayerClick();
    }

    bool s29 = IsTaskTriggeredByLooseSymbol(q, "S.29");
    bool s30 = IsTaskTriggeredByLooseSymbol(q, "S.30");
    bool s36 = IsTaskTriggeredByLooseSymbol(q, "S.36");

    Debug.Log(
        $"[QuestNetSync][KavarStateRepair] uid={q.UID} reason='{reason}' " +
        $"source='{source}' hidFlat={hidFlat} " +
        $"clearedEarlyS30={clearedEarlyRulerClick} " +
        $"S29={s29} S30={s30} S36={s36}");
}

private static bool TryApplyRemoteFoeTriggerTask(
    ulong questUID,
    string foeSymbol,
    string taskSymbol,
    string reason,
    int progressValue,
    uint sourceNetId)
{
    if (questUID == 0UL ||
        string.IsNullOrEmpty(foeSymbol) ||
        string.IsNullOrEmpty(taskSymbol))
        return false;

    string actionType =
        string.Equals(reason, "injured", StringComparison.OrdinalIgnoreCase)
            ? "InjuredFoe"
            : string.Equals(reason, "killed", StringComparison.OrdinalIgnoreCase)
                ? "KilledFoe"
                : string.Empty;
    if (string.IsNullOrEmpty(actionType))
        return false;

    Quest q = QuestMachine.Instance != null
        ? QuestMachine.Instance.GetQuest(questUID)
        : null;
    if (q == null || q.QuestComplete || q.QuestTombstoned)
        return false;

    DaggerfallWorkshop.Game.Questing.Task task =
        q.GetTask(new Symbol(taskSymbol));
    if (task == null || task.Actions == null)
        return false;

    RepairLordKavarCombatBoundaryState(
        q,
        foeSymbol,
        reason,
        "remote-foe-trigger");

    bool exactTrigger = false;
    foreach (IQuestAction action in task.Actions)
    {
        if (action == null ||
            !string.Equals(action.GetType().Name, actionType, StringComparison.Ordinal))
            continue;

        FieldInfo foeField = action.GetType().GetField(
            "foeSymbol",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (foeField == null)
            continue;

        if (string.Equals(
                GetSymbolName(foeField.GetValue(action)),
                foeSymbol,
                StringComparison.OrdinalIgnoreCase))
        {
            exactTrigger = true;
            break;
        }
    }

    if (!exactTrigger)
        return false;

    Foe foe = FindResourceBySymbol<Foe>(q, foeSymbol);
    if (foe == null)
        return false;

    if (string.Equals(reason, "injured", StringComparison.OrdinalIgnoreCase))
    {
        if (!foe.InjuredTrigger)
            foe.SetInjured();
    }
    else
    {
        int targetKills = Math.Max(1, progressValue);
        if (foe.KillCount < targetKills)
            foe.IncrementKills(targetKills - foe.KillCount);
    }

    SuppressClientQuestEndReportFromRemoteTrigger(
        questUID,
        "remote-foe-trigger-task");

    _suppressFoeTriggerEventReportDepth++;
    try
    {
        if (!task.IsTriggered)
            q.StartTask(new Symbol(taskSymbol));

        ReassertClientQuestChainAuthorityAfterTaskState(
            q,
            "remote-foe-trigger-task");
    }
    finally
    {
        _suppressFoeTriggerEventReportDepth--;
    }

    if (Debug.isDebugBuild)
    {
        Debug.Log(
            $"[QuestNetSync][FoeTriggerTask] Applied uid={questUID} " +
            $"foe='{foeSymbol}' task='{taskSymbol}' reason='{reason}' " +
            $"progress={progressValue} source={sourceNetId}");
    }

    return true;
}

public static void ReportLocalFoePopupMessage(
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    int progressValue = 0)
{
    if (IsQuestNetSyncPausedForLoad() ||
        _suppressFoeTriggerEventReportDepth > 0)
        return;

    bool taskOnlyEvent =
        messageId == 0 &&
        !string.IsNullOrEmpty(taskSymbol) &&
        (string.Equals(reason, "injured", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(reason, "killed", StringComparison.OrdinalIgnoreCase));

    if (questUID == 0UL ||
        string.IsNullOrEmpty(foeSymbol) ||
        (messageId == 0 && !taskOnlyEvent))
        return;
    if (IsQuestSharingBlacklistedUid(questUID))
        return;

    if (taskOnlyEvent)
    {
        Quest localQuest = QuestMachine.Instance != null
            ? QuestMachine.Instance.GetQuest(questUID)
            : null;
        RepairLordKavarCombatBoundaryState(
            localQuest,
            foeSymbol,
            reason,
            "local-foe-trigger");
    }

    // If this local popup was produced while applying a remote quest state packet,
    // do not report it back. The original machine already sent/owns the side-effect,
    // and echoing it is what caused double popups.
    if (IsApplyingQuestNetworkState(questUID))
    {
        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] Suppressed foe popup echo during remote apply uid={questUID} foe='{foeSymbol}' msg={messageId} reason='{reason}' task='{taskSymbol}'");
        return;
    }

    QuestNetSync inst = LocalInstance;
    if (inst == null || !inst.isLocalPlayer || !inst.isClient)
        return;

    uint sourceNetId = GetLocalNetIdForFoePopupReport(inst);

    if (inst.isServer)
    {
        ServerBroadcastFoePopupMessage(
            inst.connectionToClient,
            GetServerHostLocalConnection(),
            questUID,
            foeSymbol,
            messageId,
            taskSymbol ?? string.Empty,
            reason ?? string.Empty,
            sourceNetId,
            progressValue);
    }
    else
    {
        inst.CmdFoePopupMessage(
            questUID,
            foeSymbol,
            messageId,
            taskSymbol ?? string.Empty,
            reason ?? string.Empty,
            sourceNetId,
            progressValue);
    }
}

[Command]
private void CmdFoePopupMessage(ulong questUID, string foeSymbol, int messageId, string taskSymbol, string reason, uint sourceNetId, int progressValue)
{
    if (IsQuestNetSyncPausedForLoad())
        return;

    bool taskOnlyEvent =
        messageId == 0 &&
        !string.IsNullOrEmpty(taskSymbol) &&
        (string.Equals(reason, "injured", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(reason, "killed", StringComparison.OrdinalIgnoreCase));

    if (!isServer ||
        questUID == 0UL ||
        string.IsNullOrEmpty(foeSymbol) ||
        (messageId == 0 && !taskOnlyEvent))
        return;
    if (IsQuestSharingBlacklistedUid(questUID))
        return;

    NetworkConnection sourceConnection = connectionToClient;
    NetworkConnection hostConnection = GetServerHostLocalConnection();

    if (hostConnection != null && hostConnection != sourceConnection)
    {
        ApplyRemoteFoePopupMessage(
            questUID,
            foeSymbol,
            messageId,
            taskSymbol,
            reason,
            sourceNetId,
            progressValue);
    }

    ServerBroadcastFoePopupMessage(
        sourceConnection,
        hostConnection,
        questUID,
        foeSymbol,
        messageId,
        taskSymbol,
        reason,
        sourceNetId,
        progressValue);
}

private static void ServerBroadcastFoePopupMessage(
    NetworkConnection sourceConnection,
    NetworkConnection alreadyAppliedHostConnection,
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    uint sourceNetId,
    int progressValue)
{
    int connectedCount = 0;
    int sentCount = 0;
    int missingIdentityCount = 0;
    int missingSyncCount = 0;

    foreach (var entry in NetworkServer.connections)
    {
        NetworkConnection recipient = entry.Value;
        if (recipient == null)
            continue;

        connectedCount++;

        if (recipient == sourceConnection ||
            recipient == alreadyAppliedHostConnection)
            continue;

        NetworkIdentity identity = recipient.identity;
        if (identity == null)
        {
            missingIdentityCount++;
            continue;
        }

        QuestNetSync sync = identity.GetComponent<QuestNetSync>();
        if (sync == null)
            sync = identity.GetComponentInChildren<QuestNetSync>(true);

        if (sync == null || !sync.isServer)
        {
            missingSyncCount++;
            continue;
        }

        sync.TargetFoePopupMessage(
            recipient,
            questUID,
            foeSymbol,
            messageId,
            taskSymbol,
            reason,
            sourceNetId,
            progressValue);
        sentCount++;
    }

    Debug.Log(
        $"[QuestNetSync][FoePopupFanout] Sent foe popup " +
        $"uid={questUID} foe='{foeSymbol}' msg={messageId} reason='{reason}' " +
        $"connected={connectedCount} recipients={sentCount} " +
        $"missingIdentity={missingIdentityCount} missingSync={missingSyncCount} " +
        $"source={sourceNetId}");
}

[TargetRpc]
private void TargetFoePopupMessage(
    NetworkConnection target,
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    uint sourceNetId,
    int progressValue)
{
    bool taskOnlyEvent =
        messageId == 0 &&
        !string.IsNullOrEmpty(taskSymbol) &&
        (string.Equals(reason, "injured", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(reason, "killed", StringComparison.OrdinalIgnoreCase));

    if (!isClient ||
        questUID == 0UL ||
        string.IsNullOrEmpty(foeSymbol) ||
        (messageId == 0 && !taskOnlyEvent))
        return;

    ApplyRemoteFoePopupMessage(
        questUID,
        foeSymbol,
        messageId,
        taskSymbol,
        reason,
        sourceNetId,
        progressValue);
}

private static void QueuePendingFoePopupMessage(
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    uint sourceNetId,
    string queueReason)
{
    if (questUID == 0UL || string.IsNullOrEmpty(foeSymbol) || messageId == 0)
        return;

    string key = MakeFoePopupMessageKey(
        questUID,
        foeSymbol,
        messageId,
        reason);

    if (_remoteFoeInjuredMessagesShown.Contains(key))
        return;

    PendingFoePopupMessage pending;
    if (!_pendingFoePopupMessages.TryGetValue(key, out pending))
    {
        pending = new PendingFoePopupMessage
        {
            questUID = questUID,
            foeSymbol = foeSymbol,
            messageId = messageId,
            taskSymbol = taskSymbol ?? string.Empty,
            reason = reason ?? string.Empty,
            sourceNetId = sourceNetId,
            queuedAtRealtime = Time.realtimeSinceStartup,
            nextDebugLogRealtime = 0f,
        };
    }

    pending.taskSymbol = taskSymbol ?? pending.taskSymbol ?? string.Empty;
    pending.reason = reason ?? pending.reason ?? string.Empty;
    pending.sourceNetId = sourceNetId;
    _pendingFoePopupMessages[key] = pending;

    if (Debug.isDebugBuild &&
        Time.realtimeSinceStartup >= pending.nextDebugLogRealtime)
    {
        pending.nextDebugLogRealtime = Time.realtimeSinceStartup + 3f;
        _pendingFoePopupMessages[key] = pending;

        Debug.Log(
            $"[QuestNetSync][FoePopupQueue] Queued uid={questUID} " +
            $"foe='{foeSymbol}' msg={messageId} reason='{reason}' " +
            $"queueReason='{queueReason}'");
    }
}

private static bool TryApplyRemoteFoePopupMessageImmediate(
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    uint sourceNetId)
{
    Quest q = QuestMachine.Instance != null
        ? QuestMachine.Instance.GetQuest(questUID)
        : null;
    if (q == null || messageId == 0)
        return false;

    string key = MakeFoePopupMessageKey(
        questUID,
        foeSymbol,
        messageId,
        reason);
    if (_remoteFoeInjuredMessagesShown.Contains(key))
        return true;

    try
    {
        q.ShowMessagePopup(messageId);

        // Consume the one-shot only after the UI call succeeds.
        _remoteFoeInjuredMessagesShown.Add(key);

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync] Applied remote foe popup uid={questUID} " +
                $"foe='{foeSymbol}' msg={messageId} reason='{reason}' " +
                $"task='{taskSymbol}' source={sourceNetId}");
        }
        return true;
    }
    catch (Exception ex)
    {
        Debug.LogWarning(
            "[QuestNetSync] Failed to apply remote foe popup: " +
            ex.Message);
        return false;
    }
}

private static void ApplyRemoteFoePopupMessage(
    ulong questUID,
    string foeSymbol,
    int messageId,
    string taskSymbol,
    string reason,
    uint sourceNetId,
    int progressValue = 0)
{
    if (questUID == 0UL || string.IsNullOrEmpty(foeSymbol))
        return;

    // messageId 0 is an exact task-only foe event used by trigger actions whose
    // dialogue/timer lives in later actions of the same task. Apply quest state
    // immediately even if another modal window is open; the task's Say action can
    // wait for the normal QuestMachine/UI flow, but its boolean must beat timers.
    if (messageId == 0)
    {
        TryApplyRemoteFoeTriggerTask(
            questUID,
            foeSymbol,
            taskSymbol,
            reason,
            progressValue,
            sourceNetId);
        return;
    }

    string key = MakeFoePopupMessageKey(
        questUID,
        foeSymbol,
        messageId,
        reason);
    if (_remoteFoeInjuredMessagesShown.Contains(key))
        return;

    if (ShouldDeferQuestInventoryApplyNow())
    {
        QueuePendingFoePopupMessage(
            questUID,
            foeSymbol,
            messageId,
            taskSymbol,
            reason,
            sourceNetId,
            "ui-paused");
        return;
    }

    if (!TryApplyRemoteFoePopupMessageImmediate(
            questUID,
            foeSymbol,
            messageId,
            taskSymbol,
            reason,
            sourceNetId))
    {
        QueuePendingFoePopupMessage(
            questUID,
            foeSymbol,
            messageId,
            taskSymbol,
            reason,
            sourceNetId,
            "quest-not-ready");
    }
}

private static void ProcessPendingFoePopupMessages()
{
    if (_pendingFoePopupMessages.Count == 0)
        return;

    if (IsQuestNetSyncPausedForLoad() ||
        ShouldDeferQuestInventoryApplyNow())
        return;

    string[] keys = _pendingFoePopupMessages.Keys.ToArray();
    for (int i = 0; i < keys.Length; i++)
    {
        PendingFoePopupMessage pending;
        if (!_pendingFoePopupMessages.TryGetValue(keys[i], out pending))
            continue;

        if (_remoteFoeInjuredMessagesShown.Contains(keys[i]))
        {
            _pendingFoePopupMessages.Remove(keys[i]);
            continue;
        }

        if (TryApplyRemoteFoePopupMessageImmediate(
                pending.questUID,
                pending.foeSymbol,
                pending.messageId,
                pending.taskSymbol,
                pending.reason,
                pending.sourceNetId))
        {
            _pendingFoePopupMessages.Remove(keys[i]);
            Debug.Log(
                $"[QuestNetSync][FoePopupQueue] Applied queued popup " +
                $"uid={pending.questUID} foe='{pending.foeSymbol}' " +
                $"msg={pending.messageId} reason='{pending.reason}'");
        }
    }
}

private static void ReplayFoeInjuredMessageIfNeeded(Quest q, string foeSymbol)
{
    if (q == null || string.IsNullOrEmpty(foeSymbol))
        return;

    try
    {
        List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
        for (int i = 0; i < tasks.Count; i++)
        {
            DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
            if (task == null || task.Actions == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null)
                    continue;

                if (!string.Equals(action.GetType().Name, "InjuredFoe", StringComparison.Ordinal) &&
                    action.DebugSource != null &&
                    action.DebugSource.IndexOf("injured ", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string actionFoe;
                int messageId;
                if (!TryGetInjuredFoeActionInfo(action, out actionFoe, out messageId))
                    continue;

                if (!string.Equals(actionFoe, foeSymbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (messageId == 0)
                    return;

                string key = MakeFoeInjuredMessageKey(q.UID, foeSymbol, messageId);
                if (_remoteFoeInjuredMessagesShown.Contains(key))
                    return;

                ApplyRemoteFoePopupMessage(
                    q.UID,
                    foeSymbol,
                    messageId,
                    task.Symbol != null ? task.Symbol.Name : string.Empty,
                    "injured",
                    0U);
                return;
            }
        }
    }
    catch (Exception ex)
    {
        Debug.LogWarning("[QuestNetSync] Failed to replay injured-foe message: " + ex.Message);
    }
}

private static bool TryGetInjuredFoeActionInfo(IQuestAction action, out string foeSymbol, out int messageId)
{
    foeSymbol = null;
    messageId = 0;
    if (action == null)
        return false;

    Type actionType = action.GetType();

    // Prefer the real private fields when available. DFU's InjuredFoe action follows
    // the same shape as ClickedNpc/TotingItemAndClickedNpc: Symbol + int id.
    try
    {
        FieldInfo[] fields = actionType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field == null)
                continue;

            object value = field.GetValue(action);
            if (foeSymbol == null && value is Symbol)
                foeSymbol = GetSymbolName(value);
            else if (messageId == 0 && field.FieldType == typeof(int) &&
                     (string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(field.Name, "textId", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(field.Name, "textID", StringComparison.OrdinalIgnoreCase)))
                messageId = (int)value;
        }
    }
    catch { }

    if (!string.IsNullOrEmpty(foeSymbol))
        return true;

    // Fallback: parse the source line for versions where field names differ.
    try
    {
        string src = action.DebugSource ?? string.Empty;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            src,
            @"injured\s+(?<foe>[a-zA-Z0-9_.-]+)(?:\s+saying\s+(?<id>\d+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (m.Success)
        {
            foeSymbol = m.Groups["foe"].Value;
            int.TryParse(m.Groups["id"].Value, out messageId);
            return true;
        }
    }
    catch { }

    return false;
}

private static void LogQuestResources(Quest q)
{
    if (q == null) return;

    try
    {
        // Foes
        QuestResource[] foes = q.GetAllResources(typeof(Foe));
        if (foes != null && foes.Length > 0)
        {
            foreach (var r in foes)
            {
                Foe f = r as Foe;
                if (f == null || f.Symbol == null) continue;
                Debug.Log($"[QuestNetSync] Quest '{q.QuestName}' FOE {f.Symbol.Name}: {f.FoeType} (id={(int)f.FoeType}) x{f.SpawnCount}");
            }
        }

        // Items
        QuestResource[] items = q.GetAllResources(typeof(Item));
        if (items != null && items.Length > 0)
        {
            foreach (var r in items)
            {
                Item it = r as Item;
                if (it == null || it.Symbol == null || it.DaggerfallUnityItem == null) continue;
                var df = it.DaggerfallUnityItem;
                Debug.Log($"[QuestNetSync] Quest '{q.QuestName}' ITEM {it.Symbol.Name}: group={(int)df.ItemGroup} index={df.GroupIndex} dye={(int)df.dyeColor} stack={df.stackCount}");
            }
        }

        // Clocks
        QuestResource[] clocks = q.GetAllResources(typeof(Clock));
        if (clocks != null && clocks.Length > 0)
        {
            foreach (var r in clocks)
            {
                Clock c = r as Clock;
                if (c == null || c.Symbol == null) continue;
                Debug.Log($"[QuestNetSync] Quest '{q.QuestName}' CLOCK {c.Symbol.Name}: start={c.StartingTimeInSeconds}s remain={c.RemainingTimeInSeconds}s enabled={c.Enabled} finished={c.Finished}");
            }
        }
    }
    catch (Exception e)
    {
        Debug.LogWarning($"[QuestNetSync] LogQuestResources failed: {e}");
    }
}



private static bool SamePlaceDto(PlaceDTO a, PlaceDTO b)
{
    return
        string.Equals(a.symbol ?? string.Empty, b.symbol ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
        a.scope == b.scope &&
        string.Equals(a.name ?? string.Empty, b.name ?? string.Empty, StringComparison.Ordinal) &&
        a.p1 == b.p1 && a.p2 == b.p2 && a.p3 == b.p3 &&
        a.siteType == b.siteType &&
        a.mapId == b.mapId &&
        a.locationId == b.locationId &&
        a.regionIndex == b.regionIndex &&
        string.Equals(a.regionName ?? string.Empty, b.regionName ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(a.locationName ?? string.Empty, b.locationName ?? string.Empty, StringComparison.Ordinal) &&
        a.buildingKey == b.buildingKey &&
        string.Equals(a.buildingName ?? string.Empty, b.buildingName ?? string.Empty, StringComparison.Ordinal) &&
        a.magicNumberIndex == b.magicNumberIndex &&
        string.Equals(
            a.markerTargetsFingerprint ?? string.Empty,
            b.markerTargetsFingerprint ?? string.Empty,
            StringComparison.Ordinal);
}

private static bool SamePlaces(PlaceDTO[] a, PlaceDTO[] b)
{
    if (ReferenceEquals(a, b)) return true;
    if (a == null || b == null) return false;
    if (a.Length != b.Length) return false;

    Dictionary<string, PlaceDTO> bySymbol =
        new Dictionary<string, PlaceDTO>(StringComparer.OrdinalIgnoreCase);

    for (int i = 0; i < b.Length; i++)
    {
        if (string.IsNullOrEmpty(b[i].symbol))
            return false;
        bySymbol[b[i].symbol] = b[i];
    }

    for (int i = 0; i < a.Length; i++)
    {
        if (string.IsNullOrEmpty(a[i].symbol))
            return false;

        PlaceDTO other;
        if (!bySymbol.TryGetValue(a[i].symbol, out other) || !SamePlaceDto(a[i], other))
            return false;
    }

    return true;
}

private static bool SamePersons(PersonDTO[] a, PersonDTO[] b)
{
    if (ReferenceEquals(a, b)) return true;
    if (a == null || b == null) return false;
    if (a.Length != b.Length) return false;

    for (int i = 0; i < a.Length; i++)
    {
        if (!string.Equals(a[i].symbol, b[i].symbol, StringComparison.Ordinal)) return false;
        if (a[i].race != b[i].race) return false;
        if (a[i].gender != b[i].gender) return false;
        if (a[i].faceIndex != b[i].faceIndex) return false;
        if (a[i].nameSeed != b[i].nameSeed) return false;
        if (a[i].isQuestor != b[i].isQuestor) return false;
        if (a[i].isIndividualNPC != b[i].isIndividualNPC) return false;
        if (a[i].isIndividualAtHome != b[i].isIndividualAtHome) return false;
        if (!string.Equals(a[i].displayName ?? string.Empty, b[i].displayName ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(a[i].homePlaceSymbol ?? string.Empty, b[i].homePlaceSymbol ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(a[i].lastAssignedPlaceSymbol ?? string.Empty, b[i].lastAssignedPlaceSymbol ?? string.Empty, StringComparison.Ordinal)) return false;
        if (a[i].assignedToHome != b[i].assignedToHome) return false;
        if (a[i].factionID != b[i].factionID) return false;
        if (!string.Equals(a[i].factionTableKey ?? string.Empty, b[i].factionTableKey ?? string.Empty, StringComparison.Ordinal)) return false;
        if (a[i].discoveredThroughTalkManager != b[i].discoveredThroughTalkManager) return false;
        if (a[i].isMuted != b[i].isMuted) return false;
        if (a[i].isDestroyed != b[i].isDestroyed) return false;
        if (a[i].isHidden != b[i].isHidden) return false;
        // HasPlayerClicked is transient player input, not durable Person state. Live
        // NPC clicks use the explicit exact-task RPC and must never generate routine
        // PersonDTO deltas that can overtake that RPC on another participant.
    }

    return true;
}

    private PersonDTO[] BuildPersons(Quest q)
    {
        QuestResource[] res = q.GetAllResources(typeof(Person));
        if (res == null || res.Length == 0) return new PersonDTO[0];

        List<PersonDTO> list = new List<PersonDTO>(res.Length);
        for (int i = 0; i < res.Length; i++)
        {
            Person p = res[i] as Person;
            if (p == null) continue;

            QuestResource.ResourceSaveData_v1 rsd = p.GetResourceSaveData();
            var sd = (Person.SaveData_v1)rsd.resourceSpecific;

            PersonDTO dto = new PersonDTO();
            dto.symbol = p.Symbol.Name;
            dto.race   = (int)sd.race;
            dto.gender = (int)sd.npcGender;
            dto.faceIndex = sd.faceIndex;
            dto.nameSeed  = sd.nameSeed;
            dto.isQuestor = sd.isQuestor;
            dto.isIndividualNPC = sd.isIndividualNPC;
            dto.isIndividualAtHome = sd.isIndividualAtHome;
            dto.displayName = sd.displayName ?? string.Empty;
            dto.homePlaceSymbol = sd.homePlaceSymbol != null ? sd.homePlaceSymbol.Name : string.Empty;
            dto.lastAssignedPlaceSymbol = sd.lastAssignedPlaceSymbol != null ? sd.lastAssignedPlaceSymbol.Name : string.Empty;
            dto.assignedToHome = sd.assignedToHome;
            dto.factionID = sd.factionID;
            dto.factionTableKey = sd.factionTableKey ?? string.Empty;
            dto.discoveredThroughTalkManager = sd.discoveredThroughTalkManager;
            dto.isMuted = sd.isMuted;
            dto.isDestroyed = sd.isDestroyed;
            dto.isHidden = rsd.isHidden ||
                ShouldForceLordKavarFlatHidden(q, p.Symbol.Name);
            // Never serialize a physical click as generic Person state. Restoring this
            // flag on another player makes that machine report the copied click as a
            // new local interaction owner. Task state carries durable progress; the
            // explicit Person-click RPC carries the live input event.
            dto.hasPlayerClicked = false;
            dto.saveDataJson = ToJson(sd);

            list.Add(dto);
        }
        return list.ToArray();
    }

    private static void AppendQuestMarkerTargetsFingerprint(
        StringBuilder builder,
        string group,
        int index,
        QuestMarker marker)
    {
        builder.Append(group)
            .Append('#')
            .Append(index)
            .Append('@')
            .Append((int)marker.markerType)
            .Append(':')
            .Append(marker.markerID)
            .Append(':')
            .Append(marker.dungeonX)
            .Append(':')
            .Append(marker.dungeonZ)
            .Append(':')
            .Append(marker.buildingKey)
            .Append('=');

        if (marker.targetResources != null && marker.targetResources.Count > 0)
        {
            List<string> symbols = new List<string>(marker.targetResources.Count);
            for (int i = 0; i < marker.targetResources.Count; i++)
            {
                Symbol symbol = marker.targetResources[i];
                if (symbol != null && !string.IsNullOrEmpty(symbol.Name))
                    symbols.Add(symbol.Name);
            }

            symbols.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(symbols[i]);
            }
        }

        builder.Append(';');
    }

    private static string BuildPlaceMarkerTargetsFingerprint(SiteDetails site)
    {
        StringBuilder builder = new StringBuilder(128);

        AppendQuestMarkerTargetsFingerprint(
            builder,
            "selected",
            0,
            site.selectedMarker);

        QuestMarker[] spawnMarkers = site.questSpawnMarkers;
        if (spawnMarkers != null)
        {
            for (int i = 0; i < spawnMarkers.Length; i++)
            {
                AppendQuestMarkerTargetsFingerprint(
                    builder,
                    "spawn",
                    i,
                    spawnMarkers[i]);
            }
        }

        QuestMarker[] itemMarkers = site.questItemMarkers;
        if (itemMarkers != null)
        {
            for (int i = 0; i < itemMarkers.Length; i++)
            {
                AppendQuestMarkerTargetsFingerprint(
                    builder,
                    "item",
                    i,
                    itemMarkers[i]);
            }
        }

        return builder.ToString();
    }

    private PlaceDTO[] BuildPlaces(Quest q)
    {
        QuestResource[] res = q.GetAllResources(typeof(Place));
        if (res == null || res.Length == 0) return new PlaceDTO[0];

        List<PlaceDTO> list = new List<PlaceDTO>(res.Length);
        for (int i = 0; i < res.Length; i++)
        {
            Place pl = res[i] as Place;
            if (pl == null) continue;

            var sd = (Place.SaveData_v1)pl.GetSaveData();
            var site = sd.siteDetails;

            PlaceDTO dto = new PlaceDTO();
            dto.symbol = pl.Symbol.Name;
            dto.scope  = (int)sd.scope;
            dto.name   = sd.name ?? string.Empty;
            dto.p1 = sd.p1; dto.p2 = sd.p2; dto.p3 = sd.p3;

            // read into ints; DFU fields may be int/uint internally
            dto.siteType      = (int)site.siteType;
            dto.mapId         = (int)site.mapId;
            dto.locationId    = unchecked((int)site.locationId);
            dto.regionIndex   = (int)site.regionIndex;
            dto.regionName    = site.regionName ?? string.Empty;
            dto.locationName  = site.locationName ?? string.Empty;
            dto.buildingKey   = (int)site.buildingKey; // DFU side int
            dto.buildingName  = site.buildingName ?? string.Empty;
            dto.magicNumberIndex = (int)site.magicNumberIndex;
            dto.markerTargetsFingerprint =
                BuildPlaceMarkerTargetsFingerprint(site);
            dto.saveDataJson = ToJson(sd);

            list.Add(dto);
        }
        return list.ToArray();
    }



    /// <summary>
    /// Multiplayer teleport completion hook. Quest prompt branches can execute TeleportPc
    /// before the destination dungeon has finished building on a pure client. Refreshing
    /// placed items from the prompt itself can therefore run against the old site. TeleportPc
    /// calls this only after the destination dungeon is actually active and the player has
    /// reached the final quest marker, so placed quest items are injected against the correct
    /// dungeon without a time-based delay.
    /// </summary>
    public static void NotifyLocalQuestTeleportDestinationReady(ulong questUID, string reason)
    {
        try
        {
            if ((!NetworkClient.active && !NetworkServer.active) ||
                questUID == 0UL ||
                QuestMachine.Instance == null)
                return;

            Quest q = QuestMachine.Instance.GetQuest(questUID);
            if (q == null || q.QuestComplete || q.QuestTombstoned)
                return;

            RefreshCurrentSiteQuestItemObjects();

            Debug.Log(
                $"[QuestNetSync][TeleportItemRefresh] Refreshed destination quest items " +
                $"uid={questUID} quest='{q.QuestName}' reason='{reason}'");
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][TeleportItemRefresh] Destination item refresh failed " +
                $"uid={questUID} reason='{reason}' error={ex.Message}");
        }
    }

    private static void RefreshCurrentSiteQuestItemObjects()
    {
        try
        {
            if (GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null)
                return;

            PlayerEnterExit enterExit = GameManager.Instance.PlayerEnterExit;

            if (enterExit.IsPlayerInsideBuilding && enterExit.Interior != null)
            {
                int buildingKey = 0;
                try { buildingKey = enterExit.BuildingDiscoveryData.buildingKey; } catch { buildingKey = 0; }
                try
                {
                    if (buildingKey == 0 && enterExit.Interior.EntryDoor.buildingKey != 0)
                        buildingKey = enterExit.Interior.EntryDoor.buildingKey;
                }
                catch { }

                if (buildingKey != 0)
                {
                    DaggerfallWorkshop.Utility.GameObjectHelper.AddQuestResourceObjects(
                        SiteTypes.Building,
                        enterExit.Interior.transform,
                        buildingKey,
                        false,
                        false,
                        true);
                }
            }
            else if (enterExit.IsPlayerInsideDungeon && enterExit.Dungeon != null)
            {
                DaggerfallWorkshop.Utility.GameObjectHelper.AddQuestResourceObjects(
                    SiteTypes.Dungeon,
                    enterExit.Dungeon.transform,
                    0,
                    false,
                    false,
                    true);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[QuestNetSync][PlacedItemRefresh] Current-site item injection failed: " + ex.Message);
        }
    }

    private static void RefreshCurrentSiteQuestResourceObjects()
    {
        try
        {
            if (!NetworkClient.active && !NetworkServer.active)
                return;

            // Throttle because this can be called from QuestMachine ticks and ApplyPersons.
            if (Time.realtimeSinceStartup < _nextCurrentSiteQuestResourceRefreshTime)
                return;

            _nextCurrentSiteQuestResourceRefreshTime = Time.realtimeSinceStartup + 1.0f;

            if (GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null)
                return;

            PlayerEnterExit enterExit = GameManager.Instance.PlayerEnterExit;

            if (enterExit.IsPlayerInsideBuilding && enterExit.Interior != null)
            {
                int buildingKey = 0;
                try { buildingKey = enterExit.BuildingDiscoveryData.buildingKey; } catch { buildingKey = 0; }
                try
                {
                    if (buildingKey == 0 && enterExit.Interior.EntryDoor.buildingKey != 0)
                        buildingKey = enterExit.Interior.EntryDoor.buildingKey;
                }
                catch { }

                if (buildingKey != 0)
                {
                    // NPC-only refresh. GameObjectHelper now respects enableItems=false.
                    DaggerfallWorkshop.Utility.GameObjectHelper.AddQuestResourceObjects(
                        SiteTypes.Building,
                        enterExit.Interior.transform,
                        buildingKey,
                        true,
                        false,
                        false);
                }
            }
            else if (enterExit.IsPlayerInsideDungeon && enterExit.Dungeon != null)
            {
                DaggerfallWorkshop.Utility.GameObjectHelper.AddQuestResourceObjects(
                    SiteTypes.Dungeon,
                    enterExit.Dungeon.transform,
                    0,
                    true,
                    false,
                    false);
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] RefreshCurrentSiteQuestResourceObjects failed: " + ex.Message);
        }
    }

    private static void RemoveNonPermanentQuestInventoryItems(Quest q)
    {
        if (q == null)
            return;

        try
        {
            QuestResource[] resources = q.GetAllResources(typeof(Item));
            if (resources == null || resources.Length == 0)
                return;

            for (int i = 0; i < resources.Length; i++)
            {
                Item item = resources[i] as Item;
                if (item == null || item.Symbol == null)
                    continue;

                // MakePermanent is used by quests to keep something after quest end.
                // Also respect the runtime permanence latch: a stale ItemDTO/resource
                // reconstruction must not make cleanup delete the exact white reward.
                if (item.MadePermanent || IsQuestItemPermanenceLatched(q, item.Symbol.Name))
                {
                    if (!item.MadePermanent)
                        ApplyQuestItemMadePermanent(q, item.Symbol.Name, "end-cleanup-latch");
                    continue;
                }

                if (!AllowQuestInventoryRepair(q, item.Symbol.Name))
                    continue;

                SetQuestItemInventory(q, item.Symbol.Name, false);
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync] RemoveNonPermanentQuestInventoryItems failed: " + ex.Message);
        }
    }

    private static void ApplyPersons(Quest q, PersonDTO[] persons, bool finalQuestEnd = false)
    {
        if (persons == null) return;

        for (int i = 0; i < persons.Length; i++)
        {
            PersonDTO pd = persons[i];
            Person p = q.GetPerson(new Symbol(pd.symbol));
            if (p == null) continue;

            Person.SaveData_v1 sd;

            // Preferred: restore full Person save-data (contains QuestorData used for dialog hooks).
            if (!string.IsNullOrEmpty(pd.saveDataJson) && FromJson<Person.SaveData_v1>(pd.saveDataJson, out sd))
            {
                // sd already populated from sender
            }
            else
            {
                sd = (Person.SaveData_v1)p.GetSaveData(); // start from current
            }
            sd.race = (Races)pd.race;
            sd.npcGender = (Genders)pd.gender;
            sd.faceIndex = pd.faceIndex;
            sd.nameSeed  = pd.nameSeed;
            sd.isQuestor = pd.isQuestor;
            sd.isIndividualNPC = pd.isIndividualNPC;
            sd.isIndividualAtHome = pd.isIndividualAtHome;
            sd.displayName = pd.displayName ?? sd.displayName;
            sd.homePlaceSymbol = string.IsNullOrEmpty(pd.homePlaceSymbol) ? null : new Symbol(pd.homePlaceSymbol);
            sd.lastAssignedPlaceSymbol = string.IsNullOrEmpty(pd.lastAssignedPlaceSymbol) ? null : new Symbol(pd.lastAssignedPlaceSymbol);
            sd.assignedToHome = pd.assignedToHome;
            sd.factionID = pd.factionID;
            sd.factionTableKey = pd.factionTableKey ?? sd.factionTableKey;
            sd.discoveredThroughTalkManager = pd.discoveredThroughTalkManager;
            sd.isMuted = pd.isMuted;
            sd.isDestroyed = pd.isDestroyed;

            bool wasDestroyed = p.IsDestroyed;
            p.RestoreSaveData(sd);

            // Restore durable base QuestResource state. Preserve this machine's local
            // HasPlayerClicked flag: it is transient input and is delivered only by the
            // explicit exact-task click RPC, never by a generic PersonDTO snapshot.
            QuestResource.ResourceSaveData_v1 rsd = p.GetResourceSaveData();
            bool localHasPlayerClicked = rsd.hasPlayerClicked;
            rsd.hasPlayerClicked = localHasPlayerClicked;

            // A final EndPacket/CmdClientEnded snapshot describes a quest that is already
            // over on the authoritative machine. Its PersonDTO can legitimately still say
            // isHidden=false because scripts such as M0B11Y18 end the parent quest without
            // first running HideNpc on Lord K'avar. Treat every parent Person as locally
            // hidden during this final-state drain so generic snapshots cannot resurrect
            // the scene object while another player's Say popup is being closed.
            bool effectiveHidden =
                pd.isHidden ||
                finalQuestEnd ||
                ShouldForceLordKavarFlatHidden(q, pd.symbol);
            rsd.isHidden = effectiveHidden;
            rsd.resourceSpecific = sd;
            p.RestoreResourceSaveData(rsd);

            // DestroyNPC() has runtime meaning beyond the save flag: quest resource Tick() hides
            // the linked flat NPC. Call it when the remote state newly destroys this person.
            if (pd.isDestroyed && !wasDestroyed)
                p.DestroyNPC();

            if (effectiveHidden)
                p.IsHidden = true;
            else if (!pd.isDestroyed)
                p.IsHidden = false;

            if (p.QuestResourceBehaviour != null)
            {
                bool keepSceneHandoffSuppressed =
                    IsScenePersonHandoffBehaviourSuppressed(
                        p.QuestResourceBehaviour);

                if (pd.isDestroyed ||
                    effectiveHidden ||
                    keepSceneHandoffSuppressed)
                    p.QuestResourceBehaviour.gameObject.SetActive(false);
                else
                    p.QuestResourceBehaviour.gameObject.SetActive(true);

                if (keepSceneHandoffSuppressed && Debug.isDebugBuild)
                {
                    Debug.Log(
                        $"[QuestNetSync][SceneResourceHandoff] Kept imported " +
                        $"person suppressed during state apply uid={q.UID} " +
                        $"person='{pd.symbol}'");
                }
            }

            // Do not call SetPlayerClicked() here. That call used to turn a replicated
            // snapshot into a fresh physical click on every client, allowing each popup
            // close to contribute another quest step.
        }

        // If a Person became visible before this local machine had loaded the building/
        // dungeon interior that contains it, the actual QuestResourceBehaviour may not
        // exist yet. Try a cheap current-site refresh so late entrants can see restored
        // remote quest NPCs such as the Mummy Finger temple sage.
        //
        // Never do this for a final quest-end snapshot. The parent quest remains alive
        // briefly while reward UI drains, and a refresh here can recreate a Person whose
        // authoritative final DTO still says visible.
        if (!finalQuestEnd)
            RefreshCurrentSiteQuestResourceObjects();
    }


    private static void ApplyPlaces(Quest q, PlaceDTO[] places)
    {
        if (places == null) return;

        for (int i = 0; i < places.Length; i++)
        {
            PlaceDTO rd = places[i];
            if (string.IsNullOrEmpty(rd.symbol)) continue;

            Place pl = q.GetPlace(new Symbol(rd.symbol));
            if (pl == null) continue;

            // Preferred: restore full Place save-data (includes quest spawn markers and target resource bindings).
            // This is what prevents quest NPCs from spawning at different markers/offsets on non-owners.
            if (!string.IsNullOrEmpty(rd.saveDataJson))
            {
                Place.SaveData_v1 full;
                if (FromJson(rd.saveDataJson, out full))
                {
                    // Ensure linkage is correct for this local quest instance.
                    var siteFull = full.siteDetails;
                    siteFull.questUID = q.UID;
                    full.siteDetails = siteFull;

                    full.scope = (Place.Scopes)rd.scope;
                    full.name  = rd.name ?? full.name;
                    full.p1 = rd.p1; full.p2 = rd.p2; full.p3 = rd.p3;

                    pl.RestoreSaveData(full);
                    continue;
                }
            }

            // Fallback: patch identity fields like the original version (but include siteType).
            var sd = (Place.SaveData_v1)pl.GetSaveData();
            var site = sd.siteDetails;

            site.siteType      = (SiteTypes)rd.siteType;
            site.mapId         = rd.mapId;
            site.locationId    = unchecked((uint)rd.locationId);
            site.regionIndex   = rd.regionIndex;
            site.regionName    = rd.regionName ?? site.regionName;
            site.locationName  = rd.locationName ?? site.locationName;
            site.buildingKey   = rd.buildingKey;
            site.buildingName  = rd.buildingName ?? site.buildingName;
            site.magicNumberIndex = rd.magicNumberIndex;

            sd.scope = (Place.Scopes)rd.scope;
            sd.name  = rd.name ?? sd.name;
            sd.p1 = rd.p1; sd.p2 = rd.p2; sd.p3 = rd.p3;
            sd.siteDetails = site;

            pl.RestoreSaveData(sd);
        }

        RebuildQuestSiteLinksFromPlaces(q);
    }


    private static void RebuildQuestSiteLinksFromPlaces(Quest q)
    {
        if (q == null || QuestMachine.Instance == null)
            return;

        QuestMachine.Instance.RemoveAllQuestSiteLinks(q.UID);

        QuestResource[] places = q.GetAllResources(typeof(Place));
        if (places == null)
            return;

        for (int i = 0; i < places.Length; i++)
        {
            Place place = places[i] as Place;
            if (place == null || place.Symbol == null)
                continue;

            SiteDetails site = place.SiteDetails;
            if (site.siteType == SiteTypes.None || site.mapId == 0)
                continue;

            if (!SiteHasQuestTargets(site))
                continue;

            SiteLink link = new SiteLink();
            link.questUID = q.UID;
            link.placeSymbol = place.Symbol.Clone();
            link.siteType = site.siteType;
            link.mapId = site.mapId;
            link.buildingKey = site.buildingKey;
            link.magicNumberIndex = site.magicNumberIndex;
            QuestMachine.Instance.AddSiteLink(link);

            if (Debug.isDebugBuild)
            {
                Debug.Log($"[QuestNetSync] Rebuilt SiteLink uid={q.UID} place={place.Symbol.Name} type={site.siteType} map={site.mapId} building={site.buildingKey} magic={site.magicNumberIndex}");
            }
        }
    }

    private static bool SiteHasQuestTargets(SiteDetails site)
    {
        if (site.selectedMarker.targetResources != null && site.selectedMarker.targetResources.Count > 0)
            return true;

        if (MarkersHaveTargets(site.questSpawnMarkers))
            return true;

        if (MarkersHaveTargets(site.questItemMarkers))
            return true;

        return false;
    }

    private static bool MarkersHaveTargets(QuestMarker[] markers)
    {
        if (markers == null)
            return false;

        for (int i = 0; i < markers.Length; i++)
        {
            if (markers[i].targetResources != null && markers[i].targetResources.Count > 0)
                return true;
        }

        return false;
    }


    private Quest BindMostRecentQuestByName(string instanceId, string questName, ulong preferUid = 0)
    {
        ulong[] found = QuestMachine.Instance.FindQuests(questName, true);
        if (found == null || found.Length == 0) return null;

        Quest best = ChooseMostRecent(found, preferUid);
        if (best != null)
        {
            _cliInst2Uid[instanceId] = best.UID;
            _cliUid2Inst[best.UID]   = instanceId;
        }
        return best;
    }

    private static Quest ChooseMostRecent(ulong[] uids, ulong preferUid = 0)
    {
        Quest best = null, fallbackActive = null, fallbackAny = null;
        for (int i = 0; i < uids.Length; i++)
        {
            Quest cand = QuestMachine.Instance.GetQuest(uids[i]);
            if (cand == null) continue;

            if (preferUid != 0 && cand.UID == preferUid)
                return cand;

            if (!cand.QuestComplete && !cand.QuestTombstoned &&
                (best == null || cand.QuestStartTime.ToSeconds() > best.QuestStartTime.ToSeconds()))
                best = cand;

            if (!cand.QuestTombstoned &&
                (fallbackActive == null || cand.QuestStartTime.ToSeconds() > fallbackActive.QuestStartTime.ToSeconds()))
                fallbackActive = cand;

            if (fallbackAny == null || cand.QuestStartTime.ToSeconds() > fallbackAny.QuestStartTime.ToSeconds())
                fallbackAny = cand;
        }
        return best ?? fallbackActive ?? fallbackAny;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GetItem replication helpers
    // ─────────────────────────────────────────────────────────────────────────────
    public static bool ShouldRunGetItem(ulong questUid)
    {
        // Suppress GetItem only while a quest is being reconstructed from a packet.
        // Its exact task/action state and initial granted items are restored immediately
        // afterwards. Once reconstruction ends, every participating player must be able
        // to execute later GetItem progress and reward actions. Quest taker/sharer
        // identity must never control who is allowed to receive a quest reward.
        return !IsInRemoteParseWindow(questUid);
    }

    private static bool TryGetQuestLink(DaggerfallUnityItem it, out ulong questUid, out string symbolName)
    {
        questUid = 0;
        symbolName = null;
        if (it == null) return false;
        try
        {
            var t = it.GetType();
            // IsQuestItem
            var pIsQuest = t.GetProperty("IsQuestItem");
            if (pIsQuest != null && pIsQuest.PropertyType == typeof(bool))
            {
                bool isQuest = (bool)pIsQuest.GetValue(it, null);
                if (!isQuest) return false;
            }
            // QuestUID / questUID
            var pUid = t.GetProperty("QuestUID");
            if (pUid != null)
                questUid = (ulong)pUid.GetValue(it, null);
            else
            {
                var fUid = t.GetField("questUID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fUid != null) questUid = (ulong)fUid.GetValue(it);
            }
            // QuestItemSymbol (Symbol) / questItemSymbol
            object symObj = null;
            var pSym = t.GetProperty("QuestItemSymbol");
            if (pSym != null) symObj = pSym.GetValue(it, null);
            if (symObj == null)
            {
                var fSym = t.GetField("questItemSymbol", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fSym != null) symObj = fSym.GetValue(it);
            }
            if (symObj != null)
            {
                var pName = symObj.GetType().GetProperty("Name");
                if (pName != null) symbolName = pName.GetValue(symObj, null) as string;
            }
            return questUid != 0 && !string.IsNullOrEmpty(symbolName);
        }
        catch { return false; }
    }

    private static bool IsInventoryEntryForQuestItem(Quest q, string symbolName, DaggerfallUnityItem inventoryItem)
    {
        if (q == null || inventoryItem == null || string.IsNullOrEmpty(symbolName))
            return false;

        try
        {
            // Strict match only. The earlier group+index fallback is unsafe for books:
            // any unrelated book-like item can share the same generic group/index and
            // make SetQuestItemInventory() think the Rare Book is already present.
            // That caused the remote side to receive the pickup popup but skip adding
            // the actual quest item, so no [QuestItemPickupDbg] add log appeared.
            ulong uid;
            string sym;
            if (TryGetQuestLink(inventoryItem, out uid, out sym))
                return uid == q.UID && string.Equals(sym, symbolName, StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        try
        {
            // Keep only the exact original quest resource instance as a fallback.
            // Do not match by ItemGroup/GroupIndex here.
            Item questItem = q.GetItem(new Symbol(symbolName));
            DaggerfallUnityItem proto = questItem != null ? questItem.DaggerfallUnityItem : null;

            // Exact-reference fallback is only valid while the prototype is still a
            // quest item. GivePc intentionally makes its reward prototype permanent.
            // Treating that permanent object as a quest item lets a later/duplicate
            // quest-end cleanup delete the reward after the player takes it.
            if (proto != null && proto.IsQuestItem && object.ReferenceEquals(inventoryItem, proto))
                return true;
        }
        catch { }

        return false;
    }


    private static bool ShouldInferPickedUpQuestItemInInventory(Quest q, ItemDTO dto)
    {
        if (q == null || string.IsNullOrEmpty(dto.symbol))
            return false;

        if (dto.inPlayerInventory)
            return true;

        if (!HasPhysicalPickupActionForItem(q, dto.symbol) &&
            !HasTotingTaskForItem(q, dto.symbol) &&
            !HasFoeLootAssignmentForItem(q, dto.symbol))
            return false;

        // If this item was already turned in through a toting task, do not resurrect it
        // from stale clicked/hidden state packets. This protects turn-in/removal.
        if (HasTriggeredTotingTaskForItem(q, dto.symbol))
            return false;

        // Important MP fix: QuestResourceBehaviour.DoClick() always calls
        // targetResource.SetPlayerClicked() and then TransferWorldItemToPlayer() for Item
        // resources. So a clicked quest Item is a real pickup, even if DFU did not leave
        // an inventory link that IsQuestItemInPlayerInventory() can detect this frame.
        //
        // Do NOT infer pickup from isHidden alone. Placed dungeon quest items can be
        // hidden/not instantiated while the site is loading, before the player has ever
        // clicked them. Treating hidden alone as pickup caused the Rare Book popup to
        // replay on load and could hide/grant the book before the real pickup.
        return dto.hasPlayerClicked;
    }

    private static HashSet<string> GetFoeLootItemSymbols(Quest q)
    {
        if (q == null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> cached;
        if (_foeLootItemSymbolsByQuest.TryGetValue(q, out cached))
            return cached;

        cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "GiveItem",
                            StringComparison.Ordinal))
                        continue;

                    FieldInfo itemField = action.GetType().GetField(
                        "itemSymbol",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);
                    FieldInfo targetField = action.GetType().GetField(
                        "targetSymbol",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);
                    if (itemField == null || targetField == null)
                        continue;

                    string itemSymbol =
                        GetSymbolName(itemField.GetValue(action));
                    string targetSymbol =
                        GetSymbolName(targetField.GetValue(action));
                    if (string.IsNullOrEmpty(itemSymbol) ||
                        string.IsNullOrEmpty(targetSymbol))
                        continue;

                    QuestResource target =
                        q.GetResource(new Symbol(targetSymbol));
                    if (target is Foe)
                        cached.Add(itemSymbol);
                }
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][FoeLoot] Could not scan GiveItem-to-Foe " +
                    $"actions uid={q.UID} quest='{q.QuestName}': {ex.Message}");
            }
        }

        _foeLootItemSymbolsByQuest[q] = cached;

        if (Debug.isDebugBuild && cached.Count > 0)
        {
            Debug.Log(
                $"[QuestNetSync][FoeLoot] Corpse-loot quest item symbols " +
                $"uid={q.UID} quest='{q.QuestName}': " +
                string.Join(",", cached.OrderBy(x => x).ToArray()));
        }

        return cached;
    }

    private static bool HasFoeLootAssignmentForItem(
        Quest q,
        string symbolName)
    {
        return q != null &&
               !string.IsNullOrEmpty(symbolName) &&
               GetFoeLootItemSymbols(q).Contains(symbolName);
    }

    private static DaggerfallUnityItem FindQuestInventoryItem(
        Quest q,
        string symbolName)
    {
        try
        {
            var pe =
                GameManager.Instance != null
                    ? GameManager.Instance.PlayerEntity
                    : null;
            if (q == null ||
                pe == null ||
                pe.Items == null ||
                string.IsNullOrEmpty(symbolName))
                return null;

            for (int i = 0; i < pe.Items.Count; i++)
            {
                DaggerfallUnityItem item = pe.Items.GetItem(i);
                if (item == null)
                    continue;

                if (IsInventoryEntryForQuestItem(
                        q,
                        symbolName,
                        item))
                    return item;
            }

            Item questItem = q.GetItem(new Symbol(symbolName));
            if (questItem != null &&
                questItem.DaggerfallUnityItem != null &&
                pe.Items.Contains(questItem.DaggerfallUnityItem))
                return questItem.DaggerfallUnityItem;
        }
        catch { }

        return null;
    }

    private static bool IsActiveFoeLootQuest(Quest q)
    {
        return q != null &&
               !q.QuestComplete &&
               !q.QuestTombstoned &&
               !IsQuestSharingBlacklisted(q);
    }

    private static bool HasSameQuestLootItemUid(
        DaggerfallUnityItem first,
        DaggerfallUnityItem second)
    {
        if (first == null || second == null)
            return false;

        if (object.ReferenceEquals(first, second))
            return true;

        try
        {
            return first.UID != 0UL && first.UID == second.UID;
        }
        catch { return false; }
    }

    private static bool TryResolveNewLocalFoeLootItem(
        DaggerfallUnityItem physicalItem,
        ulong[] activeQuestUids,
        out Quest matchedQuest,
        out string matchedSymbol,
        out bool needsRelink,
        out string matchKind)
    {
        matchedQuest = null;
        matchedSymbol = null;
        needsRelink = false;
        matchKind = string.Empty;

        if (physicalItem == null || QuestMachine.Instance == null)
            return false;

        // A corpse quest item normally retains its local QuestUID + symbol while DFU
        // transfers it into PlayerEntity.Items. That pair is the only authoritative
        // identity. Resolve it directly before looking at any other active quest.
        //
        // The old code scanned active quests first and tested a loose physical match
        // for each one. Quest message IDs are local to a quest, so S0000018 _letter_
        // (message 1020) could match S0000009 note (also message 1020) and be relinked
        // to the wrong quest before the scanner ever reached S0000018.
        ulong linkedQuestUid;
        string linkedSymbol;
        if (TryGetQuestLink(
                physicalItem,
                out linkedQuestUid,
                out linkedSymbol))
        {
            Quest linkedQuest =
                QuestMachine.Instance.GetQuest(linkedQuestUid);

            if (IsActiveFoeLootQuest(linkedQuest) &&
                HasFoeLootAssignmentForItem(
                    linkedQuest,
                    linkedSymbol))
            {
                matchedQuest = linkedQuest;
                matchedSymbol = linkedSymbol;
                matchKind = "linked";
                return true;
            }

            // Never reinterpret an item that already has a complete quest link. If
            // that quest is unavailable or the symbol is not foe loot, treating the
            // item's generic template/message as another quest's resource is unsafe.
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][FoeLootEdge] Ignored newly-added item with " +
                    $"non-foe-loot quest link uid={linkedQuestUid} " +
                    $"item='{linkedSymbol}' inventoryUid={physicalItem.UID}");
            }
            return false;
        }

        Quest exactQuest = null;
        string exactSymbol = null;
        int exactMatches = 0;

        Quest looseQuest = null;
        string looseSymbol = null;
        int looseMatches = 0;

        // Legacy/network-rebuilt corpse items can occasionally arrive without their
        // quest link. Prefer one exact object/UID match. Only when none exists may the
        // old physical-field comparison be used, and then only if it is unambiguous
        // across every active quest.
        for (int qIndex = 0;
             qIndex < activeQuestUids.Length;
             qIndex++)
        {
            Quest q = QuestMachine.Instance.GetQuest(
                activeQuestUids[qIndex]);
            if (!IsActiveFoeLootQuest(q))
                continue;

            foreach (string symbol in GetFoeLootItemSymbols(q))
            {
                Item questItem = q.GetItem(new Symbol(symbol));
                DaggerfallUnityItem prototype =
                    questItem != null
                        ? questItem.DaggerfallUnityItem
                        : null;
                if (prototype == null)
                    continue;

                if (HasSameQuestLootItemUid(
                        physicalItem,
                        prototype))
                {
                    exactMatches++;
                    if (exactMatches == 1)
                    {
                        exactQuest = q;
                        exactSymbol = symbol;
                    }
                    continue;
                }

                if (LooksLikeSameSourceQuestLootItem(
                        physicalItem,
                        prototype))
                {
                    looseMatches++;
                    if (looseMatches == 1)
                    {
                        looseQuest = q;
                        looseSymbol = symbol;
                    }
                }
            }
        }

        if (exactMatches == 1)
        {
            matchedQuest = exactQuest;
            matchedSymbol = exactSymbol;
            needsRelink = true;
            matchKind = "exact-uid";
            return true;
        }

        if (exactMatches == 0 && looseMatches == 1)
        {
            matchedQuest = looseQuest;
            matchedSymbol = looseSymbol;
            needsRelink = true;
            matchKind = "unique-fallback";
            return true;
        }

        if (Debug.isDebugBuild &&
            (exactMatches > 1 || looseMatches > 1))
        {
            Debug.LogWarning(
                $"[QuestNetSync][FoeLootEdge] Refused ambiguous unlinked " +
                $"inventory item inventoryUid={physicalItem.UID} " +
                $"exactMatches={exactMatches} looseMatches={looseMatches}");
        }

        return false;
    }

    private static void DetectNewLocalFoeLootInventoryItem()
    {
        try
        {
            var player = GameManager.Instance != null
                ? GameManager.Instance.PlayerEntity
                : null;
            if (player == null || player.Items == null ||
                QuestMachine.Instance == null)
                return;

            HashSet<ulong> current = new HashSet<ulong>();
            List<DaggerfallUnityItem> added =
                new List<DaggerfallUnityItem>();

            for (int i = 0; i < player.Items.Count; i++)
            {
                DaggerfallUnityItem item = player.Items.GetItem(i);
                if (item == null)
                    continue;

                ulong uid;
                try { uid = item.UID; }
                catch { continue; }
                if (uid == 0UL)
                    continue;

                current.Add(uid);
                if (_localInventoryUidBaselineReady &&
                    !_seenLocalInventoryUids.Contains(uid))
                    added.Add(item);
            }

            _seenLocalInventoryUids.Clear();
            foreach (ulong uid in current)
                _seenLocalInventoryUids.Add(uid);

            if (!_localInventoryUidBaselineReady)
            {
                _localInventoryUidBaselineReady = true;
                return;
            }

            ulong[] active = QuestMachine.Instance.GetAllActiveQuests()
                ?? new ulong[0];

            for (int i = 0; i < added.Count; i++)
            {
                DaggerfallUnityItem physicalItem = added[i];
                Quest matchedQuest;
                string matchedSymbol;
                bool needsRelink;
                string matchKind;
                if (!TryResolveNewLocalFoeLootItem(
                        physicalItem,
                        active,
                        out matchedQuest,
                        out matchedSymbol,
                        out needsRelink,
                        out matchKind))
                    continue;

                string key = MakeQuestItemInventoryKey(
                    matchedQuest.UID,
                    matchedSymbol);

                if (_remoteFoeLootInventoryEchoGuards.Remove(key))
                    continue;

                if (!_reportedLocalFoeLootKeys.Add(key))
                    continue;

                if (needsRelink)
                {
                    try
                    {
                        if (IsQuestItemPermanenceLatched(matchedQuest, matchedSymbol))
                            physicalItem.MakePermanent();
                        else
                            physicalItem.LinkQuestItem(
                                matchedQuest.UID,
                                new Symbol(matchedSymbol));
                    }
                    catch { }
                }

                TrackQuestInventoryObject(matchedQuest, matchedSymbol, physicalItem);

                Debug.Log(
                    $"[QuestNetSync][FoeLootEdge] Actual local inventory add " +
                    $"uid={matchedQuest.UID} quest='{matchedQuest.QuestName}' " +
                    $"item='{matchedSymbol}' inventoryUid={physicalItem.UID} " +
                    $"match={matchKind}");

                ReportLocalQuestItemInventoryChanged(
                    matchedQuest.UID,
                    matchedSymbol,
                    true,
                    physicalItem);
            }
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning("[QuestNetSync][FoeLootEdge] " + ex.Message);
        }
    }

    private static void ReportNewLocalFoeLootInventoryPickups(
        Quest q,
        Dictionary<string, ItemState> before,
        Dictionary<string, ItemState> after)
    {
        if (q == null || before == null || after == null)
            return;

        HashSet<string> foeLootSymbols =
            GetFoeLootItemSymbols(q);
        if (foeLootSymbols.Count == 0)
            return;

        foreach (string symbolName in foeLootSymbols)
        {
            ItemState oldState;
            ItemState newState;
            bool hadBefore =
                before.TryGetValue(symbolName, out oldState) &&
                oldState.inPlayerInventory;
            bool hasNow =
                after.TryGetValue(symbolName, out newState) &&
                newState.inPlayerInventory;

            if (hadBefore || !hasNow)
                continue;

            string key =
                MakeQuestItemInventoryKey(q.UID, symbolName);

            if (_reportedLocalFoeLootKeys.Contains(key))
                continue;

            // A remote explicit grant is already authoritative. Do not send it back
            // to the server as though this receiver had looted the corpse.
            if (_remoteFoeLootInventoryEchoGuards.Remove(key))
            {
                if (Debug.isDebugBuild)
                {
                    Debug.Log(
                        $"[QuestNetSync][FoeLoot] Suppressed remote pickup echo " +
                        $"uid={q.UID} quest='{q.QuestName}' " +
                        $"symbol='{symbolName}'");
                }
                continue;
            }

            DaggerfallUnityItem sourceItem =
                FindQuestInventoryItem(q, symbolName);
            if (sourceItem == null)
            {
                if (Debug.isDebugBuild)
                {
                    Debug.LogWarning(
                        $"[QuestNetSync][FoeLoot] Inventory edge had no linked " +
                        $"source item uid={q.UID} quest='{q.QuestName}' " +
                        $"symbol='{symbolName}'");
                }
                continue;
            }

            _reportedLocalFoeLootKeys.Add(key);

            Debug.Log(
                $"[QuestNetSync][FoeLoot] Local corpse quest item pickup " +
                $"uid={q.UID} quest='{q.QuestName}' " +
                $"symbol='{symbolName}'");

            ReportLocalQuestItemInventoryChanged(
                q.UID,
                symbolName,
                true,
                sourceItem);
        }
    }

    private static bool HasPhysicalPickupActionForItem(Quest q, string symbolName)
    {
        bool triggered;

        // Only a real placed/clicked world item should be inferred into inventory
        // from hasPlayerClicked. Generic HaveItem/Toting checks can also exist for
        // reward/delivery resources and must not resurrect those as quest items.
        if (FindQuestActionForItem(q, symbolName, "ClickedItem", "itemSymbol", out triggered))
            return true;

        if (FindQuestActionForItem(q, symbolName, "ItemUsedDo", "itemSymbol", out triggered))
            return true;

        return false;
    }

    private static bool HasPickupActionForItem(Quest q, string symbolName)
    {
        bool triggered;

        if (FindQuestActionForItem(q, symbolName, "TotingItemAndClickedNpc", "itemSymbol", out triggered))
            return true;

        if (FindQuestActionForItem(q, symbolName, "HaveItem", "targetItem", out triggered))
            return true;

        if (FindQuestActionForItem(q, symbolName, "ClickedItem", "itemSymbol", out triggered))
            return true;

        if (FindQuestActionForItem(q, symbolName, "ItemUsedDo", "itemSymbol", out triggered))
            return true;

        if (HasFoeLootAssignmentForItem(q, symbolName))
            return true;

        return false;
    }

    private static bool HasTriggeredClickedItemTaskForItem(Quest q, string symbolName)
    {
        if (q == null || string.IsNullOrEmpty(symbolName))
            return false;

        // A placed world Item can be physically picked up and then immediately removed
        // from inventory by the same quest task (for example: clicked item -> prompt ->
        // take item from pc). In that state both inventory ownership and the transient
        // HasPlayerClicked flag can be false again, but the owning ClickedItem task is
        // still the durable quest-progress proof that this physical world item was used.
        //
        // Scan every ClickedItem action for this symbol and return true when ANY owning
        // task is currently triggered. If a quest explicitly clears that task later,
        // this naturally becomes false again and does not permanently blacklist the item.
        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                bool taskContainsItem = false;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "ClickedItem",
                            StringComparison.Ordinal))
                        continue;

                    FieldInfo itemField = action.GetType().GetField(
                        "itemSymbol",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);
                    if (itemField == null)
                        continue;

                    string actionItem =
                        GetSymbolName(itemField.GetValue(action));
                    if (!string.Equals(
                            actionItem,
                            symbolName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    taskContainsItem = true;
                    break;
                }

                if (!taskContainsItem)
                    continue;

                try
                {
                    if (task.IsTriggered)
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private static bool HasTotingTaskForItem(Quest q, string symbolName)
    {
        bool triggered;
        return FindQuestActionForItem(q, symbolName, "TotingItemAndClickedNpc", "itemSymbol", out triggered);
    }

    private static bool HasTriggeredTotingTaskForItem(Quest q, string symbolName)
    {
        if (q == null || string.IsNullOrEmpty(symbolName))
            return false;

        // An item can be accepted by more than one TotingItemAndClickedNpc task.
        // S0000002 is the important example: the same first letter can be handed
        // either to Lord Castellian (_S.02_) or to King Eadwyre (_S.05_).
        //
        // The old helper stopped at the first matching action. After handing the
        // letter to Eadwyre, _S.05_ was triggered but the earlier _S.02_ was not, so
        // this incorrectly returned false. A later passive ItemDTO then inferred the
        // already-consumed letter as still carried and added it back on remote clients.
        //
        // Scan every matching toting action and return true when ANY owning task has
        // triggered. This remains generic and does not hardcode the quest or symbols.
        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                bool taskContainsItem = false;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(
                            action.GetType().Name,
                            "TotingItemAndClickedNpc",
                            StringComparison.Ordinal))
                        continue;

                    FieldInfo itemField = action.GetType().GetField(
                        "itemSymbol",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);
                    if (itemField == null)
                        continue;

                    string actionItem =
                        GetSymbolName(itemField.GetValue(action));
                    if (!string.Equals(
                            actionItem,
                            symbolName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    taskContainsItem = true;
                    break;
                }

                if (!taskContainsItem)
                    continue;

                try
                {
                    if (task.IsTriggered)
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private static bool FindQuestActionForItem(Quest q, string symbolName, string actionTypeName, string actionItemFieldName, out bool triggered)
    {
        triggered = false;
        if (q == null || string.IsNullOrEmpty(symbolName) || string.IsNullOrEmpty(actionTypeName) || string.IsNullOrEmpty(actionItemFieldName))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null || action.GetType().Name != actionTypeName)
                        continue;

                    FieldInfo fItem = action.GetType().GetField(actionItemFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fItem == null)
                        continue;

                    string actionItem = GetSymbolName(fItem.GetValue(action));
                    if (!string.Equals(actionItem, symbolName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { triggered = task.IsTriggered; }
                    catch { triggered = false; }
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static List<DaggerfallWorkshop.Game.Questing.Task> GetQuestTasksForActionScan(Quest q)
    {
        List<DaggerfallWorkshop.Game.Questing.Task> result = new List<DaggerfallWorkshop.Game.Questing.Task>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (q == null)
            return result;

        // Scan all instance fields on Quest. DFU versions have used private task
        // collections, and q.GetTaskStates() can miss dormant tasks before they run.
        try
        {
            FieldInfo[] fields = typeof(Quest).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
                CollectTasksFromObject(fields[i].GetValue(q), result, seen);
        }
        catch { }

        // Fallback for unusual builds where tasks are only reachable by active states.
        try
        {
            Quest.TaskState[] states = q.GetTaskStates();
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].symbol == null)
                        continue;
                    if (!seen.Add(states[i].symbol.Name))
                        continue;

                    DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(states[i].symbol);
                    if (task != null)
                        result.Add(task);
                }
            }
        }
        catch { }

        return result;
    }

    private static void CollectTasksFromObject(object obj, List<DaggerfallWorkshop.Game.Questing.Task> result, HashSet<string> seen)
    {
        if (obj == null || result == null || seen == null)
            return;

        DaggerfallWorkshop.Game.Questing.Task direct = obj as DaggerfallWorkshop.Game.Questing.Task;
        if (direct != null)
        {
            string key = direct.Symbol != null ? direct.Symbol.Name : direct.GetHashCode().ToString();
            if (seen.Add(key))
                result.Add(direct);
            return;
        }

        System.Collections.IDictionary dict = obj as System.Collections.IDictionary;
        if (dict != null)
        {
            foreach (object value in dict.Values)
                CollectTasksFromObject(value, result, seen);
            return;
        }

        System.Collections.IEnumerable enumerable = obj as System.Collections.IEnumerable;
        if (enumerable != null && !(obj is string))
        {
            foreach (object value in enumerable)
                CollectTasksFromObject(value, result, seen);
        }
    }

    private static string GetSymbolName(object symObj)
    {
        if (symObj == null)
            return null;

        try
        {
            Symbol sym = symObj as Symbol;
            if (sym != null)
                return sym.Name;
        }
        catch { }

        try
        {
            PropertyInfo pName = symObj.GetType().GetProperty("Name");
            if (pName != null)
                return pName.GetValue(symObj, null) as string;
        }
        catch { }

        return null;
    }

    private static bool IsQuestItemInPlayerInventory(Quest q, string symbolName)
    {
        try
        {
            var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
            if (q == null || pe == null || pe.Items == null || string.IsNullOrEmpty(symbolName))
                return false;

            Item questItem = q.GetItem(new Symbol(symbolName));
            if (questItem != null)
            {
                // This is the same check used by vanilla TotingItemAndClickedNpc.
                // Some placed quest items do not reliably expose the reflected quest-link
                // fields after pickup, but the quest Item resource itself is still what
                // ItemCollection.Contains() understands.
                try
                {
                    if (pe.Items.Contains(questItem))
                        return true;
                }
                catch { }

                try
                {
                    if (questItem.DaggerfallUnityItem != null && pe.Items.Contains(questItem.DaggerfallUnityItem))
                        return true;
                }
                catch { }
            }

            // Fallback for generated/GetItem quest items that carry explicit quest links.
            for (int i = 0; i < pe.Items.Count; i++)
            {
                DaggerfallUnityItem it = pe.Items.GetItem(i);
                if (it == null) continue;

                if (IsInventoryEntryForQuestItem(q, symbolName, it))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static int NormalizeQuestItemStackCountForDto(DaggerfallUnityItem item, int dtoStackCount)
    {
        if (item == null)
            return Math.Max(1, dtoStackCount);

        try
        {
            if (item.IsOfTemplate(ItemGroups.Currency, (int)Currency.Gold_pieces))
                return Math.Max(0, dtoStackCount);
        }
        catch { }

        return Math.Max(1, dtoStackCount);
    }

    private static int NormalizeQuestItemStackCount(DaggerfallUnityItem item)
    {
        if (item == null)
            return 1;

        try
        {
            if (item.IsOfTemplate(ItemGroups.Currency, (int)Currency.Gold_pieces))
                return Math.Max(0, item.stackCount);
        }
        catch { }

        // Non-currency inventory items must have at least one stack or they can vanish
        // from the inventory UI after network reconstruction.
        return Math.Max(1, item.stackCount);
    }

    private static DaggerfallUnityItem CloneQuestItemForInventory(Quest q, Item questItem, string symbolName)
    {
        return CloneQuestItemForInventory(q, questItem, symbolName, string.Empty);
    }

    private static DaggerfallUnityItem CloneQuestItemForInventory(Quest q, Item questItem, string symbolName, string itemDataJson)
    {
        if (q == null)
            return null;

        DaggerfallUnityItem clone = null;

        // Best source: the exact item data captured from the player who physically
        // picked up the placed quest object. Remote clients can have an incomplete
        // or stale quest resource item at the moment the click RPC arrives.
        try
        {
            ItemData_v1 data;
            if (!string.IsNullOrEmpty(itemDataJson) && FromJson<ItemData_v1>(itemDataJson, out data))
                clone = new DaggerfallUnityItem(data);
        }
        catch { clone = null; }

        if (clone == null && questItem != null && questItem.DaggerfallUnityItem != null)
        {
            DaggerfallUnityItem source = questItem.DaggerfallUnityItem;
            try
            {
                clone = new DaggerfallUnityItem(source.GetSaveData());
            }
            catch
            {
                clone = source;
            }
        }

        if (clone == null)
            return null;

        clone.stackCount = NormalizeQuestItemStackCount(clone);

        try
        {
            if (IsQuestItemPermanenceLatched(q, symbolName))
            {
                clone.MakePermanent();
            }
            else
            {
                Symbol sym = questItem != null && questItem.Symbol != null ? questItem.Symbol.Clone() : new Symbol(symbolName);
                clone.LinkQuestItem(q.UID, sym);
            }
        }
        catch { }

        return clone;
    }

    private static bool AllowQuestInventoryRepair(Quest q, string symbolName)
    {
        if (q == null || string.IsNullOrEmpty(symbolName))
            return false;

        // This inventory repair path is only for real quest-object pickups and
        // temporary toting-item handoff replay. Reward loot can also carry a quest
        // UID/symbol, but taking a reward from the reward window must stay local.
        // The recent-click allowance covers letter-style physical pickups that are
        // proven by ReportLocalItemClicked/RpcItemClicked but not found by action scan.
        return HasPhysicalPickupActionForItem(q, symbolName) ||
               IsQuestItemClickPickupAllowed(q.UID, symbolName) ||
               HasTotingTaskForItem(q, symbolName) ||
               HasFoeLootAssignmentForItem(q, symbolName);
    }

    private static ulong MakeFreshQuestNetSyncItemUid()
    {
        unchecked
        {
            _questNetSyncGeneratedItemUid++;
            return _questNetSyncGeneratedItemUid ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        }
    }

    private static void TryAssignFreshItemUid(DaggerfallUnityItem item)
    {
        if (item == null)
            return;

        try
        {
            ulong fresh = MakeFreshQuestNetSyncItemUid();
            Type t = item.GetType();

            FieldInfo f = t.GetField("uid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
            {
                if (f.FieldType == typeof(ulong)) { f.SetValue(item, fresh); return; }
                if (f.FieldType == typeof(long)) { f.SetValue(item, (long)fresh); return; }
                if (f.FieldType == typeof(uint)) { f.SetValue(item, (uint)(fresh & 0xffffffff)); return; }
                if (f.FieldType == typeof(int)) { f.SetValue(item, (int)(fresh & 0x7fffffff)); return; }
            }

            PropertyInfo p = t.GetProperty("UID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null && p.CanWrite)
            {
                if (p.PropertyType == typeof(ulong)) { p.SetValue(item, fresh, null); return; }
                if (p.PropertyType == typeof(long)) { p.SetValue(item, (long)fresh, null); return; }
                if (p.PropertyType == typeof(uint)) { p.SetValue(item, (uint)(fresh & 0xffffffff), null); return; }
                if (p.PropertyType == typeof(int)) { p.SetValue(item, (int)(fresh & 0x7fffffff), null); return; }
            }
        }
        catch { }
    }

    // Final quest-end MakePermanent barrier.
    //
    // QNS deliberately does not replay arbitrary action tasks from EndPacket/CmdClientEnded
    // because they can contain GivePc, EndQuest, StartQuest, or reputation side effects.
    // A consequence is that a task can be authoritatively TRUE at quest end while its
    // MakePermanent action has not received another normal QuestMachine tick. If cleanup
    // runs first, a physically picked reward remains green or is treated as disposable.
    //
    // This helper performs only the idempotent MakePermanent side effect, and only from
    // tasks that are actually triggered in the authoritative final state. No quest names,
    // item names, delays, or reward heuristics are involved.
    private static int ApplyTriggeredMakePermanentActionsForEndingQuest(
        Quest q,
        TaskStateDTO[] authoritativeTasks,
        string reason)
    {
        if (q == null)
            return 0;

        HashSet<string> triggeredTasks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (authoritativeTasks != null)
        {
            for (int i = 0; i < authoritativeTasks.Length; i++)
            {
                if (authoritativeTasks[i].set &&
                    !string.IsNullOrEmpty(authoritativeTasks[i].symbol))
                {
                    triggeredTasks.Add(authoritativeTasks[i].symbol);
                }
            }
        }
        else
        {
            Quest.TaskState[] states =
                q.GetTaskStates() ?? new Quest.TaskState[0];
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].set && states[i].symbol != null &&
                    !string.IsNullOrEmpty(states[i].symbol.Name))
                {
                    triggeredTasks.Add(states[i].symbol.Name);
                }
            }
        }

        if (triggeredTasks.Count == 0)
            return 0;

        HashSet<string> appliedItems =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string taskSymbol in triggeredTasks)
        {
            DaggerfallWorkshop.Game.Questing.Task task =
                q.GetTask(new Symbol(taskSymbol));
            if (task == null || task.Actions == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null ||
                    !string.Equals(
                        action.GetType().Name,
                        "MakePermanent",
                        StringComparison.Ordinal))
                    continue;

                string itemSymbol = string.Empty;
                try
                {
                    object saveData = action.GetSaveData();
                    if (saveData != null)
                    {
                        FieldInfo targetField = saveData.GetType().GetField(
                            "target",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic);
                        if (targetField != null)
                            itemSymbol = GetSymbolName(targetField.GetValue(saveData));
                    }

                    if (string.IsNullOrEmpty(itemSymbol))
                    {
                        FieldInfo targetField = action.GetType().GetField(
                            "target",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic);
                        if (targetField != null)
                            itemSymbol = GetSymbolName(targetField.GetValue(action));
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(itemSymbol) ||
                    !appliedItems.Add(itemSymbol))
                    continue;

                ApplyQuestItemMadePermanent(
                    q,
                    itemSymbol,
                    "end-barrier:" + (reason ?? string.Empty) + ":" + taskSymbol);
            }
        }

        if (appliedItems.Count > 0)
        {
            Debug.Log(
                $"[QuestNetSync][ItemPermanentEndBarrier] Applied triggered final permanence " +
                $"uid={q.UID} quest='{q.QuestName}' items={string.Join(",", appliedItems.ToArray())} " +
                $"reason='{reason}'");
        }

        return appliedItems.Count;
    }

    // Exact MakePermanent action replication. Passive ItemDTO state is useful as a
    // durable backstop, but it can arrive too late for quests that make a physically
    // picked world item permanent and then end immediately. The action itself reports
    // the transition so every participant permanentizes the carried copy before cleanup.
    public static void ReportLocalItemMadePermanent(ulong questUID, string itemSymbol)
    {
        if (questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        // Always repair locally, including pure SP. Vanilla MakePermanent has already
        // run by the time this method is called. If the inventory contains a distinct
        // quest-linked copy, make that copy permanent too.
        ApplyQuestItemMadePermanent(q, itemSymbol, "local-make-permanent-action");

        QuestNetSync inst = LocalInstance;
        if (inst == null || !inst.isLocalPlayer || !inst.isClient)
            return;

        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        inst.CmdItemMadePermanent(questUID, itemSymbol, _localNetId);
    }

    private static bool HasMakePermanentActionForItem(Quest q, string itemSymbol)
    {
        if (q == null || string.IsNullOrEmpty(itemSymbol))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks = GetQuestTasksForActionScan(q);
            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task = tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null ||
                        !string.Equals(action.GetType().Name, "MakePermanent", StringComparison.Ordinal))
                        continue;

                    if (QuestActionReferencesSymbol(action, itemSymbol))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    [Command]
    private void CmdItemMadePermanent(ulong questUID, string itemSymbol, uint sourceNetId)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;
        if (!isServer || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;
        if (IsQuestSharingBlacklistedUid(questUID))
            return;

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null || !HasMakePermanentActionForItem(q, itemSymbol))
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning($"[QuestNetSync][ItemPermanent] Rejected unvalidated permanent event uid={questUID} item='{itemSymbol}' source={sourceNetId}");
            return;
        }

        // The listen-host has its own local inventory/quest copy and must apply a pure
        // client's MakePermanent event too. This call is idempotent for host-originated
        // events.
        ApplyQuestItemMadePermanent(q, itemSymbol, "server-make-permanent-action");

        NetworkConnection sourceConnection = connectionToClient;
        int recipients = 0;
        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection recipient = entry.Value;
            if (recipient == null || recipient == sourceConnection)
                continue;

            NetworkIdentity identity = recipient.identity;
            if (identity == null)
                continue;

            QuestNetSync sync = identity.GetComponent<QuestNetSync>();
            if (sync == null)
                sync = identity.GetComponentInChildren<QuestNetSync>(true);
            if (sync == null || !sync.isServer)
                continue;

            sync.TargetItemMadePermanent(recipient, questUID, itemSymbol, sourceNetId);
            recipients++;
        }

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync][ItemPermanent] Fanout uid={questUID} item='{itemSymbol}' source={sourceNetId} recipients={recipients}");
    }

    [TargetRpc]
    private void TargetItemMadePermanent(NetworkConnection target, ulong questUID, string itemSymbol, uint sourceNetId)
    {
        if (!isClient || questUID == 0UL || string.IsNullOrEmpty(itemSymbol))
            return;

        Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (q == null)
            return;

        ApplyQuestItemMadePermanent(q, itemSymbol, "remote-make-permanent-action");
    }

    /// <summary>
    /// Applies DFU's MakePermanent state to the quest resource and to the actual
    /// inventory object currently representing it. This is deliberately monotonic.
    /// </summary>
    private static void ApplyQuestItemMadePermanent(Quest q, string symbolName, string reason)
    {
        if (q == null || string.IsNullOrEmpty(symbolName))
            return;

        string key = MakeQuestItemInventoryKey(q.UID, symbolName);

        // Latch FIRST. Any inventory reconstruction/rebind that happens after this point
        // must inherit permanence rather than restoring a green quest link.
        _permanentQuestItemKeys.Add(key);
        RemovePickedQuestItemKey(key);

        try
        {
            Item questItem = q.GetItem(new Symbol(symbolName));
            if (questItem == null)
                return;

            var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
            DaggerfallUnityItem carried = null;
            DaggerfallUnityItem tracked = null;

            // Same principle as the old working GivePc/GetItem reward fix: start with
            // the exact physical object that entered PlayerEntity.Items.
            if (_physicalQuestInventoryItemByKey.TryGetValue(key, out tracked) &&
                tracked != null && pe != null && pe.Items != null &&
                pe.Items.Contains(tracked))
            {
                carried = tracked;
            }

            // If QNS replaced the physical object after it was first tracked, prefer the
            // Questing.Item's current object when that exact instance is in inventory.
            if (carried == null && pe != null && pe.Items != null &&
                questItem.DaggerfallUnityItem != null &&
                pe.Items.Contains(questItem.DaggerfallUnityItem))
            {
                carried = questItem.DaggerfallUnityItem;
            }

            // Last safe fallback: locate an inventory item still carrying this exact
            // quest UID + symbol. Capture every duplicate while the link still exists;
            // only one physical object should represent one quest Item resource.
            List<DaggerfallUnityItem> linkedMatches = new List<DaggerfallUnityItem>();
            if (pe != null && pe.Items != null)
            {
                for (int i = 0; i < pe.Items.Count; i++)
                {
                    DaggerfallUnityItem candidate = pe.Items.GetItem(i);
                    if (candidate == null)
                        continue;

                    if (IsInventoryEntryForQuestItem(q, symbolName, candidate))
                    {
                        linkedMatches.Add(candidate);
                        if (carried == null)
                            carried = candidate;
                    }
                }
            }

            // Bind the virtual resource to the actual physical reward object WITHOUT
            // calling LinkQuestItem(). The permanence latch is already active.
            if (carried != null && !object.ReferenceEquals(questItem.DaggerfallUnityItem, carried))
            {
                FieldInfo itemField = typeof(Item).GetField(
                    "item", BindingFlags.Instance | BindingFlags.NonPublic);
                if (itemField != null)
                    itemField.SetValue(questItem, carried);
            }

            // Make the virtual resource permanent and then explicitly permanentize the
            // tracked/carried object itself. Also strip the quest link from any duplicate
            // linked copies that were created by an earlier MP repair, then remove those
            // duplicates so one quest pickup cannot leave multiple permanent rewards.
            questItem.MakePermanent();

            if (carried != null)
            {
                carried.MakePermanent();
                _physicalQuestInventoryItemByKey[key] = carried;
            }

            for (int i = 0; i < linkedMatches.Count; i++)
            {
                DaggerfallUnityItem candidate = linkedMatches[i];
                if (candidate == null)
                    continue;

                candidate.MakePermanent();
                if (carried != null && !object.ReferenceEquals(candidate, carried) &&
                    pe != null && pe.Items != null)
                {
                    pe.Items.RemoveItem(candidate);
                }
            }

            // If the tracked object survived but was temporarily detached from the
            // inventory by a reconstruction pass, restore that exact permanent object.
            if (carried != null && pe != null && pe.Items != null && !pe.Items.Contains(carried))
                pe.Items.AddItem(carried, ItemCollection.AddPosition.Front);

            Debug.Log(
                $"[QuestNetSync][ItemPermanentPhysical] Applied uid={q.UID} " +
                $"quest='{q.QuestName}' item='{symbolName}' " +
                $"tracked={(tracked != null)} carried={(carried != null)} " +
                $"carriedUid={(carried != null ? carried.UID : 0UL)} " +
                $"isQuestItemAfter={(carried != null && carried.IsQuestItem)} " +
                $"duplicates={linkedMatches.Count} reason='{reason}'");
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][ItemPermanent] Failed uid={(q != null ? q.UID : 0UL)} " +
                $"item='{symbolName}' reason='{reason}': {ex.Message}");
        }
    }

    private static void BindQuestItemResourceToInventoryItem(Quest q, string symbolName, DaggerfallUnityItem inventoryItem, string reason)
    {
        if (q == null || inventoryItem == null || string.IsNullOrEmpty(symbolName))
            return;

        TrackQuestInventoryObject(q, symbolName, inventoryItem);

        try
        {
            Item questItem = q.GetItem(new Symbol(symbolName));
            if (questItem == null)
                return;

            // Vanilla TotingItemAndClickedNpc/HaveItem style checks often work through
            // the Questing.Item resource's private DaggerfallUnityItem instance, not only
            // through the green QuestUID/QuestItemSymbol link on arbitrary inventory items.
            FieldInfo itemField = typeof(Item).GetField("item", BindingFlags.Instance | BindingFlags.NonPublic);
            if (itemField == null)
                return;

            if (IsQuestItemPermanenceLatched(q, symbolName))
            {
                // Do not undo MakePermanent just because a late ItemDTO/pickup repair
                // found or rebuilt the physical object. Bind it as the resource's current
                // item, but keep the actual inventory object white/permanent.
                inventoryItem.MakePermanent();
                itemField.SetValue(questItem, inventoryItem);
                if (!questItem.MadePermanent)
                    questItem.MakePermanent();

                if (Debug.isDebugBuild)
                {
                    Debug.Log(
                        $"[QuestNetSync][ItemPermanentPhysical] Blocked re-link uid={q.UID} " +
                        $"symbol='{symbolName}' reason={reason} itemUid={inventoryItem.UID}");
                }
                return;
            }

            inventoryItem.LinkQuestItem(
                q.UID,
                questItem.Symbol != null ? questItem.Symbol.Clone() : new Symbol(symbolName));
            itemField.SetValue(questItem, inventoryItem);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestItemPickupDbg] Bound quest Item resource to inventory copy uid={q.UID} symbol='{symbolName}' reason={reason} itemUid={inventoryItem.UID} msg={inventoryItem.message}");
        }
        catch (Exception ex)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning($"[QuestNetSync] BindQuestItemResourceToInventoryItem failed uid={(q != null ? q.UID : 0UL)} symbol='{symbolName}' reason={reason}: {ex.Message}");
        }
    }

    private static void SetQuestItemInventory(Quest q, string symbolName, bool shouldHave)
    {
        SetQuestItemInventory(q, symbolName, shouldHave, string.Empty);
    }

    private static void SetQuestItemInventory(Quest q, string symbolName, bool shouldHave, string itemDataJson)
    {
        if (q == null || string.IsNullOrEmpty(symbolName))
            return;

        var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
        if (pe == null || pe.Items == null)
            return;

        if (!AllowQuestInventoryRepair(q, symbolName))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync] Suppressed SetQuestItemInventory for non-repair quest item uid={q.UID} symbol='{symbolName}' shouldHave={shouldHave}");
            return;
        }

        if (shouldHave &&
            IsTotingQuestItemConsumed(
                q.UID,
                symbolName))
        {
            Debug.Log(
                $"[QuestNetSync][TotingConsumed] Blocked item resurrection " +
                $"uid={q.UID} item='{symbolName}'");
            return;
        }

        string protectKey = MakeQuestItemInventoryKey(q.UID, symbolName);
        if (shouldHave)
            ProtectPickedQuestItemKey(protectKey);
        else
            RemovePickedQuestItemKey(protectKey);

        Item questItem = q.GetItem(new Symbol(symbolName));

        // Once this symbol has crossed MakePermanent, inventory repair must never
        // LinkQuestItem() it again. Use the exact tracked physical object first; this
        // mirrors the old working GivePc/GetItem reward-object handling.
        if (shouldHave && questItem != null && IsQuestItemPermanenceLatched(q, symbolName))
        {
            DaggerfallUnityItem permanentItem = null;
            _physicalQuestInventoryItemByKey.TryGetValue(protectKey, out permanentItem);
            if (permanentItem == null)
                permanentItem = questItem.DaggerfallUnityItem;

            if (permanentItem != null)
            {
                permanentItem.MakePermanent();
                if (!pe.Items.Contains(permanentItem))
                    pe.Items.AddItem(permanentItem, ItemCollection.AddPosition.Front);
                TrackQuestInventoryObject(q, symbolName, permanentItem);
            }

            if (!questItem.MadePermanent)
                questItem.MakePermanent();

            RemovePickedQuestItemKey(protectKey);

            if (Debug.isDebugBuild)
                Debug.Log($"[QuestNetSync][ItemPermanentPhysical] Ignored quest re-link for permanent item uid={q.UID} symbol='{symbolName}' present={(permanentItem != null && pe.Items.Contains(permanentItem))} itemUid={(permanentItem != null ? permanentItem.UID : 0UL)}");
            return;
        }

        List<DaggerfallUnityItem> matching = new List<DaggerfallUnityItem>();
        for (int i = 0; i < pe.Items.Count; i++)
        {
            DaggerfallUnityItem it = pe.Items.GetItem(i);
            if (it == null) continue;

            if (IsInventoryEntryForQuestItem(q, symbolName, it))
                matching.Add(it);
        }

        if (!shouldHave)
        {
            for (int i = 0; i < matching.Count; i++)
                pe.Items.RemoveItem(matching[i]);
            return;
        }

        // Keep one copy only.
        for (int i = 1; i < matching.Count; i++)
            pe.Items.RemoveItem(matching[i]);

        if (matching.Count > 0)
        {
            Debug.Log($"[QuestItemPickupDbg] Quest item already present uid={q.UID} symbol='{symbolName}' matches={matching.Count}");

            // Do not just return because a green quest-linked item exists. For corpse-looted
            // quest items the inventory object can be correct visually, while the Questing.Item
            // resource still points at an old prototype. Bind the resource to the live inventory
            // copy so vanilla TotingItemAndClickedNpc can actually see it.
            TrackQuestInventoryObject(q, symbolName, matching[0]);
            BindQuestItemResourceToInventoryItem(q, symbolName, matching[0], "already-present");

            if (questItem != null)
            {
                QuestResource.ResourceSaveData_v1 rsd = questItem.GetResourceSaveData();
                rsd.hasPlayerClicked = true;
                rsd.isHidden = true;
                questItem.RestoreResourceSaveData(rsd);
                if (questItem.QuestResourceBehaviour != null)
                    questItem.QuestResourceBehaviour.gameObject.SetActive(false);
            }
            return;
        }

        if ((questItem == null || questItem.DaggerfallUnityItem == null) && string.IsNullOrEmpty(itemDataJson))
            return;

        DaggerfallUnityItem invCopy = CloneQuestItemForInventory(q, questItem, symbolName, itemDataJson);
        if (invCopy == null)
            return;

        TryAssignFreshItemUid(invCopy);
        pe.Items.AddItem(invCopy, ItemCollection.AddPosition.Front);
        TrackQuestInventoryObject(q, symbolName, invCopy);
        BindQuestItemResourceToInventoryItem(q, symbolName, invCopy, "added-copy");

        Debug.Log($"[QuestItemPickupDbg] Added quest item to inventory uid={q.UID} symbol='{symbolName}' group={(int)invCopy.ItemGroup} index={invCopy.GroupIndex} stack={invCopy.stackCount} json={!string.IsNullOrEmpty(itemDataJson)}");

        if (questItem != null)
        {
            QuestResource.ResourceSaveData_v1 addedRsd = questItem.GetResourceSaveData();
            addedRsd.hasPlayerClicked = true;
            addedRsd.isHidden = true;
            questItem.RestoreResourceSaveData(addedRsd);

            // Do not call SetPlayerClicked() here. This is inventory/state repair, not
            // a physical click, and calling it can replay ClickedItem popups on load.
            if (questItem.QuestResourceBehaviour != null)
                questItem.QuestResourceBehaviour.gameObject.SetActive(false);
        }
    }

    private static string[] CaptureGrantedQuestSymbolsFromInventory(ulong questUid)
    {
        try
        {
            var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
            if (pe == null || pe.Items == null) return new string[0];

            var found = new HashSet<string>();
            for (int i = 0; i < pe.Items.Count; i++)
            {
                var it = pe.Items.GetItem(i);
                if (it == null) continue;
                ulong uid; string sym;
                if (TryGetQuestLink(it, out uid, out sym) && uid == questUid)
                    found.Add(sym);
            }
            return found.ToArray();
        }
        catch { return new string[0]; }
    }

    private static int[] CaptureGetItemPopupIdsForSymbols(Quest q, string[] symbols)
    {
        if (q == null || symbols == null || symbols.Length == 0) return new int[0];
        var want = new HashSet<string>(symbols);
        var ids = new List<int>();

        try
        {
            var fTasks = typeof(Quest).GetField("tasks", BindingFlags.Instance | BindingFlags.NonPublic);
            var dict = fTasks != null ? fTasks.GetValue(q) : null;
            if (dict == null) return new int[0];
            var valuesProp = dict.GetType().GetProperty("Values");
            var values = valuesProp != null ? valuesProp.GetValue(dict, null) as System.Collections.IEnumerable : null;
            if (values == null) return new int[0];

            foreach (var task in values)
            {
                if (task == null) continue;
                var fields = task.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int fi = 0; fi < fields.Length; fi++)
                {
                    var enumerable = fields[fi].GetValue(task) as System.Collections.IEnumerable;
                    if (enumerable == null) continue;
                    foreach (var act in enumerable)
                    {
                        if (act == null) continue;
                        if (act.GetType().Name != "GetItem") continue;

                        var fItemSymbol = act.GetType().GetField("itemSymbol", BindingFlags.Instance | BindingFlags.NonPublic);
                        var fTextId = act.GetType().GetField("textId", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (fItemSymbol == null || fTextId == null) continue;

                        var symObj = fItemSymbol.GetValue(act);
                        string symName = null;
                        if (symObj != null)
                        {
                            var pName = symObj.GetType().GetProperty("Name");
                            if (pName != null) symName = pName.GetValue(symObj, null) as string;
                        }

                        if (!string.IsNullOrEmpty(symName) && want.Contains(symName))
                        {
                            int id = (int)fTextId.GetValue(act);
                            if (id != 0) ids.Add(id);
                        }
                    }
                }
            }
        }
        catch { }

        // avoid duplicates
        return ids.Distinct().ToArray();
    }

    private static void EnsureGrantedQuestItemsInInventory(Quest q, string[] grantedSymbols)
    {
        if (q == null || grantedSymbols == null) return;

        var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
        if (pe == null || pe.Items == null) return;

        ulong uid = q.UID;
        var granted = new HashSet<string>(grantedSymbols);

        // Remove any quest items for this uid that are NOT granted, and also remove duplicates of granted.
        var toRemove = new List<DaggerfallUnityItem>();
        var counts = new Dictionary<string, int>();

        for (int i = 0; i < pe.Items.Count; i++)
        {
            var it = pe.Items.GetItem(i);
            if (it == null) continue;
            ulong itUid; string itSym;
            if (!TryGetQuestLink(it, out itUid, out itSym)) continue;
            if (itUid != uid) continue;

            if (!granted.Contains(itSym))
            {
                toRemove.Add(it);
                continue;
            }

            int c;
            counts.TryGetValue(itSym, out c);
            counts[itSym] = c + 1;
            if (counts[itSym] > 1)
                toRemove.Add(it);
        }

        foreach (var r in toRemove)
            pe.Items.RemoveItem(r);

        // Ensure exactly one of each granted symbol.
        foreach (var sym in grantedSymbols)
        {
            int have;
            counts.TryGetValue(sym, out have);
            if (have >= 1) continue;

            Item questItem = q.GetItem(new Symbol(sym));
            if (questItem == null || questItem.DaggerfallUnityItem == null) continue;

            TryAssignFreshItemUid(questItem.DaggerfallUnityItem);
            pe.Items.AddItem(questItem.DaggerfallUnityItem, ItemCollection.AddPosition.Front);
        }
    }



// -------------------------------------------------------------------------
// Random delivery GetItem selection
// -------------------------------------------------------------------------

/// <summary>
/// Courier/delivery quests contain multiple possible GetItem actions (book/weapon/etc) but only one should execute.
/// This returns true if the given symbol is allowed to execute for this quest UID.
/// Non-delivery symbols are always allowed.
/// </summary>
public static bool TryClaimRandomDeliveryGetItem(ulong questUid, string symbol)
{
    if (string.IsNullOrEmpty(symbol))
        return true;

    if (!_randomDeliverySymbols.Contains(symbol))
        return true; // not a random-delivery category

    string chosen;
    if (_randomDeliveryChosenByUid.TryGetValue(questUid, out chosen))
    {
        // Already chosen in this session.
        return string.Equals(chosen, symbol, System.StringComparison.OrdinalIgnoreCase);
    }

    _randomDeliveryChosenByUid[questUid] = symbol;
    return true;
}

/// <summary>Clear cached selection for quest UID so a new quest with a reused UID can roll again.</summary>
public static void ResetRandomDeliveryForQuest(ulong questUid)
{
    _randomDeliveryChosenByUid.Remove(questUid);

    // Also clear replication guards for this quest so a new quest with reused UID can replicate.
    // GetItem keys can use either the local UID fallback or the shared instance ID.
    string instanceId = GetLocalQuestInstanceId(questUid);
    string uidGetItemPrefix = "uid=" + questUid.ToString() + ":";
    string instanceGetItemPrefix = !string.IsNullOrEmpty(instanceId) ? "inst=" + instanceId + ":" : string.Empty;
    string pipePrefix = questUid.ToString() + "|";
    _replicatedGetItems.RemoveWhere(k => k.StartsWith(uidGetItemPrefix) || (!string.IsNullOrEmpty(instanceGetItemPrefix) && k.StartsWith(instanceGetItemPrefix)));
    _serverAcceptedGetItemGrants.RemoveWhere(k => k.StartsWith(uidGetItemPrefix) || (!string.IsNullOrEmpty(instanceGetItemPrefix) && k.StartsWith(instanceGetItemPrefix)));
    _appliedGetItemGrants.RemoveWhere(k => k.StartsWith(uidGetItemPrefix) || (!string.IsNullOrEmpty(instanceGetItemPrefix) && k.StartsWith(instanceGetItemPrefix)));
    _serverAcceptedPromptChoices.RemoveWhere(k => k.StartsWith(uidGetItemPrefix) || (!string.IsNullOrEmpty(instanceGetItemPrefix) && k.StartsWith(instanceGetItemPrefix)));
    _appliedPromptChoices.RemoveWhere(k => k.StartsWith(uidGetItemPrefix) || (!string.IsNullOrEmpty(instanceGetItemPrefix) && k.StartsWith(instanceGetItemPrefix)));
    _remotePersonClickMessagesShown.RemoveWhere(k => k.StartsWith(pipePrefix));
    _remotePersonClickMessageConsumeAllowed.RemoveWhere(k => k.StartsWith(pipePrefix));
    _remotePersonClicksApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    _sharedPersonClickInteractions.Remove(questUid);
    string[] personClickChainKeys =
        _personClickTaskChainCache.Keys
            .Where(k => k.StartsWith(
                questUid.ToString() + "|",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    for (int personClickChainIndex = 0;
         personClickChainIndex < personClickChainKeys.Length;
         personClickChainIndex++)
    {
        _personClickTaskChainCache.Remove(
            personClickChainKeys[personClickChainIndex]);
    }
    _remoteItemClickMessagesShown.RemoveWhere(k => k.StartsWith(pipePrefix));
    _remoteItemClicksApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    List<string> recentClickKeys = new List<string>(_recentQuestItemClickPickupUntil.Keys);
    for (int i = 0; i < recentClickKeys.Count; i++)
    {
        if (recentClickKeys[i].StartsWith(pipePrefix))
            _recentQuestItemClickPickupUntil.Remove(recentClickKeys[i]);
    }
    _remoteLocationRevealsApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    _remotePcAtApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    _remoteRewardReplayApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    _pendingRewardReplayKeys.RemoveWhere(k => k.StartsWith(pipePrefix));

    string[] pendingFoePopupKeys =
        _pendingFoePopupMessages.Keys
            .Where(k => k.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    for (int i = 0; i < pendingFoePopupKeys.Length; i++)
        _pendingFoePopupMessages.Remove(pendingFoePopupKeys[i]);
    _remoteEscortFacesApplied.RemoveWhere(k => k.StartsWith(pipePrefix));
    _reportedLocalFoeLootKeys.RemoveWhere(k => k.StartsWith(pipePrefix));
    _consumedTotingQuestItemKeys.RemoveWhere(k => k.StartsWith(pipePrefix));
    _localPickedQuestItems.RemoveWhere(k => k.StartsWith(pipePrefix));
    _permanentQuestItemKeys.RemoveWhere(k => k.StartsWith(pipePrefix));
    string[] physicalPickupKeys = _physicalQuestInventoryItemByKey.Keys
        .Where(k => k.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    for (int i = 0; i < physicalPickupKeys.Length; i++)
        _physicalQuestInventoryItemByKey.Remove(physicalPickupKeys[i]);

    List<string> pickupProtectKeys = new List<string>(_localPickedQuestItemProtectUntil.Keys);
    for (int i = 0; i < pickupProtectKeys.Count; i++)
    {
        if (pickupProtectKeys[i].StartsWith(pipePrefix))
            _localPickedQuestItemProtectUntil.Remove(pickupProtectKeys[i]);
    }

    // Popup replay guard is per quest uid (if present in your file).
    try { _shownGetItemPopup.Remove(questUid); } catch { }
}

    // -------------------------------------------------------------------------
    // GetItem replication by shared quest instance
    // -------------------------------------------------------------------------

    private static string GetLocalQuestInstanceId(ulong questUid)
    {
        string instanceId;
        if (_cliUid2Inst.TryGetValue(questUid, out instanceId) && !string.IsNullOrEmpty(instanceId))
            return instanceId;
        if (_srvUid2Inst.TryGetValue(questUid, out instanceId) && !string.IsNullOrEmpty(instanceId))
            return instanceId;
        return string.Empty;
    }

    private static string MakeGetItemGrantKey(string instanceId, ulong questUid, string symbol)
    {
        string questKey = !string.IsNullOrEmpty(instanceId)
            ? "inst=" + instanceId
            : "uid=" + questUid.ToString();
        return questKey + ":" + (symbol ?? string.Empty);
    }

    private static bool TryResolveLocalQuestUidForInstance(string instanceId, ulong fallbackUid, out ulong localUid)
    {
        localUid = fallbackUid;
        if (string.IsNullOrEmpty(instanceId))
            return localUid != 0UL;

        if (_cliInst2Uid.TryGetValue(instanceId, out localUid) && localUid != 0UL)
            return true;
        if (_srvInst2Uid.TryGetValue(instanceId, out localUid) && localUid != 0UL)
            return true;

        localUid = 0UL;
        return false;
    }

    private static string MakePendingGetItemGrantPacketKey(
        string instanceId,
        ulong remoteQuestUid,
        string questName,
        string symbol)
    {
        string questKey = !string.IsNullOrEmpty(instanceId)
            ? "inst=" + instanceId
            : "uid=" + remoteQuestUid.ToString() +
              ":name=" + (questName ?? string.Empty);

        return questKey + ":pending-getitem:" +
            (symbol ?? string.Empty);
    }

    private static bool QuestMatchesGetItemGrant(
        Quest q,
        string questName,
        string symbol)
    {
        if (q == null ||
            q.QuestComplete ||
            q.QuestTombstoned ||
            string.IsNullOrEmpty(symbol))
            return false;

        if (!string.IsNullOrEmpty(questName) &&
            !string.Equals(
                q.QuestName,
                questName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return q.GetItem(new Symbol(symbol)) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveLocalQuestForGetItemGrant(
        string instanceId,
        ulong remoteQuestUid,
        string questName,
        string symbol,
        out ulong localQuestUid)
    {
        localQuestUid = 0UL;
        if (QuestMachine.Instance == null)
            return false;

        ulong mappedUid;
        if (TryResolveLocalQuestUidForInstance(
                instanceId,
                remoteQuestUid,
                out mappedUid))
        {
            Quest mappedQuest =
                QuestMachine.Instance.GetQuest(mappedUid);
            if (QuestMatchesGetItemGrant(
                    mappedQuest,
                    questName,
                    symbol))
            {
                localQuestUid = mappedUid;
                return true;
            }
        }

        // Load hygiene can replace the old network instance ID before this packet is
        // retried. Safe-resume quests retain the same UID, so accept that copy only when
        // both the quest template and item symbol match.
        if (remoteQuestUid != 0UL)
        {
            Quest sameUidQuest =
                QuestMachine.Instance.GetQuest(remoteQuestUid);
            if (QuestMatchesGetItemGrant(
                    sameUidQuest,
                    questName,
                    symbol))
            {
                localQuestUid = remoteQuestUid;
                return true;
            }
        }

        // UID-collision-safe imports can legitimately use a different local UID. Use a
        // template fallback only when exactly one active local quest is a valid match.
        if (!string.IsNullOrEmpty(questName))
        {
            ulong[] candidates =
                QuestMachine.Instance.FindQuests(
                    questName,
                    true);
            ulong uniqueUid = 0UL;
            int matchCount = 0;

            for (int i = 0; i < candidates.Length; i++)
            {
                Quest candidate =
                    QuestMachine.Instance.GetQuest(
                        candidates[i]);
                if (!QuestMatchesGetItemGrant(
                        candidate,
                        questName,
                        symbol))
                    continue;

                uniqueUid = candidates[i];
                matchCount++;
                if (matchCount > 1)
                    break;
            }

            if (matchCount == 1)
            {
                localQuestUid = uniqueUid;
                return true;
            }
        }

        return false;
    }

    private static void QueuePendingGetItemGrantPacket(
        string instanceId,
        ulong remoteQuestUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount,
        string reason)
    {
        if (string.IsNullOrEmpty(symbol))
            return;

        string packetKey =
            MakePendingGetItemGrantPacketKey(
                instanceId,
                remoteQuestUid,
                questName,
                symbol);

        PendingGetItemGrant pending;
        if (!_pendingGetItemGrantPackets.TryGetValue(
                packetKey,
                out pending))
        {
            pending = new PendingGetItemGrant
            {
                instanceId = instanceId ?? string.Empty,
                remoteQuestUid = remoteQuestUid,
                questName = questName ?? string.Empty,
                symbol = symbol,
                queuedAtRealtime =
                    Time.realtimeSinceStartup,
                nextDebugLogRealtime = 0f,
            };
        }

        pending.popupTextId = popupTextId;
        pending.grantedStackCount =
            Mathf.Max(1, grantedStackCount);

        _pendingGetItemGrantPackets[packetKey] = pending;

        if (Debug.isDebugBuild &&
            Time.realtimeSinceStartup >=
                pending.nextDebugLogRealtime)
        {
            pending.nextDebugLogRealtime =
                Time.realtimeSinceStartup + 3f;
            _pendingGetItemGrantPackets[packetKey] =
                pending;

            Debug.Log(
                $"[QuestNetSync][LetterSync] Queued GetItem event " +
                $"reason={reason} inst='{instanceId}' " +
                $"remoteUid={remoteQuestUid} quest='{questName}' " +
                $"symbol='{symbol}' popup={popupTextId}");
        }
    }

    private bool TryApplyGetItemGrantedPacketNow(
        string instanceId,
        ulong remoteQuestUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        if (IsQuestNetSyncPausedForLoad() ||
            QuestMachine.Instance == null)
            return false;

        ulong localQuestUid;
        if (!TryResolveLocalQuestForGetItemGrant(
                instanceId,
                remoteQuestUid,
                questName,
                symbol,
                out localQuestUid))
            return false;

        Quest resolvedQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(localQuestUid)
                : null;
        if (resolvedQuest != null)
        {
            StartCoroutine(
                CoEnsureScriptedGetItemPermanent(
                    resolvedQuest,
                    symbol,
                    "remote-getitem-packet"));
        }

        // Deduplicate against this receiver's current local mapping. After load
        // hygiene the sender's old instance ID can be replaced by a new resume ID;
        // local GetItem will report under the new ID (or local UID fallback).
        string localInstanceId =
            GetLocalQuestInstanceId(localQuestUid);
        string grantKey =
            MakeGetItemGrantKey(
                localInstanceId,
                localQuestUid,
                symbol);
        if (!_appliedGetItemGrants.Add(grantKey))
            return true;

        StartCoroutine(
            CoApplyGetItemGranted(
                resolvedQuest,
                symbol,
                popupTextId,
                grantedStackCount,
                grantKey));
        return true;
    }

    private void ProcessPendingGetItemGrantPackets()
    {
        if (!isLocalPlayer ||
            _pendingGetItemGrantPackets.Count == 0 ||
            IsQuestNetSyncPausedForLoad())
            return;

        string[] packetKeys =
            _pendingGetItemGrantPackets.Keys.ToArray();

        for (int i = 0; i < packetKeys.Length; i++)
        {
            PendingGetItemGrant pending;
            if (!_pendingGetItemGrantPackets.TryGetValue(
                    packetKeys[i],
                    out pending))
                continue;

            if (!TryApplyGetItemGrantedPacketNow(
                    pending.instanceId,
                    pending.remoteQuestUid,
                    pending.questName,
                    pending.symbol,
                    pending.popupTextId,
                    pending.grantedStackCount))
                continue;

            _pendingGetItemGrantPackets.Remove(
                packetKeys[i]);

            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[QuestNetSync][LetterSync] Applied queued GetItem event " +
                    $"remoteUid={pending.remoteQuestUid} " +
                    $"quest='{pending.questName}' " +
                    $"symbol='{pending.symbol}'");
            }
        }
    }

    private static bool QuestActionReferencesSymbol(
        IQuestAction action,
        string symbolName)
    {
        if (action == null || string.IsNullOrEmpty(symbolName))
            return false;

        try
        {
            FieldInfo[] fields =
                action.GetType().GetFields(
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.Public);

            for (int i = 0; i < fields.Length; i++)
            {
                object value = fields[i].GetValue(action);
                string candidate =
                    GetSymbolName(value);
                if (string.Equals(
                        candidate,
                        symbolName,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryFindScriptedGetItemPermanentAction(
        Quest q,
        string symbolName,
        out DaggerfallWorkshop.Game.Questing.Task ownerTask,
        out IQuestAction makePermanentAction)
    {
        ownerTask = null;
        makePermanentAction = null;

        if (q == null || string.IsNullOrEmpty(symbolName))
            return false;

        try
        {
            List<DaggerfallWorkshop.Game.Questing.Task> tasks =
                GetQuestTasksForActionScan(q);

            for (int i = 0; i < tasks.Count; i++)
            {
                DaggerfallWorkshop.Game.Questing.Task task =
                    tasks[i];
                if (task == null || task.Actions == null)
                    continue;

                bool grantsItem = false;
                IQuestAction permanentAction = null;

                foreach (IQuestAction action in task.Actions)
                {
                    if (action == null)
                        continue;

                    string actionType =
                        action.GetType().Name;

                    if (string.Equals(
                            actionType,
                            "GetItem",
                            StringComparison.Ordinal) &&
                        QuestActionReferencesSymbol(
                            action,
                            symbolName))
                    {
                        grantsItem = true;
                    }
                    else if (string.Equals(
                                 actionType,
                                 "MakePermanent",
                                 StringComparison.Ordinal) &&
                             QuestActionReferencesSymbol(
                                 action,
                                 symbolName))
                    {
                        permanentAction = action;
                    }
                }

                if (grantsItem && permanentAction != null)
                {
                    ownerTask = task;
                    makePermanentAction = permanentAction;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool EnsureScriptedGetItemPermanent(
        Quest q,
        string symbolName,
        string reason)
    {
        DaggerfallWorkshop.Game.Questing.Task task;
        IQuestAction makePermanent;
        if (!TryFindScriptedGetItemPermanentAction(
                q,
                symbolName,
                out task,
                out makePermanent))
            return false;

        try
        {
            // The normal task might already have completed this action. Re-running only
            // MakePermanent is idempotent and repairs the race where the network GetItem
            // backstop adds the still-green quest prototype after the local task passed
            // its permanence action.
            ActionTemplate template =
                makePermanent as ActionTemplate;
            if (template != null)
                template.IsComplete = false;

            makePermanent.Update(task);

            if (Debug.isDebugBuild)
            {
                Item item =
                    q.GetItem(new Symbol(symbolName));
                Debug.Log(
                    $"[QuestNetSync][GetItemPermanent] Ensured scripted permanent reward " +
                    $"uid={q.UID} quest='{q.QuestName}' item='{symbolName}' " +
                    $"madePermanent={(item != null && item.MadePermanent)} reason='{reason}'");
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[QuestNetSync][GetItemPermanent] Failed uid={(q != null ? q.UID : 0UL)} " +
                $"item='{symbolName}' reason='{reason}': {ex.Message}");
            return false;
        }
    }

    private IEnumerator CoEnsureScriptedGetItemPermanent(
        Quest q,
        string symbolName,
        string reason)
    {
        // GetItem reports from inside the task action. Let the task advance naturally
        // first, then verify that its following MakePermanent action actually applied.
        for (int i = 0; i < 8; i++)
        {
            yield return null;

            if (q == null)
                yield break;

            if (EnsureScriptedGetItemPermanent(
                    q,
                    symbolName,
                    reason))
                yield break;
        }
    }

    public static void ReportGetItemGranted(
        ulong questUid,
        string symbol,
        int popupTextId)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;
        if (LocalInstance == null)
            return;

        LocalInstance.Local_ReportGetItemGranted(
            questUid,
            symbol,
            popupTextId);
    }

    private void Local_ReportGetItemGranted(
        ulong questUid,
        string symbol,
        int popupTextId)
    {
        if (string.IsNullOrEmpty(symbol))
            return;

        string instanceId =
            GetLocalQuestInstanceId(questUid);
        string key =
            MakeGetItemGrantKey(
                instanceId,
                questUid,
                symbol);
        if (_replicatedGetItems.Contains(key))
            return;

        _replicatedGetItems.Add(key);

        int grantedStackCount = 1;
        string questName = string.Empty;
        try
        {
            Quest q = QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(questUid)
                : null;
            if (q != null)
            {
                questName = q.QuestName ?? string.Empty;
                Item item =
                    q.GetItem(new Symbol(symbol));
                if (item != null &&
                    item.DaggerfallUnityItem != null)
                {
                    grantedStackCount =
                        NormalizeQuestItemStackCount(
                            item.DaggerfallUnityItem);
                }

                // GetItem is reported from inside the actual grant action. If this
                // task also contains MakePermanent for the same symbol, finalize it now
                // before marking the grant as network-complete can race with quest-end
                // cleanup. This is important for M0B11Y18 _traitorreward_: its Say popup
                // sits between GetItem and MakePermanent while downstream When tasks can
                // already advance the parent quest toward EndQuest.
                if (!EnsureScriptedGetItemPermanent(
                        q,
                        symbol,
                        "local-getitem-immediate"))
                {
                    // Keep the existing deferred repair for unusual timing where the
                    // scripted permanence action cannot be resolved synchronously.
                    StartCoroutine(
                        CoEnsureScriptedGetItemPermanent(
                            q,
                            symbol,
                            "local-getitem"));
                }
            }
        }
        catch { }

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                $"[QuestNetSync] ReportGetItemGranted uid={questUid} " +
                $"quest='{questName}' symbol={symbol} popup={popupTextId} " +
                $"server={isServer} client={isClient}");
        }

        if (isServer)
        {
            ulong serverQuestUid = questUid;
            if (!string.IsNullOrEmpty(instanceId))
            {
                ulong mappedServerUid;
                if (_srvInst2Uid.TryGetValue(
                        instanceId,
                        out mappedServerUid) &&
                    mappedServerUid != 0UL)
                {
                    serverQuestUid = mappedServerUid;
                }
                else
                {
                    instanceId = string.Empty;
                }
            }

            string serverKey =
                MakeGetItemGrantKey(
                    instanceId,
                    serverQuestUid,
                    symbol);
            if (!_serverAcceptedGetItemGrants.Add(
                    serverKey))
                return;

            ServerBroadcastGetItemGranted(
                instanceId,
                serverQuestUid,
                questName,
                symbol,
                popupTextId,
                grantedStackCount);
        }
        else if (isClient)
        {
            CmdGetItemGranted(
                instanceId,
                questUid,
                questName,
                symbol,
                popupTextId,
                grantedStackCount);
        }
    }

    [Command]
    private void CmdGetItemGranted(
        string instanceId,
        ulong questUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        if (IsQuestNetSyncPausedForLoad())
            return;
        if (!isServer ||
            string.IsNullOrEmpty(symbol))
            return;

        ulong serverQuestUid;
        if (!TryResolveLocalQuestForGetItemGrant(
                instanceId,
                questUid,
                questName,
                symbol,
                out serverQuestUid))
        {
            Debug.LogWarning(
                $"[QuestNetSync][GetItemResolve] Server rejected unresolved GetItem " +
                $"inst='{instanceId}' remoteUid={questUid} quest='{questName}' " +
                $"symbol='{symbol}'");
            return;
        }

        Quest serverQuest =
            QuestMachine.Instance != null
                ? QuestMachine.Instance.GetQuest(
                    serverQuestUid)
                : null;
        if (!QuestMatchesGetItemGrant(
                serverQuest,
                questName,
                symbol))
            return;

        string key =
            MakeGetItemGrantKey(
                instanceId,
                serverQuestUid,
                symbol);
        if (!_serverAcceptedGetItemGrants.Add(key))
            return;

        ServerBroadcastGetItemGranted(
            instanceId,
            serverQuestUid,
            serverQuest.QuestName,
            symbol,
            popupTextId,
            grantedStackCount);
    }

    [ClientRpc]
    private void RpcGetItemGranted(
        string instanceId,
        ulong questUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        ApplyGetItemGrantedPacket(
            instanceId,
            questUid,
            questName,
            symbol,
            popupTextId,
            grantedStackCount);
    }

    [TargetRpc]
    private void TargetGetItemGranted(
        NetworkConnection target,
        string instanceId,
        ulong questUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        ApplyGetItemGrantedPacket(
            instanceId,
            questUid,
            questName,
            symbol,
            popupTextId,
            grantedStackCount);
    }

    private void ApplyGetItemGrantedPacket(
        string instanceId,
        ulong questUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        if (!isClient ||
            string.IsNullOrEmpty(symbol))
            return;

        if (TryApplyGetItemGrantedPacketNow(
                instanceId,
                questUid,
                questName,
                symbol,
                popupTextId,
                grantedStackCount))
        {
            _pendingGetItemGrantPackets.Remove(
                MakePendingGetItemGrantPacketKey(
                    instanceId,
                    questUid,
                    questName,
                    symbol));
            return;
        }

        QueuePendingGetItemGrantPacket(
            instanceId,
            questUid,
            questName,
            symbol,
            popupTextId,
            grantedStackCount,
            IsQuestNetSyncPausedForLoad()
                ? "load-paused"
                : "quest-mapping-not-ready");
    }

    private static void ServerBroadcastGetItemGranted(
        string instanceId,
        ulong questUid,
        string questName,
        string symbol,
        int popupTextId,
        int grantedStackCount)
    {
        // Use the authoritative connection table. FindObjectsOfType() excludes inactive
        // player GameObjects, so an away/interior/hidden third client could miss the
        // GetItem event and rely on a later incomplete task-state backstop.
        int connectedCount = 0;
        int sentCount = 0;
        int missingIdentityCount = 0;
        int missingSyncCount = 0;

        foreach (var entry in NetworkServer.connections)
        {
            NetworkConnection conn =
                entry.Value;
            if (conn == null)
                continue;

            connectedCount++;

            NetworkIdentity identity =
                conn.identity;
            if (identity == null)
            {
                missingIdentityCount++;
                continue;
            }

            QuestNetSync sync =
                identity.GetComponent<QuestNetSync>();
            if (sync == null)
            {
                sync =
                    identity.GetComponentInChildren<QuestNetSync>(true);
            }

            if (sync == null || !sync.isServer)
            {
                missingSyncCount++;
                continue;
            }

            sync.TargetGetItemGranted(
                conn,
                instanceId,
                questUid,
                questName,
                symbol,
                popupTextId,
                grantedStackCount);
            sentCount++;
        }

        Debug.Log(
            $"[QuestNetSync][GetItemFanout] Sent GetItem uid={questUid} " +
            $"quest='{questName}' item='{symbol}' connected={connectedCount} " +
            $"recipients={sentCount} missingIdentity={missingIdentityCount} " +
            $"missingSync={missingSyncCount}");
    }

    private IEnumerator CoApplyGetItemGranted(
        Quest validatedQuest,
        string symbol,
        int popupTextId,
        int grantedStackCount,
        string grantKey)
    {
        // Every participant is allowed to execute the quest action locally. Give that
        // action time to run first; ReportGetItemGranted() records the same instance key.
        // The network grant below is only a backstop for a player who missed the task
        // trigger, not a second reward on players whose local action already succeeded.
        for (int i = 0; i < 15; i++)
        {
            if (!string.IsNullOrEmpty(grantKey) &&
                _replicatedGetItems.Contains(grantKey))
                yield break;

            if (IsQuestNetSyncPausedForLoad())
            {
                yield return null;
                i--;
                continue;
            }

            yield return null;
        }

        // Keep the exact quest object validated when the server grant arrived. The
        // parent can be tombstoned while this backstop waits for natural local action;
        // looking it up again would then discard a valid reward for the participant
        // whose prompt selected the ending branch.
        Quest q = validatedQuest;
        if (q == null)
        {
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"[QuestNetSync][LetterSync] Resolved quest disappeared before " +
                    $"GetItem apply symbol='{symbol}'");
            }
            yield break;
        }

        Item grantedItem =
            q.GetItem(new Symbol(symbol));
        if (grantedItem != null &&
            grantedItem.DaggerfallUnityItem != null)
        {
            grantedItem.DaggerfallUnityItem.stackCount =
                NormalizeQuestItemStackCountForDto(
                    grantedItem.DaggerfallUnityItem,
                    grantedStackCount);
        }

        // A real GetItem action is an explicit reacquisition and may legitimately
        // reuse a symbol that was consumed by an earlier toting turn-in.
        ClearTotingQuestItemConsumed(
            q.UID,
            symbol,
            "explicit-getitem-grant");

        // Add this one item without deleting grants from other symbols, then mark only
        // this GetItem action complete so a delayed task packet cannot grant it twice.
        EnsureGrantedGetItemByReference(q, symbol);
        EnsureScriptedGetItemPermanent(
            q,
            symbol,
            "getitem-backstop");
        MarkGetItemActionComplete(q, symbol);
        if (!string.IsNullOrEmpty(grantKey))
            _replicatedGetItems.Add(grantKey);

        if (popupTextId != 0)
            q.ShowMessagePopup(popupTextId);
    }

    private static void MarkGetItemActionComplete(Quest q, string grantedSymbol)
    {
        if (q == null || string.IsNullOrEmpty(grantedSymbol))
            return;

        Quest.TaskState[] states = q.GetTaskStates() ?? new Quest.TaskState[0];
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].symbol == null)
                continue;

            DaggerfallWorkshop.Game.Questing.Task task = q.GetTask(states[i].symbol);
            if (task == null)
                continue;

            foreach (IQuestAction action in task.Actions)
            {
                if (action == null || !string.Equals(action.GetType().Name, "GetItem", StringComparison.Ordinal))
                    continue;

                FieldInfo symbolField = action.GetType().GetField("itemSymbol", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Symbol actionSymbol = symbolField != null ? symbolField.GetValue(action) as Symbol : null;
                if (actionSymbol == null || !string.Equals(actionSymbol.Name, grantedSymbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                ActionTemplate template = action as ActionTemplate;
                if (template != null)
                    template.IsComplete = true;
            }
        }
    }

    private static void EnsureGrantedGetItemByReference(Quest q, string grantedSymbol)
    {
        if (q == null || string.IsNullOrEmpty(grantedSymbol)) return;

        var pe = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
        if (pe == null || pe.Items == null) return;

        Item questItem = q.GetItem(new Symbol(grantedSymbol));
        DaggerfallUnityItem grantedRef = questItem != null ? questItem.DaggerfallUnityItem : null;
        if (grantedRef == null)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning($"[QuestNetSync] EnsureGrantedGetItemByReference: missing resource symbol={grantedSymbol} uid={q.UID}");
            return;
        }

        int grantedCount = 0;
        for (int idx = pe.Items.Count - 1; idx >= 0; idx--)
        {
            var inv = pe.Items.GetItem(idx);
            if (!object.ReferenceEquals(inv, grantedRef))
                continue;

            grantedCount++;
            if (grantedCount > 1)
                pe.Items.RemoveItem(inv);
        }

        if (grantedCount == 0)
            pe.Items.AddItem(grantedRef, ItemCollection.AddPosition.Front);

        if (Debug.isDebugBuild)
            Debug.Log($"[QuestNetSync] EnsureGrantedGetItemByReference uid={q.UID} granted={grantedSymbol} haveAfter=1");
    }

}
