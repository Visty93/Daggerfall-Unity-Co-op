using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;

/// <summary>
/// Compatibility helper for Iliac Puddle No More / Deep Waters and Daggerfall Unity Co-op.
///
/// v3 design:
/// - Keep Deep Waters' original outdoor swimming completely active.
/// - Do NOT suppress Deep Waters swimming.
/// - Do NOT replace movement, buoyancy, breath, fog, shore handling, or collider gating.
/// - Do NOT move PlayerAdvanced.
/// - During Deep Waters' temporary forged outdoor-dungeon frame, prevent this fork's
///   multiplayer dungeon emergency-exit safety from treating that temporary state as a
///   real non-networked dungeon.
/// - While Deep Waters reports outdoor water contact/swimming, ignore collision between
///   the real local PlayerAdvanced CharacterController and the local PlayerMultiplayer
///   proxy CharacterController so the proxy cannot push/fight the real player.
///
/// This helper has no compile-time dependency on Deep Waters. It only resolves its public
/// DeepWaters.DeepWaterPlayer type and public IsInWater / IsSwimming properties by reflection.
///
/// v7: v6 spawning plus owner-side outdoor water-height support for converted aquatic enemies.
/// Requires the existing WOD-era world-coordinate and retention APIs in the MP fork.
/// </summary>
public sealed class IliacPuddleMultiplayerCompatibility : MonoBehaviour
{
    private const string DeepWaterPlayerTypeName = "DeepWaters.DeepWaterPlayer";

    private Type deepWaterPlayerType;
    private PropertyInfo deepWaterIsInWaterProperty;
    private PropertyInfo deepWaterIsSwimmingProperty;

    private bool deepWaterApiReady;
    private float nextBindAttempt;
    private bool loggedReady;
    private bool loggedMissing;

    private IliacPuddleMpPreGuard preGuard;
    private IliacPuddleMpPostGuard postGuard;

    // PlayerAdvanced <-> local PlayerMultiplayer collision-ignore ownership.
    private CharacterController realController;
    private CharacterController proxyController;
    private bool originalIgnoreState;
    private bool ownsIgnoreState;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        IliacPuddleMultiplayerCompatibility existing =
            UnityEngine.Object.FindObjectOfType<IliacPuddleMultiplayerCompatibility>();

        if (existing != null)
            return;

        GameObject go = new GameObject("Iliac Puddle MP Compatibility");
        UnityEngine.Object.DontDestroyOnLoad(go);

        IliacPuddleMultiplayerCompatibility owner =
            go.AddComponent<IliacPuddleMultiplayerCompatibility>();

        owner.preGuard = go.AddComponent<IliacPuddleMpPreGuard>();
        owner.postGuard = go.AddComponent<IliacPuddleMpPostGuard>();

        owner.preGuard.Owner = owner;
        owner.postGuard.Owner = owner;
        go.AddComponent<IliacPuddleMpEnemies>();
    }

    private void OnEnable()
    {
        nextBindAttempt = 0f;
    }

    private void OnDisable()
    {
        RestoreProxyCollisionIgnore();
    }

    private void OnDestroy()
    {
        RestoreProxyCollisionIgnore();
    }

    internal bool IsMultiplayerActive
    {
        get { return NetworkServer.active || NetworkClient.active; }
    }

    internal bool IsDeepWatersAvailable()
    {
        TryBindDeepWatersPublicApi();
        return deepWaterApiReady;
    }

    private void TryBindDeepWatersPublicApi()
    {
        if (deepWaterApiReady)
            return;

        float now = Time.realtimeSinceStartup;
        if (now < nextBindAttempt)
            return;

        nextBindAttempt = now + 1f;

        deepWaterPlayerType = FindLoadedType(DeepWaterPlayerTypeName);
        if (deepWaterPlayerType == null)
        {
            if (!loggedMissing && now > 5f)
            {
                loggedMissing = true;
                Debug.Log("[IliacMPCompat] Deep Waters public API not loaded. Helper remains idle.");
            }
            return;
        }

        deepWaterIsInWaterProperty = deepWaterPlayerType.GetProperty(
            "IsInWater",
            BindingFlags.Public | BindingFlags.Static);

        deepWaterIsSwimmingProperty = deepWaterPlayerType.GetProperty(
            "IsSwimming",
            BindingFlags.Public | BindingFlags.Static);

        if (deepWaterIsInWaterProperty == null ||
            deepWaterIsSwimmingProperty == null)
        {
            Debug.LogWarning(
                "[IliacMPCompat] Deep Waters was found, but public IsInWater/IsSwimming " +
                "properties are unavailable. Compatibility helper remains idle.");
            return;
        }

        deepWaterApiReady = true;

        if (!loggedReady)
        {
            loggedReady = true;
            Debug.Log(
                "[IliacMPCompat] v3 active. Deep Waters keeps full control of swimming. " +
                "Only MP dungeon emergency-exit shielding and local proxy collision-ignore are enabled.");
        }
    }

    internal bool IsDeepWatersWaterActive()
    {
        if (!IsDeepWatersAvailable())
            return false;

        try
        {
            bool isInWater = (bool)deepWaterIsInWaterProperty.GetValue(null, null);
            bool isSwimming = (bool)deepWaterIsSwimmingProperty.GetValue(null, null);
            return isInWater || isSwimming;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deep Waters' pre-driver temporarily creates this otherwise impossible public state:
    /// IsPlayerInsideDungeon == true while IsPlayerInside == false.
    ///
    /// A real DFU dungeon always has IsPlayerInside == true.
    /// This gives us a public, source-light way to recognize Deep Waters' temporary forge
    /// without referencing its private OutdoorSwimDriver class.
    /// </summary>
    internal bool IsDeepWatersForgedOutdoorDungeon(PlayerEnterExit pex)
    {
        if (pex == null || !IsMultiplayerActive || !IsDeepWatersAvailable())
            return false;

        return pex.IsPlayerInsideDungeon && !pex.IsPlayerInside;
    }

    internal void UpdateLocalProxyCollisionIgnore(bool forceWaterActive)
    {
        if (!IsMultiplayerActive || !IsDeepWatersAvailable())
        {
            RestoreProxyCollisionIgnore();
            return;
        }

        bool waterActive = forceWaterActive || IsDeepWatersWaterActive();

        if (!waterActive)
        {
            RestoreProxyCollisionIgnore();
            return;
        }

        GameManager gameManager = GameManager.HasInstance ? GameManager.Instance : null;
        if (gameManager == null)
            return;

        CharacterController currentReal = gameManager.PlayerController;
        PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
        CharacterController currentProxy =
            localPlayer != null ? localPlayer.GetComponent<CharacterController>() : null;

        if (currentReal == null ||
            currentProxy == null ||
            currentReal == currentProxy)
        {
            return;
        }

        if (ownsIgnoreState &&
            realController == currentReal &&
            proxyController == currentProxy)
        {
            return;
        }

        RestoreProxyCollisionIgnore();

        realController = currentReal;
        proxyController = currentProxy;

        try
        {
            originalIgnoreState =
                Physics.GetIgnoreCollision(realController, proxyController);

            if (!originalIgnoreState)
                Physics.IgnoreCollision(realController, proxyController, true);

            ownsIgnoreState = true;
        }
        catch
        {
            realController = null;
            proxyController = null;
            ownsIgnoreState = false;
        }
    }

    internal void RestoreProxyCollisionIgnore()
    {
        if (!ownsIgnoreState)
            return;

        try
        {
            if (realController != null && proxyController != null)
            {
                Physics.IgnoreCollision(
                    realController,
                    proxyController,
                    originalIgnoreState);
            }
        }
        catch
        {
        }

        realController = null;
        proxyController = null;
        ownsIgnoreState = false;
    }

    private static Type FindLoadedType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        for (int i = 0; i < assemblies.Length; i++)
        {
            Assembly assembly = assemblies[i];
            if (assembly == null)
                continue;

            try
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            catch
            {
                // Mod assemblies can be loading/unloading. Retry on the next bind attempt.
            }
        }

        return null;
    }
}

