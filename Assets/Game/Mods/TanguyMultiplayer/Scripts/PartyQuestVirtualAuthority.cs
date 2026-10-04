using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

/// <summary>
/// Makes the existing QuestNetSync see the local party leader as its logical
/// "server/host" while the real Mirror listen host only authenticates and relays
/// QuestNetSync remote calls.
///
/// The important rule is that this class does not interpret quest events.  It knows
/// only player netIds, PartyIds, party leader identity, and Mirror remote-call packets.
/// QuestNetSync continues to own all quest-specific behavior.
/// </summary>
public sealed class PartyQuestVirtualAuthority : MonoBehaviour
{
    public struct PartyQuestCommandRelayMessage : NetworkMessage
    {
        public uint sourcePlayerNetId;
        public uint netId;
        public byte componentIndex;
        public int functionHash;
        public byte[] payload;
    }

    public struct PartyQuestRpcRelayMessage : NetworkMessage
    {
        public bool targetRpc;
        public uint targetPlayerNetId;
        public bool includeOwner;
        public uint netId;
        public byte componentIndex;
        public int functionHash;
        public byte[] payload;
        public int channelId;
    }

    static PartyQuestVirtualAuthority instance;
    static QuestNetSync activeVirtualServerLifecycle;

    static readonly Dictionary<uint, NetworkConnectionToClient> virtualConnectionsByNetId =
        new Dictionary<uint, NetworkConnectionToClient>();
    static readonly Dictionary<int, NetworkConnectionToClient> virtualConnectionTable =
        new Dictionary<int, NetworkConnectionToClient>();

    bool serverHandlersRegistered;
    bool clientHandlersRegistered;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (instance != null)
            return;

        GameObject go = new GameObject("Party Quest Virtual Authority");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PartyQuestVirtualAuthority>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;

        Mirror.NetworkManager.ClientExtensionsReady += RegisterClientHandlers;
        Mirror.NetworkManager.ServerExtensionsReady += RegisterServerHandlers;
        Mirror.NetworkManager.SessionStopping += HandleSessionStopping;
        Mirror.NetworkManager.SessionStopped += HandleSessionStopped;
        MultiplayerPartyManager.PartyStateChanged += HandlePartyStateChanged;

        InstallMirrorInterceptors();

