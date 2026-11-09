// WA runtime hull bridge. No Warm Ashes assembly dependency or encounter replay.
// Install with the accompanying WorldContainerSync on EVERY peer (wire format changed).
using System;
using System.Reflection;
using System.Collections;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using System.Globalization;
using System.Text;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Entity;
using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;
using Mirror;
using UnityEngine;

public struct WaShipRecord
{
    public uint id, model, source;
    public string boardingType, boardingData;
    public int worldX, worldZ;
    public float height;
    public Quaternion rotation;
    public Vector3 scale, boundsCenter, boundsSize;
}
public struct WaShipOffer : NetworkMessage { public WaShipRecord ship; public int token; }
public struct WaShipSnapshot : NetworkMessage { public WaShipRecord ship; public int token; public WaShipMotion motion; public WaShipPart[] parts; public uint detailsRevision; }
public struct WaShipInterest : NetworkMessage { public uint[] loaded; public uint[] revisions; }

/// <summary>
/// First observed hull wins for the session. The server stores records, never needs
/// to load remote geometry. Peers load at 3 km and retain to 3.3 km. Native WA loot
/// remains a sibling: its association is a network ID, not a Transform parent.
/// </summary>
[DefaultExecutionOrder(-100)]
public sealed class WarmAshesShipMultiplayerCompatibility : MonoBehaviour
{
    const string Tag = "[WAShipMP v3.12] ";
    public const float DiscoveryMetres = 3000f;
    const float RetainMetres = 3300f;
    const float MatchMetres = 2f;
    const float LootOriginLimit = 250f;
    const float LootBoundsMargin = 40f;
    const int MaxShips = 4096;
    const int MaxLoaded = 256;
    static WarmAshesShipMultiplayerCompatibility instance;

