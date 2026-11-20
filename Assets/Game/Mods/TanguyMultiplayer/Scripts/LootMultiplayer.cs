using UnityEngine;
using System;
using System.Collections;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Items;

public class LootMultiplayer : MonoBehaviour
{
    public LootCatcher lootCatcher;

    [Header("Network loot state")]
    public uint lootId = 0;
    public int lootVersion = 0;

    public float contentCheckInterval = 0.35f;
    public float pendingUpdateTimeout = 2.0f;

    bool removalAlreadyReported = false;
    bool suppressRemovalNotification = false;
    bool applyingNetworkUpdate = false;
    bool pendingContentUpdate = false;

    string lastKnownSerializedItems = string.Empty;
    float nextContentCheckTime = 0f;
    float pendingUpdateStartedAt = 0f;
    float suppressContentUpdatesUntil = 0f;

    DaggerfallLoot loot;
    ExternalLootPickupTracker externalPickups;
    float nextExternalPickupCheck;
    ExternalLootPickupTracker ExternalPickups
    {
        get { return externalPickups ?? (externalPickups = new ExternalLootPickupTracker(this)); }
    }
    PartyQuestLootOverlay.LootEditToken partyQuestLootEditToken;
    DaggerfallUnityItem partyQuestLootClickedItem;
    // Local action is frozen from request through commit acknowledgement.
    Action editAction;
    uint editRequest;
    bool editGranted, commitSent;
    // Keep the exact proposed commit until the server acknowledges it.
    // Retrying the same request cannot transfer an item twice: server deduplicates.
    string pendingCommitItems;
    string[] pendingCommitConsumed;
    string externalQuestConsumption;
    int deferredVersion = -1;
    string deferredItems;
    public bool EditPending { get { return editRequest != 0; } }
    public bool EditGranted { get { return editGranted && !commitSent; } }

    // Receipt for a completed external quest pickup. Never executes a transfer,
    // quest click, or Begin/EndLocalLootEdit. Uses the existing server reservation.
    internal bool BeginExternalQuestConsumption(string symbol)
    {
        if (EditGranted) CompleteEdit(); // A new pickup resolves a granted but unfinished edit.
        if (!ExternalLootPickupTracker.Enabled || EditPending || !isActiveAndEnabled ||
            lootId == 0 || lootCatcher == null || !lootCatcher.isLocalPlayer ||
            string.IsNullOrEmpty(symbol) || symbol == "*" || symbol == "!" ||
            !PartyQuestLootOverlay.ExternalConsumptionStillValid(lootId, symbol)) return false;
        ExternalPickups.Poll(false);
        externalQuestConsumption = symbol;
        editAction = null;
        editGranted = commitSent = false;
        editRequest = lootCatcher.RequestLootEdit(lootId, lootVersion);
        Debug.Log("[ExternalQuestPickup] Request consumption lootId=" + lootId + " symbol=" + symbol);
        try { lootCatcher.SendLootEditRequest(lootId, lootVersion, editRequest, symbol); }
        catch (Exception ex)
        {
            Debug.LogError("[LootWire] Could not begin external quest pickup: " + ex);
            lootCatcher.CancelUnexecutedLootEdit(lootId, editRequest);
            editRequest = 0;
            externalQuestConsumption = null;
            return false;
        }
        return true;
    }

    bool ReceiveExternalQuestGrant(bool accepted, int version, string items)
    {
        if (externalQuestConsumption == null) return false;
        ExternalPickups.Poll(false);
        if (!accepted)
        {
            editRequest = 0;
            editGranted = commitSent = false;
            pendingCommitItems = null;
            pendingCommitConsumed = null;
            externalQuestConsumption = null;
            ApplyFinalSnapshot(version, items);
            Debug.Log("[ExternalQuestPickup] Consumption deferred; awaiting state or retry. lootId=" + lootId);
            return true;
        }
        bool valid = isActiveAndEnabled &&
            PartyQuestLootOverlay.ExternalConsumptionStillValid(lootId, externalQuestConsumption);
        // Return the server's ordinary contents unchanged. Only the reserved quest
        // symbol is consumed; no unrelated local inventory changes are published.
        BeginReliableCommit(items, valid ? new[] { externalQuestConsumption } : null);
        Debug.Log("[ExternalQuestPickup] Consumption commit lootId=" + lootId + " valid=" + valid);
        return true;
    }

    public bool BeginEdit(Action action)
    {
        return BeginEdit(null, action);
    }

