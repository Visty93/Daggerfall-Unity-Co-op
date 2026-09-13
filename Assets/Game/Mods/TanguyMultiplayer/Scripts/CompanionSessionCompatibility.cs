// LSO compatibility policy lives here. NetworkManager provides generic lifecycle hooks.
// Uses the original NetworkClient and NetworkServer files; no replacement of either is required.
// Install in the normal TanguyMultiplayer scripts folder, not Editor.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using Mirror;
using FullSerializer;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;

public sealed class CompanionSessionCompatibility : MonoBehaviour
{
    public string[] partyContainerNames = { "LSO Party" };
    static CompanionSessionCompatibility instance;
    readonly Dictionary<int, LocalActor> locals = new Dictionary<int, LocalActor>();
    readonly Dictionary<string, LocalActor> byToken = new Dictionary<string, LocalActor>();
    readonly Dictionary<int, ServerActor> serverActors = new Dictionary<int, ServerActor>();
    readonly Dictionary<string, ServerActor> serverImports = new Dictionary<string, ServerActor>();
    readonly Dictionary<uint, Announce> announcements = new Dictionary<uint, Announce>();
    readonly Dictionary<uint, NetworkIdentity> initializedReplicas = new Dictionary<uint, NetworkIdentity>();
    bool restorePending;
    float nextImportAttempt;

    [Serializable]
    public class Snapshot
    {
        public EnemyData_v1 state;
        public MobileEnemy mobile;
    }
    public struct ImportRequest : NetworkMessage
    {
        public string token;
        public string statePayload;
        public string behaviourLayout;
        public Guid assetId;
        public Vector3 position;
        public Quaternion rotation;
        public bool dungeon, interior;
        public int baseX, baseZ, offsetX, offsetZ;
    }
    public struct Announce : NetworkMessage
    {
        public uint netId, owner;
        public string token, statePayload;
    }
    public struct ImportRejected : NetworkMessage { public string token, reason; }

    sealed class LocalActor
    {
        public GameObject go;
        public string token = Guid.NewGuid().ToString("N");
        public Transform party;
        public bool reported, rootAfterDismissal, imported, rejected, restoring;
        public uint reporter;
        public float nextContext;
        public int lastMode = -1;
        public Snapshot snapshot;
        public Vector3 playerOffset;
        public readonly Dictionary<Behaviour, bool> paused = new Dictionary<Behaviour, bool>();
    }
    sealed class ServerActor
    {
        public GameObject go;
        public NetworkConnectionToClient owner;
        public string token;
        public Snapshot snapshot;
        public ImportRequest request;
        public bool prepared;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("Companion Session Compatibility");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<CompanionSessionCompatibility>();
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        GameObjectHelper.PreserveActorAtMultiplayerStart += PreserveAtStartup;
        DynamicEnemyAuthority.LocalActorUpdate += TickActor;
        global::Mirror.NetworkManager.ClientExtensionsReady += RegisterClientHandlers;
        global::Mirror.NetworkManager.ServerExtensionsReady += RegisterServerHandlers;
        global::Mirror.NetworkManager.ResolveClientSpawn += ResolveImportedSpawn;
        global::Mirror.NetworkManager.ClientSpawnApplied += ApplyInitialPayload;
        global::Mirror.NetworkManager.ServerBeforeReady += AnnounceForReadyConnection;
        global::Mirror.NetworkManager.SessionStopping += RetainSessionActors;
        global::Mirror.NetworkManager.SessionStopped += MarkRestorePending;
        if (NetworkClient.active) RegisterClientHandlers();
        if (NetworkServer.active) RegisterServerHandlers();
    }

    void OnDestroy()
    {
        if (instance != this) return;
        GameObjectHelper.PreserveActorAtMultiplayerStart -= PreserveAtStartup;
        DynamicEnemyAuthority.LocalActorUpdate -= TickActor;
        global::Mirror.NetworkManager.ClientExtensionsReady -= RegisterClientHandlers;
        global::Mirror.NetworkManager.ServerExtensionsReady -= RegisterServerHandlers;
        global::Mirror.NetworkManager.ResolveClientSpawn -= ResolveImportedSpawn;
        global::Mirror.NetworkManager.ClientSpawnApplied -= ApplyInitialPayload;
        global::Mirror.NetworkManager.ServerBeforeReady -= AnnounceForReadyConnection;
        global::Mirror.NetworkManager.SessionStopping -= RetainSessionActors;
        global::Mirror.NetworkManager.SessionStopped -= MarkRestorePending;
        instance = null;
    }

