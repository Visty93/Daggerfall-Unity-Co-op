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
    // Appearance controls live in their own local-only submenu. Only the launcher
    // belongs to the original vertical options list.
    GameObject paperdollDisplayButton;
    GameObject paperdollSettingsPanel;
    GameObject paperdollModeButton;
    GameObject paperdollTurnButton;
    GameObject paperdollBobButton;
    GameObject paperdollAttackButton;
    // Host-controlled DEFAULT for future parties. Live party policies are only edited
    // in the Party window by its current leader, never from this menu.
    GameObject hostQuestDefaultsLauncher;
    GameObject hostQuestDefaultsPanel;
    GameObject hostQuestDefaultsModeButton;
    GameObject hostClientConsoleCommandsButton;
    // Extensible Loot settings submenu. The only setting currently available
    // is world-container synchronization. Do not add standalone UI components.
    GameObject lootLauncher;
    GameObject lootPanel;
    GameObject worldContainerSyncButton;
    const string HostQuestDefaultMigrationPref = "DFUCoop.QuestSync.HostDefaultVisibleV1";
    Text respawnAccessLabel;
    UnityEngine.UI.Button respawnDefaultsButton;
    GameObject advancedRespawnSettingsPanel;
    GameObject advancedWipeToggleButton;
    GameObject advancedSanctuaryToggleButton;
    GameObject advancedFeeToggleButton;
    UnityEngine.UI.Slider advancedGoldSlider;
    UnityEngine.UI.Slider advancedRadiusSlider;
    Text advancedGoldLabel;
    Text advancedRadiusLabel;
    bool wasPauseMenuOpen;
    bool lastConnectedLayoutState;
    float lastOptionsCanvasHeight = -1f;

    GameObject hostModMatchingButton;

    // Runtime-only menu hierarchy. Keep the original prefab and legacy callbacks
    // intact; no scene/prefab edits and no changes to network options are needed.
    GameObject hostSettingsPanel;
    GameObject hostGeneralSettingsPanel;
    GameObject hostServerRulesPanel;
    Text defaultQuestExplanation;
    Text modMatchingExplanation;
    GameObject[] generalOptionButtons;
    UnityEngine.UI.Slider restTogetherRangeSlider;
    Text restTogetherRangeLabel;
    GameObject defaultLeaderTimeButton;
    GameObject respawnLauncher;

    void Start()
    {
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        // First upgrade from the old, now-hidden setting: do not leave players with
        // Off or Share-only forever without a visible explanation. Run this once,
        // only while disconnected, before initializing the new host-only control.
        MigrateOldHiddenHostQuestDefault();
        OptionsMultiplayer.EnsureLocalQuestSyncSettings();
        OptionsMultiplayer.EnsureLocalConsoleSettings();
        ResolveConnectionManager();
        EnsurePartyButton();
        EnsureConnectionModeButton();
        RenamePartyTimeSetting();
        BuildRespawnSettings();
        BuildPaperdollDisplaySetting();
        BuildHostQuestDefaultsSetting();
        BuildLootSettings();
        BuildHostModMatching();
        BuildReorganizedSettingsNavigation();
        RefreshLegacyOptionsAvailability();
        FitOptionsPanelToCanvas();
        StartCoroutine(Check());
    }

    IEnumerator Check()
    {
        gameUI = GameObject.Find("DaggerfallUI").GetComponent<DaggerfallUI>();
        UserInterfaceManager uiManager = gameUI.UserInterfaceManager;

        while (true)
        {
            OptionsMultiplayer.EnsureLocalRespawnSettings();
            MigrateOldHiddenHostQuestDefault();
            OptionsMultiplayer.EnsureLocalQuestSyncSettings();
            OptionsMultiplayer.EnsureLocalConsoleSettings();
            ResolveConnectionManager();
            EnsurePartyButton();
            EnsureConnectionModeButton();

            bool pauseMenuOpen = isPauseMenu(uiManager);
            // When the pause window is closed (or a DFU message box becomes the top
            // window), leave no stale HUD-only submenu hidden behind the next open.
            if (!pauseMenuOpen && wasPauseMenuOpen)
                CloseRuntimeOptionsPanels();
            wasPauseMenuOpen = pauseMenuOpen;
            canvas.enabled = pauseMenuOpen;
            raycaster.enabled = pauseMenuOpen;

            bool connected = PlayerMultiplayer.state != 0 ||
                NetworkServer.active || NetworkClient.isConnected;
            bool connectionBusy = connected || NetworkClient.active;
            RefreshLegacyOptionsAvailability();
            RefreshPaperdollDisplaySetting();
            RefreshHostQuestDefaultsSetting();
            if (hostGeneralSettingsPanel != null && hostGeneralSettingsPanel.activeSelf)
                RefreshGeneralHostSettings();
            RefreshLootSettings();
            RefreshHostModMatching();
            if (pauseMenuOpen)
                FitVisibleSettingsPage();
            if (respawnSettingsPanel != null && respawnSettingsPanel.activeSelf)
                RefreshRespawnSettings();
            if (advancedRespawnSettingsPanel != null && advancedRespawnSettingsPanel.activeSelf)
                RefreshAdvancedRespawnSettings();
            bool sessionActive = NetworkServer.active || NetworkClient.active ||
                NetworkClient.isConnected || PlayerMultiplayer.state != 0;
            float optionsCanvasHeight = canvas != null && canvas.GetComponent<RectTransform>() != null
                ? canvas.GetComponent<RectTransform>().rect.height : 0f;
            if (lastConnectedLayoutState != sessionActive ||
                Mathf.Abs(lastOptionsCanvasHeight - optionsCanvasHeight) > 1f)
            {
                lastConnectedLayoutState = sessionActive;
                lastOptionsCanvasHeight = optionsCanvasHeight;
                FitOptionsPanelToCanvas();
            }
            bool directMode = connectionManager != null &&
                connectionManager.SelectedMode == MultiplayerManager.ConnectionMode.DirectIP;

            if (pauseMenuOpen)
            {
                setStatus();

                // Client-side appearance remains accessible while connected.
                // The other network-wide settings are individually guarded.
                options.SetActive(true);
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

    // Page style follows the other runtime menus; everything is constructed from
    // existing UI assets, so the uploaded prefab needs no editing or replacement.
    RectTransform CreateOptionsPage(string name, string heading, float width, float height)
    {
        RectTransform panel = SettingsRect(name, canvas.transform,
            new Vector2(width, height), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color =
            new Color(0.08f, 0.08f, 0.08f, 0.98f);
        SettingsText(panel, heading, new Vector2(width - 55, 37),
            new Vector2(0, -13), 23);
        return panel;
    }

    void ScaleOptionsPage(GameObject page, float width, float height)
    {
        if (page == null || canvas == null) return;
        page.transform.SetAsLastSibling();
        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect == null) return;
        // Use one visual scale for all settings pages instead of a different scale
        // based on each page's size. That caused the text and buttons to jump in
        // size every time users opened another category. At narrow resolutions,
        // use the largest page's footprint to keep every panel on-screen.
        float canvasScale = Mathf.Max(0.01f, canvas.scaleFactor);
        float availableWidth = Mathf.Min(canvasRect.rect.width, Screen.width / canvasScale);
        float availableHeight = Mathf.Min(canvasRect.rect.height, Screen.height / canvasScale);
        const float commonWidth = 620f;
        const float commonHeight = 690f;
        float uniformScale = Mathf.Min(1f, Mathf.Min(
            Mathf.Max(0.1f, availableWidth - 28f) / commonWidth,
            Mathf.Max(0.1f, availableHeight - 40f) / commonHeight));
        page.transform.localScale = Vector3.one * uniformScale;
    }

    void FitVisibleSettingsPage()
    {
        // Recompute when users resize the Game view or change resolution while
        // already inside a submenu. Do not change the layout of the old HUD.
        if (hostSettingsPanel != null && hostSettingsPanel.activeSelf)
            ScaleOptionsPage(hostSettingsPanel, 510f, 475f);
        else if (hostGeneralSettingsPanel != null && hostGeneralSettingsPanel.activeSelf)
            ScaleOptionsPage(hostGeneralSettingsPanel, 560f, 690f);
        else if (hostServerRulesPanel != null && hostServerRulesPanel.activeSelf)
            ScaleOptionsPage(hostServerRulesPanel, 550f, 425f);
        else if (hostQuestDefaultsPanel != null && hostQuestDefaultsPanel.activeSelf)
            ScaleOptionsPage(hostQuestDefaultsPanel, 550f, 500f);
        else if (paperdollSettingsPanel != null && paperdollSettingsPanel.activeSelf)
            ScaleOptionsPage(paperdollSettingsPanel, 510f, 435f);
        else if (respawnSettingsPanel != null && respawnSettingsPanel.activeSelf)
            ScaleOptionsPage(respawnSettingsPanel, 590f, 535f);
        else if (advancedRespawnSettingsPanel != null && advancedRespawnSettingsPanel.activeSelf)
            ScaleOptionsPage(advancedRespawnSettingsPanel, 590f, 465f);
        else if (lootPanel != null && lootPanel.activeSelf)
            ScaleOptionsPage(lootPanel, 480f, 230f);
    }

    void BuildReorganizedSettingsNavigation()
    {
        if (canvas == null || existingOptionsPanel == null || options == null)
            return;

        // The original six "Case" entries are retained for prefab binding and
        // existing toggle methods, but their cramped rows are no longer visible.
        if (checks != null)
        {
            for (int i = 0; i < checks.Length; ++i)
            {
                if (checks[i] == null) continue;
                Transform row = checks[i].transform;
                while (row != null && row.parent != existingOptionsPanel.transform)
                    row = row.parent;
                if (row != null) row.gameObject.SetActive(false);
            }
        }
        // They are now reached through the Host settings categories instead.
        if (respawnLauncher != null) respawnLauncher.SetActive(false);
        if (lootLauncher != null) lootLauncher.SetActive(false);
        // The mod-matching toggle is physically moved into Server rules below.

        RectTransform hub = CreateOptionsPage("Host settings navigation", "Host settings", 510, 475);
        hostSettingsPanel = hub.gameObject;
        SettingsText(hub, "Server settings and starting rules for new parties.",
            new Vector2(450, 30), new Vector2(0, -57), 15);
        CreateAppearanceOptionButton(hub, "General host settings", "General settings...", -100f,
            OpenGeneralHostSettings);
        CreateAppearanceOptionButton(hub, "Party default settings", "Party defaults...", -166f,
            OpenHostQuestDefaultsSetting);
        CreateAppearanceOptionButton(hub, "Respawn and travel category", "Respawn / travel...", -232f,
            OpenRespawnSettings);
        CreateAppearanceOptionButton(hub, "Loot category", "Loot settings...", -298f,
            OpenLootSettings);
        CreateAppearanceOptionButton(hub, "Server rules category", "Server rules...", -364f,
            OpenServerRules);
        GameObject back = CreateAppearanceOptionButton(hub, "Back to multiplayer options",
            "Back", -418f, CloseHostSettings);
        back.GetComponent<RectTransform>().sizeDelta = new Vector2(135, 36);
        hostSettingsPanel.SetActive(false);

        RectTransform general = CreateOptionsPage("General server settings", "General settings", 560, 690);
        hostGeneralSettingsPanel = general.gameObject;
        SettingsText(general, "These options are chosen by the host before starting a session.",
            new Vector2(515, 40), new Vector2(0, -51), 15);
        // Highest-level scaling was already obsolete, but the original
        // world-map discovery switch (sendLocation) is still active.
        // It is separate from party-scoped town building discoveries.
        OptionsMultiplayer.useHighestLevel = false;
        generalOptionButtons = new GameObject[4];
        string[] names = { "Show player names", "Share world-map locations",
            "Player arrival messages", "Synchronize townspeople" };
        string[] descriptions = {
            "Show character names above other players.",
            "When a player visits a named location, reveal it on party members'\n" +
            "world maps. Town building discoveries are a separate feature.",
            "Show a message when another player appears in your game.",
            "Make moving townspeople appear and move the same way for everyone." };
        for (int i = 0; i < generalOptionButtons.Length; ++i)
        {
            int index = i;
            float y = -96f - i * 103f;
            generalOptionButtons[i] = CreateAppearanceOptionButton(general,
                "General setting " + i, names[i] + ": On", y, () => ToggleGeneralHostSetting(index));
            generalOptionButtons[i].GetComponent<RectTransform>().sizeDelta = new Vector2(430, 39);
            SettingsText(general, descriptions[i], new Vector2(520, 45),
                new Vector2(0, y - 43f), 14);
        }
        restTogetherRangeLabel = SettingsText(general,
            "Rest Together distance: 10 m", new Vector2(285, 32), new Vector2(-115, -523f), 16);
        restTogetherRangeSlider = CreateAdvancedRespawnSlider(general,
            "Rest Together distance (server-wide)", 1, 10, -523f,
            value => ChangeHostRestTogetherRange(value));
        SettingsText(general,
            "Applies to every party, outdoors and inside buildings/dungeons.\n" +
            "Same location or dungeon is required. Set before hosting.",
            new Vector2(525, 53), new Vector2(0, -571f), 14);
        GameObject generalBack = CreateAppearanceOptionButton(general,
            "Back to host settings", "Back", -637f, CloseGeneralHostSettings);
        generalBack.GetComponent<RectTransform>().sizeDelta = new Vector2(135, 32);
        hostGeneralSettingsPanel.SetActive(false);

        RectTransform server = CreateOptionsPage("Server rules settings", "Server rules", 550, 425);
        hostServerRulesPanel = server.gameObject;
        SettingsText(server,
            "Control client console access and choose whether joining players\n" +
            "need the same enabled mods as the host.",
            new Vector2(510, 64), new Vector2(0, -51), 15);
        hostClientConsoleCommandsButton = CreateAppearanceOptionButton(server,
            "Client developer console policy", "Client console commands: Disabled", -127f,
            ToggleHostClientConsoleCommands);
        SettingsText(server,
            "Disabled stops clients from using console commands.\n" +
            "The host can always use them.",
            new Vector2(505, 47), new Vector2(0, -179f), 14);
        if (hostModMatchingButton != null)
        {
            hostModMatchingButton.transform.SetParent(server, false);
            var rect = hostModMatchingButton.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0, -238f);
                rect.sizeDelta = new Vector2(345, 43);
                rect.localScale = Vector3.one;
            }
            hostModMatchingButton.SetActive(true);
        }
        modMatchingExplanation = SettingsText(server, string.Empty,
            new Vector2(510, 85), new Vector2(0, -285f), 14);
        GameObject serverBack = CreateAppearanceOptionButton(server,
            "Back to host settings", "Back", -373f, CloseServerRules);
        serverBack.GetComponent<RectTransform>().sizeDelta = new Vector2(135, 34);
        hostServerRulesPanel.SetActive(false);

        // Keep the host-settings launcher and player-local appearance launcher at
        // top level, with the prefab's existing Back button below them.
        if (paperdollDisplayButton != null) paperdollDisplayButton.transform.SetSiblingIndex(0);
        if (hostQuestDefaultsLauncher != null) hostQuestDefaultsLauncher.transform.SetSiblingIndex(1);
        RefreshGeneralHostSettings();
        RefreshHostQuestDefaultsSetting();
        RefreshHostModMatching();
        FitOptionsPanelToCanvas();
    }

    void OpenHostSettings()
    {
        if (hostSettingsPanel == null) return;
        if (existingOptionsPanel != null) existingOptionsPanel.SetActive(false);
        if (hostQuestDefaultsPanel != null) hostQuestDefaultsPanel.SetActive(false);
        if (hostGeneralSettingsPanel != null) hostGeneralSettingsPanel.SetActive(false);
        if (hostServerRulesPanel != null) hostServerRulesPanel.SetActive(false);
        if (respawnSettingsPanel != null) respawnSettingsPanel.SetActive(false);
        if (lootPanel != null) lootPanel.SetActive(false);
        hostSettingsPanel.SetActive(true);
        ScaleOptionsPage(hostSettingsPanel, 510, 475);
    }

    void CloseHostSettings()
    {
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        if (existingOptionsPanel != null) existingOptionsPanel.SetActive(true);
        FitOptionsPanelToCanvas();
    }

    void OpenGeneralHostSettings()
    {
        if (hostGeneralSettingsPanel == null) return;
        RefreshGeneralHostSettings();
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        hostGeneralSettingsPanel.SetActive(true);
        ScaleOptionsPage(hostGeneralSettingsPanel, 560, 690);
    }

    void CloseGeneralHostSettings()
    {
        if (hostGeneralSettingsPanel != null) hostGeneralSettingsPanel.SetActive(false);
        OpenHostSettings();
    }

    void OpenServerRules()
    {
        if (hostServerRulesPanel == null) return;
        RefreshHostQuestDefaultsSetting();
        RefreshHostModMatching();
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        hostServerRulesPanel.SetActive(true);
        ScaleOptionsPage(hostServerRulesPanel, 550, 425);
    }

    void CloseServerRules()
    {
        if (hostServerRulesPanel != null) hostServerRulesPanel.SetActive(false);
        OpenHostSettings();
    }

    void CloseRespawnSettings()
    {
        if (respawnSettingsPanel != null) respawnSettingsPanel.SetActive(false);
        OpenHostSettings();
    }

    void ToggleGeneralHostSetting(int index)
    {
        if (NetworkServer.active || NetworkClient.active || NetworkClient.isConnected ||
            PlayerMultiplayer.state != 0) return;
        switch (index)
        {
            case 0: toggleName(); break;
            case 1: toggleSendLocation(); break;
            case 2: toggleSendMessage(); break;
            case 3: toggleMobileNpcSync(); break;
        }
        RefreshGeneralHostSettings();
    }

    void RefreshGeneralHostSettings()
    {
        if (generalOptionButtons == null) return;
        bool editable = !NetworkServer.active && !NetworkClient.active &&
            !NetworkClient.isConnected && PlayerMultiplayer.state == 0;
        string[] labels = {
            "Show player names: " + (OptionsMultiplayer.displayName ? "On" : "Off"),
            "Share world-map locations: " + (OptionsMultiplayer.sendLocation ? "On" : "Off"),
            "Player arrival messages: " + (OptionsMultiplayer.sendMessage ? "On" : "Off"),
            "Synchronize townspeople: " + (OptionsMultiplayer.mobileNpcSync ? "On" : "Off")
        };
        for (int i = 0; i < generalOptionButtons.Length; ++i)
        {
            if (generalOptionButtons[i] == null) continue;
            SetButtonText(generalOptionButtons[i], labels[i]);
            var button = generalOptionButtons[i].GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.interactable = editable;
        }
        if (restTogetherRangeSlider != null)
        {
            restTogetherRangeSlider.SetValueWithoutNotify(OptionsMultiplayer.restTogetherRadiusMeters);
            restTogetherRangeSlider.interactable = editable;
        }
        if (restTogetherRangeLabel != null)
            restTogetherRangeLabel.text = "Rest Together distance: " + OptionsMultiplayer.restTogetherRadiusMeters + " m";
    }

    void ChangeHostRestTogetherRange(int value)
    {
        // This all-party host policy is configured before starting a game.
        if (NetworkServer.active || NetworkClient.active || NetworkClient.isConnected ||
            PlayerMultiplayer.state != 0) return;
        OptionsMultiplayer.SetHostRestTogetherRadiusMeters(value);
        RefreshGeneralHostSettings();
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
        respawnLauncher = launch;
        launch.name = "Respawn and travel settings";
        launch.GetComponent<UnityEngine.UI.Button>().onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        launch.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(OpenRespawnSettings);
        SetButtonText(launch, "Respawn / travel");
        launch.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        launch.GetComponent<RectTransform>().sizeDelta = new Vector2(180, 40);
        launch.SetActive(true);

        var panel = SettingsRect("Respawn travel panel", canvas.transform, new Vector2(590, 535), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
        respawnSettingsPanel = panel.gameObject;
        SettingsText(panel, "Respawn / travel", new Vector2(530, 32), new Vector2(0, -16), 23);
        respawnAccessLabel = SettingsText(panel, "Host settings - applied to all players", new Vector2(530, 26), new Vector2(0, -50), 16);
        string[] names = { "Hold to revive", "Wait before self-respawn", "Revive health", "Respawn health", "Revive fatigue", "Respawn fatigue" };
        int[] min = { 1, 5, 10, 10, 10, 10 }, max = { 10, 30, 50, 50, 50, 50 };
        respawnSliders = new UnityEngine.UI.Slider[6];
        respawnValueLabels = new Text[6];
        for (int i = 0; i < 6; i++)
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
            float y = -380 - i * 38;
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
        button.onClick.AddListener(CloseRespawnSettings);
        SetButtonText(back, "Back");
        back.SetActive(true);
        var defaults = Instantiate(back, panel);
        defaults.name = "Restore respawn defaults";
        var defaultsRect = defaults.GetComponent<RectTransform>();
        defaultsRect.anchoredPosition = new Vector2(-70, 18);
        defaultsRect.sizeDelta = new Vector2(230, 38);
        var defaultsButton = defaults.GetComponent<UnityEngine.UI.Button>();
        respawnDefaultsButton = defaultsButton;
        defaultsButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        defaultsButton.onClick.AddListener(() =>
        {
            if (!CanEditRespawnSettings()) return;
            OptionsMultiplayer.RestoreRespawnDefaults();
            PublishRespawnSettings();
        });
        SetButtonText(defaults, "Restore defaults");
        respawnSettingsPanel.SetActive(false);
        BuildAdvancedRespawnSettings();
    }

    // Submenu deliberately lives in HudMultiplayer, beside the existing
    // Respawn/Travel and Paperdoll submenus. The launcher is always available,
    // including on clients, so the active policy can be read without changing it.
    // Compact, host-controlled loot submenu. One setting for now; more can be
    // added here later without a separate UI component or a prefab change.
    ModCompatibilityAuthenticator HostModAuthenticator()
    {
        return connectionManager == null ? null :
            connectionManager.GetComponent<ModCompatibilityAuthenticator>();
    }

    bool CanConfigureHostMods()
    {
        return !DfuDedicatedServerOptions.Requested &&
            (NetworkServer.active || (!NetworkClient.active && PlayerMultiplayer.state == 0));
    }

    void BuildHostModMatching()
    {
        if (options == null || existingOptionsPanel == null) return;
        Transform parent = existingOptionsPanel.transform;
        hostModMatchingButton = Instantiate(options, parent);
        hostModMatchingButton.name = "Host mod matching";
        hostModMatchingButton.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        var rect = hostModMatchingButton.GetComponent<RectTransform>();
        if (rect != null) rect.sizeDelta = new Vector2(220, 40);
        var button = hostModMatchingButton.GetComponent<UnityEngine.UI.Button>();
        if (button != null)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            button.onClick.AddListener(() =>
            {
                var auth = HostModAuthenticator();
                if (!CanConfigureHostMods() || auth == null) return;
                auth.SetListenHostExact(!auth.ListenHostExact);
                RefreshHostModMatching();
            });
        }
        RefreshHostModMatching();
    }

    void RefreshHostModMatching()
    {
        if (hostModMatchingButton == null) return;
        bool editable = CanConfigureHostMods();
        var auth = HostModAuthenticator();
        var button = hostModMatchingButton.GetComponent<UnityEngine.UI.Button>();
        if (button != null) button.interactable = editable && auth != null;
        SetButtonText(hostModMatchingButton, !editable && NetworkClient.active && !NetworkServer.active
            ? "Mod matching: Host-controlled"
            : "Mod matching: " + (auth != null ? auth.ListenHostModeLabel : "Relaxed"));
        if (modMatchingExplanation != null)
        {
            string mode = auth != null ? auth.ListenHostModeLabel : "Relaxed";
            bool remoteClient = NetworkClient.active && !NetworkServer.active;
            modMatchingExplanation.text = remoteClient
                ? "The host decides whether players need matching mods.\n" +
                  "Changing this setting affects new connections only."
                : mode == "Exact"
                ? "Exact: Joining players need the same enabled mods and versions.\n" +
                  "Changing this setting affects new connections only."
                : mode == "Approved"
                ? "This server uses a separately configured mod policy.\n" +
                  "Changing this setting affects new connections only."
                : "Relaxed: Players do not need an identical list of mods to join.\n" +
                  "Changing this setting affects new connections only.";
        }
    }

    void BuildLootSettings()
    {
        if (options == null || existingOptionsPanel == null || canvas == null)
            return;

        Transform parent = existingOptionsPanel.transform;
        lootLauncher = Instantiate(options, parent);
        lootLauncher.name = "Loot settings";
        lootLauncher.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        var launchRect = lootLauncher.GetComponent<RectTransform>();
        if (launchRect != null) launchRect.sizeDelta = new Vector2(220, 40);
        var launchButton = lootLauncher.GetComponent<UnityEngine.UI.Button>();
        if (launchButton != null)
        {
            launchButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            launchButton.onClick.AddListener(OpenLootSettings);
        }
        SetButtonText(lootLauncher, "Loot settings...");
        lootLauncher.SetActive(true);

        RectTransform panel = SettingsRect("Loot settings panel", canvas.transform,
            new Vector2(480, 230), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color =
            new Color(0.08f, 0.08f, 0.08f, 0.98f);
        lootPanel = panel.gameObject;

        SettingsText(panel, "Loot settings", new Vector2(440, 30),
            new Vector2(0, -12), 19);

        worldContainerSyncButton = CreateAppearanceOptionButton(panel,
            "Container inventory sync selector", "Container loot sync: On", -60f,
            ToggleWorldContainerSync);
        var toggleRect = worldContainerSyncButton != null
            ? worldContainerSyncButton.GetComponent<RectTransform>() : null;
        if (toggleRect != null) toggleRect.sizeDelta = new Vector2(325, 38);

        // Brief player-facing summary of the container sync scope.
        SettingsText(panel,
            "Synchronizes interior containers and dungeon/exterior\n" +
            "loot piles. Enemy corpse loot, quest items, and shop\n" +
            "offers are unaffected.",
            new Vector2(450, 69), new Vector2(0, -111), 13);

        GameObject back = CreateAppearanceOptionButton(panel,
            "Back from loot settings", "Back", -185f,
            CloseLootSettings);
        var backRect = back != null ? back.GetComponent<RectTransform>() : null;
        if (backRect != null) backRect.sizeDelta = new Vector2(125, 34);
        lootPanel.SetActive(false);
        RefreshLootSettings();
    }

    bool CanEditWorldContainerOption()
    {
        return PlayerMultiplayer.state == 0 && !NetworkServer.active &&
            !NetworkClient.active && !NetworkClient.isConnected;
    }

    void RefreshLootSettings()
    {
        if (worldContainerSyncButton == null) return;
        bool editable = CanEditWorldContainerOption();
        SetButtonText(worldContainerSyncButton,
            "Container loot sync: " + (WorldContainerPolicy.Enabled ? "On" : "Off"));
        var button = worldContainerSyncButton.GetComponent<UnityEngine.UI.Button>();
        if (button != null) button.interactable = editable;
    }

    void OpenLootSettings()
    {
        if (lootPanel == null || existingOptionsPanel == null) return;
        RefreshLootSettings();
        existingOptionsPanel.SetActive(false);
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        lootPanel.SetActive(true);
        lootPanel.transform.SetAsLastSibling();
        ScaleOptionsPage(lootPanel, 480f, 230f);
    }

    void CloseLootSettings()
    {
        if (lootPanel != null) lootPanel.SetActive(false);
        OpenHostSettings();
    }

    void ToggleWorldContainerSync()
    {
        // Server setting is chosen before connecting, never changed mid-session.
        if (!CanEditWorldContainerOption()) return;
        WorldContainerPolicy.SetLocalPreference(!WorldContainerPolicy.Enabled);
        RefreshLootSettings();
    }

    // One-time migration: the old pre-connection quest mode selector was removed,
    // leaving prior Off/Share-only preferences invisible. Reset only those old local
    // preferences to Full, and mark the migration before any new deliberate choice.
    // Dedicated server JSON values and all active party modes are NEVER altered here.
    void MigrateOldHiddenHostQuestDefault()
    {
        if (PlayerPrefs.GetInt(HostQuestDefaultMigrationPref, 0) != 0)
            return;
        if (NetworkServer.active || NetworkClient.active || NetworkClient.isConnected ||
            PlayerMultiplayer.state != 0)
            return;
        OptionsMultiplayer.EnsureLocalQuestSyncSettings();
        OptionsMultiplayer.questSyncMode = OptionsMultiplayer.QuestSyncMode.Full;
        OptionsMultiplayer.SaveLocalQuestSyncSettings();
        PlayerPrefs.SetInt(HostQuestDefaultMigrationPref, 1);
        PlayerPrefs.Save();
    }

    // Offline players could become listen hosts, so the default is visible before
    // hosting. It remains available to an actual listen host, but is hidden from
    // connected clients and from the headless server (which uses ServerManager).
    bool CanConfigureHostQuestDefaults()
    {
        bool offline = !NetworkServer.active && !NetworkClient.active &&
            !NetworkClient.isConnected && PlayerMultiplayer.state == 0;
        bool listenHost = NetworkServer.active && NetworkClient.active &&
            PlayerMultiplayer.state != 2;
        return offline || listenHost;
    }

    void BuildHostQuestDefaultsSetting()
    {
        if (options == null || existingOptionsPanel == null || canvas == null)
            return;
        Transform parent = existingOptionsPanel.transform;
        hostQuestDefaultsLauncher = Instantiate(options, parent);
        hostQuestDefaultsLauncher.name = "Host settings launcher";
        hostQuestDefaultsLauncher.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        var launcherRect = hostQuestDefaultsLauncher.GetComponent<RectTransform>();
        if (launcherRect != null) launcherRect.sizeDelta = new Vector2(220, 40);
        var launch = hostQuestDefaultsLauncher.GetComponent<UnityEngine.UI.Button>();
        if (launch != null)
        {
            launch.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            launch.onClick.AddListener(OpenHostSettings);
        }
        SetButtonText(hostQuestDefaultsLauncher, "Host settings...");

        RectTransform panel = CreateOptionsPage("Party defaults panel", "Party defaults", 550, 500);
        hostQuestDefaultsPanel = panel.gameObject;
        SettingsText(panel,
            "These are the starting rules for NEW parties only.\n" +
            "Party leaders can change them later under Party > Manage.\n" +
            "Travel Together starts OFF. Leaders can enable it in Party > Manage.",
            new Vector2(500, 73), new Vector2(0, -54), 15);
        defaultLeaderTimeButton = CreateAppearanceOptionButton(panel,
            "Default time control for new parties", "Time: Leader only", -138f,
            ToggleDefaultPartyTime);
        SettingsText(panel,
            "Leader only: Party time follows the leader's clock.\n" +
            "A follower's rest advances time only when the leader is nearby.\n" +
            "Any member: Rest advances time even when the leader is away.",
            new Vector2(500, 81), new Vector2(0, -189), 14);
        hostQuestDefaultsModeButton = CreateAppearanceOptionButton(panel,
            "Default quest sync for new parties", "Quest sharing: Automatic", -286f,
            CycleHostQuestDefault);
        defaultQuestExplanation = SettingsText(panel,
            "Automatic: New quests are shared with the party and progress together.",
            new Vector2(500, 108), new Vector2(0, -337), 14);
        GameObject back = CreateAppearanceOptionButton(panel,
            "Back to host settings", "Back", -452f, CloseHostQuestDefaultsSetting);
        back.GetComponent<RectTransform>().sizeDelta = new Vector2(145, 36);
        hostQuestDefaultsPanel.SetActive(false);
        RefreshHostQuestDefaultsSetting();
    }

    void ToggleDefaultPartyTime()
    {
        // Original pre-connection restriction is preserved. This is a DEFAULT,
        // not an override of any existing party's current time policy.
        if (NetworkServer.active || NetworkClient.active || NetworkClient.isConnected ||
            PlayerMultiplayer.state != 0) return;
        toggleTimeHost();
        RefreshHostQuestDefaultsSetting();
    }

    void RefreshHostQuestDefaultsSetting()
    {
        if (hostQuestDefaultsLauncher != null)
            hostQuestDefaultsLauncher.SetActive(true);
        if (defaultLeaderTimeButton != null)
        {
            SetButtonText(defaultLeaderTimeButton, "Time: " +
                (OptionsMultiplayer.timeHost ? "Leader only" : "Any member"));
            var btn = defaultLeaderTimeButton.GetComponent<UnityEngine.UI.Button>();
            if (btn != null) btn.interactable = !NetworkServer.active &&
                !NetworkClient.active && !NetworkClient.isConnected && PlayerMultiplayer.state == 0;
        }
        if (hostQuestDefaultsModeButton != null)
        {
            string mode = "Automatic";
            string detail = "Automatic: New quests are shared with the party\n" +
                "and everyone progresses through shared quests together.";
            switch (OptionsMultiplayer.DefaultQuestSyncMode)
            {
                case OptionsMultiplayer.QuestSyncMode.SharedOnly:
                    mode = "Manual";
                    detail = "Manual: New quests stay private until someone uses\n" +
                        "Share Quest in their journal. Shared quests progress together.";
                    break;
                case OptionsMultiplayer.QuestSyncMode.Off:
                    mode = "Off";
                    detail = "Off: Quests remain private. Automatic sharing and\n" +
                        "the Share Quest button are disabled.";
                    break;
            }
            SetButtonText(hostQuestDefaultsModeButton, "Quest sharing: " + mode);
            if (defaultQuestExplanation != null)
                defaultQuestExplanation.text = detail;
            var btn = hostQuestDefaultsModeButton.GetComponent<UnityEngine.UI.Button>();
            if (btn != null) btn.interactable = CanConfigureHostQuestDefaults();
        }
        if (hostClientConsoleCommandsButton != null)
        {
            SetButtonText(hostClientConsoleCommandsButton,
                "Client console commands: " +
                (OptionsMultiplayer.allowClientConsoleCommands ? "Allowed" : "Disabled"));
            var btn = hostClientConsoleCommandsButton.GetComponent<UnityEngine.UI.Button>();
            if (btn != null) btn.interactable = CanConfigureHostQuestDefaults();
        }
    }

    void ToggleHostClientConsoleCommands()
    {
        if (!CanConfigureHostQuestDefaults()) return;
        OptionsMultiplayer.SetHostClientConsoleCommandsAllowed(
            !OptionsMultiplayer.allowClientConsoleCommands);
        RefreshHostQuestDefaultsSetting();

        if (!NetworkServer.active) return;
        // Broadcast on each client-owned player identity so the RPC is delivered
        // even when the host player itself is outside that client's interest range.
        string policy = OptionsMultiplayer.Export();
        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn == null || conn.identity == null) continue;
            PlayerMultiplayer player = conn.identity.GetComponent<PlayerMultiplayer>();
            if (player != null) player.rpcImportOptions(policy);
        }
    }

    void CycleHostQuestDefault()
    {
        if (!CanConfigureHostQuestDefaults()) return;
        var next = (OptionsMultiplayer.QuestSyncMode)
            (((int)OptionsMultiplayer.DefaultQuestSyncMode + 1) % 3);
        // Changes only the default for NEW parties; this setter does not send
        // PartyManager mode-change actions or affect the active party.
        OptionsMultiplayer.questSyncMode = next;
        // SaveLocalQuestSyncSettings() reads the live party-mode getter while a
        // listen host is connected, so it would silently persist the WRONG mode.
        // Save the new default itself instead. This is the existing option key;
        // it remains independent of the party's current network policy.
        PlayerPrefs.SetInt("DFUCoop.QuestSync.Mode", (int)next);
        PlayerPrefs.Save();
        RefreshHostQuestDefaultsSetting();
    }

    void OpenHostQuestDefaultsSetting()
    {
        if (hostQuestDefaultsPanel == null || existingOptionsPanel == null) return;
        RefreshHostQuestDefaultsSetting();
        existingOptionsPanel.SetActive(false);
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        hostQuestDefaultsPanel.SetActive(true);
        hostQuestDefaultsPanel.transform.SetAsLastSibling();
        ScaleOptionsPage(hostQuestDefaultsPanel, 550, 500);
    }

    void CloseHostQuestDefaultsSetting()
    {
        if (hostQuestDefaultsPanel != null) hostQuestDefaultsPanel.SetActive(false);
        OpenHostSettings();
    }

    // Available offline, for listen-hosts, and for every connected client.
    // This is local presentation only: it must never update synchronized host settings.
    void BuildPaperdollDisplaySetting()
    {
        if (options == null || existingOptionsPanel == null || canvas == null)
            return;

        Transform parent = existingOptionsPanel.transform;
        paperdollDisplayButton = Instantiate(options, parent);
        paperdollDisplayButton.name = "Player appearance settings";
        paperdollDisplayButton.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        RectTransform launcherRect = paperdollDisplayButton.GetComponent<RectTransform>();
        if (launcherRect != null) launcherRect.sizeDelta = new Vector2(220, 40);
        var launcherButton = paperdollDisplayButton.GetComponent<UnityEngine.UI.Button>();
        if (launcherButton != null)
        {
            launcherButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            launcherButton.onClick.AddListener(OpenPaperdollSettings);
        }
        SetButtonText(paperdollDisplayButton, "Player appearance...");
        paperdollDisplayButton.SetActive(true);

        RectTransform panel = SettingsRect("Player appearance panel", canvas.transform,
            new Vector2(510, 435), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
        paperdollSettingsPanel = panel.gameObject;
        SettingsText(panel, "Player appearance", new Vector2(450, 34),
            new Vector2(0, -16), 24);
        SettingsText(panel, "Local preference - each player can choose independently",
            new Vector2(450, 25), new Vector2(0, -54), 15);

        paperdollModeButton = CreateAppearanceOptionButton(panel, "Appearance mode",
            "Others: Animated sprites", -92f, () =>
            {
                PlayerAssets.UseRemotePaperdolls = !PlayerAssets.UseRemotePaperdolls;
                RefreshPaperdollDisplaySetting();
            });
        paperdollTurnButton = CreateAppearanceOptionButton(panel, "Paperdoll facing mode",
            "Paperdoll turn: Camera", -156f, () =>
            {
                PlayerAssets.RemotePaperdollTurnMode = (PlayerAssets.RemotePaperdollTurnMode + 1) % 3;
                RefreshPaperdollDisplaySetting();
            });
        paperdollBobButton = CreateAppearanceOptionButton(panel, "Paperdoll bob mode",
            "Paperdoll bob: On", -220f, () =>
            {
                PlayerAssets.RemotePaperdollBobEnabled = !PlayerAssets.RemotePaperdollBobEnabled;
                RefreshPaperdollDisplaySetting();
            });
        paperdollAttackButton = CreateAppearanceOptionButton(panel, "Paperdoll attack motion",
            "Paperdoll attacks: Off", -284f, () =>
            {
                PlayerAssets.RemotePaperdollAttacksEnabled = !PlayerAssets.RemotePaperdollAttacksEnabled;
                RefreshPaperdollDisplaySetting();
            });
        GameObject back = CreateAppearanceOptionButton(panel, "Back to multiplayer options",
            "Back", -356f, ClosePaperdollSettings);
        RectTransform backRect = back != null ? back.GetComponent<RectTransform>() : null;
        if (backRect != null) backRect.sizeDelta = new Vector2(150, 40);
        paperdollSettingsPanel.SetActive(false);
        RefreshPaperdollDisplaySetting();
    }

    GameObject CreateAppearanceOptionButton(Transform parent, string name,
        string label, float y, UnityEngine.Events.UnityAction click)
    {
        var go = Instantiate(options, parent);
        go.name = name;
        RectTransform rect = go.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0, y);
            rect.sizeDelta = new Vector2(345, 45);
            rect.localScale = Vector3.one;
        }
        var button = go.GetComponent<UnityEngine.UI.Button>();
        if (button != null)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            button.onClick.AddListener(click);
        }
        SetButtonText(go, label);
        go.SetActive(true);
        return go;
    }

    void OpenPaperdollSettings()
    {
        if (paperdollSettingsPanel == null || existingOptionsPanel == null) return;
        RefreshPaperdollDisplaySetting();
        existingOptionsPanel.SetActive(false);
        paperdollSettingsPanel.SetActive(true);
        paperdollSettingsPanel.transform.SetAsLastSibling();
        ScaleOptionsPage(paperdollSettingsPanel, 510, 435);
    }

    void ClosePaperdollSettings()
    {
        if (paperdollSettingsPanel != null) paperdollSettingsPanel.SetActive(false);
        if (existingOptionsPanel != null) existingOptionsPanel.SetActive(true);
        FitOptionsPanelToCanvas();
    }

    void CloseRuntimeOptionsPanels()
    {
        // A paused game's top window can change when ESC closes or a DFU modal
        // message appears. Only reset our own submenus. Never open the legacy
        // Options panel here: it starts inactive and is opened by its own button.
        bool wasInSubmenu = (hostSettingsPanel != null && hostSettingsPanel.activeSelf) ||
            (hostGeneralSettingsPanel != null && hostGeneralSettingsPanel.activeSelf) ||
            (hostServerRulesPanel != null && hostServerRulesPanel.activeSelf) ||
            (paperdollSettingsPanel != null && paperdollSettingsPanel.activeSelf) ||
            (respawnSettingsPanel != null && respawnSettingsPanel.activeSelf) ||
            (advancedRespawnSettingsPanel != null && advancedRespawnSettingsPanel.activeSelf) ||
            (hostQuestDefaultsPanel != null && hostQuestDefaultsPanel.activeSelf) ||
            (lootPanel != null && lootPanel.activeSelf);
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        if (hostGeneralSettingsPanel != null) hostGeneralSettingsPanel.SetActive(false);
        if (hostServerRulesPanel != null) hostServerRulesPanel.SetActive(false);
        if (paperdollSettingsPanel != null) paperdollSettingsPanel.SetActive(false);
        if (respawnSettingsPanel != null) respawnSettingsPanel.SetActive(false);
        if (advancedRespawnSettingsPanel != null) advancedRespawnSettingsPanel.SetActive(false);
        if (hostQuestDefaultsPanel != null) hostQuestDefaultsPanel.SetActive(false);
        if (lootPanel != null) lootPanel.SetActive(false);
        if (wasInSubmenu && existingOptionsPanel != null)
            existingOptionsPanel.SetActive(false);
    }

    void RefreshPaperdollDisplaySetting()
    {
        if (paperdollModeButton != null)
            SetButtonText(paperdollModeButton,
                PlayerAssets.UseRemotePaperdolls ? "Others: Paperdolls" : "Others: Animated sprites");
        if (paperdollTurnButton != null)
        {
            string facing = "Camera";
            switch (PlayerAssets.RemotePaperdollTurnMode)
            {
                case 1: facing = "Mirrored back"; break;
                case 2: facing = "Same face back"; break;
            }
            SetButtonText(paperdollTurnButton, "Paperdoll turn: " + facing);
            var button = paperdollTurnButton.GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.interactable = PlayerAssets.UseRemotePaperdolls;
        }
        if (paperdollBobButton != null)
        {
            SetButtonText(paperdollBobButton,
                PlayerAssets.RemotePaperdollBobEnabled ? "Paperdoll bob: On" : "Paperdoll bob: Off");
            var button = paperdollBobButton.GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.interactable = PlayerAssets.UseRemotePaperdolls;
        }
        if (paperdollAttackButton != null)
        {
            SetButtonText(paperdollAttackButton,
                PlayerAssets.RemotePaperdollAttacksEnabled ? "Paperdoll attacks: On" : "Paperdoll attacks: Off");
            var button = paperdollAttackButton.GetComponent<UnityEngine.UI.Button>();
            if (button != null) button.interactable = PlayerAssets.UseRemotePaperdolls;
        }
    }

    // The old prefab has a ContentSizeFitter on its vertical options group; as controls
    // were added it extended beyond the screen and cut off the time option. Centre the
    // group and bound its height to the canvas instead of relying on the old stretched
    // rect (+ a large negative SizeDelta) from the six-option layout.
    void FitOptionsPanelToCanvas()
    {
        if (existingOptionsPanel == null || canvas == null) return;
        RectTransform panel = existingOptionsPanel.GetComponent<RectTransform>();
        RectTransform available = canvas.GetComponent<RectTransform>();
        if (panel == null || available == null || available.rect.height <= 0f) return;

        var fitter = existingOptionsPanel.GetComponent<ContentSizeFitter>();
        if (fitter != null) fitter.enabled = false;
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.anchoredPosition = Vector2.zero;
        float height = 0f;
        int visible = 0;
        var group = existingOptionsPanel.GetComponent<VerticalLayoutGroup>();
        for (int i = 0; i < panel.childCount; i++)
        {
            RectTransform child = panel.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeSelf) continue;
            float preferred = LayoutUtility.GetPreferredHeight(child);
            if (preferred <= 0f) preferred = Mathf.Abs(child.sizeDelta.y);
            height += Mathf.Max(30f, preferred);
            visible++;
        }
        if (group != null)
            height += group.padding.top + group.padding.bottom + Mathf.Max(0, visible - 1) * group.spacing;
        height = Mathf.Max(50f, height);
        panel.sizeDelta = new Vector2(panel.sizeDelta.x, height);
        float maxHeight = Mathf.Max(200f, available.rect.height - 85f);
        panel.localScale = Vector3.one * Mathf.Min(1f, maxHeight / height);
    }

    void OpenRespawnSettings()
    {
        if (respawnSettingsPanel == null || existingOptionsPanel == null) return;
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        RefreshRespawnSettings();
        if (advancedRespawnSettingsPanel != null) advancedRespawnSettingsPanel.SetActive(false);
        existingOptionsPanel.SetActive(false);
        if (hostSettingsPanel != null) hostSettingsPanel.SetActive(false);
        respawnSettingsPanel.SetActive(true);
        respawnSettingsPanel.transform.SetAsLastSibling();
        ScaleOptionsPage(respawnSettingsPanel, 590f, 535f);
    }

    void RefreshRespawnSettings()
    {
        refreshingRespawnSettings = true;
        int[] values = { OptionsMultiplayer.reviveHoldSeconds, OptionsMultiplayer.manualRespawnSeconds,
            OptionsMultiplayer.reviveHealthPercent, OptionsMultiplayer.respawnHealthPercent,
            OptionsMultiplayer.reviveFatiguePercent, OptionsMultiplayer.respawnFatiguePercent };
        string[] names = { "Hold to revive", "Wait before self-respawn", "Revive health", "Respawn health", "Revive fatigue", "Respawn fatigue" };
        for (int i = 0; i < 6; i++)
        {
            respawnSliders[i].value = values[i];
            respawnValueLabels[i].text = names[i] + ": " + values[i] + (i < 2 ? " s" : "%");
        }
        respawnToggles[0].isOn = OptionsMultiplayer.respawnOutsideDungeon;
        respawnToggles[1].isOn = OptionsMultiplayer.partyTravelInsideDungeon;
        bool editable = CanEditRespawnSettings();
        if (respawnAccessLabel != null)
            respawnAccessLabel.text = editable
                ? "Host/default settings - applied to all players"
                : "Server settings (read-only for clients)";
        for (int i = 0; i < respawnSliders.Length; i++)
            if (respawnSliders[i] != null) respawnSliders[i].interactable = editable;
        for (int i = 0; i < respawnToggles.Length; i++)
            if (respawnToggles[i] != null) respawnToggles[i].interactable = editable;
        if (respawnDefaultsButton != null) respawnDefaultsButton.interactable = editable;
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
            case 4: OptionsMultiplayer.reviveFatiguePercent = value; break;
            case 5: OptionsMultiplayer.respawnFatiguePercent = value; break;
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
        RefreshAdvancedRespawnSettings();
        var local = PlayerMultiplayer.GetLocalPlayer();
        if (NetworkServer.active && local != null)
            local.rpcImportOptions(OptionsMultiplayer.Export());
    }

    // Advanced rules are a separate page to avoid overcrowding the original six
    // respawn/revive sliders and the two unrelated travel toggles.
    void BuildAdvancedRespawnSettings()
    {
        if (respawnSettingsPanel == null || canvas == null || options == null) return;
        GameObject launcher = CreateAppearanceOptionButton(respawnSettingsPanel.transform,
            "Advanced respawn launcher", "Advanced...", -13f, OpenAdvancedRespawnSettings);
        var launchRect = launcher.GetComponent<RectTransform>();
        launchRect.anchoredPosition = new Vector2(187f, -10f);
        launchRect.sizeDelta = new Vector2(157f, 35f);

        RectTransform panel = SettingsRect("Advanced respawn settings", canvas.transform,
            new Vector2(590f, 465f), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color =
            new Color(0.08f, 0.08f, 0.08f, 0.98f);
        advancedRespawnSettingsPanel = panel.gameObject;
        SettingsText(panel, "Advanced respawn", new Vector2(520, 35), new Vector2(0, -12), 22);
        advancedWipeToggleButton = CreateAppearanceOptionButton(panel,
            "Require local party wipe", "Local group wipe: Off", -65f, () =>
            {
                if (!CanEditRespawnSettings()) return;
                OptionsMultiplayer.requireLocalGroupWipe = !OptionsMultiplayer.requireLocalGroupWipe;
                PublishRespawnSettings();
            });
        advancedSanctuaryToggleButton = CreateAppearanceOptionButton(panel,
            "Find nearest settlement", "Nearest settlement: Off", -116f, () =>
            {
                if (!CanEditRespawnSettings()) return;
                OptionsMultiplayer.nearestSanctuaryRespawn = !OptionsMultiplayer.nearestSanctuaryRespawn;
                PublishRespawnSettings();
            });
        advancedFeeToggleButton = CreateAppearanceOptionButton(panel,
            "Level based healing fee", "Healing fee: Off", -167f, () =>
            {
                if (!CanEditRespawnSettings()) return;
                OptionsMultiplayer.sanctuaryHealingFeeEnabled = !OptionsMultiplayer.sanctuaryHealingFeeEnabled;
                PublishRespawnSettings();
            });
        advancedGoldLabel = SettingsText(panel, "Gold / level: 100", new Vector2(230, 35),
            new Vector2(-95, -233), 17);
        advancedGoldSlider = CreateAdvancedRespawnSlider(panel, "Gold per level", 10, 1000,
            -230f, value =>
            {
                if (!CanEditRespawnSettings()) return;
                OptionsMultiplayer.sanctuaryGoldPerLevel = value;
                PublishRespawnSettings();
            });
        advancedRadiusLabel = SettingsText(panel, "Exterior wipe radius: 200 m", new Vector2(260, 35),
            new Vector2(-78, -286), 17);
        advancedRadiusSlider = CreateAdvancedRespawnSlider(panel, "Exterior wipe distance", 10, 500,
            -284f, value =>
            {
                if (!CanEditRespawnSettings()) return;
                OptionsMultiplayer.groupWipeExteriorRadiusMeters = value;
                PublishRespawnSettings();
            });
        SettingsText(panel, "Local wipe uses settlement respawn. Fees apply at temples, inns\nand graveyards. Unpaid recovery restores 10% health.",
            new Vector2(550, 52), new Vector2(0, -339), 14);
        GameObject back = CreateAppearanceOptionButton(panel,
            "Back to ordinary respawn settings", "Back", -404f, OpenRespawnSettings);
        back.GetComponent<RectTransform>().sizeDelta = new Vector2(145, 34);
        advancedRespawnSettingsPanel.SetActive(false);
        RefreshAdvancedRespawnSettings();
    }

    UnityEngine.UI.Slider CreateAdvancedRespawnSlider(Transform panel, string name,
        int min, int max, float y, UnityEngine.Events.UnityAction<int> onChanged)
    {
        var sliderRect = SettingsRect(name, panel, new Vector2(195, 25), new Vector2(170, y));
        var bg = sliderRect.gameObject.AddComponent<UnityEngine.UI.Image>();
        bg.color = new Color(0.23f, 0.23f, 0.23f);
        var slider = sliderRect.gameObject.AddComponent<UnityEngine.UI.Slider>();
        var handle = SettingsRect("Handle", sliderRect, new Vector2(14, 25), Vector2.zero);
        handle.anchorMin = new Vector2(0, 0);
        handle.anchorMax = new Vector2(0, 1);
        handle.pivot = new Vector2(0.5f, 0.5f);
        handle.sizeDelta = new Vector2(14, 0);
        var handleImage = handle.gameObject.AddComponent<UnityEngine.UI.Image>();
        handleImage.color = new Color(0.8f, 0.7f, 0.4f);
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = true;
        slider.onValueChanged.AddListener(value => onChanged((int)value));
        return slider;
    }

    void OpenAdvancedRespawnSettings()
    {
        if (advancedRespawnSettingsPanel == null) return;
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        RefreshAdvancedRespawnSettings();
        if (respawnSettingsPanel != null) respawnSettingsPanel.SetActive(false);
        advancedRespawnSettingsPanel.SetActive(true);
        advancedRespawnSettingsPanel.transform.SetAsLastSibling();
        ScaleOptionsPage(advancedRespawnSettingsPanel, 590f, 465f);
    }

    void RefreshAdvancedRespawnSettings()
    {
        if (advancedRespawnSettingsPanel == null) return;
        bool editable = CanEditRespawnSettings();
        if (advancedWipeToggleButton != null)
        {
            SetButtonText(advancedWipeToggleButton,
                "Local group wipe: " + (OptionsMultiplayer.requireLocalGroupWipe ? "On" : "Off"));
            advancedWipeToggleButton.GetComponent<UnityEngine.UI.Button>().interactable = editable;
        }
        if (advancedSanctuaryToggleButton != null)
        {
            SetButtonText(advancedSanctuaryToggleButton,
                "Nearest settlement: " + (OptionsMultiplayer.nearestSanctuaryRespawn ? "On" : "Off"));
            advancedSanctuaryToggleButton.GetComponent<UnityEngine.UI.Button>().interactable = editable;
        }
        if (advancedFeeToggleButton != null)
        {
            SetButtonText(advancedFeeToggleButton,
                "Healing fee: " + (OptionsMultiplayer.sanctuaryHealingFeeEnabled ? "On" : "Off"));
            advancedFeeToggleButton.GetComponent<UnityEngine.UI.Button>().interactable = editable;
        }
        if (advancedGoldSlider != null)
        {
            advancedGoldSlider.SetValueWithoutNotify(OptionsMultiplayer.sanctuaryGoldPerLevel);
            advancedGoldSlider.interactable = editable;
        }
        if (advancedRadiusSlider != null)
        {
            advancedRadiusSlider.SetValueWithoutNotify(OptionsMultiplayer.groupWipeExteriorRadiusMeters);
            advancedRadiusSlider.interactable = editable;
        }
        if (advancedGoldLabel != null)
            advancedGoldLabel.text = "Gold / level: " + OptionsMultiplayer.sanctuaryGoldPerLevel;
        if (advancedRadiusLabel != null)
            advancedRadiusLabel.text = "Exterior wipe radius: " + OptionsMultiplayer.groupWipeExteriorRadiusMeters + " m";
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

    void RenamePartyTimeSetting()
    {
        // The checkbox is a child of its existing option button in the legacy HUD.
        if (checks == null || checks.Length == 0 || checks[0] == null)
            return;
        UnityEngine.UI.Button button = checks[0].GetComponentInParent<UnityEngine.UI.Button>();
        if (button == null)
            return;
        Text label = button.GetComponentInChildren<Text>(true);
        if (label != null)
            label.text = "Default: leader time";
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

    // Kept only because the original prefab still references this callback.
    // Highest-player-level scaling is obsolete and must remain disabled.
    public void toggleHighestLevel()
    {
        OptionsMultiplayer.useHighestLevel = false;
        if (checks != null && checks.Length > 2 && checks[2] != null)
            checks[2].SetActive(false);
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
        checks[2].SetActive(false);
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
        OptionsMultiplayer.EnsureLocalQuestSyncSettings();
        OptionsMultiplayer.EnsureLocalConsoleSettings();
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
