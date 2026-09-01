using System.Collections;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using UnityEngine.UI;
using Mirror;

public class HudMultiplayer : MonoBehaviour
{
    public Canvas canvas;
    public GraphicRaycaster raycaster;
    public GameObject[] checks;

    // Assigned by MultiplayerManager before this HUD prefab is instantiated.
    public static MultiplayerManager connectionManager;
    public static SteamLobby steamLobby;

    public Text status;
    public GameObject options, stop, host;

    [Header("Party Window")]
    [Tooltip("Optional manually-created Party button. If left empty, HudMultiplayer clones the Stop button at runtime and places Party next to it.")]
    public GameObject party;

    [Header("Connection Mode")]
    [Tooltip("Optional manually-created mode switch button. If left empty, HudMultiplayer clones the Steam Host button at runtime.")]
    public GameObject connectionModeButton;

    DaggerfallUI gameUI;
    bool runtimePartyButtonCreated;
    bool partyButtonWired;
    bool runtimeModeButtonCreated;
    bool modeButtonWired;

    void Start()
    {
        ResolveConnectionManager();
        EnsurePartyButton();
        EnsureConnectionModeButton();
        StartCoroutine(Check());
    }

    IEnumerator Check()
    {
        gameUI = GameObject.Find("DaggerfallUI").GetComponent<DaggerfallUI>();
        UserInterfaceManager uiManager = gameUI.UserInterfaceManager;

        while (true)
        {
            ResolveConnectionManager();
            EnsurePartyButton();
            EnsureConnectionModeButton();

            bool pauseMenuOpen = isPauseMenu(uiManager);
            canvas.enabled = pauseMenuOpen;
            raycaster.enabled = pauseMenuOpen;

            bool connected = PlayerMultiplayer.state != 0 ||
                NetworkServer.active || NetworkClient.isConnected;
            bool connectionBusy = connected || NetworkClient.active;
            bool directMode = connectionManager != null &&
                connectionManager.SelectedMode == MultiplayerManager.ConnectionMode.DirectIP;

            if (pauseMenuOpen)
            {
                setStatus();

                options.SetActive(!connectionBusy);
                host.SetActive(!connectionBusy && !directMode);
                stop.SetActive(connected);

                if (party != null)
                    party.SetActive(PlayerMultiplayer.state != 0);

                if (connectionModeButton != null)
                    connectionModeButton.SetActive(!connectionBusy);

                UpdateConnectionModeButtonLabel();
            }

            // Mirror's developer NetworkManagerHUD is used only for Direct IP and
            // only while ESC is open. It remains visible during a client connection
            // attempt so its Cancel button still works.
            if (connectionManager != null)
            {
                bool showDirectHud = pauseMenuOpen && directMode && !connected;
                connectionManager.ShowDirectNetworkHud(showDirectHud);
            }

            yield return new WaitForSecondsRealtime(0.20f);
        }
    }

    void ResolveConnectionManager()
    {
        if (connectionManager == null)
            connectionManager = FindObjectOfType<MultiplayerManager>();

        if (steamLobby == null && connectionManager != null)
            steamLobby = connectionManager.GetComponent<SteamLobby>();
    }

    void EnsurePartyButton()
    {
        if (party == null && stop != null)
        {
            party = Instantiate(stop, stop.transform.parent);
            party.name = "PartyButton";
            runtimePartyButtonCreated = true;
            PlaceCloneAfterSource(stop, party);
        }

        if (party == null || partyButtonWired)
            return;

        UnityEngine.UI.Button partyButton = party.GetComponent<UnityEngine.UI.Button>();
        if (partyButton != null)
        {
            if (runtimePartyButtonCreated)
                partyButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();

            partyButton.onClick.AddListener(partyWindowButton);
            partyButtonWired = true;
        }

        SetButtonText(party, "Party");
        party.SetActive(PlayerMultiplayer.state != 0);
    }

