using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;

// Local filesystem command interface. Installed only by the dedicated bootstrap.
// No extra network listener; all Unity work executes from Update on the main thread.
public sealed class DedicatedServerAdmin : MonoBehaviour
{
    [Serializable] public sealed class Status
    {
        public string session, utc;
        public int players, connections, maxPlayers, enemies, serverOwnedEnemies, clientOwnedEnemies;
        public int activeEnemies, inactiveEnemies, deactivatedEnemies, testEnemies, passiveTestEnemies, dungeons, pendingDungeons;
        public double averageFrameMs, unityAllocatedMiB;
    }
    [Serializable] sealed class Request { public string session, command; }
    [Serializable] sealed class Reply { public string session, command, result, utc; public bool success; }
    string directory, session;
    float nextPoll, nextStatus;
    double frameSeconds;
    int frames;
    bool ioErrorLogged;

    public void Initialize(string configDirectory)
    {
        directory = Path.Combine(configDirectory, "DedicatedServerAdmin");
        Directory.CreateDirectory(Path.Combine(directory, "commands"));
        Directory.CreateDirectory(Path.Combine(directory, "results"));
        session = Guid.NewGuid().ToString("N");
        Publish();
        Debug.Log("[DFUAdmin] Local commands/status: " + directory);
    }

    public static Status ReadStatistics()
    {
        var s = new Status();
        s.utc = DateTime.UtcNow.ToString("o");
        s.maxPlayers = NetworkManager.singleton != null ? NetworkManager.singleton.maxConnections : 0;
        s.connections = NetworkServer.connections.Count;
        foreach (var c in NetworkServer.connections.Values)
            if (c != null && c.isAuthenticated && c.identity != null && c.identity.GetComponent<PlayerMultiplayer>() != null)
                s.players++;
        foreach (var id in NetworkServer.spawned.Values)
        {
            if (id == null) continue;
            var dungeon = id.GetComponent<DaggerfallDungeon>();
            if (dungeon != null) { if (dungeon.isSet) s.dungeons++; else s.pendingDungeons++; }
            if (id.GetComponent<PlayerMultiplayer>() != null || id.GetComponent<DaggerfallEnemy>() == null) continue;
            s.enemies++;
            if (id.connectionToClient == null) s.serverOwnedEnemies++; else s.clientOwnedEnemies++;
            var authority = id.GetComponent<DynamicEnemyAuthority>();
            if (authority != null && authority.IsAuthorityDeactivatedForRetention) s.deactivatedEnemies++;
            if (authority != null && authority.KeepForDungeonGridTest) s.testEnemies++;
            if (authority != null && authority.PassiveDungeonGridTest) s.passiveTestEnemies++;
            if (id.gameObject.activeInHierarchy) s.activeEnemies++; else s.inactiveEnemies++;
        }
        s.unityAllocatedMiB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0;
        return s;
    }

    public static string StatisticsText()
    {
        var s = ReadStatistics();
        return string.Format("Players={0}/{1}, connections={2}; enemies={3} (server-owned={4}, client-owned={5}, active objects={6}, inactive objects={7}, tests={8}, observer tests={9}); dungeons={10}, pending={11}; Unity allocated={12:F1} MiB.",
            s.players, s.maxPlayers, s.connections, s.enemies, s.serverOwnedEnemies, s.clientOwnedEnemies,
            s.activeEnemies, s.inactiveEnemies, s.testEnemies, s.passiveTestEnemies, s.dungeons, s.pendingDungeons, s.unityAllocatedMiB);
    }

    void Update()
    {
        if (directory == null || !DedicatedServerBootstrap.Ready || !NetworkServer.active) return;
        frameSeconds += Time.unscaledDeltaTime;
        frames++;
        try
        {
            if (Time.realtimeSinceStartup >= nextPoll)
            {
                nextPoll = Time.realtimeSinceStartup + 0.5f;
                PollCommands();
            }
            if (Time.realtimeSinceStartup >= nextStatus)
            {
                nextStatus = Time.realtimeSinceStartup + 2f;
                Publish();
            }
            ioErrorLogged = false;
        }
        catch (Exception e)
        {
            if (!ioErrorLogged) Debug.LogWarning("[DFUAdmin] Local file interface: " + e.Message);
            ioErrorLogged = true;
        }
    }

    void Publish()
    {
        var s = ReadStatistics();
        s.session = session;
        s.averageFrameMs = frames > 0 ? frameSeconds * 1000.0 / frames : 0;
        WriteJson(Path.Combine(directory, "status.json"), JsonUtility.ToJson(s, true));
        frameSeconds = 0; frames = 0;
    }

    static void WriteJson(string path, string json)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    void PollCommands()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(directory, "commands"), "*.json").OrderBy(p => p).Take(8))
        {
            var reply = new Reply { session = session, utc = DateTime.UtcNow.ToString("o") };
            try
            {
                if (new FileInfo(path).Length > 4096) throw new InvalidOperationException("Command request is too large.");
                var request = JsonUtility.FromJson<Request>(File.ReadAllText(path));
                if (request == null || request.session != session) throw new InvalidOperationException("Stale server session; resend the command.");
                reply.command = request.command;
                string[] words = (request.command ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) throw new InvalidOperationException("Empty command.");
                if (words[0].Equals("serverstats", StringComparison.OrdinalIgnoreCase)) reply.result = StatisticsText();
                else if (words[0].Equals("help", StringComparison.OrdinalIgnoreCase))
                    reply.result = "serverstats | dungeongrid status | dungeongrid clear | dungeongrid spawn <1..500> <region> [enemies|remote-test]";
                else if (words[0].Equals("dungeongrid", StringComparison.OrdinalIgnoreCase))
                    reply.result = NetworkDungeonGrid.Command(words.Skip(1).ToArray());
                else throw new InvalidOperationException("Supported commands: help, serverstats, dungeongrid.");
                reply.success = true;
            }
            catch (Exception e) { reply.result = e.Message; reply.success = false; }
            // Remove before executing another poll; result files are for the local UI only.
            File.Delete(path);
            WriteJson(Path.Combine(directory, "results", Path.GetFileName(path)), JsonUtility.ToJson(reply, true));
            Debug.Log("[DFUAdmin] " + reply.command + ": " + reply.result);
        }
    }
}