    Transform FindParty(Transform actor)
    {
        for (Transform parent = actor.parent; parent != null; parent = parent.parent)
            foreach (string name in partyContainerNames)
                if (!string.IsNullOrEmpty(name) && parent.name == name) return parent;
        return null;
    }

    LocalActor Track(GameObject go)
    {
        LocalActor local;
        if (!locals.TryGetValue(go.GetInstanceID(), out local))
        {
            local = new LocalActor { go = go, party = FindParty(go.transform) };
            locals.Add(go.GetInstanceID(), local);
            byToken.Add(local.token, local);
        }
        return local;
    }

    bool PreserveAtStartup(GameObject go)
    {
        if (FindParty(go.transform) == null) return false;
        LocalActor local = Track(go);
        if (local.snapshot == null)
        {
            try { local.snapshot = Capture(go); }
            catch (Exception error)
            {
                local.rejected = true;
                Debug.LogError("[MPCompanionSession] Preserved follower but cannot import its save state: " + error);
            }
        }
        // Returning true prevents the startup cleanup from destroying this object.
        return true;
    }

    void RegisterClientHandlers()
    {
        NetworkClient.RegisterHandler<Announce>(message => announcements[message.netId] = message);
        NetworkClient.RegisterHandler<ImportRejected>(message =>
        {
            LocalActor local;
            if (byToken.TryGetValue(message.token, out local)) local.rejected = true;
            Debug.LogError("[MPCompanionSession] Import rejected; original follower retained: " + message.reason);
        });
    }
    void RegisterServerHandlers()
    {
        NetworkServer.RegisterHandler<ImportRequest>((connection, request) =>
        {
            try { ImportOnServer(connection as NetworkConnectionToClient, request); }
            catch (Exception error)
            {
                Debug.LogException(error);
                connection.Send(new ImportRejected { token = request.token, reason = error.Message });
            }
        });
    }

    void Update()
    {
        if (restorePending && !NetworkClient.active && !NetworkServer.active)
        {
            FinishLocalRestore();
            return;
        }
        if (!NetworkClient.ready || PlayerMultiplayer.localPlayer == null ||
            !PlayerMultiplayer.localPlayer.isLocalPlayer || Time.unscaledTime < nextImportAttempt) return;
        nextImportAttempt = Time.unscaledTime + 0.5f;
        foreach (LocalActor local in new List<LocalActor>(locals.Values))
        {
            if (local.go == null || local.imported || local.rejected || FindParty(local.go.transform) == null) continue;
            NetworkIdentity identity = local.go.GetComponent<NetworkIdentity>();
            if (identity == null || identity.netId != 0) continue;
            EnemyWorldPosition world = local.go.GetComponent<EnemyWorldPosition>();
            bool dungeon, interior;
            int bx, bz, ox, oz;
            if (world == null || !world.TryCaptureLocalActorContext(out dungeon, out interior, out bx, out bz, out ox, out oz)) continue;
            try
            {
                Snapshot snapshot = local.snapshot ?? Capture(local.go);
                string statePayload = Encode(snapshot);
                if (System.Text.Encoding.UTF8.GetByteCount(statePayload) > 32000)
                    throw new InvalidOperationException("Compressed follower state exceeds the 32 KB import limit.");
                var request = new ImportRequest { token = local.token, statePayload = statePayload,
                    behaviourLayout = BehaviourLayout(identity), assetId = identity.assetId,
                    position = local.go.transform.position, rotation = local.go.transform.rotation,
                    dungeon = dungeon, interior = interior, baseX = bx, baseZ = bz, offsetX = ox, offsetZ = oz };
                if (NetworkServer.active)
                    ImportOnServer(NetworkServer.localConnection, request);
                else
                    NetworkClient.Send(request);
            }
            catch (Exception error)
            {
                local.rejected = true;
                Debug.LogError("[MPCompanionSession] Original follower retained; import failed: " + error);
            }
        }
    }

