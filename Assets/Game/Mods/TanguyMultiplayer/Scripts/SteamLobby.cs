using UnityEngine;
using Mirror;
using Steamworks;

/// <summary>
/// Steam lobby wrapper for the single dual-transport NetworkManager.
/// Every Steam host/join callback reasserts FizzySteamworks before Mirror starts.
/// </summary>
public class SteamLobby : MonoBehaviour
{
    NetworkManager manager;
    MultiplayerManager connectionManager;

    protected Callback<LobbyCreated_t> lobbyCreated;
    protected Callback<GameLobbyJoinRequested_t> gameLobbyJoinRequested;
    protected Callback<LobbyEnter_t> lobbyEntered;

    const string hostAdressKey = "HostAdress";

    CSteamID currentLobbyId;
    bool hasCurrentLobby;
    bool callbacksInitialized;

    void Awake()
    {
        CacheReferences();
    }

    void Start()
    {
        EnsureCallbacks();
    }

    void CacheReferences()
    {
        if (manager == null)
            manager = GetComponent<NetworkManager>();

        if (connectionManager == null)
            connectionManager = GetComponent<MultiplayerManager>();
    }

    bool EnsureCallbacks()
    {
        if (callbacksInitialized)
            return true;

        if (!SteamManager.Initialized)
            return false;

        lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
        gameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
        lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
        callbacksInitialized = true;
        return true;
    }

    bool PrepareSteam(string reason)
    {
        CacheReferences();

        if (!SteamManager.Initialized)
        {
            Debug.LogWarning("[SteamLobby] Steam is not initialized; cannot " + reason + ".");
            return false;
        }

        if (!EnsureCallbacks())
        {
            Debug.LogWarning("[SteamLobby] Steam callbacks could not be initialized for " + reason + ".");
            return false;
        }

        if (connectionManager != null && !connectionManager.PrepareSteamTransport())
            return false;

        return true;
    }

    public void HostLobby()
    {
        if (!PrepareSteam("host a lobby"))
            return;

        if (manager == null)
        {
            Debug.LogError("[SteamLobby] NetworkManager is missing.");
            return;
        }

        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, manager.maxConnections);
    }

    void OnLobbyCreated(LobbyCreated_t callback)
    {
        if (callback.m_eResult != EResult.k_EResultOK)
        {
            Debug.LogWarning("[SteamLobby] Lobby creation failed: " + callback.m_eResult);
            return;
        }

        if (!PrepareSteam("start the Steam host"))
            return;

        currentLobbyId = new CSteamID(callback.m_ulSteamIDLobby);
        hasCurrentLobby = true;

        manager.StartHost();
        SteamMatchmaking.SetLobbyData(currentLobbyId, hostAdressKey, SteamUser.GetSteamID().ToString());
    }

    void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
    {
        if (!PrepareSteam("join an invited lobby"))
            return;

        SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
    }

    void OnLobbyEntered(LobbyEnter_t callback)
    {
        currentLobbyId = new CSteamID(callback.m_ulSteamIDLobby);
        hasCurrentLobby = true;

        // The host also enters its own lobby. It has already started through
        // OnLobbyCreated and must not start a second client connection here.
        if (NetworkServer.active)
            return;

        if (!PrepareSteam("connect to the Steam lobby host"))
            return;

        string hostAddress = SteamMatchmaking.GetLobbyData(currentLobbyId, hostAdressKey);
        if (string.IsNullOrEmpty(hostAddress))
        {
            Debug.LogWarning("[SteamLobby] Lobby did not provide a host Steam ID.");
            return;
        }

        manager.networkAddress = hostAddress;
        manager.StartClient();
    }

    public void LeaveCurrentLobby()
    {
        if (!hasCurrentLobby || !SteamManager.Initialized)
            return;

        SteamMatchmaking.LeaveLobby(currentLobbyId);
        hasCurrentLobby = false;
        currentLobbyId = new CSteamID(0);
    }

    public void StopNetwork()
    {
        CacheReferences();

        if (connectionManager != null)
        {
            connectionManager.StopNetwork();
            return;
        }

        LeaveCurrentLobby();

        NetworkManager activeManager = manager != null ? manager : NetworkManager.singleton;
        if (activeManager == null)
            return;

        if (NetworkServer.active && NetworkClient.active)
            activeManager.StopHost();
        else if (NetworkClient.active)
            activeManager.StopClient();
        else if (NetworkServer.active)
            activeManager.StopServer();
    }
}
