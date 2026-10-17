// Optional multiplayer-only session-scoped container bridge.
// Requires only additive LootCatcher registration methods and a PlayerActivate guard.
// Unity 2019.4 / Mirror 57. No WoD assembly dependency.
using System;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.Banking;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Utility;
using Mirror;
using UnityEngine;

public struct WcsContainerRequest : NetworkMessage
{
    public string area;
    public string key;
    public string items;
    public int stockedDate;
    public int image;
    public int archive;
    public int record;
}
public struct WcsContainerReply : NetworkMessage
{
    public string area;
    public string key;
    public uint lootId;
    public int version;
    public string items;
    public int stockedDate;
    public bool depleted;
    public int image;
    public int archive;
    public int record;
}
public struct WcsCampContainer
{
    public uint lootId;
    public int version;
    public bool depleted;
    public int type;
    public int image;
    public int archive;
    public int record;
    public float x;
    public float y;
    public float z;
    public string items;
}
public struct WcsCampRequest : NetworkMessage
{
    public string area;
    public WcsCampContainer[] containers;
}
public struct WcsCampReply : NetworkMessage
{
    public string area;
    public WcsCampContainer[] containers;
}
// Generic outdoor loot is separate from WoD's first-layout-wins camps.
// e:areas correspond to locally streamed DFU map-pixel terrains only.
public struct WcsExteriorRequest : NetworkMessage
{
    public string area;
    public string key;
    public string items;
    public int stockedDate;
    public int image;
    public int archive;
    public int record;
    public int worldX;
    public int worldZ;
    public float height;
}
public struct WcsExteriorSnapshot : NetworkMessage
{
    public string area;
    public string key;
    // For a direct reply only: key used by the requesting client before
    // the server resolved nearby coordinates to an existing canonical pile.
    public string requestedKey;
    public string items;
    public uint lootId;
    public int version;
    public bool depleted;
    public int image;
    public int archive;
    public int record;
    public int worldX;
    public int worldZ;
    public float height;
    public int stockedDate;
}
public struct WcsExteriorInterest : NetworkMessage
{
    public string[] areas;
    // Client reports what is actually bound, not just what was delivered by Mirror.
    // A snapshot lost during streaming must be offered again.
    public uint[] boundIds;
}
// Each client receives the listen-host/dedicated-server policy before scanning.
public struct WcsWorldPolicyRequest : NetworkMessage { }
public struct WcsWorldPolicyReply : NetworkMessage { public bool enabled; }
// Explicit negative answer: prevent an unapproved native pile being lootable.
public struct WcsExteriorRejected : NetworkMessage
{
    public string area;
    public string key;
}
public struct WcsPresence : NetworkMessage
{
    public string[] areas;
}
// Reader lifecycle is separate from LootMultiplayer's item transfers.
public struct WcsReader : NetworkMessage
{
    public uint lootId;
    public bool open;
}
public struct WcsDepleted : NetworkMessage
{
    public string area;
    public uint lootId;
}

/// <summary>
/// Multiplayer-only session-scoped container bridge (v1.3.4).
/// Native dungeon/interior containers, WoD first-layout camps, and best-effort
/// coordinate-identified generic modded exterior RandomTreasure piles.
/// Replicates only standard loot prefabs; no custom mod scripts or persistent
/// shared-world save. SP never starts the scanner or changes stock generation.
/// The server retains data, not scene GameObjects; works with a headless Mirror server.
/// </summary>
public sealed class WorldContainerSync : MonoBehaviour
{
    const string Tag = "[WorldContainerSync v1.3.4] ";
    const float ScanSeconds = 0.8f;
    const float PresenceSeconds = 2.0f;
    const float PresenceExpirySeconds = 8f;
    const float StateGraceSeconds = 12f;
    const float CampSettleSeconds = 3.0f;
    const float EmptyCampDiscoverySeconds = 6f;
    const float CampRetrySeconds = 3.0f;
    const float DepletionScanSeconds = 0.25f;
    const int MaxItemsText = 12288;
    const int MaxCampText = 60000;
    const int MaxCampContainers = 32;
    const int MaxExteriorAreas = 128;
    const int MaxExteriorPerArea = 128;
    // The unknown-mod path intentionally handles only ordinary DFU treasure piles.
    // Do not couple native dynamic loot with WoD camp layout replication.
    const float ExteriorInitializeWait = 2.5f;
    // World coordinates use 40 Daggerfall units per Unity unit. Small
    // differences caused by pixel rounding, streaming/floating-origin updates
    // must not create multiple independent treasure IDs for one object.
    const float ExteriorMatchWorldUnits = 38f; // about 0.95 Unity units
    const float ExteriorMatchHeight = 1.35f;
    // A remote pile might be announced before a mod finishes spawning its
    // native object. Prefer the native object; only clone after a settle period.
    const float ExteriorReplicaWait = 6.0f;
    const float ExteriorResendSeconds = 8.0f;
    const int MaxExteriorBoundIds = 1024;
    // 200 horizontal Unity metres expressed in GLOBAL Daggerfall world units.
    // The server uses WorldX/WorldZ only; floating origin and pixel boundaries
    // cannot change this check. Existing same-spot reconciliation takes priority.
    const long OtherSourceExclusionWorldUnits = 8000L;
    const float WorldPolicyRetrySeconds = 2f;

    sealed class SharedContainer
    {
        public string Area;
        public uint Id;
        public int StockedDate;
        public bool Depleted;
        public bool IsTreasure;
        public int Image;
        public int Archive;
        public int Record;
    }
    sealed class SharedExterior
    {
        public string Area, Key;
        public uint Id;
        public int StockedDate, Image, Archive, Record, WorldX, WorldZ;
        public float Height;
        public bool Depleted;
        public uint SourcePlayer;
    }
    sealed class ExteriorInterest
    {
        public readonly HashSet<string> Areas = new HashSet<string>();
        public readonly HashSet<uint> Bound = new HashSet<uint>();
        public readonly Dictionary<uint, float> LastSent = new Dictionary<uint, float>();
    }
    sealed class LocalExterior
    {
        public string Area, Key;
        public DaggerfallLoot Loot;
        public uint Id;
        public bool Ready, Replica, Depleted;
        public int WorldX, WorldZ;
        public float Height;
        public float LastRequest;
    }
    sealed class PendingExterior
    {
        public WcsExteriorSnapshot Snapshot;
        public float ReceivedAt;
    }
    sealed class SharedCamp
    {
        public string Area;
        public WcsCampContainer[] Containers;
    }
    sealed class LocalContainer
    {
        public DaggerfallLoot Loot;
        public uint NetworkId;
        public string Area;
        public string Key;
        public bool Ready;
        public bool Depleted;
        public float LastRequest;
    }
    sealed class LocalCamp
    {
        public string Area;
        public Transform Root;
        public readonly List<DaggerfallLoot> Originals = new List<DaggerfallLoot>();
        public readonly Dictionary<DaggerfallLoot, bool> InitiallyActive = new Dictionary<DaggerfallLoot, bool>();
        public readonly List<DaggerfallLoot> Replicas = new List<DaggerfallLoot>();
        public float DiscoveredAt;
        public float LastRequest;
        public WcsCampContainer[] InitialManifest;
        public bool Ready;
    }

    static WorldContainerSync instance;
    static readonly Dictionary<string, SharedContainer> serverContainers = new Dictionary<string, SharedContainer>();
    static readonly Dictionary<string, SharedCamp> serverCamps = new Dictionary<string, SharedCamp>();
    static readonly Dictionary<string, SharedExterior> serverExterior = new Dictionary<string, SharedExterior>();
    static readonly Dictionary<NetworkConnection, ExteriorInterest> serverExteriorInterests =
        new Dictionary<NetworkConnection, ExteriorInterest>();
    // Area presence is refreshed by the corresponding player, not the server's local terrain scene.
    static readonly Dictionary<NetworkConnection, Dictionary<string, float>> serverPresence =
        new Dictionary<NetworkConnection, Dictionary<string, float>>();
    static readonly Dictionary<string, float> emptySince = new Dictionary<string, float>();
    // Only readers of session-world treasure are counted. Corpse and dropped loot never enter this table.
    static readonly Dictionary<uint, HashSet<NetworkConnection>> serverReaders =
        new Dictionary<uint, HashSet<NetworkConnection>>();