    void ImportOnServer(NetworkConnectionToClient owner, ImportRequest request)
    {
        if (owner == null || !owner.isReady || owner.identity == null ||
            owner.identity.GetComponent<PlayerMultiplayer>() == null) return;
        if (string.IsNullOrEmpty(request.token) || request.token.Length != 32 ||
            string.IsNullOrEmpty(request.statePayload) || System.Text.Encoding.UTF8.GetByteCount(request.statePayload) > 32000)
            throw new InvalidOperationException("Invalid follower import payload.");
        string key = owner.connectionId + ":" + request.token;
        ServerActor existing;
        if (serverImports.TryGetValue(key, out existing) && existing.go != null) return;
        Snapshot snapshot = Decode(request.statePayload);
        if (snapshot == null || snapshot.state == null || snapshot.state.currentHealth <= 0 || snapshot.state.startingHealth <= 0 ||
            snapshot.mobile.ID < 0 || snapshot.mobile.ID >= EnemyBasics.Enemies.Length)
            throw new InvalidOperationException("Follower must have a valid living enemy state.");
        LocalActor local = null;
        GameObject go;
        bool reusedLocal = owner == NetworkServer.localConnection && byToken.TryGetValue(request.token, out local);
        if (reusedLocal)
        {
            local = byToken[request.token];
            go = local.go;
        }
        else
        {
            go = GameObjectHelper.CreateEnemy(snapshot.state.gameObjectName, (MobileTypes)snapshot.mobile.ID,
                request.position, snapshot.mobile.Gender, null, snapshot.mobile.Reactions);
            go.GetComponent<DaggerfallEnemy>().LoadID = DaggerfallUnity.NextUID;
            ApplySnapshot(go, snapshot, true);
        }
        if (go == null) throw new InvalidOperationException("Original follower is no longer available.");
        NetworkIdentity importedIdentity = go.GetComponent<NetworkIdentity>();
        if (importedIdentity == null || importedIdentity.assetId != request.assetId ||
            BehaviourLayout(importedIdentity) != request.behaviourLayout)
        {
            if (!reusedLocal) Destroy(go);
            throw new InvalidOperationException("Follower prefab or NetworkBehaviour layout differs from the server enemy prefab. Original retained.");
        }
        if (reusedLocal) ResumePausedComponents(local);
        var server = new ServerActor { go = go, owner = owner, token = request.token, snapshot = snapshot, request = request };
        serverActors[go.GetInstanceID()] = server;
        serverImports[key] = server;
        // Parentage is local mod policy; the wire pose uses a scene-root object.
        Transform party = go.transform.parent;
        go.transform.SetParent(null, true);
        go.transform.SetPositionAndRotation(request.position, request.rotation);
        // A retained follower is no longer waiting to settle at its original quest marker.
        EnemyWorldPosition importedWorld = go.GetComponent<EnemyWorldPosition>();
        importedWorld.isCreateFoeWaveSpawn = false;
        importedWorld.isFixedQuestFoeRestrained = false;
        importedWorld.intendedSpawnPos = Vector3.zero;
        try
        {
            global::Mirror.NetworkManager.SpawnPreparedObject(go, owner, PrepareServerActor, AnnounceBeforeSpawn);
            if (importedIdentity.netId == 0)
                throw new InvalidOperationException("Mirror could not spawn the retained follower.");
        }
        catch
        {
            serverActors.Remove(go.GetInstanceID());
            serverImports.Remove(key);
            if (reusedLocal)
            {
                if (importedIdentity.netId != 0) NetworkServer.UnSpawn(go);
                go.transform.SetParent(party, true);
                ApplySnapshot(go, snapshot, false);
            }
            else if (importedIdentity.netId != 0) NetworkServer.Destroy(go);
            else Destroy(go);
            throw;
        }
        if (reusedLocal)
        {
            go.transform.SetParent(party, true);
            local = byToken[request.token];
            local.imported = true;
            local.snapshot = null;
            RebindSenses(go);
        }
        Debug.Log("[MPCompanionSession] Imported follower '" + go.name + "' owner=" + owner.identity.netId + " net=" + go.GetComponent<NetworkIdentity>().netId);
    }

