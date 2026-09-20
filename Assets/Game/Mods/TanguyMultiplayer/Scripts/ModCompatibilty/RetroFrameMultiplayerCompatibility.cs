// Independent multiplayer compatibility helper. No Retro-Frame implementation
// or assets are included. Uses its documented "getOverlay" mod message only.
using System;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;

public sealed class RetroFrameMultiplayerCompatibility : MonoBehaviour
{
    const string LogPrefix = "[MPRetroFrame] ";
    static RetroFrameMultiplayerCompatibility instance;
    Panel overlay;
    Panel savedParent;
    DaggerfallHUD savedHud;
    PlayerEntity savedPlayer;
    bool savedEnabled;
    bool savedAttached;
    bool hasSnapshot;
    bool downed;
    int recoveryFrames;
    float nextLookup;
    int requestVersion;
    int verifyFrames;
    bool warned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance != null)
            return;
        GameObject owner = new GameObject("RetroFrameMultiplayerCompatibility");
        DontDestroyOnLoad(owner);
        instance = owner.AddComponent<RetroFrameMultiplayerCompatibility>();
    }

    void OnEnable()
    {
        PlayerDeath.OnPlayerDeath += OnPlayerDeath;
    }

    void OnDisable()
    {
        PlayerDeath.OnPlayerDeath -= OnPlayerDeath;
        ResetTracking();
    }

    void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    void OnPlayerDeath(object sender, EventArgs args)
    {
        if (MultiplayerRespawnManager.IsMultiplayerActive())
            BeginDowned();
    }

    void BeginDowned()
    {
        if (downed)
            return;
        downed = true;
        recoveryFrames = 0;
        verifyFrames = 0;
        // Keep the last living snapshot: other death subscribers may already
        // have disabled/removed the panel by the time this event reaches us.
        ++requestVersion;
        if (overlay != null)
            Debug.Log(LogPrefix + "Downed; snapshot=" + hasSnapshot +
                " enabledBeforeDeath=" + savedEnabled +
                " attachedBeforeDeath=" + savedAttached +
                " hasParent=" + (savedParent != null));
    }

    void LateUpdate()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            if (!warned)
            {
                warned = true;
                Debug.LogWarning(LogPrefix + "Overlay recovery failed: " + ex);
            }
            ResetTracking();
            nextLookup = Time.realtimeSinceStartup + 5f;
        }
    }

    void Tick()
    {
        if (!MultiplayerRespawnManager.IsMultiplayerActive() ||
            GameManager.Instance == null || DaggerfallUI.Instance == null ||
            (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress))
        {
            ResetTracking();
            return;
        }

        PlayerEntity player = GameManager.Instance.PlayerEntity;
        DaggerfallHUD hud = DaggerfallUI.Instance.DaggerfallHUD;
        if (player == null || hud == null || GameManager.Instance.PlayerObject == null)
        {
            ResetTracking();
            return;
        }
        if (savedPlayer != player || savedHud != hud)
        {
            ResetTracking();
            savedPlayer = player;
            savedHud = hud;
        }

        PlayerDeath death = GameManager.Instance.PlayerObject.GetComponent<PlayerDeath>();
        if (player.CurrentHealth <= 0 || (death != null && death.DeathInProgress))
        {
            BeginDowned();
            return;
        }

        if (downed)
        {
            // Both health restoration and ClearDeathAnimation must be complete.
            // Wait two living frames, and leave menu/input pauses alone.
            if (InputManager.Instance != null && InputManager.Instance.IsPaused)
                return;
            if (++recoveryFrames < 2)
                return;
            downed = false;
            RecoverOverlay();
            return;
        }

        if (verifyFrames > 0)
        {
            if (--verifyFrames == 0 && overlay != null && hasSnapshot)
                Debug.Log(LogPrefix + "After recovery: enabled=" + overlay.Enabled +
                    " hasParent=" + (overlay.Parent != null) +
                    " attached=" + Contains(savedParent, overlay) +
                    ". If the frame is still missing, include this line in the report.");
            return;
        }

        if (Time.realtimeSinceStartup >= nextLookup)
        {
            nextLookup = Time.realtimeSinceStartup + 1f;
            QueryOverlay();
        }

        // Avoid replacing the living snapshot with a menu's temporary UI state.
        if (overlay != null && (InputManager.Instance == null || !InputManager.Instance.IsPaused))
        {
            Panel parent = overlay.Parent as Panel;
            // A mod may draw a root Panel itself. Parent membership is optional,
            // and must not prevent us from capturing/restoring Enabled.
            bool firstSnapshot = !hasSnapshot;
            savedParent = parent;
            savedAttached = Contains(parent, overlay);
            savedEnabled = overlay.Enabled;
            hasSnapshot = true;
            if (firstSnapshot)
                Debug.Log(LogPrefix + "v2 captured living overlay: enabled=" + savedEnabled +
                    " hasParent=" + (savedParent != null) + " attached=" + savedAttached);
        }
    }

    void QueryOverlay()
    {
        if (ModManager.Instance == null)
            return;
        Mod mod = ModManager.Instance.GetMod("Retro-Frame");
        if (mod == null || !mod.IsReady || mod.MessageReceiver == null)
            return;
        int version = ++requestVersion;
        mod.MessageReceiver("getOverlay", null, (message, data) =>
        {
            if (this == null || !isActiveAndEnabled || downed || version != requestVersion)
                return;
            Panel found = data as Panel;
            if (found == null || ReferenceEquals(found, overlay))
                return;
            overlay = found;
            hasSnapshot = false;
            savedParent = null;
            savedAttached = false;
            Debug.Log(LogPrefix + "Detected Retro-Frame overlay through getOverlay.");
        });
    }

    void RecoverOverlay()
    {
        if (overlay == null)
            return;
        if (!hasSnapshot)
        {
            Debug.LogWarning(LogPrefix + "Recovery skipped: no living overlay captured before death.");
            return;
        }
        // Never turn on a frame that the player had disabled before being downed.
        if (!savedEnabled)
        {
            Debug.LogWarning(LogPrefix + "Recovery skipped: overlay was already disabled before death. " +
                "If the frame was visible then, its renderer may use a separate visibility state.");
            return;
        }
        // Respect an intentional move into a different UI container.
        Panel currentParent = overlay.Parent as Panel;
        if (currentParent != null && currentParent != savedParent && Contains(currentParent, overlay))
        {
            Debug.LogWarning(LogPrefix + "Recovery skipped: overlay moved to another parent.");
            return;
        }
        bool wasAttached = Contains(savedParent, overlay);
        bool wasEnabled = overlay.Enabled;
        bool reattach = savedAttached && savedParent != null && !wasAttached;
        if (reattach)
            savedParent.Components.Add(overlay);
        overlay.Enabled = savedEnabled;
        Debug.Log(LogPrefix + "v2 recovered after revive/respawn: reattached=" + reattach +
            " reenabled=" + !wasEnabled + " enabled=" + overlay.Enabled +
            " attachedBeforeDeath=" + savedAttached + ".");
        // Diagnose a mod that overrides the repair; do not fight it every frame.
        verifyFrames = 3;
    }

    static bool Contains(Panel parent, Panel child)
    {
        if (parent == null || child == null)
            return false;
        foreach (BaseScreenComponent component in parent.Components)
            if (ReferenceEquals(component, child))
                return true;
        return false;
    }

    void ResetTracking()
    {
        ++requestVersion;
        overlay = null;
        savedParent = null;
        savedHud = null;
        savedPlayer = null;
        savedAttached = false;
        savedEnabled = false;
        hasSnapshot = false;
        downed = false;
        recoveryFrames = 0;
        verifyFrames = 0;
    }
}