    readonly Dictionary<string, LocalContainer> localContainers = new Dictionary<string, LocalContainer>();
    readonly Dictionary<string, LocalCamp> localCamps = new Dictionary<string, LocalCamp>();
    readonly Dictionary<string, LocalExterior> localExterior = new Dictionary<string, LocalExterior>();
    readonly Dictionary<string, PendingExterior> pendingExterior = new Dictionary<string, PendingExterior>();
    readonly Dictionary<DaggerfallLoot, bool> suppressedExterior = new Dictionary<DaggerfallLoot, bool>();
    // Unlike nearby native duplicates these were explicitly rejected by the
    // server's other-source exclusion and must stay hidden until the area unloads.
    readonly Dictionary<DaggerfallLoot, bool> rejectedExterior = new Dictionary<DaggerfallLoot, bool>();
    readonly Dictionary<DaggerfallLoot, float> exteriorFirstSeen = new Dictionary<DaggerfallLoot, float>();
    static readonly Dictionary<string, Texture2D> textureChecks = new Dictionary<string, Texture2D>();
    static readonly HashSet<string> unavailableTextures = new HashSet<string>();
    static TextureReader exteriorTextureReader;
    readonly HashSet<uint> localDepleted = new HashSet<uint>();
    float nextScan, nextPresence, nextExpiry, nextEmptyCampDiscovery, nextDepletionScan, nextWorldPolicyRequest;
    bool networkWasActive;
    // Only a first-click continuation for ordinary non-networked containers.
    // The original LootCatcher edit/grant/commit flow is completely untouched.
    DaggerfallLoot pendingActivationLoot;
    string pendingActivationArea;
    Action pendingActivation;
    float pendingActivationAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var obj = new GameObject("WorldContainerSync");
        DontDestroyOnLoad(obj);
        instance = obj.AddComponent<WorldContainerSync>();
    }

    // IMPORTANT: Mirror's transport connection can be established before the local
    // host/client finishes authentication and becomes ready. Sending WcsPresence
    // during that gap disconnects the host's own local connection, leaving a
    // "Running as Server" HUD with no party player. Wait until BOTH phases finish.
    // This applies equally to P2P host, remote clients and a dedicated-server client.
    static bool Connected
    {
        get
        {
            return NetworkClient.active && NetworkClient.isConnected &&
                NetworkClient.connection != null && NetworkClient.connection.isAuthenticated &&
                NetworkClient.ready;
        }
    }

    void Update()
    {
        bool active = NetworkClient.active || NetworkServer.active;
        if (!active)
        {
            if (networkWasActive) ResetAll();
            networkWasActive = false;
            WorldContainerPolicy.ClearDisconnectedRemotePolicy();
            return;
        }
        // Policy delivery must work even when world-container synchronization
        // is switched OFF. No container handlers are called before policy sync.
        bool scanTick = Time.realtimeSinceStartup >= nextScan;
        if (scanTick)
        {
            nextScan = Time.realtimeSinceStartup + ScanSeconds;
            InstallHandlers();
        }
        if (Connected && !NetworkServer.active && !WorldContainerPolicy.HasRemotePolicy &&
            Time.realtimeSinceStartup >= nextWorldPolicyRequest)
        {
            nextWorldPolicyRequest = Time.realtimeSinceStartup + WorldPolicyRetrySeconds;
            NetworkClient.Send(new WcsWorldPolicyRequest());
        }
        if (!WorldContainerPolicy.Enabled)
        {
            if (networkWasActive) ResetAll();
            networkWasActive = false;
            return;
        }
        networkWasActive = true;
        if (Connected && scanTick &&
            !(SaveLoadManager.HasInstance && SaveLoadManager.Instance.LoadInProgress))
            ScanLocal();
        if (Connected && Time.realtimeSinceStartup >= nextPresence)
        {
            nextPresence = Time.realtimeSinceStartup + PresenceSeconds;
            SendPresence();
        }
        if (NetworkServer.active && Time.realtimeSinceStartup >= nextDepletionScan)
        {
            nextDepletionScan = Time.realtimeSinceStartup + DepletionScanSeconds;
            CheckDepletedOnServer();
        }
        if (NetworkServer.active && Time.realtimeSinceStartup >= nextExpiry)
        {
            nextExpiry = Time.realtimeSinceStartup + 2f;
            ExpireUnusedAreas();
        }
    }

    void InstallHandlers()
    {
        // Mirror's handlers can be cleared during reconnect; these are distinct message types.
        if (NetworkServer.active)
        {
            NetworkServer.ReplaceHandler<WcsContainerRequest>(ReceiveContainerRequest);
            NetworkServer.ReplaceHandler<WcsCampRequest>(ReceiveCampRequest);
            NetworkServer.ReplaceHandler<WcsPresence>(ReceivePresence);
            NetworkServer.ReplaceHandler<WcsReader>(ReceiveReader);
            NetworkServer.ReplaceHandler<WcsExteriorRequest>(ReceiveExteriorRequest);
            NetworkServer.ReplaceHandler<WcsExteriorInterest>(ReceiveExteriorInterest);
            NetworkServer.ReplaceHandler<WcsWorldPolicyRequest>(ReceiveWorldPolicyRequest);
        }
        if (NetworkClient.active)
        {
            NetworkClient.ReplaceHandler<WcsContainerReply>(ReceiveContainerReply);
            NetworkClient.ReplaceHandler<WcsCampReply>(ReceiveCampReply);
            NetworkClient.ReplaceHandler<WcsDepleted>(ReceiveDepleted);
            NetworkClient.ReplaceHandler<WcsExteriorSnapshot>(ReceiveExteriorSnapshot);
            NetworkClient.ReplaceHandler<WcsExteriorRejected>(ReceiveExteriorRejected);
            NetworkClient.ReplaceHandler<WcsWorldPolicyReply>(ReceiveWorldPolicyReply);
        }
    }

    void ResetAll()
    {
        foreach (var c in localCamps.Values) RestoreCamp(c);
        localCamps.Clear();
        foreach (var exterior in localExterior.Values) UnbindExterior(exterior);
        localExterior.Clear();
        pendingExterior.Clear();
        foreach (var pair in suppressedExterior)
            if (pair.Key != null) pair.Key.gameObject.SetActive(pair.Value);
        suppressedExterior.Clear();
        foreach (var pair in rejectedExterior)
            if (pair.Key != null) pair.Key.gameObject.SetActive(pair.Value);
        rejectedExterior.Clear();
        exteriorFirstSeen.Clear();
        foreach (var c in localContainers.Values)
            LootCatcher.UnbindWorldContainer(c.NetworkId, c.Loot);
        localContainers.Clear();
        localDepleted.Clear();
        // Server must not retain data between a stopped listen-host session and a new one.
        if (NetworkServer.active == false)
        {
            LootCatcher.ServerForgetAllWorldContainers();
            serverContainers.Clear();
            serverCamps.Clear();
            serverExterior.Clear();
            serverExteriorInterests.Clear();
            serverPresence.Clear();
            serverReaders.Clear();
            emptySince.Clear();
        }
        ClearPendingActivation();
        nextScan = nextPresence = nextExpiry = nextEmptyCampDiscovery = nextDepletionScan = 0;
        nextWorldPolicyRequest = 0;
    }

    void ReceiveWorldPolicyRequest(NetworkConnection conn, WcsWorldPolicyRequest m)
    {
        if (conn == null || !conn.isReady || conn.identity == null) return;
        conn.Send(new WcsWorldPolicyReply { enabled = WorldContainerPolicy.Enabled });
    }

    void ReceiveWorldPolicyReply(WcsWorldPolicyReply m)
    {
        if (!NetworkClient.active || NetworkServer.active) return;
        WorldContainerPolicy.AcceptRemotePolicy(m.enabled);
        Debug.Log(Tag + "Server world-container sync: " + (m.enabled ? "On" : "Off"));
    }

    static string CurrentInteriorArea()
    {
        if (GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null ||
            GameManager.Instance.PlayerGPS == null) return null;
        var ee = GameManager.Instance.PlayerEnterExit;
        if (ee.IsPlayerInsideDungeon && ee.Dungeon != null)
        {
            string id = ee.Dungeon.DungeonInstanceId;
            // Never merge two independent local/single-player dungeons by map ID alone.
            if (string.IsNullOrEmpty(id)) return null;
            return "d:" + id;
        }
        if (ee.IsPlayerInsideBuilding && ee.Interior != null)
        {
            int building = ee.BuildingDiscoveryData.buildingKey;
            int map = GameManager.Instance.PlayerGPS.CurrentMapID;
            if (building == 0 || map < 0) return null;
            return "i:" + map + ":" + building;
        }
        return null;
    }

    static Transform FindCampRoot(DaggerfallLoot loot)
    {
        if (loot == null || GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null ||
            GameManager.Instance.PlayerEnterExit.IsPlayerInside) return null;
        Transform first = null;
        for (Transform t = loot.transform.parent; t != null; t = t.parent)
        {
            if (t.name.IndexOf("WOD_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Nearest WOD_ ancestor is the location prefab, not the shared streaming root.
                first = t;
                break;
            }
        }
        return first;
    }

    static string CampArea(Transform camp)
    {
        if (camp == null || GameManager.Instance == null || GameManager.Instance.PlayerGPS == null ||
            GameManager.Instance.PlayerObject == null) return null;
        var gps = GameManager.Instance.PlayerGPS;
        Vector3 offset = camp.position - GameManager.Instance.PlayerObject.transform.position;
        // World DF coords avoid the 819.2-unit terrain seam / floating-origin mismatch.
        double wx = gps.WorldX + offset.x * 40.0;
        double wz = gps.WorldZ + offset.z * 40.0;
        // 2m cells absorb small loader placement discrepancies. A campsite must have a
        // reasonably stable origin for different variants to share a key.
        return "w:" + Math.Round(wx / 80.0) + ":" + Math.Round(wz / 80.0);
    }

    // Strict allow-list: corpse, dropped loot, quest corpse, and shop shelves never
    // enter this subsystem or its location/presence lifecycle.
    static bool IsNativeEligible(DaggerfallLoot loot)
    {
        return loot != null && !loot.playerOwned && !loot.customDrop &&
               loot.corpseQuestUID == 0 &&
               loot.GetComponent<QuestResourceBehaviour>() == null &&
               (loot.ContainerType == LootContainerTypes.RandomTreasure ||
                loot.ContainerType == LootContainerTypes.HouseContainers);
    }

    static string NativeKey(string area, DaggerfallLoot loot)
    {
        if (string.IsNullOrEmpty(area) || loot == null) return null;
        var ee = GameManager.Instance.PlayerEnterExit;
        Transform root = ee.IsPlayerInsideDungeon ? ee.Dungeon.transform : ee.Interior.transform;
        Transform identityTransform = loot.transform;
        if (ee.IsPlayerInsideDungeon && loot.ContainerType == LootContainerTypes.RandomTreasure &&
            loot.transform.parent != null)
        {
            // RDBLayout.AddRandomTreasure() parents dungeon loot to its original
            // flat/editor marker. The loot's FINAL position is not deterministic:
            // different random treasure billboard records have different heights,
            // and GameObjectHelper shifts the loot up by half that random height.
            // Thus two players' identical markers can have different loot Y values.
            // Their underlying marker does NOT have that randomized offset.
            identityTransform = loot.transform.parent;
        }
        Vector3 rel = identityTransform.position - root.position;
        // Same LoadID may appear in multiple copies of an RDB block, so include
        // the stable marker position relative to the dungeon instance origin.
        // This identity never uses the random treasure billboard's position.
        return area + "|" + ((int)loot.ContainerType) + "|" + loot.LoadID + "|" +
               Mathf.RoundToInt(rel.x * 20) + ":" + Mathf.RoundToInt(rel.y * 20) + ":" + Mathf.RoundToInt(rel.z * 20);
    }

    // A native dungeon marker's random billboard may have a different vertical
    // offset on every client; keep its original marker-based identity unchanged.
    // Transmit only archive/record IDs, never texture/image data.
    static bool ResolveAppearance(int archive, int record, uint id, out int safeArchive, out int safeRecord)
    {
        safeArchive = archive;
        safeRecord = record;
        string key = archive + ":" + record;
        if (archive >= 0 && record >= 0 && archive <= 99999 && record <= 65535 &&
            !unavailableTextures.Contains(key))
        {
            if (textureChecks.ContainsKey(key)) return true;
            try
            {
                if (exteriorTextureReader == null)
                    exteriorTextureReader = new TextureReader(DaggerfallUnity.Instance.Arena2Path);
                Texture2D t = exteriorTextureReader.GetTexture2D(archive, record);
                if (t != null)
                {
                    // Retain the reference: imported textures may be shared by mods.
                    // Never Destroy an asset that a texture-replacement mod owns.
                    textureChecks[key] = t;
                    return true;
                }
            }
            catch (Exception) { /* Unknown archive/record: use the vanilla fallback. */ }
            unavailableTextures.Add(key);
        }
        int[] icons = DaggerfallLootDataTables.randomTreasureIconIndices;
        safeArchive = DaggerfallLootDataTables.randomTreasureArchive;
        safeRecord = icons != null && icons.Length > 0 ? icons[(int)(id % (uint)icons.Length)] : 0;
        return false;
    }

    static DaggerfallLoot CreateSafeTreasure(Vector3 position, Transform parent,
        int archive, int record, uint lootId, InventoryContainerImages image, bool adjustPosition)
    {
        int safeArchive, safeRecord;
        ResolveAppearance(archive, record, lootId, out safeArchive, out safeRecord);
        try
        {
            DaggerfallLoot result = GameObjectHelper.CreateLootContainer(
                LootContainerTypes.RandomTreasure, image, position, parent,
                safeArchive, safeRecord, 0, null, adjustPosition);
            if (result != null) return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(Tag + "Modded treasure graphic failed; attempting vanilla fallback: " + ex.Message);
        }
        // When a mod's visual replacement throws while being instantiated,
        // make one final attempt using known vanilla identifiers.
        int[] icons = DaggerfallLootDataTables.randomTreasureIconIndices;
        if (icons == null || icons.Length == 0) return null;
        int fallbackRecord = icons[(int)(lootId % (uint)icons.Length)];
        if (safeArchive == DaggerfallLootDataTables.randomTreasureArchive && safeRecord == fallbackRecord)
            return null; // Already attempted this exact vanilla icon.
        try
        {
            return GameObjectHelper.CreateLootContainer(
                LootContainerTypes.RandomTreasure, InventoryContainerImages.Chest,
                position, parent, DaggerfallLootDataTables.randomTreasureArchive,
                fallbackRecord, 0, null, adjustPosition);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(Tag + "Vanilla treasure fallback failed: " + ex.Message);
            return null;
        }
    }

    static void ApplyNativeTreasureAppearance(DaggerfallLoot loot, int archive, int record, uint id)
    {
        if (loot == null || loot.ContainerType != LootContainerTypes.RandomTreasure) return;
        Billboard billboard = loot.GetComponent<Billboard>();
        if (billboard == null) return; // Custom model replacement; preserve the local modded artwork.
        int a, r;
        ResolveAppearance(archive, record, id, out a, out r);
        if (loot.TextureArchive == a && loot.TextureRecord == r) return;
        try
        {
            float previousHeight = billboard.Summary.Size.y;
            billboard.SetMaterial(a, r);
            float changedHeight = billboard.Summary.Size.y;
            // Centre-anchored vanilla billboards: keep bottom at the old floor position.
            loot.transform.position += Vector3.up * ((changedHeight - previousHeight) * 0.5f);
            loot.TextureArchive = a;
            loot.TextureRecord = r;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(Tag + "Cannot apply treasure appearance, retaining local graphic: " + ex.Message);
        }
    }

    static bool IsOutside()
    {
        return GameManager.Instance != null && GameManager.Instance.PlayerEnterExit != null &&
            !GameManager.Instance.PlayerEnterExit.IsPlayerInside &&
            GameManager.Instance.StreamingWorld != null && GameManager.Instance.PlayerGPS != null &&
            GameManager.Instance.PlayerObject != null;
    }

    static bool IsGenericExteriorLoot(DaggerfallLoot loot)
    {
        return IsOutside() && IsNativeEligible(loot) &&
            loot.ContainerType == LootContainerTypes.RandomTreasure &&
            loot.GetComponent<WorldContainerReplica>() == null &&
            FindCampRoot(loot) == null;
    }

    static string ExteriorArea(int px, int py) { return "e:" + px + ":" + py; }

    static bool ParseExteriorArea(string area, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrEmpty(area) || area.Length > 40 || !area.StartsWith("e:", StringComparison.Ordinal))
            return false;
        string[] parts = area.Split(':');
        return parts.Length == 3 && int.TryParse(parts[1], out x) && int.TryParse(parts[2], out y) &&
            x >= 0 && y >= 0 && x < MapsFile.MaxMapPixelX && y < MapsFile.MaxMapPixelY;
    }

    static bool TryExteriorIdentity(DaggerfallLoot loot, out string area, out string key,
        out int worldX, out int worldZ, out float height)
    {
        area = key = null;
        worldX = worldZ = 0;
        height = 0;
        if (!IsOutside() || loot == null) return false;
        var gm = GameManager.Instance;
        Vector3 delta = loot.transform.position - gm.PlayerObject.transform.position;
        double wx = gm.PlayerGPS.WorldX + delta.x * 40.0;
        double wz = gm.PlayerGPS.WorldZ + delta.z * 40.0;
        // Normalize the bottom of the sprite, not its randomly sized centre.
        // This is the exterior equivalent of the fixed RDB marker identity.
        height = loot.transform.position.y - gm.StreamingWorld.WorldCompensation.y;
        Billboard icon = loot.GetComponent<Billboard>();
        if (icon != null)
        {
            try { height -= icon.Summary.Size.y * 0.5f; }
            catch (Exception) { /* Malformed modded billboard: keep original height. */ }
        }
        if (double.IsNaN(wx) || double.IsNaN(wz) || !Finite(height) ||
            Math.Abs(wx) > 100000000 || Math.Abs(wz) > 100000000 || Math.Abs(height) > 200000) return false;
        worldX = (int)Math.Round(wx);
        worldZ = (int)Math.Round(wz);
        DaggerfallConnect.Utility.DFPosition pixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
        area = ExteriorArea(pixel.X, pixel.Y);
        // Stable across different mod LoadIDs and randomized billboard heights.
        // A 0.5 Unity-unit X/Z grid and 2-unit floor height. Two containers
        // colliding on this grid are rejected locally rather than merged.
        key = area + "|" + (int)Math.Round(wx / 20.0) + ":" +
            (int)Math.Round(wz / 20.0) + ":" + Mathf.RoundToInt(height / 2f);
        return true;
    }

    static bool SameExteriorSpot(int ax, int az, float ah, int bx, int bz, float bh)
    {
        double dx = (double)ax - bx, dz = (double)az - bz;
        return Math.Abs(ah - bh) <= ExteriorMatchHeight &&
            dx * dx + dz * dz <= ExteriorMatchWorldUnits * ExteriorMatchWorldUnits;
    }

    // Find exactly one existing shared pile near this world position.
    // Do not guess when multiple distinct piles are close enough to be ambiguous.
    static SharedExterior FindSharedExteriorNear(string area, int wx, int wz, float height, out bool ambiguous)
    {
        ambiguous = false;
        SharedExterior selected = null;
        foreach (var c in serverExterior.Values)
        {
            if (c.Area != area || !SameExteriorSpot(c.WorldX, c.WorldZ, c.Height, wx, wz, height))
                continue;
            if (selected != null && selected.Id != c.Id)
            {
                ambiguous = true;
                return null;
            }
            selected = c;
        }
        return selected;
    }

    LocalExterior FindLocalExteriorByObject(DaggerfallLoot loot)
    {
        foreach (var c in localExterior.Values)
            if (c.Loot == loot) return c;
        return null;
    }

    LocalExterior FindLocalExteriorNear(string area, int wx, int wz, float height,
        uint expectedId, out bool ambiguous)
    {
        ambiguous = false;
        LocalExterior selected = null;
        foreach (var c in localExterior.Values)
        {
            if (c.Area != area || c.Loot == null ||
                !SameExteriorSpot(c.WorldX, c.WorldZ, c.Height, wx, wz, height)) continue;
            if (c.Ready && expectedId != 0 && c.Id != expectedId)
            {
                ambiguous = true; return null;
            }
            if (selected != null && selected != c) { ambiguous = true; return null; }
            selected = c;
        }
        return selected;
    }

    void SuppressDuplicateExterior(DaggerfallLoot loot, uint id)
    {
        if (loot == null || suppressedExterior.ContainsKey(loot)) return;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync != null && (sync.lootId != 0 || sync.EditPending)) return;
        suppressedExterior[loot] = loot.gameObject.activeSelf;
        loot.gameObject.SetActive(false);
        Debug.Log(Tag + "Suppressed overlapping native exterior treasure beside id=" + id);
    }

    static GameObject ActiveTerrainForArea(string area)
    {
        if (!IsOutside()) return null;
        int x, y;
        if (!ParseExteriorArea(area, out x, out y)) return null;
        GameObject terrain = GameManager.Instance.StreamingWorld.GetTerrainFromPixel(x, y);
        return terrain != null && terrain.activeInHierarchy ? terrain : null;
    }

    static Vector3 ExteriorLocalPose(int worldX, int worldZ, float height)
    {
        var gm = GameManager.Instance;
        Vector3 p = gm.PlayerObject.transform.position;
        p.x += (float)((worldX - (double)gm.PlayerGPS.WorldX) / 40.0);
        p.z += (float)((worldZ - (double)gm.PlayerGPS.WorldZ) / 40.0);
        p.y = height + gm.StreamingWorld.WorldCompensation.y;
        return p;
    }

    static bool IsGenericInitialized(DaggerfallLoot loot)
    {
        var serializer = FindModComponent(loot.gameObject, "LocationLootSerializer");
        if (serializer != null)
        {
            bool activated;
            return TryReadLocationLoaderBool(serializer, "Activated", out activated) && activated;
        }
        // Unknown mods may create a blank prefab before filling its items.
        // If both flags are blank, wait rather than make an empty first snapshot.
        return loot.stockedDate != 0 || loot.Items.Count > 0;
    }

    static bool IsInCurrentNativeRoot(DaggerfallLoot loot)
    {
        if (loot == null || GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null) return false;
        var ee = GameManager.Instance.PlayerEnterExit;
        Transform root = ee.IsPlayerInsideDungeon && ee.Dungeon != null ? ee.Dungeon.transform :
                         ee.IsPlayerInsideBuilding && ee.Interior != null ? ee.Interior.transform : null;
        return root != null && loot.transform.IsChildOf(root);
    }

    // Called by DaggerfallInventoryWindow only when a bound world-treasure inventory opens/closes.
    // Furniture, corpse markers, dropped loot, quest loot and ordinary SP are untouched.
    public static void InventoryOpened(DaggerfallLoot loot)
    {
        if (!WorldContainerPolicy.Enabled) return;
        uint id = SharedTreasureId(loot);
        if (id != 0 && Connected && instance != null && !instance.localDepleted.Contains(id))
        {
            // Presence precedes the reader message even during the first frame
            // after a dungeon/interior transition, preventing a false rejection.
            instance.SendPresence();
            NetworkClient.Send(new WcsReader { lootId = id, open = true });
        }
    }

    public static void InventoryClosed(DaggerfallLoot loot)
    {
        if (!WorldContainerPolicy.Enabled) return;
        uint id = SharedTreasureId(loot);
        if (id != 0 && Connected)
            NetworkClient.Send(new WcsReader { lootId = id, open = false });
    }

    static uint SharedTreasureId(DaggerfallLoot loot)
    {
        if (instance == null || loot == null || loot.ContainerType != LootContainerTypes.RandomTreasure)
            return 0;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync == null || sync.lootId == 0) return 0;
        if (loot.GetComponent<WorldContainerReplica>() != null)
            return sync.lootId;
        foreach (var c in instance.localContainers.Values)
            if (c.Ready && c.Loot == loot && c.NetworkId == sync.lootId)
                return sync.lootId;
        foreach (var c in instance.localExterior.Values)
            if (c.Ready && c.Loot == loot && c.Id == sync.lootId)
                return sync.lootId;
        return 0;
    }

    static bool IsInventoryOpenFor(DaggerfallLoot loot)
    {
        return loot != null && DaggerfallUI.Instance != null &&
            DaggerfallUI.Instance.InventoryWindow != null &&
            DaggerfallUI.Instance.InventoryWindow.LootTarget == loot;
    }

    // Don't disable an object while somebody still has its window open or while an
    // inventory transaction is pending. Keeping the component bound also prevents ID reuse.
    void ApplyLocalDepletion()
    {
        foreach (var c in localContainers.Values)
        {
            if (!c.Ready || c.Loot == null ||
                c.Loot.ContainerType != LootContainerTypes.RandomTreasure ||
                !localDepleted.Contains(c.NetworkId)) continue;
            var sync = c.Loot.GetComponent<LootMultiplayer>();
            if (!IsInventoryOpenFor(c.Loot) && (sync == null || !sync.EditPending))
                c.Loot.gameObject.SetActive(false);
        }
        foreach (var c in localExterior.Values)
        {
            if (!c.Ready || c.Loot == null || !localDepleted.Contains(c.Id)) continue;
            var sync = c.Loot.GetComponent<LootMultiplayer>();
            if (!IsInventoryOpenFor(c.Loot) && (sync == null || !sync.EditPending))
                c.Loot.gameObject.SetActive(false);
        }
        foreach (var camp in localCamps.Values)
            foreach (var clone in camp.Replicas)
            {
                if (clone == null || !localDepleted.Contains(SharedTreasureId(clone))) continue;
                var sync = clone.GetComponent<LootMultiplayer>();
                if (!IsInventoryOpenFor(clone) && (sync == null || !sync.EditPending))
                    clone.gameObject.SetActive(false);
            }
    }

    /// <summary>PlayerActivate guard: never allow interaction before the first snapshot.</summary>
    public static bool AllowActivate(DaggerfallLoot loot, Action onFirstHouseStocked = null, Action onReady = null)
    {
        // Until the server option arrives, do not allow a client to open a
        // locally stocked world container that the server might be sharing.
        // This does NOT block enemy corpses, drops, shop shelves, or SP play.
        if (NetworkClient.active && !NetworkServer.active && Connected &&
            !WorldContainerPolicy.HasRemotePolicy && loot != null && IsNativeEligible(loot))
        {
            DaggerfallUI.AddHUDText("Waiting for server container settings...");
            return false;
        }
        // No extra handling for CorpseMarker, DroppedLoot, ShopShelves, etc.
        if (!WorldContainerPolicy.Enabled || !Connected || loot == null || !IsNativeEligible(loot)) return true;
        if (instance == null) return true;
        // Generic exterior replicas are explicitly registered and never re-register.
        if (loot.GetComponent<WorldContainerReplica>() != null)
        {
            uint id = SharedTreasureId(loot);
            return id != 0 && !instance.localDepleted.Contains(id);
        }
        var campRoot = FindCampRoot(loot);
        if (campRoot != null && loot.ContainerType == LootContainerTypes.RandomTreasure)
        {
            string area = CampArea(campRoot);
            LocalCamp camp;
            if (area != null && instance.localCamps.TryGetValue(area, out camp) && camp.Ready)
                return loot.GetComponent<WorldContainerReplica>() != null &&
                    !instance.localDepleted.Contains(SharedTreasureId(loot));
            instance.ScanLocal();
            DaggerfallUI.AddHUDText("Camp loot is synchronizing...");
            return false;
        }
        if (IsGenericExteriorLoot(loot))
        {
            string outsideArea, outsideKey;
            int wx, wz; float h;
            if (TryExteriorIdentity(loot, out outsideArea, out outsideKey, out wx, out wz, out h))
            {
                LocalExterior ext;
                ext = instance.FindLocalExteriorByObject(loot);
                if (ext != null && ext.Ready)
                    return !ext.Depleted && !instance.localDepleted.Contains(ext.Id);
                instance.RegisterExterior(loot);
                instance.QueueActivation(loot, outsideArea, onReady);
                DaggerfallUI.AddHUDText("Exterior loot is synchronizing...");
                return false;
            }
        }
        string interiorArea = CurrentInteriorArea();
        if (interiorArea == null || !IsInCurrentNativeRoot(loot))
        {
            if (loot.ContainerType == LootContainerTypes.RandomTreasure &&
                GameManager.Instance != null && GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInside)
                Debug.LogWarning(Tag + "Treasure activation bypassed: missing network area or container outside current root. loadID=" + loot.LoadID);
            return true;
        }
        string key = NativeKey(interiorArea, loot);
        LocalContainer c;
        if (key != null && instance.localContainers.TryGetValue(key, out c) && c.Ready && c.Loot == loot)
        {
            if (c.Depleted || instance.localDepleted.Contains(c.NetworkId)) return false;
            // Only on explicit treasure interaction: use matching keys from host
            // and client logs to diagnose any remaining dungeon layout mismatch.
            if (loot.ContainerType == LootContainerTypes.RandomTreasure)
                Debug.Log(Tag + "Treasure open area=" + interiorArea + " key=" + key + " id=" + c.NetworkId);
            return true;
        }
        // House containers are generated on first use in vanilla; give that first
        // requester an ordinary DFU roll before requesting an authoritative snapshot.
        if (loot.ContainerType == LootContainerTypes.HouseContainers && loot.stockedDate == 0 &&
            loot.Items.Count == 0 && GameManager.Instance != null)
        {
            var ee = GameManager.Instance.PlayerEnterExit;
            if (ee != null)
            {
                bool owned = (ee.BuildingType == DFLocation.BuildingTypes.Ship && DaggerfallBankManager.OwnsShip) ||
                             DaggerfallBankManager.IsHouseOwned(ee.BuildingDiscoveryData.buildingKey);
                if (owned) loot.stockedDate = 1;
                else
                {
                    loot.StockHouseContainer(ee.BuildingDiscoveryData);
                    if (onFirstHouseStocked != null) onFirstHouseStocked();
                }
            }
        }
        instance.RegisterNative(loot, interiorArea);
        instance.QueueActivation(loot, interiorArea, onReady);
        DaggerfallUI.AddHUDText("Loot is synchronizing...");
        return false;
    }

    // A click during the first server round-trip is deferred, not lost. The
    // activation is resumed automatically if the same container, area and player
    // are still valid. This does not bypass inventory edit locks or loot grants.
    void QueueActivation(DaggerfallLoot loot, string area, Action callback)
    {
        if (callback == null || loot == null || string.IsNullOrEmpty(area)) return;
        pendingActivationLoot = loot;
        pendingActivationArea = area;
        pendingActivation = callback;
        pendingActivationAt = Time.realtimeSinceStartup;
    }

    void ClearPendingActivation()
    {
        pendingActivationLoot = null;
        pendingActivationArea = null;
        pendingActivation = null;
        pendingActivationAt = 0;
    }

    void CompletePendingActivation(DaggerfallLoot loot, string area)
    {
        if (pendingActivation == null || pendingActivationLoot != loot ||
            pendingActivationArea != area) return;
        Action action = pendingActivation;
        float started = pendingActivationAt;
        ClearPendingActivation();
        var game = GameManager.Instance;
        if (loot == null || !Connected || game == null || game.PlayerObject == null ||
            Time.realtimeSinceStartup - started > 5f) return;
        bool inSameArea = area.StartsWith("w:", StringComparison.Ordinal)
            ? FindCampRoot(loot) != null && CampArea(FindCampRoot(loot)) == area
            : area.StartsWith("e:", StringComparison.Ordinal)
              ? IsOutside() && ActiveTerrainForArea(area) != null
              : CurrentInteriorArea() == area && IsInCurrentNativeRoot(loot);
        if (!inSameArea || !loot.gameObject.activeInHierarchy) return;
        // A delayed response must not open an inventory after walking away.
        if (Vector3.Distance(game.PlayerObject.transform.position, loot.transform.position) >
            PlayerActivate.TreasureActivationDistance + 1.5f) return;
        try { action(); }
        catch (Exception ex) { Debug.LogWarning(Tag + "Deferred container activation failed: " + ex); }
    }

    // External mod containers: synchronized by absolute DF world position, not
    // a hard-coded name, assembly, or parent prefab hierarchy.
    void SendExteriorInterest()
    {
        if (!Connected) return;
        var areas = new List<string>();
        if (IsOutside())
        {
            var game = GameManager.Instance;
            DaggerfallConnect.Utility.DFPosition here = game.PlayerGPS.CurrentMapPixel;
            int radius = Mathf.Clamp(game.StreamingWorld.TerrainDistance, 0, 5);
            for (int x = here.X - radius; x <= here.X + radius; x++)
                for (int y = here.Y - radius; y <= here.Y + radius; y++)
                {
                    if (areas.Count >= MaxExteriorAreas) break;
                    GameObject terrain = game.StreamingWorld.GetTerrainFromPixel(x, y);
                    if (terrain != null && terrain.activeInHierarchy)
                        areas.Add(ExteriorArea(x, y));
                }
        }
        // Some mods hold their dynamically spawned campsite GameObjects alive
        // beyond DFU's own terrain streaming radius. Such a native container
        // is also evidence that THIS client still has its area loaded.
        foreach (var c in localExterior.Values)
            if (!areas.Contains(c.Area) && c.Loot != null && !c.Replica &&
                c.Loot.transform.parent != null &&
                c.Loot.transform.parent.gameObject.activeInHierarchy &&
                areas.Count < MaxExteriorAreas)
                areas.Add(c.Area);
        // An interest announcement is NOT an acknowledgement. Report IDs that
        // are currently bound on THIS client, so a lost/deferred snapshot can be
        // resent without continuously spamming complete inventories.
        var boundIds = new HashSet<uint>();
        foreach (var c in localExterior.Values)
            if (c.Ready && c.Loot != null && c.Id != 0 && areas.Contains(c.Area) &&
                boundIds.Count < MaxExteriorBoundIds)
            {
                var sync = c.Loot.GetComponent<LootMultiplayer>();
                if (sync != null && sync.lootId == c.Id) boundIds.Add(c.Id);
            }
        NetworkClient.Send(new WcsExteriorInterest {
            areas = areas.ToArray(), boundIds = new List<uint>(boundIds).ToArray() });
    }

    static WcsExteriorSnapshot BuildExteriorSnapshot(SharedExterior c)
    {
        int ver; string items;
        if (!LootCatcher.ServerReadWorldContainer(c.Id, out ver, out items)) return new WcsExteriorSnapshot();
        return new WcsExteriorSnapshot { area = c.Area, key = c.Key,
            lootId = c.Id, version = ver, items = items, depleted = c.Depleted,
            image = c.Image, archive = c.Archive, record = c.Record,
            worldX = c.WorldX, worldZ = c.WorldZ, height = c.Height, stockedDate = c.StockedDate };
    }

    void ReceiveExteriorRequest(NetworkConnection conn, WcsExteriorRequest m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (conn == null || !conn.isReady || conn.identity == null ||
            m.items == null || m.items.Length > MaxItemsText ||
            !ValidExteriorIdentity(m.area, m.key, m.worldX, m.worldZ, m.height) ||
            m.archive < 0 || m.record < 0) return;
        NotePresence(conn, m.area);
        SharedExterior c;
        if (!serverExterior.TryGetValue(m.key, out c))
        {
            bool ambiguous;
            c = FindSharedExteriorNear(m.area, m.worldX, m.worldZ, m.height, out ambiguous);
            if (ambiguous)
            {
                Debug.LogWarning(Tag + "Ambiguous exterior treasure request near " + m.key + "; not merging.");
                return;
            }
        }
        // An exact rounded key can also collide with two stacked objects.
        // Never silently bind clearly different physical piles together.
        if (c != null && !SameExteriorSpot(c.WorldX, c.WorldZ, c.Height,
            m.worldX, m.worldZ, m.height))
        {
            Debug.LogWarning(Tag + "Exterior key collision with different height/position: " + m.key);
            return;
        }
        if (c == null)
        {
            // Only piles created by a DIFFERENT player are excluded. Same-player
            // clustered treasure is intentionally unaffected. Include adjacent
            // map pixels and already depleted locations to prevent instant rerolls.
            long radiusSq = OtherSourceExclusionWorldUnits * OtherSourceExclusionWorldUnits;
            bool blocked = false;
            foreach (var prior in serverExterior.Values)
            {
                if (prior.SourcePlayer == conn.identity.netId) continue;
                long dx = (long)m.worldX - prior.WorldX;
                long dz = (long)m.worldZ - prior.WorldZ;
                if (dx * dx + dz * dz > radiusSq) continue;
                blocked = true;
                Debug.Log(Tag + "Rejected other-player exterior loot " + m.key +
                    " within 200m of id=" + prior.Id + " (creator=" + prior.SourcePlayer + ")");
                break;
            }
            if (blocked)
            {
                conn.Send(new WcsExteriorRejected { area = m.area, key = m.key });
                return;
            }
            int count = 0;
            foreach (var existing in serverExterior.Values)
                if (existing.Area == m.area) count++;
            if (count >= MaxExteriorPerArea) return;
            uint id = LootCatcher.ServerRegisterWorldContainer(m.items);
            if (id == 0) return;
            c = new SharedExterior { Key = m.key, Area = m.area,
                Id = id, StockedDate = m.stockedDate, Image = m.image,
                Archive = m.archive, Record = m.record,
                WorldX = m.worldX, WorldZ = m.worldZ, Height = m.height,
                SourcePlayer = conn.identity.netId };
            serverExterior.Add(m.key, c);
            Debug.Log(Tag + "First generic exterior container " + m.key + " id=" + id);
        }
        if (c.Key != m.key)
            Debug.Log(Tag + "Reused existing exterior id=" + c.Id +
                " requested=" + m.key + " canonical=" + c.Key);
        var snapshot = BuildExteriorSnapshot(c);
        // Remember the local key that caused this request even if nearby
        // coordinates were merged into a different server-side canonical key.
        snapshot.requestedKey = m.key;
        if (snapshot.lootId != 0) conn.Send(snapshot);
        ExteriorInterest interest;
        if (serverExteriorInterests.TryGetValue(conn, out interest))
            interest.LastSent[c.Id] = Time.realtimeSinceStartup;
    }

    static bool ValidExteriorIdentity(string area, string key, int worldX, int worldZ, float height)
    {
        int x, y;
        if (!ParseExteriorArea(area, out x, out y) || string.IsNullOrEmpty(key) || key.Length >= 180 ||
            !key.StartsWith(area + "|", StringComparison.Ordinal) ||
            !Finite(height) || Mathf.Abs(height) > 200000 || worldX < 0 || worldZ < 0) return false;
        DaggerfallConnect.Utility.DFPosition tile = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
        return tile.X == x && tile.Y == y;
    }

    void ReceiveExteriorInterest(NetworkConnection conn, WcsExteriorInterest m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (conn == null || !conn.isReady || conn.identity == null ||
            m.areas == null || m.areas.Length > MaxExteriorAreas ||
            m.boundIds == null || m.boundIds.Length > MaxExteriorBoundIds) return;
        ExteriorInterest interest;
        if (!serverExteriorInterests.TryGetValue(conn, out interest))
            serverExteriorInterests[conn] = interest = new ExteriorInterest();
        interest.Areas.Clear();
        foreach (string area in m.areas)
        {
            int x, y;
            if (ParseExteriorArea(area, out x, out y))
            {
                interest.Areas.Add(area);
                NotePresence(conn, area);
            }
        }
        // Bound is rebuilt from this heartbeat. Previously we permanently marked
        // snapshots 'announced' at SEND time, even if the receiving client was
        // mid-fast-travel and discarded the snapshot before binding any object.
        var valid = new HashSet<uint>();
        foreach (var c in serverExterior.Values)
            if (interest.Areas.Contains(c.Area)) valid.Add(c.Id);
        interest.Bound.Clear();
        foreach (uint id in m.boundIds)
            if (valid.Contains(id)) interest.Bound.Add(id);
        foreach (uint id in new List<uint>(interest.LastSent.Keys))
            if (!valid.Contains(id)) interest.LastSent.Remove(id);
        int sent = 0;
        float now = Time.realtimeSinceStartup;
        foreach (var c in serverExterior.Values)
        {
            if (!valid.Contains(c.Id) || interest.Bound.Contains(c.Id)) continue;
            float last;
            if (interest.LastSent.TryGetValue(c.Id, out last) && now - last < ExteriorResendSeconds)
                continue;
            var snapshot = BuildExteriorSnapshot(c);
            if (snapshot.lootId == 0) continue;
            conn.Send(snapshot);
            interest.LastSent[c.Id] = now;
            if (++sent >= 64) break; // limit per-pulse catch-up traffic
        }
    }

    void ReceiveExteriorRejected(WcsExteriorRejected m)
    {
        if (!WorldContainerPolicy.Enabled || !Connected || !IsOutside() ||
            string.IsNullOrEmpty(m.key) || string.IsNullOrEmpty(m.area)) return;
        LocalExterior local;
        if (!localExterior.TryGetValue(m.key, out local) || local == null || local.Ready ||
            local.Area != m.area || local.Loot == null) return;
        var loot = local.Loot;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync != null && (sync.lootId != 0 || sync.EditPending)) return;
        localExterior.Remove(m.key);
        if (!rejectedExterior.ContainsKey(loot))
            rejectedExterior.Add(loot, loot.gameObject.activeSelf);
        loot.gameObject.SetActive(false);
        if (pendingActivationLoot == loot) ClearPendingActivation();
        Debug.Log(Tag + "Suppressed server-rejected exterior treasure " + m.key);
    }

    void ReceiveExteriorSnapshot(WcsExteriorSnapshot m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        try
        {
            if (!Connected || !IsOutside() || m.lootId == 0 || m.items == null ||
                m.items.Length > MaxItemsText ||
                !ValidExteriorIdentity(m.area, m.key, m.worldX, m.worldZ, m.height)) return;
            // Do not create a default prefab the instant an interested client
            // streams a terrain. Dynamic exterior mods often add the real loot
            // pile a few frames later; premature replicas produced double loot.
            if (TryApplyExteriorSnapshot(m, false))
            {
                pendingExterior.Remove(m.key);
                return;
            }
            PendingExterior pending;
            if (!pendingExterior.TryGetValue(m.key, out pending))
                pendingExterior[m.key] = new PendingExterior {
                    Snapshot = m, ReceivedAt = Time.realtimeSinceStartup };
            else pending.Snapshot = m;
        }
        catch (Exception ex)
        {
            // Mirror can disconnect a peer for an uncaught message-handler
            // exception. A missing mod asset must never do that.
            Debug.LogError(Tag + "Exterior snapshot skipped safely: " + ex);
        }
    }

    void ProcessPendingExterior()
    {
        foreach (var pair in new List<KeyValuePair<string, PendingExterior>>(pendingExterior))
        {
            var pending = pair.Value;
            // A delayed snapshot must not cause a pile to spawn in terrain
            // the player has already left. Native containers may outlive it.
            if (!IsOutside() ||
                (ActiveTerrainForArea(pending.Snapshot.area) == null &&
                 !HasExteriorNativeForSnapshot(pending.Snapshot)))
            {
                pendingExterior.Remove(pair.Key);
                continue;
            }
            if (Time.realtimeSinceStartup - pending.ReceivedAt > 30f)
            {
                Debug.LogWarning(Tag + "Exterior snapshot unresolved (ambiguous or unbindable): " + pair.Key);
                pendingExterior.Remove(pair.Key);
                continue;
            }
            bool allowReplica = Time.realtimeSinceStartup - pending.ReceivedAt >= ExteriorReplicaWait;
            try
            {
                if (TryApplyExteriorSnapshot(pending.Snapshot, allowReplica))
                    pendingExterior.Remove(pair.Key);
            }
            catch (Exception ex)
            {
                Debug.LogError(Tag + "Deferred exterior snapshot skipped safely: " + ex);
                pendingExterior.Remove(pair.Key);
            }
        }
    }

    bool HasExteriorNativeForSnapshot(WcsExteriorSnapshot m)
    {
        foreach (var c in localExterior.Values)
            if (c.Loot != null && c.Area == m.area && !c.Replica &&
                SameExteriorSpot(c.WorldX, c.WorldZ, c.Height,
                    m.worldX, m.worldZ, m.height)) return true;
        foreach (DaggerfallLoot native in ActiveGameObjectDatabase.GetActiveLoot())
        {
            if (!IsGenericExteriorLoot(native)) continue;
            string a, k; int x, z; float h;
            if (TryExteriorIdentity(native, out a, out k, out x, out z, out h) &&
                a == m.area && SameExteriorSpot(x, z, h, m.worldX, m.worldZ, m.height))
                return true;
        }
        return false;
    }

    bool TryApplyExteriorSnapshot(WcsExteriorSnapshot m, bool allowReplica)
    {
        LocalExterior local;
        localExterior.TryGetValue(m.key, out local);
        // On a direct reply, the server may have mapped our rounded key
        // to another player's canonical key. Locate that original request.
        if (local == null && !string.IsNullOrEmpty(m.requestedKey))
            localExterior.TryGetValue(m.requestedKey, out local);
        if (local == null)
        {
            bool ambiguous;
            local = FindLocalExteriorNear(m.area, m.worldX, m.worldZ, m.height,
                m.lootId, out ambiguous);
            if (ambiguous)
            {
                Debug.LogWarning(Tag + "Ambiguous local exterior treasure at " + m.key);
                return false;
            }
        }
        if (local != null && local.Ready)
        {
            if (local.Id != m.lootId)
                Debug.LogWarning(Tag + "Exterior identity conflict key=" + m.key +
                    " existingId=" + local.Id + " incomingId=" + m.lootId);
            // The server may answer an alternate rounded key after the
            // canonical pile is already bound. Hide its unbound duplicate.
            LocalExterior alternate;
            if (local.Id == m.lootId && !string.IsNullOrEmpty(m.requestedKey) &&
                m.requestedKey != m.key &&
                localExterior.TryGetValue(m.requestedKey, out alternate) &&
                alternate != local && !alternate.Ready && alternate.Loot != null)
            {
                SuppressDuplicateExterior(alternate.Loot, local.Id);
                if (suppressedExterior.ContainsKey(alternate.Loot))
                    localExterior.Remove(m.requestedKey);
            }
            return local.Id == m.lootId;
        }
        GameObject terrain = ActiveTerrainForArea(m.area);
        DaggerfallLoot target = local != null ? local.Loot : null;
        if (target == null)
        {
            // Use a mod's real native pile instead of spawning a generic
            // visual duplicate. Never hijack another network-bound pile.
            float bestDistance = float.MaxValue;
            bool ambiguous = false;
            foreach (DaggerfallLoot native in ActiveGameObjectDatabase.GetActiveLoot())
            {
                if (!IsGenericExteriorLoot(native)) continue;
                string area, key; int wx, wz; float h;
                if (!TryExteriorIdentity(native, out area, out key, out wx, out wz, out h) ||
                    area != m.area ||
                    !SameExteriorSpot(wx, wz, h, m.worldX, m.worldZ, m.height)) continue;
                var existingSync = native.GetComponent<LootMultiplayer>();
                if (existingSync != null && existingSync.lootId != 0 && existingSync.lootId != m.lootId)
                {
                    Debug.LogWarning(Tag + "Nearby native treasure already bound to another id; skipping duplicate " + m.key);
                    return false;
                }
                if (!IsGenericInitialized(native)) return false; // Do not overwrite a mod's not-yet-stocked pile.
                float dist = (wx - (float)m.worldX) * (wx - (float)m.worldX) +
                    (wz - (float)m.worldZ) * (wz - (float)m.worldZ);
                if (target != null && Mathf.Abs(dist - bestDistance) < 1f) ambiguous = true;
                if (dist < bestDistance) { target = native; bestDistance = dist; }
            }
            if (ambiguous)
            {
                Debug.LogWarning(Tag + "Two equally near native exterior piles at " + m.key + "; refusing unsafe match.");
                return false;
            }
        }
        bool replica = target == null;
        if (replica && (!allowReplica || terrain == null)) return false;
        if (replica)
        {
            try
            {
                target = CreateSafeTreasure(
                    ExteriorLocalPose(m.worldX, m.worldZ, m.height), terrain.transform,
                    m.archive, m.record, m.lootId, InventoryContainerImages.Chest, true);
                if (target == null) return false;
                target.gameObject.name = "MP_Exterior_Container_" + m.lootId;
                target.WorldContext = WorldContext.Exterior;
                target.gameObject.AddComponent<WorldContainerReplica>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(Tag + "Generic exterior replica failed: " + ex.Message);
                if (target != null) Destroy(target.gameObject);
                return false;
            }
        }
        if (!LootCatcher.BindWorldContainer(m.lootId, m.version, target, m.items))
        {
            if (replica) Destroy(target.gameObject);
            Debug.LogWarning(Tag + "Exterior bind failed key=" + m.key + " id=" + m.lootId);
            return false;
        }
        // Only change the local appearance AFTER the shared inventory binds.
        if (!replica) ApplyNativeTreasureAppearance(target, m.archive, m.record, m.lootId);
        if (local == null) local = new LocalExterior();
        if (!string.IsNullOrEmpty(local.Key) && local.Key != m.key)
            localExterior.Remove(local.Key);
        local.Area = m.area;
        local.Key = m.key;
        local.Loot = target;
        local.Id = m.lootId;
        local.Ready = true;
        local.Replica = replica;
        local.Depleted = m.depleted;
        local.WorldX = m.worldX;
        local.WorldZ = m.worldZ;
        local.Height = m.height;
        localExterior[m.key] = local;
        target.stockedDate = m.stockedDate;
        if (m.depleted) localDepleted.Add(m.lootId);
        if (replica) Debug.Log(Tag + "Replicated mod exterior treasure " + m.key + " id=" + m.lootId);
        else Debug.Log(Tag + "Bound native exterior treasure " + m.key + " id=" + m.lootId);
        CompletePendingActivation(target, m.area);
        ApplyLocalDepletion();
        return true;
    }

    // Pre-stock furniture during the first loaded MP interior frame, not when
    // somebody clicks it. This allows the usual heartbeat/network round-trip to
    // finish BEFORE interaction, making the first click normally immediate.
    // The local DFU rolling code and OnLootSpawned callbacks are unchanged.
    static void PrestockNativeHouseContainer(DaggerfallLoot loot)
    {
        if (loot == null || loot.ContainerType != LootContainerTypes.HouseContainers ||
            GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null)
            return;
        var ee = GameManager.Instance.PlayerEnterExit;
        var building = ee.BuildingDiscoveryData;
        bool owned = (ee.BuildingType == DFLocation.BuildingTypes.Ship && DaggerfallBankManager.OwnsShip) ||
                     DaggerfallBankManager.IsHouseOwned(building.buildingKey);
        // Player-owned house furniture normally does not receive random stock.
        if (owned || loot.stockedDate >= DaggerfallLoot.CreateStockedDate(DaggerfallUnity.Instance.WorldTime.Now))
            return;
        loot.StockHouseContainer(building);
        var player = GameManager.Instance.PlayerObject != null
            ? GameManager.Instance.PlayerObject.GetComponent<PlayerActivate>() : null;
        if (PlayerActivate.OnLootSpawned != null)
            PlayerActivate.OnLootSpawned(player, new ContainerLootSpawnedEventArgs {
                ContainerType = loot.ContainerType, Loot = loot.Items });
    }

    public static bool IsBound(DaggerfallLoot loot)
    {
        if (!WorldContainerPolicy.Enabled || !Connected || instance == null || loot == null) return false;
        foreach (var c in instance.localContainers.Values)
            if (c.Loot == loot && c.Ready) return true;
        foreach (var c in instance.localExterior.Values)
            if (c.Loot == loot && c.Ready) return true;
        return loot.GetComponent<WorldContainerReplica>() != null;
    }

    void ScanLocal()
    {
        string area = CurrentInteriorArea();
        if (pendingActivation != null && Time.realtimeSinceStartup - pendingActivationAt > 5f)
        {
            ClearPendingActivation();
            DaggerfallUI.AddHUDText("Loot synchronization timed out. Please try again.");
        }
        // Include inactive native containers in the CURRENT interior/dungeon root:
        // an empty local treasure pile may need to be re-enabled if the server's
        // first-loader snapshot contains items. We never scan other interiors.
        var list = new List<DaggerfallLoot>(ActiveGameObjectDatabase.GetActiveLoot());
        var ee = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
        Transform currentRoot = ee != null && ee.IsPlayerInsideDungeon && ee.Dungeon != null
            ? ee.Dungeon.transform : ee != null && ee.IsPlayerInsideBuilding && ee.Interior != null
            ? ee.Interior.transform : null;
        if (currentRoot != null)
            foreach (var loot in currentRoot.GetComponentsInChildren<DaggerfallLoot>(true))
                if (loot != null && !list.Contains(loot)) list.Add(loot);
        foreach (var loot in list)
        {
            if (!IsNativeEligible(loot) || loot.GetComponent<WorldContainerReplica>() != null) continue;
            Transform camp = FindCampRoot(loot);
            if (camp != null && loot.ContainerType == LootContainerTypes.RandomTreasure)
            {
                string campKey = CampArea(camp);
                if (campKey != null) DiscoverCamp(campKey, camp);
                continue;
            }
            if (IsGenericExteriorLoot(loot))
            {
                RegisterExterior(loot);
                continue;
            }
            if (area == null || !IsInCurrentNativeRoot(loot))
                continue;
            // Once a local container has registered, NEVER re-roll its contents;
            // especially important if different parties have different time bubbles.
            string key = NativeKey(area, loot);
            LocalContainer known;
            if (key != null && localContainers.TryGetValue(key, out known) && known.Loot == loot)
                continue;
            // Formerly unstocked furniture was skipped until clicked, forcing a
            // visible first-click delay. Pre-stock only on a connected client and
            // only inside the player's current MP interior; SP is never touched.
            if (loot.ContainerType == LootContainerTypes.HouseContainers)
                PrestockNativeHouseContainer(loot);
            RegisterNative(loot, area);
        }
        // Optional slower pass also finds camps with ZERO local loot objects.
        // Without it, a client whose WoD randomly generated no containers could
        // never become the first loader or receive another player's containers.
        if (Time.realtimeSinceStartup >= nextEmptyCampDiscovery)
        {
            nextEmptyCampDiscovery = Time.realtimeSinceStartup + EmptyCampDiscoverySeconds;
            DiscoverEmptyCampRoots();
        }
        foreach (var pair in new List<KeyValuePair<string, LocalCamp>>(localCamps))
        {
            var camp = pair.Value;
            if (camp.Root == null || !camp.Root.gameObject.activeInHierarchy)
            {
                RestoreCamp(camp);
                localCamps.Remove(pair.Key);
                continue;
            }
            if (camp.Ready)
            {
                // Location Loader can re-enable children. They must never become
                // a second interactive set beside the replicated authoritative copies.
                foreach (var original in camp.Originals)
                    if (original != null && original.gameObject.activeSelf) original.gameObject.SetActive(false);
            }
            else if (Time.realtimeSinceStartup - camp.DiscoveredAt >= CampSettleSeconds &&
                     Time.realtimeSinceStartup - camp.LastRequest >= CampRetrySeconds)
                RequestCamp(camp);
        }
        foreach (var pair in new List<KeyValuePair<string, LocalContainer>>(localContainers))
            if (pair.Value.Loot == null || pair.Value.Area != area)
            {
                var sync = pair.Value.Loot != null ? pair.Value.Loot.GetComponent<LootMultiplayer>() : null;
                if (sync != null && sync.EditPending) continue;
                LootCatcher.UnbindWorldContainer(pair.Value.NetworkId, pair.Value.Loot);
                localContainers.Remove(pair.Key);
            }
            else if (!pair.Value.Ready && Time.realtimeSinceStartup - pair.Value.LastRequest >= 2f)
                SendNativeRequest(pair.Value);
        CleanupExterior();
        ProcessPendingExterior();
        ApplyLocalDepletion();
    }

    // Unrecognized DFU-style treasure piles. Non-WoD dynamic camps may appear
    // under any mod hierarchy, so match by stable global coordinates rather than parent names.
    void RegisterExterior(DaggerfallLoot loot)
    {
        if (!IsGenericExteriorLoot(loot) || rejectedExterior.ContainsKey(loot)) return;
        var preExistingSync = loot.GetComponent<LootMultiplayer>();
        if (preExistingSync != null && preExistingSync.lootId != 0 &&
            FindLocalExteriorByObject(loot) == null) return;
        // A moving/floating origin can nudge a rounded world key across
        // grid boundaries. Never assign a second ID to the same GameObject.
        LocalExterior already = FindLocalExteriorByObject(loot);
        if (already != null)
        {
            if (!already.Ready && Time.realtimeSinceStartup - already.LastRequest > 3f)
                SendExteriorRequest(already);
            return;
        }
        string area, key;
        int wx, wz; float height;
        if (!TryExteriorIdentity(loot, out area, out key, out wx, out wz, out height) ||
            key.Length >= 180) return;
        // A native object may spawn after the server's replica, or an exterior
        // mod may regenerate an object after streaming/fast-travel. Only one
        // interactive pile can exist at a shared physical treasure position.
        bool ambiguous;
        LocalExterior nearby = FindLocalExteriorNear(area, wx, wz, height, 0, out ambiguous);
        if (ambiguous)
        {
            Debug.LogWarning(Tag + "Ambiguous local exterior spawn near " + key);
            return;
        }
        // Exact-key collisions must not silently replace a different local
        // object when its height makes proximity matching fail.
        LocalExterior exact;
        if (nearby == null && localExterior.TryGetValue(key, out exact))
        {
            Debug.LogWarning(Tag + "Ambiguous exterior key already in use: " + key);
            return;
        }
        if (nearby != null)
        {
            // Do not hide a mod's uninitialized treasure before its own
            // stock-generation callback has had a chance to populate it.
            if (!IsGenericInitialized(loot)) return;
            if (nearby.Replica && nearby.Ready && TryReplaceExteriorReplica(nearby, loot))
                return;
            SuppressDuplicateExterior(loot, nearby.Id);
            return;
        }
        float seen;
        if (!exteriorFirstSeen.TryGetValue(loot, out seen))
        { exteriorFirstSeen[loot] = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - seen < ExteriorInitializeWait || !IsGenericInitialized(loot)) return;
        var c = new LocalExterior { Area = area, Key = key, Loot = loot,
            WorldX = wx, WorldZ = wz, Height = height };
        localExterior[key] = c;
        SendExteriorRequest(c);
    }

    // Prefer the mod's actual object when it appears after a temporary replica.
    // Transfer the CURRENT bound version/items, never the original registration
    // snapshot, and do not swap while either inventory is being edited/open.
    bool TryReplaceExteriorReplica(LocalExterior c, DaggerfallLoot native)
    {
        if (c == null || !c.Ready || !c.Replica || c.Loot == null || native == null ||
            !IsGenericInitialized(native) || IsInventoryOpenFor(c.Loot) ||
            IsInventoryOpenFor(native)) return false;
        var old = c.Loot;
        var oldSync = old.GetComponent<LootMultiplayer>();
        if (oldSync == null || oldSync.EditPending) return false;
        var nativeSync = native.GetComponent<LootMultiplayer>();
        if (nativeSync != null && nativeSync.lootId != 0) return false;
        int version = oldSync.lootVersion;
        string items = LootCatcher.SerializeLootItems(old);
        LootCatcher.UnbindWorldContainer(c.Id, old);
        if (!LootCatcher.BindWorldContainer(c.Id, version, native, items))
        {
            // Restore the old replica if binding the native object fails.
            LootCatcher.BindWorldContainer(c.Id, version, old, items);
            Debug.LogWarning(Tag + "Could not promote native exterior object for id=" + c.Id);
            return false;
        }
        ApplyNativeTreasureAppearance(native, old.TextureArchive, old.TextureRecord, c.Id);
        c.Loot = native;
        c.Replica = false;
        Destroy(old.gameObject);
        if (c.Depleted || localDepleted.Contains(c.Id)) native.gameObject.SetActive(false);
        Debug.Log(Tag + "Replaced temporary exterior replica with mod native id=" + c.Id);
        return true;
    }

    void SendExteriorRequest(LocalExterior c)
    {
        if (!Connected || c.Loot == null || !IsGenericInitialized(c.Loot) || c.Ready) return;
        // The frozen initial identity is intentional. Recomputing its rounded
        // coordinates while walking can give one physical pile several IDs.
        string contents = LootCatcher.SerializeLootItems(c.Loot);
        if (contents == null || contents.Length > MaxItemsText) return;
        c.LastRequest = Time.realtimeSinceStartup;
        NetworkClient.Send(new WcsExteriorRequest { area = c.Area, key = c.Key, items = contents,
            stockedDate = c.Loot.stockedDate, image = (int)c.Loot.ContainerImage,
            archive = c.Loot.TextureArchive, record = c.Loot.TextureRecord,
            worldX = c.WorldX, worldZ = c.WorldZ, height = c.Height });
    }

    void UnbindExterior(LocalExterior c)
    {
        if (c == null || c.Loot == null) return;
        LootCatcher.UnbindWorldContainer(c.Id, c.Loot);
        if (c.Replica) Destroy(c.Loot.gameObject);
    }

    void CleanupExterior()
    {
        foreach (var pair in new List<KeyValuePair<string, LocalExterior>>(localExterior))
        {
            var c = pair.Value;
            bool sourceGone = c.Loot == null || (c.Replica
                ? ActiveTerrainForArea(c.Area) == null
                : c.Loot.transform.parent != null &&
                  !c.Loot.transform.parent.gameObject.activeInHierarchy);
            if (!IsOutside() || sourceGone)
            {
                var sync = c.Loot != null ? c.Loot.GetComponent<LootMultiplayer>() : null;
                if (sync != null && sync.EditPending) continue;
                UnbindExterior(c);
                localExterior.Remove(pair.Key);
            }
        }
        foreach (var pair in new List<KeyValuePair<DaggerfallLoot, bool>>(suppressedExterior))
        {
            if (pair.Key == null) { suppressedExterior.Remove(pair.Key); continue; }
            bool stillCovered = false;
            string area, key; int wx, wz; float h;
            if (IsOutside() && TryExteriorIdentity(pair.Key, out area, out key, out wx, out wz, out h))
                foreach (var c in localExterior.Values)
                    if (c.Loot != null && c.Area == area &&
                        SameExteriorSpot(c.WorldX, c.WorldZ, c.Height, wx, wz, h))
                    { stillCovered = true; break; }
            if (!stillCovered)
            {
                pair.Key.gameObject.SetActive(pair.Value);
                suppressedExterior.Remove(pair.Key);
            }
        }
        foreach (var pair in new List<KeyValuePair<DaggerfallLoot, bool>>(rejectedExterior))
            if (pair.Key == null) rejectedExterior.Remove(pair.Key);
        foreach (var pair in new List<KeyValuePair<DaggerfallLoot, float>>(exteriorFirstSeen))
            if (pair.Key == null || !IsOutside() || !pair.Key.gameObject.activeInHierarchy &&
                !suppressedExterior.ContainsKey(pair.Key)) exteriorFirstSeen.Remove(pair.Key);
    }

    void RegisterNative(DaggerfallLoot loot, string area)
    {
        if (loot == null || !IsNativeEligible(loot)) return;
        string key = NativeKey(area, loot);
        if (key == null || key.Length > 180) return;
        LocalContainer existing;
        if (localContainers.TryGetValue(key, out existing))
        {
            if (existing.Loot == loot) return;
            if (existing.Loot != null && existing.Loot.gameObject.activeInHierarchy)
            {
                Debug.LogWarning(Tag + "Ambiguous LoadID/key for two live containers: " + key);
                return;
            }
        }
        var c = new LocalContainer { Loot = loot, Area = area, Key = key };
        localContainers[key] = c;
        SendNativeRequest(c);
    }

    void SendNativeRequest(LocalContainer c)
    {
        if (!Connected || c.Loot == null) return;
        string items = LootCatcher.SerializeLootItems(c.Loot);
        if (items.Length > MaxItemsText) return;
        c.LastRequest = Time.realtimeSinceStartup;
        NetworkClient.Send(new WcsContainerRequest { area = c.Area, key = c.Key,
            items = items, stockedDate = c.Loot.stockedDate,
            image = (int)c.Loot.ContainerImage, archive = c.Loot.TextureArchive,
            record = c.Loot.TextureRecord });
    }

    // Location Loader does not expose an ordinary DaggerfallLoot hook for a
    // camp whose random configuration contains zero treasure. This infrequent,
    // exterior-only pass recognizes camp prefab ROOTS (never arbitrary terrain
    // containers) so an empty camp can legitimately win the first-loader race.
    void DiscoverEmptyCampRoots()
    {
        var game = GameManager.Instance;
        if (game == null || game.PlayerEnterExit == null ||
            game.PlayerEnterExit.IsPlayerInside || game.PlayerGPS == null ||
            game.PlayerObject == null) return;
        foreach (Transform t in FindObjectsOfType<Transform>())
        {
            if (t == null || !t.gameObject.activeInHierarchy ||
                t.name.IndexOf("WOD_", StringComparison.OrdinalIgnoreCase) < 0 ||
                t.name.IndexOf("Camp", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            // Avoid nested camp roots duplicating an outer camp object.
            Transform root = t;
            for (Transform parent = t.parent; parent != null; parent = parent.parent)
                if (parent.name.IndexOf("WOD_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    parent.name.IndexOf("Camp", StringComparison.OrdinalIgnoreCase) >= 0)
                { root = null; break; }
            if (root == null) continue;
            string area = CampArea(root);
            if (!string.IsNullOrEmpty(area)) DiscoverCamp(area, root);
        }
    }

    void DiscoverCamp(string area, Transform root)
    {
        LocalCamp camp;
        if (!localCamps.TryGetValue(area, out camp))
        {
            camp = new LocalCamp { Area = area, Root = root, DiscoveredAt = Time.realtimeSinceStartup };
            localCamps[area] = camp;
        }
        if (camp.Root != root) return; // ambiguous overlapping WOD roots: fail closed
        foreach (var loot in root.GetComponentsInChildren<DaggerfallLoot>(true))
        {
            if (loot == null || loot.ContainerType != LootContainerTypes.RandomTreasure || loot.GetComponent<WorldContainerReplica>() != null ||
                camp.Originals.Contains(loot)) continue;
            camp.Originals.Add(loot);
            camp.InitiallyActive[loot] = loot.gameObject.activeSelf;
            if (camp.Ready) loot.gameObject.SetActive(false);
        }
    }

    // World of Daggerfall uses Location Loader. It creates the scene loot with
    // an EMPTY ItemCollection, and only populates it when the location activates.
    // Never register this temporary empty state as authoritative camp loot.
    // Use reflection so WoD/Location Loader DLLs are NOT assembly dependencies.
    static bool TryReadLocationLoaderBool(Component component, string name, out bool value)
    {
        value = false;
        if (component == null) return false;
        var t = component.GetType();
        var property = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property != null && property.PropertyType == typeof(bool))
        {
            try { value = (bool)property.GetValue(component, null); return true; }
            catch (Exception) { return false; }
        }
        var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field != null && field.FieldType == typeof(bool))
        {
            try { value = (bool)field.GetValue(component); return true; }
            catch (Exception) { return false; }
        }
        return false;
    }

    // Resolve by simple class name without referencing the mod assembly.
    // GetComponents is also reliable for namespaced Location Loader components.
    static Component FindModComponent(GameObject go, string shortTypeName)
    {
        if (go == null) return null;
        foreach (var component in go.GetComponents<Component>())
            if (component != null && component.GetType().Name == shortTypeName)
                return component;
        return null;
    }

    static bool CampObjectsSpawned(LocalCamp camp)
    {
        if (camp == null || camp.Root == null) return false;
        var data = FindModComponent(camp.Root.gameObject, "LocationData");
        bool spawned;
        if (data != null)
            return TryReadLocationLoaderBool(data, "HasSpawnedDynamicObjects", out spawned) && spawned;
        // When the exact Location Loader metadata is unavailable, permit already
        // generated visible treasure but NEVER register a zero-object manifest.
        return camp.Originals.Count > 0;
    }

    static bool CampLootInitialized(LocalCamp camp)
    {
        if (!CampObjectsSpawned(camp)) return false;
        foreach (var loot in camp.Originals)
        {
            if (loot == null) continue;
            var serializer = FindModComponent(loot.gameObject, "LocationLootSerializer");
            if (serializer != null)
            {
                bool activated;
                if (!TryReadLocationLoaderBool(serializer, "Activated", out activated) || !activated)
                    return false;
            }
            else if (loot.stockedDate == 0 && loot.Items.Count == 0)
            {
                // An unrecognised mod may still be about to fill this empty marker.
                return false;
            }
        }
        return true;
    }

    void RequestCamp(LocalCamp camp)
    {
        if (!Connected || camp.Root == null) return;
        if (!CampLootInitialized(camp))
        {
            camp.LastRequest = Time.realtimeSinceStartup;
            return; // Keep local treasure alive until the mod finishes populating it.
        }
        if (camp.InitialManifest == null)
        {
            var defs = new List<WcsCampContainer>();
            int textLength = 0;
            foreach (var loot in camp.Originals)
            {
                if (!IsNativeEligible(loot) || loot.ContainerType != LootContainerTypes.RandomTreasure)
                    continue;
                if (defs.Count >= MaxCampContainers)
                {
                    Debug.LogWarning(Tag + "Too many camp containers; leaving local originals unchanged: " + camp.Area);
                    return;
                }
                if (!loot.gameObject.activeInHierarchy) continue;
                Vector3 rel = loot.transform.position - camp.Root.position;
                string contents = LootCatcher.SerializeLootItems(loot);
                if (contents == null || contents.Length > MaxItemsText || rel.sqrMagnitude > 250000f)
                    return;
                textLength += contents.Length;
                if (textLength > MaxCampText) return;
                defs.Add(new WcsCampContainer { type = (int)loot.ContainerType,
                    image = (int)loot.ContainerImage, archive = loot.TextureArchive,
                    record = loot.TextureRecord, x = rel.x, y = rel.y, z = rel.z,
                    items = contents });
            }
            // Empty layout is meaningful: first loader with zero treasure means
            // later clients must suppress any conflicting locally generated pile.
            camp.InitialManifest = defs.ToArray();
        }
        camp.LastRequest = Time.realtimeSinceStartup;
        foreach (var loot in camp.Originals)
            if (loot != null) loot.gameObject.SetActive(false);
        NetworkClient.Send(new WcsCampRequest {
            area = camp.Area, containers = camp.InitialManifest
        });
    }

    void ReceiveContainerRequest(NetworkConnection conn, WcsContainerRequest m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (!ValidNativeRequest(conn, m)) return;
        NotePresence(conn, m.area);
        SharedContainer c;
        if (!serverContainers.TryGetValue(m.key, out c))
        {
            uint id = LootCatcher.ServerRegisterWorldContainer(m.items);
            if (id == 0) return;
            c = new SharedContainer { Area = m.area, Id = id, StockedDate = m.stockedDate,
                IsTreasure = m.key.Contains("|" + ((int)LootContainerTypes.RandomTreasure) + "|"),
                Image = m.image, Archive = m.archive, Record = m.record };
            serverContainers.Add(m.key, c);
            Debug.Log(Tag + "Server accepted first native container area=" + m.area +
                " key=" + m.key + " id=" + id);
        }
        int version;
        string items;
        if (LootCatcher.ServerReadWorldContainer(c.Id, out version, out items))
            conn.Send(new WcsContainerReply { area = m.area, key = m.key,
                lootId = c.Id, version = version, items = items, stockedDate = c.StockedDate,
                depleted = c.Depleted, image = c.Image, archive = c.Archive, record = c.Record });
    }

    static bool ValidNativeRequest(NetworkConnection conn, WcsContainerRequest m)
    {
        return conn != null && conn.isReady && conn.identity != null &&
            !string.IsNullOrEmpty(m.area) && m.area.Length < 100 &&
            !string.IsNullOrEmpty(m.key) && m.key.Length < 180 &&
            m.key.StartsWith(m.area + "|", StringComparison.Ordinal) &&
            (m.area.StartsWith("d:", StringComparison.Ordinal) || m.area.StartsWith("i:", StringComparison.Ordinal)) &&
            m.items != null && m.items.Length <= MaxItemsText;
    }

    void ReceiveContainerReply(WcsContainerReply m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (!Connected || m.items == null || m.items.Length > MaxItemsText || m.lootId == 0) return;
        LocalContainer c;
        if (!localContainers.TryGetValue(m.key, out c) || c.Area != m.area || c.Loot == null ||
            CurrentInteriorArea() != m.area || !IsInCurrentNativeRoot(c.Loot)) return;
        if (LootCatcher.BindWorldContainer(m.lootId, m.version, c.Loot, m.items))
        {
            c.Ready = true;
            c.NetworkId = m.lootId;
            c.Loot.stockedDate = m.stockedDate;
            c.Depleted = m.depleted;
            if (m.depleted) localDepleted.Add(m.lootId);
            if (c.Loot.ContainerType == LootContainerTypes.RandomTreasure &&
                c.Area.StartsWith("d:", StringComparison.Ordinal))
                ApplyNativeTreasureAppearance(c.Loot, m.archive, m.record, m.lootId);
            Debug.Log(Tag + "Bound native container area=" + m.area +
                " id=" + m.lootId + " version=" + m.version);
            // Synchronize the physical visibility of empty/filled treasure piles.
            // Furniture must remain present even if its inventory is empty.
            if (c.Loot.ContainerType == LootContainerTypes.RandomTreasure)
            {
                // A truly depleted pile remains gone even if someone reconnects.
                // Initial empty snapshots are also hidden, matching vanilla DFU.
                if (c.Depleted || string.IsNullOrEmpty(m.items))
                    ApplyLocalDepletion();
                if (!c.Depleted && string.IsNullOrEmpty(m.items) && !IsInventoryOpenFor(c.Loot))
                    c.Loot.gameObject.SetActive(false);
            }
            CompletePendingActivation(c.Loot, c.Area);
        }
    }

    void ReceiveCampRequest(NetworkConnection conn, WcsCampRequest m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (conn == null || !conn.isReady || conn.identity == null || string.IsNullOrEmpty(m.area) ||
            m.area.Length > 96 || !m.area.StartsWith("w:", StringComparison.Ordinal) ||
            m.containers == null || m.containers.Length > MaxCampContainers) return;
        NotePresence(conn, m.area);
        SharedCamp camp;
        if (!serverCamps.TryGetValue(m.area, out camp))
        {
            var defs = new WcsCampContainer[m.containers.Length];
            int totalText = 0;
            for (int i = 0; i < defs.Length; i++)
            {
                var d = m.containers[i];
                totalText += d.items == null ? 0 : d.items.Length;
                if (totalText > MaxCampText ||
                    d.type != (int)LootContainerTypes.RandomTreasure || d.items == null ||
                    d.items.Length > MaxItemsText || !Finite(d.x) || !Finite(d.y) || !Finite(d.z) ||
                    Mathf.Abs(d.x) > 500 || Mathf.Abs(d.y) > 500 || Mathf.Abs(d.z) > 500 ||
                    d.archive < 0 || d.record < 0) return;
                d.lootId = LootCatcher.ServerRegisterWorldContainer(d.items);
                if (d.lootId == 0)
                {
                    for (int j = 0; j < i; j++) LootCatcher.ServerForgetWorldContainer(defs[j].lootId);
                    return;
                }
                d.version = 0;
                d.depleted = false; // Only server-side transactions can deplete a pile.
                defs[i] = d;
            }
            camp = new SharedCamp { Area = m.area, Containers = defs };
            serverCamps.Add(m.area, camp);
            Debug.Log(Tag + "Server accepted first WoD camp area=" + m.area +
                " containers=" + defs.Length);
        }
        // Always return the current inventory, not the original first-loader list.
        var answer = new WcsCampContainer[camp.Containers.Length];
        for (int i = 0; i < answer.Length; i++)
        {
            answer[i] = camp.Containers[i];
            int ver; string items;
            if (LootCatcher.ServerReadWorldContainer(answer[i].lootId, out ver, out items))
            { answer[i].version = ver; answer[i].items = items; }
        }
        conn.Send(new WcsCampReply { area = camp.Area, containers = answer });
    }

    static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

    void ReceiveCampReply(WcsCampReply m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (!Connected || m.containers == null || m.containers.Length > MaxCampContainers) return;
        LocalCamp camp;
        if (!localCamps.TryGetValue(m.area, out camp) || camp.Root == null ||
            !camp.Root.gameObject.activeInHierarchy || camp.Ready) return;
        foreach (var native in camp.Originals)
            if (native != null) native.gameObject.SetActive(false);
        foreach (var d in m.containers)
        {
            if (d.type != (int)LootContainerTypes.RandomTreasure || d.lootId == 0 ||
                d.items == null || d.items.Length > MaxItemsText || !Finite(d.x) || !Finite(d.y) || !Finite(d.z))
                continue;
            try
            {
                Vector3 pose = camp.Root.position + new Vector3(d.x, d.y, d.z);
                DaggerfallLoot loot = CreateSafeTreasure(
                    pose, camp.Root, d.archive, d.record, d.lootId,
                    (InventoryContainerImages)d.image, false);
                if (loot == null) continue;
                loot.gameObject.name = "MP_WOD_Container_" + d.lootId;
                loot.gameObject.AddComponent<WorldContainerReplica>();
                if (!LootCatcher.BindWorldContainer(d.lootId, d.version, loot, d.items))
                {
                    Destroy(loot.gameObject);
                    continue;
                }
                camp.Replicas.Add(loot);
                if (d.depleted) localDepleted.Add(d.lootId);
            }
            catch (Exception e) { Debug.LogWarning(Tag + "Camp container clone failed: " + e.Message); }
        }
        camp.Ready = camp.Replicas.Count == m.containers.Length;
        if (camp.Ready)
        {
            ApplyLocalDepletion();
            Debug.Log(Tag + "Camp synchronized area=" + m.area +
                " containers=" + camp.Replicas.Count);
        }
        if (!camp.Ready)
        {
            // Never allow partially replicated layouts or two copies of a container.
            foreach (var clone in camp.Replicas)
            {
                if (clone == null) continue;
                var sync = clone.GetComponent<LootMultiplayer>();
                if (sync != null)
                {
                    sync.SuppressRemovalNotification();
                    LootCatcher.UnbindWorldContainer(sync.lootId, clone);
                }
                Destroy(clone.gameObject);
            }
            camp.Replicas.Clear();
            Debug.LogWarning(Tag + "Incomplete camp replica, will retry; local originals remain suppressed: " + m.area);
        }
    }

    void RestoreCamp(LocalCamp c)
    {
        if (c == null) return;
        foreach (var clone in c.Replicas)
        {
            if (clone == null) continue;
            var sync = clone.GetComponent<LootMultiplayer>();
            if (sync != null)
            {
                sync.SuppressRemovalNotification();
                LootCatcher.UnbindWorldContainer(sync.lootId, clone);
            }
            Destroy(clone.gameObject);
        }
        foreach (var native in c.Originals)
        {
            bool wasActive;
            if (native != null && native.transform.parent != null &&
                c.InitiallyActive.TryGetValue(native, out wasActive))
                native.gameObject.SetActive(wasActive);
        }
        c.Replicas.Clear();
    }

    // Server-only reader bookkeeping. An authenticated player can only open a
    // known session-world treasure ID; cannot affect corpses or dropped items.
    static bool TryGetSharedTreasureArea(uint id, out string area)
    {
        foreach (var c in serverContainers.Values)
            if (c.Id == id && !c.Depleted && c.Area != null && c.IsTreasure)
            { area = c.Area; return true; }
        foreach (var camp in serverCamps.Values)
            foreach (var d in camp.Containers)
                if (d.lootId == id && !d.depleted)
                { area = camp.Area; return true; }
        foreach (var c in serverExterior.Values)
            if (c.Id == id && !c.Depleted)
            { area = c.Area; return true; }
        area = null;
        return false;
    }

    void ReceiveReader(NetworkConnection conn, WcsReader m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (conn == null || !conn.isReady || conn.identity == null || m.lootId == 0) return;
        string area;
        if (!TryGetSharedTreasureArea(m.lootId, out area))
        {
            // Late close after depletion is fine, but never create a reader on an unknown id.
            if (!m.open) RemoveReader(conn, m.lootId);
            return;
        }
        // Require this player's heartbeat to include the corresponding area.
        Dictionary<string, float> presence;
        float stamp;
        if (m.open && (!serverPresence.TryGetValue(conn, out presence) ||
            !presence.TryGetValue(area, out stamp) ||
            Time.realtimeSinceStartup - stamp > PresenceExpirySeconds)) return;
        if (m.open)
        {
            HashSet<NetworkConnection> readers;
            if (!serverReaders.TryGetValue(m.lootId, out readers))
                serverReaders[m.lootId] = readers = new HashSet<NetworkConnection>();
            readers.Add(conn);
        }
        else RemoveReader(conn, m.lootId);
    }

    static void RemoveReader(NetworkConnection conn, uint id)
    {
        HashSet<NetworkConnection> readers;
        if (!serverReaders.TryGetValue(id, out readers)) return;
        readers.Remove(conn);
        if (readers.Count == 0) serverReaders.Remove(id);
    }

    static bool HasLiveReaders(uint id)
    {
        HashSet<NetworkConnection> readers;
        if (!serverReaders.TryGetValue(id, out readers)) return false;
        readers.RemoveWhere(conn => conn == null || !conn.isReady);
        if (readers.Count == 0) { serverReaders.Remove(id); return false; }
        return true;
    }

    static bool ServerIsDepleted(uint id)
    {
        if (HasLiveReaders(id)) return false;
        int version;
        string items;
        // Version 0 can be an original empty roll; only a COMMITTED edit can
        // exhaust a shared treasure inventory. Do not mistake uninitialized LL loot.
        return LootCatcher.ServerReadWorldContainer(id, out version, out items) &&
            version > 0 && string.IsNullOrEmpty(items);
    }

    void CheckDepletedOnServer()
    {
        foreach (var c in serverContainers.Values)
        {
            if (c.Depleted || !c.IsTreasure || !ServerIsDepleted(c.Id)) continue;
            c.Depleted = true;
            NetworkServer.SendToReady(new WcsDepleted { area = c.Area, lootId = c.Id });
            Debug.Log(Tag + "Native treasure depleted id=" + c.Id + " area=" + c.Area);
        }
        foreach (var exterior in serverExterior.Values)
        {
            if (exterior.Depleted || !ServerIsDepleted(exterior.Id)) continue;
            exterior.Depleted = true;
            NetworkServer.SendToReady(new WcsDepleted { area = exterior.Area, lootId = exterior.Id });
            Debug.Log(Tag + "Exterior treasure depleted id=" + exterior.Id + " area=" + exterior.Area);
        }
        foreach (var camp in serverCamps.Values)
            for (int i = 0; i < camp.Containers.Length; i++)
            {
                var d = camp.Containers[i];
                if (d.depleted || !ServerIsDepleted(d.lootId)) continue;
                d.depleted = true;
                camp.Containers[i] = d;
                NetworkServer.SendToReady(new WcsDepleted { area = camp.Area, lootId = d.lootId });
                Debug.Log(Tag + "WoD treasure depleted id=" + d.lootId + " area=" + camp.Area);
            }
    }

    void ReceiveDepleted(WcsDepleted m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (!Connected || m.lootId == 0 || string.IsNullOrEmpty(m.area)) return;
        bool found = false;
        foreach (var c in localContainers.Values)
            if (c.Ready && c.Area == m.area && c.NetworkId == m.lootId &&
                c.Loot != null && c.Loot.ContainerType == LootContainerTypes.RandomTreasure)
            { c.Depleted = true; found = true; }
        foreach (var c in localExterior.Values)
            if (c.Ready && c.Area == m.area && c.Id == m.lootId)
            { c.Depleted = true; found = true; }
        LocalCamp camp;
        if (localCamps.TryGetValue(m.area, out camp))
            foreach (var clone in camp.Replicas)
                if (clone != null && SharedTreasureId(clone) == m.lootId)
                { found = true; break; }
        if (!found) return;
        localDepleted.Add(m.lootId);
        ApplyLocalDepletion();
    }

    void SendPresence()
    {
        // Defence in depth: never send an authenticated Mirror message during
        // the initial local-host authentication handshake or a reconnect.
        if (!Connected) return;
        var scopes = new List<string>();
        string interior = CurrentInteriorArea();
        if (!string.IsNullOrEmpty(interior)) scopes.Add(interior);
        foreach (var camp in localCamps.Values)
            if (camp.Root != null && camp.Root.gameObject.activeInHierarchy)
                scopes.Add(camp.Area);
        NetworkClient.Send(new WcsPresence { areas = scopes.ToArray() });
        SendExteriorInterest();
    }

    void ReceivePresence(NetworkConnection conn, WcsPresence m)
    {
        if (!WorldContainerPolicy.Enabled) return;
        if (conn == null || !conn.isReady || conn.identity == null || m.areas == null || m.areas.Length > 64) return;
        // An entire fresh heartbeat, not incremental area adds.
        var seen = new Dictionary<string, float>();
        foreach (string area in m.areas)
            if (!string.IsNullOrEmpty(area) && area.Length < 100 &&
                (area.StartsWith("d:", StringComparison.Ordinal) ||
                 area.StartsWith("i:", StringComparison.Ordinal) ||
                 area.StartsWith("w:", StringComparison.Ordinal)))
                seen[area] = Time.realtimeSinceStartup;
        serverPresence[conn] = seen;
    }

    void NotePresence(NetworkConnection conn, string area)
    {
        Dictionary<string, float> seen;
        if (!serverPresence.TryGetValue(conn, out seen))
            serverPresence[conn] = seen = new Dictionary<string, float>();
        seen[area] = Time.realtimeSinceStartup;
    }

    void ExpireUnusedAreas()
    {
        float now = Time.realtimeSinceStartup;
        var areas = new HashSet<string>();
        foreach (var c in serverContainers.Values) areas.Add(c.Area);
        foreach (var c in serverCamps.Values) areas.Add(c.Area);
        foreach (var c in serverExterior.Values) areas.Add(c.Area);
        foreach (var area in areas)
        {
            bool occupied = false;
            foreach (var pair in serverPresence)
            {
                if (pair.Key == null || !pair.Key.isReady) continue;
                float stamp;
                if (pair.Value.TryGetValue(area, out stamp) && now - stamp < PresenceExpirySeconds)
                { occupied = true; break; }
            }
            if (occupied) { emptySince.Remove(area); continue; }
            float at;
            if (!emptySince.TryGetValue(area, out at)) { emptySince[area] = now; continue; }
            if (now - at < StateGraceSeconds) continue;
            foreach (var kv in new List<KeyValuePair<string, SharedContainer>>(serverContainers))
                if (kv.Value.Area == area)
                { serverReaders.Remove(kv.Value.Id); LootCatcher.ServerForgetWorldContainer(kv.Value.Id); serverContainers.Remove(kv.Key); }
            SharedCamp camp;
            if (serverCamps.TryGetValue(area, out camp))
            {
                foreach (var d in camp.Containers)
                { serverReaders.Remove(d.lootId); LootCatcher.ServerForgetWorldContainer(d.lootId); }
                serverCamps.Remove(area);
            }
            foreach (var kv in new List<KeyValuePair<string, SharedExterior>>(serverExterior))
                if (kv.Value.Area == area)
                {
                    serverReaders.Remove(kv.Value.Id);
                    LootCatcher.ServerForgetWorldContainer(kv.Value.Id);
                    serverExterior.Remove(kv.Key);
                    foreach (var interest in serverExteriorInterests.Values)
                        { interest.Bound.Remove(kv.Value.Id); interest.LastSent.Remove(kv.Value.Id); }
                }
            Debug.Log(Tag + "Unloaded shared container area=" + area);
            emptySince.Remove(area);
        }
        foreach (var pair in new List<KeyValuePair<NetworkConnection, Dictionary<string, float>>>(serverPresence))
            if (pair.Key == null || !pair.Key.isReady) serverPresence.Remove(pair.Key);
        foreach (var pair in new List<KeyValuePair<NetworkConnection, ExteriorInterest>>(serverExteriorInterests))
            if (pair.Key == null || !pair.Key.isReady) serverExteriorInterests.Remove(pair.Key);
    }
}

// Host/dedicated-server policy only; no corpse/quest/dropped-loot settings live here.
// A remote client MUST wait for the authenticated server reply before scanning.
public static class WorldContainerPolicy
{
    const string PreferenceKey = "DFUCoop.WorldContainers.Enabled";
    static bool dedicatedOverride;
    static bool dedicatedEnabled = true;
    static bool remoteReceived;
    static bool remoteEnabled = true;

    public static bool HasRemotePolicy { get { return remoteReceived; } }
    public static bool Enabled
    {
        get
        {
            if (NetworkServer.active)
                return dedicatedOverride ? dedicatedEnabled : PlayerPrefs.GetInt(PreferenceKey, 1) != 0;
            if (NetworkClient.active)
                return remoteReceived && remoteEnabled;
            return PlayerPrefs.GetInt(PreferenceKey, 1) != 0;
        }
    }

    public static void SetLocalPreference(bool enabled)
    {
        // Only a disconnected player can alter the NEXT server session.
        if (NetworkServer.active || NetworkClient.active) return;
        PlayerPrefs.SetInt(PreferenceKey, enabled ? 1 : 0);
        PlayerPrefs.Save();
    }
    public static void SetDedicatedOption(bool enabled)
    {
        dedicatedOverride = true;
        dedicatedEnabled = enabled;
    }
    public static void AcceptRemotePolicy(bool enabled)
    {
        remoteEnabled = enabled;
        remoteReceived = true;
    }
    public static void ClearDisconnectedRemotePolicy()
    {
        if (!NetworkServer.active && !NetworkClient.active)
            remoteReceived = false;
    }
}