/// <summary>
/// Runs AFTER Deep Waters OutdoorSwimDriver (-32000) but BEFORE ordinary DFU Update.
///
/// Deep Waters has already forged PlayerEnterExit into a temporary dungeon by this point.
/// We only block this fork's multiplayer emergency-dungeon exit during that temporary state.
/// </summary>
[DefaultExecutionOrder(-31000)]
public sealed class IliacPuddleMpPreGuard : MonoBehaviour
{
    internal IliacPuddleMultiplayerCompatibility Owner;

    private FieldInfo emergencyDungeonExitInProgressField;
    private FieldInfo lastNetworkActiveStateField;

    private PlayerEnterExit guardedPlayerEnterExit;
    private bool savedEmergencyDungeonExitInProgress;
    private bool ownsEmergencyExitGuard;

    private bool loggedReflectionReady;
    private bool loggedReflectionFailure;
    private bool loggedWaterGuardEntry;
    private bool previousForgedState;

    private void Update()
    {
        if (Owner == null ||
            !Owner.IsMultiplayerActive ||
            !Owner.IsDeepWatersAvailable())
        {
            RestoreEmergencyExitGuard();
            Owner?.RestoreProxyCollisionIgnore();
            previousForgedState = false;
            return;
        }

        GameManager gameManager = GameManager.HasInstance ? GameManager.Instance : null;
        PlayerEnterExit pex =
            gameManager != null ? gameManager.PlayerEnterExit : null;

        if (pex == null)
        {
            RestoreEmergencyExitGuard();
            Owner.RestoreProxyCollisionIgnore();
            previousForgedState = false;
            return;
        }

        EnsurePlayerEnterExitReflection();

        bool forgedOutdoorDungeon =
            Owner.IsDeepWatersForgedOutdoorDungeon(pex);

        if (forgedOutdoorDungeon)
        {
            ApplyEmergencyExitGuard(pex);
            Owner.UpdateLocalProxyCollisionIgnore(true);

            if (!previousForgedState && !loggedWaterGuardEntry)
            {
                loggedWaterGuardEntry = true;
                Debug.Log(
                    "[IliacMPCompat] Deep Waters forged outdoor-water frame detected. " +
                    "MP dungeon emergency-exit is shielded; native Deep Waters swimming remains untouched.");
            }
        }
        else
        {
            RestoreEmergencyExitGuard();
            Owner.UpdateLocalProxyCollisionIgnore(false);
        }

        previousForgedState = forgedOutdoorDungeon;
    }

