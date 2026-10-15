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
    const string HostQuestDefaultMigrationPref = "DFUCoop.QuestSync.HostDefaultVisibleV1";
    Text respawnAccessLabel;
    UnityEngine.UI.Button respawnDefaultsButton;
    bool wasPauseMenuOpen;
    bool lastConnectedLayoutState;
    float lastOptionsCanvasHeight = -1f;

    void Start()
    {
        OptionsMultiplayer.EnsureLocalRespawnSettings();
        // First upgrade from the old, now-hidden setting: do not leave players with
        // Off or Share-only forever without a visible explanation. Run this once,
        // only while disconnected, before initializing the new host-only control.
        MigrateOldHiddenHostQuestDefault();
        OptionsMultiplayer.EnsureLocalQuestSyncSettings();
        ResolveConnectionManager();
        EnsurePartyButton();
        EnsureConnectionModeButton();
        RenamePartyTimeSetting();
        BuildRespawnSettings();
        BuildPaperdollDisplaySetting();
        BuildHostQuestDefaultsSetting();
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
            if (respawnSettingsPanel != null && respawnSettingsPanel.activeSelf)
                RefreshRespawnSettings();
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
        button.onClick.AddListener(() => { respawnSettingsPanel.SetActive(false); existingOptionsPanel.SetActive(true); });
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
        hostQuestDefaultsLauncher.name = "Host quest default settings";
        hostQuestDefaultsLauncher.transform.SetSiblingIndex(Mathf.Max(0, parent.childCount - 2));
        var launcherRect = hostQuestDefaultsLauncher.GetComponent<RectTransform>();
        if (launcherRect != null) launcherRect.sizeDelta = new Vector2(220, 40);
        var launch = hostQuestDefaultsLauncher.GetComponent<UnityEngine.UI.Button>();
        if (launch != null)
        {
            launch.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            launch.onClick.AddListener(OpenHostQuestDefaultsSetting);
        }
        SetButtonText(hostQuestDefaultsLauncher, "Quest sync when hosting...");

        RectTransform panel = SettingsRect("Host party defaults panel", canvas.transform,
            new Vector2(540, 318), Vector2.zero);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.gameObject.AddComponent<UnityEngine.UI.Image>().color =
            new Color(0.08f, 0.08f, 0.08f, 0.98f);
        hostQuestDefaultsPanel = panel.gameObject;
        SettingsText(panel, "Quest sync for new parties", new Vector2(480, 34),
            new Vector2(0, -16), 23);
        SettingsText(panel, "Used for new parties when you host a multiplayer game.",
            new Vector2(490, 26), new Vector2(0, -62), 16);
        SettingsText(panel,
            "Existing parties keep their settings. Party leaders can\n" +
            "change their own party's mode in the Party window.",
            new Vector2(490, 54), new Vector2(0, -95), 16);
        hostQuestDefaultsModeButton = CreateAppearanceOptionButton(panel,
            "Default quest sync selector", "Default quest sync: Full", -171f,
            CycleHostQuestDefault);
        GameObject back = CreateAppearanceOptionButton(panel,
            "Back from host party defaults", "Back", -244f,
            CloseHostQuestDefaultsSetting);
        var backRect = back != null ? back.GetComponent<RectTransform>() : null;
        if (backRect != null) backRect.sizeDelta = new Vector2(150, 40);
        hostQuestDefaultsPanel.SetActive(false);
        RefreshHostQuestDefaultsSetting();
    }

    void RefreshHostQuestDefaultsSetting()
    {
        bool show = CanConfigureHostQuestDefaults();
        if (hostQuestDefaultsLauncher != null && hostQuestDefaultsLauncher.activeSelf != show)
            hostQuestDefaultsLauncher.SetActive(show);
        if (!show && hostQuestDefaultsPanel != null && hostQuestDefaultsPanel.activeSelf)
        {
            hostQuestDefaultsPanel.SetActive(false);
            if (existingOptionsPanel != null) existingOptionsPanel.SetActive(false);
        }
        if (hostQuestDefaultsModeButton != null)
        {
            string text = "Full";
            switch (OptionsMultiplayer.DefaultQuestSyncMode)
            {
                case OptionsMultiplayer.QuestSyncMode.SharedOnly: text = "Share-only"; break;
                case OptionsMultiplayer.QuestSyncMode.Off: text = "Off"; break;
            }
            SetButtonText(hostQuestDefaultsModeButton, "Default quest sync: " + text);
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
        if (!CanConfigureHostQuestDefaults() || hostQuestDefaultsPanel == null ||
            existingOptionsPanel == null) return;
        RefreshHostQuestDefaultsSetting();
        existingOptionsPanel.SetActive(false);
        hostQuestDefaultsPanel.SetActive(true);
        hostQuestDefaultsPanel.transform.SetAsLastSibling();
        RectTransform canvasRect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
        if (canvasRect != null)
        {
            float scale = Mathf.Min(1f,
                Mathf.Min(canvasRect.rect.width / 570f, canvasRect.rect.height / 348f));
            hostQuestDefaultsPanel.transform.localScale = Vector3.one * scale;
        }
    }

    void CloseHostQuestDefaultsSetting()
    {
        if (hostQuestDefaultsPanel != null) hostQuestDefaultsPanel.SetActive(false);
        if (existingOptionsPanel != null) existingOptionsPanel.SetActive(true);
        FitOptionsPanelToCanvas();
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
        RectTransform canvasRect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
        if (canvasRect != null)
        {
            float scale = Mathf.Min(1f,
                Mathf.Min(canvasRect.rect.width / 540f, canvasRect.rect.height / 475f));
            paperdollSettingsPanel.transform.localScale = Vector3.one * scale;
        }
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
        bool wasInSubmenu = (paperdollSettingsPanel != null && paperdollSettingsPanel.activeSelf) ||
            (respawnSettingsPanel != null && respawnSettingsPanel.activeSelf) ||
            (hostQuestDefaultsPanel != null && hostQuestDefaultsPanel.activeSelf);
        if (paperdollSettingsPanel != null) paperdollSettingsPanel.SetActive(false);
        if (respawnSettingsPanel != null) respawnSettingsPanel.SetActive(false);
        if (hostQuestDefaultsPanel != null) hostQuestDefaultsPanel.SetActive(false);
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
        existingOptionsPanel.SetActive(false);
        respawnSettingsPanel.SetActive(true);
        respawnSettingsPanel.transform.SetAsLastSibling();
        var canvasRect = canvas.GetComponent<RectTransform>();
        float scale = Mathf.Min(1f, Mathf.Min(canvasRect.rect.width / 620f, canvasRect.rect.height / 565f));
        respawnSettingsPanel.transform.localScale = Vector3.one * scale;
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
        OptionsMultiplayer.EnsureLocalQuestSyncSettings();
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
