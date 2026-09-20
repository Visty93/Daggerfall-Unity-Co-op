// Come Sail Away MP boarding test v5. Original adapter code; no mod implementation/assets.
// always see the real boat list, so boats can follow coordinate-origin shifts.
// Requires Mirror weaving. No inspector setup or compile-time Come Sail Away reference.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using Mirror;
using UnityEngine;

public struct CSAMPSubscribe : NetworkMessage { public byte version; }
public struct CSAMPBoatState : NetworkMessage
{
    public uint owner, boat;
    public bool removed, driving, lights, sailing;
    public int hull, variant;
    public double worldX, worldZ;
    public float y;
    public Quaternion rotation, meshRotation;
    public Vector3 meshPosition;
    public Quaternion[] sailRotations, boomRotations;
    public bool[] stowed;
    public float rowX, rowZ, rowSpeed, turn;
}

[DefaultExecutionOrder(-32000)]
public sealed class ComeSailAwayMultiplayerCompatibility : MonoBehaviour
{
    const string Prefix = "[SailMP v5] ";
    const float Units = 40f, VisibleRange = 1800f;
    const int BoatLimit = 16, PartLimit = 32;
    const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance;
    Type boatType, modType;
    FieldInfo allBoats, currentBoat;
    MethodInfo spawnBoat;
    Component mod;
    bool serverWasActive, clientWasActive, reportedBinding;
    float nextBind, nextSubscribe, nextSample, nextPrune, nextWarning;
    uint sequence;
    readonly Dictionary<GameObject, LocalBoat> locals = new Dictionary<GameObject, LocalBoat>();
    readonly Dictionary<ulong, CachedBoat> cache = new Dictionary<ulong, CachedBoat>();
    readonly Dictionary<ulong, RemoteBoat> remotes = new Dictionary<ulong, RemoteBoat>();
    readonly Dictionary<int, NetworkConnection> viewers = new Dictionary<int, NetworkConnection>();
    readonly Dictionary<int, Budget> budgets = new Dictionary<int, Budget>();
    readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
    readonly HashSet<NetworkIdentity> scannedActors = new HashSet<NetworkIdentity>();
    FieldInfo nativeParentedObjects;
    MethodInfo nativeFixedUpdate;
    IList savedPhysicsBoats, emptyPhysicsBoats;
    Component physicsMod;
    bool reportedPhysicsIsolation;
    EventInfo swimSuppressionEvent;
    Delegate swimSuppressionHandler;

    sealed class LocalBoat { public uint id; public float sent; public CSAMPBoatState last; }
    sealed class CachedBoat { public CSAMPBoatState state; public float received; }
    sealed class Budget { public float since, subscription; public int count; }
    sealed class RemoteBoat
    {
        public CSAMPBoatState state;
        public float received, retry;
        public object native;
        public GameObject root, mesh;
        public double x, z;
        public float y;
        public bool positioned, visualsApplied, driving, lights;
    }

