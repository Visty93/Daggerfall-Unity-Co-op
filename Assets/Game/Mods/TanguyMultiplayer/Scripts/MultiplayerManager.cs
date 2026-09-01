using UnityEngine;
using Mirror;
using Steamworks;

/// <summary>
/// Owns the single active Mirror NetworkManager and selects either FizzySteamworks
/// or the normal direct-IP transport before a connection is started.
///
/// Keep only one NetworkManager active. Both transports are components on the same
/// GameObject. The old NetworkManagerSteam object can remain disabled as a backup.
/// </summary>
public class MultiplayerManager : MonoBehaviour
{
    public enum ConnectionMode
    {
        Steam = 0,
        DirectIP = 1,
    }

    [Header("Multiplayer HUD")]
    public GameObject hud;

    [Header("Connection Mode")]
    [Tooltip("Steam remains the preferred release mode. If Steam is unavailable at startup, Direct IP is selected instead.")]
    public ConnectionMode defaultMode = ConnectionMode.Steam;

    [Tooltip("Vertical screen offset used by the small Direct IP Host/Client panel while the ESC menu is open.")]
    public int directHudOffsetY = 72;

    NetworkManager manager;
    NetworkManagerHUD directHud;
    SteamLobby steamLobby;
    Transport directTransport;
    Transport steamTransport;

    ConnectionMode selectedMode = ConnectionMode.Steam;
    ConnectionMode activeMode = ConnectionMode.Steam;
    bool wasNetworkActive;

    // Custom Direct-IP panel state. The stock Mirror NetworkManagerHUD is kept disabled
    // because it exposes a Server Only button that this co-op mod does not support.
    bool directHudVisible;
    string directAddress = "localhost";

    public ConnectionMode SelectedMode
    {
        get { return selectedMode; }
    }

    public ConnectionMode ActiveMode
    {
        get { return IsNetworkActive ? activeMode : selectedMode; }
    }

    public bool IsNetworkActive
    {
        get { return NetworkServer.active || NetworkClient.active; }
    }

    public bool IsActuallyConnected
    {
        get { return NetworkServer.active || NetworkClient.isConnected; }
    }

    public bool SteamAvailable
    {
        get { return steamTransport != null && SteamManager.Initialized; }
    }

    public bool DirectIPAvailable
    {
        get { return directTransport != null; }
    }

    void Awake()
    {
        CacheReferences();

        // Never expose Mirror's stock NetworkManagerHUD. It includes a Server Only
        // button, while this mod supports player-hosted Host/Client sessions only.
        if (directHud != null)
            directHud.enabled = false;

        if (manager != null && !string.IsNullOrEmpty(manager.networkAddress))
            directAddress = manager.networkAddress;
    }

    void Start()
    {
        CacheReferences();

        ConnectionMode initialMode = defaultMode;
        if (initialMode == ConnectionMode.Steam && !SteamAvailable && DirectIPAvailable)
        {
            initialMode = ConnectionMode.DirectIP;
            Debug.LogWarning("[MultiplayerManager] Steam transport is unavailable. Starting in Direct IP mode.");
        }

        string error;
        if (!SelectMode(initialMode, out error))
        {
            ConnectionMode fallback = initialMode == ConnectionMode.Steam
                ? ConnectionMode.DirectIP
                : ConnectionMode.Steam;

            if (!SelectMode(fallback, out error))
                Debug.LogError("[MultiplayerManager] No usable multiplayer transport was found. " + error);
        }

        // Set statics before instantiating the HUD so its Start() can immediately
        // resolve the connection coordinator.
        HudMultiplayer.connectionManager = this;
        HudMultiplayer.steamLobby = steamLobby;

        if (hud != null)
            Instantiate(hud);
        else
            Debug.LogWarning("[MultiplayerManager] HUD prefab is not assigned.");
    }

    void Update()
    {
        bool networkActive = IsNetworkActive;

        if (networkActive && !wasNetworkActive)
        {
            // Capture whichever transport mode was selected when Mirror becomes active.
            activeMode = selectedMode;
        }

        if (networkActive && directHud != null && IsActuallyConnected)
            directHud.enabled = false;

        if (IsActuallyConnected)
            directHudVisible = false;

        wasNetworkActive = networkActive;
    }