    void PrepareServerActor(NetworkIdentity identity)
    {
        ServerActor server;
        if (!serverActors.TryGetValue(identity.gameObject.GetInstanceID(), out server) || server.prepared) return;
        server.prepared = true;
        // Undo generic fresh-spawn health/team initialization on this retained actor.
        ApplySnapshot(identity.gameObject, server.snapshot, false);
        SetupDemoEnemy setup = identity.GetComponent<SetupDemoEnemy>();
        setup.SyncedSpawnHealth = 0;
        setup.isDungeonEnemy = server.request.dungeon;
        setup.MarkInitialServerSettingsApplied();
        DynamicEnemyAuthority authority = identity.GetComponent<DynamicEnemyAuthority>();
        EnemyWorldPosition world = identity.GetComponent<EnemyWorldPosition>();
        authority.ResumeRetainedActorNetworking();
        authority.SetPersistentOwner(true, server.owner);
        var request = server.request;
        world.ApplyBoundActorContext(request.dungeon, request.interior, request.baseX, request.baseZ,
            request.offsetX, request.offsetZ, server.owner);
        world.ResumeRetainedActorNetworking();
    }

    void AnnounceBeforeSpawn(NetworkIdentity identity, NetworkConnection connection)
    {
        ServerActor server;
        if (connection == NetworkServer.localConnection ||
            !serverActors.TryGetValue(identity.gameObject.GetInstanceID(), out server)) return;
        Snapshot snapshot;
        string payload;
        try
        {
            snapshot = Capture(identity.gameObject);
            payload = Encode(snapshot);
            server.snapshot = snapshot;
        }
        catch (Exception error)
        {
            Debug.LogWarning("[MPCompanionSession] Sending last valid replica state: " + error.Message);
            payload = Encode(server.snapshot);
        }
        connection.Send(new Announce { netId = identity.netId, owner = server.owner.identity != null ? server.owner.identity.netId : 0,
            token = server.token, statePayload = payload });
    }

    void AnnounceForReadyConnection(NetworkConnection connection)
    {
        if (connection == NetworkServer.localConnection) return;
        foreach (ServerActor server in new List<ServerActor>(serverActors.Values))
        {
            if (server.go == null) continue;
            NetworkIdentity identity = server.go.GetComponent<NetworkIdentity>();
            if (identity != null && identity.netId != 0) AnnounceBeforeSpawn(identity, connection);
        }
    }

    NetworkIdentity ResolveImportedSpawn(SpawnMessage message)
    {
        Announce announce;
        if (!announcements.TryGetValue(message.netId, out announce)) return null;
        NetworkIdentity existing;
        if (NetworkClient.spawned.TryGetValue(message.netId, out existing) && existing != null) return null;

        GameObject go = ReuseSpawn(message);
        bool reused = go != null;
        if (!reused)
        {
            // Ordinary spawns never enter this path. Only announced imported actors do.
            GameObject prefab;
            if (!NetworkClient.GetPrefab(message.assetId, out prefab))
                throw new InvalidOperationException("Imported actor prefab is not registered on this client.");
            go = Instantiate(prefab, message.position, message.rotation);
        }
        LocalActor local;
        Snapshot snapshot = reused && byToken.TryGetValue(announce.token, out local) && local.snapshot != null
            ? local.snapshot : Decode(announce.statePayload);
        ApplySnapshot(go, snapshot, !reused);
        go.GetComponent<SetupDemoEnemy>().MarkInitialServerSettingsApplied();
        return go.GetComponent<NetworkIdentity>();
    }

    GameObject ReuseSpawn(SpawnMessage message)
    {
        Announce announce;
        if (!announcements.TryGetValue(message.netId, out announce)) return null;
        LocalActor local;
        if (!message.isOwner || !byToken.TryGetValue(announce.token, out local) || local.go == null) return null;
        NetworkIdentity identity = local.go.GetComponent<NetworkIdentity>();
        if (identity == null || identity.netId != 0 || identity.assetId != message.assetId) return null;
        local.party = FindParty(local.go.transform) ?? local.party;
        local.go.transform.SetParent(null, true);
        ResumePausedComponents(local);
        return local.go;
    }

    void ApplyInitialPayload(NetworkIdentity identity)
    {
        Announce announce;
        if (!announcements.TryGetValue(identity.netId, out announce)) return;
        // Spawn payloads also accompany authority changes; only initialize once.
        announcements.Remove(identity.netId);
        NetworkIdentity initialized;
        if (initializedReplicas.TryGetValue(identity.netId, out initialized) && initialized == identity) return;
        LocalActor local;
        bool reuse = byToken.TryGetValue(announce.token, out local) && local.go == identity.gameObject;
        Snapshot snapshot = reuse && local.snapshot != null ? local.snapshot : Decode(announce.statePayload);
        ApplySnapshot(identity.gameObject, snapshot, false);
        identity.GetComponent<SetupDemoEnemy>().MarkInitialServerSettingsApplied();
        initializedReplicas[identity.netId] = identity;
        if (reuse)
        {
            local.go.transform.SetParent(local.party, true);
            local.imported = true;
            local.snapshot = null;
            identity.GetComponent<DynamicEnemyAuthority>().ResumeRetainedActorNetworking();
            RebindSenses(local.go);
            Debug.Log("[MPCompanionSession] Reused original follower object '" + local.go.name + "' net=" + identity.netId);
        }
    }

