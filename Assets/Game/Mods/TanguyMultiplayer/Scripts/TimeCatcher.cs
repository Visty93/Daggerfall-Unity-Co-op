using System.Collections;
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
    static TimeCatcher localInstance;
    static bool postLoadAuthoritativeTimeReady;
    uint lastGameMinutes;
    const float checkIntervalSeconds = 2.25f;
    const float periodicLeaderSyncSeconds = 10f;
    const uint largeLocalJumpMinutes = 40;
    const uint applyDriftThresholdMinutes = 1;

    bool subscribed;
    bool wasLoadInProgress;
    bool observedTimeOption;
    bool routeKnown;
    bool routeNeedsSync;
    bool waitingForFreshSample;
    ulong localParty, localEpoch;
    uint localLeader;
    ulong requestToken, lastDelivery;
    float nextCheckTime, nextPeriodicSyncTime;
    bool alignClassicDiseasesOnNextPostLoadHostTime;

    // These belong to this player's replica on the physical server, not its clock.
    ulong serverParty, serverEpoch, serverToken, serverDelivery;
    uint serverLeader;

    public static bool IsMultiplayerSessionActive
    {
        get { return NetworkServer.active || NetworkClient.isConnected; }
    }

    public static bool IsPostLoadAuthoritativeTimeReady
    {
        get
        {
            if (!OptionsMultiplayer.timeHost || !IsMultiplayerSessionActive)
                return true;
            ulong party, epoch;
            uint leader;
            return localInstance != null && !localInstance.IsLoadInProgress() &&
                postLoadAuthoritativeTimeReady && TryGetLocalRoute(out party, out leader, out epoch) &&
                localInstance.routeKnown && localInstance.localParty == party &&
                localInstance.localLeader == leader && localInstance.localEpoch == epoch;
        }
    }

    // Used ONLY by travel, prison, and vampire transformation. Rest/wait and
    // exhaustion retain their existing local time passage and gameplay effects.
    public static bool ShouldSuppressLocalTimeJump
    {
        get
        {
            if (!OptionsMultiplayer.timeHost || !IsMultiplayerSessionActive)
                return false;
            ulong party, epoch;
            uint leader;
            // During initial membership discovery do not assume local leadership.
            return !TryGetLocalRoute(out party, out leader, out epoch) || leader != GetLocalNetId();
        }
    }

    // Compatibility for callers compiled against the old name. The host can now
    // be a follower and a pure client can now be a leader.
    public static bool IsPureClientUsingHostTime
    {
        get { return ShouldSuppressLocalTimeJump; }
    }

    static uint GetLocalNetId()
    {
        if (NetworkClient.localPlayer != null)
            return NetworkClient.localPlayer.netId;
        return localInstance != null ? localInstance.netId : 0U;
    }

    static bool TryGetLocalRoute(out ulong party, out uint leader, out ulong epoch)
    {
        party = epoch = 0UL;
        leader = 0U;
        uint player = GetLocalNetId();
        if (player == 0U)
            return false;
        if (NetworkServer.active)
            return MultiplayerPartyManager.ServerTryGetPartyTimeRoute(player, out party, out leader, out epoch);
        MultiplayerPartyManager.PartyClientState state;
        if (!MultiplayerPartyManager.TryGetClientPartyState(player, out state) || state == null)
            return false;
        party = state.PartyId;
        leader = state.LeaderNetId;
        epoch = state.TimeEpoch;
        return party != 0UL && leader != 0U && epoch != 0UL;
    }

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        InitializeLocal();
    }

    void Start()
    {
        if (isLocalPlayer)
            InitializeLocal();
    }

    void InitializeLocal()
    {
        if (subscribed || !isLocalPlayer)
            return;
        localInstance = this;
        postLoadAuthoritativeTimeReady = false;
        observedTimeOption = OptionsMultiplayer.timeHost;
        wasLoadInProgress = IsLoadInProgress();
        alignClassicDiseasesOnNextPostLoadHostTime = wasLoadInProgress && ShouldSuppressLocalTimeJump;
        SaveLoadManager.OnStartLoad += HandleStartLoad;
        SaveLoadManager.OnLoad += HandleLoadComplete;
        MultiplayerPartyManager.PartyStateChanged += HandlePartyStateChanged;
        Mirror.NetworkManager.SessionStopped += HandleSessionStopped;
        subscribed = true;
        routeNeedsSync = true;
        nextCheckTime = nextPeriodicSyncTime = 0f;
        InvalidateSample();
    }

    void OnDestroy()
    {
        Unsubscribe();
    }

    public override void OnStopClient()
    {
        Unsubscribe();
        base.OnStopClient();
    }

    void HandleSessionStopped()
    {
        Unsubscribe();
    }

    void Unsubscribe()
    {
        if (subscribed)
        {
            SaveLoadManager.OnStartLoad -= HandleStartLoad;
            SaveLoadManager.OnLoad -= HandleLoadComplete;
            MultiplayerPartyManager.PartyStateChanged -= HandlePartyStateChanged;
            Mirror.NetworkManager.SessionStopped -= HandleSessionStopped;
            subscribed = false;
        }
        if (localInstance == this)
        {
            localInstance = null;
            postLoadAuthoritativeTimeReady = false;
        }
        routeKnown = false;
        serverParty = serverEpoch = serverToken = 0UL;
    }

    bool EnsureWorldTime()
    {
        if (worldTime == null && DaggerfallUnity.Instance != null)
            worldTime = DaggerfallUnity.Instance.WorldTime;
        return worldTime != null;
    }

    bool IsLoadInProgress()
    {
        return SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
    }

    void InvalidateSample()
    {
        requestToken++;
        if (requestToken == 0UL) requestToken++;
        waitingForFreshSample = true;
        lastDelivery = 0UL;
        postLoadAuthoritativeTimeReady = false;
    }

    void RefreshRoute()
    {
        ulong party, epoch;
        uint leader;
        bool known = TryGetLocalRoute(out party, out leader, out epoch);
        if (known == routeKnown && party == localParty && leader == localLeader && epoch == localEpoch)
            return;
        routeKnown = known;
        localParty = party;
        localLeader = leader;
        localEpoch = epoch;
        InvalidateSample();
        routeNeedsSync = true;
        nextCheckTime = 0f;
        // Becoming solo or leader keeps the current clock. It never restores a
        // pre-party date or applies an old leader's cached sample.
    }

    void HandlePartyStateChanged()
    {
        if (!isLocalPlayer || localInstance != this || !OptionsMultiplayer.timeHost)
            return;
        RefreshRoute();
        // Work runs in Update, outside the party snapshot handler.
    }

    void HandleStartLoad(SaveData_v1 saveData)
    {
        wasLoadInProgress = true;
        alignClassicDiseasesOnNextPostLoadHostTime = ShouldSuppressLocalTimeJump;
        InvalidateSample();
        routeNeedsSync = true;
    }

    void HandleLoadComplete(SaveData_v1 saveData)
    {
        wasLoadInProgress = false;
        // A second invalidation also covers a missed OnStartLoad and rejects any
        // response sampled during restoration. Only a new leader reply can reopen readiness.
        InvalidateSample();
        if (OptionsMultiplayer.timeHost) RefreshRoute();
        routeNeedsSync = true;
        SynchronizeNow("load-complete");
    }

    void Update()
    {
        if (!isLocalPlayer || !IsMultiplayerSessionActive)
            return;
        InitializeLocal();
        if (localInstance != this || !EnsureWorldTime())
            return;

        if (observedTimeOption != OptionsMultiplayer.timeHost)
        {
            observedTimeOption = OptionsMultiplayer.timeHost;
            InvalidateSample();
            routeNeedsSync = true;
        }
        if (OptionsMultiplayer.timeHost) RefreshRoute();
        bool loading = IsLoadInProgress();
        if (loading && !wasLoadInProgress)
            HandleStartLoad(default(SaveData_v1));
        else if (!loading && wasLoadInProgress)
            HandleLoadComplete(default(SaveData_v1));
        if (loading)
            return;

        float now = Time.realtimeSinceStartup;
        if (routeNeedsSync)
        {
            SynchronizeNow("initial-or-party-change");
            return;
        }
        if (now < nextCheckTime)
            return;
        nextCheckTime = now + checkIntervalSeconds;
        uint current = GetClassicGameMinutes();
        bool largeJump = current >= lastGameMinutes && current - lastGameMinutes > largeLocalJumpMinutes;
        if (OptionsMultiplayer.timeHost)
        {
            if (waitingForFreshSample || largeJump || now >= nextPeriodicSyncTime)
                SynchronizeNow(largeJump ? "local-time-jump" : "periodic-or-retry");
        }
        else if (largeJump)
        {
            // Preserve the old setting-OFF behavior: a local large jump is shared.
            cmdSendTime(worldTime.Now.ToSeconds());
        }
        lastGameMinutes = GetClassicGameMinutes();
    }

    void SynchronizeNow(string reason)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime() || !NetworkClient.ready)
            return;
        if (OptionsMultiplayer.timeHost) RefreshRoute();
        if (OptionsMultiplayer.timeHost && !routeKnown)
            return; // No host-time fallback while discovering a solo party.
        routeNeedsSync = false;
        nextCheckTime = Time.realtimeSinceStartup + checkIntervalSeconds;
        nextPeriodicSyncTime = Time.realtimeSinceStartup + periodicLeaderSyncSeconds;
        lastGameMinutes = GetClassicGameMinutes();
        if (!OptionsMultiplayer.timeHost)
        {
            cmdSendTime(worldTime.Now.ToSeconds());
            waitingForFreshSample = false;
            MarkAuthoritativeTimeReady(reason + "-legacy");
            return;
        }
        if (localLeader == netId)
        {
            alignClassicDiseasesOnNextPostLoadHostTime = false;
            waitingForFreshSample = false;
            CmdPublishPartyTime(localParty, localEpoch, worldTime.Now.ToSeconds());
            MarkAuthoritativeTimeReady(reason + "-leader");
        }
        else
        {
            CmdRequestPartyTime(localParty, localEpoch, requestToken);
        }
    }

    static TimeCatcher FindServerClock(uint player)
    {
        NetworkIdentity identity;
        return NetworkServer.spawned.TryGetValue(player, out identity) && identity != null
            ? identity.GetComponent<TimeCatcher>() : null;
    }

    bool ServerMatchesRoute(ulong party, ulong epoch, out uint leader)
    {
        ulong currentParty, currentEpoch;
        return MultiplayerPartyManager.ServerTryGetPartyTimeRoute(netId, out currentParty, out leader, out currentEpoch) &&
            currentParty == party && currentEpoch == epoch;
    }

    [Command]
    void CmdPublishPartyTime(ulong party, ulong epoch, ulong seconds)
    {
        uint leader;
        if (!OptionsMultiplayer.timeHost || !ServerMatchesRoute(party, epoch, out leader) || leader != netId)
            return;
        foreach (NetworkConnection connection in MultiplayerPartyManager.ServerGetPartyConnections(netId))
        {
            if (connection == null || !connection.isReady || connection.identity == null || connection.identity.netId == netId)
                continue;
            TimeCatcher follower = connection.identity.GetComponent<TimeCatcher>();
            if (follower != null)
                follower.SendPartySample(party, epoch, netId, follower.serverToken, seconds, false);
        }
    }

    [Command]
    void CmdRequestPartyTime(ulong party, ulong epoch, ulong token)
    {
        uint leader;
        if (!OptionsMultiplayer.timeHost || token == 0UL ||
            !ServerMatchesRoute(party, epoch, out leader) || leader == netId)
            return;
        serverParty = party;
        serverEpoch = epoch;
        serverLeader = leader;
        serverToken = token;
        TimeCatcher source = FindServerClock(leader);
        if (source != null && source.connectionToClient != null && source.connectionToClient.isReady)
            source.TargetCapturePartyTime(source.connectionToClient, netId, party, epoch, token);
    }

    [TargetRpc]
    void TargetCapturePartyTime(NetworkConnection target, uint requester, ulong party, ulong epoch, ulong token)
    {
        if (!isLocalPlayer || !OptionsMultiplayer.timeHost || IsLoadInProgress() || !EnsureWorldTime())
            return;
        InitializeLocal();
        RefreshRoute();
        if (!routeKnown || localParty != party || localEpoch != epoch || localLeader != netId)
            return;
        CmdReplyPartyTime(requester, party, epoch, token, worldTime.Now.ToSeconds());
    }

    [Command]
    void CmdReplyPartyTime(uint requester, ulong party, ulong epoch, ulong token, ulong seconds)
    {
        uint leader;
        if (!OptionsMultiplayer.timeHost || !ServerMatchesRoute(party, epoch, out leader) || leader != netId)
            return;
        TimeCatcher follower = FindServerClock(requester);
        if (follower != null && requester != netId)
            follower.SendPartySample(party, epoch, netId, token, seconds, true);
    }

    void SendPartySample(ulong party, ulong epoch, uint leader, ulong token, ulong seconds, bool freshReply)
    {
        uint currentLeader;
        if (!NetworkServer.active || token == 0UL || serverToken != token || serverParty != party ||
            serverEpoch != epoch || serverLeader != leader || !ServerMatchesRoute(party, epoch, out currentLeader) ||
            currentLeader != leader || connectionToClient == null || !connectionToClient.isReady)
            return;
        serverDelivery++;
        TargetApplyPartyTime(connectionToClient, party, epoch, leader, token, serverDelivery, seconds, freshReply);
    }

    [TargetRpc]
    void TargetApplyPartyTime(NetworkConnection target, ulong party, ulong epoch, uint leader,
        ulong token, ulong delivery, ulong seconds, bool freshReply)
    {
        if (!isLocalPlayer || !OptionsMultiplayer.timeHost || IsLoadInProgress() || !EnsureWorldTime())
            return;
        InitializeLocal();
        RefreshRoute();
        if (!routeKnown || localParty != party || localEpoch != epoch || localLeader != leader ||
            leader == netId || token != requestToken || delivery <= lastDelivery ||
            (waitingForFreshSample && !freshReply))
            return;
        lastDelivery = delivery;
        waitingForFreshSample = false;
        ApplySyncedTime(seconds, "party-leader-" + leader);
    }

    // Keep the public legacy entry points for existing compiled callers, but
    // never permit them to broadcast global time while party-leader time is ON.
    [Command]
    public void cmdSendTime(ulong seconds)
    {
        if (!OptionsMultiplayer.timeHost)
            rpcSendTime(seconds);
    }

    [Command]
    public void cmdReceiveTime()
    {
        if (!OptionsMultiplayer.timeHost && EnsureWorldTime())
            receiveTime(worldTime.Now.ToSeconds());
    }

    [ClientRpc]
    void receiveTime(ulong seconds)
    {
        if (!OptionsMultiplayer.timeHost && isLocalPlayer && EnsureWorldTime())
            ApplySyncedTime(seconds, "legacy-receiveTime");
    }

    [ClientRpc]
    public void rpcSendTime(ulong seconds)
    {
        if (!OptionsMultiplayer.timeHost && !isLocalPlayer && localInstance != null && localInstance.EnsureWorldTime())
            localInstance.ApplySyncedTime(seconds, "legacy-rpcSendTime");
    }

    void MarkAuthoritativeTimeReady(string reason)
    {
        if (IsLoadInProgress())
            return;
        if (!postLoadAuthoritativeTimeReady && Debug.isDebugBuild)
            Debug.Log("[PartyTime] Ready. reason=" + reason + " party=" + localParty +
                " leader=" + localLeader + " minutes=" + GetClassicGameMinutes());
        postLoadAuthoritativeTimeReady = true;
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

        if (!EnsureWorldTime())
            return;

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

        // PlayerEntity has its own per-minute accumulator. When leader time pulls a follower
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
