using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.MagicAndEffects;

// Server-side relevance only. Mirror owns spawn/hide messages and client object lifetime.
// Non-enemy objects and the listen host retain their existing visibility.
public sealed class EnemyInterestManagement : InterestManagement
{
    sealed class Entry
    {
        public NetworkIdentity id;
        public DynamicEnemyAuthority authority;
        public EnemyWorldPosition world;
        public SetupDemoEnemy setup;
        public EntityEffectManager effects;
        public bool protectedLifecycle;
    }
    readonly Dictionary<NetworkIdentity, Entry> entries = new Dictionary<NetworkIdentity, Entry>();
    readonly LinkedList<Entry> pending = new LinkedList<Entry>();
    readonly Dictionary<NetworkIdentity, LinkedListNode<Entry>> nodes = new Dictionary<NetworkIdentity, LinkedListNode<Entry>>();
    readonly Dictionary<NetworkConnection, PositionMultiplayer> positions = new Dictionary<NetworkConnection, PositionMultiplayer>();
    readonly Dictionary<NetworkConnection, int> newSpawns = new Dictionary<NetworkConnection, int>();
    int budgetFrame = -1;
    NetworkIdentity handoffIdentity;
    NetworkConnection handoffConnection;
    public static bool FilteringEnabled = true;

    public static string Command(params string[] args)
    {
        if (args.Length > 0 && (args[0] == "on" || args[0] == "off"))
        {
            if (!NetworkServer.active) return "Change filtering on the host/server.";
            FilteringEnabled = args[0] == "on";
        }
        else if (args.Length > 0 && args[0] != "status") return "enemyinterest status|on|off";
        if (!NetworkServer.active)
        {
            int clientEnemies = 0;
            foreach (var id in NetworkClient.spawned.Values)
                if (id != null && id.GetComponent<DynamicEnemyAuthority>() != null) clientEnemies++;
            return "Client enemy objects: " + clientEnemies;
        }
        var manager = NetworkServer.aoi as EnemyInterestManagement;
        if (manager == null) return "DF enemy interest manager not installed yet (installed on first enemy spawn), or another AOI is active.";
        int links = 0, protectedCount = 0, unstamped = 0;
        foreach (var entry in manager.entries.Values)
        {
            if (entry.id == null) continue;
            if (manager.Protected(entry)) protectedCount++;
            if (entry.world != null && entry.world.isDungeonSpawn && string.IsNullOrEmpty(entry.world.PresentationDungeonInstanceId)) unstamped++;
            foreach (var observer in entry.id.observers.Values)
                if (observer != NetworkServer.localConnection) links++;
        }
        return "Enemy interest: " + FilteringEnabled + "; server enemies=" + manager.entries.Count +
            "; remote observer links=" + links + "; protected=" + protectedCount +
            "; unstamped dungeon enemies=" + unstamped + "; pending test blocks=" +
            DaggerfallWorkshop.NetworkDungeonGrid.PendingTestEnemyBlocks;
    }

    public static void Install()
    {
        if (NetworkServer.aoi != null || NetworkManager.singleton == null) return;
        NetworkManager.singleton.gameObject.AddComponent<EnemyInterestManagement>();
    }

    // Must precede AssignClientAuthority: Mirror 57 sends ChangeOwner only to observers.
    public static void PrepareOwner(NetworkIdentity id, NetworkConnection owner)
    {
        var manager = NetworkServer.aoi as EnemyInterestManagement;
        if (manager == null || id == null || owner == null || !owner.isReady) return;
        manager.handoffIdentity = id;
        manager.handoffConnection = owner;
        try { NetworkServer.RebuildObservers(id, false); }
        finally { manager.handoffIdentity = null; manager.handoffConnection = null; }
    }

    public override void OnSpawned(NetworkIdentity id)
    {
        if (id == null || entries.ContainsKey(id)) return;
        var authority = id.GetComponent<DynamicEnemyAuthority>();
        if (authority == null || id.GetComponent<PlayerMultiplayer>() != null) return;
        var entry = new Entry { id = id, authority = authority,
            world = id.GetComponent<EnemyWorldPosition>(), setup = id.GetComponent<SetupDemoEnemy>(),
            effects = id.GetComponent<EntityEffectManager>() };
        entries.Add(id, entry);
        nodes.Add(id, pending.AddLast(entry));
    }
    public override void OnDestroyed(NetworkIdentity id)
    {
        LinkedListNode<Entry> node;
        if (nodes.TryGetValue(id, out node)) { pending.Remove(node); nodes.Remove(id); }
        entries.Remove(id);
    }
    public override void Reset()
    {
        entries.Clear(); pending.Clear(); nodes.Clear(); positions.Clear(); newSpawns.Clear();
        budgetFrame = -1; handoffIdentity = null; handoffConnection = null;
    }