    void TickActor(DynamicEnemyAuthority authority)
    {
        Transform party = FindParty(authority.transform);
        LocalActor local;
        if (party == null && !locals.TryGetValue(authority.gameObject.GetInstanceID(), out local)) return;
        local = Track(authority.gameObject);
        bool member = party != null;
        authority.HasLocalOwnershipClaim = member;
        if (!NetworkClient.active)
        {
            local.reported = false; local.reporter = 0;
            return;
        }
        PlayerMultiplayer player = PlayerMultiplayer.localPlayer;
        if (!NetworkClient.ready || !authority.isClient || authority.netId == 0 || player == null || !player.isLocalPlayer) return;
        if (local.reporter != player.netId) { local.reporter = player.netId; local.reported = false; }
        if (member) { local.party = party; local.rootAfterDismissal = false; }
        else if (local.rootAfterDismissal && authority.transform.parent != null) authority.transform.SetParent(null, true);
        EnemyWorldPosition world = authority.GetComponent<EnemyWorldPosition>();
        bool dungeon, interior;
        int bx, bz, ox, oz;
        if (!member && local.reported && authority.PersistentOwnerPlayerNetId == player.netId)
        {
            if (!authority.hasAuthority) return;
            authority.transform.SetParent(null, true);
            local.rootAfterDismissal = true;
            if (world == null || !world.TryCaptureLocalActorContext(out dungeon, out interior, out bx, out bz, out ox, out oz)) return;
            local.reported = false;
            authority.CmdReleasePersistentOwnerAtPose(authority.transform.position, authority.transform.rotation,
                dungeon, interior, bx, bz, ox, oz);
            return;
        }
        if (member != local.reported)
        {
            local.reported = member;
            authority.CmdSetPersistentOwner(member);
        }
        if (!member || !authority.hasAuthority || authority.PersistentOwnerPlayerNetId != player.netId || world == null) return;
        if (!world.TryCaptureLocalActorContext(out dungeon, out interior, out bx, out bz, out ox, out oz)) return;
        int mode = dungeon ? 2 : (interior ? 1 : 0);
        if (mode == local.lastMode && Time.unscaledTime < local.nextContext) return;
        local.lastMode = mode; local.nextContext = Time.unscaledTime + 0.1f;
        world.CmdPublishBoundActorContext(dungeon, interior, bx, bz, ox, oz);
    }

    void CaptureLocalFollowers()
    {
        foreach (DaggerfallEnemy enemy in FindObjectsOfType<DaggerfallEnemy>())
            if (FindParty(enemy.transform) != null) Track(enemy.gameObject);
    }

    bool RetainOnShutdown(NetworkIdentity identity)
    {
        if (identity == null || FindParty(identity.transform) == null) return false;
        var entity = identity.GetComponent<DaggerfallEntityBehaviour>();
        if (entity == null || entity.Entity == null || entity.Entity.CurrentHealth <= 0) return false;
        LocalActor local = Track(identity.gameObject);
        if (!local.restoring)
        {
            local.snapshot = TryCaptureForShutdown(local.go);
            local.party = FindParty(local.go.transform);
            local.playerOffset = GameManager.Instance != null && GameManager.Instance.PlayerObject != null
                ? local.go.transform.position - GameManager.Instance.PlayerObject.transform.position : Vector3.zero;
            local.restoring = true;
        }
        // Retention applies only to THIS peer's actual party members.
        // Other peers' replicas follow normal Mirror destruction.
        restorePending = true;
        return true;
    }

