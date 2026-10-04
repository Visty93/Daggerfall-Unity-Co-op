using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Weather;
using Mirror;

// Uses the party membership/epoch supplied by the party-time patch.
// The physical server validates and relays; it never supplies its own weather.
public class WeatherCatcher : NetworkBehaviour
{
    WeatherManager weatherManager;
    PlayerWeather playerWeather;
    bool subscribed, wasLoading, routeKnown, waitingForFreshSample;
    bool applyingSyncedWeather, hasAppliedSyncedWeather, hasPendingWeather;
    bool forceNextSyncedWeatherApply;
    WeatherType lastAppliedSyncedWeather, pendingWeather;
    ulong localParty, localEpoch, requestToken, lastDelivery;
    uint localLeader;
    float nextSyncTime;
    const float checkIntervalSeconds = 2.56f;
    // Subscription belongs to this player's replica on the physical server.
    ulong serverParty, serverEpoch, serverToken, serverDelivery;
    uint serverLeader;

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        InitializeLocal();
    }

    void Start() { if (isLocalPlayer) InitializeLocal(); }
    void OnDestroy() { Unsubscribe(); }
    public override void OnStopClient() { Unsubscribe(); base.OnStopClient(); }

    void InitializeLocal()
    {
        if (subscribed || !isLocalPlayer) return;
        WeatherManager.OnWeatherChange += HandleWeatherChange;
        SaveLoadManager.OnStartLoad += HandleStartLoad;
        SaveLoadManager.OnLoad += HandleLoadComplete;
        MultiplayerPartyManager.PartyStateChanged += HandlePartyStateChanged;
        Mirror.NetworkManager.SessionStopped += Unsubscribe;
        PlayerEnterExit.OnRespawnerComplete += HandleRespawnerComplete;
        PlayerEnterExit.OnTransitionExterior += HandleExterior;
        PlayerEnterExit.OnTransitionDungeonExterior += HandleExterior;
        subscribed = true;
        wasLoading = IsLoadInProgress();
        InvalidateSample();
        nextSyncTime = Time.realtimeSinceStartup + 0.75f;
    }

    void Unsubscribe()
    {
        if (subscribed)
        {
            WeatherManager.OnWeatherChange -= HandleWeatherChange;
            SaveLoadManager.OnStartLoad -= HandleStartLoad;
            SaveLoadManager.OnLoad -= HandleLoadComplete;
            MultiplayerPartyManager.PartyStateChanged -= HandlePartyStateChanged;
            Mirror.NetworkManager.SessionStopped -= Unsubscribe;
            PlayerEnterExit.OnRespawnerComplete -= HandleRespawnerComplete;
            PlayerEnterExit.OnTransitionExterior -= HandleExterior;
            PlayerEnterExit.OnTransitionDungeonExterior -= HandleExterior;
        }
        subscribed = routeKnown = hasPendingWeather = false;
        serverParty = serverEpoch = serverToken = 0UL;
    }

    bool EnsureWeather()
    {
        if (GameManager.Instance == null) return false;
        weatherManager = GameManager.Instance.WeatherManager;
        playerWeather = weatherManager != null ? weatherManager.PlayerWeather : null;
        return playerWeather != null;
    }

    bool IsLoadInProgress()
    {
        return SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
    }

    bool TryGetLocalRoute(out ulong party, out uint leader, out ulong epoch)
    {
        party = epoch = 0UL; leader = 0U;
        if (netId == 0U) return false;
        if (NetworkServer.active)
            return MultiplayerPartyManager.ServerTryGetPartyTimeRoute(netId, out party, out leader, out epoch);
        MultiplayerPartyManager.PartyClientState state;
        if (!MultiplayerPartyManager.TryGetClientPartyState(netId, out state) || state == null) return false;
        party = state.PartyId; leader = state.LeaderNetId; epoch = state.TimeEpoch;
        return party != 0UL && leader != 0U && epoch != 0UL;
    }

    void InvalidateSample()
    {
        requestToken++;
        if (requestToken == 0UL) requestToken++;
        lastDelivery = 0UL;
        waitingForFreshSample = true;
        hasPendingWeather = false;
        hasAppliedSyncedWeather = false;
    }

    void RefreshRoute()
    {
        ulong party, epoch; uint leader;
        bool known = TryGetLocalRoute(out party, out leader, out epoch);
        if (known == routeKnown && party == localParty && leader == localLeader && epoch == localEpoch) return;
        routeKnown = known; localParty = party; localLeader = leader; localEpoch = epoch;
        InvalidateSample();
        nextSyncTime = 0f;
        // Becoming solo or leader keeps current weather. Discard old deferred samples.
    }

    void HandlePartyStateChanged() { if (isLocalPlayer) RefreshRoute(); }
    void HandleStartLoad(SaveData_v1 data)
    {
        wasLoading = true;
        InvalidateSample();
    }
    void HandleLoadComplete(SaveData_v1 data)
    {
        wasLoading = false;
        InvalidateSample();
        forceNextSyncedWeatherApply = true;
        nextSyncTime = Time.realtimeSinceStartup + 0.75f;
    }
    void HandleWeatherChange(WeatherType weather)
    {
        if (isLocalPlayer && !applyingSyncedWeather)
            nextSyncTime = Time.realtimeSinceStartup + 0.15f;
        // The event precedes PlayerWeather.WeatherType assignment; sample in Update.
    }
    void HandleRespawnerComplete()
    {
        if (!isLocalPlayer) return;
        InvalidateSample();
        forceNextSyncedWeatherApply = true;
        nextSyncTime = Time.realtimeSinceStartup + 0.75f;
    }
    void HandleExterior(PlayerEnterExit.TransitionEventArgs args)
    {
        if (!isLocalPlayer) return;
        InvalidateSample();
        forceNextSyncedWeatherApply = true;
        nextSyncTime = Time.realtimeSinceStartup + 0.5f;
    }

    void Update()
    {
        if (!isLocalPlayer || !(NetworkServer.active || NetworkClient.isConnected)) return;
        InitializeLocal();
        RefreshRoute();
        bool loading = IsLoadInProgress();
        if (loading && !wasLoading) HandleStartLoad(default(SaveData_v1));
        else if (!loading && wasLoading) HandleLoadComplete(default(SaveData_v1));
        if (loading || !EnsureWeather() || !routeKnown || !TimeCatcher.IsPostLoadAuthoritativeTimeReady) return;
        ApplyPendingWeather();
        if (Time.realtimeSinceStartup < nextSyncTime) return;
        nextSyncTime = Time.realtimeSinceStartup + checkIntervalSeconds;
        if (localLeader == netId)
        {
            // First publication and periodic refresh also cover missed events.
            CmdPublishPartyWeather(localParty, localEpoch, playerWeather.WeatherType);
        }
        else
        {
            // Request a fresh sample even if our local weather did not change.
            // This also recovers when the leader was loading or snapshots were in flight.
            CmdRequestPartyWeather(localParty, localEpoch, requestToken);
        }
    }

    void ApplyPendingWeather()
    {
        if (!hasPendingWeather || !routeKnown || localLeader == netId || IsLoadInProgress() ||
            !TimeCatcher.IsPostLoadAuthoritativeTimeReady || !EnsureWeather()) return;
        // Preserve interior/dungeon fog. Keep only the latest party sample until outside.
        if (GameManager.Instance.IsPlayerInsideBuilding || GameManager.Instance.IsPlayerInsideDungeon ||
            GameManager.Instance.IsPlayerInsideCastle || GameObject.Find("Exterior") == null) return;
        WeatherType incoming = NormalizeWeatherForLocalClimate(pendingWeather);
        if (!forceNextSyncedWeatherApply && hasAppliedSyncedWeather &&
            lastAppliedSyncedWeather == incoming && playerWeather.WeatherType == incoming)
        {
            hasPendingWeather = false;
            return;
        }
        applyingSyncedWeather = true;
        try { weatherManager.SetWeather(incoming); }
        finally { applyingSyncedWeather = false; }
        hasAppliedSyncedWeather = true;
        lastAppliedSyncedWeather = incoming;
        hasPendingWeather = forceNextSyncedWeatherApply = false;
    }

    static bool IsValidWeather(WeatherType weather)
    {
        return weather == WeatherType.Sunny || weather == WeatherType.Cloudy ||
            weather == WeatherType.Overcast || weather == WeatherType.Fog ||
            weather == WeatherType.Rain || weather == WeatherType.Snow || weather == WeatherType.Thunder;
    }

    static WeatherCatcher FindServerWeather(uint player)
    {
        NetworkIdentity identity;
        return NetworkServer.spawned.TryGetValue(player, out identity) && identity != null
            ? identity.GetComponent<WeatherCatcher>() : null;
    }

    bool ServerMatchesRoute(ulong party, ulong epoch, out uint leader)
    {
        ulong currentParty, currentEpoch;
        return MultiplayerPartyManager.ServerTryGetPartyTimeRoute(netId, out currentParty, out leader, out currentEpoch) &&
            currentParty == party && currentEpoch == epoch;
    }

    [Command]
    void CmdPublishPartyWeather(ulong party, ulong epoch, WeatherType weather)
    {
        uint leader;
        if (!IsValidWeather(weather) || !ServerMatchesRoute(party, epoch, out leader) || leader != netId)
            return;
        foreach (NetworkConnection connection in MultiplayerPartyManager.ServerGetPartyConnections(netId))
        {
            if (connection == null || !connection.isReady || connection.identity == null || connection.identity.netId == netId)
                continue;
            WeatherCatcher follower = connection.identity.GetComponent<WeatherCatcher>();
            if (follower != null)
                follower.SendPartySample(party, epoch, netId, follower.serverToken, weather, false);
        }
    }

    [Command]
    void CmdRequestPartyWeather(ulong party, ulong epoch, ulong token)
    {
        uint leader;
        if (token == 0UL ||
            !ServerMatchesRoute(party, epoch, out leader) || leader == netId)
            return;
        serverParty = party;
        serverEpoch = epoch;
        serverLeader = leader;
        serverToken = token;
        WeatherCatcher source = FindServerWeather(leader);
        if (source != null && source.connectionToClient != null && source.connectionToClient.isReady)
            source.TargetCapturePartyWeather(source.connectionToClient, netId, party, epoch, token);
    }

    [TargetRpc]
    void TargetCapturePartyWeather(NetworkConnection target, uint requester, ulong party, ulong epoch, ulong token)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !TimeCatcher.IsPostLoadAuthoritativeTimeReady || !EnsureWeather())
            return;
        InitializeLocal();
        RefreshRoute();
        if (!routeKnown || localParty != party || localEpoch != epoch || localLeader != netId)
            return;
        CmdReplyPartyWeather(requester, party, epoch, token, playerWeather.WeatherType);
    }

    [Command]
    void CmdReplyPartyWeather(uint requester, ulong party, ulong epoch, ulong token, WeatherType weather)
    {
        uint leader;
        if (!IsValidWeather(weather) || !ServerMatchesRoute(party, epoch, out leader) || leader != netId)
            return;
        WeatherCatcher follower = FindServerWeather(requester);
        if (follower != null && requester != netId)
            follower.SendPartySample(party, epoch, netId, token, weather, true);
    }

    void SendPartySample(ulong party, ulong epoch, uint leader, ulong token, WeatherType weather, bool freshReply)
    {
        uint currentLeader;
        if (!NetworkServer.active || token == 0UL || serverToken != token || serverParty != party ||
            serverEpoch != epoch || serverLeader != leader || !ServerMatchesRoute(party, epoch, out currentLeader) ||
            currentLeader != leader || connectionToClient == null || !connectionToClient.isReady)
            return;
        serverDelivery++;
        TargetApplyPartyWeather(connectionToClient, party, epoch, leader, token, serverDelivery, weather, freshReply);
    }

    [TargetRpc]
    void TargetApplyPartyWeather(NetworkConnection target, ulong party, ulong epoch, uint leader,
        ulong token, ulong delivery, WeatherType weather, bool freshReply)
    {
        if (!isLocalPlayer || IsLoadInProgress() || !TimeCatcher.IsPostLoadAuthoritativeTimeReady || !EnsureWeather())
            return;
        InitializeLocal();
        RefreshRoute();
        if (!routeKnown || localParty != party || localEpoch != epoch || localLeader != leader ||
            leader == netId || token != requestToken || delivery <= lastDelivery ||
            (waitingForFreshSample && !freshReply))
            return;
        lastDelivery = delivery;
        waitingForFreshSample = false;
        pendingWeather = weather;
        hasPendingWeather = true;
        ApplyPendingWeather();
    }

    WeatherType NormalizeWeatherForLocalClimate(WeatherType incomingWeather)
    {
        if (incomingWeather == WeatherType.Snow &&
            weatherManager != null &&
            playerWeather != null &&
            playerWeather.PlayerGps != null)
        {
            int climateIndex = playerWeather.PlayerGps.CurrentClimateIndex;
            if (WeatherManager.IsSnowFreeClimate(climateIndex))
            {
                // Leader can be in a snowy climate while this client is in desert,
                // rainforest, or subtropical climate. Keep the bad-weather sync
                // feeling, but do not show impossible snow in snow-free climates.
                return WeatherType.Rain;
            }
        }

        return incomingWeather;
    }

}
