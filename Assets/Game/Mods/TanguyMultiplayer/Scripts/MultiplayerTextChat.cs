// Project: Daggerfall Unity - TanguyMultiplayer
// Runtime-created multiplayer text chat. No prefab or scene setup required.
// v15: Enter-chat ignores Return while DFU developer console is open.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Mirror;
using DaggerfallWorkshop.Game;
using DFUI = DaggerfallWorkshop.Game.DaggerfallUI;

public struct MultiplayerChatSubmitMessage : NetworkMessage
{
    public string text;
}

public struct MultiplayerChatBroadcastMessage : NetworkMessage
{
    public string senderName;
    public string text;
    public bool isSystemMessage;
}

/// <summary>
/// Global multiplayer text chat created entirely at runtime.
/// It remains dormant and creates no visible UI in singleplayer.
/// </summary>
public sealed class MultiplayerTextChat : MonoBehaviour
{

    const int MaxMessageLength = 240;
    const int MaxHistoryLines = 150;
    const float ServerMinimumMessageInterval = 0.35f;
    const float HudMessageDuration = 4.25f;
    const float CustomCaretBlinkHalfCycleSeconds = 0.80f;
    const int HudMessageMaxCharacters = 120;
    const string EnterOpensChatPreferenceKey = "TanguyMultiplayer.Chat.EnterOpensChat";

    static MultiplayerTextChat instance;

    readonly List<string> history = new List<string>();
    readonly Dictionary<int, float> serverNextAllowedMessageTime = new Dictionary<int, float>();
    readonly Dictionary<int, string> serverAnnouncedPlayerNames = new Dictionary<int, string>();
    readonly List<int> serverConnectionIdsScratch = new List<int>();
    readonly List<int> serverDisconnectedIdsScratch = new List<int>();

    bool serverHandlerRegistered;
    bool clientHandlerRegistered;
    bool chatOpen;
    bool controlsCaptured;
    bool closingInputCapture;
    int chatOpenedFrame;
    KeyCode closingSwallowKey = KeyCode.None;
    int closingReleaseFrame = -1;
    int focusInputAtFrame = -1;
    int scrollToBottomAtFrame = -1;
    bool closeRequestedFromButton;
    bool manualOpenRequested;
    bool partyWindowRequested;
    bool partyWindowActive;
    bool enterOpensChat;
    bool enterModeChangePending;
    bool clearToggleSelectionPending;
    bool openChatAfterModeTogglePending;

    Wenzil.Console.ConsoleController daggerfallConsoleController;

    bool inputManagerStateCaptured;
    bool inputManagerWasPaused;

    bool cursorStateCaptured;
    bool cursorActiveBeforeChat;
    CursorLockMode cursorLockStateBeforeChat;
    bool cursorVisibleBeforeChat;
    bool inputManagerCursorVisibleBeforeChat;

    bool observedCursorStateValid;
    bool observedCursorActive;
    CursorLockMode observedCursorLockState;
    bool observedCursorVisible;
    bool observedInputManagerCursorVisible;

    Canvas chatCanvas;
    CanvasGroup chatCanvasGroup;
    RectTransform chatRoot;
    ScrollRect scrollRect;
    Scrollbar verticalScrollbar;
    RectTransform scrollContent;
    Text historyText;
    InputField inputField;
    Text inputTextComponent;
    Text inputPlaceholder;
    Toggle enterOpensChatToggle;
    RectTransform customCaret;
    Image customCaretImage;
    string lastCaretText = string.Empty;
    int lastCaretPosition = -1;
    float customCaretBlinkTimer;
    bool customCaretBlinkVisible;
    MultiplayerChatDragHandle dragHandle;

    PlayerMotor playerMotor;
    PlayerMouseLook playerMouseLook;
    WeaponManager weaponManager;
    PlayerActivate playerActivate;

