using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Weather;
using Mirror;

public class WeatherCatcher : NetworkBehaviour
{
    WeatherManager weatherManager;
    PlayerWeather playerWeather;
    WeatherType lastWeather;
    string weatherName;

    // Client-side guard against re-applying the same synced weather every check.
    // WeatherManager.SetWeather(Rain/Snow/Fog) can randomly choose Rain1/Rain2 or
    // Snow1/Snow2 sky variants, so calling it repeatedly for the same WeatherType
    // makes the sky flicker even though the weather did not really change.
    bool hasAppliedSyncedWeather = false;
    WeatherType lastAppliedSyncedWeather = WeatherType.Sunny;

    // Prevent the OnWeatherChange event raised by our own synced SetWeather() from
    // immediately asking the host for the same weather again.
    bool applyingSyncedWeather = false;
    bool subscribedToWeatherEvents = false;
    Coroutine delayedSyncCoroutine = null;

    // After leaving a dungeon/interior the WeatherType enum can already match the host
    // while RenderSettings/DaggerfallSky still have stale dungeon fog. Bypass the
    // same-weather skip once so WeatherManager.SetWeather(Fog) can rebuild visuals.
    bool forceNextSyncedWeatherApply = false;

    void Start()
    {
        init();
    }

    void OnDestroy()
    {
        UnsubscribeWeatherEvents();
    }

    void init()
    {
        weatherManager = GameManager.Instance.WeatherManager;
        playerWeather = weatherManager.PlayerWeather;

        if (isLocalPlayer)
        {
            SubscribeWeatherEvents();
            StartCoroutine(Check());

            // Initial connect/join sync. The old code initialized lastWeather to the
            // local value and then waited for a change, so a newly joined client could
            // keep its own startup/load weather until something changed later.
            QueueWeatherSync(0.75f);
        }
    }

    void SubscribeWeatherEvents()
    {
        if (subscribedToWeatherEvents)
            return;

        WeatherManager.OnWeatherChange += WeatherManager_OnWeatherChange;
        SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
        PlayerEnterExit.OnRespawnerComplete += PlayerEnterExit_OnRespawnerComplete;
        PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
        PlayerEnterExit.OnTransitionDungeonExterior += PlayerEnterExit_OnTransitionExterior;

        subscribedToWeatherEvents = true;
    }

    void UnsubscribeWeatherEvents()
    {
        if (!subscribedToWeatherEvents)
            return;

        WeatherManager.OnWeatherChange -= WeatherManager_OnWeatherChange;
        SaveLoadManager.OnLoad -= SaveLoadManager_OnLoad;
        PlayerEnterExit.OnRespawnerComplete -= PlayerEnterExit_OnRespawnerComplete;
        PlayerEnterExit.OnTransitionExterior -= PlayerEnterExit_OnTransitionExterior;
        PlayerEnterExit.OnTransitionDungeonExterior -= PlayerEnterExit_OnTransitionExterior;

        subscribedToWeatherEvents = false;
    }

    void WeatherManager_OnWeatherChange(WeatherType weather)
    {
        if (!isLocalPlayer || applyingSyncedWeather)
            return;

        // Event-based sync catches save loads and immediate weather changes instead
        // of waiting only for the 2.56s polling loop.
        QueueWeatherSync(0.15f);
    }

    void SaveLoadManager_OnLoad(SaveData_v1 saveData)
    {
        if (!isLocalPlayer)
            return;

        // SaveLoadManager.OnLoad sets local weather from the save in WeatherManager.
        // After a short delay, resync from host so clients do not keep stale save/load weather.
        QueueWeatherSync(0.75f);
    }

    void PlayerEnterExit_OnRespawnerComplete()
    {
        if (!isLocalPlayer)
            return;

        // Fast travel / respawn can roll or restore weather through WeatherManager.
        // Force one visual reapply because the enum can be correct while fog/sky settings are stale.
        forceNextSyncedWeatherApply = true;
        QueueWeatherSync(0.75f);
    }

