using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.MagicAndEffects;
using Mirror;

public class TimeCatcher : NetworkBehaviour
{
    WorldTime worldTime;
    static uint lastGameMinutes;

    // Current behaviour syncs after a large local time jump (>40 minutes), on a host
    // heartbeat, and immediately after connect/load. QuestNetSync uses the readiness
    // flag below to avoid importing a timed quest before authoritative host time arrives.
    const float checkIntervalSeconds = 2.25f;
    const float periodicHostSyncSeconds = 10f;
    const uint largeLocalJumpMinutes = 40;
    const uint applyDriftThresholdMinutes = 1;

    static bool postLoadAuthoritativeTimeReady = false;

    float nextPeriodicSyncTime = 0f;
    bool wasLoadInProgress = false;
    bool subscribedToSaveLoadEvents = false;

    // Set only when a pure client actually starts loading a save while host-authoritative
    // time is active. The next accepted host-time sample rebases ordinary classic disease
    // day counters once, then this flag is consumed. Initial connection and periodic time
    // packets never set it, so ordinary disease progression remains intact.
    bool alignClassicDiseasesOnNextPostLoadHostTime = false;

    // This is deliberately acknowledgement-based rather than drift-based. A one-minute
    // difference is harmless; the important point is that host time has been received
    // after the current load. Non-host-authoritative mode never needs this gate.
    public static bool IsPostLoadAuthoritativeTimeReady
    {
        get { return !OptionsMultiplayer.timeHost || postLoadAuthoritativeTimeReady; }
    }

    public static bool IsMultiplayerSessionActive
    {
        get { return NetworkServer.active || NetworkClient.isConnected; }
    }

    // A listen-server host is also a Mirror client, so NetworkClient.isConnected alone
    // is not enough. This is the shared policy for local-only behaviour that must be
    // suppressed when the host owns world time.
    public static bool IsPureClientUsingHostTime
    {
        get
        {
            return OptionsMultiplayer.timeHost &&
                   NetworkClient.isConnected &&
                   !NetworkServer.active;
        }
    }

    void Start()
    {
        init();
    }

    void init()
    {
        worldTime = GameObject.Find("DaggerfallUnity").GetComponent<WorldTime>();

        if (isLocalPlayer)
        {
            SubscribeToSaveLoadEvents();
            StartCoroutine(Check());
        }
    }

    void OnDestroy()
    {
        UnsubscribeFromSaveLoadEvents();
    }

    void SubscribeToSaveLoadEvents()
    {
        if (subscribedToSaveLoadEvents)
            return;

        SaveLoadManager.OnStartLoad += HandleStartLoad;
        SaveLoadManager.OnLoad += HandleLoadComplete;
        subscribedToSaveLoadEvents = true;
    }

    void UnsubscribeFromSaveLoadEvents()
    {
        if (!subscribedToSaveLoadEvents)
            return;

        SaveLoadManager.OnStartLoad -= HandleStartLoad;
        SaveLoadManager.OnLoad -= HandleLoadComplete;
        subscribedToSaveLoadEvents = false;
    }

    void HandleStartLoad(SaveData_v1 saveData)
    {
        wasLoadInProgress = true;
        alignClassicDiseasesOnNextPostLoadHostTime = IsPureClientUsingHostTime;
        MarkAuthoritativeTimePending("load-start");
    }

    void HandleLoadComplete(SaveData_v1 saveData)
    {
        wasLoadInProgress = false;
        SynchronizeImmediatelyAfterLoad("load-complete-event");
    }