        if (NetworkClient.active)
            RegisterClientHandlers();
        if (NetworkServer.active)
            RegisterServerHandlers();
    }

    void OnDestroy()
    {
        if (instance != this)
            return;

        DeactivateVirtualServerLifecycle();

        Mirror.NetworkManager.ClientExtensionsReady -= RegisterClientHandlers;
        Mirror.NetworkManager.ServerExtensionsReady -= RegisterServerHandlers;
        Mirror.NetworkManager.SessionStopping -= HandleSessionStopping;
        Mirror.NetworkManager.SessionStopped -= HandleSessionStopped;
        MultiplayerPartyManager.PartyStateChanged -= HandlePartyStateChanged;

        QuestNetVirtualTransport.ClearInterceptors();
        instance = null;
    }

    void Update()
    {
        // Handler dictionaries are recreated by Mirror for every session.
        if (NetworkClient.active && !clientHandlersRegistered)
            RegisterClientHandlers();
        if (NetworkServer.active && !serverHandlersRegistered)
            RegisterServerHandlers();

        EvaluateVirtualServerLifecycle();
    }

    static void InstallMirrorInterceptors()
    {
        QuestNetVirtualTransport.CommandInterceptor = InterceptServerCommand;
        QuestNetVirtualTransport.ClientRpcInterceptor = InterceptClientRpc;
        QuestNetVirtualTransport.TargetRpcInterceptor = InterceptTargetRpc;
        QuestNetVirtualTransport.ServerLifecycleInterceptor = InterceptPhysicalServerLifecycle;
    }

    void RegisterServerHandlers()
    {
        if (serverHandlersRegistered)
            return;

        NetworkServer.RegisterHandler<PartyQuestRpcRelayMessage>(HandleServerRpcRelay);
        serverHandlersRegistered = true;
        Debug.Log("[PartyQuestVirtual] Registered server RPC relay handler.");
    }

    void RegisterClientHandlers()
    {
        if (clientHandlersRegistered)
            return;

        NetworkClient.RegisterHandler<PartyQuestCommandRelayMessage>(HandleClientCommandRelay);
        clientHandlersRegistered = true;
        Debug.Log("[PartyQuestVirtual] Registered client Command relay handler.");
    }

    void HandlePartyStateChanged()
    {
        // Do not execute a full QuestMachine lifecycle from inside the party snapshot
        // handler. Update() will apply the transition after the message handler returns.
    }

    void HandleSessionStopping()
    {
        DeactivateVirtualServerLifecycle();
    }

    void HandleSessionStopped()
    {
        DeactivateVirtualServerLifecycle();
        serverHandlersRegistered = false;
        clientHandlersRegistered = false;
        virtualConnectionsByNetId.Clear();
        virtualConnectionTable.Clear();
        InstallMirrorInterceptors();
    }

    // ---------------------------------------------------------------------
    // QuestNetSync facade API
    // ---------------------------------------------------------------------

    // Mirror.NetworkIdentity asks this once for each NetworkBehaviour during the
    // physical server lifecycle. Only QuestNetSync is suppressed; every other
    // component keeps Mirror's original host lifecycle unchanged.
    static bool InterceptPhysicalServerLifecycle(NetworkBehaviour behaviour, bool starting)
    {
        return behaviour is QuestNetSync;
    }

    public static bool QuestVirtualizationActive
    {
        get { return NetworkClient.active || NetworkServer.active; }
    }

    public static bool VirtualServerActive
    {
        get { return IsLocalPartyLeaderExact(); }
    }

    public static bool IsVirtualServer(QuestNetSync sync)
    {
        if (sync == null || !NetworkClient.active || !IsLocalPartyLeaderExact())
            return false;

        uint localNetId = GetLocalNetId();
        uint targetNetId = sync.netIdentity != null ? sync.netIdentity.netId : 0U;
        if (localNetId == 0U || targetNetId == 0U)
            return false;

        ulong localPartyId;
        uint localLeaderNetId;
        ulong targetPartyId;
        uint targetLeaderNetId;
        if (!TryGetExactPartyState(localNetId, out localPartyId, out localLeaderNetId) ||
            !TryGetExactPartyState(targetNetId, out targetPartyId, out targetLeaderNetId))
            return false;

        return localPartyId != 0UL &&
               localLeaderNetId == localNetId &&
               localPartyId == targetPartyId;
    }

    public static bool IsVirtualClientOnly(QuestNetSync sync)
    {
        return sync != null && sync.isClient && !IsVirtualServer(sync);
    }

    public static Dictionary<int, NetworkConnectionToClient> VirtualConnections
    {
        get
        {
            RebuildVirtualConnections();
            return virtualConnectionTable;
        }
    }

    public static NetworkConnectionToClient VirtualLocalConnection
    {
        get
        {
            if (!IsLocalPartyLeaderExact())
                return null;

            return GetVirtualConnectionForNetId(GetLocalNetId());
        }
    }

    public static NetworkConnectionToClient GetVirtualConnectionToClient(QuestNetSync sync)
    {
        if (!IsVirtualServer(sync) || sync.netIdentity == null)
            return null;

        return GetVirtualConnectionForNetId(sync.netIdentity.netId);
    }

    static uint GetLocalNetId()
    {
        if (NetworkClient.localPlayer != null)
            return NetworkClient.localPlayer.netId;

        if (PlayerMultiplayer.localPlayer != null)
            return PlayerMultiplayer.localPlayer.netId;

        return 0U;
    }

    static bool IsLocalPartyLeaderExact()
    {
        uint localNetId = GetLocalNetId();
        if (localNetId == 0U)
            return false;

        ulong partyId;
        uint leaderNetId;
        return TryGetExactPartyState(localNetId, out partyId, out leaderNetId) &&
               partyId != 0UL && leaderNetId == localNetId;
    }

    static bool TryGetExactLocalParty(out ulong partyId, out uint leaderNetId)
    {
        return TryGetExactPartyState(GetLocalNetId(), out partyId, out leaderNetId);
    }

    static bool TryGetExactPartyState(uint playerNetId, out ulong partyId, out uint leaderNetId)
    {
        partyId = 0UL;
        leaderNetId = 0U;
        if (playerNetId == 0U)
            return false;

        // Real host: use the server-owned party table directly.
        if (NetworkClient.isHostClient && NetworkServer.active)
        {
            if (!MultiplayerPartyManager.ServerTryGetPartyId(playerNetId, out partyId))
                return false;

            leaderNetId = MultiplayerPartyManager.ServerGetPartyLeaderNetId(playerNetId);
            return partyId != 0UL && leaderNetId != 0U;
        }

        MultiplayerPartyManager.PartyClientState state;
        if (!MultiplayerPartyManager.TryGetClientPartyState(playerNetId, out state) || state == null)
            return false;

        partyId = state.PartyId;
        leaderNetId = state.LeaderNetId;
        return partyId != 0UL && leaderNetId != 0U;
    }

    static NetworkIdentity FindLocalIdentity(uint netId)
    {
        if (netId == 0U)
            return null;

        NetworkIdentity identity;
        if (NetworkClient.spawned.TryGetValue(netId, out identity) && identity != null)
            return identity;

        // In host mode the authoritative server dictionary is the most reliable source.
        if (NetworkClient.isHostClient &&
            NetworkServer.spawned.TryGetValue(netId, out identity) && identity != null)
            return identity;

        return null;
    }

    static void RebuildVirtualConnections()
    {
        virtualConnectionTable.Clear();

        uint localNetId = GetLocalNetId();
        ulong localPartyId;
        uint leaderNetId;
        if (!TryGetExactLocalParty(out localPartyId, out leaderNetId) ||
            leaderNetId != localNetId)
            return;

        // On the real host, authoritative party connections already identify every
        // member even before the loopback client snapshot arrives. Build virtual tokens
        // from those identities, but never expose the real connection table to QNS.
        if (NetworkClient.isHostClient && NetworkServer.active)
        {
            List<NetworkConnection> partyConnections =
                MultiplayerPartyManager.ServerGetPartyConnections(localNetId);

            for (int i = 0; i < partyConnections.Count; i++)
            {
                NetworkConnection real = partyConnections[i];
                if (real == null || real.identity == null || real.identity.netId == 0U)
                    continue;

                AddVirtualConnection(real.identity.netId, real.identity);
            }
            return;
        }

        MultiplayerPartyManager.PartyClientState[] roster =
            MultiplayerPartyManager.GetClientRosterSnapshot();

        for (int i = 0; i < roster.Length; i++)
        {
            MultiplayerPartyManager.PartyClientState state = roster[i];
            if (state == null || state.NetId == 0U || state.PartyId != localPartyId)
                continue;

            NetworkIdentity identity = FindLocalIdentity(state.NetId);
            if (identity == null)
                continue;

            AddVirtualConnection(state.NetId, identity);
        }
    }

    static void AddVirtualConnection(uint playerNetId, NetworkIdentity identity)
    {
        if (playerNetId == 0U || identity == null)
            return;

        NetworkConnectionToClient connection;
        if (!virtualConnectionsByNetId.TryGetValue(playerNetId, out connection) ||
            connection == null || connection.identity != identity)
        {
            int virtualConnectionId = unchecked((int)playerNetId);
            connection = QuestNetVirtualTransport.CreateVirtualConnection(
                virtualConnectionId,
                identity);
            virtualConnectionsByNetId[playerNetId] = connection;
        }

        virtualConnectionTable[connection.connectionId] = connection;
    }

    static NetworkConnectionToClient GetVirtualConnectionForNetId(uint netId)
    {
        if (netId == 0U)
            return null;

        RebuildVirtualConnections();

        NetworkConnectionToClient connection;
        return virtualConnectionsByNetId.TryGetValue(netId, out connection)
            ? connection
            : null;
    }

    // ---------------------------------------------------------------------
    // Virtual QuestNetSync server lifecycle
    // ---------------------------------------------------------------------

    void EvaluateVirtualServerLifecycle()
    {
        QuestNetSync local = QuestNetSync.LocalInstance;
        bool shouldBeVirtualServer =
            local != null && local.isLocalPlayer && IsLocalPartyLeaderExact();

        if (shouldBeVirtualServer)
        {
            if (!object.ReferenceEquals(activeVirtualServerLifecycle, local))
            {
                DeactivateVirtualServerLifecycle();
                ActivateVirtualServerLifecycle(local);
            }
        }
        else if (activeVirtualServerLifecycle != null)
        {
            DeactivateVirtualServerLifecycle();
        }
    }

    static void EnsureVirtualServerLifecycleReady()
    {
        PartyQuestVirtualAuthority current = instance;
        if (current != null)
            current.EvaluateVirtualServerLifecycle();
    }

    static void ActivateVirtualServerLifecycle(QuestNetSync local)
    {
        if (local == null || object.ReferenceEquals(activeVirtualServerLifecycle, local))
            return;

        local.OnStartServer();
        activeVirtualServerLifecycle = local;
        Debug.Log(
            "[PartyQuestVirtual] Activated original QuestNetSync server lifecycle on " +
            "party leader netId=" + local.netId + " party=" + MultiplayerPartyManager.GetLocalPartyId() + ".");
    }

    static void DeactivateVirtualServerLifecycle()
    {
        QuestNetSync active = activeVirtualServerLifecycle;
        if (active == null)
            return;

        activeVirtualServerLifecycle = null;
        try
        {
            if (active != null)
                active.OnStopServer();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PartyQuestVirtual] Virtual QuestNetSync server stop failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // Real server: Command routing
    // ---------------------------------------------------------------------

    static bool IsQuestNetSyncComponent(NetworkIdentity identity, byte componentIndex)
    {
        if (identity == null || identity.NetworkBehaviours == null ||
            componentIndex >= identity.NetworkBehaviours.Length)
            return false;

        return identity.NetworkBehaviours[componentIndex] is QuestNetSync;
    }

    static bool InterceptServerCommand(
        NetworkConnection connection,
        NetworkIdentity identity,
        CommandMessage message,
        int channelId)
    {
        if (!IsQuestNetSyncComponent(identity, message.componentIndex))
            return false;

        // From this point onward fail closed. A QuestNetSync Command must never fall
        // through to an unrelated physical host merely because party state is late.
        uint sourceNetId = connection != null && connection.identity != null
            ? connection.identity.netId
            : 0U;
        if (sourceNetId == 0U)
        {
            Debug.LogWarning("[PartyQuestVirtual] Dropped QNS Command with no source player identity.");
            return true;
        }

        uint leaderNetId = MultiplayerPartyManager.ServerGetPartyLeaderNetId(sourceNetId);
        if (leaderNetId == 0U)
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Dropped QNS Command because party authority is not ready. source=" +
                sourceNetId + ".");
            return true;
        }

        uint hostLocalNetId =
            NetworkServer.localConnection != null && NetworkServer.localConnection.identity != null
                ? NetworkServer.localConnection.identity.netId
                : 0U;

        // If the listen-host PLAYER is this party's leader, Mirror's ordinary server
        // dispatch executes the original Command body locally. RPC output is still
        // intercepted and party-scoped below.
        if (hostLocalNetId != 0U && leaderNetId == hostLocalNetId)
        {
            EnsureVirtualServerLifecycleReady();
            return false;
        }

        NetworkConnection leaderConnection = FindServerConnectionByPlayerNetId(leaderNetId);
        if (leaderConnection == null)
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Dropped QNS Command because remote party leader is unavailable. " +
                "source=" + sourceNetId + " leader=" + leaderNetId + ".");
            return true;
        }

        PartyQuestCommandRelayMessage relay = new PartyQuestCommandRelayMessage
        {
            sourcePlayerNetId = sourceNetId,
            netId = message.netId,
            componentIndex = message.componentIndex,
            functionHash = message.functionHash,
            payload = CopyPayload(message.payload),
        };

        leaderConnection.Send(relay, channelId);

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                "[PartyQuestVirtual] Relayed QNS Command source=" + sourceNetId +
                " -> leader=" + leaderNetId + " netId=" + message.netId +
                " hash=" + message.functionHash + ".");
        }

        return true;
    }

    static NetworkConnection FindServerConnectionByPlayerNetId(uint playerNetId)
    {
        if (playerNetId == 0U || !NetworkServer.active)
            return null;

        foreach (KeyValuePair<int, NetworkConnectionToClient> pair in NetworkServer.connections)
        {
            NetworkConnection connection = pair.Value;
            if (connection != null && connection.identity != null &&
                connection.identity.netId == playerNetId)
                return connection;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Party leader client: execute relayed original Command
    // ---------------------------------------------------------------------

    void HandleClientCommandRelay(PartyQuestCommandRelayMessage relay)
    {
        if (!IsLocalPartyLeaderExact())
        {
            Debug.LogWarning("[PartyQuestVirtual] Ignored QNS Command relay on non-leader client.");
            return;
        }

        EnsureVirtualServerLifecycleReady();
        if (activeVirtualServerLifecycle == null)
        {
            Debug.LogWarning("[PartyQuestVirtual] Cannot execute QNS Command relay because virtual server lifecycle is not ready.");
            return;
        }

        uint localNetId = GetLocalNetId();
        if (!MultiplayerPartyManager.AreSameParty(localNetId, relay.sourcePlayerNetId))
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Rejected QNS Command relay from another party. source=" +
                relay.sourcePlayerNetId + " local=" + localNetId + ".");
            return;
        }

        NetworkConnectionToClient virtualSender =
            GetVirtualConnectionForNetId(relay.sourcePlayerNetId);
        if (virtualSender == null)
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Cannot execute QNS Command relay because source player " +
                "identity is not spawned locally. source=" + relay.sourcePlayerNetId + ".");
            return;
        }

        CommandMessage command = new CommandMessage
        {
            netId = relay.netId,
            componentIndex = relay.componentIndex,
            functionHash = relay.functionHash,
            payload = new ArraySegment<byte>(relay.payload ?? new byte[0]),
        };

        QuestNetVirtualTransport.InvokeCommandOnVirtualServer(command, virtualSender);
    }

    // ---------------------------------------------------------------------
    // Party leader: outgoing original ClientRpc / TargetRpc interception
    // ---------------------------------------------------------------------

    static bool InterceptClientRpc(
        NetworkBehaviour behaviour,
        RpcMessage message,
        bool includeOwner,
        int channelId)
    {
        QuestNetSync sync = behaviour as QuestNetSync;
        if (sync == null)
            return false;

        // QuestNetSync RPCs are *always* consumed by this layer. This is the seal:
        // an accidental physical-host QNS call can never fall through to Mirror's
        // ordinary all-observer send path and leak into another party.
        if (!IsVirtualServer(sync))
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Suppressed QNS ClientRpc from non-authoritative instance netId=" +
                behaviour.netId + ".");
            return true;
        }

        return RelayOutgoingRpc(
            sync,
            message,
            false,
            0U,
            includeOwner,
            channelId);
    }

    static bool InterceptTargetRpc(
        NetworkBehaviour behaviour,
        NetworkConnection target,
        RpcMessage message,
        int channelId)
    {
        QuestNetSync sync = behaviour as QuestNetSync;
        if (sync == null)
            return false;

        if (!IsVirtualServer(sync))
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Suppressed QNS TargetRpc from non-authoritative instance netId=" +
                behaviour.netId + ".");
            return true;
        }

        NetworkConnection resolvedTarget = target;
        if (resolvedTarget == null)
            resolvedTarget = sync.connectionToClient;

        uint targetPlayerNetId =
            resolvedTarget != null && resolvedTarget.identity != null
                ? resolvedTarget.identity.netId
                : 0U;

        if (targetPlayerNetId == 0U)
        {
            Debug.LogWarning("[PartyQuestVirtual] Suppressed QNS TargetRpc with no target player identity.");
            return true;
        }

        return RelayOutgoingRpc(
            sync,
            message,
            true,
            targetPlayerNetId,
            true,
            channelId);
    }

    static bool RelayOutgoingRpc(
        QuestNetSync sync,
        RpcMessage message,
        bool targetRpc,
        uint targetPlayerNetId,
        bool includeOwner,
        int channelId)
    {
        uint localLeaderNetId = GetLocalNetId();
        if (localLeaderNetId == 0U)
            return true;

        PartyQuestRpcRelayMessage relay = new PartyQuestRpcRelayMessage
        {
            targetRpc = targetRpc,
            targetPlayerNetId = targetPlayerNetId,
            includeOwner = includeOwner,
            netId = message.netId,
            componentIndex = message.componentIndex,
            functionHash = message.functionHash,
            payload = CopyPayload(message.payload),
            channelId = channelId,
        };

        // The listen host can route its own leader RPC immediately without sending
        // a loopback custom message. A remote leader sends only this opaque relay to
        // the real host; the host does not execute QuestNetSync.
        if (NetworkClient.isHostClient && NetworkServer.active)
        {
            ServerForwardRpcFromLeader(localLeaderNetId, relay);
        }
        else if (NetworkClient.ready)
        {
            NetworkClient.Send(relay, channelId);
        }
        else
        {
            Debug.LogWarning("[PartyQuestVirtual] Dropped QNS RPC because client transport is not ready.");
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // Real host: validate leader RPC relay and forward normal RpcMessage only
    // to that leader's authoritative party.
    // ---------------------------------------------------------------------

    void HandleServerRpcRelay(NetworkConnection connection, PartyQuestRpcRelayMessage relay)
    {
        uint sourceNetId = connection != null && connection.identity != null
            ? connection.identity.netId
            : 0U;
        if (sourceNetId == 0U)
            return;

        ServerForwardRpcFromLeader(sourceNetId, relay);
    }

    static void ServerForwardRpcFromLeader(uint sourceLeaderNetId, PartyQuestRpcRelayMessage relay)
    {
        if (!NetworkServer.active || sourceLeaderNetId == 0U)
            return;

        uint authoritativeLeader =
            MultiplayerPartyManager.ServerGetPartyLeaderNetId(sourceLeaderNetId);
        if (authoritativeLeader == 0U || authoritativeLeader != sourceLeaderNetId)
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Rejected QNS RPC relay from non-leader source=" +
                sourceLeaderNetId + " leader=" + authoritativeLeader + ".");
            return;
        }

        ulong partyId;
        if (!MultiplayerPartyManager.ServerTryGetPartyId(sourceLeaderNetId, out partyId) ||
            partyId == 0UL)
            return;

        NetworkIdentity rpcIdentity;
        if (!NetworkServer.spawned.TryGetValue(relay.netId, out rpcIdentity) ||
            rpcIdentity == null ||
            !IsQuestNetSyncComponent(rpcIdentity, relay.componentIndex))
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Rejected QNS RPC relay for invalid identity/component netId=" +
                relay.netId + " component=" + relay.componentIndex + ".");
            return;
        }

        if (!MultiplayerPartyManager.ServerIsPlayerInParty(rpcIdentity.netId, partyId))
        {
            Debug.LogWarning(
                "[PartyQuestVirtual] Rejected QNS RPC relay whose NetworkIdentity is outside leader party. " +
                "leader=" + sourceLeaderNetId + " rpcNetId=" + rpcIdentity.netId + ".");
            return;
        }

        RpcMessage rpc = new RpcMessage
        {
            netId = relay.netId,
            componentIndex = relay.componentIndex,
            functionHash = relay.functionHash,
            payload = new ArraySegment<byte>(relay.payload ?? new byte[0]),
        };

        if (relay.targetRpc)
        {
            if (!MultiplayerPartyManager.ServerIsPlayerInParty(relay.targetPlayerNetId, partyId))
            {
                Debug.LogWarning(
                    "[PartyQuestVirtual] Rejected cross-party QNS TargetRpc leader=" + sourceLeaderNetId +
                    " target=" + relay.targetPlayerNetId + ".");
                return;
            }

            NetworkConnection target = FindServerConnectionByPlayerNetId(relay.targetPlayerNetId);
            if (target != null && target.isReady)
                target.Send(rpc, relay.channelId);

            return;
        }

        List<NetworkConnection> partyConnections =
            MultiplayerPartyManager.ServerGetPartyConnectionsByPartyId(partyId);

        int sent = 0;
        for (int i = 0; i < partyConnections.Count; i++)
        {
            NetworkConnection recipient = partyConnections[i];
            if (recipient == null || !recipient.isReady)
                continue;

            // Preserve original ClientRpc observer semantics, but intersect them with
            // the owning party so an observed player in Party C can never receive a
            // Party B QuestNetSync RPC.
            if (rpcIdentity.observers == null ||
                !rpcIdentity.observers.ContainsKey(recipient.connectionId))
                continue;

            bool isOwner = recipient == rpcIdentity.connectionToClient;
            if (isOwner && !relay.includeOwner)
                continue;

            recipient.Send(rpc, relay.channelId);
            sent++;
        }

        if (Debug.isDebugBuild)
        {
            Debug.Log(
                "[PartyQuestVirtual] Forwarded QNS ClientRpc leader=" + sourceLeaderNetId +
                " party=" + partyId + " recipients=" + sent +
                " netId=" + relay.netId + " hash=" + relay.functionHash + ".");
        }
    }

    static byte[] CopyPayload(ArraySegment<byte> payload)
    {
        if (payload.Array == null || payload.Count <= 0)
            return new byte[0];

        byte[] copy = new byte[payload.Count];
        Buffer.BlockCopy(payload.Array, payload.Offset, copy, 0, payload.Count);
        return copy;
    }
}
