using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Questing;

// Physical enemies belong to a party + QNS instance, never to a foreign local UID.
// This registry outlives the enemy, because its corpse outlives NetworkServer.spawned.
public sealed class PartyQuestFoeBinding : MonoBehaviour
{
    public struct ResolveMessage : NetworkMessage
    { public uint enemy; public ulong uid; public string foe; }
    public struct ResolvedMessage : NetworkMessage
    { public uint enemy; public string inst; public bool isPrivate; }
    public struct ValidateMessage : NetworkMessage
    { public uint enemy; public ulong party; public string inst; public string foe; }
    public struct ValidatedMessage : NetworkMessage
    { public uint enemy; public string inst; }
    public struct BindingMessage : NetworkMessage
    { public uint requester; public uint enemy; public ulong party; public string inst; public string foe; }

    public sealed class Binding
    {
        public uint requester;
        public uint enemy;
        public ulong party;
        public string inst;
        public string foe;
    }
    sealed class Pending
    {
        public uint requester;
        public ulong uid;
        public Binding binding;
        public uint validatingLeader;
    }
    static PartyQuestFoeBinding instance;
    static readonly Dictionary<uint, Binding> server = new Dictionary<uint, Binding>();
    static readonly Dictionary<uint, Binding> client = new Dictionary<uint, Binding>();
    readonly Dictionary<uint, Pending> pending = new Dictionary<uint, Pending>();
    readonly HashSet<uint> bound = new HashSet<uint>();
    bool clientReady, serverReady;
    readonly HashSet<uint> bindingInProgress = new HashSet<uint>();
    float nextBindRetry;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("Party Quest Foe Binding");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PartyQuestFoeBinding>();
    }
    void Awake()
    {
        instance = this;
        Mirror.NetworkManager.ClientExtensionsReady += RegisterClient;
        Mirror.NetworkManager.ServerExtensionsReady += RegisterServer;
        Mirror.NetworkManager.SessionStopped += ResetSession;
        MultiplayerPartyManager.PartyStateChanged += PartyChanged;
    }
    void OnDestroy()
    {
        Mirror.NetworkManager.ClientExtensionsReady -= RegisterClient;
        Mirror.NetworkManager.ServerExtensionsReady -= RegisterServer;
        Mirror.NetworkManager.SessionStopped -= ResetSession;
        MultiplayerPartyManager.PartyStateChanged -= PartyChanged;
        if (instance == this) instance = null;
    }
    void Update()
    {
        if (NetworkClient.active && !clientReady) RegisterClient();
        if (NetworkServer.active && !serverReady) RegisterServer();
        if (NetworkClient.active && Time.realtimeSinceStartup >= nextBindRetry)
        {
            nextBindRetry = Time.realtimeSinceStartup + 1f;
            foreach (Binding b in new List<Binding>(client.Values))
                if (IsLocalParty(b.party) && !bound.Contains(b.enemy) && !bindingInProgress.Contains(b.enemy))
                {
                    NetworkIdentity ni;
                    if (NetworkClient.spawned.TryGetValue(b.enemy, out ni) && ni != null)
                        StartCoroutine(BindLocal(b));
                }
        }
    }
    void RegisterClient()
    {
        if (clientReady) return;
        NetworkClient.RegisterHandler<ResolveMessage>(m => StartCoroutine(Resolve(m)));
        NetworkClient.RegisterHandler<ValidateMessage>(m => StartCoroutine(Validate(m)));
        NetworkClient.RegisterHandler<BindingMessage>(Apply);
        clientReady = true;
    }
    void RegisterServer()
    {
        if (serverReady) return;
        NetworkServer.RegisterHandler<ResolvedMessage>(Resolved);
        NetworkServer.RegisterHandler<ValidatedMessage>(Validated);
        serverReady = true;
    }
    void ResetSession()
    {
        StopAllCoroutines();
        pending.Clear(); bound.Clear(); bindingInProgress.Clear(); server.Clear(); client.Clear();
        clientReady = serverReady = false;
    }
    void PartyChanged()
    {
        if (NetworkServer.active)
        {
            foreach (Binding b in server.Values)
                foreach (var c in NetworkServer.connections.Values)
                    if (c != null && c.isReady) c.Send(new BindingMessage { requester = b.requester, enemy = b.enemy, party = b.party, inst = b.inst, foe = b.foe });
            PartyQuestLootOverlay.ServerReplay();
        }
        // Old metadata cannot authorize a different party. Re-evaluate local resources.
        foreach (Binding b in new List<Binding>(client.Values))
        {
            NetworkIdentity ni;
            if (!NetworkClient.spawned.TryGetValue(b.enemy, out ni) || ni == null) continue;
            var qrb = ni.GetComponent<QuestResourceBehaviour>();
            if (!IsLocalParty(b.party))
            {
                if (qrb != null) { qrb.enabled = false; Destroy(qrb); }
                bound.Remove(b.enemy);
            }
            else if (!bound.Contains(b.enemy)) StartCoroutine(BindLocal(b));
        }
    }
    public static bool IsLocalParty(ulong party)
    { return party != 0UL && MultiplayerPartyManager.GetLocalPartyId() == party; }
    public static bool TryGetLocal(uint enemy, out Binding b)
    { return client.TryGetValue(enemy, out b); }
    public static bool TryGetServer(uint enemy, out Binding b)
    { return server.TryGetValue(enemy, out b); }
    public static Binding[] LocalBindings()
    { return new List<Binding>(client.Values).ToArray(); }

    // Called only by a physical server spawn, with the authenticated requester.
    public static void Register(uint enemy, uint requester, ulong uid, string foe)
    {
        if (instance == null || !NetworkServer.active || enemy == 0 || requester == 0 || uid == 0 || string.IsNullOrEmpty(foe)) return;
        if (server.ContainsKey(enemy) || instance.pending.ContainsKey(enemy)) return;
        ulong party;
        if (!MultiplayerPartyManager.ServerTryGetPartyId(requester, out party) || party == 0) return;
        var b = new Binding { requester = requester, enemy = enemy, party = party, foe = foe };
        instance.pending[enemy] = new Pending { requester = requester, uid = uid, binding = b };
        var connection = FindConnection(requester);
        if (connection != null) connection.Send(new ResolveMessage { enemy = enemy, uid = uid, foe = foe });
    }
    IEnumerator Resolve(ResolveMessage m)
    {
        float until = Time.realtimeSinceStartup + 20f;
        float nextRequest = 0f;
        while (NetworkClient.active && Time.realtimeSinceStartup < until)
        {
            var q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(m.uid) : null;
            if (q != null)
            {
                if (!QuestNetSync.PartyVirtualShouldPublishQuestLoot(m.uid))
                {
                    NetworkClient.Send(new ResolvedMessage { enemy = m.enemy, isPrivate = true });
                    yield break;
                }
                string inst;
                if (QuestNetSync.PartyVirtualTryGetNetworkInstance(m.uid, out inst))
                {
                    NetworkClient.Send(new ResolvedMessage { enemy = m.enemy, inst = inst });
                    yield break;
                }
                if (Time.realtimeSinceStartup >= nextRequest && QuestNetSync.LocalInstance != null)
                {
                    nextRequest = Time.realtimeSinceStartup + 0.5f;
                    QuestNetSync.LocalInstance.PartyVirtualEnsureSpawnMapping(m.uid);
                }
            }
            yield return null;
        }
        Debug.LogWarning("[PartyQuestFoe] Missing requester quest mapping enemy=" + m.enemy + " uid=" + m.uid);
    }
    void Resolved(NetworkConnection c, ResolvedMessage m)
    {
        Pending p;
        if (c == null || c.identity == null || !pending.TryGetValue(m.enemy, out p) || c.identity.netId != p.requester ||
            !MultiplayerPartyManager.ServerIsPlayerInParty(p.requester, p.binding.party)) return;
        if (m.isPrivate)
        {
            pending.Remove(m.enemy);
            var pm = c.identity.GetComponent<PlayerMultiplayer>();
            if (pm != null) pm.TargetBindPrivateQuestFoe(c, m.enemy, p.uid, p.binding.foe);
            return;
        }
        if (string.IsNullOrEmpty(m.inst)) return;
        p.binding.inst = m.inst;
        p.validatingLeader = MultiplayerPartyManager.ServerGetPartyLeaderNetId(p.requester);
        var leader = FindConnection(p.validatingLeader);
        if (leader != null) leader.Send(new ValidateMessage { enemy = m.enemy, party = p.binding.party, inst = m.inst, foe = p.binding.foe });
    }
    IEnumerator Validate(ValidateMessage m)
    {
        float until = Time.realtimeSinceStartup + 20f;
        while (NetworkClient.active && IsLocalParty(m.party) && PartyQuestVirtualAuthority.VirtualServerActive && Time.realtimeSinceStartup < until)
        {
            ulong uid;
            string canonical;
            if (QuestNetSync.PartyVirtualTryGetLocalQuestUid(m.inst, out uid) &&
                QuestNetSync.PartyVirtualTryGetNetworkInstance(uid, out canonical))
            {
                var q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(uid) : null;
                if (q != null && q.GetFoe(new Symbol(m.foe)) != null)
                {
                    NetworkClient.Send(new ValidatedMessage { enemy = m.enemy, inst = canonical });
                    yield break;
                }
            }
            yield return null;
        }
        Debug.LogWarning("[PartyQuestFoe] Leader could not validate instance=" + m.inst + " enemy=" + m.enemy);
    }
    void Validated(NetworkConnection c, ValidatedMessage m)
    {
        Pending p;
        if (c == null || c.identity == null || string.IsNullOrEmpty(m.inst) || !pending.TryGetValue(m.enemy, out p) ||
            c.identity.netId != p.validatingLeader ||
            MultiplayerPartyManager.ServerGetPartyLeaderNetId(p.requester) != p.validatingLeader ||
            !MultiplayerPartyManager.ServerIsPlayerInParty(p.requester, p.binding.party)) return;
        p.binding.inst = m.inst;
        server[m.enemy] = p.binding;
        pending.Remove(m.enemy);
        // Only ownership metadata is global; quest item data is never broadcast globally.
        var msg = new BindingMessage { requester = p.requester, enemy = m.enemy, party = p.binding.party, inst = m.inst, foe = p.binding.foe };
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != null && connection.isReady) connection.Send(msg);
        Debug.Log("[PartyQuestFoe] Registered enemy=" + m.enemy + " party=" + p.binding.party + " inst=" + m.inst + " foe=" + p.binding.foe);
    }
    void Apply(BindingMessage m)
    {
        var b = new Binding { requester = m.requester, enemy = m.enemy, party = m.party, inst = m.inst, foe = m.foe };
        client[m.enemy] = b;
        if (IsLocalParty(b.party)) StartCoroutine(BindLocal(b));
    }
    IEnumerator BindLocal(Binding b)
    {
        if (!bindingInProgress.Add(b.enemy)) yield break;
        try
        {
        float until = Time.realtimeSinceStartup + 20f;
        while (NetworkClient.active && IsLocalParty(b.party) && Time.realtimeSinceStartup < until)
        {
            if (bound.Contains(b.enemy)) yield break;
            ulong uid;
            NetworkIdentity ni;
            if (QuestNetSync.PartyVirtualTryGetLocalQuestUid(b.inst, out uid) &&
                NetworkClient.spawned.TryGetValue(b.enemy, out ni) && ni != null)
            {
                var q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(uid) : null;
                var foe = q != null ? q.GetFoe(new Symbol(b.foe)) : null;
                if (foe != null)
                {
                    var qrb = ni.GetComponent<QuestResourceBehaviour>();
                    if (qrb == null) qrb = ni.gameObject.AddComponent<QuestResourceBehaviour>();
                    qrb.AssignResource(foe);
                    uint localPlayer = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.netId : 0U;
                    if (b.requester != localPlayer)
                        qrb.SuppressQuestResourceBackReferenceForMultiplayerRemoteFoe("party-instance-bind");
                    foe.RearmInjured();
                    var df = ni.GetComponent<DaggerfallEnemy>();
                    if (df != null) df.QuestSpawn = true;
                    bound.Add(b.enemy);
                    Debug.Log("[PartyQuestFoe] Bound enemy=" + b.enemy + " party=" + b.party + " inst=" + b.inst + " localUid=" + uid);
                    yield break;
                }
            }
            yield return null;
        }
        // A quick kill can legitimately destroy the enemy before binding arrives.
        // The retained metadata still lets the leader publish its Foe item queue.
        }
        finally { bindingInProgress.Remove(b.enemy); }
    }
    static NetworkConnection FindConnection(uint player)
    {
        foreach (var c in NetworkServer.connections.Values)
            if (c != null && c.identity != null && c.identity.netId == player) return c;
        return null;
    }
}