    bool playerMotorWasEnabled;
    bool playerMouseLookWasEnabled;
    bool weaponManagerWasEnabled;
    bool playerActivateWasEnabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!Application.isPlaying)
            return;

        if (instance != null)
            return;

        GameObject go = new GameObject("MultiplayerTextChat");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<MultiplayerTextChat>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (instance == this)
            instance = null;

        // Do not deactivate InputField/Canvas behaviours while the Editor is tearing
        // down play mode. That can produce Unity's ShouldRunBehaviour assertion.
        if (Application.isPlaying && controlsCaptured)
            RestoreGameplayControls();
    }

    void Update()
    {
        RefreshNetworkHandlers();
        UpdateServerPresenceAnnouncements();

        bool chatAvailable = IsMultiplayerChatAvailable();
        if (!chatAvailable)
        {
            partyWindowRequested = false;
            partyWindowActive = false;

            if (chatOpen || closingInputCapture || controlsCaptured)
                ForceCloseAndRestoreImmediately(true);

            SetChatCanvasVisible(false);
            return;
        }

        EnsureChatUI();

        // Keep the runtime chat canvas out of the way while DFU's journal-style
        // party popup is the active window. This also gives the party window sole
        // ownership of cursor and input until it is closed.
        if (partyWindowActive)
        {
            if (IsPartyWindowTop())
            {
                SetChatCanvasVisible(false);
                return;
            }

            partyWindowActive = false;
        }

        // Button callbacks only queue this request. Create/push the DFU popup here,
        // outside EventSystem processing, just like the existing close workflow.
        if (partyWindowRequested)
        {
            partyWindowRequested = false;
            OpenPartyWindow();
            return;
        }

        // The checkbox has two deliberately different closed states:
        // - Enter opens chat ON: closed chat is hidden and Enter opens it.
        // - Enter opens chat OFF: chat follows DFU's own cursorActive state. It appears
        //   in old-school cursor gameplay mode and disappears again in free-look mode.
        if (enterModeChangePending)
        {
            enterModeChangePending = false;

            // When Enter-chat is enabled from the passive cursor-mode panel, that
            // panel is technically closed (chatOpen == false). Do not immediately
            // apply the new hidden-when-closed rule. Activate the input instead so
            // the window the player is interacting with remains open until Enter
            // explicitly sends/closes it.
            if (openChatAfterModeTogglePending)
            {
                openChatAfterModeTogglePending = false;
                if (!chatOpen && !closingInputCapture && CanOpenChatFromCurrentUI())
                {
                    OpenChat();
                    return;
                }
            }

            if (!chatOpen && !closingInputCapture)
            {
                if (enterOpensChat)
                    ForceGameplayFreeLook();

                SetChatCanvasVisible(ShouldShowClosedChatCanvas());
            }
        }
        else
        {
            SetChatCanvasVisible(chatOpen || ShouldShowClosedChatCanvas());
        }

        // Close-button callbacks only queue work. Actual InputField/UI state changes
        // happen here, outside EventSystem/GUIUtility event processing.
        if (closeRequestedFromButton)
        {
            closeRequestedFromButton = false;
            manualOpenRequested = false;
            if (chatOpen)
                BeginCloseChat(true, KeyCode.None);
        }

        if (closingInputCapture)
        {
            MaintainChatInputSuppression();

            if (IsSwallowKeyHeld(closingSwallowKey))
            {
                closingReleaseFrame = -1;
            }
            else if (closingReleaseFrame < 0)
            {
                closingReleaseFrame = Time.frameCount;
            }
            else if (Time.frameCount > closingReleaseFrame)
            {
                RestoreGameplayControls();
                closingInputCapture = false;
                closingSwallowKey = KeyCode.None;
                closingReleaseFrame = -1;
                ObserveGameplayCursorState();
                SetChatCanvasVisible(ShouldShowClosedChatCanvas());
            }

            return;
        }

        if (!chatOpen)
        {
            UpdatePassiveInputHint();

            if (manualOpenRequested)
            {
                manualOpenRequested = false;
                if (CanOpenChatFromCurrentUI())
                    OpenChat();
                return;
            }

            // When disabled, Enter is completely ignored by chat so DFU can use it for
            // its normal ActivateCursor / old-school mouse gameplay toggle.
            if (enterOpensChat && WasEnterPressed() && CanOpenChatFromCurrentUI())
                OpenChat();

            return;
        }

        MaintainChatInputSuppression();
        UpdateCustomCaret(false);

        // Do not consume the same Enter press which opened chat.
        if (Time.frameCount > chatOpenedFrame + 1 && WasEnterPressed())
        {
            SubmitCurrentMessageAndClose();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
            BeginCloseChat(true, KeyCode.Escape);
    }

    void LateUpdate()
    {
        // Unity keeps a clicked Toggle selected. Return/Enter is also a UI Submit key,
        // so leaving it selected would toggle the option again when the player uses
        // Enter to close chat or switch DFU mouse mode. Clear selection outside the
        // EventSystem callback to avoid UI lifecycle assertions.
        if (clearToggleSelectionPending)
        {
            clearToggleSelectionPending = false;
            try
            {
                if (EventSystem.current != null && enterOpensChatToggle != null &&
                    EventSystem.current.currentSelectedGameObject == enterOpensChatToggle.gameObject)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }
            catch { }
        }

        if (chatOpen || closingInputCapture)
        {
            // DFU binds Enter to ActivateCursor. While chat owns input, keep DFU's
            // action list paused and force a real UI cursor after gameplay scripts.
            MaintainChatInputSuppression();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            try
            {
                InputManager.Instance.CursorVisible = true;
            }
            catch { }

            if (chatOpen && focusInputAtFrame >= 0 && Time.frameCount >= focusInputAtFrame)
            {
                focusInputAtFrame = -1;
                FocusInputFieldNow();
            }

            if (scrollToBottomAtFrame >= 0 && Time.frameCount >= scrollToBottomAtFrame)
            {
                scrollToBottomAtFrame = -1;
                ScrollToBottomNow();
            }

            if (chatOpen)
                UpdateCustomCaret(false);

            return;
        }

        if (IsMultiplayerChatAvailable())
        {
            ObserveGameplayCursorState();

            // PlayerMouseLook processes DFU's Enter/ActivateCursor toggle in Update().
            // Refresh visibility in LateUpdate so manual-click chat hides on the same
            // frame free look is restored, regardless of script execution order.
            if (!chatOpen && !closingInputCapture)
                SetChatCanvasVisible(ShouldShowClosedChatCanvas());
        }
    }

    static bool WasEnterPressed()
    {
        return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
    }

    void RefreshNetworkHandlers()
    {
        if (NetworkServer.active)
        {
            if (!serverHandlerRegistered)
            {
                NetworkServer.RegisterHandler<MultiplayerChatSubmitMessage>(OnServerChatSubmit);
                serverHandlerRegistered = true;
            }
        }
        else
        {
            serverHandlerRegistered = false;
            serverNextAllowedMessageTime.Clear();
            serverAnnouncedPlayerNames.Clear();
            serverConnectionIdsScratch.Clear();
            serverDisconnectedIdsScratch.Clear();
        }

        if (NetworkClient.active)
        {
            if (!clientHandlerRegistered)
            {
                NetworkClient.RegisterHandler<MultiplayerChatBroadcastMessage>(OnClientChatBroadcast);
                clientHandlerRegistered = true;
            }
        }
        else
        {
            clientHandlerRegistered = false;
        }
    }

    void UpdateServerPresenceAnnouncements()
    {
        if (!NetworkServer.active)
            return;

        serverConnectionIdsScratch.Clear();

        // Wait until each connection has a spawned identity and a real synchronized
        // character name. This avoids announcing temporary fallback names while a
        // player is still completing Mirror's connection/spawn sequence.
        foreach (var pair in NetworkServer.connections)
        {
            int connectionId = pair.Key;
            NetworkConnection connection = pair.Value;
            serverConnectionIdsScratch.Add(connectionId);

            string playerName;
            if (!TryGetServerAuthoritativeCharacterName(connection, out playerName))
                continue;

            string announcedName;
            if (!serverAnnouncedPlayerNames.TryGetValue(connectionId, out announcedName))
            {
                serverAnnouncedPlayerNames[connectionId] = playerName;
                BroadcastSystemMessage(playerName + " entered Daggerfall.");
            }
            else if (!string.Equals(announcedName, playerName, StringComparison.Ordinal))
            {
                // Keep the latest authoritative name so the eventual leave message
                // cannot use a stale value if another system updates playerName.
                serverAnnouncedPlayerNames[connectionId] = playerName;
            }
        }

        serverDisconnectedIdsScratch.Clear();
        foreach (KeyValuePair<int, string> pair in serverAnnouncedPlayerNames)
        {
            if (!serverConnectionIdsScratch.Contains(pair.Key))
                serverDisconnectedIdsScratch.Add(pair.Key);
        }

        for (int i = 0; i < serverDisconnectedIdsScratch.Count; i++)
        {
            int connectionId = serverDisconnectedIdsScratch[i];
            string playerName;
            if (!serverAnnouncedPlayerNames.TryGetValue(connectionId, out playerName))
                continue;

            serverAnnouncedPlayerNames.Remove(connectionId);
            serverNextAllowedMessageTime.Remove(connectionId);
            BroadcastSystemMessage(playerName + " left Daggerfall.");
        }
    }

    static void BroadcastSystemMessage(string text)
    {
        if (!NetworkServer.active)
            return;

        string cleaned = SanitizeMessage(text);
        if (string.IsNullOrEmpty(cleaned))
            return;

        MultiplayerChatBroadcastMessage broadcast = new MultiplayerChatBroadcastMessage
        {
            senderName = string.Empty,
            text = cleaned,
            isSystemMessage = true,
        };

        NetworkServer.SendToAll(broadcast);
    }

    bool IsMultiplayerChatAvailable()
    {
        if (!NetworkClient.active || !NetworkClient.isConnected)
            return false;

        try
        {
            return NetworkClient.localPlayer != null;
        }
        catch
        {
            return false;
        }
    }

    bool IsDaggerfallConsoleOpen()
    {
        try
        {
            // DFU's developer console is a separate Unity UI overlay, not a
            // DaggerfallBaseWindow. The normal TopWindow/HUD check therefore cannot
            // see it. Cache its always-running controller and query ConsoleUI directly.
            if (daggerfallConsoleController == null)
                daggerfallConsoleController = UnityEngine.Object.FindObjectOfType<Wenzil.Console.ConsoleController>();

            return daggerfallConsoleController != null &&
                daggerfallConsoleController.ui != null &&
                daggerfallConsoleController.ui.isConsoleOpen;
        }
        catch
        {
            return false;
        }
    }

    bool CanOpenChatFromCurrentUI()
    {
        // Return submits commands in DFU's developer console. Never let the same
        // key press open chat or capture gameplay/cursor controls behind the console.
        if (IsDaggerfallConsoleOpen())
            return false;

        try
        {
            if (DFUI.Instance == null || DFUI.Instance.UserInterfaceManager == null)
                return true;

            object topWindow = DFUI.Instance.UserInterfaceManager.TopWindow;
            object hud = DFUI.Instance.DaggerfallHUD;

            // Gameplay is normally either HUD-on-top or no modal window.
            return topWindow == null || hud == null || object.ReferenceEquals(topWindow, hud);
        }
        catch
        {
            return !IsDaggerfallConsoleOpen();
        }
    }

    bool IsPartyWindowTop()
    {
        try
        {
            if (DFUI.Instance == null || DFUI.Instance.UserInterfaceManager == null)
                return false;

            return DFUI.Instance.UserInterfaceManager.TopWindow is
                DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMultiplayerPartyWindow;
        }
        catch
        {
            return false;
        }
    }

    void OpenPartyWindow()
    {
        if (!IsMultiplayerChatAvailable())
            return;

        // If another route already opened it, just suppress this overlay until it closes.
        if (IsPartyWindowTop())
        {
            partyWindowActive = true;
            SetChatCanvasVisible(false);
            return;
        }

        // Fully release chat-owned gameplay controls before handing control to DFU's
        // popup manager. This runs during Update, not from the Unity Button callback.
        ForceCloseAndRestoreImmediately(false);

        try
        {
            if (DFUI.Instance == null || DFUI.Instance.UserInterfaceManager == null)
                return;

            DaggerfallWorkshop.Game.UserInterface.IUserInterfaceManager manager =
                DFUI.Instance.UserInterfaceManager;
            DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMultiplayerPartyWindow partyWindow =
                new DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallMultiplayerPartyWindow(manager);

            manager.PushWindow(partyWindow);
            partyWindowActive = true;
            SetChatCanvasVisible(false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MultiplayerTextChat] Could not open party window: " + ex);
            partyWindowActive = false;
            SetChatCanvasVisible(ShouldShowClosedChatCanvas());
        }
    }

    void OpenChat()
    {
        // Keep this second guard even though all normal callers use
        // CanOpenChatFromCurrentUI(). It prevents future/direct callers from opening
        // chat over the developer console.
        if (IsDaggerfallConsoleOpen())
            return;

        EnsureChatUI();
        if (chatRoot == null || inputField == null)
            return;

        CaptureGameplayCursorStateBeforeChat();

        chatOpen = true;
        closingInputCapture = false;
        chatOpenedFrame = Time.frameCount;
        SetChatCanvasVisible(true);
        CaptureAndDisableGameplayControls();
        MaintainChatInputSuppression();

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        try
        {
            InputManager.Instance.CursorVisible = true;
        }
        catch { }

        inputField.text = string.Empty;
        if (inputPlaceholder != null)
            inputPlaceholder.text = "Type a message and press Enter...";
        inputField.ForceLabelUpdate();
        lastCaretText = string.Empty;
        lastCaretPosition = -1;
        customCaretBlinkTimer = 0f;
        customCaretBlinkVisible = true;

        // Use frame-state instead of coroutines. This remains safe if play mode,
        // networking, or the UI is torn down between frames.
        focusInputAtFrame = Time.frameCount + 2;
        scrollToBottomAtFrame = Time.frameCount + 1;
    }

    void FocusInputFieldNow()
    {
        if (!chatOpen || inputField == null)
            return;

        Canvas.ForceUpdateCanvases();
        if (chatRoot != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(chatRoot);
        inputField.ForceLabelUpdate();

        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(inputField.gameObject);
        }

        inputField.Select();
        inputField.ActivateInputField();
        inputField.caretPosition = inputField.text != null ? inputField.text.Length : 0;
        inputField.ForceLabelUpdate();
        Canvas.ForceUpdateCanvases();
        UpdateCustomCaret(true);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void SubmitCurrentMessageAndClose()
    {
        string text = inputField != null ? inputField.text : string.Empty;
        string cleaned = SanitizeMessage(text);

        if (!string.IsNullOrEmpty(cleaned) && NetworkClient.active && NetworkClient.isConnected)
        {
            NetworkClient.Send(new MultiplayerChatSubmitMessage { text = cleaned });
        }

        BeginCloseChat(true, KeyCode.Return);
    }

    void BeginCloseChat(bool clearInput, KeyCode swallowKey)
    {
        if (!chatOpen && !closingInputCapture)
            return;

        chatOpen = false;
        closingInputCapture = true;

        if (inputField != null)
        {
            inputField.DeactivateInputField();
            if (clearInput)
                inputField.text = string.Empty;
            inputField.ForceLabelUpdate();
        }

        if (EventSystem.current != null && inputField != null &&
            EventSystem.current.currentSelectedGameObject == inputField.gameObject)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }

        SetCustomCaretVisible(false);
        SetChatCanvasVisible(enterOpensChat ? false : true);
        UpdatePassiveInputHint();
        MaintainChatInputSuppression();

        closingSwallowKey = swallowKey;
        closingReleaseFrame = swallowKey == KeyCode.None ? Time.frameCount : -1;
        focusInputAtFrame = -1;
        scrollToBottomAtFrame = -1;
    }

    static bool IsSwallowKeyHeld(KeyCode key)
    {
        if (key == KeyCode.Return || key == KeyCode.KeypadEnter)
            return Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

        if (key == KeyCode.None)
            return false;

        return Input.GetKey(key);
    }

    void ForceCloseAndRestoreImmediately(bool clearInput)
    {
        chatOpen = false;
        closingInputCapture = false;
        closingSwallowKey = KeyCode.None;
        closingReleaseFrame = -1;
        focusInputAtFrame = -1;
        scrollToBottomAtFrame = -1;
        closeRequestedFromButton = false;
        manualOpenRequested = false;

        if (inputField != null)
        {
            // Only deactivate during normal runtime. Avoid touching UIBehaviour
            // lifecycle while the Editor is tearing play mode down.
            if (Application.isPlaying && inputField.isFocused)
                inputField.DeactivateInputField();
            if (clearInput)
                inputField.text = string.Empty;
        }

        SetCustomCaretVisible(false);
        SetChatCanvasVisible(false);
        if (controlsCaptured)
            RestoreGameplayControls();
    }

    bool ShouldShowClosedChatCanvas()
    {
        if (enterOpensChat)
            return false;

        try
        {
            PlayerMouseLook mouseLook = GameManager.Instance != null
                ? GameManager.Instance.PlayerMouseLook
                : null;

            if (mouseLook != null)
                return mouseLook.cursorActive;
        }
        catch { }

        return observedCursorStateValid && observedCursorActive;
    }

    void ObserveGameplayCursorState()
    {
        try
        {
            PlayerMouseLook mouseLook = GameManager.Instance != null
                ? GameManager.Instance.PlayerMouseLook
                : null;

            if (mouseLook == null)
                return;

            observedCursorActive = mouseLook.cursorActive;
            observedCursorLockState = Cursor.lockState;
            observedCursorVisible = Cursor.visible;
            observedInputManagerCursorVisible = InputManager.Instance.CursorVisible;
            observedCursorStateValid = true;
        }
        catch { }
    }

    void CaptureGameplayCursorStateBeforeChat()
    {
        if (!observedCursorStateValid)
            ObserveGameplayCursorState();

        if (observedCursorStateValid)
        {
            cursorActiveBeforeChat = observedCursorActive;
            cursorLockStateBeforeChat = observedCursorLockState;
            cursorVisibleBeforeChat = observedCursorVisible;
            inputManagerCursorVisibleBeforeChat = observedInputManagerCursorVisible;
        }
        else
        {
            try
            {
                PlayerMouseLook mouseLook = GameManager.Instance != null
                    ? GameManager.Instance.PlayerMouseLook
                    : null;
                cursorActiveBeforeChat = mouseLook != null && mouseLook.cursorActive;
                inputManagerCursorVisibleBeforeChat = InputManager.Instance.CursorVisible;
            }
            catch
            {
                cursorActiveBeforeChat = false;
                inputManagerCursorVisibleBeforeChat = false;
            }

            cursorLockStateBeforeChat = Cursor.lockState;
            cursorVisibleBeforeChat = Cursor.visible;
        }

        cursorStateCaptured = true;
    }

    void MaintainChatInputSuppression()
    {
        try
        {
            InputManager.Instance.IsPaused = true;
            InputManager.Instance.ClearAllActions();
            InputManager.Instance.CursorVisible = true;
        }
        catch { }
    }

    void CaptureAndDisableGameplayControls()
    {
        if (controlsCaptured)
            return;

        try
        {
            inputManagerWasPaused = InputManager.Instance.IsPaused;
            inputManagerStateCaptured = true;
            InputManager.Instance.IsPaused = true;
            InputManager.Instance.ClearAllActions();
            InputManager.Instance.CursorVisible = true;
        }
        catch
        {
            inputManagerStateCaptured = false;
        }

        try
        {
            if (GameManager.Instance != null)
            {
                playerMotor = GameManager.Instance.PlayerMotor;
                playerMouseLook = GameManager.Instance.PlayerMouseLook;
                weaponManager = GameManager.Instance.WeaponManager;
                playerActivate = GameManager.Instance.PlayerActivate;
            }
        }
        catch { }

        if (playerMotor != null)
        {
            playerMotorWasEnabled = playerMotor.enabled;
            playerMotor.enabled = false;
        }

        if (playerMouseLook != null)
        {
            playerMouseLookWasEnabled = playerMouseLook.enabled;
            playerMouseLook.enabled = false;
        }

        if (weaponManager != null)
        {
            weaponManagerWasEnabled = weaponManager.enabled;
            weaponManager.enabled = false;
        }

        if (playerActivate != null)
        {
            playerActivateWasEnabled = playerActivate.enabled;
            playerActivate.enabled = false;
        }

        controlsCaptured = true;
    }

    void RestoreGameplayControls()
    {
        // When Enter is assigned to chat it can no longer be used to leave DFU's
        // old-school cursor mode. Always return to free look after that chat mode closes.
        // With Enter-chat disabled, preserve DFU's exact pre-chat cursor mode.
        bool forceFreeLook = enterOpensChat;
        if (playerMouseLook != null)
        {
            if (forceFreeLook)
            {
                playerMouseLook.cursorActive = false;
                playerMouseLook.enableMouseLook = true;
            }
            else if (cursorStateCaptured)
            {
                playerMouseLook.cursorActive = cursorActiveBeforeChat;
            }
        }

        if (playerMotor != null)
            playerMotor.enabled = playerMotorWasEnabled;
        if (playerMouseLook != null)
            playerMouseLook.enabled = playerMouseLookWasEnabled;
        if (weaponManager != null)
            weaponManager.enabled = weaponManagerWasEnabled;
        if (playerActivate != null)
            playerActivate.enabled = playerActivateWasEnabled;

        try
        {
            if (inputManagerStateCaptured)
                InputManager.Instance.IsPaused = inputManagerWasPaused;

            InputManager.Instance.ClearAllActions();
            if (forceFreeLook)
                InputManager.Instance.CursorVisible = false;
            else if (cursorStateCaptured)
                InputManager.Instance.CursorVisible = inputManagerCursorVisibleBeforeChat;
        }
        catch { }

        if (forceFreeLook)
        {
            Cursor.visible = false;
            if (playerMouseLook != null && playerMouseLook.lockCursor)
                Cursor.lockState = CursorLockMode.Locked;
        }
        else if (cursorStateCaptured)
        {
            Cursor.lockState = cursorLockStateBeforeChat;
            Cursor.visible = cursorVisibleBeforeChat;
        }

        controlsCaptured = false;
        inputManagerStateCaptured = false;
        cursorStateCaptured = false;
        playerMotor = null;
        playerMouseLook = null;
        weaponManager = null;
        playerActivate = null;
    }

    void ForceGameplayFreeLook()
    {
        try
        {
            PlayerMouseLook mouseLook = GameManager.Instance != null
                ? GameManager.Instance.PlayerMouseLook
                : null;

            if (mouseLook != null)
            {
                mouseLook.cursorActive = false;
                mouseLook.enableMouseLook = true;
            }

            InputManager.Instance.ClearAllActions();
            InputManager.Instance.CursorVisible = false;

            Cursor.visible = false;
            if (mouseLook != null && mouseLook.lockCursor)
                Cursor.lockState = CursorLockMode.Locked;
        }
        catch { }

        observedCursorStateValid = false;
    }

    void OnServerChatSubmit(NetworkConnection connection, MultiplayerChatSubmitMessage message)
    {
        if (connection == null || connection.identity == null)
            return;

        string cleaned = SanitizeMessage(message.text);
        if (string.IsNullOrEmpty(cleaned))
            return;

        int connectionId = connection.connectionId;
        float now = Time.realtimeSinceStartup;
        float nextAllowed;
        if (serverNextAllowedMessageTime.TryGetValue(connectionId, out nextAllowed) && now < nextAllowed)
            return;

        serverNextAllowedMessageTime[connectionId] = now + ServerMinimumMessageInterval;

        string senderName = GetServerAuthoritativeCharacterName(connection);
        MultiplayerChatBroadcastMessage broadcast = new MultiplayerChatBroadcastMessage
        {
            senderName = senderName,
            text = cleaned,
            isSystemMessage = false,
        };

        NetworkServer.SendToAll(broadcast);
    }

    static string GetServerAuthoritativeCharacterName(NetworkConnection connection)
    {
        string playerName;
        if (TryGetServerAuthoritativeCharacterName(connection, out playerName))
            return playerName;

        return connection != null ? "Player " + connection.connectionId : "Player";
    }

    static bool TryGetServerAuthoritativeCharacterName(NetworkConnection connection, out string playerName)
    {
        playerName = string.Empty;
        if (connection == null || connection.identity == null)
            return false;

        try
        {
            PlayerAssets assets = connection.identity.GetComponent<PlayerAssets>();
            if (assets != null)
                playerName = assets.playerName;
        }
        catch
        {
            playerName = string.Empty;
        }

        playerName = SanitizeName(playerName);
        return !string.IsNullOrEmpty(playerName);
    }

    void OnClientChatBroadcast(MultiplayerChatBroadcastMessage message)
    {
        string text = SanitizeMessage(message.text);
        if (string.IsNullOrEmpty(text))
            return;

        if (message.isSystemMessage)
        {
            AppendHistoryLine(text);
            ShowTemporaryHudMessage(text);
            return;
        }

        string senderName = SanitizeName(message.senderName);
        if (string.IsNullOrEmpty(senderName))
            senderName = "Player";

        string line = senderName + ": " + text;
        AppendHistoryLine(line);
        ShowTemporaryHudMessage(line);
    }

    static string SanitizeMessage(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        StringBuilder builder = new StringBuilder(Mathf.Min(value.Length, MaxMessageLength));
        bool previousWasSpace = false;

        for (int i = 0; i < value.Length && builder.Length < MaxMessageLength; i++)
        {
            char c = value[i];

            if (c == '\r' || c == '\n' || c == '\t')
                c = ' ';

            if (char.IsControl(c))
                continue;

            if (char.IsWhiteSpace(c))
            {
                if (previousWasSpace)
                    continue;

                c = ' ';
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    static string SanitizeName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        StringBuilder builder = new StringBuilder(Mathf.Min(value.Length, 48));
        for (int i = 0; i < value.Length && builder.Length < 48; i++)
        {
            char c = value[i];
            if (char.IsControl(c) || c == '\r' || c == '\n')
                continue;

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    void AppendHistoryLine(string line)
    {
        bool wasNearBottom = scrollRect == null || scrollRect.verticalNormalizedPosition <= 0.08f;

        history.Add(line);
        while (history.Count > MaxHistoryLines)
            history.RemoveAt(0);

        if (historyText == null)
            return;

        historyText.text = string.Join("\n", history.ToArray());
        ResizeHistoryContent();

        if (wasNearBottom || !chatOpen)
            scrollToBottomAtFrame = Mathf.Max(scrollToBottomAtFrame, Time.frameCount + 1);
    }

    void ResizeHistoryContent()
    {
        if (historyText == null || scrollContent == null)
            return;

        Canvas.ForceUpdateCanvases();
        float preferred = Mathf.Max(1f, historyText.preferredHeight + 8f);
        scrollContent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, preferred);
    }

    void ScrollToBottomNow()
    {
        if (scrollRect == null)
            return;

        ResizeHistoryContent();
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
    }

    void UpdateCustomCaret(bool resetBlink)
    {
        if (customCaret == null || inputField == null || inputTextComponent == null)
            return;

        bool shouldShow = chatOpen && inputField.isFocused;
        if (!shouldShow)
        {
            SetCustomCaretVisible(false);
            return;
        }

        string currentText = inputField.text ?? string.Empty;
        int caretPosition = Mathf.Clamp(inputField.caretPosition, 0, currentText.Length);
        bool caretMoved = currentText != lastCaretText || caretPosition != lastCaretPosition;

        if (resetBlink || caretMoved)
        {
            lastCaretText = currentText;
            lastCaretPosition = caretPosition;
            customCaretBlinkTimer = 0f;
            customCaretBlinkVisible = true;
        }
        else
        {
            customCaretBlinkTimer += Time.unscaledDeltaTime;
            if (customCaretBlinkTimer >= CustomCaretBlinkHalfCycleSeconds)
            {
                customCaretBlinkTimer = 0f;
                customCaretBlinkVisible = !customCaretBlinkVisible;
            }
        }

        // InputField can horizontally shift its Text RectTransform for long input.
        // The custom caret is a child of that Text, so it follows that shift.
        string beforeCaret = caretPosition > 0 ? currentText.Substring(0, caretPosition) : string.Empty;
        float width = 0f;
        if (beforeCaret.Length > 0)
        {
            TextGenerationSettings settings = inputTextComponent.GetGenerationSettings(
                new Vector2(100000f, Mathf.Max(1f, inputTextComponent.rectTransform.rect.height)));
            width = inputTextComponent.cachedTextGeneratorForLayout.GetPreferredWidth(beforeCaret, settings)
                / Mathf.Max(0.0001f, inputTextComponent.pixelsPerUnit);
        }

        customCaret.anchoredPosition = new Vector2(width, 0f);
        SetCustomCaretVisible(customCaretBlinkVisible);
    }

    void SetCustomCaretVisible(bool visible)
    {
        if (customCaretImage != null)
            customCaretImage.canvasRenderer.SetAlpha(visible ? 1f : 0f);
    }

    static void ShowTemporaryHudMessage(string line)
    {
        string hudLine = line;
        if (hudLine.Length > HudMessageMaxCharacters)
            hudLine = hudLine.Substring(0, HudMessageMaxCharacters - 3) + "...";

        try
        {
            if (DFUI.Instance != null)
                DFUI.AddHUDText(hudLine, HudMessageDuration);
        }
        catch { }
    }

    void EnsureChatUI()
    {
        if (chatCanvas != null)
            return;

        EnsureEventSystem();

        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        enterOpensChat = PlayerPrefs.GetInt(EnterOpensChatPreferenceKey, 0) != 0;

        GameObject canvasObject = new GameObject(
            "MultiplayerChatCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster),
            typeof(CanvasGroup));
        DontDestroyOnLoad(canvasObject);

        chatCanvas = canvasObject.GetComponent<Canvas>();
        chatCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        chatCanvas.sortingOrder = 32000;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        chatCanvasGroup = canvasObject.GetComponent<CanvasGroup>();

        GameObject rootObject = CreateUIObject("ChatPanel", canvasObject.transform, typeof(Image));
        chatRoot = rootObject.GetComponent<RectTransform>();
        chatRoot.anchorMin = new Vector2(0.5f, 0.5f);
        chatRoot.anchorMax = new Vector2(0.5f, 0.5f);
        chatRoot.pivot = Vector2.zero;
        chatRoot.sizeDelta = new Vector2(560f, 220f);

        Image rootImage = rootObject.GetComponent<Image>();
        rootImage.color = new Color(0f, 0f, 0f, 0.30f);
        rootImage.raycastTarget = true;

        GameObject headerObject = CreateUIObject("Header", rootObject.transform, typeof(Image), typeof(MultiplayerChatDragHandle));
        RectTransform headerRect = headerObject.GetComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.sizeDelta = new Vector2(0f, 30f);
        headerRect.anchoredPosition = Vector2.zero;

        Image headerImage = headerObject.GetComponent<Image>();
        headerImage.color = new Color(0f, 0f, 0f, 0.52f);

        Text headerText = CreateText("Title", headerObject.transform, font, 15, TextAnchor.MiddleLeft);
        RectTransform headerTextRect = headerText.rectTransform;
        headerTextRect.anchorMin = Vector2.zero;
        headerTextRect.anchorMax = Vector2.one;
        headerTextRect.offsetMin = new Vector2(10f, 0f);
        headerTextRect.offsetMax = new Vector2(-304f, 0f);
        headerText.text = "Multiplayer Chat   (drag this bar)";
        headerText.color = new Color(1f, 1f, 1f, 0.92f);
        headerText.raycastTarget = false;

        enterOpensChatToggle = CreateToggle(
            "EnterOpensChatToggle",
            headerObject.transform,
            font,
            "Enter opens chat",
            enterOpensChat);
        RectTransform toggleRect = enterOpensChatToggle.GetComponent<RectTransform>();
        toggleRect.anchorMin = new Vector2(1f, 0.5f);
        toggleRect.anchorMax = new Vector2(1f, 0.5f);
        toggleRect.pivot = new Vector2(1f, 0.5f);
        toggleRect.anchoredPosition = new Vector2(-42f, 0f);
        toggleRect.sizeDelta = new Vector2(185f, 24f);
        Navigation toggleNavigation = enterOpensChatToggle.navigation;
        toggleNavigation.mode = Navigation.Mode.None;
        enterOpensChatToggle.navigation = toggleNavigation;
        enterOpensChatToggle.onValueChanged.AddListener(OnEnterOpensChatChanged);

        UnityEngine.UI.Button partyButton = CreateButton("PartyButton", headerObject.transform, font, "Party");
        RectTransform partyRect = partyButton.GetComponent<RectTransform>();
        partyRect.anchorMin = new Vector2(1f, 0.5f);
        partyRect.anchorMax = new Vector2(1f, 0.5f);
        partyRect.pivot = new Vector2(1f, 0.5f);
        partyRect.anchoredPosition = new Vector2(-232f, 0f);
        partyRect.sizeDelta = new Vector2(66f, 24f);
        partyButton.onClick.AddListener(delegate { partyWindowRequested = true; });

        UnityEngine.UI.Button closeButton = CreateButton("CloseButton", headerObject.transform, font, "×");
        RectTransform closeRect = closeButton.GetComponent<RectTransform>();
        closeRect.anchorMin = new Vector2(1f, 0.5f);
        closeRect.anchorMax = new Vector2(1f, 0.5f);
        closeRect.pivot = new Vector2(1f, 0.5f);
        closeRect.anchoredPosition = new Vector2(-4f, 0f);
        closeRect.sizeDelta = new Vector2(34f, 24f);
        closeButton.onClick.AddListener(delegate { closeRequestedFromButton = true; });

        dragHandle = headerObject.GetComponent<MultiplayerChatDragHandle>();
        dragHandle.Initialize(chatRoot);

        GameObject scrollObject = CreateUIObject("MessageScroll", rootObject.transform, typeof(Image), typeof(ScrollRect));
        RectTransform scrollRectTransform = scrollObject.GetComponent<RectTransform>();
        scrollRectTransform.anchorMin = Vector2.zero;
        scrollRectTransform.anchorMax = Vector2.one;
        scrollRectTransform.offsetMin = new Vector2(8f, 45f);
        scrollRectTransform.offsetMax = new Vector2(-8f, -36f);

        Image scrollBackground = scrollObject.GetComponent<Image>();
        scrollBackground.color = new Color(0f, 0f, 0f, 0.12f);
        scrollBackground.raycastTarget = true;

        scrollRect = scrollObject.GetComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 28f;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.12f;

        GameObject viewportObject = CreateUIObject("Viewport", scrollObject.transform, typeof(Image), typeof(RectMask2D));
        RectTransform viewportRect = viewportObject.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(5f, 4f);
        viewportRect.offsetMax = new Vector2(-22f, -4f);

        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
        viewportImage.raycastTarget = true;

        // Visible draggable scrollbar in addition to mouse-wheel scrolling.
        GameObject scrollbarObject = CreateUIObject(
            "VerticalScrollbar", scrollObject.transform, typeof(Image), typeof(Scrollbar));
        RectTransform scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = new Vector2(1f, 1f);
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.offsetMin = new Vector2(-17f, 4f);
        scrollbarRect.offsetMax = new Vector2(-3f, -4f);

        Image scrollbarTrack = scrollbarObject.GetComponent<Image>();
        scrollbarTrack.color = new Color(0f, 0f, 0f, 0.34f);
        scrollbarTrack.raycastTarget = true;

        GameObject slidingAreaObject = CreateUIObject("Sliding Area", scrollbarObject.transform);
        RectTransform slidingAreaRect = slidingAreaObject.GetComponent<RectTransform>();
        slidingAreaRect.anchorMin = Vector2.zero;
        slidingAreaRect.anchorMax = Vector2.one;
        slidingAreaRect.offsetMin = new Vector2(2f, 2f);
        slidingAreaRect.offsetMax = new Vector2(-2f, -2f);

        GameObject handleObject = CreateUIObject("Handle", slidingAreaObject.transform, typeof(Image));
        RectTransform handleRect = handleObject.GetComponent<RectTransform>();
        handleRect.anchorMin = Vector2.zero;
        handleRect.anchorMax = Vector2.one;
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;

        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(1f, 1f, 1f, 0.45f);
        handleImage.raycastTarget = true;

        verticalScrollbar = scrollbarObject.GetComponent<Scrollbar>();
        verticalScrollbar.handleRect = handleRect;
        verticalScrollbar.targetGraphic = handleImage;
        verticalScrollbar.direction = Scrollbar.Direction.BottomToTop;
        verticalScrollbar.size = 0.25f;

        scrollRect.verticalScrollbar = verticalScrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        scrollRect.verticalScrollbarSpacing = 2f;

        GameObject contentObject = CreateUIObject("Content", viewportObject.transform);
        scrollContent = contentObject.GetComponent<RectTransform>();
        scrollContent.anchorMin = new Vector2(0f, 1f);
        scrollContent.anchorMax = new Vector2(1f, 1f);
        scrollContent.pivot = new Vector2(0.5f, 1f);
        scrollContent.anchoredPosition = Vector2.zero;
        scrollContent.sizeDelta = new Vector2(0f, 1f);

        historyText = CreateText("Messages", contentObject.transform, font, 15, TextAnchor.UpperLeft);
        RectTransform historyRect = historyText.rectTransform;
        historyRect.anchorMin = Vector2.zero;
        historyRect.anchorMax = Vector2.one;
        historyRect.offsetMin = new Vector2(4f, 2f);
        historyRect.offsetMax = new Vector2(-4f, -2f);
        historyText.horizontalOverflow = HorizontalWrapMode.Wrap;
        historyText.verticalOverflow = VerticalWrapMode.Overflow;
        historyText.supportRichText = false;
        historyText.fontStyle = FontStyle.Bold;
        historyText.color = new Color(1f, 1f, 0f, 1f);
        historyText.raycastTarget = false;

        scrollRect.viewport = viewportRect;
        scrollRect.content = scrollContent;

        inputField = CreateInputField(rootObject.transform, font);

        ObserveGameplayCursorState();
        SetChatCanvasVisible(ShouldShowClosedChatCanvas());
        UpdatePassiveInputHint();

        Canvas.ForceUpdateCanvases();
        dragHandle.ApplySavedOrDefaultPosition(new Vector2(24f, 24f));
        ResizeHistoryContent();
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null)
            return;

        GameObject eventSystemObject = new GameObject(
            "MultiplayerChatEventSystem",
            typeof(EventSystem),
            typeof(StandaloneInputModule));
        DontDestroyOnLoad(eventSystemObject);
    }

    void OnEnterOpensChatChanged(bool enabled)
    {
        enterOpensChat = enabled;
        enterModeChangePending = true;

        // If this was enabled from the visible passive panel, activate chat on the
        // next Update rather than instantly hiding that same panel. This is queued
        // because changing InputField/UI state inside a Toggle callback can trigger
        // Unity UI lifecycle assertions.
        openChatAfterModeTogglePending = enabled &&
            !chatOpen &&
            !closingInputCapture &&
            chatCanvasGroup != null &&
            chatCanvasGroup.alpha > 0.01f;

        // A clicked Unity Toggle remains the EventSystem's selected object, and Enter
        // can submit it again. Clear that selection in LateUpdate, outside the click
        // callback. When chat is already typing, restore focus to the input afterwards.
        clearToggleSelectionPending = true;
        if (chatOpen)
            focusInputAtFrame = Mathf.Max(focusInputAtFrame, Time.frameCount + 1);

        PlayerPrefs.SetInt(EnterOpensChatPreferenceKey, enabled ? 1 : 0);
        PlayerPrefs.Save();
        UpdatePassiveInputHint();
    }

    void UpdatePassiveInputHint()
    {
        if (inputPlaceholder == null || chatOpen)
            return;

        inputPlaceholder.text = enterOpensChat
            ? "Press Enter or click here to chat..."
            : "Click here to chat...";
    }

    InputField CreateInputField(Transform parent, Font font)
    {
        GameObject inputObject = CreateUIObject("InputField", parent, typeof(Image), typeof(RectMask2D), typeof(InputField));
        RectTransform inputRect = inputObject.GetComponent<RectTransform>();
        inputRect.anchorMin = new Vector2(0f, 0f);
        inputRect.anchorMax = new Vector2(1f, 0f);
        inputRect.pivot = new Vector2(0.5f, 0f);
        inputRect.offsetMin = new Vector2(8f, 7f);
        inputRect.offsetMax = new Vector2(-8f, 37f);

        Image inputBackground = inputObject.GetComponent<Image>();
        inputBackground.color = new Color(0f, 0f, 0f, 0.46f);

        Text inputText = CreateText("Text", inputObject.transform, font, 16, TextAnchor.MiddleLeft);
        inputTextComponent = inputText;
        RectTransform inputTextRect = inputText.rectTransform;
        inputTextRect.anchorMin = Vector2.zero;
        inputTextRect.anchorMax = Vector2.one;
        inputTextRect.offsetMin = new Vector2(8f, 2f);
        inputTextRect.offsetMax = new Vector2(-8f, -2f);
        inputTextRect.pivot = new Vector2(0.5f, 0.5f);
        inputText.horizontalOverflow = HorizontalWrapMode.Overflow;
        inputText.verticalOverflow = VerticalWrapMode.Truncate;
        inputText.supportRichText = false;
        inputText.color = Color.white;

        Text placeholder = CreateText("Placeholder", inputObject.transform, font, 15, TextAnchor.MiddleLeft);
        inputPlaceholder = placeholder;
        RectTransform placeholderRect = placeholder.rectTransform;
        placeholderRect.anchorMin = Vector2.zero;
        placeholderRect.anchorMax = Vector2.one;
        placeholderRect.offsetMin = new Vector2(8f, 2f);
        placeholderRect.offsetMax = new Vector2(-8f, -2f);
        placeholder.text = "Type a message and press Enter...";
        placeholder.fontStyle = FontStyle.Italic;
        placeholder.color = new Color(1f, 1f, 1f, 0.48f);
        placeholder.raycastTarget = false;

        InputField field = inputObject.GetComponent<InputField>();
        field.textComponent = inputText;
        field.placeholder = placeholder;
        field.lineType = InputField.LineType.SingleLine;
        field.characterLimit = MaxMessageLength;
        // Unity's generated InputField caret can be offset on a runtime-created,
        // scaled overlay Canvas. Make it transparent and draw our own caret as a
        // child of the actual text RectTransform so it shares every scale/move.
        field.caretBlinkRate = 0.75f;
        field.caretWidth = 1;
        field.customCaretColor = true;
        field.caretColor = Color.clear;

        // Unity 2019's legacy UI InputField does not expose an onSelect UnityEvent.
        // Use a pointer-click EventTrigger instead. The callback only queues work;
        // OpenChat() still runs later from Update(), outside GUIUtility processing.
        EventTrigger clickTrigger = inputObject.AddComponent<EventTrigger>();
        clickTrigger.triggers = new List<EventTrigger.Entry>();
        EventTrigger.Entry clickEntry = new EventTrigger.Entry();
        clickEntry.eventID = EventTriggerType.PointerClick;
        clickEntry.callback = new EventTrigger.TriggerEvent();
        clickEntry.callback.AddListener(delegate(BaseEventData eventData) {
            if (!chatOpen && !closingInputCapture)
                manualOpenRequested = true;
        });
        clickTrigger.triggers.Add(clickEntry);

        GameObject caretObject = CreateUIObject("ChatCaret", inputText.transform, typeof(Image));
        customCaret = caretObject.GetComponent<RectTransform>();
        customCaret.anchorMin = new Vector2(0f, 0.5f);
        customCaret.anchorMax = new Vector2(0f, 0.5f);
        customCaret.pivot = new Vector2(0f, 0.5f);
        customCaret.anchoredPosition = Vector2.zero;
        customCaret.sizeDelta = new Vector2(2f, 18f);
        customCaretImage = caretObject.GetComponent<Image>();
        customCaretImage.color = new Color(1f, 1f, 1f, 0.95f);
        customCaretImage.raycastTarget = false;
        customCaretImage.canvasRenderer.SetAlpha(0f);

        field.ForceLabelUpdate();
        return field;
    }

    static Text CreateText(string name, Transform parent, Font font, int fontSize, TextAnchor alignment)
    {
        GameObject objectWithText = CreateUIObject(name, parent, typeof(Text));
        Text text = objectWithText.GetComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        return text;
    }

    static Toggle CreateToggle(string name, Transform parent, Font font, string label, bool initialValue)
    {
        GameObject toggleObject = CreateUIObject(name, parent, typeof(Toggle));
        Toggle toggle = toggleObject.GetComponent<Toggle>();

        GameObject backgroundObject = CreateUIObject("Background", toggleObject.transform, typeof(Image));
        RectTransform backgroundRect = backgroundObject.GetComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0f, 0.5f);
        backgroundRect.anchorMax = new Vector2(0f, 0.5f);
        backgroundRect.pivot = new Vector2(0f, 0.5f);
        backgroundRect.anchoredPosition = new Vector2(2f, 0f);
        backgroundRect.sizeDelta = new Vector2(16f, 16f);
        Image backgroundImage = backgroundObject.GetComponent<Image>();
        backgroundImage.color = new Color(0f, 0f, 0f, 0.62f);

        GameObject checkmarkObject = CreateUIObject("Checkmark", backgroundObject.transform, typeof(Image));
        RectTransform checkmarkRect = checkmarkObject.GetComponent<RectTransform>();
        checkmarkRect.anchorMin = Vector2.zero;
        checkmarkRect.anchorMax = Vector2.one;
        checkmarkRect.offsetMin = new Vector2(3f, 3f);
        checkmarkRect.offsetMax = new Vector2(-3f, -3f);
        Image checkmarkImage = checkmarkObject.GetComponent<Image>();
        checkmarkImage.color = new Color(1f, 1f, 0f, 1f);

        Text labelText = CreateText("Label", toggleObject.transform, font, 13, TextAnchor.MiddleLeft);
        RectTransform labelRect = labelText.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(23f, 0f);
        labelRect.offsetMax = Vector2.zero;
        labelText.text = label;
        labelText.color = new Color(1f, 1f, 1f, 0.92f);
        labelText.raycastTarget = false;

        toggle.targetGraphic = backgroundImage;
        toggle.graphic = checkmarkImage;
        toggle.isOn = initialValue;
        return toggle;
    }

    static UnityEngine.UI.Button CreateButton(string name, Transform parent, Font font, string label)
    {
        GameObject buttonObject = CreateUIObject(name, parent, typeof(Image), typeof(UnityEngine.UI.Button));
        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.28f);

        UnityEngine.UI.Button button = buttonObject.GetComponent<UnityEngine.UI.Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
        button.colors = colors;

        Text text = CreateText("Label", buttonObject.transform, font, 20, TextAnchor.MiddleCenter);
        text.text = label;
        text.raycastTarget = false;
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        return button;
    }

    static GameObject CreateUIObject(string name, Transform parent, params Type[] extraComponents)
    {
        List<Type> components = new List<Type>();
        components.Add(typeof(RectTransform));
        if (extraComponents != null)
            components.AddRange(extraComponents);

        GameObject go = new GameObject(name, components.ToArray());
        go.transform.SetParent(parent, false);
        return go;
    }

    void SetChatCanvasVisible(bool visible)
    {
        if (chatCanvas == null || chatCanvasGroup == null)
            return;

        // Keep the Canvas behaviour enabled. Toggling UIBehaviour/Canvas state from
        // input callbacks can trip Unity 2019's ShouldRunBehaviour assertion.
        chatCanvasGroup.alpha = visible ? 1f : 0f;
        chatCanvasGroup.interactable = visible;
        chatCanvasGroup.blocksRaycasts = visible;
    }
}