    private void EnsurePlayerEnterExitReflection()
    {
        if (emergencyDungeonExitInProgressField != null &&
            lastNetworkActiveStateField != null)
        {
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.NonPublic;

        emergencyDungeonExitInProgressField =
            typeof(PlayerEnterExit).GetField(
                "emergencyDungeonExitInProgress",
                flags);

        lastNetworkActiveStateField =
            typeof(PlayerEnterExit).GetField(
                "lastNetworkActiveState",
                flags);

        if (emergencyDungeonExitInProgressField != null &&
            lastNetworkActiveStateField != null)
        {
            if (!loggedReflectionReady)
            {
                loggedReflectionReady = true;
                Debug.Log(
                    "[IliacMPCompat] PlayerEnterExit MP safety guard bound.");
            }
        }
        else if (!loggedReflectionFailure)
        {
            loggedReflectionFailure = true;
            Debug.LogWarning(
                "[IliacMPCompat] Current PlayerEnterExit no longer exposes the two expected " +
                "MP safety fields. Water compatibility guard cannot prevent the emergency dungeon exit.");
        }
    }

    private void ApplyEmergencyExitGuard(PlayerEnterExit pex)
    {
        if (pex == null ||
            emergencyDungeonExitInProgressField == null ||
            lastNetworkActiveStateField == null)
        {
            return;
        }

        // Keep PlayerEnterExit's cached network state synchronized with reality. This prevents
        // the temporary Deep Waters dummy dungeon from entering the "network just started
        // while inside a local dungeon" conversion path.
        try
        {
            lastNetworkActiveStateField.SetValue(
                pex,
                NetworkServer.active || NetworkClient.active);
        }
        catch
        {
        }

        if (ownsEmergencyExitGuard && guardedPlayerEnterExit == pex)
            return;

        RestoreEmergencyExitGuard();

        try
        {
            guardedPlayerEnterExit = pex;

            object current =
                emergencyDungeonExitInProgressField.GetValue(pex);

            savedEmergencyDungeonExitInProgress =
                current is bool && (bool)current;

            // EmergencyExitDungeonForNetworkChange() starts with:
            // if (emergencyDungeonExitInProgress || !isPlayerInsideDungeon) return false;
            //
            // Deep Waters still gets its forged dungeon state for native swimming, but the
            // multiplayer emergency-exit routine becomes a no-op for this one Update.
            emergencyDungeonExitInProgressField.SetValue(pex, true);
            ownsEmergencyExitGuard = true;
        }
        catch
        {
            guardedPlayerEnterExit = null;
            ownsEmergencyExitGuard = false;
        }
    }

    internal void RestoreEmergencyExitGuard()
    {
        if (!ownsEmergencyExitGuard)
            return;

        try
        {
            if (guardedPlayerEnterExit != null &&
                emergencyDungeonExitInProgressField != null)
            {
                emergencyDungeonExitInProgressField.SetValue(
                    guardedPlayerEnterExit,
                    savedEmergencyDungeonExitInProgress);
            }
        }
        catch
        {
        }

        guardedPlayerEnterExit = null;
        ownsEmergencyExitGuard = false;
    }
}

/// <summary>
/// Runs AFTER PlayerEnterExit/normal game Update, but BEFORE Deep Waters' own +32000
/// post-driver. This restores the multiplayer emergency-exit guard while leaving all
/// Deep Waters forged water fields for Deep Waters itself to restore one step later.
/// </summary>
[DefaultExecutionOrder(31000)]
public sealed class IliacPuddleMpPostGuard : MonoBehaviour
{
    internal IliacPuddleMultiplayerCompatibility Owner;

    private IliacPuddleMpPreGuard cachedPreGuard;

    private void Update()
    {
        if (cachedPreGuard == null)
            cachedPreGuard = GetComponent<IliacPuddleMpPreGuard>();

        if (cachedPreGuard != null)
            cachedPreGuard.RestoreEmergencyExitGuard();

        if (Owner != null)
            Owner.UpdateLocalProxyCollisionIgnore(false);
    }
}

// Original multiplayer adapter. References Deep Waters' tracker field names only;
// no spawning, swimming, or asset implementation is copied from the mod.
public struct IliacEnemyRequest : NetworkMessage
{
    public uint marker;
    public int worldX, worldZ, type, gender, team;
    public float relativeY, yaw;
    public Vector3 scale;
    public bool hostile, allied;
}
public struct IliacEnemyReply : NetworkMessage
{
    public uint marker, netId;
    public byte status; // 1 = live, 2 = killed, 3 = retry
}
public struct IliacEnemyInfo : NetworkMessage { public uint netId; }
public struct IliacEnemyRosterRequest : NetworkMessage { }

public sealed class IliacPuddleMpEnemies : MonoBehaviour
{
    const string Prefix = "[IliacMPEnemies v6] ";
    const float Activation = 100f, RequestLimit = 125f, Retention = 3000f, Units = 40f;
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    IliacPuddleMultiplayerCompatibility owner;
    FieldInfo groupsField, trackerField, objectsField;
    float nextScan, nextBind, nextRosterRequest;
    bool wasActive, rosterRequested, warnedLayout, loggedLayout;
    uint sequence;
    readonly Dictionary<GameObject, Marker> markers = new Dictionary<GameObject, Marker>();
    readonly Dictionary<ulong, SpawnRecord> records = new Dictionary<ulong, SpawnRecord>();
    readonly HashSet<uint> knownEnemies = new HashSet<uint>();
    readonly Dictionary<int, Budget> budgets = new Dictionary<int, Budget>();
    readonly List<GameObject> scratch = new List<GameObject>();
    readonly HashSet<GameObject> eventSources = new HashSet<GameObject>();
    static readonly FieldInfo MotorEntity = typeof(EnemyMotor).GetField("entity", Fields);
    static readonly FieldInfo SensesEntity = typeof(EnemySenses).GetField("enemyEntity", Fields);

    sealed class Marker
    {
        public GameObject Source;
        public uint Id, NetId;
        public float NextRequest;
        public bool WasActive, Dead, Corrected;
    }
    sealed class SpawnRecord
    {
        public GameObject Actor;
        public EnemyEntity Entity;
        public bool Dead;
    }
    sealed class Budget { public float Since; public int Count; }