    bool Protected(Entry entry)
    {
        // Quest binding is delivered by one-shot RPCs; preserve those objects until
        // a separate durable quest-binding replay exists. Also preserve mod-owned lifetimes.
        if (!entry.protectedLifecycle &&
            (entry.authority.PersistentOwnerPlayerNetId != 0 ||
             entry.id.GetComponent<QuestResourceBehaviour>() != null ||
             (entry.world != null && entry.world.isCreateFoeWaveSpawn) ||
             (entry.setup != null && (entry.setup.ExternalSpawnOriginNetId != 0 ||
                 !string.IsNullOrEmpty(entry.setup.ExternalSpawnDisplayName)))))
            entry.protectedLifecycle = true;
        return entry.protectedLifecycle;
    }
    PositionMultiplayer Position(NetworkConnection connection)
    {
        if (connection.identity == null) return null;
        PositionMultiplayer value;
        if (!positions.TryGetValue(connection, out value) || value == null || value.netIdentity != connection.identity)
            positions[connection] = value = connection.identity.GetComponent<PositionMultiplayer>();
        return value;
    }
    bool Relevant(Entry entry, NetworkConnection connection)
    {
        if (!FilteringEnabled || connection == NetworkServer.localConnection ||
            connection == entry.id.connectionToClient ||
            (entry.id == handoffIdentity && connection == handoffConnection) || Protected(entry)) return true;
        var world = entry.world;
        if (world == null || entry.setup == null) return true;
        var player = Position(connection);
        // Defer initial enemy delivery until a real player location has arrived.
        if (player == null || !player.EnemyInterestLocationKnown) return false;
        bool alreadyObserved = entry.id.observers.ContainsKey(connection.connectionId);
        // Do not tear down an existing copy while its effect manager is busy.
        if (alreadyObserved && entry.effects != null && !entry.effects.CanSuspendNetworkObserverUpdates) return true;
        if (!string.IsNullOrEmpty(world.PresentationDungeonInstanceId))
            return player.EnemyInterestState == PositionMultiplayer.PartyLocationState.DungeonInterior &&
                player.EnemyInterestDungeonId == world.PresentationDungeonInstanceId;
        // Imported enemies can be networked before their block is parented/stamped.
        // Defer them rather than broadcasting a temporary unknown dungeon to everyone.
        if (world.isDungeonSpawn || entry.setup.isDungeonEnemy) return false;
        if (world.isInteriorSpawn)
        {
            if (world.PresentationBuildingKey == 0) return true;
            return player.EnemyInterestState == PositionMultiplayer.PartyLocationState.BuildingInterior &&
                player.EnemyInterestBuildingKey == world.PresentationBuildingKey &&
                player.EnemyInterestRegion == world.PresentationRegionName &&
                player.EnemyInterestLocation == world.PresentationLocationName;
        }
        if (player.EnemyInterestState == PositionMultiplayer.PartyLocationState.DungeonInterior ||
            player.EnemyInterestState == PositionMultiplayer.PartyLocationState.BuildingInterior) return false;
        if (!world.initialized || player.x == 0 || player.z == 0) return false;
        // DF coordinates remain valid across Unity terrain wrapping. Hysteresis avoids churn.
        double dx = ((double)player.x - world.worldX) / 40.0;
        double dz = ((double)player.z - world.worldZ) / 40.0;
        double radius = alreadyObserved ? 250.0 : 225.0;
        return dx * dx + dz * dz <= radius * radius;
    }
    public override bool OnCheckObserver(NetworkIdentity id, NetworkConnection connection)
    {
        if (connection == null) return false;
        Entry entry;
        if (!entries.TryGetValue(id, out entry)) return true;
        if (!Relevant(entry, connection)) return false;
        if (connection == NetworkServer.localConnection || connection == id.connectionToClient ||
            (id == handoffIdentity && connection == handoffConnection) || Protected(entry) ||
            id.observers.ContainsKey(connection.connectionId)) return true;
        if (budgetFrame != Time.frameCount) { budgetFrame = Time.frameCount; newSpawns.Clear(); }
        int count;
        newSpawns.TryGetValue(connection, out count);
        if (count >= 4) return false;
        newSpawns[connection] = count + 1;
        return true;
    }
    public override void OnRebuildObservers(NetworkIdentity id, HashSet<NetworkConnection> observers, bool initialize)
    {
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != null && connection.isReady && OnCheckObserver(id, connection)) observers.Add(connection);
    }
    public override void SetHostVisibility(NetworkIdentity id, bool visible)
    {
        // This AOI filters remote enemy delivery only. Do not invoke the base renderer
        // toggle: it enables every child Renderer when visible=true, including DFU's
        // deliberately hidden editor markers and deferred dungeon presentation.
        // Non-enemy identities remain broadcast; host enemy/dungeon presentation is
        // controlled by DynamicEnemyAuthority and ServerDungeonPresentation.
        // The host shares authoritative objects, so it must retain their renderer state.
    }
    void Update()
    {
        if (!NetworkServer.active || pending.Count == 0) return;
        int count = Mathf.Clamp(Mathf.CeilToInt(pending.Count * Mathf.Min(Time.unscaledDeltaTime, 0.05f) / 0.25f), 1, 256);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < count && pending.First != null; i++)
        {
            var node = pending.First;
            pending.RemoveFirst(); pending.AddLast(node);
            if (node.Value.id != null) NetworkServer.RebuildObservers(node.Value.id, false);
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - start) / (double)System.Diagnostics.Stopwatch.Frequency >= 0.002) break;
        }
    }
}
