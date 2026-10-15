using System;
using System.Collections;
using UnityEngine;
using Mirror;
using kcp2k;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility;

// Installed by MultiplayerManager only when explicitly launched with -dfu-server.
// This first server mode retains DFU's scene data services, but never loads a save,
// starts a character, starts a local Mirror client, or creates a host player.
public sealed class DedicatedServerBootstrap : MonoBehaviour
{
    public static bool Ready { get; private set; }
    NetworkManager manager;
    int lastConnectionCount = -1;
    float nextStatusTime;

    public static bool ValidateDataPath()
    {
        var options = DfuDedicatedServerOptions.Current;
        if (!string.IsNullOrEmpty(options.Error))
        {
            Fail(options.Error);
            return false;
        }
        try
        {
            if (!string.IsNullOrEmpty(options.DataPath) &&
                (DaggerfallUnity.Settings.MyDaggerfallPath != options.DataPath || !DaggerfallUnity.Instance.IsPathValidated))
            {
                // DFU accepts a Daggerfall installation folder or the ARENA2 folder.
                // Use its normal validation/content-reader initialization.
                DaggerfallUnity.Settings.MyDaggerfallPath = options.DataPath;
                DaggerfallUnity.Instance.ChangeArena2Path(options.DataPath);
            }
            if (!DaggerfallUnity.Instance.IsPathValidated || !DaggerfallUnity.Instance.IsReady || DaggerfallUnity.Instance.ContentReader == null)
            {
                Fail("Daggerfall data is unavailable. Configure DFU normally first, or pass -dfu-data followed by the quoted Daggerfall/ARENA2 path.");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Fail("Daggerfall resource initialization failed: " + e);
            return false;
        }
    }

    public static void Fail(string reason)
    {
        Ready = false;
        Debug.LogError("[DFUDedicated] STARTUP FAILED: " + reason);
        Application.Quit(1);
    }

    IEnumerator Start()
    {
        if (!DfuDedicatedServerOptions.Requested)
        {
            enabled = false;
            yield break;
        }
        Ready = false;
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 30;
        Debug.Log("[DFUDedicated] Preparing DFU services; no local network player will be created.");

        // Let scene Awake/Start methods finish before parking the support components.
        yield return null;
        if (!ValidateDataPath()) yield break;

        manager = GetComponent<NetworkManager>();
        var coordinator = GetComponent<MultiplayerManager>();
        var game = GameManager.Instance;
        if (manager == null || coordinator == null || game == null ||
            game.PlayerObject == null || game.PlayerEntity == null || game.PlayerEnterExit == null || game.PlayerGPS == null)
        {
            Fail("The game scene is missing its NetworkManager or DFU player data services.");
            yield break;
        }
        if (NetworkServer.active || NetworkClient.active)
        {
            Fail("Networking started before dedicated initialization. Check for a second NetworkManager or automatic startup script.");
            yield break;
        }

        try
        {
            // Initialize the normal Start-state services without firing new-game or
            // load-save events for a fictional host character.
            string configPath;
            var options = DfuDedicatedServerOptions.Current;
            DedicatedServerConfig config = DedicatedServerConfig.Load(options, out configPath);
            config.ApplyGameplayOptions();

            // The policy lives next to DedicatedServer.json, never in DFU's Mods.json.
            // Export metadata and install pre-player admission before StartServer().
            string modPolicyDirectory = System.IO.Path.GetDirectoryName(configPath);
            var modAuthenticator = ModCompatibilityAuthenticator.Install(manager);
            modAuthenticator.UseServerPolicyDirectory(modPolicyDirectory);
            ModCompatibilityPolicy.ExportServerInventory(modPolicyDirectory);
            Debug.Log("[DFUDedicated] Configuration: " + configPath + " dungeonSize=" + config.world.dungeonSize +
                " defaultLeaderControlsTime=" + config.partyDefaults.leaderControlsTime + " defaultQuestSync=" + config.partyDefaults.questSync);
            StateManager stateManager = game.StateManager;
            ParkLocalPlayer(game);
            if (!coordinator.PrepareDirectIPTransport())
                throw new InvalidOperationException("Could not select the existing KCP transport.");
            KcpTransport kcp = Transport.activeTransport as KcpTransport;
            if (kcp == null)
                throw new InvalidOperationException("Dedicated startup requires KcpTransport.");
            kcp.Port = (ushort)(options.Port != 0 ? options.Port : config.network.port);
            manager.maxConnections = options.MaxPlayers != 0 ? options.MaxPlayers : config.network.maxPlayers;
            if (manager.playerPrefab == null)
                throw new InvalidOperationException("NetworkManager has no player prefab.");
            // A scene switch here would replace the services just initialized.
            if (!string.IsNullOrEmpty(manager.onlineScene))
                throw new InvalidOperationException("Dedicated startup expects the existing empty NetworkManager Online Scene setting.");

            game.PauseGame(false);
            manager.StartServer();
            if (!NetworkServer.active || !kcp.ServerActive() || NetworkClient.active || NetworkServer.localConnection != null)
                throw new InvalidOperationException("KCP did not enter server-only mode.");
            Ready = true;
            Debug.Log("[DFUDedicated] READY: KCP UDP port=" + kcp.Port + " maxPlayers=" + manager.maxConnections +
                " localClient=false. Connect using the existing Direct IP client option. Party time follows each party's selected mode; weather follows its leader.");
        }
        catch (Exception e)
        {
            if (NetworkServer.active) manager.StopServer();
            Fail(e.ToString());
        }
    }

    static void ParkLocalPlayer(GameManager game)
    {
        // Keep the object active so legacy Find/GetComponent data lookups work.
        // Disable local simulation, input, collisions and rendering. These objects
        // are not connection candidates: no PlayerMultiplayer is spawned for them.
        game.PlayerEntity.Quiesce = true;
        foreach (MonoBehaviour behaviour in game.PlayerObject.GetComponentsInChildren<MonoBehaviour>(true))
        {
            behaviour.StopAllCoroutines();
            behaviour.enabled = false;
        }
        foreach (Collider collider in game.PlayerObject.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        foreach (Rigidbody body in game.PlayerObject.GetComponentsInChildren<Rigidbody>(true))
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
        foreach (Renderer renderer in game.PlayerObject.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (Camera camera in FindObjectsOfType<Camera>())
            camera.enabled = false;
        foreach (AudioListener listener in FindObjectsOfType<AudioListener>())
            listener.enabled = false;
        AudioListener.pause = true;
        if (game.StreamingWorld != null)
        {
            game.StreamingWorld.StopAllCoroutines();
            game.StreamingWorld.enabled = false;
        }
        if (game.StartGameBehaviour != null)
            game.StartGameBehaviour.enabled = false;
        // Keep generation services and dungeon geometry available. No exterior
        // terrain or playable character is loaded around this support object.
    }

    void Update()
    {
        if (!Ready || Time.unscaledTime < nextStatusTime) return;
        nextStatusTime = Time.unscaledTime + 1f;
        int count = NetworkServer.connections.Count;
        if (count != lastConnectionCount)
        {
            lastConnectionCount = count;
            Debug.Log("[DFUDedicated] Connected clients=" + count);
        }
    }

    void OnDestroy()
    {
        Ready = false;
    }
}