    internal IEnumerable<uint> KnownEnemyIds { get { return knownEnemies; } }
    void Awake()
    {
        owner = GetComponent<IliacPuddleMultiplayerCompatibility>();
        var water = gameObject.AddComponent<IliacMpAquaticWater>();
        water.Owner = this;
    }
    void OnEnable()
    {
        GameObjectHelper.PreserveActorAtMultiplayerStart += PreserveAtConnect;
        GameManager.OnEnemySpawn += ObserveModSpawn;
        RegisterHandlers();
    }
    void RegisterHandlers()
    {
        NetworkServer.ReplaceHandler<IliacEnemyRequest>(ReceiveRequest);
        NetworkServer.ReplaceHandler<IliacEnemyRosterRequest>(ReceiveRosterRequest);
        NetworkClient.ReplaceHandler<IliacEnemyReply>(ReceiveReply);
        NetworkClient.ReplaceHandler<IliacEnemyInfo>(ReceiveInfo);
    }
    void OnDisable()
    {
        GameObjectHelper.PreserveActorAtMultiplayerStart -= PreserveAtConnect;
        GameManager.OnEnemySpawn -= ObserveModSpawn;
        RestoreSources();
    }

    // Treasure guards are not in pixelGroups in current Deep Waters. Identify
    // their synchronous creation by the declaring type on the spawn-event stack.
    // This also remembers SP-created guards for the later host/connect cleanup.
    void ObserveModSpawn(GameObject actor)
    {
        if (actor == null || owner == null || !owner.IsDeepWatersAvailable()) return;
        var identity = actor.GetComponent<NetworkIdentity>();
        if (identity == null || identity.netId != 0) return;
        var game = GameManager.HasInstance ? GameManager.Instance : null;
        if (game != null && game.PlayerEnterExit != null && game.PlayerEnterExit.IsPlayerInside) return;
        var frames = new System.Diagnostics.StackTrace(false).GetFrames();
        if (frames == null) return;
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null || method.DeclaringType == null ||
                method.DeclaringType.FullName != "DeepWaters.UnderwaterEnemySpawner") continue;
            eventSources.RemoveWhere(go => go == null);
            eventSources.Add(actor);
            break;
        }
    }

    bool BindTracker()
    {
        if (groupsField != null) return true;
        if (Time.unscaledTime < nextBind) return false;
        nextBind = Time.unscaledTime + 2f;
        if (owner == null || !owner.IsDeepWatersAvailable()) return false;
        Type spawner = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            spawner = a.GetType("DeepWaters.UnderwaterEnemySpawner", false);
            if (spawner != null) break;
        }
        FieldInfo groups = spawner == null ? null : spawner.GetField("pixelGroups", BindingFlags.Static | BindingFlags.NonPublic);
        Type groupType = spawner == null ? null : spawner.GetNestedType("PixelEnemyGroup", BindingFlags.NonPublic);
        FieldInfo tracker = groupType == null ? null : groupType.GetField("Enemies", Fields);
        FieldInfo objects = tracker == null ? null : tracker.FieldType.GetField("objects", Fields);
        if (groups == null || tracker == null || objects == null ||
            !typeof(System.Collections.IDictionary).IsAssignableFrom(groups.FieldType) ||
            !typeof(System.Collections.IList).IsAssignableFrom(objects.FieldType))
        {
            if (!warnedLayout) Debug.LogWarning(Prefix + "Enemy tracker layout unavailable. Only positively identified spawn events can convert; swimming remains active.");
            warnedLayout = true;
            return false;
        }
        groupsField = groups; trackerField = tracker; objectsField = objects;
        if (!loggedLayout) Debug.Log(Prefix + "Tracker bound. Activation=100m; horizontal retention=3000m; native underwater height preserved.");
        loggedLayout = true;
        return true;
    }

    // Read membership only. Never remove originals from the mod's tracker or alter its cap.
    void ReadTrackedSources()
    {
        scratch.Clear();
        eventSources.RemoveWhere(go => go == null);
        foreach (GameObject source in eventSources) scratch.Add(source);
        if (!BindTracker()) return;
        var groups = groupsField.GetValue(null) as System.Collections.IDictionary;
        if (groups == null) return;
        foreach (object group in groups.Values)
        {
            if (group == null) continue;
            object tracker = trackerField.GetValue(group);
            var list = tracker == null ? null : objectsField.GetValue(tracker) as System.Collections.IList;
            if (list == null) continue;
            foreach (object item in list)
            {
                var actor = item as GameObject;
                if (actor != null && actor.GetComponent<SetupDemoEnemy>() != null && !scratch.Contains(actor))
                    scratch.Add(actor);
            }
        }
    }

    bool PreserveAtConnect(GameObject actor)
    {
        if (actor == null || actor.GetComponent<SetupDemoEnemy>() == null) return false;
        try { ReadTrackedSources(); return scratch.Contains(actor); }
        catch (Exception e) { WarnLayout(e); return false; }
    }

    void WarnLayout(Exception e)
    {
        if (!warnedLayout) Debug.LogWarning(Prefix + "Tracker read failed: " + e.Message);
        warnedLayout = true;
    }

    // LateUpdate sees the normal exterior state after Deep Waters restores its
    // temporary swimming/dungeon fields. Swimming code itself is unchanged.
    void LateUpdate()
    {
        bool active = NetworkServer.active || NetworkClient.active;
        if (!active)
        {
            if (wasActive) RestoreSources();
            wasActive = false;
            return;
        }
        wasActive = true;
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.5f;
        if (NetworkServer.active)
        {
            NetworkServer.ReplaceHandler<IliacEnemyRequest>(ReceiveRequest);
            NetworkServer.ReplaceHandler<IliacEnemyRosterRequest>(ReceiveRosterRequest);
        }
        if (NetworkClient.active)
        {
            NetworkClient.ReplaceHandler<IliacEnemyReply>(ReceiveReply);
            NetworkClient.ReplaceHandler<IliacEnemyInfo>(ReceiveInfo);
        }
        if (owner == null || !owner.IsDeepWatersAvailable()) return;
        if (!rosterRequested && NetworkClient.ready && !NetworkServer.active && Time.unscaledTime >= nextRosterRequest)
        {
            NetworkClient.Send(new IliacEnemyRosterRequest());
            nextRosterRequest = Time.unscaledTime + 3f;
        }
        RefreshClientActors();
        try { ReadTrackedSources(); }
        catch (Exception e) { WarnLayout(e); return; }
        foreach (GameObject source in scratch)
        {
            if (markers.ContainsKey(source)) continue;
            var identity = source.GetComponent<NetworkIdentity>();
            var behaviour = source.GetComponent<DaggerfallEntityBehaviour>();
            if (identity == null || identity.netId != 0 || behaviour == null ||
                !(behaviour.Entity is EnemyEntity) || behaviour.Entity.CurrentHealth <= 0) continue;
            Marker marker = new Marker { Source = source, Id = ++sequence, WasActive = source.activeSelf };
            markers.Add(source, marker);
            source.SetActive(false);
            Debug.Log(Prefix + "Captured local underwater marker " + marker.Id + " '" + source.name + "'.");
        }
        var removed = new List<GameObject>();
        foreach (var entry in markers)
        {
            Marker marker = entry.Value;
            if (marker.Source == null) { removed.Add(entry.Key); continue; }
            // The original follows its terrain across origin shifts and remains
            // disposable by the mod. The separate network actor is never in that list.
            if (marker.Source.activeSelf) marker.Source.SetActive(false);
            if (!marker.Dead) TryRequest(marker);
        }
        foreach (GameObject source in removed) markers.Remove(source);
    }

    bool LocalExteriorReady(out GameManager game, out PlayerMultiplayer local)
    {
        game = GameManager.HasInstance ? GameManager.Instance : null;
        local = PlayerMultiplayer.GetLocalPlayer();
        return game != null && game.PlayerGPS != null && game.PlayerObject != null &&
            game.PlayerEnterExit != null && !game.PlayerEnterExit.IsPlayerInside &&
            local != null && local.netId != 0 && NetworkClient.ready;
    }

    void TryRequest(Marker marker)
    {
        GameManager game; PlayerMultiplayer local;
        if (!LocalExteriorReady(out game, out local)) return;
        Transform parent = marker.Source.transform.parent;
        if (parent == null || !parent.gameObject.activeInHierarchy) return;
        if (marker.NetId != 0 && NetworkClient.spawned.ContainsKey(marker.NetId)) return;
        if (Time.unscaledTime < marker.NextRequest) return;
        Vector3 offset = marker.Source.transform.position - game.PlayerObject.transform.position;
        if (offset.sqrMagnitude > Activation * Activation) return;
        var setup = marker.Source.GetComponent<SetupDemoEnemy>();
        var behaviour = marker.Source.GetComponent<DaggerfallEntityBehaviour>();
        var entity = behaviour == null ? null : behaviour.Entity as EnemyEntity;
        if (setup == null || entity == null || entity.CurrentHealth <= 0) return;
        var motor = marker.Source.GetComponent<EnemyMotor>();
        var request = new IliacEnemyRequest {
            marker = marker.Id,
            worldX = game.PlayerGPS.WorldX + Mathf.RoundToInt(offset.x * Units),
            worldZ = game.PlayerGPS.WorldZ + Mathf.RoundToInt(offset.z * Units),
            relativeY = offset.y, yaw = marker.Source.transform.eulerAngles.y,
            scale = marker.Source.transform.lossyScale,
            type = (int)setup.EnemyType, gender = (int)setup.EnemyGender,
            team = (int)entity.Team, allied = setup.AlliedToPlayer,
            hostile = motor != null ? motor.IsHostile : setup.EnemyReaction == MobileReactions.Hostile
        };
        marker.NextRequest = Time.unscaledTime + 5f;
        if (NetworkServer.active) ReceiveRequest(NetworkServer.localConnection, request);
        else NetworkClient.Send(request);
    }

    static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    static bool Exterior(PositionMultiplayer p)
    {
        return p != null && p.PartyCurrentLocationState != PositionMultiplayer.PartyLocationState.BuildingInterior &&
            p.PartyCurrentLocationState != PositionMultiplayer.PartyLocationState.DungeonInterior;
    }
    bool AllowRequest(NetworkConnection connection)
    {
        Budget budget;
        if (!budgets.TryGetValue(connection.connectionId, out budget))
            budgets[connection.connectionId] = budget = new Budget();
        if (Time.unscaledTime - budget.Since > 10f) { budget.Since = Time.unscaledTime; budget.Count = 0; }
        return ++budget.Count <= 40;
    }
    void ReceiveRosterRequest(NetworkConnection connection, IliacEnemyRosterRequest request)
    {
        if (!NetworkServer.active || connection == null || !connection.isReady || !AllowRequest(connection)) return;
        foreach (SpawnRecord r in records.Values)
        {
            if (r.Actor == null) continue;
            var ni = r.Actor.GetComponent<NetworkIdentity>();
            if (ni != null && ni.netId != 0) connection.Send(new IliacEnemyInfo { netId = ni.netId });
        }
        connection.Send(new IliacEnemyInfo { netId = 0 }); // roster completion
    }
    void ReceiveInfo(NetworkConnection connection, IliacEnemyInfo info)
    {
        if (info.netId != 0) knownEnemies.Add(info.netId);
        else rosterRequested = true;
    }
    void Reply(NetworkConnection c, IliacEnemyRequest r, byte status, uint id)
    {
        c.Send(new IliacEnemyReply { marker = r.marker, status = status, netId = id });
    }
    void ReceiveRequest(NetworkConnection connection, IliacEnemyRequest request)
    {
        if (!NetworkServer.active || connection == null || !connection.isReady || connection.identity == null ||
            owner == null || !owner.IsDeepWatersAvailable() || !AllowRequest(connection)) return;
        var player = connection.identity.GetComponent<PlayerMultiplayer>();
        var position = connection.identity.GetComponent<PositionMultiplayer>();
        if (player == null || !Exterior(position)) { Reply(connection, request, 3, 0); return; }
        double dx = ((double)request.worldX - position.x) / Units;
        double dz = ((double)request.worldZ - position.z) / Units;
        bool invalid = request.marker == 0 || !GameObjectHelper.EnemyDict.ContainsKey(request.type) ||
            !Enum.IsDefined(typeof(MobileGender), request.gender) || !Enum.IsDefined(typeof(MobileTeams), request.team) ||
            !Finite(request.relativeY) || !Finite(request.yaw) ||
            dx * dx + dz * dz + (double)request.relativeY * request.relativeY > RequestLimit * RequestLimit ||
            !Finite(request.scale.x) || !Finite(request.scale.y) || !Finite(request.scale.z) ||
            request.scale.x < 0.1f || request.scale.y < 0.1f || request.scale.z < 0.1f ||
            request.scale.x > 10f || request.scale.y > 10f || request.scale.z > 10f;
        if (invalid) { Reply(connection, request, 3, 0); return; }
        // Random encounters are per requester. Retries reuse an actor; separate
        // players may contribute separate encounters, as with the agreed WOD policy.
        ulong key = ((ulong)connection.identity.netId << 32) | request.marker;
        SpawnRecord record;
        if (records.TryGetValue(key, out record))
        {
            if (record.Entity != null && record.Entity.CurrentHealth <= 0) record.Dead = true;
            if (record.Dead) { Reply(connection, request, 2, 0); return; }
            if (record.Actor != null)
            {
                var ni = record.Actor.GetComponent<NetworkIdentity>();
                if (ni != null && ni.netId != 0) { Reply(connection, request, 1, ni.netId); return; }
            }
        }
        Spawn(connection, position, player, request, key, (float)dx, (float)dz);
    }

    void Spawn(NetworkConnection connection, PositionMultiplayer position, PlayerMultiplayer player,
        IliacEnemyRequest r, ulong key, float dx, float dz)
    {
        GameObject actor = null;
        try
        {
            actor = Instantiate(DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject);
            actor.name = "Iliac MP " + ((MobileTypes)r.type);
            actor.transform.SetParent(null, true);
            Vector3 pose = position.transform.position + new Vector3(dx, r.relativeY, dz);
            // Host can be elsewhere. Never raycast against the host's terrain.
            if (connection == NetworkServer.localConnection && GameManager.HasInstance)
            {
                var game = GameManager.Instance;
                pose = game.PlayerObject.transform.position + new Vector3(
                    (r.worldX - game.PlayerGPS.WorldX) / Units, r.relativeY,
                    (r.worldZ - game.PlayerGPS.WorldZ) / Units);
            }
            actor.transform.SetPositionAndRotation(pose, Quaternion.Euler(0, r.yaw, 0));
            actor.transform.localScale = r.scale;
            var setup = actor.GetComponent<SetupDemoEnemy>();
            var ni = actor.GetComponent<NetworkIdentity>();
            var world = actor.GetComponent<EnemyWorldPosition>();
            var authority = actor.GetComponent<DynamicEnemyAuthority>();
            if (setup == null || ni == null || ni.assetId == Guid.Empty || world == null || authority == null)
                throw new InvalidOperationException("Enemy prefab lacks required MP components.");
            setup.SpawnTeamOverride = r.team;
            setup.ApplyEnemySettingsWithScalingLevel((MobileTypes)r.type,
                r.hostile ? MobileReactions.Hostile : MobileReactions.Passive, (MobileGender)r.gender, 0,
                r.allied || (MobileTeams)r.team == MobileTeams.PlayerAlly, Mathf.Clamp(player.PlayerMPLevel, 1, 100));
            var entity = actor.GetComponent<DaggerfallEntityBehaviour>().Entity as EnemyEntity;
            if (entity == null) throw new InvalidOperationException("Enemy entity not ready.");
            entity.WorldContext = WorldContext.Exterior;
            setup.ApplySpawnTeamOverride();
            setup.isDungeonEnemy = false;
            setup.syncedInitialY = pose.y;
            setup.SyncedSpawnHealth = entity.CurrentHealth;
            var motor = actor.GetComponent<EnemyMotor>();
            if (motor != null) motor.IsHostile = r.hostile;
            setup.SyncedMotorIsHostile = setup.SpawnedMotorIsHostile =
                setup.CurrentMotorIsHostile = setup.LastAppliedMotorIsHostile = r.hostile;
            world.isInteriorSpawn = world.isDungeonSpawn = world.isCreateFoeWaveSpawn = false;
            world.requesterNetId = connection.identity.netId;
            world.SetDistantExteriorCoordinates(r.worldX, r.worldZ);
            authority.HorizontalRetentionDistanceDF = Retention * Units;
            GameManager.Instance.RaiseOnEnemySpawnEvent(actor);
            authority.PreserveInitialExteriorOwner(connection);
            NetworkServer.Spawn(actor, connection);
            if (ni.netId == 0) throw new InvalidOperationException("Network spawn did not assign netId.");
            world.ResumeRetainedActorNetworking();
            world.SetDistantExteriorCoordinates(r.worldX, r.worldZ);
            authority.ResumeRetainedActorNetworking();
            records[key] = new SpawnRecord { Actor = actor, Entity = entity };
            knownEnemies.Add(ni.netId);
            NetworkServer.SendToAll(new IliacEnemyInfo { netId = ni.netId });
            Reply(connection, r, 1, ni.netId);
            Debug.Log(Prefix + "Spawned marker=" + r.marker + " requester=" + connection.identity.netId +
                " netId=" + ni.netId + " DF=" + r.worldX + "/" + r.worldZ + " pose=" + pose);
        }
        catch (Exception e)
        {
            if (actor != null)
            {
                var ni = actor.GetComponent<NetworkIdentity>();
                if (ni != null && ni.netId != 0) NetworkServer.Destroy(actor); else Destroy(actor);
            }
            Debug.LogError(Prefix + "Spawn failed: " + e);
            Reply(connection, r, 3, 0);
        }
    }

    void ReceiveReply(NetworkConnection connection, IliacEnemyReply reply)
    {
        foreach (Marker marker in markers.Values)
        {
            if (marker.Id != reply.marker) continue;
            if (reply.status == 2) { marker.Dead = true; return; }
            if (reply.status != 1 || reply.netId == 0) return;
            if (marker.NetId != reply.netId) marker.Corrected = false;
            marker.NetId = reply.netId;
            knownEnemies.Add(reply.netId);
            if (!marker.Corrected)
            {
                marker.Corrected = true;
                StartCoroutine(PlaceOwnedActor(marker, reply.netId));
            }
            return;
        }
    }

    System.Collections.IEnumerator PlaceOwnedActor(Marker marker, uint id)
    {
        float until = Time.unscaledTime + 5f;
        while (Time.unscaledTime < until)
        {
            yield return null;
            if (!NetworkClient.active || marker.Source == null) yield break;
            GameManager game; PlayerMultiplayer local;
            if (!LocalExteriorReady(out game, out local)) continue;
            NetworkIdentity ni;
            if (!NetworkClient.spawned.TryGetValue(id, out ni) || ni == null || !ni.hasAuthority) continue;
            var setup = ni.GetComponent<SetupDemoEnemy>();
            if (setup == null || (!NetworkServer.active && !setup.HasReceivedInitialServerSettings())) continue;
            Transform parent = marker.Source.transform.parent;
            if (parent == null || !parent.gameObject.activeInHierarchy) yield break;
            Vector3 desired = marker.Source.transform.position;
            if ((desired - game.PlayerObject.transform.position).sqrMagnitude > RequestLimit * RequestLimit) yield break;
            var cc = ni.GetComponent<CharacterController>();
            bool enabled = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            ni.transform.position = desired;
            if (cc != null) cc.enabled = enabled;
            var world = ni.GetComponent<EnemyWorldPosition>();
            if (world != null) world.NoteExternalSeamFrameCorrection();
            var motor = ni.GetComponent<EnemyMotor>();
            if (motor != null) motor.LastGroundedY = desired.y;
            Debug.Log(Prefix + "Owner placed netId=" + id + " at native underwater pose=" + desired);
            yield break;
        }
        Debug.LogWarning(Prefix + "Owner placement timed out netId=" + id + "; retaining network spawn pose.");
    }

    void RefreshClientActors()
    {
        if (!NetworkClient.active || NetworkServer.active || MotorEntity == null || SensesEntity == null) return;
        foreach (uint id in knownEnemies)
        {
            NetworkIdentity ni;
            if (!NetworkClient.spawned.TryGetValue(id, out ni) || ni == null) continue;
            var setup = ni.GetComponent<SetupDemoEnemy>();
            if (setup == null || !setup.HasReceivedInitialServerSettings()) continue;
            var behaviour = ni.GetComponent<DaggerfallEntityBehaviour>();
            var live = behaviour == null ? null : behaviour.Entity as EnemyEntity;
            if (live == null) continue;
            var motor = ni.GetComponent<EnemyMotor>();
            var senses = ni.GetComponent<EnemySenses>();
            RefreshEntity(motor, MotorEntity, live);
            RefreshEntity(senses, SensesEntity, live);
        }
    }
    static void RefreshEntity(Component component, FieldInfo field, EnemyEntity live)
    {
        if (component == null || field.FieldType != typeof(EnemyEntity)) return;
        var cached = field.GetValue(component) as EnemyEntity;
        if (cached != null && !ReferenceEquals(cached, live)) field.SetValue(component, live);
    }
    void RestoreSources()
    {
        StopAllCoroutines();
        foreach (Marker marker in markers.Values)
            if (marker.Source != null) marker.Source.SetActive(marker.WasActive);
        markers.Clear(); records.Clear(); budgets.Clear(); knownEnemies.Clear();
        rosterRequested = false; nextRosterRequest = 0f;
        // Do not reset sequence when a local player briefly reconnects in the same
        // host session: old requester/marker IDs must not be accidentally reused.
    }
}