    void CacheReferences()
    {
        if (manager == null)
            manager = GetComponent<NetworkManager>();

        if (directHud == null)
            directHud = GetComponent<NetworkManagerHUD>();

        if (steamLobby == null)
            steamLobby = GetComponent<SteamLobby>();

        FindTransports();

        if (directHud != null)
        {
            // Keep the component disabled even if it remains on the GameObject.
            // The custom panel below replaces it and intentionally has no Server Only button.
            directHud.enabled = false;
            directHud.offsetX = 0;
            directHud.offsetY = directHudOffsetY;
        }

        if (manager != null && string.IsNullOrEmpty(directAddress) &&
            !string.IsNullOrEmpty(manager.networkAddress))
            directAddress = manager.networkAddress;
    }

    void FindTransports()
    {
        Transport[] transports = GetComponents<Transport>();
        for (int i = 0; i < transports.Length; i++)
        {
            Transport candidate = transports[i];
            if (candidate == null)
                continue;

            string typeName = candidate.GetType().Name;
            string fullName = candidate.GetType().FullName ?? string.Empty;

            if (typeName == "FizzySteamworks" || fullName.EndsWith(".FizzySteamworks"))
            {
                steamTransport = candidate;
                continue;
            }

            if (typeName == "KcpTransport" || fullName.IndexOf("kcp", System.StringComparison.OrdinalIgnoreCase) >= 0)
                directTransport = candidate;
        }

        // If there are exactly two transports and one is Fizzy, use the other as
        // the direct-IP transport even if its concrete class name differs.
        if (directTransport == null && steamTransport != null)
        {
            for (int i = 0; i < transports.Length; i++)
            {
                if (transports[i] != null && transports[i] != steamTransport)
                {
                    directTransport = transports[i];
                    break;
                }
            }
        }
    }

    public bool SelectSteamMode()
    {
        string error;
        bool selected = SelectMode(ConnectionMode.Steam, out error);
        if (!selected)
            Debug.LogWarning("[MultiplayerManager] Cannot select Steam mode: " + error);
        return selected;
    }

    public bool SelectDirectIPMode()
    {
        string error;
        bool selected = SelectMode(ConnectionMode.DirectIP, out error);
        if (!selected)
            Debug.LogWarning("[MultiplayerManager] Cannot select Direct IP mode: " + error);
        return selected;
    }

    public bool SelectMode(ConnectionMode mode, out string error)
    {
        CacheReferences();
        error = string.Empty;

        if (IsNetworkActive)
        {
            error = "A connection is already active or being started.";
            return false;
        }

        Transport targetTransport;
        if (mode == ConnectionMode.Steam)
        {
            if (steamTransport == null)
            {
                error = "FizzySteamworks is not attached to the active NetworkManager.";
                return false;
            }

            if (!SteamManager.Initialized)
            {
                error = "Steam is not initialized.";
                return false;
            }

            targetTransport = steamTransport;
        }
        else
        {
            if (directTransport == null)
            {
                error = "No direct-IP transport (KCP) is attached to the active NetworkManager.";
                return false;
            }

            targetTransport = directTransport;
        }

        // NetworkManager.Awake() has already established this object as the singleton.
        // In Mirror 57, later StartHost/StartClient calls keep the currently selected
        // Transport.activeTransport when the singleton is already initialized.
        Transport.activeTransport = targetTransport;
        selectedMode = mode;

        directHudVisible = false;
        if (directHud != null)
            directHud.enabled = false;

        Debug.Log("[MultiplayerManager] Selected " + GetModeDisplayName(mode) +
            " transport: " + targetTransport.GetType().Name);
        return true;
    }

    public bool PrepareSteamTransport()
    {
        if (selectedMode == ConnectionMode.Steam &&
            Transport.activeTransport == steamTransport && SteamAvailable)
            return true;

        return SelectSteamMode();
    }

    public bool PrepareDirectIPTransport()
    {
        if (selectedMode == ConnectionMode.DirectIP &&
            Transport.activeTransport == directTransport && DirectIPAvailable)
            return true;

        return SelectDirectIPMode();
    }