    sealed class ServerShip { public WaShipRecord Record; public uint Source; public WaShipMotion Motion; public WaShipPart[] Parts = new WaShipPart[0]; public uint DetailsRevision; }
    struct MotionSample { public float Time; public WaShipMotion Motion; }
    sealed class LocalShip
    {
        public WaShipRecord Record;
        public GameObject Object;
        public bool Replica;
        public float RetryAt;
        public Transform Anchor;
        public WaShipMotion Motion;
        public readonly List<MotionSample> Samples = new List<MotionSample>();
        public WaShipPart[] Parts = new WaShipPart[0];
        public uint DetailsRevision;
        public readonly Dictionary<int, string> VisualRecipes = new Dictionary<int, string>();
        public readonly Dictionary<int, GameObject> Visuals = new Dictionary<int, GameObject>();
    }
    sealed class NativeShip
    {
        public GameObject Object;
        public WaShipRecord Offer;
        public uint Id;
        public float SentAt = -10f;
        public bool Suppressed;
        public bool WasActive;
        public bool Rejected, ObservedSinking;
        public Transform Anchor;
        public WaShipMotion LastMotion;
        public float LastMotionAt, NearQuestUntil;
        public uint DetailsRevision;
        public string DetailSignature;
        public readonly Dictionary<GameObject, bool> Pieces = new Dictionary<GameObject, bool>();
        public readonly Dictionary<GameObject, bool> HiddenPieces = new Dictionary<GameObject, bool>();
    }
    sealed class Budget { public float Start; public int Count; }
    readonly Dictionary<uint, ServerShip> server = new Dictionary<uint, ServerShip>();
    readonly Dictionary<uint, LocalShip> local = new Dictionary<uint, LocalShip>();
    readonly Dictionary<int, NativeShip> native = new Dictionary<int, NativeShip>();
    readonly Dictionary<NetworkConnection, Budget> budgets = new Dictionary<NetworkConnection, Budget>();
    readonly HashSet<uint> warnedModels = new HashSet<uint>();
    uint nextId;
    float nextScan, nextInterest;
    bool wasActive;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("WarmAshesShipMultiplayerCompatibility");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<WarmAshesShipMultiplayerCompatibility>();
        Debug.Log(Tag + "Installed: 3000m discovery, 3300m retention; models 864098-864101.");
    }

    static bool Connected
    {
        get { return NetworkClient.active && NetworkClient.isConnected && NetworkClient.ready &&
            NetworkClient.connection != null && NetworkClient.connection.isAuthenticated; }
    }
    static bool Outside
    {
        get
        {
            var gm = GameManager.Instance;
            return gm != null && gm.PlayerObject != null && gm.PlayerGPS != null &&
                gm.StreamingWorld != null && gm.PlayerEnterExit != null && !gm.PlayerEnterExit.IsPlayerInside &&
                !(SaveLoadManager.HasInstance && SaveLoadManager.Instance.LoadInProgress);
        }
    }
    static bool ModelAllowed(uint model) { return model >= 864098 && model <= 864101; }
    static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
    static bool Finite(Quaternion q) { return Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w); }

    void Update()
    {
        bool active = NetworkServer.active || NetworkClient.active;
        if (!active)
        {
            if (wasActive) ResetSession();
            wasActive = false;
            return;
        }
        wasActive = true;
        TraceLoadState();
        // Re-install after Mirror resets handlers on stop/start. No authenticated
        // message is sent until the local connection is ready.
        if (NetworkServer.active)
        {
            NetworkServer.ReplaceHandler<WaShipOffer>(ReceiveOffer);
            NetworkServer.ReplaceHandler<WaShipInterest>(ReceiveInterest);
        }
        InstallRuntimeHandlers();
        TickInteractions();
        if (NetworkClient.active) NetworkClient.ReplaceHandler<WaShipSnapshot>(ReceiveSnapshot);
        if (!Connected) return;
        if (!Outside) { UnloadLocal(); return; }
        if (GameManager.Instance.StreamingWorld.IsWorldUpdateRunning) return;
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 1f;
            NetworkClient.Send(new WaShipAreaClaim());
            Discover();
            ScanAssemblies();
            Maintain();
        }
        if (Time.unscaledTime >= nextInterest)
        {
            nextInterest = Time.unscaledTime + 2f;
            var loaded = new List<uint>();
            var revisions = new List<uint>();
            foreach (var pair in local)
                if ((HullReady(pair.Key) || pair.Value.Motion.removed) && loaded.Count < MaxLoaded)
                { loaded.Add(pair.Key); revisions.Add(pair.Value.DetailsRevision); }
            NetworkClient.Send(new WaShipInterest { loaded = loaded.ToArray(), revisions = revisions.ToArray() });
        }
    }

    void LateUpdate()
    {
        if (!Connected || !Outside || GameManager.Instance.StreamingWorld.IsWorldUpdateRunning) return;
        ObserveNativeMotion();
        AnimateReplicas();
    }

    static bool ReadModel(string name, out uint model)
    {
        model = 0;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("WA_Model [", StringComparison.Ordinal)) return false;
        int end = name.IndexOf(']', 10);
        return end > 10 && uint.TryParse(name.Substring(10, end - 10), out model) && ModelAllowed(model);
    }

    void Discover()
    {
        Transform root = GameManager.Instance.StreamingWorld.StreamingTarget;
        // WA's roots live under StreamingTarget. Never search retained dungeons,
        // assets, prefabs or inactive terrain trees using Resources.FindObjectsOfTypeAll.
        foreach (Transform t in root.GetComponentsInChildren<Transform>())
        {
            uint model;
            if (!ReadModel(t.name, out model) || t.GetComponent<WaShipReplica>() != null) continue;
            if (t.GetComponentInParent<DaggerfallDungeon>() != null ||
                t.GetComponentInParent<DaggerfallInterior>() != null) continue;
            NativeShip n;
            int token = t.gameObject.GetInstanceID();
            if (!native.TryGetValue(token, out n))
            {
                var r = Capture(t, model);
                if (!Valid(r))
                {
                    if (warnedModels.Add(model))
                        Debug.LogWarning(Tag + "Cannot describe native " + t.name +
                            ": missing mesh bounds or unsupported transform. Associated loot waits for a valid hull.");
                    continue;
                }
                if (DistanceToLocal(r) > DiscoveryMetres) continue;
                if (native.Count >= MaxShips) break;
                n = new NativeShip { Object = t.gameObject, Offer = r, WasActive = t.gameObject.activeSelf,
                    Anchor = CreateAnchor(r, "Source") };
                native.Add(token, n);
            }
            // Re-adopt on return from an interior/streaming unload. Otherwise a
            // retained native hull and a newly made replica could overlap.
            if (n.Rejected || n.Suppressed) continue;
            if (n.Id != 0)
            {
                LocalShip known;
                if (local.TryGetValue(n.Id, out known)) AdoptNative(known, n);
                continue;
            }
            if (n.Suppressed || Time.unscaledTime - n.SentAt < 3f) continue;
            n.SentAt = Time.unscaledTime;
            NetworkClient.Send(new WaShipOffer { ship = n.Offer, token = token });
        }
        foreach (var pair in new List<KeyValuePair<int, NativeShip>>(native))
            if (pair.Value.Object == null && pair.Value.Id == 0)
            { DestroyAnchor(pair.Value.Anchor); native.Remove(pair.Key); }
    }

    static WaShipRecord Capture(Transform t, uint model)
    {
        var gm = GameManager.Instance;
        Vector3 delta = t.position - gm.PlayerObject.transform.position;
        var r = new WaShipRecord { model = model,
            worldX = (int)Math.Round(gm.PlayerGPS.WorldX + delta.x * 40.0),
            worldZ = (int)Math.Round(gm.PlayerGPS.WorldZ + delta.z * 40.0),
            height = t.position.y - gm.StreamingWorld.WorldCompensation.y,
            rotation = t.rotation, scale = t.lossyScale };
        var boarding = new WaShipPart();
        CaptureSettings(t, ref boarding, true);
        if (boarding.talkData == null || boarding.talkData.Length <= 8192)
        { r.boardingType = boarding.talkType; r.boardingData = boarding.talkData; }
        // Store bounds in the unscaled hull's local frame. This also works for
        // tilted wrecks and loot below sea level. Exclude particles/billboards.
        bool any = false;
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        foreach (MeshFilter mesh in t.GetComponentsInChildren<MeshFilter>())
        {
            if (mesh.sharedMesh == null || mesh.GetComponentInParent<DaggerfallBillboard>() != null ||
                IsWaAssemblyObject(mesh.transform, t) ||
                mesh.GetComponentInParent<DaggerfallLoot>() != null ||
                mesh.GetComponentInParent<DaggerfallWorkshop.Game.Entity.DaggerfallEntityBehaviour>() != null) continue;
            Bounds b = mesh.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = t.InverseTransformPoint(mesh.transform.TransformPoint(corner));
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }
        r.boundsCenter = bounds.center;
        r.boundsSize = bounds.size;
        return r;
    }

    static bool Valid(WaShipRecord r)
    {
        float norm = r.rotation.x * r.rotation.x + r.rotation.y * r.rotation.y +
            r.rotation.z * r.rotation.z + r.rotation.w * r.rotation.w;
        return (r.boardingType == null || r.boardingType.Length <= 256) &&
            (r.boardingData == null || r.boardingData.Length <= 8192) && ModelAllowed(r.model) && r.worldX >= 0 && r.worldZ >= 0 &&
            r.worldX <= 100000000 && r.worldZ <= 100000000 && Finite(r.height) && Mathf.Abs(r.height) <= 200000 &&
            Finite(r.rotation) && norm > 0.9f && norm < 1.1f && Finite(r.scale) &&
            r.scale.x >= 0.01f && r.scale.y >= 0.01f && r.scale.z >= 0.01f &&
            r.scale.x <= 100f && r.scale.y <= 100f && r.scale.z <= 100f &&
            Finite(r.boundsCenter) && Finite(r.boundsSize) && r.boundsCenter.magnitude <= 10000f &&
            r.boundsSize.sqrMagnitude > 0.01f && r.boundsSize.x >= 0 && r.boundsSize.y >= 0 && r.boundsSize.z >= 0 &&
            Vector3.Scale(r.boundsSize, r.scale).magnitude <= 500f;
    }

    static double Horizontal(int ax, int az, int bx, int bz)
    {
        double dx = ((double)ax - bx) / 40.0, dz = ((double)az - bz) / 40.0;
        return Math.Sqrt(dx * dx + dz * dz);
    }
    static double DistanceToLocal(WaShipRecord r)
    {
        var gps = GameManager.Instance.PlayerGPS;
        return Horizontal(r.worldX, r.worldZ, gps.WorldX, gps.WorldZ);
    }
    static PositionMultiplayer Player(NetworkConnection conn)
    {
        if (conn == null || !conn.isReady || !conn.isAuthenticated || conn.identity == null) return null;
        var p = conn.identity.GetComponent<PositionMultiplayer>();
        if (p == null || p.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.BuildingInterior ||
            p.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.DungeonInterior) return null;
        return p;
    }
    bool WithinBudget(NetworkConnection conn)
    {
        Budget b;
        if (!budgets.TryGetValue(conn, out b)) budgets[conn] = b = new Budget();
        if (Time.unscaledTime - b.Start >= 10f) { b.Start = Time.unscaledTime; b.Count = 0; }
        return ++b.Count <= 80;
    }

    void ReceiveOffer(NetworkConnection conn, WaShipOffer m)
    {
        var p = Player(conn);
        if (p == null || !WithinBudget(conn) || !Valid(m.ship) ||
            Horizontal(m.ship.worldX, m.ship.worldZ, p.x, p.z) > RetainMetres) return;
        ServerShip match = null;
        foreach (var s in server.Values)
        {
            if (Horizontal(m.ship.worldX, m.ship.worldZ, s.Record.worldX, s.Record.worldZ) > MatchMetres ||
                Mathf.Abs(m.ship.height - s.Record.height) > 3f) continue;
            // Same physical origin but differing locally random variant: first wins.
            if (match != null) return; // Ambiguous: do not merge two real objects.
            match = s;
        }
        if (match == null)
        {
            if (!AcceptPopulation(conn, m.ship))
            { conn.Send(new WaShipRejected { token = m.token }); return; }
            if (server.Count >= MaxShips) return;
            var r = m.ship;
            r.id = ++nextId;
            r.source = conn.identity.netId;
            match = new ServerShip { Record = r, Source = r.source,
                Motion = new WaShipMotion { id = r.id, rotation = r.rotation, scale = r.scale } };
            server.Add(r.id, match);
            Debug.Log(Tag + "Registered hull id=" + r.id + " model=" + r.model + " DF=" + r.worldX + "/" + r.worldZ);
        }
        conn.Send(Snapshot(match, m.token));
    }

    void ReceiveInterest(NetworkConnection conn, WaShipInterest m)
    {
        var p = Player(conn);
        if (p == null || m.loaded == null || m.loaded.Length > MaxLoaded || !WithinBudget(conn)) return;
        var loaded = new Dictionary<uint, uint>();
        if (m.revisions == null || m.revisions.Length != m.loaded.Length) return;
        for (int i = 0; i < m.loaded.Length; i++) loaded[m.loaded[i]] = m.revisions[i];
        int sent = 0;
        foreach (var s in server.Values)
        {
            if (Horizontal(s.Record.worldX, s.Record.worldZ, p.x, p.z) > DiscoveryMetres) continue;
            uint revision;
            if (loaded.TryGetValue(s.Record.id, out revision) && revision == s.DetailsRevision) continue;
            conn.Send(Snapshot(s, 0));
            if (++sent >= 64) break;
        }
        foreach (var c in new List<NetworkConnection>(budgets.Keys))
            if (c == null || !c.isReady) budgets.Remove(c);
    }

    void ReceiveSnapshot(WaShipSnapshot m)
    {
        if (!Connected || !Outside || m.ship.id == 0 || !Valid(m.ship) || DistanceToLocal(m.ship) > RetainMetres ||
            !ValidMotion(m.motion) || m.motion.id != m.ship.id || !ValidParts(m.parts)) return;
        try
        {
            LocalShip s;
            if (!local.TryGetValue(m.ship.id, out s))
            {
                if (local.Count >= MaxShips) return;
                local[m.ship.id] = s = new LocalShip { Record = m.ship };
            }
            if (m.motion.revision >= s.Motion.revision) SetMotion(s, m.motion);
            if (m.detailsRevision >= s.DetailsRevision)
            { s.Parts = m.parts; s.DetailsRevision = m.detailsRevision; }
            NativeShip n;
            if (m.token != 0 && native.TryGetValue(m.token, out n) && n.Object != null)
            {
                AdoptNative(s, n);
            }
            if (s.Motion.removed) RemoveReplica(s);
            else { EnsureHull(s); ApplyParts(s); }
        }
        catch (Exception ex) { Debug.LogWarning(Tag + "Snapshot deferred safely: " + ex.Message); }
    }

    void AdoptNative(LocalShip s, NativeShip n)
    {
        n.Id = s.Record.id;
        if (IsLocalSource(s.Record) && !s.Motion.removed && !n.Suppressed &&
            (s.Object == null || s.Replica))
        {
            RemoveReplica(s);
            s.Object = n.Object;
            s.Replica = false;
            // Never write WA's native Transform: WA owns sinking and cleanup.
        }
        else if (s.Object != n.Object) SuppressNative(n);
    }

    void Maintain()
    {
        foreach (var s in local.Values)
        {
            if (s.Motion.removed || DistanceToLocal(s.Record) > RetainMetres)
            { RemoveReplica(s); continue; }
            if (DistanceToLocal(s.Record) <= DiscoveryMetres || s.Object != null)
            { EnsureHull(s); ApplyParts(s); }
            if (s.Replica && s.Object != null) RepairBoardingAdapters(s);
        }
        foreach (var n in native.Values)
            if (n.Suppressed) EnforceSuppression(n);
    }

    void EnsureHull(LocalShip s)
    {
        if (s.Motion.removed || (s.Object != null && s.Object.activeInHierarchy)) return;
        // An accepted native ship is owned by WA. Do not resurrect it locally
        // while WA removes or unloads it; the next native scan can re-adopt it.
        if (IsLocalSource(s.Record)) return;
        if (Time.unscaledTime < s.RetryAt) return;
        s.RetryAt = Time.unscaledTime + 5f;
        // WA may have streamed its native root out. The shared record survives;
        // a separate replica is independent of the discovering player's cleanup.
        GameObject go = null;
        try
        {
            if (s.Anchor == null) s.Anchor = CreateAnchor(s.Record, "Replica");
            Transform parent = s.Anchor;
            go = MeshReplacement.ImportCustomGameobject(s.Record.model, parent, Matrix4x4.identity);
            if (go == null)
                go = GameObjectHelper.CreateDaggerfallMeshGameObject(s.Record.model, parent);
            if (go == null) return;
            // Preserve WA's model identity for its installed activation code.
            // WaShipReplica, not the display name, excludes this from discovery.
            go.name = "WA_Model [" + s.Record.model + "]";
            go.AddComponent<WaShipReplica>();
            go.transform.localPosition = s.Motion.offset;
            go.transform.localRotation = s.Motion.rotation;
            go.transform.localScale = s.Motion.scale;
            // A missing model must not count as a hull. Do not reveal treasure
            // beside an empty placeholder. Custom prefab colliders are preserved.
            bool mesh = false, solid = false;
            foreach (MeshFilter f in go.GetComponentsInChildren<MeshFilter>())
                if (f.sharedMesh != null && f.sharedMesh.vertexCount > 0) mesh = true;
            foreach (Collider c in go.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger && (!(c is MeshCollider) || ((MeshCollider)c).sharedMesh != null)) solid = true;
            if (!mesh || !solid) throw new InvalidOperationException("model has no usable mesh/solid collider");
            // Do not allow a replacement prefab's incidental treasure/enemies to
            // become a second encounter. Only the hull asset is reproduced here.
            foreach (DaggerfallLoot loot in go.GetComponentsInChildren<DaggerfallLoot>(true))
                loot.gameObject.SetActive(false);
            foreach (DaggerfallWorkshop.Game.Entity.DaggerfallEntityBehaviour entity in
                go.GetComponentsInChildren<DaggerfallWorkshop.Game.Entity.DaggerfallEntityBehaviour>(true))
                entity.gameObject.SetActive(false);
            // A same-ID fallback can still be the wrong variant if WA changed
            // its prefab or assembled extra pieces. Fail closed instead of
            // unlocking loot next to visibly different collision geometry.
            WaShipRecord rebuilt = Capture(go.transform, s.Record.model);
            Vector3 expectedSize = Vector3.Scale(s.Record.boundsSize, s.Record.scale);
            Vector3 actualSize = Vector3.Scale(rebuilt.boundsSize, rebuilt.scale);
            Vector3 expectedCenter = Vector3.Scale(s.Record.boundsCenter, s.Record.scale);
            Vector3 actualCenter = Vector3.Scale(rebuilt.boundsCenter, rebuilt.scale);
            float tolerance = 2f + expectedSize.magnitude * 0.05f;
            if (Vector3.Distance(expectedSize, actualSize) > tolerance ||
                Vector3.Distance(expectedCenter, actualCenter) > tolerance)
                throw new InvalidOperationException("reconstructed bounds differ from WA's discovered hull");
            // Billboard children are described by the assembly snapshot, including
            // removals. Hide prefab copies so they cannot survive a raid transition.
            foreach (var billboard in go.GetComponentsInChildren<DaggerfallBillboard>(true))
                if (billboard.gameObject != go) billboard.gameObject.SetActive(false);
            s.Object = go;
            s.Replica = true;
            if (!string.IsNullOrEmpty(s.Record.boardingType))
            {
                try
                {
                    // Install the forwarding component first: PlayerActivate checks only
                    // the hit object's first IPlayerActivable, not its parents.
                    var boarding = go.AddComponent<WaShipBoardingActivation>();
                    boarding.Ship = s.Record.id;
                    bool restored = RestoreSettings(new WaShipPart { talkType = s.Record.boardingType, talkData = s.Record.boardingData }, go, true);
                    foreach (var component in go.GetComponents<Component>())
                        if (component != null && BoardingType(component.GetType())) boarding.Target = component as IPlayerActivable;
                    foreach (var collider in go.GetComponentsInChildren<Collider>(true))
                    {
                        if (collider.gameObject == go || collider.isTrigger || !collider.enabled ||
                            collider.GetComponent<IPlayerActivable>() != null) continue;
                        var forward = collider.gameObject.AddComponent<WaShipBoardingActivation>();
                        forward.Ship = s.Record.id; forward.Target = boarding.Target;
                    }
                    Debug.Log(Tag + "Replica boarding hull=" + s.Record.id + " component=" + s.Record.boardingType + " restored=" + restored);
                }
                catch (Exception ex) { Debug.LogWarning(Tag + "Boarding component unavailable: " + ex.Message); }
            }
            Debug.Log(Tag + "Loaded hull id=" + s.Record.id + " model=" + s.Record.model);
        }
        catch (Exception ex)
        {
            if (go != null) Destroy(go);
            if (warnedModels.Add(s.Record.model))
                Debug.LogWarning(Tag + "Cannot reconstruct model " + s.Record.model + ": " + ex.Message +
                    ". Associated loot stays hidden. Check matching WA assets on this peer.");
        }
    }

    static void Place(Transform t, WaShipRecord r)
    {
        var gm = GameManager.Instance;
        var p = gm.PlayerObject.transform.position;
        p.x += (float)((r.worldX - (double)gm.PlayerGPS.WorldX) / 40.0);
        p.z += (float)((r.worldZ - (double)gm.PlayerGPS.WorldZ) / 40.0);
        p.y = r.height + gm.StreamingWorld.WorldCompensation.y;
        t.SetPositionAndRotation(p, r.rotation);
        Vector3 parent = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(r.scale.x / parent.x, r.scale.y / parent.y, r.scale.z / parent.z);
    }

    public static bool HullReady(uint id)
    {
        LocalShip s;
        return id != 0 && instance != null && Connected && Outside && instance.local.TryGetValue(id, out s) &&
            !s.Motion.removed && s.Object != null && s.Object.activeInHierarchy && DistanceToLocal(s.Record) <= RetainMetres;
    }

    public static bool IsWaLoot(DaggerfallLoot loot)
    {
        if (loot == null) return false;
        if (loot.GetComponent<WaShipLootGate>() != null) return true;
        for (Transform t = loot.transform; t != null; t = t.parent)
            if (t.name == "WA_Loot" || t.name.StartsWith("WA_Loot ", StringComparison.Ordinal) ||
                t.name.StartsWith("WA_Loot(", StringComparison.Ordinal)) return true;
        return false;
    }

    static float BoundsDistance(WaShipRecord r, int wx, int wz, float height)
    {
        Vector3 delta = new Vector3((float)((wx - (double)r.worldX) / 40.0), height - r.height,
            (float)((wz - (double)r.worldZ) / 40.0));
        Vector3 rotated = Quaternion.Inverse(r.rotation) * delta;
        Vector3 center = Vector3.Scale(r.boundsCenter, r.scale);
        Vector3 size = Vector3.Scale(r.boundsSize, r.scale);
        return Vector3.Distance(rotated, new Bounds(center, size).ClosestPoint(rotated));
    }

    public static bool TryLootShip(DaggerfallLoot loot, out uint id)
    {
        id = 0;
        if (!IsWaLoot(loot)) return true; // Ordinary exterior treasure is unchanged.
        var gate = loot.GetComponent<WaShipLootGate>();
        if (gate == null) gate = loot.gameObject.AddComponent<WaShipLootGate>();
        if (instance == null || !Outside || instance.BelongsToRejectedShip(loot)) return false;
        if (gate.ShipId != 0) { id = gate.ShipId; return HullReady(id); }
        var gm = GameManager.Instance;
        Vector3 p = loot.transform.position;
        int wx = (int)Math.Round(gm.PlayerGPS.WorldX + (p.x - gm.PlayerObject.transform.position.x) * 40.0);
        int wz = (int)Math.Round(gm.PlayerGPS.WorldZ + (p.z - gm.PlayerObject.transform.position.z) * 40.0);
        float height = p.y - gm.StreamingWorld.WorldCompensation.y;
        float best = float.MaxValue;
        bool ambiguous = false;
        foreach (var s in instance.local.Values)
        {
            if (!HullReady(s.Record.id) || Horizontal(wx, wz, s.Record.worldX, s.Record.worldZ) > LootOriginLimit) continue;
            float distance = BoundsDistance(s.Record, wx, wz, height);
            if (distance > LootBoundsMargin) continue;
            if (Mathf.Abs(distance - best) < 2f) ambiguous = true;
            else if (distance < best) { best = distance; id = s.Record.id; ambiguous = false; }
        }
        if (ambiguous || id == 0) { id = 0; return false; }
        gate.ShipId = id;
        return true;
    }

    public static void BindLoot(DaggerfallLoot loot, uint id)
    {
        if (loot == null || id == 0) return;
        var gate = loot.GetComponent<WaShipLootGate>();
        if (gate == null) gate = loot.gameObject.AddComponent<WaShipLootGate>();
        gate.ShipId = id;
    }

    public static bool ValidateLoot(NetworkConnection conn, uint id, int wx, int wz, float height, bool creating)
    {
        ServerShip s;
        var p = Player(conn);
        return id != 0 && instance != null && p != null && instance.server.TryGetValue(id, out s) && !s.Motion.removed &&
            (!creating || s.Source == conn.identity.netId) &&
            Horizontal(wx, wz, p.x, p.z) <= RetainMetres &&
            Horizontal(wx, wz, s.Record.worldX, s.Record.worldZ) <= LootOriginLimit &&
            BoundsDistance(s.Record, wx, wz, height) <= LootBoundsMargin + 2f;
    }

    public static Transform LootParent(uint id)
    {
        return HullReady(id) ? GameManager.Instance.StreamingWorld.StreamingTarget : null;
    }
    public static void AddInterestAreas(List<string> areas, int maximum)
    {
        if (instance == null || !Outside) return;
        // Include hull and nearby loot pixels even beyond local terrain distance.
        foreach (var s in instance.local.Values)
        {
            if (!HullReady(s.Record.id)) continue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var pixel = MapsFile.WorldCoordToMapPixel(s.Record.worldX + dx * 10000, s.Record.worldZ + dz * 10000);
                    string area = "e:" + pixel.X + ":" + pixel.Y;
                    if (areas.Count < maximum && !areas.Contains(area)) areas.Add(area);
                }
        }
    }

    void UnloadLocal()
    {
        foreach (var s in local.Values)
        {
            RemoveReplica(s);
        }
    }
    void ResetSession()
    {
        UnloadLocal();
        foreach (var n in native.Values)
        {
            DestroyAnchor(n.Anchor);
            foreach (var piece in n.HiddenPieces) if (piece.Key != null) piece.Key.SetActive(piece.Value);
            if (n.Object != null && n.Suppressed) n.Object.SetActive(n.WasActive);
        }
        WaShipLootGate.ReleaseAll();
        server.Clear(); local.Clear(); native.Clear(); budgets.Clear(); warnedModels.Clear();
        population.Clear(); claimTimes.Clear(); diagnostics.Clear();
        ClearInteractions();
        nextId = 0; nextScan = nextInterest = 0;
    }
    const int CellUnits = 10240; // 256 metres. Claim a player-centred disk, not a disk per hull.
    const int MaxParts = 96;
    readonly Dictionary<long, uint> population = new Dictionary<long, uint>();
    readonly Dictionary<NetworkConnection, float> claimTimes = new Dictionary<NetworkConnection, float>();
    readonly HashSet<string> diagnostics = new HashSet<string>();
    bool questHook;

    void InstallRuntimeHandlers()
    {
        InstallInteractionHandlers();
        if (!questHook) { QuestMachine.OnQuestEnded += QuestEnded; QuestMachine.OnQuestStarted += QuestStarted; questHook = true; }
        if (NetworkServer.active)
        {
            NetworkServer.ReplaceHandler<WaShipAreaClaim>(ReceiveClaim);
            NetworkServer.ReplaceHandler<WaShipMotion>(ReceiveMotion);
            NetworkServer.ReplaceHandler<WaShipDetails>(ReceiveDetails);
        }
        if (NetworkClient.active)
        {
            NetworkClient.ReplaceHandler<WaShipRejected>(ReceiveRejected);
            NetworkClient.ReplaceHandler<WaShipMotion>(ApplyMotion);
            NetworkClient.ReplaceHandler<WaShipDetails>(ApplyDetails);
        }
    }
    void OnDestroy() { if (questHook) { QuestMachine.OnQuestEnded -= QuestEnded; QuestMachine.OnQuestStarted -= QuestStarted; } }
    static long CellKey(int x, int z) { return ((long)x << 32) | (uint)z; }
    void ReceiveClaim(NetworkConnection conn, WaShipAreaClaim message)
    {
        var p = Player(conn);
        if (p == null || p.x <= 0 || p.z <= 0) return;
        float last;
        if (claimTimes.TryGetValue(conn, out last) && Time.unscaledTime - last < .8f) return;
        claimTimes[conn] = Time.unscaledTime;
        int cx = p.x / CellUnits, cz = p.z / CellUnits;
        for (int x = cx - 12; x <= cx + 12; x++)
            for (int z = cz - 12; z <= cz + 12; z++)
            {
                if (x < 0 || z < 0 || Horizontal(x * CellUnits + CellUnits / 2,
                    z * CellUnits + CellUnits / 2, p.x, p.z) > DiscoveryMetres + 181f) continue;
                long key = CellKey(x, z);
                if (!population.ContainsKey(key) && population.Count < 65536)
                    population.Add(key, conn.identity.netId);
            }
    }
    bool AcceptPopulation(NetworkConnection conn, WaShipRecord record)
    {
        uint owner;
        return population.TryGetValue(CellKey(record.worldX / CellUnits, record.worldZ / CellUnits), out owner)
            && owner == conn.identity.netId;
    }
    static bool IsLocalSource(WaShipRecord record)
    {
        return NetworkClient.connection != null && NetworkClient.connection.identity != null &&
            record.source == NetworkClient.connection.identity.netId;
    }
    static WaShipSnapshot Snapshot(ServerShip s, int token)
    {
        return new WaShipSnapshot { ship = s.Record, token = token, motion = s.Motion,
            parts = s.Parts, detailsRevision = s.DetailsRevision };
    }
    void Broadcast<T>(ServerShip ship, T message) where T : struct, NetworkMessage
    {
        foreach (var conn in NetworkServer.connections.Values)
        {
            var p = Player(conn);
            if (p != null && Horizontal(ship.Record.worldX, ship.Record.worldZ, p.x, p.z) <= RetainMetres)
                conn.Send(message);
        }
    }
    static bool UnitQuaternion(Quaternion q)
    {
        float n = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
        return Finite(q) && n > .9f && n < 1.1f;
    }
    static bool ValidMotion(WaShipMotion m)
    {
        return m.id != 0 && Finite(m.offset) && m.offset.magnitude < 2000f && UnitQuaternion(m.rotation) &&
            Finite(m.scale) && m.scale.x > 0 && m.scale.y > 0 && m.scale.z > 0 &&
            m.scale.x <= 100 && m.scale.y <= 100 && m.scale.z <= 100;
    }
    // DFU material keys use a signed-short archive and byte record. Mod archives
    // (WA uses 3101/3102 and 10011+) are not limited to three digits.
    static bool ValidParts(WaShipPart[] parts)
    {
        if (parts == null || parts.Length > MaxParts) return false;
        var keys = new HashSet<int>();
        foreach (var p in parts)
            if ((p.sourceName != null && p.sourceName.Length > 256) || !keys.Add(p.key) || !Finite(p.position) || p.position.magnitude > 500 ||
                !Finite(p.scale) || p.scale.magnitude > 200 || !UnitQuaternion(p.rotation) ||
                (p.billboard ? p.archive < 0 || p.archive > short.MaxValue || p.record < 0 || p.record > byte.MaxValue : p.model > 999999 || ModelAllowed(p.model)) ||
                (p.talkData != null && p.talkData.Length > 8192) || (p.talkType != null && p.talkType.Length > 256)) return false;
        return true;
    }
    void ReceiveMotion(NetworkConnection conn, WaShipMotion m)
    {
        ServerShip s;
        if (Player(conn) == null || !ValidMotion(m) || !server.TryGetValue(m.id, out s) ||
            s.Source != conn.identity.netId || m.revision <= s.Motion.revision || s.Motion.removed) return;
        s.Motion = m;
        Broadcast(s, m);
    }
    void ReceiveDetails(NetworkConnection conn, WaShipDetails m)
    {
        ServerShip s;
        if (Player(conn) == null || !ValidParts(m.parts) || !server.TryGetValue(m.id, out s) ||
            s.Source != conn.identity.netId || m.revision <= s.DetailsRevision || s.Motion.removed || !WithinBudget(conn)) return;
        s.Parts = m.parts; s.DetailsRevision = m.revision;
        Broadcast(s, m);
    }
    void ApplyMotion(WaShipMotion m)
    {
        LocalShip s;
        if (!ValidMotion(m) || !local.TryGetValue(m.id, out s) || m.revision < s.Motion.revision) return;
        SetMotion(s, m);
        if (m.removed) RemoveReplica(s);
    }
    void ApplyDetails(WaShipDetails m)
    {
        LocalShip s;
        if (!ValidParts(m.parts) || !local.TryGetValue(m.id, out s) || m.revision <= s.DetailsRevision) return;
        s.Parts = m.parts; s.DetailsRevision = m.revision;
        ApplyParts(s);
    }
    static Transform CreateAnchor(WaShipRecord r, string kind)
    {
        var t = new GameObject("MP_WA_" + kind + "Anchor").transform;
        t.parent = GameManager.Instance.StreamingWorld.StreamingTarget;
        Place(t, r);
        t.rotation = Quaternion.identity;
        t.localScale = Vector3.one;
        return t;
    }
    static void DestroyAnchor(Transform anchor) { if (anchor != null) Destroy(anchor.gameObject); }
    void RemoveReplica(LocalShip s)
    {
        foreach (var go in s.Visuals.Values) if (go != null) Destroy(go);
        s.Visuals.Clear(); s.VisualRecipes.Clear();
        if (s.Replica && s.Object != null) Destroy(s.Object);
        s.Object = null;
        DestroyAnchor(s.Anchor); s.Anchor = null;
    }
    void SetMotion(LocalShip s, WaShipMotion m)
    {
        if (s.Samples.Count == 0 || m.revision > s.Motion.revision)
        {
            s.Samples.Add(new MotionSample { Time = Time.unscaledTime, Motion = m });
            if (s.Samples.Count > 16) s.Samples.RemoveAt(0);
        }
        s.Motion = m;
    }
    void AnimateReplicas()
    {
        float renderTime = Time.unscaledTime - .2f;
        foreach (var s in local.Values)
        {
            if (!s.Replica || s.Object == null || s.Motion.removed) continue;
            while (s.Samples.Count > 2 && s.Samples[1].Time <= renderTime) s.Samples.RemoveAt(0);
            var pose = s.Motion;
            if (s.Samples.Count > 0)
            {
                pose = s.Samples[0].Motion;
                for (int i = 1; i < s.Samples.Count; i++)
                {
                    var a = s.Samples[i-1]; var b = s.Samples[i];
                    float f = Mathf.Clamp01((renderTime - a.Time) / Mathf.Max(.001f, b.Time - a.Time));
                    pose.offset = Vector3.Lerp(a.Motion.offset, b.Motion.offset, f);
                    pose.rotation = Quaternion.Slerp(a.Motion.rotation, b.Motion.rotation, f);
                    pose.scale = Vector3.Lerp(a.Motion.scale, b.Motion.scale, f);
                    if (renderTime <= b.Time) break;
                }
            }
            var t = s.Object.transform;
            t.localPosition = pose.offset;
            t.localRotation = pose.rotation;
            t.localScale = pose.scale;
        }
    }
    bool lastLoadBusy;
    void TraceLoadState()
    {
        bool busy = SaveLoadManager.HasInstance && SaveLoadManager.Instance.LoadInProgress;
        if (busy == lastLoadBusy) return;
        lastLoadBusy = busy;
        Debug.Log(Tag + "Save load " + (busy ? "started" : "finished") + " peer=" + LocalPeerId() +
            " serverShips=" + server.Count + " localShips=" + local.Count + " nativeShips=" + native.Count + " areaClaims=" + population.Count);
    }
    static string ObjectPath(Transform t)
    {
        string path = ""; int depth = 0;
        while (t != null && depth++ < 7) { path = t.name + (path.Length == 0 ? "" : "/" + path); t = t.parent; }
        return path;
    }
    void RepairBoardingAdapters(LocalShip ship)
    {
        IPlayerActivable target = null;
        foreach (var c in ship.Object.GetComponents<Component>())
            if (c != null && BoardingType(c.GetType())) { target = c as IPlayerActivable; break; }
        if (target == null) return;
        foreach (var collider in ship.Object.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled || collider.isTrigger) continue;
            var adapter = collider.GetComponent<WaShipBoardingActivation>();
            if (adapter == null)
            {
                // Preserve other interactions; only fill empty collider activation routes.
                if (collider.GetComponent<IPlayerActivable>() != null) continue;
                adapter = collider.gameObject.AddComponent<WaShipBoardingActivation>();
                Debug.Log(Tag + "Repaired boarding collider ship=" + ship.Record.id + " object=" + ObjectPath(collider.transform));
            }
            adapter.Ship = ship.Record.id; adapter.Target = target;
        }
    }

    readonly Dictionary<ulong, uint> questShipLinks = new Dictionary<ulong, uint>();
    static uint LocalPeerId()
    {
        return NetworkClient.connection != null && NetworkClient.connection.identity != null ? NetworkClient.connection.identity.netId : 0;
    }
    static bool ShipQuest(Quest q) { return q != null && !string.IsNullOrEmpty(q.QuestName) && q.QuestName.StartsWith("WAQ_SHIP", StringComparison.OrdinalIgnoreCase); }
    void QuestStarted(Quest q)
    {
        if (!Connected || !ShipQuest(q)) return;
        // Exact association only when WA starts this quest inside our captain relay.
        if (ownerInteraction.ship != 0 && questShipLinks.Count < MaxShips) questShipLinks[q.UID] = ownerInteraction.ship;
    }
    void CompleteLinkedSinking(Quest quest)
    {
        // Only an exact captain-relay link can select a hull. Never guess by proximity.
        uint id; LocalShip ship;
        if (!ShipQuest(quest) || !questShipLinks.TryGetValue(quest.UID, out id) ||
            !local.TryGetValue(id, out ship) || ship.Replica || ship.Record.source != LocalPeerId() ||
            ship.Object == null || ship.Motion.removed) return;
        try
        {
            object sink = null;
            foreach (var state in quest.GetTaskStates())
            {
                if (!state.set) continue;
                var task = quest.GetTask(state.symbol);
                if (task == null) continue;
                foreach (var action in task.Actions)
                    if (action != null && action.GetType().FullName == "DaggerfallWorkshop.Game.Questing.Actions.SinkShip")
                        sink = action;
            }
            if (sink == null) return; // Refusal and unrelated endings cannot sink a ship.
            Type sunkType = null;
            foreach (var type in sink.GetType().Assembly.GetTypes())
                if (type.Name == "SunkShip" && typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    if (sunkType != null) throw new InvalidOperationException("Ambiguous SunkShip type");
                    sunkType = type;
                }
            if (sunkType == null) { Debug.LogWarning(Tag + "Linked sinking unavailable ship=" + id + " SunkShip missing"); return; }
            if (ship.Object.GetComponent(sunkType) == null)
            {
                ship.Object.AddComponent(sunkType);
                Debug.Log(Tag + "Linked sinking started ship=" + id + " quest=" + quest.UID + " type=" + sunkType.FullName);
            }
            else Debug.Log(Tag + "Linked sinking already active ship=" + id + " quest=" + quest.UID);
        }
        catch (Exception ex) { Debug.LogWarning(Tag + "Linked sinking failed ship=" + id + " " + ex.Message); }
    }
    void QuestEnded(Quest quest)
    {
        if (!Connected || !Outside || quest == null || string.IsNullOrEmpty(quest.QuestName) ||
            !quest.QuestName.StartsWith("WAQ_SHIP", StringComparison.OrdinalIgnoreCase)) return;
        CompleteLinkedSinking(quest);
        NativeShip closest = null; double best = 250;
        foreach (var n in native.Values)
        {
            if (n.Id == 0 || n.Suppressed) continue;
            double distance = DistanceToLocal(n.Offer);
            if (distance < best) { best = distance; closest = n; }
        }
        if (closest != null) closest.NearQuestUntil = Time.unscaledTime + 30f;
        Debug.Log(Tag + "Ship quest ended: " + quest.QuestName + " nearbyNative=" + (closest == null ? 0 : closest.Id));
    }
    void ObserveNativeMotion()
    {
        foreach (var n in native.Values)
        {
            LocalShip s;
            if (n.Id == 0 || n.Suppressed || n.Anchor == null || !local.TryGetValue(n.Id, out s) ||
                !IsLocalSource(s.Record) || s.Motion.removed || Time.unscaledTime - n.LastMotionAt < .1f) continue;
            n.LastMotionAt = Time.unscaledTime;
            var m = s.Motion;
            bool exists = n.Object != null && n.Object.activeInHierarchy;
            if (!exists)
            {
                // Streaming alone must not delete the shared encounter. Require a nearby
                // source and observed sinking or a recently completed WA ship quest.
                if (DistanceToLocal(n.Offer) > 250 || (!n.ObservedSinking && Time.unscaledTime > n.NearQuestUntil)) continue;
                m.removed = true;
            }
            else
            {
                m.offset = n.Object.transform.position - n.Anchor.position;
                m.rotation = n.Object.transform.rotation;
                m.scale = n.Object.transform.lossyScale;
                if (m.offset.y < -.5f) n.ObservedSinking = true;
            }
            if (!ValidMotion(m) || (m.removed == n.LastMotion.removed &&
                Vector3.Distance(m.offset, n.LastMotion.offset) < .01f &&
                Quaternion.Angle(m.rotation, n.LastMotion.rotation) < .05f &&
                Vector3.Distance(m.scale, n.LastMotion.scale) < .001f)) continue;
            m.revision = Math.Max(s.Motion.revision, n.LastMotion.revision) + 1;
            n.LastMotion = m;
            s.Motion = m;
            NetworkClient.Send(m);
            if (m.removed) Debug.Log(Tag + "Source removed hull id=" + n.Id + "; tombstone sent.");
        }
    }
    void ReceiveRejected(WaShipRejected m)
    {
        NativeShip n;
        if (!native.TryGetValue(m.token, out n)) return;
        n.Rejected = true;
        SuppressNative(n);
        Debug.Log(Tag + "Suppressed foreign population token=" + m.token + " model=" + n.Offer.model);
    }
    static bool Excluded(Transform t)
    {
        return t.GetComponentInParent<WaShipReplica>() != null ||
            t.GetComponentInParent<DaggerfallEntityBehaviour>() != null ||
            t.GetComponentInParent<NetworkIdentity>() != null;
    }
    bool NearNative(NativeShip n, Transform t, float margin)
    {
        if (n.Object == null || t == n.Object.transform || Excluded(t)) return false;
        Vector3 p = n.Object.transform.InverseTransformPoint(t.position);
        return Vector3.Distance(p, new Bounds(n.Offer.boundsCenter, n.Offer.boundsSize).ClosestPoint(p)) <= margin;
    }
    IEnumerable<Transform> Candidates(NativeShip n)
    {
        if (n.Object == null) yield break;
        // Scan the scene root, but select narrowly by actual hull bounds. This also
        // finds WA's sibling objects rather than assuming everything is a hull child.
        var root = GameManager.Instance.StreamingWorld.StreamingTarget;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            uint model;
            if (ReadModel(t.name, out model)) continue;
            bool child = t != n.Object.transform && t.IsChildOf(n.Object.transform);
            if (child)
            {
                if (t.GetComponentInParent<WaShipReplica>() != null ||
                    t.GetComponentInParent<DaggerfallEntityBehaviour>() != null) continue;
            }
            else if (!NearNative(n, t, 12f)) continue;
            if (t.GetComponent<DaggerfallBillboard>() != null || t.GetComponent<DaggerfallMesh>() != null ||
                t.name.StartsWith("WA_", StringComparison.Ordinal) || t.IsChildOf(n.Object.transform)) yield return t;
        }
    }
    void SuppressNative(NativeShip n)
    {
        if (n.Object == null) return;
        foreach (var t in Candidates(n))
        {
            // Only known renderable objects/loot; never deactivate a shared streaming parent.
            if (t.GetComponent<DaggerfallBillboard>() == null && t.GetComponent<DaggerfallMesh>() == null &&
                t.GetComponent<DaggerfallLoot>() == null && !t.name.StartsWith("WA_Loot", StringComparison.Ordinal)) continue;
            if (!n.HiddenPieces.ContainsKey(t.gameObject)) n.HiddenPieces.Add(t.gameObject, t.gameObject.activeSelf);
        }
        n.Suppressed = true;
        EnforceSuppression(n);
    }
    void EnforceSuppression(NativeShip n)
    {
        // Rescan to catch delayed NPC/loot creation after the hull's offer was rejected.
        if (n.Object != null)
            foreach (var t in Candidates(n))
                if ((t.GetComponent<DaggerfallBillboard>() != null || t.GetComponent<DaggerfallMesh>() != null ||
                    t.GetComponent<DaggerfallLoot>() != null || t.name.StartsWith("WA_Loot", StringComparison.Ordinal)) &&
                    !n.HiddenPieces.ContainsKey(t.gameObject)) n.HiddenPieces.Add(t.gameObject, t.gameObject.activeSelf);
        foreach (var p in n.HiddenPieces) if (p.Key != null) p.Key.SetActive(false);
        if (n.Object != null) n.Object.SetActive(false);
    }
    bool BelongsToRejectedShip(DaggerfallLoot loot)
    {
        foreach (var n in native.Values)
            if (n.Suppressed && (n.HiddenPieces.ContainsKey(loot.gameObject) || NearNative(n, loot.transform, LootBoundsMargin))) return true;
        return false;
    }
    static bool PartModel(Transform t, out uint model)
    {
        model = 0;
        string name = t.name;
        int start = name.IndexOf("[ID=", StringComparison.Ordinal);
        int count = 4;
        if (start < 0 && name.StartsWith("WA_Model [", StringComparison.Ordinal)) { start = 9; count = 1; }
        if (start < 0)
        {
            var mesh = t.GetComponent<MeshFilter>();
            return mesh != null && mesh.sharedMesh != null && uint.TryParse(mesh.sharedMesh.name, out model) &&
                model > 0 && model <= 999999 && !ModelAllowed(model);
        }
        int end = name.IndexOf(']', start + count);
        return end > start + count && uint.TryParse(name.Substring(start + count, end - start - count), out model) && !ModelAllowed(model);
    }
    void ScanAssemblies()
    {
        foreach (var n in native.Values)
        {
            LocalShip s;
            if (n.Id == 0 || n.Suppressed || n.Object == null || !n.Object.activeInHierarchy ||
                !local.TryGetValue(n.Id, out s) || !IsLocalSource(s.Record) || s.Motion.removed) continue;
            if (diagnostics.Add("shipCallbacks:" + n.Id)) LogUnknown(n.Object.transform);
            var parts = new List<WaShipPart>();
            foreach (var t in Candidates(n))
            {
                if (t.GetComponentInParent<DaggerfallLoot>() != null || !t.gameObject.activeInHierarchy) continue;
                var billboard = t.GetComponent<DaggerfallBillboard>();
                uint model = 0;
                bool hasModel = PartModel(t, out model);
                // Meshes already embedded in the hull asset are instantiated with that asset.
                if (billboard == null && (!hasModel || (t.IsChildOf(n.Object.transform) && !IsWaAssemblyObject(t, n.Object.transform))))
                {
                    if (!hasModel) LogUnknown(t);
                    continue;
                }
                var p = new WaShipPart { key = t.gameObject.GetInstanceID(), model = model, sourceName = t.name,
                    billboard = billboard != null, position = n.Object.transform.InverseTransformPoint(t.position),
                    rotation = Quaternion.Inverse(n.Object.transform.rotation) * t.rotation,
                    scale = Divide(t.lossyScale, n.Object.transform.lossyScale) };
                if (billboard != null) { p.archive = billboard.Summary.Archive; p.record = billboard.Summary.Record; p.faceY = billboard.FaceY; }
                p.interactive = HasInteraction(n, t);
                try { CaptureTalk(t, ref p); }
                catch (Exception ex)
                {
                    if (diagnostics.Add("talk:" + t.name)) Debug.LogWarning(Tag + "Talk metadata unavailable: " + ex.Message);
                    p.talkType = p.talkData = null;
                }
                if (t.name.StartsWith("WA_ShipNPC", StringComparison.Ordinal) && string.IsNullOrEmpty(p.talkType)) LogUnknown(t);
                if (p.talkData != null && p.talkData.Length > 8192) p.talkType = p.talkData = null;
                if (!ValidParts(new[] { p }))
                {
                    if (diagnostics.Add("invalid:" + t.name + ":" + p.archive + ":" + p.record))
                        Debug.LogWarning(Tag + "Skipped invalid part " + t.name + " archive=" + p.archive + " record=" + p.record + " model=" + p.model);
                    continue;
                }
                parts.Add(p);
                if (parts.Count == MaxParts) break;
            }
            parts.Sort((a,b) => a.key.CompareTo(b.key));
            var values = parts.ToArray();
            if (!ValidParts(values)) continue;
            string signature = Signature(values);
            if (signature != n.DetailSignature)
            { n.DetailSignature = signature; n.DetailsRevision++; }
            else if (s.DetailsRevision >= n.DetailsRevision) continue;
            NetworkClient.Send(new WaShipDetails { id = n.Id, revision = n.DetailsRevision, parts = values });
            Debug.Log(Tag + "Assembly id=" + n.Id + " revision=" + n.DetailsRevision + " parts=" + parts.Count);
        }
    }
    void LogUnknown(Transform t)
    {
        var names = new List<string>();
        foreach (var c in t.GetComponents<Component>()) if (c != null) names.Add(c.GetType().FullName);
        var mesh = t.GetComponent<MeshFilter>();
        string description = t.name + " mesh=" + (mesh != null && mesh.sharedMesh != null ? mesh.sharedMesh.name : "none") +
            " components=" + string.Join(",", names.ToArray());
        foreach (var c in t.GetComponents<Component>())
            if (IsWa(c))
                foreach (var method in c.GetType().GetMethods(ReflectInstance | BindingFlags.DeclaredOnly))
                    if (method.Name.IndexOf("Activ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        method.Name.IndexOf("Click", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        method.Name.IndexOf("Enter", StringComparison.OrdinalIgnoreCase) >= 0)
                        description += " callback=" + method.ToString();
        if (diagnostics.Count < 80 && diagnostics.Add(description)) Debug.Log(Tag + "Unmapped ship object: " + description);
    }
    static Vector3 Divide(Vector3 a, Vector3 b) { return new Vector3(a.x/b.x, a.y/b.y, a.z/b.z); }
    static string Signature(WaShipPart[] parts)
    {
        var text = new StringBuilder();
        foreach (var p in parts)
        {
            text.Append(p.sourceName).Append(p.interactive).Append(p.talkType).Append(p.talkData).Append(p.key).Append(':').Append(p.model).Append(':').Append(p.archive).Append(':').Append(p.record).Append(':').Append(p.faceY);
            // Billboards turn to face the camera; this is not an authoritative NPC state change.
            foreach (float f in new[] { p.position.x,p.position.y,p.position.z,p.scale.x,p.scale.y,p.scale.z,
                p.billboard ? 0 : p.rotation.x,p.billboard ? 0 : p.rotation.y,p.billboard ? 0 : p.rotation.z,p.billboard ? 1 : p.rotation.w })
                text.Append(':').Append(Math.Round(f, 2).ToString(CultureInfo.InvariantCulture));
            text.Append(';');
        }
        return text.ToString();
    }
    void ApplyParts(LocalShip s)
    {
        if (!s.Replica || s.Object == null || s.Motion.removed) return;
        var live = new HashSet<int>();
        foreach (var p in s.Parts)
        {
            live.Add(p.key);
            GameObject go;
            string recipe = p.sourceName + ":" + p.billboard + ":" + p.model + ":" + p.archive + ":" + p.record + ":" + p.interactive + ":" + p.talkType + ":" + p.talkData;
            string previous;
            if (s.VisualRecipes.TryGetValue(p.key, out previous) && previous != recipe &&
                s.Visuals.TryGetValue(p.key, out go) && go != null) Destroy(go);
            if (!s.Visuals.TryGetValue(p.key, out go) || go == null)
            {
                try
                {
                    go = p.billboard ? GameObjectHelper.CreateDaggerfallBillboardGameObject(p.archive, p.record, s.Object.transform) :
                        MeshReplacement.ImportCustomGameobject(p.model, s.Object.transform, Matrix4x4.identity);
                    if (go == null && !p.billboard) go = GameObjectHelper.CreateDaggerfallMeshGameObject(p.model, s.Object.transform);
                    if (go == null) continue;
                    go.name = string.IsNullOrEmpty(p.sourceName) ? "MP_WA_Detail_" + p.key : p.sourceName;
                    go.AddComponent<WaShipReplica>();
                    // Visual replicas must not start a second local WA event.
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                    foreach (var e in go.GetComponentsInChildren<DaggerfallEntityBehaviour>(true)) e.gameObject.SetActive(false);
                    foreach (var l in go.GetComponentsInChildren<DaggerfallLoot>(true)) l.gameObject.SetActive(false);
                    s.Visuals[p.key] = go; s.VisualRecipes[p.key] = recipe;
                    ConfigureInteraction(s, p, go);
                }
                catch (Exception ex) { if (diagnostics.Add("part:" + p.model + ":" + p.archive + ":" + p.record)) Debug.LogWarning(Tag + "Part unavailable: " + ex.Message); continue; }
            }
            go.transform.localPosition = p.position;
            if (!p.billboard) go.transform.localRotation = p.rotation;
            go.transform.localScale = p.scale;
            var b = go.GetComponent<DaggerfallBillboard>();
            if (b != null) b.FaceY = p.faceY;
        }
        foreach (var pair in new List<KeyValuePair<int, GameObject>>(s.Visuals))
            if (!live.Contains(pair.Key)) { if (pair.Value != null) Destroy(pair.Value); s.Visuals.Remove(pair.Key); s.VisualRecipes.Remove(pair.Key); }
    }

    // WA's renamed child objects are separate event pieces, not the base hull asset.
    static bool IsWaAssemblyObject(Transform t, Transform hull)
    {
        for (; t != null && t != hull; t = t.parent)
            if (t.name.StartsWith("WA_ShipDetail", StringComparison.Ordinal) ||
                t.name.StartsWith("WA_ShipNPC", StringComparison.Ordinal)) return true;
        return false;
    }
    static readonly BindingFlags ReflectInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static bool TalkType(Type type)
    {
        return type != null && (string.IsNullOrEmpty(type.Namespace) || type.Namespace.StartsWith("WarmAshes", StringComparison.Ordinal)) &&
            type.Name.Replace("_", "").Equals("WATalkNPC", StringComparison.OrdinalIgnoreCase);
    }
    static bool BoardingType(Type type)
    {
        return type != null && (string.IsNullOrEmpty(type.Namespace) || type.Namespace.StartsWith("WarmAshes", StringComparison.Ordinal)) &&
            type.Name.Replace("_", "").Equals("WAEnterShip", StringComparison.Ordinal) && typeof(IPlayerActivable).IsAssignableFrom(type);
    }
    static bool Scalar(Type type) { return type == typeof(string) || type.IsEnum || type == typeof(bool) || type == typeof(int) || type == typeof(float); }
    static void CaptureTalk(Transform t, ref WaShipPart part) { CaptureSettings(t, ref part, false); }
    static void CaptureSettings(Transform t, ref WaShipPart part, bool boarding)
    {
        foreach (var component in t.GetComponents<Component>())
        {
            if (component == null || !(boarding ? BoardingType(component.GetType()) : TalkType(component.GetType()))) continue;
            var fields = new List<WaTalkField>();
            foreach (var field in component.GetType().GetFields(ReflectInstance))
            {
                if (field.IsStatic || field.IsInitOnly || field.IsNotSerialized || !Scalar(field.FieldType) ||
                    (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField)))) continue;
                object value = field.GetValue(component);
                fields.Add(new WaTalkField { name = field.Name, value = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) });
            }
            part.talkType = component.GetType().FullName;
            part.talkData = JsonUtility.ToJson(new WaTalkFields { fields = fields.ToArray() });
            return;
        }
    }
    bool RestoreTalk(WaShipPart part, GameObject go) { return RestoreSettings(part, go, false); }
    bool RestoreSettings(WaShipPart part, GameObject go, bool boarding)
    {
        if (string.IsNullOrEmpty(part.talkType)) return false;
        Type type = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        { type = assembly.GetType(part.talkType, false); if (type != null) break; }
        if (!(boarding ? BoardingType(type) : TalkType(type)) || !typeof(MonoBehaviour).IsAssignableFrom(type))
        {
            if (diagnostics.Add("missingTalk:" + part.talkType)) Debug.LogWarning(Tag + "Installed talk component not found: " + part.talkType);
            return false;
        }
        var data = JsonUtility.FromJson<WaTalkFields>(part.talkData);
        if (data == null || data.fields == null || data.fields.Length > 64) return false;
        // Inactive construction lets serialized settings arrive before Awake/Start.
        go.SetActive(false);
        Component component = go.AddComponent(type);
        try
        {
            foreach (var item in data.fields)
            {
                var field = type.GetField(item.name, ReflectInstance);
                if (field == null || field.IsStatic || field.IsInitOnly || field.IsNotSerialized || !Scalar(field.FieldType) ||
                    (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField)))) continue;
                object value = field.FieldType.IsEnum ? Enum.Parse(field.FieldType, item.value) :
                    Convert.ChangeType(item.value, field.FieldType, CultureInfo.InvariantCulture);
                field.SetValue(component, value);
            }
            go.SetActive(true);
            return true;
        }
        catch
        {
            // Do not leave a partially configured talk script enabled.
            var behaviour = component as Behaviour; if (behaviour != null) behaviour.enabled = false;
            Destroy(component); go.SetActive(true); throw;
        }
    }
    static bool IsWa(Component component)
    {
        if (component == null) return false;
        Type type = component.GetType();
        if (type.Namespace != null && type.Namespace.StartsWith("WarmAshes", StringComparison.Ordinal)) return true;
        string name = type.Name.Replace("_", "");
        return string.IsNullOrEmpty(type.Namespace) && (name == "WATalkNPC" || name == "WAEnterShip");
    }
    static PlayerActivate.CustomActivation RegisteredActivation(Transform t)
    {
        var field = typeof(PlayerActivate).GetField("customModActivations", BindingFlags.Static | BindingFlags.NonPublic);
        var map = field == null ? null : field.GetValue(null) as IDictionary;
        string name = t.name; int end = name.IndexOf(']'); if (end >= 0) name = name.Substring(0, end + 1);
        if (map == null || !map.Contains(name)) return null;
        object entry = map[name]; var actionField = entry.GetType().GetField("Action", ReflectInstance);
        var action = actionField == null ? null : actionField.GetValue(entry) as PlayerActivate.CustomActivation;
        if (action == null || action.Method.DeclaringType == null || action.Method.DeclaringType.Namespace == null ||
            !action.Method.DeclaringType.Namespace.StartsWith("WarmAshes", StringComparison.Ordinal)) return null;
        return action;
    }
    static bool HasInteraction(NativeShip n, Transform t)
    {
        foreach (var c in t.GetComponents<Component>()) if (IsWa(c) && c is IPlayerActivable) return true;
        if (RegisteredActivation(t) != null) return true;
        // Candidate only; owner resolves the real callback before invoking anything.
        var b = t.GetComponent<DaggerfallBillboard>();
        return b != null && ((b.Summary.Archive == 195 && b.Summary.Record == 1) ||
            (b.Summary.Archive == 3101 && b.Summary.Record == 93));
    }
    void ConfigureInteraction(LocalShip s, WaShipPart part, GameObject go)
    {
        if (!part.interactive && string.IsNullOrEmpty(part.talkType)) return;
        var mesh = go.GetComponent<MeshFilter>();
        if (mesh == null || mesh.sharedMesh == null) return;
        var collider = go.AddComponent<BoxCollider>();
        collider.center = mesh.sharedMesh.bounds.center;
        var size = mesh.sharedMesh.bounds.size; size.z = Mathf.Max(.1f, size.z);
        collider.size = size; collider.isTrigger = true;
        try { if (RestoreTalk(part, go)) return; }
        catch (Exception ex) { Debug.LogWarning(Tag + "Talk component unavailable: " + ex.Message); }
        var activator = go.AddComponent<WaShipNpcActivation>();
        activator.Ship = s.Record.id; activator.Part = part.key;
    }

    sealed class InteractionRoute
    {
        public uint token, ship, requester, owner, step;
        public int part;
        public float expires;
        public bool prompted;
        public int[] buttons;
    }
    readonly Dictionary<uint, InteractionRoute> interactionRoutes = new Dictionary<uint, InteractionRoute>();
    uint nextInteraction;
    WaShipInteract ownerInteraction;
    float ownerInteractionUntil;
    object ownerPreviousWindow;
    DaggerfallMessageBox ownerPrompt, remotePrompt;
    bool ownerPromptSent;
    uint ownerStep;
    float ownerTransitionUntil;
    uint remoteToken;
    float nextInteractionRequest;

    void InstallInteractionHandlers()
    {
        if (NetworkServer.active)
        {
            NetworkServer.ReplaceHandler<WaShipInteract>(RouteInteraction);
            NetworkServer.ReplaceHandler<WaShipPrompt>(RoutePrompt);
            NetworkServer.ReplaceHandler<WaShipAnswer>(RouteAnswer);
        }
        if (NetworkClient.active)
        {
            NetworkClient.ReplaceHandler<WaShipInteract>(RunInteraction);
            NetworkClient.ReplaceHandler<WaShipPrompt>(ShowRemotePrompt);
            NetworkClient.ReplaceHandler<WaShipAnswer>(AnswerOriginalPrompt);
        }
    }
    static NetworkConnection Connection(uint id)
    {
        foreach (var c in NetworkServer.connections.Values) if (c != null && c.identity != null && c.identity.netId == id) return c;
        return null;
    }
    public static void ActivateReplica(uint ship, int part, float distance)
    {
        if (instance == null || !Connected || !Outside || distance > 5f || !HullReady(ship) || Time.unscaledTime < instance.nextInteractionRequest) return;
        instance.nextInteractionRequest = Time.unscaledTime + 1f;
        NetworkClient.Send(new WaShipInteract { ship = ship, part = part });
    }
    void RouteInteraction(NetworkConnection conn, WaShipInteract m)
    {
        var player = Player(conn); ServerShip ship;
        if (player == null || !WithinBudget(conn) || !server.TryGetValue(m.ship, out ship) || ship.Motion.removed) return;
        WaShipPart? target = null;
        foreach (var p in ship.Parts) if (p.key == m.part && p.interactive) { target = p; break; }
        if (!target.HasValue) return;
        var offset = ship.Motion.offset + ship.Motion.rotation * Vector3.Scale(target.Value.position, ship.Motion.scale);
        if (Horizontal(ship.Record.worldX + (int)(offset.x * 40), ship.Record.worldZ + (int)(offset.z * 40), player.x, player.z) > 8) return;
        // The existing quest protocol is party scoped; never start another party's event through this bridge.
        if (!MultiplayerPartyManager.AreSameParty(conn.identity.netId, ship.Source))
        { conn.Send(new WaShipPrompt { text = "Join the ship owner's party to start this encounter." }); return; }
        var owner = Connection(ship.Source); var ownerPlayer = Player(owner);
        if (ownerPlayer == null || Horizontal(ship.Record.worldX, ship.Record.worldZ, ownerPlayer.x, ownerPlayer.z) > 350)
        { conn.Send(new WaShipPrompt { text = "The ship's encounter owner is unavailable or too far away." }); return; }
        foreach (var route in interactionRoutes.Values)
            if (route.expires > Time.unscaledTime && (route.ship == m.ship || route.owner == ship.Source || route.requester == conn.identity.netId))
            { conn.Send(new WaShipPrompt { text = "A ship conversation is already in progress." }); return; }
        Debug.Log(Tag + "Captain route ship=" + m.ship + " owner=" + ship.Source + " requester=" + conn.identity.netId);
        var r = new InteractionRoute { token = ++nextInteraction, ship = m.ship, part = m.part,
            requester = conn.identity.netId, owner = ship.Source, expires = Time.unscaledTime + 60f };
        interactionRoutes[r.token] = r;
        owner.Send(new WaShipInteract { ship = r.ship, part = r.part, token = r.token });
    }
    void RunInteraction(WaShipInteract request)
    {
        if (!Connected || !Outside) return;
        if (ownerInteraction.token != 0 || !(DaggerfallUI.UIManager.TopWindow is DaggerfallHUD))
        { NetworkClient.Send(new WaShipPrompt { token = request.token, text = "The encounter owner is busy. Try again shortly." }); return; }
        NativeShip source = null;
        foreach (var n in native.Values) if (n.Id == request.ship && !n.Suppressed && n.Object != null) { source = n; break; }
        LocalShip s;
        if (source == null || !local.TryGetValue(request.ship, out s) || !IsLocalSource(s.Record) || s.Motion.removed) return;
        Transform target = null;
        foreach (var t in Candidates(source)) if (t.gameObject.GetInstanceID() == request.part && t.gameObject.activeInHierarchy) { target = t; break; }
        if (target == null) { NetworkClient.Send(new WaShipPrompt { token = request.token, text = "That NPC is no longer available." }); return; }
        ownerInteraction = request; ownerInteractionUntil = Time.unscaledTime + 60f;
        ownerPreviousWindow = DaggerfallUI.UIManager.TopWindow; ownerPrompt = null; ownerPromptSent = false;
        ownerStep = 0; ownerTransitionUntil = Time.unscaledTime + 2f;
        try
        {
            RaycastHit hit;
            if (!HitOriginal(target, out hit)) throw new InvalidOperationException("original NPC has no usable collider");
            var action = RegisteredActivation(target);
            if (action != null) { action(hit); return; }
            for (Transform t = target; t != null; t = t.parent)
            {
                foreach (var c in t.GetComponents<Component>())
                    if (IsWa(c) && c is IPlayerActivable) { ((IPlayerActivable)c).Activate(hit); return; }
                if (t == source.Object.transform) break;
            }
            LogUnknown(source.Object.transform);
            throw new InvalidOperationException("no registered WA callback or IPlayerActivable on NPC/ship");
        }
        catch (Exception ex)
        {
            Debug.LogWarning(Tag + "Interaction not run: " + ex.Message);
            NetworkClient.Send(new WaShipPrompt { token = request.token, text = "This WA interaction is not supported yet. See Player.log." });
            ownerInteraction = new WaShipInteract();
        }
    }
    static bool HitOriginal(Transform target, out RaycastHit hit)
    {
        hit = new RaycastHit();
        foreach (var c in target.GetComponentsInChildren<Collider>())
        {
            if (!c.enabled) continue;
            var center = c.bounds.center;
            foreach (var direction in new[] { Vector3.forward, Vector3.right, Vector3.up })
            {
                var ray = new Ray(center - direction * 2f, direction);
                if (c.Raycast(ray, out hit, 4f)) return true;
            }
        }
        return false;
    }
    void RoutePrompt(NetworkConnection conn, WaShipPrompt prompt)
    {
        InteractionRoute route;
        if (Player(conn) == null || !interactionRoutes.TryGetValue(prompt.token, out route) || route.owner != conn.identity.netId ||
            route.expires <= Time.unscaledTime || prompt.text == null || prompt.text.Length > 8192 ||
            (prompt.buttons != null && prompt.buttons.Length > 8)) return;
        if (prompt.buttons != null)
            foreach (int button in prompt.buttons) if (!Enum.IsDefined(typeof(DaggerfallMessageBox.MessageBoxButtons), button)) return;
        if (prompt.advance)
        {
            if (prompt.step != route.step) return;
            route.prompted = false; route.buttons = null;
            var requester = Connection(route.requester); if (requester != null) requester.Send(prompt);
            return;
        }
        if (prompt.buttons != null && prompt.buttons.Length > 0)
        {
            if (route.prompted || prompt.step != route.step + 1) return;
            route.step = prompt.step;
        }
        route.buttons = prompt.buttons; route.prompted = true;
        route.expires = Time.unscaledTime + 60f;
        var target = Connection(route.requester); if (target != null) target.Send(prompt);
        if (prompt.buttons == null || prompt.buttons.Length == 0) interactionRoutes.Remove(prompt.token);
    }
    void ShowRemotePrompt(WaShipPrompt prompt)
    {
        if (remotePrompt != null && prompt.token == remoteToken)
        { remotePrompt.CloseWindow(); remotePrompt = null; }
        if (string.IsNullOrEmpty(prompt.text)) return;
        var box = new DaggerfallMessageBox(DaggerfallUI.UIManager);
        box.SetText(prompt.text);
        if (prompt.buttons == null || prompt.buttons.Length == 0)
        { box.ClickAnywhereToClose = true; box.Show(); return; }
        remoteToken = prompt.token; remotePrompt = box;
        foreach (int button in prompt.buttons) box.AddButton((DaggerfallMessageBox.MessageBoxButtons)button);
        box.OnButtonClick += (sender, button) =>
        {
            if (remotePrompt != sender) return;
            remotePrompt = null; remoteToken = 0;
            sender.CloseWindow(); NetworkClient.Send(new WaShipAnswer { token = prompt.token, step = prompt.step, button = (int)button });
        };
        box.Show();
    }
    void RouteAnswer(NetworkConnection conn, WaShipAnswer answer)
    {
        InteractionRoute route;
        if (Player(conn) == null || !interactionRoutes.TryGetValue(answer.token, out route) || route.requester != conn.identity.netId ||
            route.expires <= Time.unscaledTime || answer.step != route.step || !route.prompted || route.buttons == null || Array.IndexOf(route.buttons, answer.button) < 0) return;
        route.prompted = false; route.buttons = null; // one choice per step; keep route for subsequent prompts
        route.expires = Time.unscaledTime + 60f;
        var owner = Connection(route.owner); if (owner != null) owner.Send(answer);
    }
    static bool IsWaPrompt(DaggerfallMessageBox box)
    {
        // QuestNetSync already owns quest message boxes. Only bridge a direct WA
        // callback's initial prompt, or the requester could receive two choice UIs.
        var field = typeof(DaggerfallMessageBox).GetField("OnButtonClick", ReflectInstance);
        var handlers = field == null ? null : field.GetValue(box) as Delegate;
        if (handlers == null) return false;
        foreach (var handler in handlers.GetInvocationList())
        {
            var type = handler.Method.DeclaringType;
            if (type != null && type.Namespace != null && type.Namespace.StartsWith("WarmAshes", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    static List<Button> PromptButtons(DaggerfallMessageBox box)
    {
        var f = typeof(DaggerfallMessageBox).GetField("buttons", ReflectInstance);
        return f == null ? null : f.GetValue(box) as List<Button>;
    }
    void AnswerOriginalPrompt(WaShipAnswer answer)
    {
        if (ownerInteraction.token != answer.token || ownerStep != answer.step || ownerPrompt == null || !ReferenceEquals(DaggerfallUI.UIManager.TopWindow, ownerPrompt)) return;
        var buttons = PromptButtons(ownerPrompt); if (buttons == null) return;
        foreach (var button in buttons)
            if ((int)(DaggerfallMessageBox.MessageBoxButtons)button.Tag == answer.button)
            {
                ownerPreviousWindow = ownerPrompt;
                ownerPrompt = null; ownerPromptSent = false;
                ownerTransitionUntil = Time.unscaledTime + 2f;
                ownerInteractionUntil = Time.unscaledTime + 60f;
                button.TriggerMouseClick(); // DFU's actual button event invokes WA's original handler.
                return;
            }
    }
    void TickInteractions()
    {
        foreach (var pair in new List<KeyValuePair<uint, InteractionRoute>>(interactionRoutes))
            if (pair.Value.expires < Time.unscaledTime)
            {
                var conn = Connection(pair.Value.requester);
                if (conn != null) conn.Send(new WaShipPrompt { token = pair.Key, text = "The ship interaction timed out. Try again." });
                interactionRoutes.Remove(pair.Key);
            }
        if (ownerInteraction.token == 0 || !Connected) return;
        var window = DaggerfallUI.UIManager.TopWindow;
        if (ownerInteractionUntil < Time.unscaledTime || (ownerPromptSent && !ReferenceEquals(window, ownerPrompt)))
        {
            NetworkClient.Send(new WaShipPrompt { token = ownerInteraction.token, text = "The original ship conversation has closed." });
            ownerInteraction = new WaShipInteract(); ownerPrompt = null; return;
        }
        if (!ownerPromptSent && window is DaggerfallHUD && Time.unscaledTime >= ownerTransitionUntil)
        {
            NetworkClient.Send(new WaShipPrompt { token = ownerInteraction.token, text = "" });
            ownerInteraction = new WaShipInteract(); ownerPrompt = null; return;
        }
        if (ownerPromptSent || ReferenceEquals(window, ownerPreviousWindow)) return;
        var box = window as DaggerfallMessageBox;
        if (box == null || !IsWaPrompt(box)) return;
        var buttons = PromptButtons(box);
        var labelField = typeof(DaggerfallMessageBox).GetField("label", ReflectInstance);
        var label = labelField == null ? null : labelField.GetValue(box) as MultiFormatTextLabel;
        if (buttons == null || buttons.Count == 0 || buttons.Count > 8 || label == null || label.TextLabels.Count == 0) return;
        var text = new StringBuilder(); foreach (var line in label.TextLabels) text.AppendLine(line.Text);
        if (text.Length > 8192) return;
        var values = new List<int>(); foreach (var button in buttons) values.Add((int)(DaggerfallMessageBox.MessageBoxButtons)button.Tag);
        ownerPrompt = box; ownerPromptSent = true;
        uint token = ownerInteraction.token;
        uint step = ++ownerStep;
        ownerInteractionUntil = Time.unscaledTime + 60f;
        box.OnButtonClick += (sender, button) =>
        {
            if (ownerInteraction.token != token || ownerStep != step || ownerPrompt != sender) return;
            ownerPreviousWindow = sender; ownerPrompt = null; ownerPromptSent = false;
            ownerTransitionUntil = Time.unscaledTime + 2f;
            NetworkClient.Send(new WaShipPrompt { token = token, step = step, advance = true, text = "" });
        };
        NetworkClient.Send(new WaShipPrompt { token = token, step = step, text = text.ToString(), buttons = values.ToArray() });
    }
    void ClearInteractions()
    {
        questShipLinks.Clear();
        interactionRoutes.Clear(); ownerInteraction = new WaShipInteract(); ownerPrompt = null;
        if (remotePrompt != null) remotePrompt.CloseWindow(); remotePrompt = null; remoteToken = 0;
    }

}

// Local markers, not network prefabs or saved data.
public sealed class WaShipReplica : MonoBehaviour { }

/// <summary>Keep native sibling loot alive for stock initialization, but invisible
/// and non-interactive until both its hull and shared inventory are ready.</summary>
public sealed class WaShipLootGate : MonoBehaviour
{
    public uint ShipId;
    static readonly HashSet<WaShipLootGate> all = new HashSet<WaShipLootGate>();
    DaggerfallLoot loot;
    float nextWarning;
    readonly List<Renderer> hidden = new List<Renderer>();
    readonly List<Collider> blocked = new List<Collider>();
    void Awake()
    {
        loot = GetComponent<DaggerfallLoot>();
        nextWarning = Time.unscaledTime + 15f;
        all.Add(this);
    }
    void LateUpdate()
    {
        if (!NetworkClient.active || !WorldContainerPolicy.Enabled) { Restore(); return; }
        if (WarmAshesShipMultiplayerCompatibility.HullReady(ShipId) && WorldContainerSync.IsBound(loot))
        { Restore(); return; }
        if (Time.unscaledTime >= nextWarning)
        {
            nextWarning = Time.unscaledTime + 60f;
            Debug.LogWarning("[WAShipMP v3.12] Treasure waiting: " + name + " shipId=" + ShipId +
                " hullReady=" + WarmAshesShipMultiplayerCompatibility.HullReady(ShipId) +
                " inventoryBound=" + WorldContainerSync.IsBound(loot));
        }
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            if (r.enabled) { if (!hidden.Contains(r)) hidden.Add(r); r.enabled = false; }
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            if (c.enabled) { if (!blocked.Contains(c)) blocked.Add(c); c.enabled = false; }
    }
    void Restore()
    {
        foreach (var r in hidden) if (r != null) r.enabled = true;
        foreach (var c in blocked) if (c != null) c.enabled = true;
        hidden.Clear(); blocked.Clear();
    }
    public static void ReleaseAll()
    {
        foreach (var gate in new List<WaShipLootGate>(all))
            if (gate != null) { gate.Restore(); Destroy(gate); }
        all.Clear();
    }
    void OnDestroy() { Restore(); all.Remove(this); }
}

public struct WaShipAreaClaim : NetworkMessage { }
public struct WaShipRejected : NetworkMessage { public int token; }
public struct WaShipMotion : NetworkMessage
{
    public uint id, revision;
    public Vector3 offset, scale;
    public Quaternion rotation;
    public bool removed;
}
public struct WaShipPart
{
    public int key, archive, record;
    public uint model;
    public bool billboard, faceY, interactive;
    public string talkType, talkData, sourceName;
    public Vector3 position, scale;
    public Quaternion rotation;
}
public struct WaShipDetails : NetworkMessage
{
    public uint id, revision;
    public WaShipPart[] parts;
}

[Serializable] public sealed class WaTalkField { public string name, value; }
[Serializable] public sealed class WaTalkFields { public WaTalkField[] fields; }
public struct WaShipInteract : NetworkMessage { public uint ship, token; public int part; }
public struct WaShipPrompt : NetworkMessage { public uint token, step; public bool advance; public string text; public int[] buttons; }
public struct WaShipAnswer : NetworkMessage { public uint token, step; public int button; }
public sealed class WaShipNpcActivation : MonoBehaviour, IPlayerActivable
{
    public uint Ship;
    public int Part;
    public void Activate(RaycastHit hit) { WarmAshesShipMultiplayerCompatibility.ActivateReplica(Ship, Part, hit.distance); }
}

// Local activation adapter only; never asks the encounter owner to move a player.
public sealed class WaShipBoardingActivation : MonoBehaviour, IPlayerActivable
{
    public uint Ship;
    public IPlayerActivable Target;
    float nextClick;
    public void Activate(RaycastHit hit)
    {
        if (hit.distance > 100f || Time.unscaledTime < nextClick) return;
        nextClick = Time.unscaledTime + .5f;
        Debug.Log("[WAShipMP v3.12] Boarding click ship=" + Ship + " hitObject=" + name + " distance=" + hit.distance + " target=" + (Target == null ? "missing" : Target.GetType().FullName));
        var component = Target as Component;
        if (component == null) return;
        var before = DaggerfallUI.UIManager.TopWindow;
        try
        {
            Target.Activate(hit);
            Debug.Log("[WAShipMP v3.12] Boarding returned ship=" + Ship + " windowChanged=" + !ReferenceEquals(before, DaggerfallUI.UIManager.TopWindow));
        }
        catch (Exception ex) { Debug.LogWarning("[WAShipMP v3.12] Boarding failed ship=" + Ship + " " + ex); }
    }
}
