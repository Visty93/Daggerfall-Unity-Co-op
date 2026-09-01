using UnityEngine;
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

    void Update()
    {
        if (lootId == 0)
            return;

        if (lootCatcher == null)
            return;

        if (applyingNetworkUpdate || suppressRemovalNotification)
            return;

        if (Time.unscaledTime < suppressContentUpdatesUntil)
            return;

        if (loot == null)
            loot = GetComponent<DaggerfallLoot>();

        if (loot == null)
            return;

        if (Time.unscaledTime < nextContentCheckTime)
            return;

        nextContentCheckTime = Time.unscaledTime + contentCheckInterval;

        // Prevent command spam while waiting for the server to accept/reject the previous change.
        if (pendingContentUpdate)
        {
            if (Time.unscaledTime - pendingUpdateStartedAt < pendingUpdateTimeout)
                return;

            // If no server response came back, allow one retry.
            pendingContentUpdate = false;
        }

        string current = LootCatcher.SerializeLootItems(loot);
        if (current == lastKnownSerializedItems)
            return;

        Debug.Log("[LootMultiplayer] Loot contents changed. lootId=" + lootId + " version=" + lootVersion + " old=" + lastKnownSerializedItems + " new=" + current);

        pendingContentUpdate = true;
        pendingUpdateStartedAt = Time.unscaledTime;

        // Do not update lootVersion locally here. The server increments it and broadcasts back.
        // Do update the local last-known string so this object does not spam every check.
        lastKnownSerializedItems = current;

        lootCatcher.NotifyLootContentsChanged(lootId, lootVersion, current);
    }

    void OnDisable()
    {
        NotifyRemoved();
    }

    void OnDestroy()
    {
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
