using System.Collections;
using UnityEngine;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using Mirror;

public class TimeCatcher : NetworkBehaviour
{
    WorldTime worldTime;
    static TimeCatcher localInstance;
    static bool postLoadAuthoritativeTimeReady;
    ulong lastObservedSeconds;
    ulong proposalSerial, pendingProposal, pendingForwardSeconds;
    const float checkIntervalSeconds = 2.25f;
    const float periodicLeaderSyncSeconds = 10f;
    const ulong localJumpThresholdSeconds = 60;
    const uint applyDriftThresholdMinutes = 1;

    bool subscribed;
    bool wasLoadInProgress;
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
            if (!IsMultiplayerSessionActive)
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
            if (!IsMultiplayerSessionActive || !LeaderControlsTime)
                return false;
            ulong party, epoch;
            uint leader;
            // During initial membership discovery do not assume local leadership.
            return !TryGetLocalRoute(out party, out leader, out epoch) || leader != GetLocalNetId();
        }
    }

    static bool LeaderControlsTime
    {
        get
        {
            bool enabled;
            return !MultiplayerPartyManager.TryGetLocalLeaderControlsTime(out enabled) || enabled;
        }
    }

    static bool IsPartyFollower
    {
        get
        {
            ulong party, epoch;
            uint leader;
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
        wasLoadInProgress = IsLoadInProgress();
        alignClassicDiseasesOnNextPostLoadHostTime = wasLoadInProgress && IsPartyFollower;
        SaveLoadManager.OnStartLoad += HandleStartLoad;
        SaveLoadManager.OnLoad += HandleLoadComplete;
        DaggerfallTravelPopUp.OnPostFastTravel += HandlePostFastTravel;
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
            DaggerfallTravelPopUp.OnPostFastTravel -= HandlePostFastTravel;
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
        pendingProposal = pendingForwardSeconds = 0UL;
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
        if (!isLocalPlayer || localInstance != this)
            return;
        RefreshRoute();
        // Work runs in Update, outside the party snapshot handler.
    }

    void HandleStartLoad(SaveData_v1 saveData)
    {
        wasLoadInProgress = true;
        alignClassicDiseasesOnNextPostLoadHostTime = IsPartyFollower;
        InvalidateSample();
        routeNeedsSync = true;
    }

    void HandleLoadComplete(SaveData_v1 saveData)
    {
        wasLoadInProgress = false;
        // A second invalidation also covers a missed OnStartLoad and rejects any
        // response sampled during restoration. Only a new leader reply can reopen readiness.
        InvalidateSample();
        RefreshRoute();
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

        RefreshRoute();
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
        ulong current = worldTime.Now.ToSeconds();
        bool jumped = current > lastObservedSeconds && current - lastObservedSeconds > localJumpThresholdSeconds;
        CaptureSharedAdvance();
        if (pendingProposal != 0UL)
            SendPendingAdvance();
        else if (waitingForFreshSample || jumped || now >= nextPeriodicSyncTime)
            SynchronizeNow(jumped ? "local-time-jump" : "periodic-or-retry");
        lastObservedSeconds = worldTime.Now.ToSeconds();
    }

    void HandlePostFastTravel()
    {
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime()) return;
        RefreshRoute();
        CaptureSharedAdvance();
        if (pendingProposal != 0UL)
            SendPendingAdvance();
        else
        {
            // Reject any pre-travel reply; get the leader's current date now.
            InvalidateSample();
            SynchronizeNow("post-travel");
        }
    }

    void CaptureSharedAdvance()
    {
        if (LeaderControlsTime || !routeKnown || localLeader == netId || waitingForFreshSample ||
            routeNeedsSync || IsLoadInProgress() || !postLoadAuthoritativeTimeReady || !EnsureWorldTime()) return;
        ulong seconds = worldTime.Now.ToSeconds();
        if (seconds <= lastObservedSeconds || seconds - lastObservedSeconds <= localJumpThresholdSeconds ||
            (pendingProposal != 0UL && seconds <= pendingForwardSeconds)) return;
        proposalSerial++;
        if (proposalSerial == 0UL) proposalSerial++;
        pendingProposal = proposalSerial;
        pendingForwardSeconds = seconds;
        lastObservedSeconds = seconds;
    }

    void SendPendingAdvance()
    {
        if (pendingProposal != 0UL && NetworkClient.ready)
            CmdProposePartyAdvance(localParty, localEpoch, requestToken, pendingProposal, pendingForwardSeconds);
    }

    void SynchronizeNow(string reason)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime() || !NetworkClient.ready)
            return;
        RefreshRoute();
        if (!routeKnown)
            return; // No host-time fallback while discovering a solo party.
        CaptureSharedAdvance();
        routeNeedsSync = false;
        nextCheckTime = Time.realtimeSinceStartup + checkIntervalSeconds;
        nextPeriodicSyncTime = Time.realtimeSinceStartup + periodicLeaderSyncSeconds;
        lastObservedSeconds = worldTime.Now.ToSeconds();
        if (localLeader == netId)
        {
            alignClassicDiseasesOnNextPostLoadHostTime = false;
            waitingForFreshSample = false;
            CmdPublishPartyTime(localParty, localEpoch, worldTime.Now.ToSeconds());
            MarkAuthoritativeTimeReady(reason + "-leader");
        }
        else if (pendingProposal != 0UL)
            SendPendingAdvance();
        else
            CmdRequestPartyTime(localParty, localEpoch, requestToken);
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
        ServerPublishPartyTime(party, epoch, seconds);
    }

    // An ordinary server method, not another Command: dedicated replicas have no
    // local client from which Mirror could send a nested Command.
    void ServerPublishPartyTime(ulong party, ulong epoch, ulong seconds)
    {
        uint leader;
        if (!ServerMatchesRoute(party, epoch, out leader) || leader != netId)
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
        if (token == 0UL ||
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
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime())
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
        if (!ServerMatchesRoute(party, epoch, out leader) || leader != netId)
            return;
        TimeCatcher follower = FindServerClock(requester);
        if (follower != null && requester != netId)
            follower.SendPartySample(party, epoch, netId, token, seconds, true);
    }

    void SendPartySample(ulong party, ulong epoch, uint leader, ulong token, ulong seconds, bool freshReply, ulong acknowledgedProposal = 0UL)
    {
        uint currentLeader;
        if (!NetworkServer.active || token == 0UL || serverToken != token || serverParty != party ||
            serverEpoch != epoch || serverLeader != leader || !ServerMatchesRoute(party, epoch, out currentLeader) ||
            currentLeader != leader || connectionToClient == null || !connectionToClient.isReady)
            return;
        serverDelivery++;
        TargetApplyPartyTime(connectionToClient, party, epoch, leader, token, serverDelivery, seconds, freshReply, acknowledgedProposal);
    }

    [TargetRpc]
    void TargetApplyPartyTime(NetworkConnection target, ulong party, ulong epoch, uint leader,
        ulong token, ulong delivery, ulong seconds, bool freshReply, ulong acknowledgedProposal)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime())
            return;
        InitializeLocal();
        RefreshRoute();
        if (!routeKnown || localParty != party || localEpoch != epoch || localLeader != leader ||
            leader == netId || token != requestToken || delivery <= lastDelivery ||
            (waitingForFreshSample && !freshReply))
            return;
        // A heartbeat may arrive before Update sees a rest/travel jump. Preserve
        // that advance until the leader acknowledges it, instead of snapping it away.
        CaptureSharedAdvance();
        if (pendingProposal != 0UL && acknowledgedProposal != pendingProposal)
        {
            SendPendingAdvance();
            return;
        }
        pendingProposal = pendingForwardSeconds = 0UL;
        lastDelivery = delivery;
        waitingForFreshSample = false;
        ApplySyncedTime(seconds, "party-leader-" + leader);
    }

    [Command]
    void CmdProposePartyAdvance(ulong party, ulong epoch, ulong token, ulong proposal, ulong seconds)
    {
        uint leader;
        if (MultiplayerPartyManager.ServerLeaderControlsTime(netId) || proposal == 0UL || token == 0UL ||
            !ServerMatchesRoute(party, epoch, out leader) || leader == netId || serverToken != token ||
            serverParty != party || serverEpoch != epoch || serverLeader != leader) return;
        TimeCatcher source = FindServerClock(leader);
        if (source != null && source.connectionToClient != null && source.connectionToClient.isReady)
            source.TargetConsiderPartyAdvance(source.connectionToClient, netId, party, epoch, token, proposal, seconds);
    }

    [TargetRpc]
    void TargetConsiderPartyAdvance(NetworkConnection target, uint requester, ulong party, ulong epoch,
        ulong token, ulong proposal, ulong seconds)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !EnsureWorldTime()) return;
        InitializeLocal();
        RefreshRoute();
        if (LeaderControlsTime || !routeKnown || localParty != party || localEpoch != epoch || localLeader != netId) return;
        // Absolute forward proposals are idempotent. Two concurrent trips to the
        // same arrival date do not charge the party twice; old retries cannot rewind it.
        if (seconds > worldTime.Now.ToSeconds())
            ApplySyncedTime(seconds, "party-member-" + requester);
        CmdAcceptPartyAdvance(requester, party, epoch, token, proposal, worldTime.Now.ToSeconds());
    }

    [Command]
    void CmdAcceptPartyAdvance(uint requester, ulong party, ulong epoch, ulong token, ulong proposal, ulong seconds)
    {
        uint leader, requesterLeader;
        if (MultiplayerPartyManager.ServerLeaderControlsTime(netId) ||
            !ServerMatchesRoute(party, epoch, out leader) || leader != netId) return;
        TimeCatcher follower = FindServerClock(requester);
        if (follower == null || !follower.ServerMatchesRoute(party, epoch, out requesterLeader) ||
            requesterLeader != netId || follower.serverToken != token) return;
        follower.SendPartySample(party, epoch, netId, token, seconds, false, proposal);
        // Use the same party-only publication path as normal leader advancement.
        ServerPublishPartyTime(party, epoch, seconds);
    }

    // Old compiled callers can request a refresh, but cannot inject an absolute
    // global date without a party epoch/load token. There are no core callers left.
    [Command]
    public void cmdSendTime(ulong seconds)
    {
        if (connectionToClient != null) TargetRefreshPartyTime(connectionToClient);
    }

    [Command]
    public void cmdReceiveTime()
    {
        if (connectionToClient != null) TargetRefreshPartyTime(connectionToClient);
    }

    [TargetRpc]
    void TargetRefreshPartyTime(NetworkConnection target)
    {
        if (isLocalPlayer) SynchronizeNow("legacy-refresh");
    }

    [ClientRpc]
    public void rpcSendTime(ulong seconds)
    {
        if (isLocalPlayer) SynchronizeNow("legacy-refresh");
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
            lastObservedSeconds = worldTime.Now.ToSeconds();

            if (movedBackwards && GameManager.Instance != null && GameManager.Instance.EntityEffectBroker != null)
                GameManager.Instance.EntityEffectBroker.AlignMagicRoundTimerToCurrentTime($"TimeCatcher-{source}-tiny-backward-sync");

            AlignClassicDiseasesAfterLoadedClientTime(source, beforeMinutes, syncedMinutes);

            MarkAuthoritativeTimeReady(source + "-tiny-drift");
            return;
        }

        worldTime.Now.FromSeconds(seconds);

        syncedMinutes = worldTime.Now.ToClassicDaggerfallTime();
        lastObservedSeconds = worldTime.Now.ToSeconds();

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

        if (!IsPartyFollower)
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