    void RetainSessionActors()
    {
        CaptureLocalFollowers();
        // Include inactive networked party members, and host objects whose local spawn
        // message has not arrived yet. Snapshot lists because unspawn removes entries.
        var candidates = new HashSet<NetworkIdentity>(NetworkClient.spawned.Values);
        if (NetworkServer.active)
            foreach (NetworkIdentity identity in NetworkServer.spawned.Values) candidates.Add(identity);
        foreach (NetworkIdentity identity in candidates)
            if (identity != null && RetainOnShutdown(identity))
                global::Mirror.NetworkManager.DetachRetainedObject(identity);
    }

    void MarkRestorePending()
    {
        restorePending = true;
        if (!NetworkClient.active && !NetworkServer.active) FinishLocalRestore();
    }

    void FinishLocalRestore()
    {
        foreach (LocalActor local in new List<LocalActor>(locals.Values))
        {
            if (local.go == null) continue;
            if (local.restoring || FindParty(local.go.transform) != null)
            {
                if (local.snapshot == null) local.snapshot = TryCaptureForShutdown(local.go);
                if (!local.restoring)
                {
                    local.party = FindParty(local.go.transform);
                    local.playerOffset = GameManager.Instance != null && GameManager.Instance.PlayerObject != null
                        ? local.go.transform.position - GameManager.Instance.PlayerObject.transform.position : Vector3.zero;
                }
                DynamicEnemyAuthority authority = local.go.GetComponent<DynamicEnemyAuthority>();
                if (authority != null) authority.ResetRetainedActorToLocal();
                EnemyWorldPosition world = local.go.GetComponent<EnemyWorldPosition>();
                if (world != null) world.ResetRetainedActorToLocal();
                SetupDemoEnemy setup = local.go.GetComponent<SetupDemoEnemy>();
                if (setup != null) setup.StopAllCoroutines();
                foreach (Behaviour behaviour in local.go.GetComponents<Behaviour>())
                {
                    if (behaviour is DynamicEnemyAuthority || behaviour is EnemyWorldPosition ||
                        behaviour.GetType().FullName.StartsWith("Mirror.") && !(behaviour is NetworkIdentity))
                    {
                        if (!local.paused.ContainsKey(behaviour)) local.paused.Add(behaviour, behaviour.enabled);
                        behaviour.enabled = false;
                    }
                }
                local.go.transform.SetParent(local.party, true);
                ApplySnapshot(local.go, local.snapshot, false);
                if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                    local.go.transform.position = GameManager.Instance.PlayerObject.transform.position + local.playerOffset;
                DaggerfallEnemy enemy = local.go.GetComponent<DaggerfallEnemy>();
                if (enemy != null && enemy.LoadID == 0)
                {
                    enemy.LoadID = DaggerfallUnity.NextUID;
                    SerializableEnemy serial = local.go.GetComponent<SerializableEnemy>();
                    if (serial != null) SaveLoadManager.RegisterSerializableGameObject(serial);
                }
                RebindSenses(local.go);
                Debug.Log("[MPCompanionSession] Restored original follower to SP: " + local.go.name);
            }
            local.restoring = local.imported = local.rejected = local.reported = false;
            local.reporter = 0; local.lastMode = -1; local.snapshot = null;
        }
        serverActors.Clear(); serverImports.Clear(); announcements.Clear(); initializedReplicas.Clear();
        restorePending = false;
    }

    static void ResumePausedComponents(LocalActor local)
    {
        foreach (var item in local.paused)
            if (item.Key != null) item.Key.enabled = item.Value;
        local.paused.Clear();
    }

    static string BehaviourLayout(NetworkIdentity identity)
    {
        // Use Mirror's cached ordering: this is the order used by its serializer.
        var types = new List<string>();
        foreach (NetworkBehaviour behaviour in identity.NetworkBehaviours)
            types.Add(behaviour != null ? behaviour.GetType().FullName : "<missing>");
        return string.Join("|", types.ToArray());
    }

    static Snapshot TryCaptureForShutdown(GameObject go)
    {
        try { return Capture(go); }
        catch (Exception error)
        {
            // Keeping the original object still preserves its live mod/entity state.
            // A failed snapshot must never abort Mirror shutdown or destroy that object.
            Debug.LogWarning("[MPCompanionSession] Retaining live follower without a save snapshot: " + error.Message);
            return null;
        }
    }

    static Snapshot Capture(GameObject go)
    {
        SerializableEnemy serial = go.GetComponent<SerializableEnemy>();
        EnemyData_v1 data = serial != null ? serial.GetSaveData() as EnemyData_v1 : null;
        var behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
        if (data == null || entity == null) throw new InvalidOperationException("Enemy save components are not initialized.");
        return new Snapshot { state = data, mobile = entity.MobileEnemy };
    }