    void PlayerEnterExit_OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args)
    {
        if (!isLocalPlayer)
            return;

        // Re-apply/request after exterior is actually present so deferred weather from
        // interiors/dungeons does not get stuck until the next natural weather change.
        // This must bypass the same-weather skip once, especially for WeatherType.Fog.
        forceNextSyncedWeatherApply = true;
        QueueWeatherSync(0.5f);
    }

    void QueueWeatherSync(float delay)
    {
        if (!isLocalPlayer)
            return;

        if (delayedSyncCoroutine != null)
            StopCoroutine(delayedSyncCoroutine);

        delayedSyncCoroutine = StartCoroutine(DelayedWeatherSync(delay));
    }

    IEnumerator DelayedWeatherSync(float delay)
    {
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        delayedSyncCoroutine = null;
        SyncWeatherNow();
    }

    void SyncWeatherNow()
    {
        if (!isLocalPlayer || weatherManager == null || playerWeather == null)
            return;

        if (isServer)
            SendHostWeather();
        else
            cmdReceiveWeather();

        lastWeather = playerWeather.WeatherType;
    }

    IEnumerator Check()
    {
        lastWeather = playerWeather.WeatherType;

        while (true)
        {
            if (lastWeather != playerWeather.WeatherType)
            {
                SyncWeatherNow();
            }

            yield return new WaitForSeconds(2.56f);
        }
    }

    [Command]
    void cmdReceiveWeather()
    {
        SendHostWeather();
    }

    void SendHostWeather()
    {
        if (!isServer || playerWeather == null)
            return;

        print("WEATHER SEND " + playerWeather.WeatherType.ToString());
        rpcSendWeather(playerWeather.WeatherType.ToString());
        lastWeather = playerWeather.WeatherType;
    }

    [ClientRpc]
    public void rpcSendWeather(string weather)
    {
        if (!isServer)
        {
            WeatherType incomingWeather = NormalizeWeatherForLocalClimate(getWeatherType(weather));

            // If this exact synced weather has already been applied and the local
            // PlayerWeather still matches it, do not call WeatherManager.SetWeather()
            // again. Re-applying Rain/Snow/Fog randomly picks a sky variant again.
            if (!forceNextSyncedWeatherApply &&
                hasAppliedSyncedWeather &&
                lastAppliedSyncedWeather == incomingWeather &&
                playerWeather != null &&
                playerWeather.WeatherType == incomingWeather)
            {
                lastWeather = incomingWeather;
                return;
            }

            weatherName = incomingWeather.ToString();
            setWeather();
        }
    }

    void setWeather()
    {
        WeatherType incomingWeather = NormalizeWeatherForLocalClimate(getWeatherType(weatherName));

        if (GameObject.Find("Exterior") != null)
        {
            applyingSyncedWeather = true;
            weatherManager.SetWeather(incomingWeather);
            applyingSyncedWeather = false;

            hasAppliedSyncedWeather = true;
            lastAppliedSyncedWeather = incomingWeather;
            lastWeather = incomingWeather;
            forceNextSyncedWeatherApply = false;
        }
        else
        {
            Invoke("setWeather", 0.5f); // retry setting weather later if player still isn't outside
        }
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
                // Host can be in a snowy climate while this client is in desert,
                // rainforest, or subtropical climate. Keep the bad-weather sync
                // feeling, but do not show impossible snow in snow-free climates.
                return WeatherType.Rain;
            }
        }

        return incomingWeather;
    }

    WeatherType getWeatherType(string s)
    {
        switch (s)
        {
            case "Sunny":
                return WeatherType.Sunny;
            case "Cloudy":
                return WeatherType.Cloudy;
            case "Overcast":
                return WeatherType.Overcast;
            case "Fog":
                return WeatherType.Fog;
            case "Rain":
                return WeatherType.Rain;
            case "Rain_Normal":
                return WeatherType.Rain;
            case "Snow":
                return WeatherType.Snow;
            case "Snow_Normal":
                return WeatherType.Snow;
            case "Thunder":
                return WeatherType.Thunder;
        }
        return WeatherType.Sunny;
    }
}