    // Optional exact clicked item is used only for party-scoped corpse quest items.
    // All ordinary loot keeps the original transaction path unchanged.
    public bool BeginEdit(DaggerfallUnityItem clickedItem, Action action)
    {
        ExternalPickups.Poll(false);
        // A canceled split/quantity popup may have left an approved edit open.
        // Resolve it from the current real inventory when the user attempts another
        // action, without a timeout and without re-executing either transfer.
        if (EditGranted) CompleteEdit();
        if (EditPending || lootId == 0 || lootCatcher == null)
            return false;
        string partySelection = PartyQuestLootOverlay.Selection(loot, clickedItem);
        if (partySelection == "!")
        {
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Quest loot is synchronizing. Please try again.");
            return false;
        }
        editAction = action;
        partyQuestLootClickedItem = clickedItem;
        editGranted = commitSent = false;
        editRequest = lootCatcher.RequestLootEdit(lootId, lootVersion);
        try
        {
            lootCatcher.SendLootEditRequest(lootId, lootVersion, editRequest, partySelection);
        }
        catch (Exception ex)
        {
            Debug.LogError("[LootWire] Could not begin inventory edit: " + ex);
            lootCatcher.CancelUnexecutedLootEdit(lootId, editRequest);
            editRequest = 0;
            editAction = null;
            partyQuestLootClickedItem = null;
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Loot could not synchronize. See Player.log.");
            return false;
        }
        return true;
    }

    // Used only after a callback fails before executing its permitted action.
    // Do not touch requests which have already moved an item locally.
    public void ClearUnexecutedRequest(uint request)
    {
        if (request != editRequest || editGranted || commitSent) return;
        editRequest = 0;
        editAction = null;
        partyQuestLootClickedItem = null;
        partyQuestLootEditToken = null;
        externalQuestConsumption = null;
    }

    public bool ReceiveEditGrant(uint request, bool accepted, int version, string items)
    {
        if (request != editRequest) return false;
        if (ReceiveExternalQuestGrant(accepted, version, items)) return true;
        ExternalPickups.Poll(false);
        if (!accepted)
        {
            editRequest = 0;
            editGranted = commitSent = false;
            pendingCommitItems = null;
            pendingCommitConsumed = null;
            editAction = null;
            partyQuestLootEditToken = null;
            partyQuestLootClickedItem = null;
            ApplyFinalSnapshot(version, items);
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Loot changed or is busy. Please try again.");
            return true;
        }
        PartyQuestLootOverlay.ReconcileBeforeGrantedEdit(loot);
        editGranted = true;
        partyQuestLootEditToken = PartyQuestLootOverlay.BeginLocalLootEdit(loot, partyQuestLootClickedItem);
        partyQuestLootClickedItem = null;
        var action = editAction;
        editAction = null;
        try
        {
            ExternalPickups.InApprovedAction = true;
            if (isActiveAndEnabled && action != null) action();
            else CompleteEdit();
        }
        catch (Exception ex)
        {
            // Publish any native changes already made, rather than unlocking with
            // an old snapshot that would duplicate an item already transferred.
            Debug.LogError("[LootTransaction] Inventory action failed: " + ex);
            CompleteEdit();
        }
        finally
        {
            ExternalPickups.InApprovedAction = false;
            ExternalPickups.Remember(false);
        }
        return true;
    }

    public void CompleteEdit()
    {
        if (!EditGranted || lootCatcher == null) return;

        string[] partyConsumed = PartyQuestLootOverlay.EndLocalLootEdit(loot, partyQuestLootEditToken);
        partyQuestLootEditToken = null;
        partyQuestLootClickedItem = null;

        // Also covers ordinary inventory actions completing after a split popup.
        ExternalPickups.Remember(false);
        try
        {
            BeginReliableCommit(LootCatcher.SerializeLootItems(loot), partyConsumed);
        }
        catch (Exception ex)
        {
            // Never silently cancel after a local transfer: that could duplicate
            // the item. Report failure, but do not prevent closing the loot window.
            commitSent = true;
            Debug.LogError("[LootTransaction] Unable to encode edited loot; reconciliation required: " + ex);
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Loot serialization failed. Close the inventory if needed; check Player.log.");
        }
    }

    void BeginReliableCommit(string items, string[] consumed)
    {
        pendingCommitItems = items ?? string.Empty;
        pendingCommitConsumed = consumed;
        commitSent = true;
        // LootCatcher owns the exact pending transfer until acknowledged, even
        // if this loot marker is unloaded or this inventory window is closed.
        RetryCommit();
    }

