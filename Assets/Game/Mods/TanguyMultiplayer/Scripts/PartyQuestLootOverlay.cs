using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.Questing;

// Retains its filename for installation compatibility. This is now a server-owned
// party quest-item ledger, not requester-authored corpse overlays. The party leader
// supplies one item definition. The physical server reserves/consumes its logical
// identity across ALL matching corpses. QNS still owns inventory/progression sync.
public sealed class PartyQuestLootOverlay : MonoBehaviour
{
    public struct PublishMessage : NetworkMessage
    { public uint enemy; public string symbol; public string json; }
    public struct StateMessage : NetworkMessage
    { public uint enemy; public ulong party; public string inst; public string symbol; public string json; public bool consumed; }

    sealed class ItemState
    {
        public ulong party;
        public string inst, symbol, json;
        public bool consumed;
        public readonly HashSet<uint> enemies = new HashSet<uint>();
    }
    sealed class Reservation
    {
        public uint player, loot, request;
        public readonly List<string> keys = new List<string>();
    }
    internal sealed class LootEditEntry
    { public string key, symbol; public DaggerfallUnityItem item; public ulong uid; }
    public sealed class LootEditToken
    {
        internal readonly List<LootEditEntry> entries = new List<LootEditEntry>();
    }

    static PartyQuestLootOverlay instance;
    readonly Dictionary<string, ItemState> serverItems = new Dictionary<string, ItemState>(StringComparer.Ordinal);
    readonly Dictionary<string, ItemState> localItems = new Dictionary<string, ItemState>(StringComparer.Ordinal);
    readonly Dictionary<string, Reservation> reservations = new Dictionary<string, Reservation>(StringComparer.Ordinal);
    readonly Dictionary<uint, Dictionary<string, DaggerfallUnityItem>> displayed = new Dictionary<uint, Dictionary<string, DaggerfallUnityItem>>();
    readonly Dictionary<uint, DaggerfallLoot> corpses = new Dictionary<uint, DaggerfallLoot>();
    readonly HashSet<string> published = new HashSet<string>(StringComparer.Ordinal);
    bool serverReady, clientReady;
    float nextPublish;

    // External pickup receipts only. QNS can replace the original inventory object,
    // so retain the quest identity independently of the item's mutable reference.
    sealed class ExternalConsumption
    {
        public uint loot;
        public DaggerfallUnityItem item;
        public ulong questUid;
        public string symbol;
        public ItemCollection inventory;
        public float retryAt;
    }
    readonly Dictionary<string, ExternalConsumption> externalConsumptions =
        new Dictionary<string, ExternalConsumption>(StringComparer.Ordinal);

    sealed class InventoryObservation
    {
        public uint loot;
        public ItemCollection inventory;
        public int count;
    }
    readonly Dictionary<string, InventoryObservation> externalInventoryBefore =
        new Dictionary<string, InventoryObservation>(StringComparer.Ordinal);