    void EnsureConnectionModeButton()
    {
        if (connectionModeButton == null && host != null)
        {
            connectionModeButton = Instantiate(host, host.transform.parent);
            connectionModeButton.name = "ConnectionModeButton";
            runtimeModeButtonCreated = true;
            PlaceCloneAfterSource(host, connectionModeButton);
        }

        if (connectionModeButton == null || modeButtonWired)
            return;

        UnityEngine.UI.Button modeButton = connectionModeButton.GetComponent<UnityEngine.UI.Button>();
        if (modeButton != null)
        {
            if (runtimeModeButtonCreated)
                modeButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();

            modeButton.onClick.AddListener(connectionModeButtonClicked);
            modeButtonWired = true;
        }

        UpdateConnectionModeButtonLabel();
    }

    void PlaceCloneAfterSource(GameObject source, GameObject clone)
    {
        if (source == null || clone == null)
            return;

        RectTransform sourceRect = source.GetComponent<RectTransform>();
        RectTransform cloneRect = clone.GetComponent<RectTransform>();
        HorizontalOrVerticalLayoutGroup layoutGroup = source.transform.parent != null
            ? source.transform.parent.GetComponent<HorizontalOrVerticalLayoutGroup>()
            : null;

        if (layoutGroup != null)
        {
            clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
        }
        else if (sourceRect != null && cloneRect != null)
        {
            float spacing = Mathf.Max(6f, sourceRect.rect.width + 6f);
            cloneRect.anchoredPosition = sourceRect.anchoredPosition + new Vector2(spacing, 0f);
        }
    }

