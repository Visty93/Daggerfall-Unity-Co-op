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
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        ResolveConnectionManager();
        EnsurePartyButton();
        EnsureConnectionModeButton();
        BuildRespawnSettings();
        RefreshLegacyOptionsAvailability();
        StartCoroutine(Check());
    }

    IEnumerator Check()
    {
        gameUI = GameObject.Find("DaggerfallUI").GetComponent<DaggerfallUI>();
        UserInterfaceManager uiManager = gameUI.UserInterfaceManager;

        while (true)
        {
            OptionsMultiplayer.EnsureLocalRespawnSettings();
            ResolveConnectionManager();
            EnsurePartyButton();
            EnsureConnectionModeButton();

            bool pauseMenuOpen = isPauseMenu(uiManager);
            canvas.enabled = pauseMenuOpen;
            raycaster.enabled = pauseMenuOpen;

            bool connected = PlayerMultiplayer.state != 0 ||
                NetworkServer.active || NetworkClient.isConnected;
            bool connectionBusy = connected || NetworkClient.active;
            RefreshLegacyOptionsAvailability();
            bool directMode = connectionManager != null &&
                connectionManager.SelectedMode == MultiplayerManager.ConnectionMode.DirectIP;

            if (pauseMenuOpen)
            {
                setStatus();

                options.SetActive(!connectionBusy || NetworkServer.active);
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

    CanvasGroup[] legacyOptionRows;

    void RefreshLegacyOptionsAvailability()
    {
        if (checks == null || existingOptionsPanel == null)
            return;
        if (legacyOptionRows == null)
            legacyOptionRows = new CanvasGroup[Mathf.Min(6, checks.Length)];

        bool editable = PlayerMultiplayer.state == 0 &&
            !NetworkServer.active && !NetworkClient.active && !NetworkClient.isConnected;
        for (int i = 0; i < legacyOptionRows.Length; i++)
        {
            if (legacyOptionRows[i] == null)
            {
                if (checks[i] == null) continue;
                // Each serialized checkmark belongs to one Case row beneath Options.
                // Dim the whole row (label and checkmark), not the options panel itself.
                Transform row = checks[i].transform;
                while (row != null && row.parent != existingOptionsPanel.transform)
                    row = row.parent;
                if (row == null) continue;
                legacyOptionRows[i] = row.GetComponent<CanvasGroup>();
                if (legacyOptionRows[i] == null)
                    legacyOptionRows[i] = row.gameObject.AddComponent<CanvasGroup>();
            }
            legacyOptionRows[i].alpha = editable ? 1f : 0.45f;
            legacyOptionRows[i].interactable = editable;
        }
    }

    GameObject respawnSettingsPanel;
    GameObject existingOptionsPanel;
    UnityEngine.UI.Slider[] respawnSliders;
    UnityEngine.UI.Toggle[] respawnToggles;
    Text[] respawnValueLabels;
    bool refreshingRespawnSettings;

    bool CanEditRespawnSettings()
    {
        return NetworkServer.active || !NetworkClient.active;
    }

    RectTransform SettingsRect(string name, Transform parent, Vector2 size, Vector2 position)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        return rect;
    }

    Text SettingsText(Transform parent, string value, Vector2 size, Vector2 position, int fontSize = 18)
    {
        var rect = SettingsRect(value, parent, size, position);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = status != null ? status.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    void BuildRespawnSettings()
    {
        if (checks == null || checks.Length == 0 || checks[0] == null || options == null || canvas == null)
            return;
        // Find the existing options layout from its serialized checkmark; no prefab-name dependency.
        Transform parent = checks[0].transform.parent;
        while (parent != null && parent.GetComponent<VerticalLayoutGroup>() == null)
            parent = parent.parent;
        if (parent == null) return;
        existingOptionsPanel = parent.gameObject;
        var launch = Instantiate(options, parent);
        launch.name = "Respawn and travel settings";
        launch.GetComponent<UnityEngine.UI.Button>().onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        launch.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(OpenRespawnSettings);
        SetButtonText(launch, "Respawn / travel");
        launch.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        launch.GetComponent<RectTransform>().sizeDelta = new Vector2(180, 40);
        launch.SetActive(true);

        var panel = SettingsRect("Respawn travel panel", canvas.transform, new Vector2(590, 435), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
        respawnSettingsPanel = panel.gameObject;
        SettingsText(panel, "Respawn / travel", new Vector2(530, 32), new Vector2(0, -16), 23);
        SettingsText(panel, "Host settings - applied to all players", new Vector2(530, 26), new Vector2(0, -50), 16);
        string[] names = { "Hold to revive", "Wait before self-respawn", "Revive health", "Respawn health" };
        int[] min = { 1, 5, 10, 10 }, max = { 10, 30, 50, 50 };
        respawnSliders = new UnityEngine.UI.Slider[4];
        respawnValueLabels = new Text[4];
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            float y = -90 - i * 47;
            respawnValueLabels[i] = SettingsText(panel, names[i], new Vector2(320, 30), new Vector2(-105, y));
            var sliderRect = SettingsRect(names[i], panel, new Vector2(200, 26), new Vector2(160, y));
            var background = sliderRect.gameObject.AddComponent<UnityEngine.UI.Image>();
            background.color = new Color(0.23f, 0.23f, 0.23f);
            var slider = sliderRect.gameObject.AddComponent<UnityEngine.UI.Slider>();
            var handle = SettingsRect("Handle", sliderRect, new Vector2(14, 26), Vector2.zero);
            var handleImage = handle.gameObject.AddComponent<UnityEngine.UI.Image>();
            handleImage.color = new Color(0.8f, 0.7f, 0.4f);
            handle.anchorMin = new Vector2(0, 0);
            handle.anchorMax = new Vector2(0, 1);
            handle.pivot = new Vector2(0.5f, 0.5f);
            handle.sizeDelta = new Vector2(14, 0);
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min[i]; slider.maxValue = max[i]; slider.wholeNumbers = true;
            slider.onValueChanged.AddListener(value => ChangeRespawnSetting(index, (int)value));
            respawnSliders[i] = slider;
        }
        respawnToggles = new UnityEngine.UI.Toggle[2];
        string[] toggles = { "Respawn outside dungeons", "Party travel directly inside dungeons" };
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            float y = -286 - i * 38;
            var row = SettingsRect(toggles[i], panel, new Vector2(530, 30), new Vector2(0, y));
            var toggle = row.gameObject.AddComponent<UnityEngine.UI.Toggle>();
            var box = SettingsRect("Box", row, new Vector2(25, 25), new Vector2(-250, 0));
            var background = box.gameObject.AddComponent<UnityEngine.UI.Image>();
            background.color = new Color(0.3f, 0.3f, 0.3f);
            var check = SettingsRect("Checked", box, new Vector2(17, 17), new Vector2(0, -4));
            var checkImage = check.gameObject.AddComponent<UnityEngine.UI.Image>();
            checkImage.color = new Color(0.8f, 0.7f, 0.4f);
            toggle.targetGraphic = background; toggle.graphic = checkImage;
            SettingsText(row, toggles[i], new Vector2(485, 30), new Vector2(23, 0)).raycastTarget = true;
            toggle.onValueChanged.AddListener(value => ChangeRespawnToggle(index, value));
            respawnToggles[i] = toggle;
        }
        var back = Instantiate(options, panel);
        back.name = "Back";
        var backRect = back.GetComponent<RectTransform>();
        backRect.anchorMin = backRect.anchorMax = new Vector2(0.5f, 0f);
        backRect.pivot = new Vector2(0.5f, 0f);
        backRect.anchoredPosition = new Vector2(150, 18);
        backRect.sizeDelta = new Vector2(120, 38);
        var button = back.GetComponent<UnityEngine.UI.Button>();
        button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.onClick.AddListener(() => { respawnSettingsPanel.SetActive(false); existingOptionsPanel.SetActive(true); });
        SetButtonText(back, "Back");
        back.SetActive(true);
        var defaults = Instantiate(back, panel);
        defaults.name = "Restore respawn defaults";
        var defaultsRect = defaults.GetComponent<RectTransform>();
        defaultsRect.anchoredPosition = new Vector2(-70, 18);
        defaultsRect.sizeDelta = new Vector2(230, 38);
        var defaultsButton = defaults.GetComponent<UnityEngine.UI.Button>();
        defaultsButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        defaultsButton.onClick.AddListener(() =>
        {
            if (!CanEditRespawnSettings()) return;
            OptionsMultiplayer.RestoreRespawnDefaults();
            PublishRespawnSettings();
        });
        SetButtonText(defaults, "Restore defaults");
        respawnSettingsPanel.SetActive(false);
    }

    void OpenRespawnSettings()
    {
        if (!CanEditRespawnSettings() || respawnSettingsPanel == null) return;
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        RefreshRespawnSettings();
        existingOptionsPanel.SetActive(false);
        respawnSettingsPanel.SetActive(true);
        respawnSettingsPanel.transform.SetAsLastSibling();
        var canvasRect = canvas.GetComponent<RectTransform>();
        float scale = Mathf.Min(1f, Mathf.Min(canvasRect.rect.width / 620f, canvasRect.rect.height / 465f));
        respawnSettingsPanel.transform.localScale = Vector3.one * scale;
    }

    void RefreshRespawnSettings()
    {
        refreshingRespawnSettings = true;
        int[] values = { OptionsMultiplayer.reviveHoldSeconds, OptionsMultiplayer.manualRespawnSeconds,
            OptionsMultiplayer.reviveHealthPercent, OptionsMultiplayer.respawnHealthPercent };
        string[] names = { "Hold to revive", "Wait before self-respawn", "Revive health", "Respawn health" };
        for (int i = 0; i < 4; i++)
        {
            respawnSliders[i].value = values[i];
            respawnValueLabels[i].text = names[i] + ": " + values[i] + (i < 2 ? " s" : "%");
        }
        respawnToggles[0].isOn = OptionsMultiplayer.respawnOutsideDungeon;
        respawnToggles[1].isOn = OptionsMultiplayer.partyTravelInsideDungeon;
        refreshingRespawnSettings = false;
    }

    void ChangeRespawnSetting(int index, int value)
    {
        if (refreshingRespawnSettings || !CanEditRespawnSettings()) return;
        switch (index)
        {
            case 0: OptionsMultiplayer.reviveHoldSeconds = value; break;
            case 1: OptionsMultiplayer.manualRespawnSeconds = value; break;
            case 2: OptionsMultiplayer.reviveHealthPercent = value; break;
            case 3: OptionsMultiplayer.respawnHealthPercent = value; break;
        }
        PublishRespawnSettings();
    }

    void ChangeRespawnToggle(int index, bool value)
    {
        if (refreshingRespawnSettings || !CanEditRespawnSettings()) return;
        if (index == 0) OptionsMultiplayer.respawnOutsideDungeon = value;
        else OptionsMultiplayer.partyTravelInsideDungeon = value;
        PublishRespawnSettings();
    }

    void PublishRespawnSettings()
    {
        OptionsMultiplayer.SaveLocalRespawnSettings();
        RefreshRespawnSettings();
        var local = PlayerMultiplayer.GetLocalPlayer();
        if (NetworkServer.active && local != null)
            local.rpcImportOptions(OptionsMultiplayer.Export());
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
        if (NetworkServer.active || NetworkClient.active) return;
        OptionsMultiplayer.timeHost = !OptionsMultiplayer.timeHost;
        checks[0].SetActive(OptionsMultiplayer.timeHost);
    }

    public void toggleName()
    {
        if (NetworkServer.active || NetworkClient.active) return;
        OptionsMultiplayer.displayName = !OptionsMultiplayer.displayName;
        checks[1].SetActive(OptionsMultiplayer.displayName);
    }

    public void toggleHighestLevel()
    {
        if (NetworkServer.active || NetworkClient.active) return;
        OptionsMultiplayer.useHighestLevel = !OptionsMultiplayer.useHighestLevel;
        checks[2].SetActive(OptionsMultiplayer.useHighestLevel);
    }

    public void toggleSendLocation()
    {
        if (NetworkServer.active || NetworkClient.active) return;
        OptionsMultiplayer.sendLocation = !OptionsMultiplayer.sendLocation;
        checks[3].SetActive(OptionsMultiplayer.sendLocation);
    }

    public void toggleSendMessage()
    {
        if (NetworkServer.active || NetworkClient.active) return;
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
        RefreshLegacyOptionsAvailability();
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
        OptionsMultiplayer.EnsureLocalRespawnSettings();
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
