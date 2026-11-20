using System;
using System.Collections.Generic;
using System.Collections;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using System.Linq;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;

/// <summary>
/// Server-owned multiplayer party membership layer.
///
/// This intentionally does not depend on the listen-host's local player, QuestMachine,
/// WorldTime, WeatherManager, or any player-prefab SyncVars. Party state is kept by the
/// network server and mirrored to clients with ordinary Mirror NetworkMessages.
/// That makes the same layer usable later by a true dedicated server.
///
/// Current scope:
/// - verified against the current custom Mirror.NetworkManager extension lifecycle
///   (ClientExtensionsReady / ServerExtensionsReady / SessionStopped);
/// - every connected player begins in a one-person party and is its leader;
/// - leaders can invite solo players;
/// - invitees can accept/decline;
/// - members can leave;
/// - leaders can kick/promote members;
/// - leader disconnect/leave promotes another remaining member automatically;
/// - clients receive a lightweight roster snapshot for UI/party filtering.
///
/// Quest/time/weather routing is deliberately NOT changed here. Those systems can query
/// this manager in later migration stages instead of inferring "party == all clients".
/// </summary>
public sealed class MultiplayerPartyManager : MonoBehaviour
{
    const float ServerSweepInterval = 0.50f;
    const float SnapshotHeartbeatInterval = 5.0f;
    const float MissingConnectionGraceSeconds = 2.0f;
    const float InviteLifetimeSeconds = 60.0f;

    const int ActionInvite = 1;
    const int ActionAcceptInvite = 2;
    const int ActionDeclineInvite = 3;
    const int ActionLeaveParty = 4;
    const int ActionKickMember = 5;
    const int ActionPromoteLeader = 6;
    const int ActionSetQuestMode = 7;
    const int ActionSetLeaderTime = 8;
    const int ActionSetRestTogether = 9;
    const int ActionSetTravelTogether = 10;
    // Travel Together has its own fixed range, separate from the adjustable rest radius.
    const int TravelTogetherRadiusMeters = 10;
    const float TravelTogetherSessionTimeout = 90f;
    const float DfUnitsPerMeter = 40f;

    static MultiplayerPartyManager instance;

    public struct PartyActionRequest : NetworkMessage
    {
        public int action;
        public uint targetNetId;
        public ulong partyId;
        public uint inviterNetId;
    }

    public struct PartySnapshotMessage : NetworkMessage
    {
        public uint[] netIds;
        public ulong[] partyIds;
        public uint[] leaderNetIds;
        public ulong[] timeEpochs;
        public int[] questSyncModes;
        public bool[] leaderControlsTime;
        public bool[] restTogether;
        public bool[] travelTogether;
        public int[] memberCounts;
        public string[] playerNames;
    }

    public struct PartyInviteMessage : NetworkMessage
    {
        public uint inviterNetId;
        public ulong partyId;
        public string inviterName;
    }

    public struct PartyStatusMessage : NetworkMessage
    {
        public string text;
    }

    public sealed class PartyClientState
    {
        public uint NetId;
        public ulong PartyId;
        public uint LeaderNetId;
        public ulong TimeEpoch;
        public OptionsMultiplayer.QuestSyncMode QuestMode;
        public bool LeaderControlsTime = true;
        public bool RestTogether = true;
        public bool TravelTogether = false;
        public int MemberCount;
        public string PlayerName;
    }

    sealed class ServerMember
    {
        public uint netId;
        public ulong partyId;
        public NetworkConnection connection;
        public float lastSeenRealtime;
    }

    sealed class ServerParty
    {
        public ulong id;
        public uint leaderNetId;
        public ulong timeEpoch = 1UL;
        public OptionsMultiplayer.QuestSyncMode questMode;
        public bool leaderControlsTime = true;
        public bool restTogether = true;
        public bool travelTogether = false;
        public readonly HashSet<uint> members = new HashSet<uint>();
    }

    sealed class PendingInvite
    {
        public uint inviterNetId;
        public uint targetNetId;
        public ulong partyId;
        public float expiresRealtime;
    }

    // These messages are independent of the player prefab and are routed via
    // Mirror's already-established party extension lifecycle (including headless).
    public struct PartyRestStartRequest : NetworkMessage { public uint requestId; }
    public struct PartyRestStartReply : NetworkMessage
    {
        public uint requestId;
        public ulong sessionId;
        public bool approved;
        public bool leaderPresentAtStart;
        public bool leaderControlsTime;
    }
    public struct PartyRestHourRequest : NetworkMessage
    {
        public ulong sessionId;
        public int completedHours;
    }
    public struct PartyRestStopRequest : NetworkMessage { public ulong sessionId; }
    public struct PartyRestHourNotice : NetworkMessage
    {
        public ulong sessionId;
        public uint initiatorId;
        public int completedHours;
        public bool ended;
    }
    public struct PartyRestAdvanceLeaderClock : NetworkMessage
    {
        public ulong sessionId;
        public int completedHours;
    }

    sealed class ServerRestSession
    {
        public ulong id;
        public ulong partyId;
        public uint initiator;
        public uint leaderAtStart;
        public bool leaderControlsTime;
        public bool leaderPresentAtStart;
        public int lastCompletedHours;
        public float lastProgressTime;
        public readonly HashSet<uint> recipients = new HashSet<uint>();
    }

    // Travel Together uses explicit acceptance. The initiator always makes a normal
    // vanilla-map trip. The server freezes the nearby eligible party members at
    // departure and invites each separately. Followers pay their own vanilla-quoted
    // fare and receive the initiator's cautious/inn/ship choices, but do not apply
    // another local clock jump. A late Yes can still work after the initiator lands.
    public struct PartyTravelStartRequest : NetworkMessage
    {
        public uint requestId;
        public int destinationPixelX;
        public int destinationPixelY;
        public string destinationName;
        public bool speedCautious;
        public bool sleepModeInn;
        public bool travelShip;
        public ulong startClockSeconds;
        public int travelMinutes;
    }
    public struct PartyTravelOfferMessage : NetworkMessage
    {
        public ulong partyId;
        public ulong timeEpoch;
        public uint initiatorId;
        public uint requestId;
        public int destinationPixelX;
        public int destinationPixelY;
        public string destinationName;
        public string initiatorName;
        public bool speedCautious;
        public bool sleepModeInn;
        public bool travelShip;
    }
    public struct PartyTravelResponseRequest : NetworkMessage
    {
        public ulong partyId;
        public uint initiatorId;
        public uint requestId;
        public bool accepted;
    }
    public struct PartyTravelArrivedRequest : NetworkMessage
    {
        public uint requestId;
        public int destinationPixelX;
        public int destinationPixelY;
        public int worldX;
        public int worldZ;
        public float worldY;
    }
    public struct PartyTravelFollowMessage : NetworkMessage
    {
        public ulong partyId;
        public ulong timeEpoch;
        public uint initiatorId;
        public uint requestId;
        public int worldX;
        public int worldZ;
        public float worldY;
        public bool speedCautious;
        public bool sleepModeInn;
        public bool travelShip;
    }

    public struct PartyTravelAdvanceLeaderClock : NetworkMessage
    {
        public ulong partyId;
        public ulong timeEpoch;
        public uint leaderNetId;
        public uint initiatorId;
        public uint requestId;
        public ulong targetSeconds;
    }

    sealed class ServerTravelSession
    {
        public ulong partyId;
        public ulong timeEpoch;
        public uint initiator;
        public uint requestId;
        public int pixelX;
        public int pixelY;
        public string destinationName;
        public float expiresRealtime;
        public bool speedCautious;
        public bool sleepModeInn;
        public bool travelShip;
        public bool hasArrived;
        public int arrivalWorldX;
        public int arrivalWorldZ;
        public float arrivalWorldY;
        public ulong startClockSeconds;
        public int travelMinutes;
        public bool clockAdvanceSent;
        public readonly HashSet<uint> invited = new HashSet<uint>();
        public readonly HashSet<uint> accepted = new HashSet<uint>();
        public readonly HashSet<uint> dispatched = new HashSet<uint>();
    }

    // A client-side fare reservation, quoted from that character's real starting
    // map pixel, equipment and guild privileges. No gold is deducted until arrival.
    sealed class LocalAcceptedTravel
    {
        public ulong partyId;
        public ulong timeEpoch;
        public uint initiatorId;
        public uint requestId;
        public int totalCost;
        public int piecesCost;
        public bool speedCautious;
        public bool sleepModeInn;
        public bool travelShip;
        public float acceptedAt;
    }

    sealed class ManagementAction
    {
        public int action;
        public uint targetNetId;
        public string label;
    }

    readonly Dictionary<uint, ServerMember> serverMembers = new Dictionary<uint, ServerMember>();
    readonly Dictionary<ulong, ServerParty> serverParties = new Dictionary<ulong, ServerParty>();
    readonly Dictionary<uint, PendingInvite> pendingInvitesByTarget = new Dictionary<uint, PendingInvite>();
    readonly Dictionary<ulong, ServerRestSession> serverRestSessions = new Dictionary<ulong, ServerRestSession>();
    // Keep completed trips until their invitation window expires, without blocking
    // the next party trip. Each session is uniquely identified by party, traveler,
    // and request ID, rather than occupying a single per-party slot for 90 seconds.
    readonly Dictionary<string, ServerTravelSession> serverTravelSessions = new Dictionary<string, ServerTravelSession>();
    uint localTravelRequestSerial;
    uint activeLocalTravelRequestId;
    int activeLocalTravelPixelX;
    int activeLocalTravelPixelY;
    ulong activeLocalTravelStartClock;
    int activeLocalTravelMinutes;
    bool localVanillaTravelInProgress;
    bool autoFollowingTravel;
    readonly Queue<PartyTravelOfferMessage> queuedTravelOffers = new Queue<PartyTravelOfferMessage>();
    bool travelOfferPopupOpen;
    LocalAcceptedTravel pendingAcceptedTravel;
    ulong nextRestSessionId = 1UL;
    uint localRestRequestSerial;
    uint pendingRestRequestId;
    Action<bool> pendingRestStart;
    float pendingRestRequestedAt;
    ulong localRestSessionId;
    bool localRestWithoutLeader;
    ulong remoteRestSessionId;
    uint remoteRestInitiatorId;
    int localRestHoursSent;
    int remoteRestLastCompletedHour;
    ulong leaderRestLastSession;
    int leaderRestLastHour;

    readonly Dictionary<uint, PartyClientState> clientStates = new Dictionary<uint, PartyClientState>();
    readonly Queue<PartyInviteMessage> queuedInvites = new Queue<PartyInviteMessage>();
    readonly Queue<string> queuedStatusMessages = new Queue<string>();

    bool serverHandlersRegistered;
    bool clientHandlersRegistered;
    bool serverStateDirty;
    bool invitePopupOpen;
    ulong nextPartyId = 1UL;
    float nextServerSweepTime;
    float nextSnapshotHeartbeatTime;