    static void ApplySnapshot(GameObject go, Snapshot snapshot, bool fullRestore)
    {
        if (snapshot == null || snapshot.state == null) return;
        Vector3 position = go.transform.position;
        if (fullRestore)
        {
            go.GetComponent<SetupDemoEnemy>().ApplyEnemySettings((MobileTypes)snapshot.mobile.ID,
                snapshot.mobile.Reactions, snapshot.mobile.Gender, 0, snapshot.state.alliedToPlayer);
            snapshot.state.loadID = go.GetComponent<DaggerfallEnemy>().LoadID;
            go.GetComponent<SerializableEnemy>().RestoreSaveData(snapshot.state);
        }
        var behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
        var entity = behaviour.Entity as EnemyEntity;
        if (entity == null) throw new InvalidOperationException("Unable to initialize imported follower entity.");
        entity.SetMobileEnemy(snapshot.mobile);
        entity.MaxHealth = snapshot.state.startingHealth;
        behaviour.ApplyAuthoritativeHealthCurrentAndMax(snapshot.state.currentHealth, snapshot.state.startingHealth);
        entity.SetFatigue(snapshot.state.currentFatigue, true);
        entity.SetMagicka(snapshot.state.currentMagicka, true);
        if (snapshot.state.team > 0) entity.Team = (MobileTeams)(snapshot.state.team - 1);
        go.name = snapshot.state.gameObjectName;
        go.GetComponent<EnemyMotor>().IsHostile = snapshot.state.isHostile;
        go.transform.position = position;
    }

    const int MaxDecodedStateBytes = 2 * 1024 * 1024;
    static string Encode(Snapshot snapshot)
    {
        fsData data;
        new fsSerializer().TrySerialize(snapshot, out data).AssertSuccessWithoutWarnings();
        byte[] bytes = Encoding.UTF8.GetBytes(fsJsonPrinter.CompressedJson(data));
        if (bytes.Length > MaxDecodedStateBytes)
            throw new InvalidOperationException("Follower save state exceeds the 2 MB limit.");
        using (var output = new MemoryStream())
        {
            using (var compressor = new GZipStream(output, CompressionMode.Compress, true))
                compressor.Write(bytes, 0, bytes.Length);
            string payload = Convert.ToBase64String(output.ToArray());
            if (payload.Length > 32000)
                throw new InvalidOperationException("Compressed follower state exceeds the 32 KB import limit.");
            return payload;
        }
    }
    static Snapshot Decode(string statePayload)
    {
        using (var input = new MemoryStream(Convert.FromBase64String(statePayload)))
        using (var decompressor = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            byte[] buffer = new byte[4096];
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecodedStateBytes)
                    throw new InvalidOperationException("Follower save state exceeds the 2 MB limit.");
                output.Write(buffer, 0, read);
            }
            Snapshot snapshot = null;
            string jsonText = Encoding.UTF8.GetString(output.ToArray());
            new fsSerializer().TryDeserialize(fsJsonParser.Parse(jsonText), ref snapshot).AssertSuccessWithoutWarnings();
            return snapshot;
        }
    }
    static void RebindSenses(GameObject go)
    {
        // Re-run only the known DFU senses initialization, never mod lifecycle methods.
        EnemySenses senses = go.GetComponent<EnemySenses>();
        if (senses == null) return;
        senses.StopAllCoroutines();
        senses.Target = null;
        MethodInfo start = typeof(EnemySenses).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic);
        if (start == null) return;
        var blocked = senses.BlockedByIllusionEffectHandler;
        var see = senses.CanSeeTargetHandler;
        var hear = senses.CanHearTargetHandler;
        var detect = senses.CanDetectOtherwiseHandler;
        float fieldOfView = senses.FieldOfView;
        try { start.Invoke(senses, null); }
        finally
        {
            if (blocked != null) senses.BlockedByIllusionEffectHandler = blocked;
            if (see != null) senses.CanSeeTargetHandler = see;
            if (hear != null) senses.CanHearTargetHandler = hear;
            if (detect != null) senses.CanDetectOtherwiseHandler = detect;
            senses.FieldOfView = fieldOfView;
        }
    }
}