// Original adapter only. Queries the installed mod's public ocean-height API;
// does not reproduce its water simulation or modify player swimming state.
[DefaultExecutionOrder(-30000)]
public sealed class IliacMpAquaticWater : MonoBehaviour
{
    internal IliacPuddleMpEnemies Owner;
    const BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly FieldInfo Surface = typeof(EnemyMotor).GetField("cachedAquaticWaterSurfaceY", PrivateFields);
    static readonly FieldInfo Valid = typeof(EnemyMotor).GetField("cachedAquaticWaterValid", PrivateFields);
    static readonly FieldInfo Refresh = typeof(EnemyMotor).GetField("nextAquaticWaterRefreshTime", PrivateFields);
    static readonly FieldInfo Swims = typeof(EnemyMotor).GetField("swims", PrivateFields);
    MethodInfo oceanQuery;
    float nextBind, nextDiagnostic;
    bool warnedLayout;
    readonly object[] oceanArguments = { 0f };
    readonly HashSet<EnemyMotor> leased = new HashSet<EnemyMotor>();
    readonly HashSet<EnemyMotor> current = new HashSet<EnemyMotor>();
    readonly List<EnemyMotor> release = new List<EnemyMotor>();

    bool Bind()
    {
        if (Surface == null || Surface.FieldType != typeof(float) ||
            Valid == null || Valid.FieldType != typeof(bool) ||
            Refresh == null || Refresh.FieldType != typeof(float) ||
            Swims == null || Swims.FieldType != typeof(bool))
        {
            if (!warnedLayout) Debug.LogWarning("[IliacMPWater v7] EnemyMotor water-cache fields unavailable. Water test inactive.");
            warnedLayout = true;
            return false;
        }
        if (oceanQuery != null) return true;
        if (Time.unscaledTime < nextBind) return false;
        nextBind = Time.unscaledTime + 3f;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type world = assembly.GetType("DeepWaters.DeepWaterWorld", false);
            if (world == null) continue;
            oceanQuery = world.GetMethod("TryGetOceanSurfaceWorldY", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(float).MakeByRefType() }, null);
            if (oceanQuery == null || oceanQuery.ReturnType != typeof(bool))
            {
                oceanQuery = null;
                if (!warnedLayout) Debug.LogWarning("[IliacMPWater v7] Installed Deep Waters has no compatible public ocean-height query. Water test inactive.");
                warnedLayout = true;
            }
            break;
        }
        return oceanQuery != null;
    }

    void FixedUpdate()
    {
        current.Clear();
        try { SupplyWater(); }
        catch (Exception e)
        {
            if (!warnedLayout) Debug.LogWarning("[IliacMPWater v7] Water query failed: " + e.GetBaseException().Message);
            warnedLayout = true;
            current.Clear();
        }
        finally { ReleaseUnused(); }
    }

    void SupplyWater()
    {
        if (Owner == null || !Owner.isActiveAndEnabled || (!NetworkServer.active && !NetworkClient.active) ||
            !GameManager.HasInstance || GameManager.Instance.PlayerEnterExit == null ||
            GameManager.Instance.PlayerEnterExit.IsPlayerInside || !Bind()) return;
        // This API accounts for world compensation. Never infer sea level from
        // the player's Y or the temporary forged dungeon/swimming waterline.
        oceanArguments[0] = 0f;
        if (!(bool)oceanQuery.Invoke(null, oceanArguments)) return;
        float oceanY = (float)oceanArguments[0];
        if (float.IsNaN(oceanY) || float.IsInfinity(oceanY)) return;
        bool diagnose = Time.unscaledTime >= nextDiagnostic;
        int diagnosticCount = 0;
        if (diagnose) nextDiagnostic = Time.unscaledTime + 5f;
        foreach (uint id in Owner.KnownEnemyIds)
        {
            NetworkIdentity identity;
            if (NetworkServer.active)
            {
                if (!NetworkServer.spawned.TryGetValue(id, out identity) || identity == null) continue;
                // Match motor simulation ownership: server-only actors and listen
                // host actors here; remote-owned actors on their owning client.
                if (identity.connectionToClient != null && identity.connectionToClient != NetworkServer.localConnection) continue;
            }
            else if (!NetworkClient.spawned.TryGetValue(id, out identity) || identity == null || !identity.hasAuthority) continue;
            if (!identity.gameObject.activeInHierarchy) continue;
            var world = identity.GetComponent<EnemyWorldPosition>();
            var motor = identity.GetComponent<EnemyMotor>();
            if (world == null || world.isDungeonSpawn || world.isInteriorSpawn ||
                motor == null || !motor.isActiveAndEnabled || !(bool)Swims.GetValue(motor)) continue;
            var behaviour = identity.GetComponent<DaggerfallEntityBehaviour>();
            var entity = behaviour == null ? null : behaviour.Entity as EnemyEntity;
            if (entity == null || entity.CurrentHealth <= 0 || entity.WorldContext != WorldContext.Exterior) continue;
            current.Add(motor);
            bool first = leased.Add(motor);
            Surface.SetValue(motor, oceanY);
            Valid.SetValue(motor, true);
            // Renew just before motor FixedUpdate. A short expiry also ensures
            // this can never leave a permanent override if the adapter stops.
            Refresh.SetValue(motor, Time.time + Mathf.Max(0.1f, Time.fixedDeltaTime * 2f));
            if (first) Debug.Log("[IliacMPWater v7] Ocean height supplied: netId=" + id +
                " peer=" + (NetworkServer.active ? "host" : "client") + " surfaceY=" + oceanY.ToString("F2"));
            if (diagnose && diagnosticCount < 3 && GameManager.Instance.PlayerObject != null &&
                (identity.transform.position - GameManager.Instance.PlayerObject.transform.position).sqrMagnitude < 15625f)
            {
                var senses = identity.GetComponent<EnemySenses>();
                Debug.Log("[IliacMPWater v7] netId=" + id + " surfaceY=" + oceanY.ToString("F2") +
                    " enemyY=" + identity.transform.position.y.ToString("F2") + " canAct=" + motor.CanAct +
                    " target=" + (senses != null && senses.Target != null ? senses.Target.name : "none"));
                diagnosticCount++;
            }
        }
    }

    void ReleaseUnused()
    {
        release.Clear();
        foreach (var motor in leased)
            if (motor == null || !current.Contains(motor))
            {
                if (motor != null)
                {
                    Valid.SetValue(motor, false);
                    Refresh.SetValue(motor, 0f);
                }
                release.Add(motor);
            }
        foreach (var motor in release) leased.Remove(motor);
    }
    void OnDisable() { current.Clear(); ReleaseUnused(); }
    void OnDestroy() { current.Clear(); ReleaseUnused(); }
}