    public static event Action PartyStateChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null)
            return;

        GameObject go = new GameObject("Multiplayer Party Manager");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<MultiplayerPartyManager>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;

        global::Mirror.NetworkManager.ClientExtensionsReady += RegisterClientHandlers;
        global::Mirror.NetworkManager.ServerExtensionsReady += RegisterServerHandlers;
        global::Mirror.NetworkManager.SessionStopped += HandleSessionStopped;
        DaggerfallTravelPopUp.OnPreFastTravel += HandlePreVanillaTravel;
        DaggerfallTravelPopUp.OnPostFastTravel += HandlePostVanillaTravel;

        if (NetworkClient.active)
            RegisterClientHandlers();
        if (NetworkServer.active)
            RegisterServerHandlers();
    }

    void OnDestroy()
    {
        if (instance != this)
            return;

        global::Mirror.NetworkManager.ClientExtensionsReady -= RegisterClientHandlers;
        global::Mirror.NetworkManager.ServerExtensionsReady -= RegisterServerHandlers;
        global::Mirror.NetworkManager.SessionStopped -= HandleSessionStopped;
        DaggerfallTravelPopUp.OnPreFastTravel -= HandlePreVanillaTravel;
        DaggerfallTravelPopUp.OnPostFastTravel -= HandlePostVanillaTravel;
        instance = null;
    }

    void Update()
    {
        if (NetworkServer.active)
        {
            if (!serverHandlersRegistered)
                RegisterServerHandlers();

            float now = Time.unscaledTime;
            if (now >= nextServerSweepTime)
            {
                nextServerSweepTime = now + ServerSweepInterval;
                SweepServerConnections(now);
                ExpireInvites(now);
                ExpireRestSessions(now);
                ExpireTravelSessions(now);
            }

            if (serverStateDirty || now >= nextSnapshotHeartbeatTime)
            {
                serverStateDirty = false;
                nextSnapshotHeartbeatTime = now + SnapshotHeartbeatInterval;
                BroadcastSnapshot();
            }
        }

        if (NetworkClient.active && !clientHandlersRegistered)
            RegisterClientHandlers();

        if (pendingRestStart != null && Time.unscaledTime - pendingRestRequestedAt > 8f)
        {
            // No network response: let vanilla rest proceed locally, without shared recovery.
            Action<bool> callback = pendingRestStart;
            pendingRestStart = null;
            pendingRestRequestId = 0U;
            if (callback != null) callback(true);
        }
        if (pendingAcceptedTravel != null &&
            Time.unscaledTime - pendingAcceptedTravel.acceptedAt > TravelTogetherSessionTimeout + 5f)
        {
            pendingAcceptedTravel = null;
            DaggerfallUI.AddHUDText("The accepted party-travel invitation expired.", 4f);
        }
        DrainClientUiQueues();
    }

    void RegisterServerHandlers()
    {
        if (serverHandlersRegistered)
            return;

        NetworkServer.RegisterHandler<PartyActionRequest>(HandleServerAction);
        NetworkServer.RegisterHandler<PartyRestStartRequest>(HandleServerRestStart);
        NetworkServer.RegisterHandler<PartyRestHourRequest>(HandleServerRestHour);
        NetworkServer.RegisterHandler<PartyRestStopRequest>(HandleServerRestStop);
        NetworkServer.RegisterHandler<PartyTravelStartRequest>(HandleServerTravelStart);
        NetworkServer.RegisterHandler<PartyTravelResponseRequest>(HandleServerTravelResponse);
        NetworkServer.RegisterHandler<PartyTravelArrivedRequest>(HandleServerTravelArrived);
        serverHandlersRegistered = true;
        nextServerSweepTime = 0f;
        nextSnapshotHeartbeatTime = 0f;
        serverStateDirty = true;
        Debug.Log("[MPParty] Registered server party handlers.");
    }

    void RegisterClientHandlers()
    {
        if (clientHandlersRegistered)
            return;

        NetworkClient.RegisterHandler<PartySnapshotMessage>(ApplyClientSnapshot);
        NetworkClient.RegisterHandler<PartyRestStartReply>(ApplyClientRestStartReply);
        NetworkClient.RegisterHandler<PartyRestHourNotice>(ApplyClientRestHour);
        NetworkClient.RegisterHandler<PartyRestAdvanceLeaderClock>(ApplyLeaderRestTimeAdvance);
        NetworkClient.RegisterHandler<PartyTravelOfferMessage>(QueueClientTravelOffer);
        NetworkClient.RegisterHandler<PartyTravelFollowMessage>(ApplyClientTravelFollow);
        NetworkClient.RegisterHandler<PartyTravelAdvanceLeaderClock>(ApplyTravelLeaderClockAdvance);
        NetworkClient.RegisterHandler<PartyInviteMessage>(QueueClientInvite);
        NetworkClient.RegisterHandler<PartyStatusMessage>(QueueClientStatus);
        clientHandlersRegistered = true;
        Debug.Log("[MPParty] Registered client party handlers.");
    }

    void HandleSessionStopped()
    {
        serverHandlersRegistered = false;
        clientHandlersRegistered = false;

        serverMembers.Clear();
        serverParties.Clear();
        pendingInvitesByTarget.Clear();
        serverRestSessions.Clear();
        serverTravelSessions.Clear();
        nextRestSessionId = 1UL;
        pendingRestStart = null;
        pendingRestRequestId = 0U;
        localRestSessionId = remoteRestSessionId = leaderRestLastSession = 0UL;
        localRestWithoutLeader = false;
        localRestHoursSent = remoteRestLastCompletedHour = leaderRestLastHour = 0;
        activeLocalTravelRequestId = 0U;
        activeLocalTravelStartClock = 0UL;
        activeLocalTravelMinutes = 0;
        localVanillaTravelInProgress = autoFollowingTravel = false;
        pendingAcceptedTravel = null;
        travelOfferPopupOpen = false;
        queuedTravelOffers.Clear();
        clientStates.Clear();
        queuedInvites.Clear();
        queuedStatusMessages.Clear();

        nextPartyId = 1UL;
        serverStateDirty = false;
        invitePopupOpen = false;
        nextServerSweepTime = 0f;
        nextSnapshotHeartbeatTime = 0f;

        RaisePartyStateChanged();
    }

    // -------------------------------------------------------------------------
    // Client-facing query API used by party HUD/window/map today and later by
    // QuestNetSync, TimeCatcher, and WeatherCatcher.
    // -------------------------------------------------------------------------

    public static ulong GetLocalPartyId()
    {
        uint localNetId = GetLocalNetId();
        PartyClientState state;
        return TryGetClientState(localNetId, out state) ? state.PartyId : 0UL;
    }

    public static uint GetLocalPartyLeaderNetId()
    {
        uint localNetId = GetLocalNetId();
        PartyClientState state;
        return TryGetClientState(localNetId, out state) ? state.LeaderNetId : 0U;
    }

    public static int GetLocalPartyMemberCount()
    {
        uint localNetId = GetLocalNetId();
        PartyClientState state;
        return TryGetClientState(localNetId, out state) ? Mathf.Max(1, state.MemberCount) : 1;
    }

    public static bool IsLocalPartyLeader()
    {
        uint localNetId = GetLocalNetId();
        if (localNetId == 0U)
            return false;

        PartyClientState state;
        return TryGetClientState(localNetId, out state) && state.LeaderNetId == localNetId;
    }

    public static bool IsPartyLeader(global::PlayerMultiplayer player)
    {
        if (player == null || player.netId == 0U)
            return false;

        PartyClientState state;
        return TryGetClientState(player.netId, out state) && state.LeaderNetId == player.netId;
    }

    public static bool IsLocalPartyMember(global::PlayerMultiplayer player)
    {
        if (player == null)
            return false;
        if (player.isLocalPlayer)
            return true;

        uint localNetId = GetLocalNetId();
        if (localNetId == 0U || player.netId == 0U)
            return false; // Hide remote players until their network identities are known.

        PartyClientState localState;
        PartyClientState remoteState;
        if (!TryGetClientState(localNetId, out localState) ||
            !TryGetClientState(player.netId, out remoteState))
            return false; // Show remote players only after party membership is confirmed.

        return localState.PartyId != 0UL && localState.PartyId == remoteState.PartyId;
    }

    public static bool AreSameParty(uint firstNetId, uint secondNetId)
    {
        if (firstNetId == 0U || secondNetId == 0U)
            return false;
        if (firstNetId == secondNetId)
            return true;

        PartyClientState first;
        PartyClientState second;
        return TryGetClientState(firstNetId, out first) &&
               TryGetClientState(secondNetId, out second) &&
               first.PartyId != 0UL && first.PartyId == second.PartyId;
    }

    public static bool TryGetClientPartyState(uint netId, out PartyClientState state)
    {
        return TryGetClientState(netId, out state);
    }

    public static PartyClientState[] GetClientRosterSnapshot()
    {
        if (instance == null)
            return new PartyClientState[0];

        // Pure clients use the last server snapshot. A listen-host can additionally
        // read the same authoritative server dictionary immediately, before its local
        // loopback snapshot has been processed. Merge by netId so both paths behave
        // identically once the message arrives.
        Dictionary<uint, PartyClientState> merged = new Dictionary<uint, PartyClientState>();
        foreach (KeyValuePair<uint, PartyClientState> pair in instance.clientStates)
            merged[pair.Key] = CloneClientState(pair.Value);

        if (NetworkServer.active)
        {
            foreach (uint netId in instance.serverMembers.Keys)
            {
                PartyClientState serverState;
                if (instance.TryBuildClientStateFromServer(netId, out serverState))
                    merged[netId] = serverState;
            }
        }

        return merged.Values
            .Where(s => s != null)
            .OrderBy(s => s.PlayerName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.NetId)
            .Select(CloneClientState)
            .ToArray();
    }

    static PartyClientState CloneClientState(PartyClientState source)
    {
        if (source == null)
            return null;

        return new PartyClientState
        {
            NetId = source.NetId,
            PartyId = source.PartyId,
            LeaderNetId = source.LeaderNetId,
            TimeEpoch = source.TimeEpoch,
            QuestMode = source.QuestMode,
            LeaderControlsTime = source.LeaderControlsTime,
            RestTogether = source.RestTogether,
            TravelTogether = source.TravelTogether,
            MemberCount = source.MemberCount,
            PlayerName = source.PlayerName,
        };
    }

    static bool TryGetClientState(uint netId, out PartyClientState state)
    {
        state = null;
        if (netId == 0U || instance == null)
            return false;

        if (instance.clientStates.TryGetValue(netId, out state) && state != null)
            return true;

        // Listen-host UI can query authoritative server state before its own local
        // snapshot message has looped back through Mirror.
        if (NetworkServer.active)
            return instance.TryBuildClientStateFromServer(netId, out state);

        return false;
    }

    bool TryBuildClientStateFromServer(uint netId, out PartyClientState state)
    {
        state = null;

        ServerMember member;
        ServerParty party;
        if (!serverMembers.TryGetValue(netId, out member) || member == null ||
            !serverParties.TryGetValue(member.partyId, out party) || party == null)
            return false;

        state = new PartyClientState
        {
            NetId = netId,
            PartyId = party.id,
            LeaderNetId = party.leaderNetId,
            TimeEpoch = party.timeEpoch,
            QuestMode = party.questMode,
            LeaderControlsTime = party.leaderControlsTime,
            RestTogether = party.restTogether,
            TravelTogether = party.travelTogether,
            MemberCount = party.members.Count,
            PlayerName = ResolveServerPlayerName(netId),
        };
        return true;
    }

    static uint GetLocalNetId()
    {
        try
        {
            uint id = global::PlayerMultiplayer.GetLocalPlayerNetIdSafe();
            if (id != 0U)
                return id;
        }
        catch { }

        try
        {
            if (NetworkClient.localPlayer != null)
                return NetworkClient.localPlayer.netId;
        }
        catch { }

        return 0U;
    }

    // -------------------------------------------------------------------------
    // Travel Together: vanilla travel-map trips only. The original traveler
    // performs ordinary fast travel. Each nearby follower is offered a choice,
    // quoted their own fare, and receives the normal cautious-travel recovery
    // after acceptance. Only the original trip advances the party clock.
    // -------------------------------------------------------------------------

    void HandlePreVanillaTravel(DaggerfallTravelPopUp popup)
    {
        activeLocalTravelRequestId = 0U;
        activeLocalTravelStartClock = 0UL;
        activeLocalTravelMinutes = 0;
        localVanillaTravelInProgress = false;
        // Party-member rendezvous popups and alternative travel mods are out of
        // scope. A vanilla world-map travel popup owns a TravelWindow.
        if (popup == null || !NetworkClient.isConnected ||
            !NetworkClient.ready || autoFollowingTravel)
            return;
        if (popup.TravelWindow == null)
        {
            // Direct "travel to party member" uses a separate window and is not
            // Travel Together. This is intentionally an ordinary-trip-only hook.
            Debug.Log("[PartyTravelTogether] Fast-travel event has no world-map popup, skipped.");
            return;
        }
        uint selfId = GetLocalNetId();
        // A party rule update may arrive after the player opened the map. Do not
        // silently suppress the travel request based on a stale client snapshot.
        // The server checks live party membership and Travel Together permission.
        if (selfId == 0U || localRestSessionId != 0UL || remoteRestSessionId != 0UL)
        {
            Debug.Log("[PartyTravelTogether] Initiator start skipped: netId=" + selfId +
                " localRest=" + localRestSessionId + " remoteRest=" + remoteRestSessionId);
            return;
        }
        var self = PlayerMultiplayer.GetLocalPlayer();
        if (self == null || self.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
        {
            Debug.Log("[PartyTravelTogether] Initiator start skipped: player object missing or not alive, id=" + selfId);
            return;
        }
        localTravelRequestSerial++;
        if (localTravelRequestSerial == 0U) localTravelRequestSerial++;
        activeLocalTravelRequestId = localTravelRequestSerial;
        activeLocalTravelPixelX = popup.EndPos.X;
        activeLocalTravelPixelY = popup.EndPos.Y;
        localVanillaTravelInProgress = true;
        // The absolute target makes this idempotent even if the regular TimeCatcher
        // path already advanced the leader's clock. Never add a duration twice.
        if (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.WorldTime != null)
            activeLocalTravelStartClock = DaggerfallUnity.Instance.WorldTime.Now.ToSeconds();
        activeLocalTravelMinutes = QuoteLocalTravelMinutes(popup);
        try
        {
            GameManager travelGame = GameManager.Instance;
            if (travelGame != null && travelGame.GuildManager != null)
            {
                var fare = new TravelTimeCalculator();
                var inventory = travelGame.PlayerEntity.Items;
                bool horse = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                    (int)DaggerfallWorkshop.Game.Items.Transportation.Horse);
                bool cart = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                    (int)DaggerfallWorkshop.Game.Items.Transportation.Small_cart);
                bool ship = DaggerfallWorkshop.Game.Banking.DaggerfallBankManager.OwnsShip ||
                    travelGame.GuildManager.FreeShipTravel();
                // Ocean-pixel information depends on calculating travel time using this instance.
                int minutes = fare.CalculateTravelTime(popup.EndPos, popup.SpeedCautious,
                    popup.SleepModeInn, popup.TravelShip, horse, cart);
                minutes = travelGame.GuildManager.FastTravel(minutes);
                fare.CalculateTripCost(minutes, popup.SleepModeInn, ship, popup.TravelShip);
                bool freeInns = travelGame.GuildManager.GetGuild(
                    DaggerfallConnect.Arena2.FactionFile.GuildGroups.KnightlyOrder).FreeTavernRooms();
                Debug.Log("[PartyTravelTogether][Fare] Initiator: inns=" + popup.SleepModeInn +
                    " cautious=" + popup.SpeedCautious + " ship=" + popup.TravelShip +
                    " travelMinutes=" + minutes + " totalGold=" + fare.TotalCost +
                    " looseCoins=" + fare.PiecesCost + " freeKnightInns=" + freeInns);
            }
        }
        catch (Exception ex) { Debug.LogWarning("[PartyTravelTogether][Fare] Initiator quote unavailable: " + ex.Message); }
        string destinationName = string.Empty;
        try
        {
            var summary = popup.TravelWindow.LocationSummary;
            DFLocation location;
            if (DaggerfallUnity.Instance.ContentReader.GetLocation(
                    summary.RegionIndex, summary.MapIndex, out location))
                destinationName = location.Name;
            if (TextManager.Instance != null)
                destinationName = TextManager.Instance.GetLocalizedLocationName(
                    summary.MapID, destinationName);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PartyTravelTogether] Could not read destination name: " + ex.Message);
        }
        Debug.Log("[PartyTravelTogether] Initiator sending request=" + activeLocalTravelRequestId +
            " self=" + selfId + " destPixel=" + activeLocalTravelPixelX + "," +
            activeLocalTravelPixelY + " minutes=" + activeLocalTravelMinutes);
        NetworkClient.Send(new PartyTravelStartRequest
        {
            requestId = activeLocalTravelRequestId,
            destinationPixelX = activeLocalTravelPixelX,
            destinationPixelY = activeLocalTravelPixelY,
            destinationName = destinationName,
            speedCautious = popup.SpeedCautious,
            sleepModeInn = popup.SleepModeInn,
            travelShip = popup.TravelShip,
            startClockSeconds = activeLocalTravelStartClock,
            travelMinutes = activeLocalTravelMinutes,
        });
    }

    // Mirrors the normal popup's time calculation before vanilla advances the
    // world clock. The server uses it only as an absolute, bounded fallback.
    static int QuoteLocalTravelMinutes(DaggerfallTravelPopUp popup)
    {
        if (popup == null || popup.EndPos == null || GameManager.Instance == null)
            return 0;
        GameManager gm = GameManager.Instance;
        if (gm.PlayerEntity == null || gm.GuildManager == null || gm.TransportManager == null)
            return 0;
        try
        {
            var inventory = gm.PlayerEntity.Items;
            bool horse = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                (int)DaggerfallWorkshop.Game.Items.Transportation.Horse);
            bool cart = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                (int)DaggerfallWorkshop.Game.Items.Transportation.Small_cart);
            TravelTimeCalculator calculator = new TravelTimeCalculator();
            int mins = calculator.CalculateTravelTime(popup.EndPos,
                popup.SpeedCautious, popup.SleepModeInn, popup.TravelShip, horse, cart);
            return Mathf.Clamp(gm.GuildManager.FastTravel(mins), 0, 525600);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PartyTravelTogether] Travel-time quote failed: " + ex);
            return 0;
        }
    }

    void HandlePostVanillaTravel()
    {
        if (!localVanillaTravelInProgress || activeLocalTravelRequestId == 0U)
            return;
        uint requestId = activeLocalTravelRequestId;
        int pixelX = activeLocalTravelPixelX, pixelY = activeLocalTravelPixelY;
        activeLocalTravelStartClock = 0UL;
        activeLocalTravelMinutes = 0;
        activeLocalTravelRequestId = 0U;
        localVanillaTravelInProgress = false;
        StartCoroutine(SendTravelArrivalAfterStreaming(requestId, pixelX, pixelY));
    }

    IEnumerator SendTravelArrivalAfterStreaming(uint requestId, int pixelX, int pixelY)
    {
        // Vanilla RaiseOnPostFastTravel happens immediately after starting the
        // asynchronous exterior rebuild; do not read the old GPS coordinates.
        StreamingWorld world = GameManager.Instance != null ? GameManager.Instance.StreamingWorld : null;
        float deadline = Time.realtimeSinceStartup + 30f;
        yield return null;
        while (world != null && world.IsWorldUpdateRunning &&
               Time.realtimeSinceStartup < deadline)
            yield return null;
        if (world == null || world.IsWorldUpdateRunning)
            yield break;
        yield return new WaitForEndOfFrame();
        if (!NetworkClient.isConnected || !NetworkClient.ready || GameManager.Instance == null ||
            GameManager.Instance.PlayerGPS == null || GameManager.Instance.PlayerObject == null)
            yield break;
        // Completion belongs to an already-started trip. Do not drop its arrival
        // because the client party snapshot arrived late or was refreshed mid-trip.
        // The server validates the trip ID, membership, rules and destination.
        Debug.Log("[PartyTravelTogether] Initiator sending arrival for request=" + requestId +
            " pixel=" + pixelX + "," + pixelY);
        NetworkClient.Send(new PartyTravelArrivedRequest
        {
            requestId = requestId,
            destinationPixelX = pixelX,
            destinationPixelY = pixelY,
            worldX = GameManager.Instance.PlayerGPS.WorldX,
            worldZ = GameManager.Instance.PlayerGPS.WorldZ,
            worldY = GameManager.Instance.PlayerObject.transform.position.y,
        });
    }

    static string TravelSessionKey(ulong partyId, uint initiatorId, uint requestId)
    {
        return partyId.ToString() + ":" + initiatorId.ToString() + ":" + requestId.ToString();
    }

    void RemoveTravelSessionsForParty(ulong partyId)
    {
        if (serverTravelSessions.Count == 0) return;
        List<string> remove = null;
        foreach (KeyValuePair<string, ServerTravelSession> entry in serverTravelSessions)
        {
            if (entry.Value.partyId != partyId) continue;
            if (remove == null) remove = new List<string>();
            remove.Add(entry.Key);
        }
        if (remove != null)
            foreach (string key in remove) serverTravelSessions.Remove(key);
    }

    void HandleServerTravelStart(NetworkConnection connection, PartyTravelStartRequest request)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null ||
            request.requestId == 0U || request.destinationPixelX < 0 || request.destinationPixelY < 0 ||
            request.destinationPixelX > 999 || request.destinationPixelY > 999)
        {
            Debug.LogWarning("[PartyTravelTogether] Departure rejected: invalid network sender or destination pixel.");
            return;
        }
        uint senderId = connection.identity.netId;
        ServerMember sender;
        ServerParty party;
        if (!serverMembers.TryGetValue(senderId, out sender) || !TryGetParty(sender, out party) ||
            !party.travelTogether || party.members.Count < 2)
        {
            Debug.LogWarning("[PartyTravelTogether] Departure rejected: traveler=" + senderId +
                " is not in a multiplayer party with Travel Together enabled.");
            return;
        }
        PlayerMultiplayer origin = GetServerPlayer(senderId);
        if (origin == null || origin.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
        {
            Debug.LogWarning("[PartyTravelTogether] Departure rejected: traveler=" + senderId +
                " server character is missing or not alive.");
            return;
        }
        // Only an UNFINISHED departure blocks another departure. A completed trip
        // still retains its invitations for late Yes responses for up to 90 seconds.
        foreach (ServerTravelSession running in serverTravelSessions.Values)
        {
            if (running.partyId == party.id && running.timeEpoch == party.timeEpoch &&
                !running.hasArrived && running.expiresRealtime > Time.unscaledTime)
            {
                Debug.LogWarning("[PartyTravelTogether] Departure rejected: unfinished trip " +
                    running.requestId + " by " + running.initiator + " is still in progress.");
                return;
            }
        }
        ServerTravelSession trip = new ServerTravelSession
        {
            partyId = party.id,
            timeEpoch = party.timeEpoch,
            initiator = senderId,
            requestId = request.requestId,
            pixelX = request.destinationPixelX,
            pixelY = request.destinationPixelY,
            destinationName = CleanTravelDestinationName(request.destinationName,
                request.destinationPixelX, request.destinationPixelY),
            speedCautious = request.speedCautious,
            sleepModeInn = request.sleepModeInn,
            travelShip = request.travelShip,
            startClockSeconds = request.startClockSeconds,
            travelMinutes = Mathf.Clamp(request.travelMinutes, 0, 525600),
            expiresRealtime = Time.unscaledTime + TravelTogetherSessionTimeout,
        };
        serverTravelSessions[TravelSessionKey(trip.partyId, senderId, request.requestId)] = trip;
        foreach (uint id in party.members)
        {
            if (id == senderId) continue;
            ServerMember other;
            if (!serverMembers.TryGetValue(id, out other) || other == null ||
                other.connection == null || !other.connection.isReady)
            {
                Debug.Log("[PartyTravelTogether] Skipped member=" + id +
                    " because server connection is not ready.");
                continue;
            }
            PlayerMultiplayer target = GetServerPlayer(id);
            if (!EligibleForNearbyPartyAction(origin, target, TravelTogetherRadiusMeters))
            {
                PositionMultiplayer a = origin.GetComponent<PositionMultiplayer>();
                PositionMultiplayer b = target != null ? target.GetComponent<PositionMultiplayer>() : null;
                Debug.Log("[PartyTravelTogether] Skipped member=" + id +
                    " as ineligible at departure. sourceKnown=" +
                    (a != null && a.EnemyInterestLocationKnown) + " targetKnown=" +
                    (b != null && b.EnemyInterestLocationKnown) +
                    " sourceState=" + (a != null ? a.EnemyInterestState.ToString() : "missing") +
                    " targetState=" + (b != null ? b.EnemyInterestState.ToString() : "missing") +
                    " dxDf=" + (a != null && b != null ? (a.x - b.x).ToString() : "?") +
                    " dzDf=" + (a != null && b != null ? (a.z - b.z).ToString() : "?"));
                continue;
            }
            trip.invited.Add(id);
            other.connection.Send(new PartyTravelOfferMessage
            {
                partyId = trip.partyId,
                timeEpoch = trip.timeEpoch,
                initiatorId = senderId,
                requestId = trip.requestId,
                destinationPixelX = trip.pixelX,
                destinationPixelY = trip.pixelY,
                destinationName = trip.destinationName,
                initiatorName = ResolveServerPlayerName(senderId),
                speedCautious = trip.speedCautious,
                sleepModeInn = trip.sleepModeInn,
                travelShip = trip.travelShip,
            });
        }
        Debug.Log("[PartyTravelTogether] Invited party=" + trip.partyId +
            " initiator=" + senderId + " eligible=" + trip.invited.Count);
    }

    static string CleanTravelDestinationName(string name, int x, int y)
    {
        name = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (name.Length > 72) name = name.Substring(0, 72);
        if (string.IsNullOrEmpty(name))
            name = "map location (" + x + ", " + y + ")";
        return name;
    }

    void HandleServerTravelResponse(NetworkConnection connection, PartyTravelResponseRequest response)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null ||
            response.requestId == 0U)
            return;
        uint responderId = connection.identity.netId;
        ServerMember responder;
        ServerParty party;
        ServerTravelSession trip;
        if (!serverMembers.TryGetValue(responderId, out responder) ||
            !TryGetParty(responder, out party) ||
            party.id != response.partyId || !party.travelTogether ||
            !serverTravelSessions.TryGetValue(
                TravelSessionKey(response.partyId, response.initiatorId, response.requestId), out trip) ||
            trip.timeEpoch != party.timeEpoch ||
            trip.initiator != response.initiatorId || trip.requestId != response.requestId ||
            !trip.invited.Contains(responderId) ||
            Time.unscaledTime > trip.expiresRealtime)
            return;
        // A response can only act on a recipient snapshotted at departure.
        // Once accepted, a stray later No cannot retract a travel already dispatched.
        if (!response.accepted)
        {
            if (!trip.accepted.Contains(responderId))
                trip.invited.Remove(responderId);
            return;
        }
        trip.accepted.Add(responderId);
        if (trip.hasArrived)
        {
            TrySendAcceptedTravelFollower(party, trip, responderId);
            TrySendTravelLeaderClockAdvance(party, trip);
        }
    }

    void HandleServerTravelArrived(NetworkConnection connection, PartyTravelArrivedRequest request)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null ||
            request.requestId == 0U || request.worldX <= 0 || request.worldZ <= 0 ||
            float.IsNaN(request.worldY) || float.IsInfinity(request.worldY))
            return;
        uint senderId = connection.identity.netId;
        ServerMember sender;
        ServerParty party;
        ServerTravelSession trip;
        if (!serverMembers.TryGetValue(senderId, out sender) || !TryGetParty(sender, out party) ||
            !party.travelTogether || !serverTravelSessions.TryGetValue(
                TravelSessionKey(party.id, senderId, request.requestId), out trip) ||
            trip.initiator != senderId || trip.requestId != request.requestId ||
            trip.timeEpoch != party.timeEpoch || Time.unscaledTime > trip.expiresRealtime ||
            trip.pixelX != request.destinationPixelX || trip.pixelY != request.destinationPixelY ||
            trip.hasArrived)
            return;
        // No arbitrary coordinates: validate against the departure destination.
        var actualPixel = MapsFile.WorldCoordToMapPixel(request.worldX, request.worldZ);
        if (Mathf.Abs(actualPixel.X - trip.pixelX) > 1 ||
            Mathf.Abs(actualPixel.Y - trip.pixelY) > 1)
        {
            Debug.LogWarning("[PartyTravelTogether] Invalid arrival destination from " + senderId);
            serverTravelSessions.Remove(TravelSessionKey(party.id, senderId, request.requestId));
            return;
        }
        trip.hasArrived = true;
        trip.arrivalWorldX = request.worldX;
        trip.arrivalWorldZ = request.worldZ;
        trip.arrivalWorldY = request.worldY;
        // Keep the session for late acceptances, but never dispatch twice.
        foreach (uint id in trip.accepted)
            TrySendAcceptedTravelFollower(party, trip, id);
        TrySendTravelLeaderClockAdvance(party, trip);
    }

    void TrySendTravelLeaderClockAdvance(ServerParty party, ServerTravelSession trip)
    {
        if (party == null || trip == null || !trip.hasArrived || trip.clockAdvanceSent ||
            trip.partyId != party.id || trip.timeEpoch != party.timeEpoch ||
            !party.travelTogether || trip.startClockSeconds == 0UL || trip.travelMinutes <= 0)
            return;
        // With leader-only time, a follower may advance the party clock only if
        // the leader actually accepted this trip. In Any-member mode, any completed
        // initiator trip may advance it. The destination is already validated.
        if (party.leaderControlsTime && trip.initiator != party.leaderNetId &&
            !trip.accepted.Contains(party.leaderNetId))
            return;
        ServerMember leader;
        if (!serverMembers.TryGetValue(party.leaderNetId, out leader) || leader == null ||
            leader.connection == null || !leader.connection.isReady ||
            leader.partyId != party.id)
            return;
        ulong increment = (ulong)trip.travelMinutes * 60UL;
        if (trip.startClockSeconds > ulong.MaxValue - increment) return;
        trip.clockAdvanceSent = true;
        leader.connection.Send(new PartyTravelAdvanceLeaderClock
        {
            partyId = party.id,
            timeEpoch = party.timeEpoch,
            leaderNetId = party.leaderNetId,
            initiatorId = trip.initiator,
            requestId = trip.requestId,
            targetSeconds = trip.startClockSeconds + increment,
        });
    }

    void ApplyTravelLeaderClockAdvance(PartyTravelAdvanceLeaderClock message)
    {
        PartyClientState state;
        uint self = GetLocalNetId();
        if (message.requestId == 0U || message.partyId == 0UL ||
            message.targetSeconds == 0UL || self == 0U ||
            !TryGetClientState(self, out state) || state == null ||
            state.PartyId != message.partyId || state.TimeEpoch != message.timeEpoch ||
            state.LeaderNetId != self || message.leaderNetId != self ||
            DaggerfallUnity.Instance == null ||
            (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress))
            return;
        // The initiator may already have advanced time via DFU's vanilla popup
        // or TimeCatcher's Any-member proposal. Never apply the travel twice.
        ulong current = DaggerfallUnity.Instance.WorldTime.Now.ToSeconds();
        if (message.targetSeconds <= current) return;
        ulong delta = message.targetSeconds - current;
        if (delta > 525600UL * 60UL + 60UL) return;
        DaggerfallUnity.Instance.WorldTime.Now.RaiseTime((int)delta);
        TimeCatcher.NotifyLocalRestHourCompleted();
        Debug.Log("[PartyTravelTogether] Applied missing shared travel time of " +
            delta + " seconds for trip " + message.requestId);
    }

    void TrySendAcceptedTravelFollower(ServerParty party, ServerTravelSession trip, uint id)
    {
        if (!trip.hasArrived || !trip.invited.Contains(id) ||
            !trip.accepted.Contains(id) || trip.dispatched.Contains(id) ||
            Time.unscaledTime > trip.expiresRealtime || party.id != trip.partyId ||
            party.timeEpoch != trip.timeEpoch || !party.travelTogether)
            return;
        ServerMember member;
        if (!serverMembers.TryGetValue(id, out member) || member == null ||
            member.partyId != party.id || member.connection == null || !member.connection.isReady)
            return;
        var player = GetServerPlayer(id);
        if (player == null || player.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
            return;
        trip.dispatched.Add(id);
        member.connection.Send(new PartyTravelFollowMessage
        {
            partyId = trip.partyId,
            timeEpoch = trip.timeEpoch,
            initiatorId = trip.initiator,
            requestId = trip.requestId,
            worldX = trip.arrivalWorldX,
            worldZ = trip.arrivalWorldZ,
            worldY = trip.arrivalWorldY,
            speedCautious = trip.speedCautious,
            sleepModeInn = trip.sleepModeInn,
            travelShip = trip.travelShip,
        });
    }

    void QueueClientTravelOffer(PartyTravelOfferMessage offer)
    {
        // Mirror callbacks may run before UI is ready. Present on the next Update.
        if (offer.requestId != 0U)
        {
            Debug.Log("[PartyTravelTogether] Received offer for request=" + offer.requestId +
                " initiator=" + offer.initiatorId + " party=" + offer.partyId);
            queuedTravelOffers.Enqueue(offer);
        }
    }

    // Reuse DFU's exact travel cost formula, evaluated with the follower's own
    // transport items, guild privileges, starting map pixel and available gold.
    static bool TryQuoteFollowerFare(PartyTravelOfferMessage offer,
        out int totalCost, out int piecesCost)
    {
        totalCost = piecesCost = 0;
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.PlayerEntity == null || gm.GuildManager == null ||
            gm.TransportManager == null || gm.PlayerGPS == null)
            return false;
        try
        {
            var inventory = gm.PlayerEntity.Items;
            bool hasHorse = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                (int)DaggerfallWorkshop.Game.Items.Transportation.Horse);
            bool hasCart = inventory.Contains(DaggerfallWorkshop.Game.Items.ItemGroups.Transportation,
                (int)DaggerfallWorkshop.Game.Items.Transportation.Small_cart);
            bool hasShip = DaggerfallWorkshop.Game.Banking.DaggerfallBankManager.OwnsShip ||
                gm.GuildManager.FreeShipTravel();
            TravelTimeCalculator calc = new TravelTimeCalculator();
            int minutes = calc.CalculateTravelTime(new DFPosition(
                    offer.destinationPixelX, offer.destinationPixelY),
                offer.speedCautious, offer.sleepModeInn, offer.travelShip,
                hasHorse, hasCart);
            minutes = gm.GuildManager.FastTravel(minutes);
            calc.CalculateTripCost(minutes, offer.sleepModeInn, hasShip, offer.travelShip);
            totalCost = calc.TotalCost;
            piecesCost = calc.PiecesCost;
            bool freeKnightInns = gm.GuildManager.GetGuild(
                DaggerfallConnect.Arena2.FactionFile.GuildGroups.KnightlyOrder).FreeTavernRooms();
            Debug.Log("[PartyTravelTogether][Fare] Follower: inns=" + offer.sleepModeInn +
                " cautious=" + offer.speedCautious + " ship=" + offer.travelShip +
                " travelMinutes=" + minutes + " totalGold=" + totalCost +
                " looseCoins=" + piecesCost + " freeKnightInns=" + freeKnightInns);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PartyTravelTogether] Unable to quote follower's fare: " + ex);
            return false;
        }
    }

    static bool CanAffordFollowerFare(int totalCost, int piecesCost)
    {
        var gm = GameManager.Instance;
        return gm != null && gm.PlayerEntity != null &&
            gm.PlayerEntity.GetGoldAmount() >= totalCost &&
            gm.PlayerEntity.GoldPieces >= piecesCost;
    }

    void ShowTravelOfferPopup(PartyTravelOfferMessage offer)
    {
        uint selfId = GetLocalNetId();
        PartyClientState state;
        var player = PlayerMultiplayer.GetLocalPlayer();
        if (selfId == 0U || !TryGetClientState(selfId, out state) || state == null ||
            !state.TravelTogether || state.PartyId != offer.partyId ||
            state.TimeEpoch != offer.timeEpoch ||
            player == null || player.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive ||
            autoFollowingTravel || localVanillaTravelInProgress ||
            localRestSessionId != 0UL || remoteRestSessionId != 0UL ||
            pendingAcceptedTravel != null)
        {
            SendTravelResponse(offer, false);
            return;
        }
        int totalCost, piecesCost;
        if (!TryQuoteFollowerFare(offer, out totalCost, out piecesCost))
        {
            SendTravelResponse(offer, false);
            return;
        }
        IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
        if (uiManager == null) return;
        travelOfferPopupOpen = true;
        bool answered = false;
        string name = string.IsNullOrEmpty(offer.initiatorName) ? "A party member" : offer.initiatorName;
        string options = (offer.speedCautious ? "Cautious" : "Reckless") + " travel, " +
            (offer.sleepModeInn ? "sleep at inns" : "camp outside") + ", " +
            (offer.travelShip ? "ship where possible" : "travel by land");
        string price = "Travel cost: " + totalCost + " gold";
        DaggerfallMessageBox popup = new DaggerfallMessageBox(uiManager, uiManager.TopWindow, true);
        popup.SetText(new string[]
        {
            name + " is fast traveling to " +
                CleanTravelDestinationName(offer.destinationName,
                    offer.destinationPixelX, offer.destinationPixelY) + ".",
            "",
            options + ".",
            price,
            "",
            "Travel together?",
        });
        popup.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
        popup.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
        popup.OnButtonClick += (sender, button) =>
        {
            if (answered) return;
            answered = true;
            sender.CloseWindow();
            if (button != DaggerfallMessageBox.MessageBoxButtons.Yes)
            {
                SendTravelResponse(offer, false);
                return;
            }
            if (!CanAffordFollowerFare(totalCost, piecesCost))
            {
                SendTravelResponse(offer, false);
                DaggerfallUI.MessageBox("Not enough gold to join this fast-travel trip.");
                return;
            }
            pendingAcceptedTravel = new LocalAcceptedTravel
            {
                partyId = offer.partyId,
                timeEpoch = offer.timeEpoch,
                initiatorId = offer.initiatorId,
                requestId = offer.requestId,
                totalCost = totalCost,
                piecesCost = piecesCost,
                speedCautious = offer.speedCautious,
                sleepModeInn = offer.sleepModeInn,
                travelShip = offer.travelShip,
                acceptedAt = Time.unscaledTime,
            };
            SendTravelResponse(offer, true);
            DaggerfallUI.AddHUDText("Travel accepted. Joining when your party arrives.", 3f);
        };
        popup.OnClose += () =>
        {
            travelOfferPopupOpen = false;
            if (!answered)
            {
                answered = true;
                SendTravelResponse(offer, false);
            }
        };
        uiManager.PushWindow(popup);
    }

    static void SendTravelResponse(PartyTravelOfferMessage offer, bool accepted)
    {
        if (NetworkClient.isConnected && NetworkClient.ready)
            NetworkClient.Send(new PartyTravelResponseRequest
            {
                partyId = offer.partyId,
                initiatorId = offer.initiatorId,
                requestId = offer.requestId,
                accepted = accepted,
            });
    }

    void ApplyClientTravelFollow(PartyTravelFollowMessage notice)
    {
        uint selfId = GetLocalNetId();
        PartyClientState state;
        var player = PlayerMultiplayer.GetLocalPlayer();
        LocalAcceptedTravel accepted = pendingAcceptedTravel;
        if (accepted == null || accepted.requestId != notice.requestId ||
            accepted.partyId != notice.partyId || accepted.initiatorId != notice.initiatorId ||
            accepted.timeEpoch != notice.timeEpoch ||
            accepted.speedCautious != notice.speedCautious ||
            accepted.sleepModeInn != notice.sleepModeInn ||
            accepted.travelShip != notice.travelShip ||
            selfId == 0U || selfId == notice.initiatorId ||
            !TryGetClientState(selfId, out state) || state == null ||
            state.PartyId != notice.partyId || state.TimeEpoch != notice.timeEpoch ||
            !state.TravelTogether || player == null ||
            player.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive ||
            autoFollowingTravel || localVanillaTravelInProgress ||
            localRestSessionId != 0UL || remoteRestSessionId != 0UL)
            return;
        if (GameManager.Instance == null || GameManager.Instance.StreamingWorld == null ||
            GameManager.Instance.PlayerEnterExit == null ||
            !CanAffordFollowerFare(accepted.totalCost, accepted.piecesCost))
        {
            pendingAcceptedTravel = null;
            DaggerfallUI.MessageBox("Cannot join party travel. The trip fare is no longer available.");
            return;
        }
        pendingAcceptedTravel = null;
        StartCoroutine(FollowVanillaPartyTravel(notice, accepted));
    }

    IEnumerator FollowVanillaPartyTravel(PartyTravelFollowMessage notice, LocalAcceptedTravel accepted)
    {
        autoFollowingTravel = true;
        bool teleported = false;
        try
        {
            var game = GameManager.Instance;
            if (DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
            {
                DaggerfallUI.AddHUDText("Traveling with your party.", 3f);
                DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();
                DaggerfallUI.Instance.PopToHUD();
            }
            PlayerEnterExit enterExit = game.PlayerEnterExit;
            bool startedInsideDungeon = enterExit.IsPlayerInsideDungeon;
            if (enterExit.IsPlayerInsideDungeon)
                enterExit.EmergencyExitDungeonForNetworkChange("travel-together");
            else if (enterExit.IsPlayerInsideBuilding)
                enterExit.EmergencyExitBuildingForNetworkChange("travel-together");
            if (game.TransportManager.IsOnShip())
                SaveLoadManager.CacheScene(game.StreamingWorld.SceneName);
            game.StreamingWorld.RestoreWorldCompensationHeight(0);
            game.StreamingWorld.TeleportToWorldCoordinates(notice.worldX, notice.worldZ, notice.worldY);
            teleported = true;
            // Match DFU's normal payment split, only after the destination transfer
            // has successfully been started. Declined/failed trips pay nothing.
            game.PlayerEntity.GoldPieces -= accepted.piecesCost;
            game.PlayerEntity.DeductGoldAmount(accepted.totalCost - accepted.piecesCost);
            if (notice.speedCautious)
            {
                // Mirrors vanilla cautious fast travel. A dungeon departure must
                // not become a free health refill during a network-only transition.
                if (!startedInsideDungeon)
                    game.PlayerEntity.CurrentHealth = game.PlayerEntity.MaxHealth;
                game.PlayerEntity.CurrentFatigue = game.PlayerEntity.MaxFatigue;
                if (!game.PlayerEntity.Career.NoRegenSpellPoints)
                    game.PlayerEntity.CurrentMagicka = game.PlayerEntity.MaxMagicka;
            }
            game.PlayerEntity.PreventEnemySpawns = true;
            game.PlayerEntity.RaiseSkills();
        }
        catch (Exception ex)
        {
            Debug.LogError("[PartyTravelTogether] Follower fast travel failed: " + ex);
        }
        StreamingWorld world = GameManager.Instance != null ? GameManager.Instance.StreamingWorld : null;
        float timeoutAt = Time.realtimeSinceStartup + 22f;
        yield return null;
        while (teleported && world != null && world.IsWorldUpdateRunning &&
               Time.realtimeSinceStartup < timeoutAt)
            yield return null;
        yield return new WaitForEndOfFrame();
        if (DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
            DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(0.7f);
        autoFollowingTravel = false;
    }

    void ExpireTravelSessions(float now)
    {
        if (serverTravelSessions.Count == 0) return;
        List<string> expired = null;
        foreach (var entry in serverTravelSessions)
            if (now > entry.Value.expiresRealtime)
            {
                if (expired == null) expired = new List<string>();
                expired.Add(entry.Key);
            }
        if (expired != null)
            foreach (string key in expired) serverTravelSessions.Remove(key);
    }

    // -------------------------------------------------------------------------
    // Rest Together. Party-locked, start-time eligibility, recovery per completed
    // real rest hour (not world-clock jumps), no windows for recipients.
    // -------------------------------------------------------------------------

    // In leader-only mode, a follower resting away from the leader must not
    // propose its local rest time to TimeCatcher. In any-member mode, the
    // follower can advance party time regardless of the leader's proximity.
    public static bool IsLocalRestWithoutLeader
    {
        get { return instance != null && instance.localRestWithoutLeader; }
    }

    /// <summary>Request a party rest before switching the vanilla rest window into resting mode.
    /// When the option is off, solo, or offline, vanilla rest starts immediately.</summary>
    public static void RequestLocalRestStart(Action<bool> begin)
    {
        if (begin == null) return;
        if (instance == null || !NetworkClient.isConnected || !NetworkClient.ready)
        {
            begin(true);
            return;
        }

        PartyClientState state;
        uint localId = GetLocalNetId();
        if (!TryGetClientState(localId, out state) || state == null ||
            state.MemberCount < 2 || !state.RestTogether)
        {
            begin(true);
            return;
        }

        if (instance.pendingRestStart != null || instance.localRestSessionId != 0UL ||
            instance.remoteRestSessionId != 0UL)
        {
            DaggerfallUI.AddHUDText("Your party is already resting.", 4f);
            begin(false);
            return;
        }

        PositionMultiplayer pm = NetworkClient.localPlayer != null
            ? NetworkClient.localPlayer.GetComponent<PositionMultiplayer>() : null;
        if (pm != null)
        {
            pm.ForceSendCurrentCoordinatesNow("party-rest-start");
            pm.ForceSendPartyLocationNowForRest();
        }

        instance.localRestRequestSerial++;
        if (instance.localRestRequestSerial == 0U) instance.localRestRequestSerial++;
        instance.pendingRestRequestId = instance.localRestRequestSerial;
        instance.pendingRestStart = begin;
        instance.pendingRestRequestedAt = Time.unscaledTime;
        NetworkClient.Send(new PartyRestStartRequest { requestId = instance.pendingRestRequestId });
    }

    /// <summary>Called by the vanilla rest window after it actually applies a full hour
    /// of vitals. Loitering and interrupted/unfinished hours never grant shared recovery.</summary>
    public static void LocalRestHourCompleted()
    {
        if (instance == null || instance.localRestSessionId == 0UL ||
            !NetworkClient.isConnected || !NetworkClient.ready) return;
        instance.localRestHoursSent++;
        NetworkClient.Send(new PartyRestHourRequest
        {
            sessionId = instance.localRestSessionId,
            completedHours = instance.localRestHoursSent,
        });
        TimeCatcher.NotifyLocalRestHourCompleted();
    }

    /// <summary>Always called by OnPop/EndRest, including encounter interruption.</summary>
    public static void LocalRestEnded()
    {
        if (instance == null) return;
        if (instance.localRestSessionId != 0UL && NetworkClient.isConnected && NetworkClient.ready)
            NetworkClient.Send(new PartyRestStopRequest { sessionId = instance.localRestSessionId });
        if (instance.localRestWithoutLeader)
            TimeCatcher.RefreshLeaderTimeAfterRestWithoutLeader();
        instance.localRestSessionId = 0UL;
        instance.localRestWithoutLeader = false;
        instance.localRestHoursSent = 0;
    }

    void ApplyClientRestStartReply(PartyRestStartReply reply)
    {
        if (pendingRestStart == null || reply.requestId == 0U ||
            reply.requestId != pendingRestRequestId)
        {
            // A late reply after our timeout must not strand a server-side lock.
            if (reply.approved && reply.sessionId != 0UL && NetworkClient.ready)
                NetworkClient.Send(new PartyRestStopRequest { sessionId = reply.sessionId });
            return;
        }
        Action<bool> begin = pendingRestStart;
        pendingRestStart = null;
        pendingRestRequestId = 0U;
        if (reply.approved && reply.sessionId != 0UL)
        {
            localRestSessionId = reply.sessionId;
            // Only suppress follower time proposals when the party explicitly
            // requires leader control AND the leader wasn't an eligible participant.
            // Any-member mode uses TimeCatcher's absolute-time proposals instead.
            localRestWithoutLeader = reply.leaderControlsTime && !reply.leaderPresentAtStart;
            localRestHoursSent = 0;
        }
        else if (!reply.approved)
            DaggerfallUI.AddHUDText("Your party is already resting.", 4f);
        begin(reply.approved);
    }

    static PlayerMultiplayer GetServerPlayer(uint id)
    {
        NetworkIdentity identity;
        return id != 0U && NetworkServer.spawned.TryGetValue(id, out identity) && identity != null
            ? identity.GetComponent<PlayerMultiplayer>() : null;
    }

    static bool EligibleForRest(PlayerMultiplayer origin, PlayerMultiplayer target)
    {
        return EligibleForNearbyPartyAction(origin, target,
            Mathf.Clamp(OptionsMultiplayer.restTogetherRadiusMeters, 1, 10));
    }

    static bool EligibleForNearbyPartyAction(PlayerMultiplayer origin, PlayerMultiplayer target,
        int radiusMeters)
    {
        if (origin == null || target == null || target.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
            return false;
        PositionMultiplayer a = origin.GetComponent<PositionMultiplayer>();
        PositionMultiplayer b = target.GetComponent<PositionMultiplayer>();
        if (a == null || b == null) return false;
        // Mandatory server-only location metadata stays usable with the HUD hidden.
        if (!a.EnemyInterestLocationKnown || !b.EnemyInterestLocationKnown) return false;
        var aState = a.EnemyInterestState;
        var bState = b.EnemyInterestState;
        if (aState == PositionMultiplayer.PartyLocationState.Unknown ||
            bState == PositionMultiplayer.PartyLocationState.Unknown) return false;

        bool aOutside = aState == PositionMultiplayer.PartyLocationState.ExteriorLocation ||
            aState == PositionMultiplayer.PartyLocationState.Wilderness;
        bool bOutside = bState == PositionMultiplayer.PartyLocationState.ExteriorLocation ||
            bState == PositionMultiplayer.PartyLocationState.Wilderness;
        if (aOutside && bOutside)
        {
            double dx = (double)a.x - b.x, dz = (double)a.z - b.z;
            double range = Mathf.Max(1, radiusMeters) * DfUnitsPerMeter;
            return dx * dx + dz * dz <= range * range;
        }
        if (aState != bState) return false;
        if (aState == PositionMultiplayer.PartyLocationState.BuildingInterior)
        {
            if (a.EnemyInterestBuildingKey == 0 ||
                a.EnemyInterestBuildingKey != b.EnemyInterestBuildingKey) return false;
            bool sameBuilding;
            if (a.NpcBuildingRevealMapID >= 0 && b.NpcBuildingRevealMapID >= 0)
                sameBuilding = a.NpcBuildingRevealMapID == b.NpcBuildingRevealMapID;
            else
            {
                // Stand-alone buildings must match their stable exterior entrance.
                sameBuilding = a.EnemyInterestBuildingAnchorValid && b.EnemyInterestBuildingAnchorValid &&
                    a.EnemyInterestBuildingAnchorX == b.EnemyInterestBuildingAnchorX &&
                    a.EnemyInterestBuildingAnchorZ == b.EnemyInterestBuildingAnchorZ &&
                    string.Equals(a.EnemyInterestRegion, b.EnemyInterestRegion, StringComparison.Ordinal) &&
                    string.Equals(a.EnemyInterestLocation, b.EnemyInterestLocation, StringComparison.Ordinal);
            }
            return sameBuilding && WithinInteriorRestRadius(a, b, radiusMeters);
        }
        if (aState == PositionMultiplayer.PartyLocationState.DungeonInterior)
            return !string.IsNullOrEmpty(a.EnemyInterestDungeonId) &&
                string.Equals(a.EnemyInterestDungeonId, b.EnemyInterestDungeonId, StringComparison.Ordinal) &&
                WithinInteriorRestRadius(a, b, radiusMeters);
        return false;
    }

    static bool WithinInteriorRestRadius(PositionMultiplayer a, PositionMultiplayer b, int radiusMeters)
    {
        // Interior and dungeon DF-world x/z values are entrance anchors, NOT positions
        // within those scenes. NetworkTransform player roots carry Unity-space positions.
        float range = Mathf.Max(1, radiusMeters);
        return (a.transform.position - b.transform.position).sqrMagnitude <= range * range;
    }

    void HandleServerRestStart(NetworkConnection connection, PartyRestStartRequest request)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null || request.requestId == 0U)
            return;
        uint requesterId = connection.identity.netId;
        ServerMember member;
        ServerParty party;
        if (!serverMembers.TryGetValue(requesterId, out member) || !TryGetParty(member, out party))
        {
            connection.Send(new PartyRestStartReply { requestId = request.requestId, approved = false });
            return;
        }
        ServerRestSession existing;
        if (serverRestSessions.TryGetValue(party.id, out existing))
        {
            connection.Send(new PartyRestStartReply { requestId = request.requestId, approved = false });
            return;
        }
        PlayerMultiplayer initiator = GetServerPlayer(requesterId);
        if (initiator == null || initiator.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
        {
            connection.Send(new PartyRestStartReply { requestId = request.requestId, approved = false });
            return;
        }
        // Parties that just turned the setting OFF may have an older client snapshot.
        // Approve ordinary local rest without creating a synchronized session.
        if (!party.restTogether || party.members.Count < 2)
        {
            connection.Send(new PartyRestStartReply { requestId = request.requestId, approved = true });
            return;
        }
        ServerRestSession session = new ServerRestSession
        {
            id = nextRestSessionId++,
            partyId = party.id,
            initiator = requesterId,
            leaderAtStart = party.leaderNetId,
            leaderControlsTime = party.leaderControlsTime,
            lastProgressTime = Time.unscaledTime,
        };
        if (session.id == 0UL) session.id = nextRestSessionId++;
        foreach (uint id in party.members)
        {
            if (id == requesterId) continue;
            ServerMember recipient;
            if (!serverMembers.TryGetValue(id, out recipient) || recipient.connection == null ||
                !recipient.connection.isReady || !EligibleForRest(initiator, GetServerPlayer(id))) continue;
            session.recipients.Add(id);
        }
        session.leaderPresentAtStart = session.recipients.Contains(session.leaderAtStart);
        serverRestSessions.Add(party.id, session);
        // The start reply precedes the first completed hour; the initiator's own
        // recovery still occurs in vanilla DaggerfallRestWindow.TickVitals().
        connection.Send(new PartyRestStartReply
            { requestId = request.requestId, approved = true, sessionId = session.id,
              leaderPresentAtStart = session.leaderAtStart == requesterId || session.leaderPresentAtStart,
              leaderControlsTime = session.leaderControlsTime });
        Debug.Log("[PartyRest] start party=" + party.id + " initiator=" + requesterId +
            " recipients=" + session.recipients.Count + " leaderNearby=" + session.leaderPresentAtStart);
    }

    void HandleServerRestHour(NetworkConnection connection, PartyRestHourRequest message)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null || message.sessionId == 0UL)
            return;
        uint sender = connection.identity.netId;
        ServerMember owner;
        ServerRestSession session;
        if (!serverMembers.TryGetValue(sender, out owner) ||
            !serverRestSessions.TryGetValue(owner.partyId, out session) || session.id != message.sessionId ||
            session.initiator != sender || message.completedHours <= session.lastCompletedHours ||
            message.completedHours != session.lastCompletedHours + 1 || message.completedHours > 99)
            return;
        session.lastCompletedHours = message.completedHours;
        session.lastProgressTime = Time.unscaledTime;
        foreach (uint id in session.recipients)
        {
            ServerMember recipient;
            if (!serverMembers.TryGetValue(id, out recipient) || recipient == null ||
                recipient.partyId != session.partyId || recipient.connection == null || !recipient.connection.isReady)
                continue;
            PlayerMultiplayer player = GetServerPlayer(id);
            if (player == null || player.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive)
                continue;
            recipient.connection.Send(new PartyRestHourNotice
            {
                sessionId = session.id,
                initiatorId = sender,
                completedHours = session.lastCompletedHours,
            });
        }
        // Leader-only time: a nearby leader owns the calendar even if a follower
        // starts the rest. An absent leader never has its clock advanced by rest.
        // When 'any member can advance' is selected, TimeCatcher already sends
        // absolute-time proposals, so do not advance the clock twice.
        if (session.leaderControlsTime && session.leaderAtStart != sender &&
            session.leaderPresentAtStart && session.recipients.Contains(session.leaderAtStart))
        {
            ServerMember leader;
            if (serverMembers.TryGetValue(session.leaderAtStart, out leader) && leader != null &&
                leader.partyId == session.partyId && leader.connection != null && leader.connection.isReady)
                leader.connection.Send(new PartyRestAdvanceLeaderClock
                    { sessionId = session.id, completedHours = session.lastCompletedHours });
        }
    }

    void HandleServerRestStop(NetworkConnection connection, PartyRestStopRequest request)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null) return;
        uint sender = connection.identity.netId;
        ServerMember member;
        ServerRestSession session;
        if (serverMembers.TryGetValue(sender, out member) &&
            serverRestSessions.TryGetValue(member.partyId, out session) &&
            session.initiator == sender && session.id == request.sessionId)
            EndServerRestSession(member.partyId);
    }

    void EndServerRestSession(ulong partyId)
    {
        ServerRestSession session;
        if (!serverRestSessions.TryGetValue(partyId, out session)) return;
        serverRestSessions.Remove(partyId);
        foreach (uint id in session.recipients)
        {
            ServerMember member;
            if (serverMembers.TryGetValue(id, out member) && member.partyId == partyId &&
                member.connection != null && member.connection.isReady)
                member.connection.Send(new PartyRestHourNotice
                {
                    sessionId = session.id,
                    initiatorId = session.initiator,
                    ended = true,
                });
        }
    }

    void ExpireRestSessions(float now)
    {
        foreach (ulong partyId in serverRestSessions.Keys.ToArray())
        {
            ServerRestSession session = serverRestSessions[partyId];
            if (now - session.lastProgressTime > 35f)
                EndServerRestSession(partyId);
        }
    }

    void ApplyClientRestHour(PartyRestHourNotice message)
    {
        if (message.ended)
        {
            if (remoteRestSessionId == message.sessionId)
            {
                remoteRestSessionId = 0UL;
                remoteRestInitiatorId = 0U;
                remoteRestLastCompletedHour = 0;
            }
            return;
        }
        if (message.sessionId == 0UL || message.completedHours < 1 || message.completedHours > 99 ||
            localRestSessionId != 0UL) return;
        if (remoteRestSessionId != message.sessionId)
        {
            remoteRestSessionId = message.sessionId;
            remoteRestInitiatorId = message.initiatorId;
            remoteRestLastCompletedHour = 0;
        }
        if (message.initiatorId != remoteRestInitiatorId ||
            message.completedHours <= remoteRestLastCompletedHour) return;
        // Apply once per completed hour, and only to a living local recipient.
        if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null ||
            GameManager.Instance.PlayerDeath == null || GameManager.Instance.PlayerDeath.DeathInProgress ||
            (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress)) return;
        DaggerfallWorkshop.Game.Entity.PlayerEntity player = GameManager.Instance.PlayerEntity;
        if (player.CurrentHealth <= 0) return;
        remoteRestLastCompletedHour = message.completedHours;
        player.CurrentHealth += DaggerfallWorkshop.Game.Formulas.FormulaHelper.CalculateHealthRecoveryRate(player);
        player.CurrentFatigue += DaggerfallWorkshop.Game.Formulas.FormulaHelper.CalculateFatigueRecoveryRate(player.MaxFatigue);
        player.CurrentMagicka += DaggerfallWorkshop.Game.Formulas.FormulaHelper.CalculateSpellPointRecoveryRate(player);
        player.TallySkill((short)DaggerfallConnect.DFCareer.Skills.Medical, 1);
    }

    void ApplyLeaderRestTimeAdvance(PartyRestAdvanceLeaderClock message)
    {
        if (message.sessionId == 0UL || message.completedHours < 1 || message.completedHours > 99 ||
            !IsLocalPartyLeader() || DaggerfallUnity.Instance == null ||
            (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress)) return;
        if (leaderRestLastSession != message.sessionId)
        {
            leaderRestLastSession = message.sessionId;
            leaderRestLastHour = 0;
        }
        if (message.completedHours <= leaderRestLastHour) return;
        int delta = message.completedHours - leaderRestLastHour;
        leaderRestLastHour = message.completedHours;
        DaggerfallUnity.Instance.WorldTime.Now.RaiseTime(delta * 3600);
        TimeCatcher.NotifyLocalRestHourCompleted();
    }

    // -------------------------------------------------------------------------
    // Party management UI. Kept here so the existing party window only needs one
    // small Manage button instead of owning network protocol details.
    // -------------------------------------------------------------------------

    // The Party window retains its Manage button. The top-level picker now shows
    // only categories, so adding more party rules does not crowd the member list.
    public static void OpenManagementWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous)
    {
        if (instance == null || uiManager == null) return;
        uint localNetId = GetLocalNetId();
        PartyClientState state;
        if (localNetId == 0U || !TryGetClientState(localNetId, out state))
        {
            DaggerfallUI.MessageBox("Party state is still synchronizing.");
            return;
        }

        bool leader = state.LeaderNetId == localNetId;
        List<string> labels = new List<string>();
        List<int> categories = new List<int>();
        labels.Add(leader ? "Party rules..." : "Party rules (view)...");
        categories.Add(0);
        if (leader)
        {
            labels.Add("Members & invitations...");
            categories.Add(1);
        }
        if (state.MemberCount > 1)
        {
            labels.Add("Leave party");
            categories.Add(2);
        }

        DaggerfallListPickerWindow picker = new DaggerfallListPickerWindow(uiManager, previous);
        picker.ListBox.AddItems(labels.ToArray());
        picker.OnItemPicked += (index, itemString) =>
        {
            if (index < 0 || index >= categories.Count) return;
            int category = categories[index];
            picker.CloseWindow();
            if (category == 0) OpenManagementSection(uiManager, previous, true);
            else if (category == 1) OpenManagementSection(uiManager, previous, false);
            else SendAction(ActionLeaveParty, 0U, 0UL, 0U);
        };
        uiManager.PushWindow(picker);
    }

    static void OpenManagementSection(IUserInterfaceManager uiManager,
        IUserInterfaceWindow previous, bool rules)
    {
        uint localNetId = GetLocalNetId();
        PartyClientState localState;
        if (localNetId == 0U || !TryGetClientState(localNetId, out localState))
        {
            DaggerfallUI.MessageBox("Party state is still synchronizing.");
            return;
        }
        bool leader = localState.LeaderNetId == localNetId;
        if (rules && !leader)
        {
            DaggerfallMessageBox summary = new DaggerfallMessageBox(uiManager, previous, true);
            summary.SetText(new string[]
            {
                "Current party rules:",
                "Time: " + (localState.LeaderControlsTime ? "Leader only" : "Any member"),
                "Quest sharing: " + QuestModeLabel(localState.QuestMode),
                "Rest Together: " + (localState.RestTogether ? "On" : "Off"),
                "Travel Together: " + (localState.TravelTogether ? "On" : "Off"),
                "",
                "Only the party leader can change these rules.",
                "Automatic shares new quests.",
                "Manual shares from the journal.",
                "Off disables sharing.",
            });
            summary.ClickAnywhereToClose = true;
            uiManager.PushWindow(summary);
            return;
        }

        if (rules)
        {
            OpenLivePartyRulesPicker(uiManager, previous, localState);
            return;
        }

        List<ManagementAction> actions = new List<ManagementAction>();
        if (leader)
        {
            PartyClientState[] roster = GetClientRosterSnapshot();
            for (int i = 0; i < roster.Length; i++)
            {
                PartyClientState candidate = roster[i];
                if (candidate == null || candidate.NetId == localNetId) continue;
                if (candidate.PartyId != localState.PartyId && candidate.MemberCount <= 1)
                    actions.Add(new ManagementAction { action = ActionInvite,
                        targetNetId = candidate.NetId, label = "Invite " + SafeName(candidate) });
            }
            for (int i = 0; i < roster.Length; i++)
            {
                PartyClientState member = roster[i];
                if (member == null || member.NetId == localNetId || member.PartyId != localState.PartyId)
                    continue;
                actions.Add(new ManagementAction { action = ActionPromoteLeader,
                    targetNetId = member.NetId, label = "Make leader: " + SafeName(member) });
                actions.Add(new ManagementAction { action = ActionKickMember,
                    targetNetId = member.NetId, label = "Kick: " + SafeName(member) });
            }
        }
        if (actions.Count == 0)
        {
            DaggerfallUI.MessageBox("No solo players are currently available to invite, and no other members can be managed.");
            return;
        }
        DaggerfallListPickerWindow picker = new DaggerfallListPickerWindow(uiManager, previous);
        picker.ListBox.AddItems(actions.Select(a => a.label).ToArray());
        picker.OnItemPicked += (index, itemString) =>
        {
            if (index < 0 || index >= actions.Count) return;
            ManagementAction chosen = actions[index];
            picker.CloseWindow();
            SendAction(chosen.action, chosen.targetNetId,
                (chosen.action == ActionSetQuestMode || chosen.action == ActionSetLeaderTime ||
                    chosen.action == ActionSetRestTogether || chosen.action == ActionSetTravelTogether)
                    ? localState.PartyId : 0UL, 0U);
        };
        uiManager.PushWindow(picker);
    }

    // Keep the party rules window open after a change. All three policies are
    // represented the same way: selecting a row advances to its next choice.
    // The latest server snapshot refreshes the labels, including rejected edits.
    static void OpenLivePartyRulesPicker(IUserInterfaceManager uiManager,
        IUserInterfaceWindow previous, PartyClientState initialState)
    {
        if (initialState == null) return;
        uint localNetId = GetLocalNetId();
        ulong partyId = initialState.PartyId;
        bool leaderOnly = initialState.LeaderControlsTime;
        bool restTogether = initialState.RestTogether;
        bool travelTogether = initialState.TravelTogether;
        OptionsMultiplayer.QuestSyncMode questMode = initialState.QuestMode;

        DaggerfallListPickerWindow picker = new DaggerfallListPickerWindow(uiManager, previous);
        Action updateLabels = () =>
        {
            int selectedIndex = picker.ListBox.SelectedIndex;
            picker.ListBox.ClearItems();
            picker.ListBox.AddItems(new string[]
            {
                "Time: " + (leaderOnly ? "Leader only" : "Any member"),
                "Quest sharing: " + QuestModeLabel(questMode),
                "Rest Together: " + (restTogether ? "On" : "Off"),
                "Travel Together: " + (travelTogether ? "On" : "Off"),
                "Explain party rules...",
                "Back",
            });
            picker.ListBox.SelectedIndex = Mathf.Clamp(selectedIndex, 0, 5);
        };

        // Server snapshot is authoritative. Keep the currently open picker in sync
        // with changes and remove the event handler when the picker is closed.
        Action onPartyChanged = () =>
        {
            PartyClientState latest;
            if (!TryGetClientState(localNetId, out latest) || latest == null ||
                latest.PartyId != partyId || latest.LeaderNetId != localNetId)
            {
                picker.CloseWindow();
                return;
            }
            leaderOnly = latest.LeaderControlsTime;
            questMode = latest.QuestMode;
            restTogether = latest.RestTogether;
            travelTogether = latest.TravelTogether;
            updateLabels();
        };
        PartyStateChanged += onPartyChanged;
        picker.OnClose += () => { PartyStateChanged -= onPartyChanged; };
        updateLabels();
        picker.OnItemPicked += (index, itemString) =>
        {
            if (index == 5)
            {
                picker.CloseWindow();
                return;
            }
            if (index == 4)
            {
                DaggerfallMessageBox help = new DaggerfallMessageBox(uiManager, picker, true);
                help.SetText(new string[]
                {
                    "PARTY TIME",
                    "Leader only: Leader controls the clock.",
                    "Any member: Everyone can advance time.",
                    "",
                    "QUEST SHARING",
                    "Automatic: New quests are shared.",
                    "Manual: Share Quest from the journal.",
                    "Off: No quest sharing.",
                    "",
                    "REST TOGETHER",
                    "Eligible members recover together.",
                    "Max distance: " + OptionsMultiplayer.restTogetherRadiusMeters + " metres.",
                    "Same building or dungeon instance required indoors.",
                    "",
                    "TRAVEL TOGETHER",
                    "Accept or decline within 10 metres.",
                    "Each traveler pays their own fare.",
                    "Cautious travel restores health and stamina.",
                    "The party clock advances once.",
                });
                help.ClickAnywhereToClose = true;
                uiManager.PushWindow(help);
                return;
            }
            if (index < 0 || index > 3) return;
            PartyClientState current;
            if (!TryGetClientState(localNetId, out current) || current == null ||
                current.PartyId != partyId || current.LeaderNetId != localNetId)
            {
                picker.CloseWindow();
                return;
            }

            switch (index)
            {
                case 0:
                    leaderOnly = !leaderOnly;
                    SendAction(ActionSetLeaderTime, leaderOnly ? 1U : 0U, partyId, 0U);
                    break;
                case 1:
                    questMode = (OptionsMultiplayer.QuestSyncMode)(((int)questMode + 1) % 3);
                    SendAction(ActionSetQuestMode, (uint)questMode, partyId, 0U);
                    break;
                case 2:
                    restTogether = !restTogether;
                    SendAction(ActionSetRestTogether, restTogether ? 1U : 0U, partyId, 0U);
                    break;
                case 3:
                    travelTogether = !travelTogether;
                    SendAction(ActionSetTravelTogether, travelTogether ? 1U : 0U, partyId, 0U);
                    break;
            }
            // Do not close the popup. Refresh immediately, then reconcile with
            // the authoritative snapshot when the server replies.
            updateLabels();
        };
        uiManager.PushWindow(picker);
    }

    public static string QuestModeLabel(OptionsMultiplayer.QuestSyncMode mode)
    {
        return mode == OptionsMultiplayer.QuestSyncMode.Off ? "Off" :
            mode == OptionsMultiplayer.QuestSyncMode.SharedOnly ? "Manual" : "Automatic";
    }

    public static bool TryGetLocalQuestSyncMode(out OptionsMultiplayer.QuestSyncMode mode)
    {
        mode = OptionsMultiplayer.QuestSyncMode.Full;
        PartyClientState state;
        uint local = GetLocalNetId();
        if (local == 0U || instance == null) return false;
        bool found = NetworkServer.active ? instance.TryBuildClientStateFromServer(local, out state) : TryGetClientState(local, out state);
        if (!found || state == null) return false;
        mode = state.QuestMode;
        return true;
    }

    public static bool CanChangeLocalQuestSyncMode()
    {
        PartyClientState state;
        uint local = GetLocalNetId();
        return local != 0U && TryGetClientState(local, out state) && state != null && state.LeaderNetId == local;
    }

    public static void RequestLocalQuestSyncMode(OptionsMultiplayer.QuestSyncMode mode)
    {
        PartyClientState state;
        uint local = GetLocalNetId();
        if (local == 0U || !TryGetClientState(local, out state) || state == null || state.LeaderNetId != local) return;
        SendAction(ActionSetQuestMode, (uint)mode, state.PartyId, 0U);
    }

    void ServerSetTravelTogether(ServerMember source, ulong expectedParty, uint enabled)
    {
        ServerParty party;
        if (enabled > 1U || !TryGetParty(source, out party) ||
            party.id != expectedParty || party.leaderNetId != source.netId)
            return;
        if (party.travelTogether == (enabled != 0U)) return;
        party.travelTogether = enabled != 0U;
        if (!party.travelTogether)
            RemoveTravelSessionsForParty(party.id);
        MarkServerStateDirty();
        BroadcastSnapshot();
    }

    void ServerSetRestTogether(ServerMember source, ulong expectedParty, uint enabled)
    {
        ServerParty party;
        if (enabled > 1U || !TryGetParty(source, out party) ||
            party.id != expectedParty || party.leaderNetId != source.netId)
            return;
        party.restTogether = enabled != 0U;
        if (!party.restTogether)
            EndServerRestSession(party.id);
        MarkServerStateDirty();
        BroadcastSnapshot();
        // No popup: the changed state is visible in the Manage menu and party snapshot.
    }

    void ServerSetQuestMode(ServerMember source, ulong expectedParty, uint mode)
    {
        ServerParty party;
        if (mode > 2U || !TryGetParty(source, out party) || party.id != expectedParty || party.leaderNetId != source.netId)
            return;
        if ((uint)party.questMode == mode) return;
        if (party.members.Contains(GetLocalNetId()))
            QuestNetSync.CaptureExistingQuestPolicies(party.questMode);
        party.questMode = (OptionsMultiplayer.QuestSyncMode)mode;
        // Do not touch timeEpoch: a policy edit must not restart time/weather sync.
        MarkServerStateDirty();
        BroadcastSnapshot();
        NotifyParty(party, "Quest sharing: " + QuestModeLabel(party.questMode) + ". Existing quests keep their participation.");
    }

    // Defaults are only consulted when creating a party. Live policy belongs to
    // the server's party record, including when the physical host is a follower.
    public static bool TryGetLocalLeaderControlsTime(out bool enabled)
    {
        enabled = true;
        PartyClientState state;
        uint local = GetLocalNetId();
        if (local == 0U || instance == null) return false;
        bool found = NetworkServer.active ? instance.TryBuildClientStateFromServer(local, out state) : TryGetClientState(local, out state);
        if (!found || state == null) return false;
        enabled = state.LeaderControlsTime;
        return true;
    }

    public static bool ServerLeaderControlsTime(uint playerNetId)
    {
        PartyClientState state;
        return !NetworkServer.active || instance == null ||
            !instance.TryBuildClientStateFromServer(playerNetId, out state) || state.LeaderControlsTime;
    }

    void ServerSetLeaderTime(ServerMember source, ulong expectedParty, uint enabled)
    {
        ServerParty party;
        if (enabled > 1U || !TryGetParty(source, out party) || party.id != expectedParty || party.leaderNetId != source.netId)
            return;
        bool value = enabled != 0U;
        if (party.leaderControlsTime == value) return;
        party.leaderControlsTime = value;
        // Old-mode samples/proposals must never be applied in the new mode.
        party.timeEpoch++;
        if (party.timeEpoch == 0UL) party.timeEpoch++;
        MarkServerStateDirty();
        BroadcastSnapshot();
        NotifyParty(party, value
            ? "Time: Only the party leader can advance the party's clock."
            : "Time: Any party member can advance the party's clock.");
    }

    static string SafeName(PartyClientState state)
    {
        if (state == null || string.IsNullOrEmpty(state.PlayerName) ||
            string.Equals(state.PlayerName, "Nameless", StringComparison.OrdinalIgnoreCase))
            return "Player " + (state != null ? state.NetId.ToString() : "?");

        return state.PlayerName;
    }

    static void SendAction(int action, uint targetNetId, ulong partyId, uint inviterNetId)
    {
        if (instance == null)
            return;

        PartyActionRequest request = new PartyActionRequest
        {
            action = action,
            targetNetId = targetNetId,
            partyId = partyId,
            inviterNetId = inviterNetId,
        };

        // Listen host: call the exact same server handler through its local connection.
        // Pure clients use a normal NetworkMessage. A future dedicated server has no
        // local UI and therefore never enters this branch.
        if (NetworkServer.active && NetworkServer.localConnection != null &&
            NetworkServer.localConnection.identity != null &&
            NetworkServer.localConnection.identity.netId == GetLocalNetId())
        {
            instance.HandleServerAction(NetworkServer.localConnection, request);
            return;
        }

        if (NetworkClient.ready)
            NetworkClient.Send(request);
    }

    // -------------------------------------------------------------------------
    // Server-only public helpers for later migration stages.
    // -------------------------------------------------------------------------

    public static bool ServerTryGetPartyId(uint playerNetId, out ulong partyId)
    {
        partyId = 0UL;
        if (!NetworkServer.active || instance == null)
            return false;

        ServerMember member;
        if (!instance.serverMembers.TryGetValue(playerNetId, out member) || member == null)
            return false;

        partyId = member.partyId;
        return partyId != 0UL;
    }

    // Time messages are valid only for the exact current membership/leadership epoch.
    public static bool ServerTryGetPartyTimeRoute(uint playerNetId, out ulong partyId,
        out uint leaderNetId, out ulong timeEpoch)
    {
        partyId = timeEpoch = 0UL;
        leaderNetId = 0U;
        if (!NetworkServer.active || instance == null)
            return false;
        ServerMember member;
        ServerParty party;
        if (!instance.serverMembers.TryGetValue(playerNetId, out member) || member == null ||
            !instance.serverParties.TryGetValue(member.partyId, out party) || party == null)
            return false;
        partyId = party.id;
        leaderNetId = party.leaderNetId;
        timeEpoch = party.timeEpoch;
        return partyId != 0UL && leaderNetId != 0U && timeEpoch != 0UL;
    }

    public static uint ServerGetPartyLeaderNetId(uint playerNetId)
    {
        if (!NetworkServer.active || instance == null)
            return 0U;

        ServerMember member;
        ServerParty party;
        if (!instance.serverMembers.TryGetValue(playerNetId, out member) || member == null ||
            !instance.serverParties.TryGetValue(member.partyId, out party) || party == null)
            return 0U;

        return party.leaderNetId;
    }

    public static List<NetworkConnection> ServerGetPartyConnections(uint playerNetId)
    {
        List<NetworkConnection> result = new List<NetworkConnection>();
        if (!NetworkServer.active || instance == null)
            return result;

        ServerMember source;
        ServerParty party;
        if (!instance.serverMembers.TryGetValue(playerNetId, out source) || source == null ||
            !instance.serverParties.TryGetValue(source.partyId, out party) || party == null)
            return result;

        foreach (uint memberNetId in party.members)
        {
            ServerMember member;
            if (instance.serverMembers.TryGetValue(memberNetId, out member) &&
                member != null && member.connection != null)
                result.Add(member.connection);
        }

        return result;
    }

    /// <summary>
    /// Returns true when the supplied player is currently a member of the exact
    /// authoritative server party. Intended for party-scoped gameplay routing.
    /// </summary>
    public static bool ServerIsPlayerInParty(uint playerNetId, ulong partyId)
    {
        if (!NetworkServer.active || instance == null || playerNetId == 0U || partyId == 0UL)
            return false;

        ServerMember member;
        return instance.serverMembers.TryGetValue(playerNetId, out member) &&
               member != null && member.partyId == partyId;
    }

    /// <summary>
    /// Gets the currently connected members of an exact server-owned party.
    /// Unlike ServerGetPartyConnections(playerNetId), this remains usable after
    /// quest ownership has been bound to a PartyId and the original taker leaves.
    /// </summary>
    public static List<NetworkConnection> ServerGetPartyConnectionsByPartyId(ulong partyId)
    {
        List<NetworkConnection> result = new List<NetworkConnection>();
        if (!NetworkServer.active || instance == null || partyId == 0UL)
            return result;

        ServerParty party;
        if (!instance.serverParties.TryGetValue(partyId, out party) || party == null)
            return result;

        foreach (uint memberNetId in party.members)
        {
            ServerMember member;
            if (instance.serverMembers.TryGetValue(memberNetId, out member) &&
                member != null && member.connection != null)
                result.Add(member.connection);
        }

        return result;
    }

    public static uint ServerGetPartyLeaderNetIdByPartyId(ulong partyId)
    {
        if (!NetworkServer.active || instance == null || partyId == 0UL)
            return 0U;

        ServerParty party;
        if (!instance.serverParties.TryGetValue(partyId, out party) || party == null)
            return 0U;

        return party.leaderNetId;
    }

    // -------------------------------------------------------------------------
    // Server state / actions
    // -------------------------------------------------------------------------

    void SweepServerConnections(float now)
    {
        bool changed = false;
        HashSet<uint> seen = new HashSet<uint>();

        foreach (KeyValuePair<int, NetworkConnectionToClient> entry in NetworkServer.connections)
        {
            NetworkConnection connection = entry.Value;
            if (connection == null || connection.identity == null || connection.identity.netId == 0U)
                continue;

            uint netId = connection.identity.netId;
            seen.Add(netId);

            ServerMember member;
            if (!serverMembers.TryGetValue(netId, out member) || member == null)
            {
                member = new ServerMember
                {
                    netId = netId,
                    connection = connection,
                    lastSeenRealtime = now,
                };
                serverMembers[netId] = member;
                CreateSoloParty(member);
                changed = true;
                Debug.Log("[MPParty] Registered player netId=" + netId + " in solo party=" + member.partyId);
            }
            else
            {
                member.connection = connection;
                member.lastSeenRealtime = now;
            }
        }

        List<uint> gone = null;
        foreach (KeyValuePair<uint, ServerMember> pair in serverMembers)
        {
            ServerMember member = pair.Value;
            if (member == null)
                continue;
            if (seen.Contains(pair.Key))
                continue;
            if (now - member.lastSeenRealtime < MissingConnectionGraceSeconds)
                continue;

            if (gone == null)
                gone = new List<uint>();
            gone.Add(pair.Key);
        }

        if (gone != null)
        {
            for (int i = 0; i < gone.Count; i++)
            {
                RemoveDisconnectedMember(gone[i]);
                changed = true;
            }
        }

        if (changed)
            MarkServerStateDirty();
    }

    void CreateSoloParty(ServerMember member)
    {
        if (member == null)
            return;

        ulong id = AllocatePartyId();
        ServerParty party = new ServerParty
        {
            id = id,
            leaderNetId = member.netId,
            questMode = OptionsMultiplayer.DefaultQuestSyncMode,
            leaderControlsTime = OptionsMultiplayer.timeHost,
        };
        party.members.Add(member.netId);
        serverParties[id] = party;
        member.partyId = id;
    }

    ulong AllocatePartyId()
    {
        while (nextPartyId == 0UL || serverParties.ContainsKey(nextPartyId))
            nextPartyId++;

        return nextPartyId++;
    }

    void RemoveDisconnectedMember(uint netId)
    {
        ServerMember member;
        if (!serverMembers.TryGetValue(netId, out member) || member == null)
            return;

        RemoveInvitesInvolving(netId);
        RemoveMemberFromParty(member, false);
        serverMembers.Remove(netId);
        Debug.Log("[MPParty] Removed disconnected player netId=" + netId);
    }

    void RemoveInvitesInvolving(uint netId)
    {
        List<uint> removeTargets = null;
        foreach (KeyValuePair<uint, PendingInvite> pair in pendingInvitesByTarget)
        {
            PendingInvite invite = pair.Value;
            if (pair.Key != netId && invite != null && invite.inviterNetId != netId)
                continue;

            if (removeTargets == null)
                removeTargets = new List<uint>();
            removeTargets.Add(pair.Key);
        }

        if (removeTargets != null)
            for (int i = 0; i < removeTargets.Count; i++)
                pendingInvitesByTarget.Remove(removeTargets[i]);
    }

    void RemoveMemberFromParty(ServerMember member, bool createSoloParty)
    {
        if (member == null)
            return;

        ServerParty party;
        if (member.partyId != 0UL && serverParties.TryGetValue(member.partyId, out party) && party != null)
        {
            EndServerRestSession(party.id);
            RemoveTravelSessionsForParty(party.id);
            party.members.Remove(member.netId);
            party.timeEpoch++; // Invalidate time packets from the previous membership/leadership.

            if (party.members.Count == 0)
            {
                serverParties.Remove(party.id);
            }
            else if (party.leaderNetId == member.netId || !party.members.Contains(party.leaderNetId))
            {
                party.leaderNetId = party.members.OrderBy(id => id).First();
                NotifyParty(party, ResolveServerPlayerName(party.leaderNetId) + " is now the party leader.");
            }
        }

        member.partyId = 0UL;
        if (createSoloParty)
            CreateSoloParty(member);
    }

    void HandleServerAction(NetworkConnection connection, PartyActionRequest request)
    {
        if (!NetworkServer.active || connection == null || connection.identity == null)
            return;

        uint sourceNetId = connection.identity.netId;
        if (sourceNetId == 0U)
            return;

        ServerMember source;
        if (!serverMembers.TryGetValue(sourceNetId, out source) || source == null)
        {
            source = new ServerMember
            {
                netId = sourceNetId,
                connection = connection,
                lastSeenRealtime = Time.unscaledTime,
            };
            serverMembers[sourceNetId] = source;
            CreateSoloParty(source);
            MarkServerStateDirty();
        }
        else
        {
            source.connection = connection;
            source.lastSeenRealtime = Time.unscaledTime;
        }

        switch (request.action)
        {
            case ActionSetRestTogether:
                ServerSetRestTogether(source, request.partyId, request.targetNetId);
                break;
            case ActionSetTravelTogether:
                ServerSetTravelTogether(source, request.partyId, request.targetNetId);
                break;
            case ActionSetLeaderTime:
                ServerSetLeaderTime(source, request.partyId, request.targetNetId);
                break;
            case ActionSetQuestMode:
                ServerSetQuestMode(source, request.partyId, request.targetNetId);
                break;
            case ActionInvite:
                ServerInvite(source, request.targetNetId);
                break;
            case ActionAcceptInvite:
                ServerAcceptInvite(source, request.inviterNetId, request.partyId);
                break;
            case ActionDeclineInvite:
                ServerDeclineInvite(source, request.inviterNetId, request.partyId);
                break;
            case ActionLeaveParty:
                ServerLeaveParty(source);
                break;
            case ActionKickMember:
                ServerKickMember(source, request.targetNetId);
                break;
            case ActionPromoteLeader:
                ServerPromoteLeader(source, request.targetNetId);
                break;
        }
    }

    void ServerInvite(ServerMember source, uint targetNetId)
    {
        ServerParty sourceParty;
        if (!TryGetParty(source, out sourceParty))
            return;
        if (sourceParty.leaderNetId != source.netId)
        {
            SendStatus(source, "Only the party leader can invite players.");
            return;
        }

        ServerMember target;
        ServerParty targetParty;
        if (targetNetId == 0U || !serverMembers.TryGetValue(targetNetId, out target) || target == null ||
            !TryGetParty(target, out targetParty))
        {
            SendStatus(source, "That player is no longer connected.");
            return;
        }
        if (target.netId == source.netId || target.partyId == source.partyId)
        {
            SendStatus(source, "That player is already in your party.");
            return;
        }
        if (targetParty.members.Count > 1)
        {
            SendStatus(source, "That player is already in another party.");
            return;
        }

        PendingInvite invite = new PendingInvite
        {
            inviterNetId = source.netId,
            targetNetId = target.netId,
            partyId = source.partyId,
            expiresRealtime = Time.unscaledTime + InviteLifetimeSeconds,
        };
        pendingInvitesByTarget[target.netId] = invite;

        string inviterName = ResolveServerPlayerName(source.netId);
        if (target.connection != null)
        {
            target.connection.Send(new PartyInviteMessage
            {
                inviterNetId = source.netId,
                partyId = source.partyId,
                inviterName = inviterName,
            });
        }

        SendStatus(source, "Party invitation sent to " + ResolveServerPlayerName(target.netId) + ".");
        Debug.Log("[MPParty] Invite source=" + source.netId + " target=" + target.netId + " party=" + source.partyId);
    }

    void ServerAcceptInvite(ServerMember target, uint inviterNetId, ulong requestedPartyId)
    {
        PendingInvite invite;
        if (!pendingInvitesByTarget.TryGetValue(target.netId, out invite) || invite == null ||
            invite.inviterNetId != inviterNetId || invite.partyId != requestedPartyId ||
            Time.unscaledTime > invite.expiresRealtime)
        {
            pendingInvitesByTarget.Remove(target.netId);
            SendStatus(target, "That party invitation has expired.");
            return;
        }

        ServerParty targetCurrentParty;
        if (!TryGetParty(target, out targetCurrentParty) || targetCurrentParty.members.Count > 1)
        {
            pendingInvitesByTarget.Remove(target.netId);
            SendStatus(target, "Leave your current party before accepting another invitation.");
            return;
        }

        ServerParty destination;
        if (!serverParties.TryGetValue(invite.partyId, out destination) || destination == null || destination.members.Count == 0)
        {
            pendingInvitesByTarget.Remove(target.netId);
            SendStatus(target, "That party no longer exists.");
            return;
        }

        pendingInvitesByTarget.Remove(target.netId);
        RemoveMemberFromParty(target, false);
        destination.members.Add(target.netId);
        destination.timeEpoch++;
        target.partyId = destination.id;

        string targetName = ResolveServerPlayerName(target.netId);
        NotifyParty(destination, targetName + " joined the party.");
        MarkServerStateDirty();
        Debug.Log("[MPParty] Accepted invite target=" + target.netId + " party=" + destination.id);
    }

    void ServerDeclineInvite(ServerMember target, uint inviterNetId, ulong requestedPartyId)
    {
        PendingInvite invite;
        if (pendingInvitesByTarget.TryGetValue(target.netId, out invite) && invite != null &&
            invite.inviterNetId == inviterNetId && invite.partyId == requestedPartyId)
        {
            pendingInvitesByTarget.Remove(target.netId);

            ServerMember inviter;
            if (serverMembers.TryGetValue(inviterNetId, out inviter) && inviter != null)
                SendStatus(inviter, ResolveServerPlayerName(target.netId) + " declined the party invitation.");
        }
    }

    void ServerLeaveParty(ServerMember source)
    {
        ServerParty party;
        if (!TryGetParty(source, out party) || party.members.Count <= 1)
        {
            SendStatus(source, "You are already in a solo party.");
            return;
        }

        string sourceName = ResolveServerPlayerName(source.netId);
        ulong oldPartyId = source.partyId;
        RemoveInvitesInvolving(source.netId);
        RemoveMemberFromParty(source, true);

        ServerParty oldParty;
        if (serverParties.TryGetValue(oldPartyId, out oldParty) && oldParty != null)
            NotifyParty(oldParty, sourceName + " left the party.");

        SendStatus(source, "You left the party.");
        MarkServerStateDirty();
    }

    void ServerKickMember(ServerMember source, uint targetNetId)
    {
        ServerParty party;
        if (!TryGetParty(source, out party) || party.leaderNetId != source.netId)
        {
            SendStatus(source, "Only the party leader can kick members.");
            return;
        }

        ServerMember target;
        if (targetNetId == 0U || targetNetId == source.netId ||
            !serverMembers.TryGetValue(targetNetId, out target) || target == null || target.partyId != party.id)
        {
            SendStatus(source, "That player is not in your party.");
            return;
        }

        string targetName = ResolveServerPlayerName(target.netId);
        RemoveInvitesInvolving(target.netId);
        RemoveMemberFromParty(target, true);
        SendStatus(target, "You were removed from the party.");
        NotifyParty(party, targetName + " was removed from the party.");
        MarkServerStateDirty();
    }

    void ServerPromoteLeader(ServerMember source, uint targetNetId)
    {
        ServerParty party;
        if (!TryGetParty(source, out party) || party.leaderNetId != source.netId)
        {
            SendStatus(source, "Only the party leader can transfer leadership.");
            return;
        }

        if (targetNetId == 0U || targetNetId == source.netId || !party.members.Contains(targetNetId))
        {
            SendStatus(source, "That player is not an eligible party member.");
            return;
        }

        EndServerRestSession(party.id);
        party.leaderNetId = targetNetId;
        party.timeEpoch++;
        NotifyParty(party, ResolveServerPlayerName(targetNetId) + " is now the party leader.");
        MarkServerStateDirty();
    }

    bool TryGetParty(ServerMember member, out ServerParty party)
    {
        party = null;
        return member != null && member.partyId != 0UL &&
               serverParties.TryGetValue(member.partyId, out party) && party != null;
    }

    void ExpireInvites(float now)
    {
        List<uint> expired = null;
        foreach (KeyValuePair<uint, PendingInvite> pair in pendingInvitesByTarget)
        {
            if (pair.Value == null || now > pair.Value.expiresRealtime)
            {
                if (expired == null)
                    expired = new List<uint>();
                expired.Add(pair.Key);
            }
        }

        if (expired != null)
            for (int i = 0; i < expired.Count; i++)
                pendingInvitesByTarget.Remove(expired[i]);
    }

    void MarkServerStateDirty()
    {
        serverStateDirty = true;
        nextSnapshotHeartbeatTime = 0f;
    }

    void BroadcastSnapshot()
    {
        if (!NetworkServer.active)
            return;

        List<uint> ids = serverMembers.Keys.OrderBy(id => id).ToList();
        PartySnapshotMessage snapshot = new PartySnapshotMessage
        {
            netIds = new uint[ids.Count],
            partyIds = new ulong[ids.Count],
            leaderNetIds = new uint[ids.Count],
            timeEpochs = new ulong[ids.Count],
            questSyncModes = new int[ids.Count],
            leaderControlsTime = new bool[ids.Count],
            restTogether = new bool[ids.Count],
            travelTogether = new bool[ids.Count],
            memberCounts = new int[ids.Count],
            playerNames = new string[ids.Count],
        };

        for (int i = 0; i < ids.Count; i++)
        {
            uint netId = ids[i];
            ServerMember member = serverMembers[netId];
            ServerParty party;
            serverParties.TryGetValue(member.partyId, out party);

            snapshot.netIds[i] = netId;
            snapshot.partyIds[i] = member.partyId;
            snapshot.leaderNetIds[i] = party != null ? party.leaderNetId : netId;
            snapshot.timeEpochs[i] = party != null ? party.timeEpoch : 0UL;
            snapshot.questSyncModes[i] = party != null ? (int)party.questMode : 0;
            snapshot.leaderControlsTime[i] = party == null || party.leaderControlsTime;
            snapshot.restTogether[i] = party == null || party.restTogether;
            snapshot.travelTogether[i] = party != null && party.travelTogether;
            snapshot.memberCounts[i] = party != null ? Mathf.Max(1, party.members.Count) : 1;
            snapshot.playerNames[i] = ResolveServerPlayerName(netId);
        }

        foreach (KeyValuePair<int, NetworkConnectionToClient> entry in NetworkServer.connections)
        {
            NetworkConnection connection = entry.Value;
            if (connection == null || !connection.isReady || connection.identity == null)
                continue;

            connection.Send(snapshot);
        }
    }

    string ResolveServerPlayerName(uint netId)
    {
        try
        {
            global::PlayerMultiplayer[] players = UnityEngine.Object.FindObjectsOfType<global::PlayerMultiplayer>();
            for (int i = 0; i < players.Length; i++)
            {
                global::PlayerMultiplayer player = players[i];
                if (player == null || player.netId != netId)
                    continue;

                global::PlayerAssets assets = player.GetComponent<global::PlayerAssets>();
                if (assets != null && !string.IsNullOrEmpty(assets.playerName) &&
                    !string.Equals(assets.playerName, "Nameless", StringComparison.OrdinalIgnoreCase))
                    return assets.playerName;
                break;
            }
        }
        catch { }

        return "Player " + netId;
    }

    void SendStatus(ServerMember member, string text)
    {
        if (member == null || member.connection == null || string.IsNullOrEmpty(text))
            return;

        member.connection.Send(new PartyStatusMessage { text = text });
    }

    void NotifyParty(ServerParty party, string text)
    {
        if (party == null || string.IsNullOrEmpty(text))
            return;

        foreach (uint memberNetId in party.members)
        {
            ServerMember member;
            if (serverMembers.TryGetValue(memberNetId, out member) && member != null)
                SendStatus(member, text);
        }
    }

    // -------------------------------------------------------------------------
    // Client message application / invitation UI
    // -------------------------------------------------------------------------

    void ApplyClientSnapshot(PartySnapshotMessage message)
    {
        // Freeze existing quest participation before the local party policy changes.
        PartyClientState oldLocal;
        uint localId = GetLocalNetId();
        if (clientStates.TryGetValue(localId, out oldLocal) && oldLocal != null && message.netIds != null)
        {
            int localIndex = Array.IndexOf(message.netIds, localId);
            if (localIndex >= 0 && message.questSyncModes != null && localIndex < message.questSyncModes.Length &&
                (int)oldLocal.QuestMode != message.questSyncModes[localIndex])
                QuestNetSync.CaptureExistingQuestPolicies(oldLocal.QuestMode);
        }
        clientStates.Clear();

        int count = message.netIds != null ? message.netIds.Length : 0;
        for (int i = 0; i < count; i++)
        {
            uint netId = message.netIds[i];
            if (netId == 0U)
                continue;

            PartyClientState state = new PartyClientState
            {
                NetId = netId,
                PartyId = message.partyIds != null && i < message.partyIds.Length ? message.partyIds[i] : 0UL,
                LeaderNetId = message.leaderNetIds != null && i < message.leaderNetIds.Length ? message.leaderNetIds[i] : netId,
                TimeEpoch = message.timeEpochs != null && i < message.timeEpochs.Length ? message.timeEpochs[i] : 0UL,
                QuestMode = message.questSyncModes != null && i < message.questSyncModes.Length && message.questSyncModes[i] >= 0 && message.questSyncModes[i] <= 2 ? (OptionsMultiplayer.QuestSyncMode)message.questSyncModes[i] : OptionsMultiplayer.QuestSyncMode.Full,
                LeaderControlsTime = message.leaderControlsTime == null || i >= message.leaderControlsTime.Length || message.leaderControlsTime[i],
                RestTogether = message.restTogether == null || i >= message.restTogether.Length || message.restTogether[i],
                TravelTogether = message.travelTogether != null && i < message.travelTogether.Length && message.travelTogether[i],
                MemberCount = message.memberCounts != null && i < message.memberCounts.Length ? Mathf.Max(1, message.memberCounts[i]) : 1,
                PlayerName = message.playerNames != null && i < message.playerNames.Length ? message.playerNames[i] : "Player " + netId,
            };
            clientStates[netId] = state;
        }

        RaisePartyStateChanged();
    }

    void QueueClientInvite(PartyInviteMessage message)
    {
        queuedInvites.Enqueue(message);
    }

    void QueueClientStatus(PartyStatusMessage message)
    {
        if (!string.IsNullOrEmpty(message.text))
            queuedStatusMessages.Enqueue(message.text);
    }

    void DrainClientUiQueues()
    {
        if (!NetworkClient.active || DaggerfallUI.UIManager == null)
            return;

        if (queuedStatusMessages.Count > 0)
        {
            string text = queuedStatusMessages.Dequeue();
            if (!string.IsNullOrEmpty(text))
                DaggerfallUI.MessageBox(text);
        }

        if (!invitePopupOpen && !travelOfferPopupOpen && queuedTravelOffers.Count > 0)
            ShowTravelOfferPopup(queuedTravelOffers.Dequeue());
        if (!invitePopupOpen && !travelOfferPopupOpen && queuedInvites.Count > 0)
            ShowInvitePopup(queuedInvites.Dequeue());
    }

    void ShowInvitePopup(PartyInviteMessage invite)
    {
        IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
        if (uiManager == null)
            return;

        invitePopupOpen = true;
        string inviterName = string.IsNullOrEmpty(invite.inviterName)
            ? "Another player"
            : invite.inviterName;

        DaggerfallMessageBox messageBox = new DaggerfallMessageBox(uiManager, uiManager.TopWindow);
        messageBox.SetText(inviterName + " invited you to join their party.");
        messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
        messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
        messageBox.OnButtonClick += (sender, button) =>
        {
            invitePopupOpen = false;
            sender.CloseWindow();

            if (button == DaggerfallMessageBox.MessageBoxButtons.Yes)
                SendAction(ActionAcceptInvite, 0U, invite.partyId, invite.inviterNetId);
            else
                SendAction(ActionDeclineInvite, 0U, invite.partyId, invite.inviterNetId);
        };
        // Escape/cancel should not permanently block later invitations.
        messageBox.OnClose += () => { invitePopupOpen = false; };
        uiManager.PushWindow(messageBox);
    }

    static void RaisePartyStateChanged()
    {
        Action handler = PartyStateChanged;
        if (handler != null)
            handler();
    }
}