    void RetryCommit()
    {
        if (!commitSent || editRequest == 0 || lootCatcher == null ||
            !lootCatcher.isLocalPlayer || pendingCommitItems == null) return;
        try
        {
            // The catcher queues and retries the SAME serialized proposal. Never
            // re-execute the UI action: that would duplicate a player inventory transfer.
            lootCatcher.CommitLootEdit(lootId, editRequest, pendingCommitItems, pendingCommitConsumed);
        }
        catch (Exception ex)
        {
            Debug.LogError("[LootTransaction] Could not queue commit: " + ex);
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Loot transfer pending. You may close the inventory.");
        }
    }

    public void ReceiveEditCommit(uint request, int version, string items)
    {
        if (request != editRequest) return;
        ExternalPickups.Poll(false);
        editRequest = 0;
        externalQuestConsumption = null;
        editGranted = commitSent = false;
        pendingCommitItems = null;
        pendingCommitConsumed = null;
        editAction = null;
        partyQuestLootEditToken = null;
        partyQuestLootClickedItem = null;
        ApplyFinalSnapshot(version, items);
    }

    public bool DeferSnapshot(int version, string items)
    {
        // Catch speculative transfers before an incoming snapshot erases evidence.
        ExternalPickups.Poll(false);
        if (!EditPending) return false;
        if (version >= deferredVersion)
        {
            deferredVersion = version;
            deferredItems = items;
        }
        return true;
    }

    void ApplyFinalSnapshot(int version, string items)
    {
        if (deferredVersion > version) { version = deferredVersion; items = deferredItems; }
        deferredVersion = -1;
        deferredItems = null;
        if (version >= 0 && lootCatcher != null)
            lootCatcher.ApplyLootEditSnapshot(lootId, version, items);
    }

    bool inventoryOpen;
    uint readerLootId;
    LootCatcher readerCatcher;
    Coroutine closeRoutine;

    // Called only by inventory open/close. No changes to the item transfer code.
    public static void InventoryOpened(DaggerfallLoot target)
    {
        if (target == null) return;
        var sync = target.GetComponent<LootMultiplayer>();
        if (sync == null) return;
        sync.inventoryOpen = true;
        if (!sync.IsDroppedNetworkLoot()) return;
        if (sync.closeRoutine != null)
        {
            sync.StopCoroutine(sync.closeRoutine);
            sync.closeRoutine = null;
        }
        sync.EnsureReader();
    }

    // True means the server owns empty-container cleanup for this target.
    public static bool InventoryClosed(DaggerfallLoot target)
    {
        if (target == null) return false;
        var sync = target.GetComponent<LootMultiplayer>();
        if (sync == null) return false;
        sync.inventoryOpen = false;
        // Closing the window cancels ONLY permission that has not yet been used.
        // Never cancel or roll back an executed transfer; its commit continues via catcher.
        if (sync.EditPending && !sync.editGranted && !sync.commitSent && sync.lootCatcher != null)
        {
            sync.lootCatcher.CancelUnexecutedLootEdit(sync.lootId, sync.editRequest);
            sync.editRequest = 0;
            sync.editAction = null;
            sync.partyQuestLootClickedItem = null;
            sync.externalQuestConsumption = null;
        }
        else if (sync.EditGranted)
            sync.CompleteEdit();
        if (!sync.IsDroppedNetworkLoot()) return false;
        if (sync.closeRoutine == null && sync.isActiveAndEnabled)
            sync.closeRoutine = sync.StartCoroutine(sync.CloseAfterContents());
        return true;
    }

    bool IsDroppedNetworkLoot()
    {
        if (!NetworkClient.active && !NetworkServer.active) return false;
        if (loot == null) loot = GetComponent<DaggerfallLoot>();
        return loot != null && loot.ContainerType == LootContainerTypes.DroppedLoot &&
            !LootCatcher.IsCorpseLootId(lootId) && lootCatcher != null;
    }

    void EnsureReader()
    {
        if (readerLootId != 0 || lootId == 0 || PlayerMultiplayer.localPlayer == null)
            return;
        var catcher = PlayerMultiplayer.localPlayer.GetComponent<LootCatcher>();
        if (catcher == null || !catcher.isLocalPlayer) return;
        readerCatcher = catcher;
        readerLootId = lootId;
        readerCatcher.SetDroppedLootOpen(readerLootId, true);
    }