    IEnumerator Check()
    {
        // Wait one frame so Mirror ownership/world objects have settled, then do an
        // immediate synchronization. Pure clients stay gated until a host response arrives.
        yield return null;

        lastGameMinutes = GetClassicGameMinutes();
        wasLoadInProgress = IsLoadInProgress();
        nextPeriodicSyncTime = Time.realtimeSinceStartup + periodicHostSyncSeconds;

        if (wasLoadInProgress)
        {
            // Do not apply an in-flight host packet while SaveLoadManager is still
            // restoring this save. OnLoad will request again immediately.
            MarkAuthoritativeTimePending("initial-load-in-progress");
            alignClassicDiseasesOnNextPostLoadHostTime = IsPureClientUsingHostTime;
        }
        else
        {
            if (OptionsMultiplayer.timeHost && !isServer)
                MarkAuthoritativeTimePending("initial-client-sync");

            RequestOrBroadcastCurrentTime("initial");
        }

        while (true)
        {
            bool loadInProgress = IsLoadInProgress();

            // Event callbacks are the normal path. Keep this edge detector only as a
            // fallback for unusual lifecycle ordering where the local component missed
            // OnStartLoad or OnLoad while it was being created/re-enabled.
            if (loadInProgress && !wasLoadInProgress)
            {
                wasLoadInProgress = true;
                alignClassicDiseasesOnNextPostLoadHostTime = IsPureClientUsingHostTime;
                MarkAuthoritativeTimePending("load-start-poll-fallback");
            }
            else if (!loadInProgress && wasLoadInProgress)
            {
                wasLoadInProgress = false;
                SynchronizeImmediatelyAfterLoad("load-complete-poll-fallback");
            }

            if (loadInProgress)
            {
                yield return new WaitForSeconds(checkIntervalSeconds);
                continue;
            }

            uint currentGameMinutes = GetClassicGameMinutes();

            // Do not use unsigned subtraction here. If host time sync moves the local
            // clock backwards, uint/ulong subtraction underflows and this check fires forever.
            if (currentGameMinutes >= lastGameMinutes && currentGameMinutes - lastGameMinutes > largeLocalJumpMinutes)
                RequestOrBroadcastCurrentTime("large-local-jump");

            // Only the host's local player broadcasts periodically in host-authoritative mode.
            // Clients request immediately on connect/load or after a large local jump.
            if (OptionsMultiplayer.timeHost && isServer && Time.realtimeSinceStartup >= nextPeriodicSyncTime)
            {
                SendCurrentTimeToOthers("periodic-host");
                nextPeriodicSyncTime = Time.realtimeSinceStartup + periodicHostSyncSeconds;
            }

            lastGameMinutes = GetClassicGameMinutes();
            yield return new WaitForSeconds(checkIntervalSeconds);
        }
    }

    void SynchronizeImmediatelyAfterLoad(string reason)
    {
        lastGameMinutes = GetClassicGameMinutes();
        nextPeriodicSyncTime = Time.realtimeSinceStartup + periodicHostSyncSeconds;

        if (OptionsMultiplayer.timeHost)
            MarkAuthoritativeTimePending(reason + "-requesting");

        // No extra frame or 0.5-second delay: SaveLoadManager raises OnLoad only after
        // restoring save data and clearing LoadInProgress.
        RequestOrBroadcastCurrentTime(reason);
    }

    bool IsLoadInProgress()
    {
        return SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
    }

    static void MarkAuthoritativeTimePending(string reason)
    {
        if (!OptionsMultiplayer.timeHost)
        {
            postLoadAuthoritativeTimeReady = true;
            return;
        }

        postLoadAuthoritativeTimeReady = false;
        if (Debug.isDebugBuild)
            Debug.Log("[TimeCatcher] Authoritative time pending. reason=" + reason);
    }

    void MarkAuthoritativeTimeReady(string reason)
    {
        // A periodic packet can arrive while SaveLoadManager is still restoring the save.
        // Do not let that old/in-flight packet reopen quest sharing during the load.
        if (IsLoadInProgress())
            return;

        if (!postLoadAuthoritativeTimeReady && Debug.isDebugBuild)
            Debug.Log("[TimeCatcher] Authoritative time ready. reason=" + reason + " minutes=" + GetClassicGameMinutes());

        postLoadAuthoritativeTimeReady = true;
    }

    void RequestOrBroadcastCurrentTime(string reason)
    {
        if (worldTime == null)
            worldTime = GameObject.Find("DaggerfallUnity").GetComponent<WorldTime>();

        // Host-authoritative mode: clients request host time. The host's loaded local
        // clock is authoritative, so it can become ready as soon as it broadcasts.
        if (OptionsMultiplayer.timeHost && !isServer)
        {
            Debug.Log($"[TimeCatcher] Requesting host time. reason={reason} localMinutes={GetClassicGameMinutes()}");
            cmdReceiveTime();
            return;
        }

        SendCurrentTimeToOthers(reason);
        MarkAuthoritativeTimeReady(reason + "-local-authority");
    }

    void SendCurrentTimeToOthers(string reason)
    {
        if (worldTime == null)
            worldTime = GameObject.Find("DaggerfallUnity").GetComponent<WorldTime>();

        DaggerfallDateTime now = worldTime.Now;
        Debug.Log($"[TimeCatcher] Sending time. reason={reason} minutes={now.ToClassicDaggerfallTime()} seconds={now.ToSeconds()} isServer={isServer}");
        cmdSendTime(now.ToSeconds());
    }

    [Command]
    public void cmdSendTime(ulong i/*int year, int month, int day, int hour, int minute, float second*/)
    {
        rpcSendTime(i/*year, month, day, hour, minute, second*/);
    }

    [Command]
    public void cmdReceiveTime()
    {
        DaggerfallDateTime now = worldTime.Now;
        receiveTime(now.ToSeconds()/*now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second*/);
    }