    public void ShowDirectNetworkHud(bool show)
    {
        CacheReferences();

        // The stock Mirror HUD is deliberately never enabled. It contains a Server Only
        // button, which is not a supported play mode for this project.
        if (directHud != null)
            directHud.enabled = false;

        bool mayShow = selectedMode == ConnectionMode.DirectIP &&
            !NetworkServer.active && !NetworkClient.isConnected;

        if (show && mayShow)
        {
            // Reassert KCP immediately before the custom Host/Client controls can be used.
            if (!PrepareDirectIPTransport())
            {
                directHudVisible = false;
                return;
            }

            directHudVisible = true;
        }
        else
        {
            directHudVisible = false;
        }
    }

    void OnGUI()
    {
        if (!directHudVisible || selectedMode != ConnectionMode.DirectIP)
            return;

        if (manager == null)
            CacheReferences();

        if (manager == null)
            return;

        GUILayout.BeginArea(new Rect(10f, directHudOffsetY, 280f, 175f), GUI.skin.box);
        GUILayout.Label("Direct IP");

        // During an in-progress client connection, only expose Cancel. This preserves
        // the useful part of NetworkManagerHUD without exposing Server Only.
        if (NetworkClient.active && !NetworkClient.isConnected && !NetworkServer.active)
        {
            GUILayout.Label("Connecting to " + manager.networkAddress + "...");
            if (GUILayout.Button("Cancel"))
                manager.StopClient();

            GUILayout.EndArea();
            return;
        }

        // HudMultiplayer normally hides this panel once connected. Keep this guard here
        // as well so OnGUI can never offer another connection action mid-session.
        if (NetworkServer.active || NetworkClient.isConnected)
        {
            GUILayout.EndArea();
            return;
        }

        if (GUILayout.Button("Host"))
        {
            if (PrepareDirectIPTransport())
            {
                activeMode = ConnectionMode.DirectIP;
                manager.StartHost();
                directHudVisible = false;
            }
        }

        GUILayout.Label("Server Address");
        directAddress = GUILayout.TextField(directAddress ?? string.Empty);

        if (GUILayout.Button("Client"))
        {
            if (PrepareDirectIPTransport())
            {
                string address = (directAddress ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(address))
                    address = "localhost";

                directAddress = address;
                manager.networkAddress = address;
                activeMode = ConnectionMode.DirectIP;
                manager.StartClient();
            }
        }

        GUILayout.EndArea();
    }

    public void HostSteam()
    {
        CacheReferences();

        if (steamLobby == null)
        {
            Debug.LogError("[MultiplayerManager] SteamLobby is not attached to the active NetworkManager.");
            return;
        }

        if (!PrepareSteamTransport())
            return;

        steamLobby.HostLobby();
    }

    public void StopNetwork()
    {
        CacheReferences();

        if (activeMode == ConnectionMode.Steam && steamLobby != null)
            steamLobby.LeaveCurrentLobby();

        if (manager == null)
            manager = NetworkManager.singleton;

        if (manager == null)
        {
            Debug.LogWarning("[MultiplayerManager] No NetworkManager is available to stop.");
            return;
        }

        if (NetworkServer.active && NetworkClient.active)
            manager.StopHost();
        else if (NetworkClient.active)
            manager.StopClient();
        else if (NetworkServer.active)
            manager.StopServer();

        directHudVisible = false;
        if (directHud != null)
            directHud.enabled = false;
    }

    public string GetSelectedModeDisplayName()
    {
        return GetModeDisplayName(selectedMode);
    }

    public string GetActiveModeDisplayName()
    {
        return GetModeDisplayName(ActiveMode);
    }

    public static string GetModeDisplayName(ConnectionMode mode)
    {
        return mode == ConnectionMode.Steam ? "Steam" : "Direct IP";
    }

    void OnDestroy()
    {
        directHudVisible = false;
        if (directHud != null)
            directHud.enabled = false;

        if (HudMultiplayer.connectionManager == this)
            HudMultiplayer.connectionManager = null;
    }
}
