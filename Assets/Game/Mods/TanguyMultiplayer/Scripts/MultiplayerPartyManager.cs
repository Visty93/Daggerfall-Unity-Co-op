using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Utility;

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
        public readonly HashSet<uint> members = new HashSet<uint>();
    }

    sealed class PendingInvite
    {
        public uint inviterNetId;
        public uint targetNetId;
        public ulong partyId;
        public float expiresRealtime;
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

        DrainClientUiQueues();
    }

    void RegisterServerHandlers()
    {
        if (serverHandlersRegistered)
            return;

        NetworkServer.RegisterHandler<PartyActionRequest>(HandleServerAction);
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
    // Party management UI. Kept here so the existing party window only needs one
    // small Manage button instead of owning network protocol details.
    // -------------------------------------------------------------------------

    public static void OpenManagementWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous)
    {
        if (instance == null || uiManager == null)
            return;

        uint localNetId = GetLocalNetId();
        PartyClientState localState;
        if (localNetId == 0U || !TryGetClientState(localNetId, out localState))
        {
            DaggerfallUI.MessageBox("Party state is still synchronizing.");
            return;
        }

        bool isLeader = localState.LeaderNetId == localNetId;
        List<ManagementAction> actions = new List<ManagementAction>();
        PartyClientState[] roster = GetClientRosterSnapshot();

        if (isLeader)
        {
            for (uint mode = 0; mode <= 2; mode++)
                actions.Add(new ManagementAction { action = ActionSetQuestMode, targetNetId = mode,
                    label = "Quest sync: " + QuestModeLabel((OptionsMultiplayer.QuestSyncMode)mode) +
                        ((uint)localState.QuestMode == mode ? " (current)" : "") });
            for (int i = 0; i < roster.Length; i++)
            {
                PartyClientState candidate = roster[i];
                if (candidate == null || candidate.NetId == localNetId)
                    continue;

                if (candidate.PartyId != localState.PartyId && candidate.MemberCount <= 1)
                {
                    actions.Add(new ManagementAction
                    {
                        action = ActionInvite,
                        targetNetId = candidate.NetId,
                        label = "Invite " + SafeName(candidate),
                    });
                }
            }

            for (int i = 0; i < roster.Length; i++)
            {
                PartyClientState member = roster[i];
                if (member == null || member.NetId == localNetId || member.PartyId != localState.PartyId)
                    continue;

                actions.Add(new ManagementAction
                {
                    action = ActionPromoteLeader,
                    targetNetId = member.NetId,
                    label = "Make leader: " + SafeName(member),
                });
                actions.Add(new ManagementAction
                {
                    action = ActionKickMember,
                    targetNetId = member.NetId,
                    label = "Kick: " + SafeName(member),
                });
            }
        }

        if (localState.MemberCount > 1)
        {
            actions.Add(new ManagementAction
            {
                action = ActionLeaveParty,
                targetNetId = 0U,
                label = "Leave party",
            });
        }

        if (actions.Count == 0)
        {
            DaggerfallUI.MessageBox(isLeader
                ? "No solo players are currently available to invite."
                : "Only the party leader can invite, kick, or promote members.");
            return;
        }

        DaggerfallListPickerWindow picker = new DaggerfallListPickerWindow(uiManager, previous);
        picker.ListBox.AddItems(actions.Select(a => a.label).ToArray());
        picker.OnItemPicked += (index, itemString) =>
        {
            if (index < 0 || index >= actions.Count)
                return;

            ManagementAction chosen = actions[index];
            picker.CloseWindow();
            SendAction(chosen.action, chosen.targetNetId, chosen.action == ActionSetQuestMode ? localState.PartyId : 0UL, 0U);
        };
        uiManager.PushWindow(picker);
    }

    public static string QuestModeLabel(OptionsMultiplayer.QuestSyncMode mode)
    {
        return mode == OptionsMultiplayer.QuestSyncMode.Off ? "Off" :
            mode == OptionsMultiplayer.QuestSyncMode.SharedOnly ? "Share-only" : "Full sync";
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
        NotifyParty(party, "Quest sync: " + QuestModeLabel(party.questMode) + ". Existing quests keep their participation.");
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

        if (!invitePopupOpen && queuedInvites.Count > 0)
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