    [ClientRpc]
    void receiveTime(ulong i/*int year, int month, int day, int hour, int minute, float second*/)
    {
        if (isLocalPlayer)
            ApplySyncedTime(i, "receiveTime");
    }

    [ClientRpc]
    public void rpcSendTime(ulong i/*int year, int month, int day, int hour, int minute, float second*/)
    {
        if (!isLocalPlayer)
            ApplySyncedTime(i, "rpcSendTime");
    }

    uint GetClassicGameMinutes()
    {
        if (worldTime == null)
            return 0;

        return worldTime.Now.ToClassicDaggerfallTime();
    }

    void ApplySyncedTime(ulong seconds, string source)
    {
        // Ignore old/in-flight network time while a save is still being restored. The
        // precise OnLoad callback requests a fresh authoritative sample immediately.
        if (IsLoadInProgress())
        {
            if (Debug.isDebugBuild)
                Debug.Log("[TimeCatcher] Ignored synced time during active load. source=" + source);
            return;
        }

        if (worldTime == null)
            worldTime = GameObject.Find("DaggerfallUnity").GetComponent<WorldTime>();

        uint beforeMinutes = worldTime.Now.ToClassicDaggerfallTime();

        DaggerfallDateTime incoming = new DaggerfallDateTime();
        incoming.FromSeconds(seconds);
        uint syncedMinutes = incoming.ToClassicDaggerfallTime();

        bool movedBackwards = syncedMinutes < beforeMinutes;
        uint drift = (beforeMinutes >= syncedMinutes) ? beforeMinutes - syncedMinutes : syncedMinutes - beforeMinutes;
        if (drift <= applyDriftThresholdMinutes)
        {
            // Acknowledgement still completes the post-load gate even when the drift is
            // intentionally too small to snap WorldTime.
            lastGameMinutes = syncedMinutes;

            if (movedBackwards && GameManager.Instance != null && GameManager.Instance.EntityEffectBroker != null)
                GameManager.Instance.EntityEffectBroker.AlignMagicRoundTimerToCurrentTime($"TimeCatcher-{source}-tiny-backward-sync");

            AlignClassicDiseasesAfterLoadedClientTime(source, beforeMinutes, syncedMinutes);

            MarkAuthoritativeTimeReady(source + "-tiny-drift");
            return;
        }

        worldTime.Now.FromSeconds(seconds);

        syncedMinutes = worldTime.Now.ToClassicDaggerfallTime();
        lastGameMinutes = syncedMinutes;

        // PlayerEntity has its own per-minute accumulator. When host time pulls a client
        // backwards, it must accept the host minute as the new baseline.
        if (GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
            GameManager.Instance.PlayerEntity.LastGameMinutes = syncedMinutes;

        // Also discard EntityEffectBroker's future local baseline after backwards sync.
        if (movedBackwards && GameManager.Instance != null && GameManager.Instance.EntityEffectBroker != null)
            GameManager.Instance.EntityEffectBroker.AlignMagicRoundTimerToCurrentTime($"TimeCatcher-{source}-backward-sync");

        AlignClassicDiseasesAfterLoadedClientTime(source, beforeMinutes, syncedMinutes);

        Debug.Log($"[TimeCatcher] Applied synced time from {source}. beforeMinutes={beforeMinutes} syncedMinutes={syncedMinutes} driftMinutes={drift} seconds={seconds} movedBackwards={movedBackwards}");
        MarkAuthoritativeTimeReady(source);
    }

    void AlignClassicDiseasesAfterLoadedClientTime(string source, uint beforeMinutes, uint syncedMinutes)
    {
        if (!alignClassicDiseasesOnNextPostLoadHostTime)
            return;

        // Consume first so an exception or missing manager cannot turn every later periodic
        // synchronization into a disease rebase.
        alignClassicDiseasesOnNextPostLoadHostTime = false;

        if (!IsPureClientUsingHostTime)
            return;

        EntityEffectManager effectManager = null;
        if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
            effectManager = GameManager.Instance.PlayerObject.GetComponent<EntityEffectManager>();

        if (effectManager == null)
        {
            Debug.LogWarning("[TimeCatcher] Could not align classic diseases after client load: PlayerEffectManager is missing.");
            return;
        }

        int aligned = effectManager.AlignClassicDiseaseTimersToCurrentTime(
            "mp-client-post-load-host-time-" + source);

        Debug.Log(
            $"[TimeCatcher] Consumed client-load disease realignment. source={source} beforeMinutes={beforeMinutes} syncedMinutes={syncedMinutes} alignedClassicDiseases={aligned}");
    }
}