    static int QuestInventoryCount(ItemCollection inventory, ulong questUid, string symbol)
    {
        if (inventory == null || questUid == 0 || string.IsNullOrEmpty(symbol)) return 0;
        int count = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var item = inventory.GetItem(i);
            if (item != null && item.IsQuestItem && item.QuestUID == questUid &&
                item.QuestItemSymbol != null && string.Equals(item.QuestItemSymbol.Name,
                    symbol, StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    void RememberExternalInventory(uint lootId, string key, ulong questUid, string symbol)
    {
        if (!ExternalLootPickupTracker.Enabled) return;
        var manager = GameManager.Instance;
        if (manager == null || manager.PlayerEntity == null) return;
        var inventory = manager.PlayerEntity.Items;
        externalInventoryBefore[lootId + "|" + key] = new InventoryObservation
            { loot = lootId, inventory = inventory, count = QuestInventoryCount(inventory, questUid, symbol) };
    }

    static bool InLocalInventory(ExternalConsumption pickup)
    {
        var manager = GameManager.Instance;
        return manager != null && manager.PlayerEntity != null &&
            ReferenceEquals(manager.PlayerEntity.Items, pickup.inventory) &&
            QuestInventoryCount(pickup.inventory, pickup.questUid, pickup.symbol) > 0;
    }

    internal static bool ExternalConsumptionStillValid(uint loot, string symbol)
    {
        if (instance == null) return false;
        foreach (var pair in instance.externalConsumptions)
        {
            ItemState state;
            if (pair.Value.loot == loot && InLocalInventory(pair.Value) &&
                instance.localItems.TryGetValue(pair.Key, out state) && !state.consumed &&
                PartyQuestFoeBinding.IsLocalParty(state.party) &&
                QuestNetSync.PartyVirtualShouldPublishQuestLoot(pair.Value.questUid) &&
                string.Equals(state.symbol, symbol, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    void ObserveExternalQuestPickup(uint lootId, DaggerfallLoot loot)
    {
        if (!ExternalLootPickupTracker.Enabled) return;
        Dictionary<string, DaggerfallUnityItem> shown;
        var manager = GameManager.Instance;
        if (manager == null || manager.PlayerEntity == null ||
            !displayed.TryGetValue(lootId, out shown)) return;
        var inventory = manager.PlayerEntity.Items;
        foreach (var pair in shown)
        {
            ItemState state;
            var item = pair.Value;
            ulong questUid;
            if (item == null || !item.IsQuestItem ||
                !localItems.TryGetValue(pair.Key, out state) || state.consumed ||
                !PartyQuestFoeBinding.IsLocalParty(state.party) || externalConsumptions.ContainsKey(pair.Key) ||
                !QuestNetSync.PartyVirtualTryGetLocalQuestUid(state.inst, out questUid) ||
                questUid != item.QuestUID || !QuestNetSync.PartyVirtualShouldPublishQuestLoot(questUid)) continue;
            if (Contains(loot, item))
            {
                RememberExternalInventory(lootId, pair.Key, questUid, state.symbol);
                continue;
            }
            bool exact = ReferenceEquals(inventory.GetItem(item.UID), item);
            InventoryObservation before;
            bool replaced = externalInventoryBefore.TryGetValue(lootId + "|" + pair.Key, out before) &&
                ReferenceEquals(before.inventory, inventory) && before.count == 0 &&
                QuestInventoryCount(inventory, questUid, state.symbol) > 0;
            // Missing source alone (e.g. snapshot replacement), or an already-owned
            // matching book alone, is not evidence of an external pickup.
            if (!exact && !replaced) continue;
            externalConsumptions[pair.Key] = new ExternalConsumption
                { loot = lootId, item = item, inventory = inventory, questUid = questUid, symbol = state.symbol };
            Debug.Log("[ExternalQuestPickup] Observed corpse-to-player transfer lootId=" + lootId +
                " symbol=" + state.symbol + " match=" + (exact ? "exact-object" : "quest-identity") + " questUid=" + questUid);
        }
    }

    void SubmitExternalQuestConsumptions()
    {
        foreach (var pair in new List<KeyValuePair<string, ExternalConsumption>>(externalConsumptions))
        {
            ItemState state;
            DaggerfallLoot loot;
            var pickup = pair.Value;
            if (!localItems.TryGetValue(pair.Key, out state) || state.consumed ||
                !PartyQuestFoeBinding.IsLocalParty(state.party) || !InLocalInventory(pickup) ||
                !corpses.TryGetValue(pickup.loot, out loot) || loot == null)
            {
                externalConsumptions.Remove(pair.Key);
                continue;
            }
            if (Time.unscaledTime < pickup.retryAt) continue;
            var sync = loot.GetComponent<LootMultiplayer>();
            if (sync == null || sync.EditPending) continue;
            pickup.retryAt = Time.unscaledTime + 1f;
            sync.BeginExternalQuestConsumption(state.symbol);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("Party Quest Loot Ledger");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PartyQuestLootOverlay>();
    }
    void Awake()
    {
        instance = this;
        Mirror.NetworkManager.ClientExtensionsReady += RegisterClient;
        Mirror.NetworkManager.ServerExtensionsReady += RegisterServer;
        Mirror.NetworkManager.SessionStopped += ResetSession;
    }
    void OnDestroy()
    {
        Mirror.NetworkManager.ClientExtensionsReady -= RegisterClient;
        Mirror.NetworkManager.ServerExtensionsReady -= RegisterServer;
        Mirror.NetworkManager.SessionStopped -= ResetSession;
        if (instance == this) instance = null;
    }
    void RegisterClient()
    {
        if (clientReady) return;
        NetworkClient.RegisterHandler<StateMessage>(ApplyState);
        clientReady = true;
    }
    void RegisterServer()
    {
        if (serverReady) return;
        NetworkServer.RegisterHandler<PublishMessage>(ServerPublish);
        serverReady = true;
    }
    void ResetSession()
    {
        serverItems.Clear(); localItems.Clear(); reservations.Clear();
        displayed.Clear(); corpses.Clear(); published.Clear();
        externalConsumptions.Clear();
        externalInventoryBefore.Clear();
        serverReady = clientReady = false; nextPublish = 0f;
    }
    void Update()
    {
        if (NetworkClient.active && !clientReady) RegisterClient();
        if (NetworkServer.active && !serverReady) RegisterServer();
        if (NetworkClient.ready && Time.realtimeSinceStartup >= nextPublish)
        {
            nextPublish = Time.realtimeSinceStartup + 0.2f;
            PublishLeaderQueues();
        }
    }
    void LateUpdate()
    {
        // QRB can add a queued local item after death. Reconcile against the ledger
        // after normal Updates so it cannot resurrect a consumed/different book.
        if (!NetworkClient.active) return;
        foreach (var pair in new List<KeyValuePair<uint, DaggerfallLoot>>(corpses))
        {
            if (pair.Value == null) { corpses.Remove(pair.Key); displayed.Remove(pair.Key); }
            else Reconcile(pair.Key, pair.Value);
        }
        SubmitExternalQuestConsumptions();
        foreach (var pair in new List<KeyValuePair<string, InventoryObservation>>(externalInventoryBefore))
            if (!corpses.ContainsKey(pair.Value.loot)) externalInventoryBefore.Remove(pair.Key);
    }
    static string Key(ulong party, string inst, string symbol)
    { return party + "|" + inst + "|" + symbol.ToLowerInvariant(); }

    public static void OnLocalCorpseRegistered(GameObject enemy, DaggerfallLoot loot)
    {
        if (instance == null || enemy == null || loot == null) return;
        var ni = enemy.GetComponent<NetworkIdentity>();
        if (ni == null || ni.netId == 0) return;
        uint id = LootCatcher.BuildCorpseLootId(ni.netId);
        instance.corpses[id] = loot;
        instance.Reconcile(id, loot);
    }
    public static void ReconcileCorpse(DaggerfallLoot loot)
    {
        if (instance == null || loot == null) return;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync == null || !LootCatcher.IsCorpseLootId(sync.lootId)) return;
        instance.corpses[sync.lootId] = loot;
        instance.Reconcile(sync.lootId, loot);
    }
    void PublishLeaderQueues()
    {
        if (!PartyQuestVirtualAuthority.VirtualServerActive) return;
        foreach (var b in PartyQuestFoeBinding.LocalBindings())
        {
            if (!PartyQuestFoeBinding.IsLocalParty(b.party)) continue;
            ulong uid;
            if (!QuestNetSync.PartyVirtualTryGetLocalQuestUid(b.inst, out uid) || !QuestNetSync.PartyVirtualShouldPublishQuestLoot(uid)) continue;
            var q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(uid) : null;
            var foe = q != null ? q.GetFoe(new Symbol(b.foe)) : null;
            var queue = foe != null ? foe.GetClonedItemQueue() : null;
            if (queue == null) continue;
            foreach (var item in queue)
            {
                if (item == null || !item.IsQuestItem || item.QuestUID != uid || item.QuestItemSymbol == null || string.IsNullOrEmpty(item.QuestItemSymbol.Name)) continue;
                string symbol = item.QuestItemSymbol.Name;
                string publishKey = b.enemy + "|" + Key(b.party, b.inst, symbol);
                if (published.Contains(publishKey)) continue;
                string json = QuestNetSync.PartyVirtualSerializeQuestItem(item);
                if (string.IsNullOrEmpty(json)) continue;
                NetworkClient.Send(new PublishMessage { enemy = b.enemy, symbol = symbol, json = json });
                published.Add(publishKey);
            }
        }
    }
    void ServerPublish(NetworkConnection c, PublishMessage m)
    {
        PartyQuestFoeBinding.Binding b;
        if (c == null || c.identity == null || string.IsNullOrEmpty(m.symbol) || string.IsNullOrEmpty(m.json) || m.json.Length > 262144 ||
            !PartyQuestFoeBinding.TryGetServer(m.enemy, out b) ||
            !MultiplayerPartyManager.ServerIsPlayerInParty(c.identity.netId, b.party) ||
            MultiplayerPartyManager.ServerGetPartyLeaderNetId(c.identity.netId) != c.identity.netId) return;
        string key = Key(b.party, b.inst, m.symbol);
        ItemState state;
        if (!serverItems.TryGetValue(key, out state))
        {
            state = new ItemState { party = b.party, inst = b.inst, symbol = m.symbol, json = m.json };
            serverItems[key] = state;
        }
        // The FIRST leader definition is canonical for this logical resource, including
        // book message/value. Another foe/requester cannot replace it with a new book.
        if (state.enemies.Add(m.enemy)) SendState(state, m.enemy);
    }
    void SendState(ItemState state, uint enemy)
    {
        var m = new StateMessage { enemy = enemy, party = state.party, inst = state.inst, symbol = state.symbol,
            json = state.consumed ? string.Empty : state.json, consumed = state.consumed };
        foreach (var c in MultiplayerPartyManager.ServerGetPartyConnectionsByPartyId(state.party))
            if (c != null && c.isReady) c.Send(m);
    }
    // Re-send existing party-authorized ledger state for a reconstructed corpse.
    // This does not publish, reroll, bind a quest or reset consumed items.
    public static void ServerReplayCorpse(NetworkConnection connection, uint enemyId)
    {
        if (!NetworkServer.active || instance == null || connection == null || !connection.isReady) return;
        foreach (var state in instance.serverItems.Values)
        {
            if (!state.enemies.Contains(enemyId)) continue;
            bool belongsToParty = false;
            foreach (var member in MultiplayerPartyManager.ServerGetPartyConnectionsByPartyId(state.party))
                if (member == connection) { belongsToParty = true; break; }
            if (!belongsToParty) continue;
            connection.Send(new StateMessage { enemy = enemyId, party = state.party,
                inst = state.inst, symbol = state.symbol,
                json = state.consumed ? string.Empty : state.json, consumed = state.consumed });
        }
    }

    void ApplyState(StateMessage m)
    {
        if (!PartyQuestFoeBinding.IsLocalParty(m.party) || string.IsNullOrEmpty(m.inst) || string.IsNullOrEmpty(m.symbol)) return;
        string key = Key(m.party, m.inst, m.symbol);
        ItemState state;
        if (!localItems.TryGetValue(key, out state))
        {
            state = new ItemState { party = m.party, inst = m.inst, symbol = m.symbol, json = m.json };
            localItems[key] = state;
        }
        // Consumption is monotonic. A delayed publication must never resurrect it.
        state.consumed |= m.consumed;
        if (string.IsNullOrEmpty(state.json) && !m.consumed) state.json = m.json;
        if (m.enemy != 0) state.enemies.Add(m.enemy);
        if (m.consumed)
        {
            Debug.Log("[PartyQuestLoot] Consumed party=" + m.party + " inst=" + m.inst + " symbol=" + m.symbol);
            foreach (var pair in new List<KeyValuePair<uint, DaggerfallLoot>>(corpses)) Reconcile(pair.Key, pair.Value);
        }
        else
        {
            DaggerfallLoot loot;
            uint id = LootCatcher.BuildCorpseLootId(m.enemy);
            if (corpses.TryGetValue(id, out loot)) Reconcile(id, loot);
        }
    }
    public static void ReconcileBeforeGrantedEdit(DaggerfallLoot loot)
    {
        if (instance == null || loot == null) return;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync != null) instance.Reconcile(sync.lootId, loot, true);
    }
    void Reconcile(uint lootId, DaggerfallLoot loot, bool beforeGrantedEdit = false)
    {
        if (loot == null || loot.Items == null) return;
        PartyQuestFoeBinding.Binding b;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (!beforeGrantedEdit && sync != null && sync.EditPending) return;
        // UI edits are protected by EditPending, including delayed quantity dialogs.
        // Inspect before reconciliation can replace a physically removed book.
        if (!beforeGrantedEdit && sync != null) ObserveExternalQuestPickup(lootId, loot);
        if (!PartyQuestFoeBinding.TryGetLocal(lootId & 0x7fffffffu, out b))
        {
            // Binding/state may arrive after the corpse. Never expose a locally
            // generated shared book while waiting; private/Off quest items stay intact.
            if (PartyQuestVirtualAuthority.QuestVirtualizationActive)
                foreach (var item in Snapshot(loot))
                    if (item != null && item.IsQuestItem && QuestNetSync.PartyVirtualShouldPublishQuestLoot(item.QuestUID))
                        loot.Items.RemoveItem(item);
            return;
        } // exact clicked object must survive its transaction
        Dictionary<string, DaggerfallUnityItem> keep;
        if (!displayed.TryGetValue(lootId, out keep))
            displayed[lootId] = keep = new Dictionary<string, DaggerfallUnityItem>(StringComparer.Ordinal);
        var desired = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        ulong uid = 0;
        if (PartyQuestFoeBinding.IsLocalParty(b.party) && QuestNetSync.PartyVirtualTryGetLocalQuestUid(b.inst, out uid) &&
            QuestNetSync.PartyVirtualShouldPublishQuestLoot(uid))
            foreach (var pair in localItems)
                if (!pair.Value.consumed && !externalConsumptions.ContainsKey(pair.Key) &&
                    pair.Value.party == b.party && pair.Value.inst == b.inst && pair.Value.enemies.Contains(b.enemy))
                    desired[pair.Key] = pair.Value;
        foreach (var item in Snapshot(loot))
        {
            if (item == null || !item.IsQuestItem) continue; // ordinary loot is always global
            bool retain = false;
            foreach (var pair in keep)
                if (desired.ContainsKey(pair.Key) && ReferenceEquals(item, pair.Value)) { retain = true; break; }
            if (!retain) loot.Items.RemoveItem(item);
        }
        foreach (string key in new List<string>(keep.Keys))
            if (!desired.ContainsKey(key))
            {
                keep.Remove(key);
                externalInventoryBefore.Remove(lootId + "|" + key);
            }
        foreach (var pair in desired)
        {
            DaggerfallUnityItem item;
            if (keep.TryGetValue(pair.Key, out item) && Contains(loot, item)) continue;
            // A definition arriving after the reservation cannot become an extra
            // unreserved Take All item. Only remove stale entries at grant time.
            if (beforeGrantedEdit) continue;
            item = QuestNetSync.PartyVirtualDeserializeQuestItem(pair.Value.json, uid, pair.Value.symbol);
            if (item == null) continue;
            loot.Items.AddItem(item);
            keep[pair.Key] = item;
            RememberExternalInventory(lootId, pair.Key, uid, pair.Value.symbol);
        }
    }
    static DaggerfallUnityItem[] Snapshot(DaggerfallLoot loot)
    {
        // CloneAll allocates NEW UIDs and cannot be used to remove original items.
        var result = new DaggerfallUnityItem[loot.Items.Count];
        for (int i = 0; i < result.Length; i++) result[i] = loot.Items.GetItem(i);
        return result;
    }
    static bool Contains(DaggerfallLoot loot, DaggerfallUnityItem item)
    {
        if (loot == null || loot.Items == null || item == null) return false;
        for (int i = 0; i < loot.Items.Count; i++)
            if (ReferenceEquals(loot.Items.GetItem(i), item)) return true;
        return false;
    }

    // Empty = ordinary item; * = Take All; ! = stale/unbound shared quest item.
    // Symbols are interpreted only inside the server-owned corpse binding.
    public static string Selection(DaggerfallLoot loot, DaggerfallUnityItem item)
    {
        if (instance == null || loot == null) return string.Empty;
        var sync = loot.GetComponent<LootMultiplayer>();
        if (sync == null || !LootCatcher.IsCorpseLootId(sync.lootId)) return string.Empty;
        PartyQuestFoeBinding.Binding b;
        if (!PartyQuestFoeBinding.TryGetLocal(sync.lootId & 0x7fffffffu, out b))
        {
            instance.Reconcile(sync.lootId, loot);
            return item != null && item.IsQuestItem && QuestNetSync.PartyVirtualShouldPublishQuestLoot(item.QuestUID) ? "!" : string.Empty;
        }
        instance.Reconcile(sync.lootId, loot);
        if (item != null && GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
        {
            var inventory = GameManager.Instance.PlayerEntity.Items;
            for (int i = 0; i < inventory.Count; i++)
                if (ReferenceEquals(inventory.GetItem(i), item)) return string.Empty;
        }
        if (item == null) return "*";
        if (!item.IsQuestItem) return string.Empty;
        Dictionary<string, DaggerfallUnityItem> shown;
        if (instance.displayed.TryGetValue(sync.lootId, out shown))
            foreach (var pair in shown)
                if (ReferenceEquals(pair.Value, item) && Contains(loot, item))
                    return instance.localItems[pair.Key].symbol;
        return "!";
    }
    public static LootEditToken BeginLocalLootEdit(DaggerfallLoot loot, DaggerfallUnityItem clickedItem)
    {
        if (instance == null || loot == null) return null;
        var sync = loot.GetComponent<LootMultiplayer>();
        Dictionary<string, DaggerfallUnityItem> shown;
        if (sync == null || !instance.displayed.TryGetValue(sync.lootId, out shown)) return null;
        var token = new LootEditToken();
        foreach (var pair in shown)
        {
            if ((clickedItem != null && !ReferenceEquals(clickedItem, pair.Value)) || !Contains(loot, pair.Value)) continue;
            token.entries.Add(new LootEditEntry { key = pair.Key, symbol = instance.localItems[pair.Key].symbol,
                item = pair.Value, uid = pair.Value.UID });
        }
        return token;
    }
    public static string[] EndLocalLootEdit(DaggerfallLoot loot, LootEditToken token)
    {
        var consumed = new List<string>();
        if (instance == null || token == null) return consumed.ToArray();
        foreach (var e in token.entries)
            if (!Contains(loot, e.item)) consumed.Add(e.symbol);
        return consumed.ToArray();
    }

    // Called inside the original physical-server loot reservation/commit. Unlike the
    // old post-pickup report, this excludes competing pickups BEFORE inventory changes.
    public static bool ServerReserve(NetworkConnection c, uint loot, uint request, string selection)
    {
        if (instance == null || string.IsNullOrEmpty(selection)) return true;
        PartyQuestFoeBinding.Binding b;
        if (c == null || c.identity == null || selection == "!") return false;
        if (!PartyQuestFoeBinding.TryGetServer(loot & 0x7fffffffu, out b) ||
            !MultiplayerPartyManager.ServerIsPlayerInParty(c.identity.netId, b.party)) return selection == "*";
        var r = new Reservation { player = c.identity.netId, loot = loot, request = request };
        foreach (var pair in instance.serverItems)
        {
            var state = pair.Value;
            if (state.party != b.party || state.inst != b.inst || !state.enemies.Contains(b.enemy) ||
                (selection != "*" && !string.Equals(selection, state.symbol, StringComparison.OrdinalIgnoreCase))) continue;
            if (state.consumed)
            {
                instance.SendState(state, b.enemy);
                if (selection != "*") return false;
                continue;
            }
            if (instance.reservations.ContainsKey(pair.Key)) return false;
            r.keys.Add(pair.Key);
        }
        if (selection != "*" && r.keys.Count == 0) return false;
        foreach (string key in r.keys) instance.reservations[key] = r;
        return true;
    }
    public static void ServerCommit(uint player, uint loot, uint request, string[] consumed)
    {
        if (instance == null) return;
        foreach (var pair in new List<KeyValuePair<string, Reservation>>(instance.reservations))
        {
            var r = pair.Value;
            if (r.player != player || r.loot != loot || r.request != request) continue;
            var state = instance.serverItems[pair.Key];
            if (consumed != null && Array.Exists(consumed, s => string.Equals(s, state.symbol, StringComparison.OrdinalIgnoreCase)))
            {
                state.consumed = true;
                instance.SendState(state, 0);
                Debug.Log("[PartyQuestLoot] Commit party=" + state.party + " inst=" + state.inst + " symbol=" + state.symbol + " picker=" + player);
            }
            instance.reservations.Remove(pair.Key);
        }
    }
    public static void ServerReplay()
    {
        if (instance == null || !NetworkServer.active) return;
        foreach (var state in instance.serverItems.Values)
            foreach (uint enemy in state.enemies) instance.SendState(state, enemy);
    }

    public static void ServerRelease(uint player)
    {
        if (instance == null) return;
        foreach (var pair in new List<KeyValuePair<string, Reservation>>(instance.reservations))
            if (pair.Value.player == player) instance.reservations.Remove(pair.Key);
    }
}