    void SetButtonText(GameObject buttonObject, string text)
    {
        if (buttonObject == null)
            return;

        Text[] labels = buttonObject.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < labels.Length; i++)
            labels[i].text = text;
    }

    void UpdateConnectionModeButtonLabel()
    {
        if (connectionModeButton == null)
            return;

        bool directMode = connectionManager != null &&
            connectionManager.SelectedMode == MultiplayerManager.ConnectionMode.DirectIP;

        // Label is the other mode the button will switch to. The status text always
        // states which mode is currently selected.
        SetButtonText(connectionModeButton, directMode ? "Steam" : "Direct IP");
        SetButtonText(host, "Steam Host");
    }

    bool isPauseMenu(UserInterfaceManager uiManager)
    {
        return uiManager.TopWindow != null &&
            uiManager.TopWindow.ToString() == "DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallPauseOptionsWindow";
    }

    void setStatus()
    {
        string modeName = connectionManager != null
            ? connectionManager.GetActiveModeDisplayName()
            : "Unknown transport";

        if (NetworkClient.active && !NetworkClient.isConnected && !NetworkServer.active)
        {
            status.text = "Connecting — " + modeName;
            status.color = Color.yellow;
            return;
        }

        switch (PlayerMultiplayer.state)
        {
            case 0:
                if (NetworkServer.active && NetworkClient.active)
                {
                    status.text = "Connected as Host — " + modeName;
                    status.color = Color.green;
                }
                else if (NetworkServer.active)
                {
                    status.text = "Running as Server — " + modeName;
                    status.color = Color.green;
                }
                else if (NetworkClient.isConnected)
                {
                    status.text = "Connected as Client — " + modeName;
                    status.color = Color.green;
                }
                else
                {
                    string selectedName = connectionManager != null
                        ? connectionManager.GetSelectedModeDisplayName()
                        : modeName;
                    status.text = "Not connected — " + selectedName;
                    status.color = Color.red;
                }
                break;
            case 1:
                status.text = "Connected as Host — " + modeName;
                status.color = Color.green;
                break;
            case 2:
                status.text = "Connected as Client — " + modeName;
                status.color = Color.green;
                break;
            default:
                status.text = "Unknown state — " + modeName;
                status.color = Color.blue;
                break;
        }
    }

    public void enableGameUI(bool b)
    {
        gameUI.enabled = b;
    }

    public void toggleTimeHost()
    {
        OptionsMultiplayer.timeHost = !OptionsMultiplayer.timeHost;
        checks[0].SetActive(OptionsMultiplayer.timeHost);
    }

    public void toggleName()
    {
        OptionsMultiplayer.displayName = !OptionsMultiplayer.displayName;
        checks[1].SetActive(OptionsMultiplayer.displayName);
    }

    public void toggleHighestLevel()
    {
        OptionsMultiplayer.useHighestLevel = !OptionsMultiplayer.useHighestLevel;
        checks[2].SetActive(OptionsMultiplayer.useHighestLevel);
    }

    public void toggleSendLocation()
    {
        OptionsMultiplayer.sendLocation = !OptionsMultiplayer.sendLocation;
        checks[3].SetActive(OptionsMultiplayer.sendLocation);
    }

    public void toggleSendMessage()
    {
        OptionsMultiplayer.sendMessage = !OptionsMultiplayer.sendMessage;
        checks[4].SetActive(OptionsMultiplayer.sendMessage);
    }

    public void toggleMobileNpcSync()
    {
        // This options panel is hidden once a connection starts. Keep the same rule
        // here as a safety check so the session policy cannot be changed mid-session.
        if (NetworkServer.active || NetworkClient.active || NetworkClient.isConnected)
            return;

        OptionsMultiplayer.SetMobileNpcSync(!OptionsMultiplayer.mobileNpcSync);

        if (checks != null && checks.Length > 5 && checks[5] != null)
            checks[5].SetActive(OptionsMultiplayer.mobileNpcSync);
    }

    public void refreshAllChecks()
    {
        checks[0].SetActive(OptionsMultiplayer.timeHost);
        checks[1].SetActive(OptionsMultiplayer.displayName);
        checks[2].SetActive(OptionsMultiplayer.useHighestLevel);
        checks[3].SetActive(OptionsMultiplayer.sendLocation);
        checks[4].SetActive(OptionsMultiplayer.sendMessage);

        if (checks != null && checks.Length > 5 && checks[5] != null)
            checks[5].SetActive(OptionsMultiplayer.mobileNpcSync);
    }

    public void connectionModeButtonClicked()
    {
        ResolveConnectionManager();
        if (connectionManager == null)
        {
            Debug.LogWarning("[HudMultiplayer] No MultiplayerManager was found for connection mode switching.");
            return;
        }

        if (connectionManager.SelectedMode == MultiplayerManager.ConnectionMode.Steam)
            connectionManager.SelectDirectIPMode();
        else
            connectionManager.SelectSteamMode();

        UpdateConnectionModeButtonLabel();
        setStatus();
    }

    public void hostButton()
    {
        ResolveConnectionManager();

        if (connectionManager != null)
        {
            connectionManager.HostSteam();
            return;
        }

        // Compatibility fallback for an older scene setup.
        if (steamLobby != null)
            steamLobby.HostLobby();
        else
            Debug.LogWarning("[HudMultiplayer] Steam Host cannot start because no SteamLobby is available.");
    }

    public void stopButton()
    {
        ResolveConnectionManager();

        if (connectionManager != null)
        {
            connectionManager.StopNetwork();
            return;
        }

        if (steamLobby != null)
            steamLobby.StopNetwork();
    }

    public void partyWindowButton()
    {
        if (PlayerMultiplayer.state == 0)
            return;

        if (gameUI == null)
            gameUI = GameObject.Find("DaggerfallUI").GetComponent<DaggerfallUI>();

        if (gameUI == null || gameUI.UserInterfaceManager == null)
        {
            Debug.LogWarning("[HudMultiplayer] Cannot open Party window because DaggerfallUI is unavailable.");
            return;
        }

        UserInterfaceManager uiManager = gameUI.UserInterfaceManager;
        if (uiManager.TopWindow is DaggerfallMultiplayerPartyWindow)
            return;

        DaggerfallMultiplayerPartyWindow window = new DaggerfallMultiplayerPartyWindow(uiManager);

        if (canvas != null)
            canvas.enabled = false;
        if (raycaster != null)
            raycaster.enabled = false;
        if (connectionManager != null)
            connectionManager.ShowDirectNetworkHud(false);

        uiManager.PushWindow(window);
    }

    void OnDestroy()
    {
        if (connectionManager != null)
            connectionManager.ShowDirectNetworkHud(false);

        if (runtimePartyButtonCreated && party != null)
            Destroy(party);

        if (runtimeModeButtonCreated && connectionModeButton != null)
            Destroy(connectionModeButton);
    }
}