    void Awake()
    {
        FloatingOrigin.OnPositionUpdate += OnOriginShift;
        var cleanup = gameObject.AddComponent<CSAMPActorParentCleanup>();
        cleanup.Owner = this;
        NetworkServer.ReplaceHandler<CSAMPSubscribe>(Subscribe);
        NetworkServer.ReplaceHandler<CSAMPBoatState>(ReceiveOwnerState);
        NetworkClient.ReplaceHandler<CSAMPBoatState>(ReceiveRemoteState);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindObjectOfType<ComeSailAwayMultiplayerCompatibility>() != null) return;
        var root = new GameObject("Come Sail Away MP Compatibility");
        DontDestroyOnLoad(root);
        root.AddComponent<ComeSailAwayMultiplayerCompatibility>();
    }

    void Update()
    {
        RestoreBoatPhysicsCollection();
        RemoveNetworkActorsFromBoatCleanup();
        // Mirror clears custom handlers on shutdown. Reinstall on each session transition.
        if (NetworkServer.active && !serverWasActive)
        {
            NetworkServer.ReplaceHandler<CSAMPSubscribe>(Subscribe);
            NetworkServer.ReplaceHandler<CSAMPBoatState>(ReceiveOwnerState);
        }
        if (NetworkClient.active && !clientWasActive)
            NetworkClient.ReplaceHandler<CSAMPBoatState>(ReceiveRemoteState);
        if (!NetworkServer.active && serverWasActive) { cache.Clear(); viewers.Clear(); budgets.Clear(); }
        if (!NetworkClient.active && clientWasActive) { ClearRemotes(); locals.Clear(); }
        serverWasActive = NetworkServer.active;
        clientWasActive = NetworkClient.active;
        if (!NetworkClient.active && !NetworkServer.active) return;

        if (Time.unscaledTime >= nextBind)
        {
            nextBind = Time.unscaledTime + 2f;
            Bind();
            BindPassengerSwimming();
        }
        if (NetworkClient.ready && ModAvailable() && Time.unscaledTime >= nextSubscribe)
        {
            nextSubscribe = Time.unscaledTime + 5f;
            NetworkClient.Send(new CSAMPSubscribe { version = 1 });
        }
        if (NetworkClient.ready && Time.unscaledTime >= nextSample)
        {
            nextSample = Time.unscaledTime + 0.1f;
            try { Publish(); } catch (Exception e) { Warn("Sampling boat: " + e.GetBaseException().Message); }
        }
        if (NetworkServer.active && Time.unscaledTime >= nextPrune)
        {
            nextPrune = Time.unscaledTime + 1f;
            PruneServer();
        }
    }

    bool ModAvailable()
    {
        var behaviour = mod as Behaviour;
        return behaviour != null && behaviour.isActiveAndEnabled && spawnBoat != null;
    }

    void FixedUpdate()
    {
        if (remotes.Count != 0) Physics.SyncTransforms();
        RestoreBoatPhysicsCollection();
        if (!NetworkServer.active && !NetworkClient.active) return;
        if (mod == null && Time.unscaledTime >= nextBind)
        {
            nextBind = Time.unscaledTime + 2f;
            Bind();
        }
        if (!ModAvailable()) return;
        var boats = allBoats.GetValue(mod) as IList;
        if (boats == null || boats.Count == 0) return;
        // Run before the installed mod's FixedUpdate. Network roots and the
        // enemy-model children used by player avatars both need protection.
        scannedActors.Clear();
        if (NetworkServer.active)
            foreach (var identity in NetworkServer.spawned.Values) GuardActor(identity);
        if (NetworkClient.active)
            foreach (var identity in NetworkClient.spawned.Values) GuardActor(identity);
        if (nativeFixedUpdate == null) return;
        // Suspend just the automatic MonoBehaviour physics dispatch. Its event
        // subscriptions still run, with the REAL boat list intact, so world-origin
        // updates cannot miss boats. The post-driver invokes its physics method once.
        physicsMod = mod;
        ((Behaviour)physicsMod).enabled = false;
        if (!reportedPhysicsIsolation)
        {
            reportedPhysicsIsolation = true;
            Debug.Log(Prefix + "Boat physics isolated; real boat list retained for world-origin events.");
        }
    }

    internal void FinishBoatPhysics()
    {
        if (physicsMod == null) { RestoreBoatPhysicsCollection(); return; }
        try
        {
            if (!NetworkServer.active && !NetworkClient.active) return;
            if (!physicsMod.gameObject.activeInHierarchy) return;
            savedPhysicsBoats = allBoats.GetValue(physicsMod) as IList;
            if (savedPhysicsBoats == null) return;
            if (emptyPhysicsBoats == null)
                emptyPhysicsBoats = (IList)Activator.CreateInstance(allBoats.FieldType);
            // No frame or other FixedUpdate executes between swap and restore.
            // Native wave/current physics runs; native actor parenting is skipped.
            allBoats.SetValue(physicsMod, emptyPhysicsBoats);
            nativeFixedUpdate.Invoke(physicsMod, null);
        }
        catch (Exception e) { Warn("Boat physics: " + e.GetBaseException().Message); }
        finally { RestoreBoatPhysicsCollection(); }
    }

    internal void RestoreBoatPhysicsCollection()
    {
        try
        {
            if (savedPhysicsBoats != null && physicsMod != null && allBoats != null &&
                ReferenceEquals(allBoats.GetValue(physicsMod), emptyPhysicsBoats))
            {
                foreach (object boat in emptyPhysicsBoats)
                    if (!savedPhysicsBoats.Contains(boat)) savedPhysicsBoats.Add(boat);
                allBoats.SetValue(physicsMod, savedPhysicsBoats);
            }
        }
        finally
        {
            savedPhysicsBoats = null;
            if (emptyPhysicsBoats != null) emptyPhysicsBoats.Clear();
            var behaviour = physicsMod as Behaviour;
            physicsMod = null;
            if (behaviour != null) behaviour.enabled = true;
        }
    }

    void GuardActor(NetworkIdentity identity)
    {
        if (identity == null || identity.netId == 0 || !scannedActors.Add(identity)) return;
        foreach (var enemy in identity.GetComponentsInChildren<DaggerfallEnemy>(true))
        {
            if (enemy == null) continue;
            var guard = enemy.GetComponent<CSAMPActorParentGuard>();
            if (guard == null) guard = enemy.gameObject.AddComponent<CSAMPActorParentGuard>();
            guard.Arm(this, modType);
        }
    }

    internal bool ProtectActorParents
    {
        get { return (NetworkServer.active || NetworkClient.active) && ModAvailable(); }
    }

    internal void RemoveNetworkActorsFromBoatCleanup()
    {
        if (!ProtectActorParents || nativeParentedObjects == null) return;
        var tracked = nativeParentedObjects.GetValue(mod) as IList;
        if (tracked == null) return;
        // The mod records an actor after SetParent returns. Even when the parent
        // change is immediately undone, that entry must not survive to its load
        // cleanup, which destroys tracked actors. Native SP actors are untouched.
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var actor = tracked[i] as GameObject;
            if (actor != null && actor.GetComponent<CSAMPActorParentGuard>() != null) tracked.RemoveAt(i);
        }
    }
    bool ExteriorReady()
    {
        return GameManager.HasInstance && GameManager.Instance.PlayerObject != null &&
            GameManager.Instance.PlayerGPS != null && GameManager.Instance.PlayerEnterExit != null &&
            !GameManager.Instance.PlayerEnterExit.IsPlayerInside;
    }
    void Bind()
    {
        if (mod != null) return;
        if (modType == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                modType = assembly.GetType("ComeSailAwayMod.ComeSailAway", false);
                if (modType != null) break;
            }
            if (modType == null) return;
            boatType = modType.Assembly.GetType("ComeSailAwayMod.Boat", false);
            if (boatType == null) return;
            allBoats = modType.GetField("AllBoats", Fields);
            currentBoat = modType.GetField("CurrentBoat", Fields);
            spawnBoat = modType.GetMethod("SpawnBoat", Fields, null, new[] { boatType }, null);
            nativeFixedUpdate = modType.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            nativeParentedObjects = modType.GetField("parentedObjects", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (boatType == null || allBoats == null || currentBoat == null || spawnBoat == null) return;
        mod = FindObjectOfType(modType) as Component;
        if (mod != null && !reportedBinding)
        {
            reportedBinding = true;
            Debug.Log(Prefix + "Installed mod found. Remote deck collision and native moving-platform passenger test enabled.");
        }
    }
    object Get(object boat, string name)
    {
        if (boat == null) return null;
        FieldInfo field;
        if (!fields.TryGetValue(name, out field))
        {
            field = boatType.GetField(name, Fields);
            fields[name] = field;
        }
        return field == null ? null : field.GetValue(boat);
    }
    T Read<T>(object boat, string name) { object value = Get(boat, name); return value is T ? (T)value : default(T); }
    static ulong Key(uint owner, uint boat) { return ((ulong)owner << 32) | boat; }

    void Publish()
    {
        var seen = new HashSet<GameObject>();
        if (ModAvailable() && ExteriorReady())
        {
            var boats = allBoats.GetValue(mod) as IList;
            if (boats != null)
            {
                foreach (object native in boats)
                {
                    var root = Read<GameObject>(native, "GameObject");
                    if (root == null || !root.activeInHierarchy || Read<bool>(native, "inside")) continue;
                    if (seen.Count >= BoatLimit) break;
                    seen.Add(root);
                    LocalBoat local;
                    if (!locals.TryGetValue(root, out local))
                    {
                        local = new LocalBoat { id = ++sequence, sent = -100f };
                        locals.Add(root, local);
                    }
                    var state = Capture(native, root, local.id);
                    if (Time.unscaledTime - local.sent < 1f && !Changed(local.last, state)) continue;
                    NetworkClient.Send(state);
                    local.last = state;
                    local.sent = Time.unscaledTime;
                }
            }
        }
        var gone = new List<GameObject>();
        foreach (var entry in locals)
            if (!seen.Contains(entry.Key))
            {
                NetworkClient.Send(new CSAMPBoatState { boat = entry.Value.id, removed = true });
                gone.Add(entry.Key);
            }
        foreach (var root in gone) locals.Remove(root);
    }
    CSAMPBoatState Capture(object native, GameObject root, uint id)
    {
        var game = GameManager.Instance;
        Vector3 offset = root.transform.position - game.PlayerObject.transform.position;
        var mesh = Read<GameObject>(native, "MeshObject");
        var sails = Get(native, "Sails") as IList;
        var state = new CSAMPBoatState {
            boat = id, hull = Read<int>(native, "hull"), variant = Read<int>(native, "variant"),
            driving = ReferenceEquals(currentBoat.GetValue(mod), native), lights = Read<bool>(native, "LightOn"),
            worldX = game.PlayerGPS.WorldX + (double)offset.x * Units,
            worldZ = game.PlayerGPS.WorldZ + (double)offset.z * Units,
            y = root.transform.position.y, rotation = root.transform.rotation,
            meshPosition = mesh != null ? mesh.transform.localPosition : Vector3.zero,
            meshRotation = mesh != null ? mesh.transform.localRotation : Quaternion.identity,
            sailRotations = Rotations(sails), boomRotations = Rotations(Get(native, "Booms") as IList),
            stowed = new bool[Math.Min(sails == null ? 0 : sails.Count, PartLimit)]
        };
        for (int i = 0; i < state.stowed.Length; i++)
        {
            var sail = sails[i] as Transform;
            var animator = sail == null ? null : sail.GetComponent<Animator>();
            state.stowed[i] = animator == null || animator.GetBool("Stowed");
        }
        var rudder = Read<Animator>(native, "RudderAnimator");
        if (rudder != null)
        {
            state.sailing = HasParam(rudder, "Sailing", AnimatorControllerParameterType.Bool) && rudder.GetBool("Sailing");
            state.rowX = AnimFloat(rudder, "RowX"); state.rowZ = AnimFloat(rudder, "RowZ");
            state.rowSpeed = AnimFloat(rudder, "RowSpeed"); state.turn = AnimFloat(rudder, "TurnAngle");
        }
        return state;
    }
    static bool HasParam(Animator animator, string name, AnimatorControllerParameterType type)
    {
        foreach (var p in animator.parameters) if (p.name == name && p.type == type) return true;
        return false;
    }
    static float AnimFloat(Animator animator, string name)
    { return HasParam(animator, name, AnimatorControllerParameterType.Float) ? animator.GetFloat(name) : 0f; }
    static Quaternion[] Rotations(IList list)
    {
        var result = new Quaternion[Math.Min(list == null ? 0 : list.Count, PartLimit)];
        for (int i = 0; i < result.Length; i++)
        {
            var t = list[i] as Transform;
            result[i] = t == null ? Quaternion.identity : t.localRotation;
        }
        return result;
    }
    static bool Changed(CSAMPBoatState a, CSAMPBoatState b)
    {
        return b.driving || a.driving != b.driving || a.lights != b.lights || a.hull != b.hull ||
            a.variant != b.variant || Math.Abs(a.worldX - b.worldX) > 2 || Math.Abs(a.worldZ - b.worldZ) > 2 ||
            Mathf.Abs(a.y - b.y) > 0.02f || Quaternion.Angle(a.rotation, b.rotation) > 0.5f;
    }

    Budget GetBudget(NetworkConnection connection)
    {
        Budget value;
        if (!budgets.TryGetValue(connection.connectionId, out value))
        { value = new Budget { subscription = -100 }; budgets.Add(connection.connectionId, value); }
        return value;
    }
    void Subscribe(NetworkConnection connection, CSAMPSubscribe message)
    {
        if (message.version != 1 || connection == null || connection.identity == null || !connection.isReady) return;
        var budget = GetBudget(connection);
        if (Time.unscaledTime - budget.subscription < 2f) return;
        budget.subscription = Time.unscaledTime;
        viewers[connection.connectionId] = connection;
        foreach (var record in cache.Values)
            if (record.state.owner != connection.identity.netId) connection.Send(record.state);
    }
    void ReceiveOwnerState(NetworkConnection connection, CSAMPBoatState state)
    {
        if (connection == null || connection.identity == null || !connection.isReady || state.boat == 0) return;
        var budget = GetBudget(connection);
        if (Time.unscaledTime - budget.since >= 1f) { budget.since = Time.unscaledTime; budget.count = 0; }
        if (++budget.count > 200) return;
        state.owner = connection.identity.netId;
        ulong key = Key(state.owner, state.boat);
        if (state.removed) { if (cache.Remove(key)) Relay(state); return; }
        if (!Valid(state)) return;
        CachedBoat record;
        if (!cache.TryGetValue(key, out record))
        {
            int count = 0;
            foreach (var item in cache.Values) if (item.state.owner == state.owner) count++;
            if (count >= BoatLimit) return;
            record = new CachedBoat(); cache.Add(key, record);
        }
        record.state = state; record.received = Time.unscaledTime;
        Relay(state);
    }
    void Relay(CSAMPBoatState state)
    {
        foreach (var connection in viewers.Values)
            if (connection != null && connection.isReady && connection.identity != null &&
                connection.identity.netId != state.owner) connection.Send(state);
    }
    static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    static bool ValidVector(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z) && v.sqrMagnitude < 1e8f; }
    static bool ValidRotation(Quaternion q)
    {
        float length = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
        return Finite(length) && length > 0.9f && length < 1.1f;
    }
    static bool Valid(CSAMPBoatState s)
    {
        if (s.hull < 0 || s.hull > 3 || s.variant < 0 || s.variant > 64 ||
            !Finite(s.worldX) || !Finite(s.worldZ) || Math.Abs(s.worldX) > 1e9 || Math.Abs(s.worldZ) > 1e9 ||
            !Finite(s.y) || Math.Abs(s.y) > 100000 || !ValidVector(s.meshPosition) ||
            !ValidRotation(s.rotation) || !ValidRotation(s.meshRotation) ||
            !Finite(s.rowX) || !Finite(s.rowZ) || !Finite(s.rowSpeed) || !Finite(s.turn) ||
            s.sailRotations == null || s.boomRotations == null || s.stowed == null ||
            s.sailRotations.Length > PartLimit || s.boomRotations.Length > PartLimit ||
            s.stowed.Length != s.sailRotations.Length) return false;
        foreach (var q in s.sailRotations) if (!ValidRotation(q)) return false;
        foreach (var q in s.boomRotations) if (!ValidRotation(q)) return false;
        return true;
    }
    void PruneServer()
    {
        var gone = new List<ulong>();
        foreach (var entry in cache)
            if (!NetworkServer.spawned.ContainsKey(entry.Value.state.owner) || Time.unscaledTime - entry.Value.received > 10f)
            {
                var state = entry.Value.state; state.removed = true; Relay(state); gone.Add(entry.Key);
            }
        foreach (ulong key in gone) cache.Remove(key);
        var departed = new List<int>();
        foreach (var entry in viewers)
            if (entry.Value == null || entry.Value.identity == null || !entry.Value.isReady) departed.Add(entry.Key);
        foreach (int id in departed) { viewers.Remove(id); budgets.Remove(id); }
    }
    void ReceiveRemoteState(NetworkConnection connection, CSAMPBoatState state)
    {
        ulong key = Key(state.owner, state.boat);
        RemoteBoat remote;
        if (state.removed)
        {
            if (remotes.TryGetValue(key, out remote)) DestroyRemote(remote);
            remotes.Remove(key); return;
        }
        if (!ModAvailable() || !Valid(state)) return;
        if (!remotes.TryGetValue(key, out remote))
        { remote = new RemoteBoat(); remotes.Add(key, remote); }
        if (remote.state.hull != state.hull || remote.state.variant != state.variant) DestroyRemote(remote);
        remote.state = state; remote.received = Time.unscaledTime;
    }

    void LateUpdate()
    {
        bool ready = NetworkClient.ready && ModAvailable() && ExteriorReady();
        var expired = new List<ulong>();
        foreach (var entry in remotes)
        {
            var remote = entry.Value;
            if (Time.unscaledTime - remote.received > 12f) { DestroyRemote(remote); expired.Add(entry.Key); continue; }
            if (!ready) { HideRemote(remote); continue; }
            var game = GameManager.Instance;
            double dx = (remote.state.worldX - game.PlayerGPS.WorldX) / Units;
            double dz = (remote.state.worldZ - game.PlayerGPS.WorldZ) / Units;
            if (dx*dx + dz*dz > VisibleRange*VisibleRange)
            { HideRemote(remote); remote.positioned = false; continue; }
            try
            {
                if (remote.root == null && Time.unscaledTime >= remote.retry) Build(remote);
                if (remote.root == null) continue;
                remote.root.SetActive(true);
                float blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
                if (!remote.positioned || Math.Abs(remote.x - remote.state.worldX) + Math.Abs(remote.z - remote.state.worldZ) > 4000)
                {
                    // A discontinuous owner relocation is not normal deck motion.
                    ReleasePassenger(remote.root.transform);
                    remote.x = remote.state.worldX; remote.z = remote.state.worldZ; remote.y = remote.state.y;
                }
                else
                {
                    remote.x += (remote.state.worldX - remote.x)*blend;
                    remote.z += (remote.state.worldZ - remote.z)*blend;
                    remote.y = Mathf.Lerp(remote.y, remote.state.y, blend);
                }
                Vector3 player = game.PlayerObject.transform.position;
                remote.root.transform.position = new Vector3(player.x + (float)((remote.x-game.PlayerGPS.WorldX)/Units),
                    remote.y, player.z + (float)((remote.z-game.PlayerGPS.WorldZ)/Units));
                remote.root.transform.rotation = remote.positioned ? Quaternion.Slerp(remote.root.transform.rotation, remote.state.rotation, blend) : remote.state.rotation;
                remote.positioned = true;
                ApplyVisuals(remote, blend);
            }
            catch (Exception e)
            { DestroyRemote(remote); remote.retry = Time.unscaledTime + 10f; Warn("Remote boat: " + e.GetBaseException().Message); }
        }
        foreach (ulong key in expired) remotes.Remove(key);
        if (remotes.Count != 0) Physics.SyncTransforms();
    }
    void Build(RemoteBoat remote)
    {
        remote.retry = Time.unscaledTime + 10f;
        // Ask the installed mod to construct its own assets. Never enter AllBoats,
        // never call StartSailing, and never create/save a player-owned boat entry.
        remote.native = Activator.CreateInstance(boatType, new object[] { remote.state.hull, remote.state.variant });
        try { spawnBoat.Invoke(mod, new[] { remote.native }); }
        finally { remote.root = Read<GameObject>(remote.native, "GameObject"); }
        if (remote.root == null) throw new InvalidOperationException("Installed mod did not construct a boat.");
        remote.root.name = "MP boardable boat [" + remote.state.owner + ":" + remote.state.boat + "]";
        remote.root.transform.SetParent(null, true);
        remote.mesh = Read<GameObject>(remote.native, "MeshObject");
        remote.root.AddComponent<CSAMPRemoteDeck>();
        // Keep installed solid geometry; never enable interaction triggers.
        // PlayerMotor supplies the contacted transform to PlayerGroundMotor,
        // which already handles moving platforms, rotation and walking off.
        foreach (var collider in remote.root.GetComponentsInChildren<Collider>(true))
            if (collider.isTrigger || collider is CharacterController) collider.enabled = false;
        foreach (var body in remote.root.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = true; }
        foreach (var audio in remote.root.GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); audio.enabled = false; }
        foreach (var script in remote.root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (script == null) continue;
            if (script.GetType().Name == "CollisionParenter") { script.enabled = false; Destroy(script); }
            // Animation events can call disabled behaviours. Replace the installed
            // event forwarder so observer oars cannot operate the viewer's own boat.
            if (script.GetType().Name == "RudderAnimationEventListener")
            {
                script.enabled = false;
                if (script.GetComponent<CSAMPVisualOarEvents>() == null) script.gameObject.AddComponent<CSAMPVisualOarEvents>();
                Destroy(script);
            }
        }
        var cargo = Get(remote.native, "Cargo") as Component;
        if (cargo != null) { cargo.gameObject.SetActive(false); Destroy(cargo.gameObject); }
        // Interaction markers must not appear on observer copies.
        foreach (string name in new[] { "DriveTrigger", "CargoTrigger", "StatusTrigger", "PositionTrigger", "VariantTrigger" })
        { var marker = Read<GameObject>(remote.native, name); if (marker != null) marker.SetActive(false); }
        foreach (string name in new[] { "BoardTriggers", "DoorTriggers" })
        {
            var markers = Get(remote.native, name) as IList;
            if (markers != null) foreach (var value in markers) { var marker = value as GameObject; if (marker != null) marker.SetActive(false); }
        }
        var variants = Read<GameObject>(remote.native, "VariantObject");
        if (variants != null)
            for (int i = 0; i < variants.transform.childCount; i++) variants.transform.GetChild(i).gameObject.SetActive(i == remote.state.variant);
        remote.visualsApplied = false;
        Debug.Log(Prefix + "Created boardable boat: owner=" + remote.state.owner + " boat=" + remote.state.boat + " hull=" + remote.state.hull);
    }
    void ApplyVisuals(RemoteBoat remote, float blend)
    {
        var s = remote.state;
        if (!remote.visualsApplied) blend = 1f;
        if (remote.mesh != null)
        {
            remote.mesh.transform.localPosition = Vector3.Lerp(remote.mesh.transform.localPosition, s.meshPosition, blend);
            remote.mesh.transform.localRotation = Quaternion.Slerp(remote.mesh.transform.localRotation, s.meshRotation, blend);
        }
        if (!remote.visualsApplied || remote.driving != s.driving || remote.lights != s.lights)
        {
            var animator = Read<Animator>(remote.native, "RudderAnimator");
            if (animator != null && (!remote.visualsApplied || remote.driving != s.driving))
            {
                string state = s.driving ? "Rowing" : "Disembarked";
                if (animator.HasState(0, Animator.StringToHash(state))) animator.CrossFade(state, 0.2f);
            }
            var active = Read<GameObject>(remote.native, "ActiveObject");
            var idle = Read<GameObject>(remote.native, "IdleObject");
            if (active != null) active.SetActive(s.driving);
            if (idle != null) idle.SetActive(!s.driving);
            var lights = Get(remote.native, "Lights") as IList;
            if (lights != null) foreach (var value in lights) { var light = value as Light; if (light != null) light.enabled = s.lights; }
            remote.driving = s.driving; remote.lights = s.lights; remote.visualsApplied = true;
        }
        ApplyRotations(Get(remote.native, "Booms") as IList, s.boomRotations, blend);
        var sails = Get(remote.native, "Sails") as IList;
        ApplyRotations(sails, s.sailRotations, blend);
        if (sails != null)
            for (int i = 0; i < Math.Min(sails.Count, s.stowed.Length); i++)
            {
                var t = sails[i] as Transform;
                var animator = t == null ? null : t.GetComponent<Animator>();
                if (animator != null && animator.GetBool("Stowed") != s.stowed[i])
                { animator.SetBool("Stowed", s.stowed[i]); animator.CrossFade(s.stowed[i] ? "Stowed" : "Unstowed", 0.2f); }
            }
        var rudder = Read<Animator>(remote.native, "RudderAnimator");
        if (rudder != null)
        {
            SetFloat(rudder, "RowX", s.rowX); SetFloat(rudder, "RowZ", s.rowZ);
            SetFloat(rudder, "RowSpeed", s.rowSpeed); SetFloat(rudder, "TurnAngle", s.turn);
            if (HasParam(rudder, "Sailing", AnimatorControllerParameterType.Bool)) rudder.SetBool("Sailing", s.sailing);
        }
    }
    static void SetFloat(Animator a, string name, float value)
    { if (HasParam(a, name, AnimatorControllerParameterType.Float)) a.SetFloat(name, value); }
    static void ApplyRotations(IList list, Quaternion[] values, float blend)
    {
        if (list == null || values == null) return;
        for (int i = 0; i < Math.Min(list.Count, values.Length); i++)
        { var t = list[i] as Transform; if (t != null) t.localRotation = Quaternion.Slerp(t.localRotation, values[i], blend); }
    }
    void HideRemote(RemoteBoat remote)
    {
        if (remote.root == null) return;
        ReleasePassenger(remote.root.transform);
        remote.root.SetActive(false);
    }

    static void ReleasePassenger(Transform root)
    {
        if (!GameManager.HasInstance || GameManager.Instance.PlayerObject == null) return;
        var ground = GameManager.Instance.PlayerObject.GetComponent<PlayerGroundMotor>();
        if (ground != null && ground.ActivePlatform != null &&
            ground.ActivePlatform.IsChildOf(root)) ground.ClearActivePlatform();
    }

    void OnOriginShift(Vector3 offset)
    {
        // Remote roots are outside StreamingTarget and native AllBoats. Shift
        // immediately so physics cannot see a new-origin player and old-origin
        // deck. DF coordinates stay unchanged; DFU clears its anchor on seams.
        foreach (var remote in remotes.Values)
        {
            if (remote.root == null) continue;
            ReleasePassenger(remote.root.transform);
            remote.root.transform.position += offset;
        }
        if (remotes.Count != 0) Physics.SyncTransforms();
    }

    void BindPassengerSwimming()
    {
        if (swimSuppressionEvent != null || !ModAvailable()) return;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("DeepWaters.DeepWaterPlayer", false);
            if (type == null) continue;
            var hook = type.GetEvent("ShouldSuppressOutdoorSwimming", BindingFlags.Public | BindingFlags.Static);
            if (hook == null || hook.EventHandlerType != typeof(Func<bool>)) return;
            try
            {
                var callback = new Func<bool>(IsStandingOnRemoteDeck);
                hook.AddEventHandler(null, callback);
                swimSuppressionEvent = hook;
                swimSuppressionHandler = callback;
            }
            catch (Exception e) { Warn("Passenger water hook: " + e.GetBaseException().Message); }
            return;
        }
    }

    void UnbindPassengerSwimming()
    {
        if (swimSuppressionEvent == null) return;
        try { swimSuppressionEvent.RemoveEventHandler(null, swimSuppressionHandler); }
        catch (Exception e) { Warn("Passenger water cleanup: " + e.GetBaseException().Message); }
        finally { swimSuppressionEvent = null; swimSuppressionHandler = null; }
    }

    bool IsStandingOnRemoteDeck()
    {
        if (!isActiveAndEnabled || !NetworkClient.ready || !ExteriorReady()) return false;
        var player = GameManager.Instance.PlayerObject;
        var controller = player.GetComponent<CharacterController>();
        var motor = player.GetComponent<PlayerMotor>();
        if (controller == null || !controller.enabled || !controller.detectCollisions ||
            motor == null || motor.IsLevitating || (motor.IsJumping && motor.MoveDirection.y > 0)) return false;
        // Only a real surface at the feet qualifies. A hull above the swimmer
        // or a nearby ship must not switch swimming off.
        Physics.SyncTransforms();
        var bounds = controller.bounds;
        Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        RaycastHit hit;
        if (!Physics.Raycast(foot + Vector3.up * 0.18f, Vector3.down, out hit,
            0.36f, Physics.DefaultRaycastLayers & ~(1 << player.layer), QueryTriggerInteraction.Ignore)) return false;
        return hit.normal.y > 0.5f && hit.collider.GetComponentInParent<CSAMPRemoteDeck>() != null;
    }

    void DestroyRemote(RemoteBoat remote)
    {
        if (remote.root != null) { HideRemote(remote); Destroy(remote.root); }
        remote.root = null; remote.mesh = null; remote.native = null; remote.positioned = false;
    }
    void ClearRemotes() { foreach (var remote in remotes.Values) DestroyRemote(remote); remotes.Clear(); }
    void OnDisable()
    {
        RestoreBoatPhysicsCollection();
        foreach (var remote in remotes.Values) HideRemote(remote);
        UnbindPassengerSwimming();
    }
    void OnDestroy()
    {
        FloatingOrigin.OnPositionUpdate -= OnOriginShift;
        UnbindPassengerSwimming();
        RestoreBoatPhysicsCollection(); ClearRemotes();
    }
    void Warn(string message)
    {
        if (Time.unscaledTime < nextWarning) return;
        nextWarning = Time.unscaledTime + 10f;
        Debug.LogWarning(Prefix + message);
    }
}