    void ReleaseReader()
    {
        uint id = readerLootId;
        var catcher = readerCatcher;
        readerLootId = 0;
        readerCatcher = null;
        if (id != 0 && catcher != null && NetworkClient.active)
            catcher.SetDroppedLootOpen(id, false);
    }

    IEnumerator CloseAfterContents()
    {
        yield return null;
        if (!inventoryOpen) ReleaseReader();
        closeRoutine = null;
    }

    void Awake()
    {
        loot = GetComponent<DaggerfallLoot>();
    }

    void Start()
    {
        if (loot == null)
            loot = GetComponent<DaggerfallLoot>();

        if (lootId != 0 && string.IsNullOrEmpty(lastKnownSerializedItems))
            lastKnownSerializedItems = LootCatcher.SerializeLootItems(loot);
    }

    public void ConfigureNetworkLoot(uint newLootId, int newVersion, string serializedItems)
    {
        lootId = newLootId;
        lootVersion = newVersion;
        if (inventoryOpen || closeRoutine != null) EnsureReader();
        lastKnownSerializedItems = serializedItems ?? string.Empty;
        pendingContentUpdate = false;

        if (loot == null)
            loot = GetComponent<DaggerfallLoot>();
        if (!applyingNetworkUpdate) ExternalPickups.Remember(false);
    }

    public void BeginNetworkApply()
    {
        applyingNetworkUpdate = true;
    }

    public void EndNetworkApply()
    {
        applyingNetworkUpdate = false;
        pendingContentUpdate = false;
        suppressContentUpdatesUntil = 0f;

        if (loot == null)
            loot = GetComponent<DaggerfallLoot>();

        lastKnownSerializedItems = LootCatcher.SerializeLootItems(loot);
        ExternalPickups.Remember(true);
    }

    public void SuppressRemovalNotification()
    {
        suppressRemovalNotification = true;
    }

    public void SuppressContentUpdatesFor(float seconds)
    {
        suppressContentUpdatesUntil = Mathf.Max(suppressContentUpdatesUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
    }

    // Inventory changes are sent only by an acknowledged edit. Observers must
    // never echo an incoming snapshot as a new local inventory operation.
    // Commit retries are owned by the persistent player LootCatcher, not by the
    // temporary corpse/pile MonoBehaviour, and do not depend on the inventory UI.

    void LateUpdate()
    {
        if (!ExternalLootPickupTracker.Enabled) return;
        if (applyingNetworkUpdate || Time.unscaledTime < suppressContentUpdatesUntil ||
            Time.unscaledTime < nextExternalPickupCheck || !NetworkClient.active || lootId == 0)
            return;
        nextExternalPickupCheck = Time.unscaledTime + 0.1f;
        var manager = DaggerfallWorkshop.Game.GameManager.Instance;
        if (manager == null) return;
        var player = manager.PlayerObject;
        if (player == null || (player.transform.position - transform.position).sqrMagnitude > 64f)
            return;
        ExternalPickups.Poll(true);
    }

    void OnDisable()
    {
        if (externalPickups != null) externalPickups.Poll(false);
        // If permission has not arrived, NO local item transfer was authorized.
        // Cancel that reservation explicitly (no clock-based ownership stealing).
        if (EditPending && !editGranted && !commitSent && lootCatcher != null)
        {
            lootCatcher.CancelUnexecutedLootEdit(lootId, editRequest);
            editRequest = 0;
        }
        // If an action was already granted, publish its actual resulting contents.
        // A post-action commit stays with LootCatcher even when this object disappears.
        CompleteEdit();
        editAction = null;
        partyQuestLootEditToken = null;
        partyQuestLootClickedItem = null;
        inventoryOpen = false;
        if (closeRoutine != null) { StopCoroutine(closeRoutine); closeRoutine = null; }
        ReleaseReader();
        NotifyRemoved();
    }

    void OnDestroy()
    {
        ReleaseReader();
        NotifyRemoved();
    }

    void NotifyRemoved()
    {
        if (suppressRemovalNotification)
            return;

        if (removalAlreadyReported)
            return;

        removalAlreadyReported = true;

        if (lootCatcher == null)
        {
            Debug.LogWarning("[LootMultiplayer] Loot removed, but lootCatcher is null. Disable sync skipped.");
            return;
        }

        Debug.Log("[LootMultiplayer] Loot removed. Scheduling disable sync. lootId=" + lootId);
        lootCatcher.NotifyLootRemovedDelayed(lootId, transform.position);
    }
}


