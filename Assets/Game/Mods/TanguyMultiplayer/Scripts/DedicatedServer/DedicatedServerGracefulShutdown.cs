using System;
using System.Collections;
using Process = System.Diagnostics.Process;
using System.IO;
using System.Text;
using UnityEngine;
using Mirror;

// Dedicated-server-only shutdown request listener.
// The manager writes DedicatedServer.shutdown beside the active DedicatedServer.json.
// The file contains the target process ID, preventing stale shutdown files from
// terminating a later server instance.
public sealed class DedicatedServerGracefulShutdown : MonoBehaviour
{
    string requestPath;
    int ownPid;
    bool shuttingDown;
    float nextPoll;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (!DfuDedicatedServerOptions.Requested)
            return;

        var go = new GameObject("Dedicated server graceful shutdown");
        DontDestroyOnLoad(go);
        go.AddComponent<DedicatedServerGracefulShutdown>();
    }

    void Awake()
    {
        ownPid = Process.GetCurrentProcess().Id;
        string configPath = FindCommandLineValue("-dfu-config");
        if (string.IsNullOrEmpty(configPath))
        {
            Debug.LogWarning("[DFUDedicated] Graceful shutdown listener disabled: -dfu-config path was not supplied.");
            enabled = false;
            return;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        requestPath = Path.Combine(directory, "DedicatedServer.shutdown");

        // A marker left behind by a crashed/force-killed older process must not stop
        // this new server. Managers also delete stale markers before launch.
        RemoveStaleRequest();
    }

    void Update()
    {
        if (shuttingDown || !enabled || Time.realtimeSinceStartup < nextPoll)
            return;
        nextPoll = Time.realtimeSinceStartup + 0.25f;

        if (!File.Exists(requestPath))
            return;

        try
        {
            string text = File.ReadAllText(requestPath, Encoding.UTF8).Trim();
            int targetPid;
            if (!int.TryParse(text, out targetPid) || targetPid != ownPid)
            {
                // Invalid/stale request: remove it so it cannot affect a later launch.
                File.Delete(requestPath);
                Debug.LogWarning("[DFUDedicated] Ignored stale/invalid shutdown request.");
                return;
            }

            File.Delete(requestPath);
            shuttingDown = true;
            StartCoroutine(ShutdownRoutine());
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DFUDedicated] Could not process graceful shutdown request: " + e.Message);
        }
    }

    IEnumerator ShutdownRoutine()
    {
        Debug.Log("[DFUDedicated] Graceful shutdown requested. Disconnecting clients and stopping Mirror/KCP.");

        // StopServer() performs Mirror server shutdown and transport shutdown. No
        // dungeon/enemy/world persistence is performed or required here.
        if (NetworkServer.active)
        {
            if (NetworkManager.singleton != null)
                NetworkManager.singleton.StopServer();
            else
                NetworkServer.Shutdown();
        }

        // Give Unity one short unscaled interval to finish normal shutdown callbacks
        // and flush the final log messages before quitting the process.
        yield return new WaitForSecondsRealtime(0.25f);

        Debug.Log("[DFUDedicated] Graceful shutdown complete. Quitting Unity.");
        Application.Quit();
    }

    void RemoveStaleRequest()
    {
        try
        {
            if (!File.Exists(requestPath)) return;
            string text = File.ReadAllText(requestPath, Encoding.UTF8).Trim();
            int targetPid;
            if (!int.TryParse(text, out targetPid) || targetPid != ownPid)
                File.Delete(requestPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DFUDedicated] Could not clear stale shutdown request: " + e.Message);
        }
    }

    static string FindCommandLineValue(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