// The original mod still runs its normal physics update. Only its attempts to
// reparent an already-networked actor (including a player's enemy-model child)
// are reversed synchronously. Parenting by other systems is accepted normally.
public sealed class CSAMPActorParentGuard : MonoBehaviour
{
    ComeSailAwayMultiplayerCompatibility owner;
    Type modType;
    Transform savedParent;
    bool armed, restoring, reported;

    internal void Arm(ComeSailAwayMultiplayerCompatibility helper, Type installedModType)
    {
        owner = helper;
        modType = installedModType;
        if (!armed) { savedParent = transform.parent; armed = true; }
    }

    void OnTransformParentChanged()
    {
        if (!armed || restoring) return;
        if (owner == null || !owner.ProtectActorParents || !IsBoatParentingCall())
        {
            savedParent = transform.parent;
            return;
        }
        restoring = true;
        try
        {
            transform.SetParent(savedParent, true);
            if (!reported)
            {
                reported = true;
                Debug.Log("[SailMP v5] Blocked additional boat actor reparent: " + gameObject.name +
                    "; kept parent=" + (savedParent == null ? "scene root" : savedParent.name));
            }
        }
        finally { restoring = false; }
    }

    bool IsBoatParentingCall()
    {
        var trace = new System.Diagnostics.StackTrace(false);
        for (int i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i).GetMethod();
            var type = method == null ? null : method.DeclaringType;
            if (type == modType || (type != null && modType != null &&
                type.Assembly == modType.Assembly && type.FullName == "ComeSailAwayMod.CollisionParenter")) return true;
        }
        return false;
    }
}

[DefaultExecutionOrder(32000)]
public sealed class CSAMPActorParentCleanup : MonoBehaviour
{
    internal ComeSailAwayMultiplayerCompatibility Owner;
    void FixedUpdate()
    {
        if (Owner == null) return;
        Owner.FinishBoatPhysics();
        Owner.RemoveNetworkActorsFromBoatCleanup();
    }
}

// Consume visual animation events without forwarding them into the local mod singleton.
public sealed class CSAMPVisualOarEvents : MonoBehaviour
{
    public void OarEvent_In() { }
    public void OarEvent_Sweep() { }
    public void OarEvent_Out() { }
}

// Identity only: no parenting, ownership or AI behavior.
public sealed class CSAMPRemoteDeck : MonoBehaviour { }
