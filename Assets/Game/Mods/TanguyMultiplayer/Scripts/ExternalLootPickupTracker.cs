using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;

// Observes transfers; networking remains entirely in LootMultiplayer/LootCatcher.
// Uses exact item identity, never a template-only match against player possessions.
public sealed class ExternalLootPickupTracker
{
    // Optional adapter: no reference to, or patch of, the mod's assembly.
    static Type physicalManagerType;
    static Behaviour physicalManager;
    static float nextPresenceCheck;
    static bool announced;
    public static bool Enabled
    {
        get
        {
            if (!NetworkClient.active) return false;
            if (Time.unscaledTime >= nextPresenceCheck)
            {
                nextPresenceCheck = Time.unscaledTime + 2f;
                if (physicalManagerType == null)
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        physicalManagerType = assembly.GetType("PhysicalItems.PhysicalItemManager", false);
                        if (physicalManagerType != null) break;
                    }
                if (physicalManagerType != null && physicalManager == null &&
                    typeof(Behaviour).IsAssignableFrom(physicalManagerType))
                    physicalManager = UnityEngine.Object.FindObjectOfType(physicalManagerType) as Behaviour;
            }
            bool active = physicalManager != null && physicalManager.isActiveAndEnabled;
            if (active && !announced)
            {
                announced = true;
                Debug.Log("[ExternalLootPickup] Item and direct-gold rollback test active. Quest-linked items are excluded.");
            }
            return active;
        }
    }

    static bool Eligible(DaggerfallUnityItem item)
    {
        // A quest click has effects outside these collections BEFORE Transfer.
        // Never reverse, reinsert, or replay a quest-linked item.
        return item != null && !item.IsQuestItem && item.QuestUID == 0;
    }

    static bool IsGold(DaggerfallUnityItem item)
    {
        return Eligible(item) && item.IsOfTemplate(ItemGroups.Currency, (int)Currency.Gold_pieces);
    }

    static long InventoryGold(ItemCollection items)
    {
        long count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items.GetItem(i);
            if (IsGold(item)) count += item.stackCount;
        }
        return count;
    }

    sealed class Entry
    {
        public DaggerfallUnityItem item;
        public int count;
        public string key;
    }

    readonly LootMultiplayer sync;
    readonly Dictionary<ulong, Entry> baseline = new Dictionary<ulong, Entry>();
    readonly Dictionary<ulong, Entry> retired = new Dictionary<ulong, Entry>();
    readonly Dictionary<ulong, int> playerCounts = new Dictionary<ulong, int>();
    bool scanning;
    bool initialized;
    DaggerfallWorkshop.Game.Entity.PlayerEntity rememberedPlayer;
    int previousGoldPieces;
    long previousInventoryGold;
    public bool InApprovedAction { get; set; }

    public ExternalLootPickupTracker(LootMultiplayer owner) { sync = owner; }

    // All calls run on Unity's main thread. FullSerializer is designed to reuse
    // its converter/metadata caches across independent root serializations.
    // Initialize only when this optional adapter actually needs a fingerprint.
    static FullSerializer.fsSerializer itemKeySerializer;

    static string ItemKey(DaggerfallUnityItem item)
    {
        try
        {
        var saved = item.GetSaveData();
        saved.uid = 0;
        saved.stackCount = 1;
        FullSerializer.fsData data;
        if (itemKeySerializer == null)
            itemKeySerializer = new FullSerializer.fsSerializer();
        var result = itemKeySerializer.TrySerialize(
            typeof(DaggerfallWorkshop.Game.Serialization.ItemData_v1), saved, out data);
        if (result.Failed) return null;
        return FullSerializer.fsJsonPrinter.CompressedJson(data);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExternalLootPickup] Cannot fingerprint item: " + ex.Message);
            return null;
        }
    }

    ItemCollection PlayerItems
    {
        get { return GameManager.Instance != null && GameManager.Instance.PlayerEntity != null
            ? GameManager.Instance.PlayerEntity.Items : null; }
    }

    public void Remember(bool incomingSnapshot)
    {
        if (!Enabled) { initialized = false; baseline.Clear(); retired.Clear(); playerCounts.Clear(); return; }
        var loot = sync.GetComponent<DaggerfallLoot>();
        var player = PlayerItems;
        if (loot == null || player == null) return;
        var entity = GameManager.Instance.PlayerEntity;
        if (!ReferenceEquals(rememberedPlayer, entity))
        {
            baseline.Clear();
            retired.Clear();
            rememberedPlayer = entity;
        }
        if (incomingSnapshot)
        {
            foreach (var pair in baseline)
            {
                // A retained physical object can still point at this old local UID.
                if (!loot.Items.Contains(pair.Key) && !player.Contains(pair.Key))
                    retired[pair.Key] = pair.Value;
            }
        }
        baseline.Clear();
        for (int i = 0; i < loot.Items.Count; i++)
        {
            var item = loot.Items.GetItem(i);
            if (!Eligible(item)) continue;
            baseline[item.UID] = new Entry { item = item, count = item.stackCount, key = ItemKey(item) };
            retired.Remove(item.UID);
        }
        playerCounts.Clear();
        for (int i = 0; i < player.Count; i++)
        {
            var item = player.GetItem(i);
            playerCounts[item.UID] = item.stackCount;
        }
        initialized = true;
        previousGoldPieces = entity.GoldPieces;
        previousInventoryGold = InventoryGold(player);
    }

    public void Poll(bool allowRequest)
    {
        // An approved UI action may await a quantity popup across several frames.
        if (!Enabled) { initialized = false; return; }
        if (scanning || InApprovedAction || sync.EditGranted || sync.lootId == 0) return;
        var loot = sync.GetComponent<DaggerfallLoot>();
        var player = PlayerItems;
        if (loot == null || player == null) return;
        var entity = GameManager.Instance.PlayerEntity;
        if (!initialized || !ReferenceEquals(rememberedPlayer, entity)) { Remember(false); return; }
        scanning = true;
        try
        {
            Entry selected = null;
            int selectedCount = 0;
            bool selectedAsCurrency = false;
            // Currency has no UID in the player's inventory. Require both a source
            // gold removal and the exact matching cash gain. Never infer a pickup
            // from a wallet change alone or from the total including credit notes.
            long removedGold = 0;
            foreach (var pair in baseline)
            {
                if (!IsGold(pair.Value.item)) continue;
                var remainingGold = loot.Items.GetItem(pair.Key);
                int removedCount = pair.Value.count - (remainingGold == null ? 0 : remainingGold.stackCount);
                if (removedCount > 0) removedGold += removedCount;
            }
            long cashGain = (long)entity.GoldPieces - previousGoldPieces;
            bool directGoldMatched = removedGold > 0 && removedGold <= int.MaxValue &&
                cashGain == removedGold && previousGoldPieces >= 0 &&
                InventoryGold(player) == previousInventoryGold;
            // Reject an old physical reference whose backing item was removed/rebuilt
            // by a network snapshot. Do not infer identity from matching item types.
            foreach (var pair in retired)
            {
                if (!Eligible(pair.Value.item)) continue;
                var gained = player.GetItem(pair.Key);
                int previous;
                playerCounts.TryGetValue(pair.Key, out previous);
                if (!Eligible(gained) || gained.stackCount <= previous) continue;
                int staleQuantity = gained.stackCount - previous;
                if (previous == 0) player.RemoveItem(gained);
                else gained.stackCount = previous;
                // Never map an obsolete object to another identical-looking item.
                Debug.Log("[ExternalLootPickup] Rejected stale physical reference. lootId=" + sync.lootId + " itemUid=" + pair.Key + " count=" + staleQuantity);
                DaggerfallUI.AddHUDText("This physical item changed. Open the loot inventory to refresh it.");
            }

            foreach (var pair in baseline)
            {
                var before = pair.Value;
                if (!Eligible(before.item)) continue;
                var remaining = loot.Items.GetItem(pair.Key);
                int removed = before.count - (remaining == null ? 0 : remaining.stackCount);
                if (removed <= 0) continue;
                if (IsGold(before.item) && directGoldMatched)
                {
                    // Reverse cash, not credit notes and not an inventory gold item.
                    entity.GoldPieces -= removed;
                    if (remaining == null)
                    {
                        before.item.stackCount = before.count;
                        loot.Items.AddItem(before.item, noStack: true);
                    }
                    else remaining.stackCount = before.count;
                    Debug.Log("[ExternalLootPickup] Reversed direct gold pickup. lootId=" + sync.lootId + " itemUid=" + pair.Key + " amount=" + removed);
                    if (selected == null && allowRequest && !sync.EditPending)
                    {
                        selected = before;
                        selectedCount = removed;
                        selectedAsCurrency = true;
                    }
                    else DaggerfallUI.AddHUDText("Loot is busy. Please try that gold pickup again.");
                    continue;
                }
                var gained = player.GetItem(pair.Key);
                int previous;
                playerCounts.TryGetValue(pair.Key, out previous);
                // Some mods clone on pickup or merge into an existing player stack.
                // Match the complete saved properties AND a newly gained quantity.
                if (!Eligible(gained) || gained.stackCount - previous != removed)
                {
                    gained = null;
                    if (before.key != null)
                        for (int i = 0; i < player.Count; i++)
                        {
                            var candidate = player.GetItem(i);
                            int oldCount;
                            playerCounts.TryGetValue(candidate.UID, out oldCount);
                            if (Eligible(candidate) && candidate.stackCount - oldCount == removed && ItemKey(candidate) == before.key)
                            {
                                // Ambiguous gains cannot be safely reversed.
                                if (gained != null) { gained = null; break; }
                                gained = candidate; previous = oldCount;
                            }
                        }
                }
                if (gained == null)
                {
                    Debug.LogWarning("[ExternalLootPickup] Could not correlate removed item with a player inventory gain. lootId=" + sync.lootId + " itemUid=" + pair.Key +
                        (IsGold(before.item) ? " goldRemoved=" + removedGold + " cashGain=" + cashGain + "; currency unchanged by tracker" : string.Empty));
                    continue;
                }

                // Undo the speculative mod transfer before asking the existing server
                // transaction to perform it. Rejection therefore leaves no duplicate.
                if (previous == 0) player.RemoveItem(gained);
                else gained.stackCount = previous;
                if (remaining == null)
                {
                    before.item.stackCount = before.count;
                    loot.Items.AddItem(before.item, noStack: true);
                }
                else remaining.stackCount = before.count;

                if (selected == null && allowRequest && !sync.EditPending)
                {
                    selected = before;
                    selectedCount = removed;
                }
                else DaggerfallUI.AddHUDText("Loot is busy. Please try that pickup again.");
            }
            Remember(false);
            if (selected == null) return;
            var chosen = selected;
            int quantity = selectedCount;
            bool asCurrency = selectedAsCurrency;
            Debug.Log("[ExternalLootPickup] Requesting existing loot transaction. lootId=" + sync.lootId + " itemUid=" + chosen.item.UID + " count=" + quantity);
            if (!sync.BeginEdit(chosen.item, () =>
            {
                if (GameManager.Instance == null || !ReferenceEquals(GameManager.Instance.PlayerEntity, entity) ||
                    !ReferenceEquals(entity.Items, player))
                {
                    sync.CompleteEdit();
                    return;
                }
                var current = loot.Items.GetItem(chosen.item.UID);
                if (Eligible(current) && current.stackCount >= quantity)
                {
                    if (asCurrency)
                    {
                        if (IsGold(current) && entity.GoldPieces >= 0 &&
                            (long)entity.GoldPieces + quantity <= int.MaxValue)
                        {
                            if (current.stackCount == quantity) loot.Items.RemoveItem(current);
                            else current.stackCount -= quantity;
                            entity.GoldPieces += quantity;
                            Debug.Log("[ExternalLootPickup] Applied granted gold pickup. lootId=" + sync.lootId + " amount=" + quantity);
                        }
                    }
                    else if (current.stackCount == quantity)
                        player.Transfer(current, loot.Items);
                    else
                    {
                        // Preserve custom save data when splitting instead of Clone().
                        var saved = current.GetSaveData();
                        saved.uid = DaggerfallUnity.NextUID;
                        saved.stackCount = quantity;
                        var split = new ItemCollection();
                        split.DeserializeItems(new[] { saved });
                        var transferred = split.GetItem(0);
                        if (transferred != null)
                        {
                            current.stackCount -= quantity;
                            player.AddItem(transferred);
                        }
                    }
                }
                sync.CompleteEdit();
            }))
                DaggerfallUI.AddHUDText("Loot is busy. Please try that pickup again.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[ExternalLootPickup] Tracking failed: " + ex);
        }
        finally { scanning = false; }
    }
}