/// <summary>
/// Drags the chat window by its title bar and stores a resolution-independent position.
/// </summary>
public sealed class MultiplayerChatDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    const string PositionXKey = "TanguyMultiplayer.Chat.PositionX";
    const string PositionYKey = "TanguyMultiplayer.Chat.PositionY";

    RectTransform target;
    RectTransform parentRect;
    Vector2 pointerOffset;

    public void Initialize(RectTransform targetRect)
    {
        target = targetRect;
        parentRect = target != null ? target.parent as RectTransform : null;
    }

    public void ApplySavedOrDefaultPosition(Vector2 bottomLeftMargin)
    {
        if (target == null || parentRect == null)
            return;

        Canvas.ForceUpdateCanvases();

        Rect parent = parentRect.rect;
        Rect child = target.rect;
        float movableWidth = Mathf.Max(0f, parent.width - child.width);
        float movableHeight = Mathf.Max(0f, parent.height - child.height);

        Vector3 position = target.localPosition;
        if (PlayerPrefs.HasKey(PositionXKey) && PlayerPrefs.HasKey(PositionYKey))
        {
            float normalizedX = Mathf.Clamp01(PlayerPrefs.GetFloat(PositionXKey));
            float normalizedY = Mathf.Clamp01(PlayerPrefs.GetFloat(PositionYKey));
            position.x = parent.xMin + normalizedX * movableWidth;
            position.y = parent.yMin + normalizedY * movableHeight;
        }
        else
        {
            position.x = parent.xMin + bottomLeftMargin.x;
            position.y = parent.yMin + bottomLeftMargin.y;
        }

        target.localPosition = position;
        ClampToParent();
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (target == null || parentRect == null)
            return;

        Vector2 localPointer;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parentRect,
            eventData.position,
            eventData.pressEventCamera,
            out localPointer))
        {
            pointerOffset = (Vector2)target.localPosition - localPointer;
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (target == null || parentRect == null)
            return;

        Vector2 localPointer;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parentRect,
            eventData.position,
            eventData.pressEventCamera,
            out localPointer))
        {
            return;
        }

        Vector3 position = target.localPosition;
        position.x = localPointer.x + pointerOffset.x;
        position.y = localPointer.y + pointerOffset.y;
        target.localPosition = position;
        ClampToParent();
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        SavePosition();
    }

    void ClampToParent()
    {
        if (target == null || parentRect == null)
            return;

        Rect parent = parentRect.rect;
        Rect child = target.rect;

        float minX = parent.xMin;
        float maxX = parent.xMax - child.width;
        float minY = parent.yMin;
        float maxY = parent.yMax - child.height;

        if (maxX < minX)
            maxX = minX;
        if (maxY < minY)
            maxY = minY;

        Vector3 position = target.localPosition;
        position.x = Mathf.Clamp(position.x, minX, maxX);
        position.y = Mathf.Clamp(position.y, minY, maxY);
        target.localPosition = position;
    }

    void SavePosition()
    {
        if (target == null || parentRect == null)
            return;

        Rect parent = parentRect.rect;
        Rect child = target.rect;
        float movableWidth = Mathf.Max(0.001f, parent.width - child.width);
        float movableHeight = Mathf.Max(0.001f, parent.height - child.height);

        float normalizedX = Mathf.Clamp01((target.localPosition.x - parent.xMin) / movableWidth);
        float normalizedY = Mathf.Clamp01((target.localPosition.y - parent.yMin) / movableHeight);

        PlayerPrefs.SetFloat(PositionXKey, normalizedX);
        PlayerPrefs.SetFloat(PositionYKey, normalizedY);
        PlayerPrefs.Save();
    }
}
