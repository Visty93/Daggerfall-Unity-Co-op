using UnityEngine;
using System;
using System.Collections;
using Mirror;
using DaggerfallWorkshop;

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
    // Local action is frozen from request through commit acknowledgement.
    Action editAction;
    uint editRequest;
    bool editGranted, commitSent;
    int deferredVersion = -1;
    string deferredItems;
    public bool EditPending { get { return editRequest != 0; } }
    public bool EditGranted { get { return editGranted && !commitSent; } }

    public bool BeginEdit(Action action)
    {
        if (EditPending || lootId == 0 || lootCatcher == null)
            return false;
        editAction = action;
        editGranted = commitSent = false;
        editRequest = lootCatcher.RequestLootEdit(lootId, lootVersion);
        lootCatcher.SendLootEditRequest(lootId, lootVersion, editRequest);
        return true;
    }

    public bool ReceiveEditGrant(uint request, bool accepted, int version, string items)
    {
        if (request != editRequest) return false;
        if (!accepted)
        {
            editRequest = 0;
            editAction = null;
            ApplyFinalSnapshot(version, items);
            DaggerfallWorkshop.Game.DaggerfallUI.AddHUDText("Loot changed or is busy. Please try again.");
            return true;
        }
        editGranted = true;
        var action = editAction;
        editAction = null;
        try
        {
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
        return true;
    }

    public void CompleteEdit()
    {
        if (!EditGranted || lootCatcher == null) return;
        commitSent = true;
        lootCatcher.CommitLootEdit(lootId, editRequest, LootCatcher.SerializeLootItems(loot));
    }

    public void ReceiveEditCommit(uint request, int version, string items)
    {
        if (request != editRequest) return;
        editRequest = 0;
        editGranted = commitSent = false;
        editAction = null;
        ApplyFinalSnapshot(version, items);
    }

    public bool DeferSnapshot(int version, string items)
    {
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
        if (sync == null || !sync.IsDroppedNetworkLoot()) return;
        sync.inventoryOpen = true;
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
        if (sync == null || !sync.IsDroppedNetworkLoot()) return false;
        sync.inventoryOpen = false;
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
        while (!inventoryOpen && IsDroppedNetworkLoot() && (lootId == 0 || EditPending))
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
    void Update() { }

    void OnDisable()
    {
        CompleteEdit();
        editAction = null;
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

