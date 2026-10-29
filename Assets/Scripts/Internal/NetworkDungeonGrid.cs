// Multiplayer dungeon placement and opt-in enemy-free stress testing.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using DaggerfallConnect;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop
{
    public sealed class NetworkDungeonGrid : MonoBehaviour
    {
        public const int LevelsPerColumn = 8;
        public const float ColumnStep = 512f;
        public static bool IsSafeRecoveryOrigin(Vector3 origin)
        {
            return !float.IsNaN(origin.x) && !float.IsInfinity(origin.x) &&
                !float.IsNaN(origin.y) && !float.IsInfinity(origin.y) &&
                !float.IsNaN(origin.z) && !float.IsInfinity(origin.z) && Mathf.Abs(origin.y) < 4800f;
        }
        // Reserve one whole extra block on every horizontal edge. Special/modded layouts
        // can span multiple columns; allocation skips conflicting slots instead of overlapping.
        public static Bounds LayoutBounds(DFLocation location, Vector3 origin)
        {
            float side = RDBLayout.RDBSide;
            Vector3 min = new Vector3(-side, -145f, -side);
            Vector3 max = new Vector3(side * 2, 145f, side * 2);
            if (location.Dungeon.Blocks != null && location.Dungeon.Blocks.Length > 0)
            {
                min.x = min.z = float.MaxValue;
                max.x = max.z = float.MinValue;
                foreach (var block in location.Dungeon.Blocks)
                {
                    min.x = Mathf.Min(min.x, (block.X - 1) * side);
                    max.x = Mathf.Max(max.x, (block.X + 2) * side);
                    min.z = Mathf.Min(min.z, (block.Z - 1) * side);
                    max.z = Mathf.Max(max.z, (block.Z + 2) * side);
                }
            }
            var bounds = new Bounds();
            bounds.SetMinMax(origin + min, origin + max);
            return bounds;
        }

        public static Vector3 SlotPosition(int slot)
        {
            int column = slot / LevelsPerColumn;
            int x = 0, z = 0, dx = 1, dz = 0, length = 1, walked = 0, turns = 0;
            for (int n = 0; n < column; n++)
            {
                x += dx; z += dz;
                if (++walked == length)
                {
                    walked = 0;
                    int oldDx = dx; dx = -dz; dz = oldDx;
                    if (++turns % 2 == 0) length++;
                }
            }
            return new Vector3(x * ColumnStep,
                DaggerfallDungeon.NetworkDungeonFirstY + (slot % LevelsPerColumn) * DaggerfallDungeon.NetworkDungeonYSpacing,
                z * ColumnStep);
        }

        public static Vector3 Allocate(DFLocation location, DaggerfallDungeon ignore = null)
        {
            var occupied = new List<Bounds>();
            foreach (var dungeon in FindObjectsOfType<DaggerfallDungeon>())
            {
                if (dungeon == ignore || (!dungeon.HasGridPosition && Mathf.Abs(dungeon.PositionY) < 1f)) continue;
                // Old live slots (including below the new eighth level) are respected too.
                occupied.Add(LayoutBounds(dungeon.Summary.LocationData, dungeon.GridPosition));
            }
            for (int slot = 0; slot < 65536; slot++)
            {
                Vector3 position = SlotPosition(slot);
                Bounds bounds = LayoutBounds(location, position);
                if (!occupied.Any(other => other.Intersects(bounds)))
                {
                    Debug.Log($"[DungeonGrid] Reserved slot={slot + 1} column={slot / LevelsPerColumn} origin={position} location='{location.RegionName}/{location.Name}'");
                    return position;
                }
            }
            throw new InvalidOperationException("No free dungeon grid slot within allocation limit.");
        }

        public static bool Contains(DaggerfallDungeon dungeon, Vector3 position)
        {
            return dungeon != null && dungeon.isSet && LayoutBounds(dungeon.Summary.LocationData, dungeon.GridPosition).Contains(position);
        }

        public static DaggerfallDungeon FindAt(Vector3 position)
        {
            DaggerfallDungeon best = null;
            float distance = float.MaxValue;
            RefreshLookup();
            foreach (var entry in lookupBounds)
            {
                var dungeon = entry.Key;
                if (dungeon == null || !entry.Value.Contains(position)) continue;
                float d = (position - dungeon.GridPosition).sqrMagnitude;
                if (d < distance) { best = dungeon; distance = d; }
            }
            return best;
        }

        // The grid has repeated heights. Keep an enemy's player-target candidates
        // within its dungeon when separate columns share a Unity height.
        public static bool SharesDungeon(Vector3 enemyPosition, string enemyDungeonId, global::PlayerMultiplayer player)
        {
            if (enemyPosition.y > -300f || player == null) return true;
            RefreshLookup();
            DaggerfallDungeon owner = null;
            if (!string.IsNullOrEmpty(enemyDungeonId))
                lookupById.TryGetValue(enemyDungeonId, out owner);
            if (owner == null) owner = FindAt(enemyPosition);
            if (owner == null) return false;
            Bounds bounds;
            return lookupBounds.TryGetValue(owner, out bounds) && bounds.Contains(player.transform.position);
        }

        public static void InvalidateLookup() { nextLookup = 0f; }
        static float nextLookup;
        static readonly Dictionary<DaggerfallDungeon, Bounds> lookupBounds = new Dictionary<DaggerfallDungeon, Bounds>();
        static readonly Dictionary<string, DaggerfallDungeon> lookupById = new Dictionary<string, DaggerfallDungeon>();
        static void RefreshLookup()
        {
            if (Time.unscaledTime >= nextLookup || nextLookup - Time.unscaledTime > 1f)
            {
                nextLookup = Time.unscaledTime + 0.25f;
                lookupBounds.Clear(); lookupById.Clear();
                foreach (var dungeon in FindObjectsOfType<DaggerfallDungeon>())
                {
                    if (!dungeon.isSet) continue;
                    lookupBounds[dungeon] = LayoutBounds(dungeon.Summary.LocationData, dungeon.GridPosition);
                    if (!string.IsNullOrEmpty(dungeon.DungeonInstanceId)) lookupById[dungeon.DungeonInstanceId] = dungeon;
                }
            }
        }

        public static DaggerfallDungeon CreatingTestDungeon;
        public static bool CreatingPassiveTest;
        public static int PendingTestEnemyBlocks { get; private set; }
        static int creationFrame = -1, creationsThisFrame;
        public static bool TakeEnemyCreationSlot()
        {
            if (creationFrame != Time.frameCount) { creationFrame = Time.frameCount; creationsThisFrame = 0; }
            if (creationsThisFrame >= 2) return false;
            creationsThisFrame++;
            return true;
        }
        public static IEnumerator TrackTestEnemyBlock(IEnumerator routine)
        {
            PendingTestEnemyBlocks++;
            try { while (routine.MoveNext()) yield return routine.Current; }
            finally { (routine as IDisposable)?.Dispose(); PendingTestEnemyBlocks--; }
        }

        static NetworkDungeonGrid runner;
        bool running;
        bool cancel;
        readonly List<DaggerfallDungeon> tests = new List<DaggerfallDungeon>();
        readonly Dictionary<DaggerfallDungeon, HashSet<GameObject>> testEnemies = new Dictionary<DaggerfallDungeon, HashSet<GameObject>>();

        public static void RegisterCommands()
        {
            Wenzil.Console.ConsoleCommandsDatabase.RegisterCommand("enemyinterest", "Enemy network relevance status / on / off.",
                "enemyinterest status|on|off", global::EnemyInterestManagement.Command);
            Wenzil.Console.ConsoleCommandsDatabase.RegisterCommand("serverstats", "Report server players, enemies and dungeons.",
                "serverstats", args => NetworkServer.active ? DedicatedServerAdmin.StatisticsText() : "Server only.");
            Wenzil.Console.ConsoleCommandsDatabase.RegisterCommand("dungeongrid", "Generate retained server test dungeons; report or clear them.",
                "dungeongrid spawn <count> [region name or index] [enemies|remote-test]\ndungeongrid status\ndungeongrid clear\ndungeongrid bench baseline|scripts|physics [seconds]\ndungeongrid bench restore|status\ndungeongrid objects spawn empty|identity|transform <count>\ndungeongrid objects measure [seconds] | strip | clear | status", Command);
        }

        public static string Command(params string[] args)
        {
            if (!NetworkServer.active) return "Start a multiplayer host/server first. This command is server-only.";
            if (args.Length == 0) return "Usage: dungeongrid spawn <count> [region name or index] [enemies|remote-test] | status | clear";
            if (runner == null)
            {
                var go = new GameObject("Dungeon grid test runner");
                DontDestroyOnLoad(go);
                runner = go.AddComponent<NetworkDungeonGrid>();
            }
            if (args[0].Equals("objects", StringComparison.OrdinalIgnoreCase)) return runner.ObjectProbeCommand(args);
            if (args[0].Equals("bench", StringComparison.OrdinalIgnoreCase))
            {
                if (runner.objectProbeBusy || runner.objectProbeMeasuring || runner.objectProbeObjects.Count != 0)
                    return "Clear the synthetic benchmark first: dungeongrid objects clear";
                return runner.ProbeCommand(args);
            }
            if (args[0].Equals("status", StringComparison.OrdinalIgnoreCase)) return runner.Report();
            if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                runner.objectProbeCancel = true;
                runner.ClearObjectProbe();
                runner.RestoreProbe();
                runner.cancel = true;
                int cleared = 0, occupied = 0;
                foreach (var dungeon in runner.tests.ToArray())
                {
                    if (dungeon == null) { runner.tests.Remove(dungeon); continue; }
                    if (FindObjectsOfType<global::PlayerMultiplayer>().Any(p => Contains(dungeon, p.transform.position)))
                    { occupied++; continue; }
                    runner.tests.Remove(dungeon);
                    runner.ClearTestEnemies(dungeon);
                    NetworkServer.Destroy(dungeon.gameObject);
                    cleared++;
                }
                return $"Cleared {cleared} test dungeons; retained {occupied} with players inside. Spawn queue cancelled.";
            }
            int count;
            if (!args[0].Equals("spawn", StringComparison.OrdinalIgnoreCase) || args.Length < 2 ||
                !int.TryParse(args[1], out count) || count < 1 || count > 500)
                return "Usage: dungeongrid spawn <1..500> [region name or index] [enemies|remote-test] | status | clear";
            if (runner.running) return "A spawn run is already active. Use status or clear.";
            if (runner.objectProbeBusy || runner.objectProbeMeasuring || runner.objectProbeObjects.Count != 0)
                return "Use dungeongrid objects clear before spawning test dungeons.";
            if (runner.probeMode != "off") return "Use dungeongrid bench restore before spawning more dungeons.";
            var reader = DaggerfallUnity.Instance.ContentReader.MapFileReader;
            string mode = "none";
            int regionEnd = args.Length;
            if (args.Length > 2 && (args[args.Length - 1].Equals("enemies", StringComparison.OrdinalIgnoreCase) ||
                args[args.Length - 1].Equals("remote-test", StringComparison.OrdinalIgnoreCase)))
            { mode = args[args.Length - 1].ToLowerInvariant(); regionEnd--; }
            string regionArg = regionEnd > 2 ? string.Join(" ", args.Skip(2).Take(regionEnd - 2).ToArray()).Trim('"') :
                GameManager.Instance != null && GameManager.Instance.PlayerGPS != null ? GameManager.Instance.PlayerGPS.CurrentRegion.Name : null;
            if (string.IsNullOrEmpty(regionArg)) return "Specify a region name or index on a headless server.";
            int regionIndex = -1, parsed;
            if (int.TryParse(regionArg, out parsed) && parsed >= 0 && parsed < reader.RegionCount) regionIndex = parsed;
            else for (int i = 0; i < reader.RegionCount; i++)
                if (string.Equals(reader.GetRegionName(i), regionArg, StringComparison.OrdinalIgnoreCase)) { regionIndex = i; break; }
            if (regionIndex < 0) return "Region not found: " + regionArg;
            if (NetworkManager.singleton == null || !NetworkManager.singleton.spawnPrefabs.Any(p => p != null && p.GetComponent<DaggerfallDungeon>() != null))
                return "The registered network dungeon prefab is missing.";
            if (!reader.LoadRegion(regionIndex)) return "Could not load region: " + reader.GetRegionName(regionIndex);
            runner.cancel = false;
            runner.running = true;
            runner.StartCoroutine(runner.Spawn(count, regionIndex, mode));
            return $"Generating up to {count} distinct dungeons (enemy mode={mode}) in {reader.GetRegionName(regionIndex)}. They remain until dungeongrid clear. See the Unity/player log for progress.";
        }

        IEnumerator Spawn(int requested, int regionIndex, string mode)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long memoryBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            int made = 0;
            try
            {
                var reader = DaggerfallUnity.Instance.ContentReader.MapFileReader;
                var region = reader.GetRegion(regionIndex);
                var random = new System.Random();
                var indices = Enumerable.Range(0, (int)region.LocationCount).OrderBy(i => random.Next()).ToArray();
                foreach (int index in indices)
                {
                    if (cancel || !NetworkServer.active || made >= requested) break;
                    bool created = false;
                    GameObject obj = null;
                    try
                    {
                        DFLocation location = reader.GetLocation(regionIndex, index);
                        if (location.Loaded && location.HasDungeon && !DaggerfallDungeon.IsMainStoryDungeon(location.MapTableData.MapId) &&
                            !FindObjectsOfType<DaggerfallDungeon>().Any(d => d.Summary.ID == location.MapTableData.MapId))
                        {
                            Vector3 origin = Allocate(location);
                            var prefab = NetworkManager.singleton.spawnPrefabs.First(p => p != null && p.GetComponent<DaggerfallDungeon>() != null);
                            obj = Instantiate(prefab);
                            obj.name = DaggerfallDungeon.GetSceneName(location);
                            var dungeon = obj.GetComponent<DaggerfallDungeon>();
                            dungeon.KeepForGridTest = true;
                            dungeon.SetGridPosition(origin);
                            dungeon.EnsureNetworkDungeonIdentity();
                            // Resolve the same exterior anchor as normal entry. Do not change the host's location.
                            int wx, wz; Vector3 entrance;
                            if (!StreamingWorld.TryGetDungeonEntranceWorldCoordinates(location, out wx, out wz, out entrance))
                                throw new InvalidOperationException("Cannot resolve dungeon entrance anchor.");
                            dungeon.SetDungeonWorldAnchor(wx, wz, "grid-test");
                            NetworkServer.Spawn(obj);
                            CreatingTestDungeon = dungeon;
                            CreatingPassiveTest = mode == "remote-test";
                            try { dungeon.GenerateDungeon(location, mode != "none", origin.y); }
                            finally { CreatingTestDungeon = null; CreatingPassiveTest = false; }
                            if (mode != "none") CaptureTestEnemies(dungeon, mode == "remote-test");
                            if (!dungeon.isSet) throw new InvalidOperationException("Dungeon generation did not finish.");
                            tests.Add(dungeon);
                            created = true;
                            made++;
                            Debug.Log($"[DungeonGridTest] {made}/{requested}: {location.Name} origin={origin} enemyMode={mode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[DungeonGridTest] " + ex);
                        if (obj != null && !created)
                        {
                            ClearTestEnemies(obj.GetComponent<DaggerfallDungeon>());
                            NetworkServer.Destroy(obj);
                        }
                    }
                    // Spread work across frames; each normal dungeon layout is still synchronous.
                    yield return null;
                    while (PendingTestEnemyBlocks > 0 && NetworkServer.active && !cancel)
                        yield return null;
                    // Repeat after paced imports have finished and their normal Start callbacks ran.
                    if (created && obj != null && mode != "none")
                        CaptureTestEnemies(obj.GetComponent<DaggerfallDungeon>(), mode == "remote-test");
                }
            }
            finally
            {
                running = false;
                long memoryAfter = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                Debug.Log($"[DungeonGridTest] Finished {made}/{requested} in {timer.Elapsed.TotalSeconds:F2}s. Unity allocated-memory delta={(memoryAfter-memoryBefore)/1048576.0:F1} MiB (includes other concurrent game activity). " + Report());
            }
        }

        void CaptureTestEnemies(DaggerfallDungeon dungeon, bool passive)
        {
            if (dungeon == null) return;
            HashSet<GameObject> enemies;
            if (!testEnemies.TryGetValue(dungeon, out enemies))
                testEnemies[dungeon] = enemies = new HashSet<GameObject>();
            foreach (var enemy in dungeon.GetComponentsInChildren<DaggerfallEnemy>(true))
                if (enemy.GetComponent<PlayerMultiplayer>() == null) enemies.Add(enemy.gameObject);
            // Native network enemies can be de-parented; use the stamped instance ID.
            foreach (var id in NetworkServer.spawned.Values)
            {
                if (id == null || id.GetComponent<PlayerMultiplayer>() != null || id.GetComponent<DaggerfallEnemy>() == null) continue;
                var world = id.GetComponent<EnemyWorldPosition>();
                if (world != null && !string.IsNullOrEmpty(dungeon.DungeonInstanceId) &&
                    world.PresentationDungeonInstanceId == dungeon.DungeonInstanceId) enemies.Add(id.gameObject);
            }
            foreach (var enemy in enemies)
            {
                if (enemy == null) continue;
                var authority = enemy.GetComponent<DynamicEnemyAuthority>();
                if (authority != null) authority.ConfigureDungeonGridTest(passive);
                // Leave native components enabled. Their normal simulator checks and
                // existing local presentation culling handle the observer workload.
            }
        }

        void ClearTestEnemies(DaggerfallDungeon dungeon)
        {
            // Refresh the list using identity stamps in case an imported actor was de-parented later.
            CaptureTestEnemies(dungeon, false);
            HashSet<GameObject> enemies;
            if (!testEnemies.TryGetValue(dungeon, out enemies)) return;
            foreach (var enemy in enemies)
                if (enemy != null) NetworkServer.Destroy(enemy);
            testEnemies.Remove(dungeon);
        }

        // Opt-in diagnostic only. Never applied to ordinary or actively owned enemies.
        readonly List<Behaviour> probeDisabledScripts = new List<Behaviour>();
        readonly List<Collider> probeDisabledColliders = new List<Collider>();
        readonly List<Rigidbody> probeDisabledBodies = new List<Rigidbody>();
        readonly List<GameObject> probeEnemies = new List<GameObject>();
        readonly List<float> probeSamples = new List<float>(8192);
        string probeMode = "off";
        string probeLastResult = "No measurement yet.";
        bool probeMeasuring;
        float probeWarmupUntil, probeMeasureUntil, probeNextSafetyCheck;
        int probeIdentityCount, probeSkippedFrames;

        static bool ProbeHasRemoteConnections()
        {
            foreach (var connection in NetworkServer.connections.Values)
                if (connection != null && connection != NetworkServer.localConnection)
                    return true;
            return false;
        }

        bool ProbeHasOccupiedDungeon()
        {
            var players = FindObjectsOfType<global::PlayerMultiplayer>();
            foreach (var dungeon in tests)
                if (dungeon != null)
                    foreach (var player in players)
                        if (player != null && Contains(dungeon, player.transform.position)) return true;
            return false;
        }

        string ProbeCommand(string[] args)
        {
            if (args.Length < 2)
                return "Usage: dungeongrid bench baseline|scripts|physics [seconds] | restore | status";
            string mode = args[1].ToLowerInvariant();
            if (mode == "status") return "Probe mode=" + probeMode +
                ", measuring=" + probeMeasuring + ". " + probeLastResult;
            if (mode == "restore")
            {
                RestoreProbe();
                return "Restored only component/collision flags changed by the probe. Test enemies remain spawned.";
            }
            if (mode != "baseline" && mode != "scripts" && mode != "physics")
                return "Probe modes: baseline, scripts, physics, restore, status.";
            int seconds = 20;
            if (args.Length > 2 && (!int.TryParse(args[2], out seconds) || seconds < 5 || seconds > 120))
                return "Measurement duration must be 5..120 seconds.";
            if (running) return "Wait for dungeon generation to finish before measuring.";
            if (ProbeHasRemoteConnections()) return "Diagnostic requires a solo host/server, without remote connections.";
            if (ProbeHasOccupiedDungeon()) return "Leave all retained test dungeons before measuring.";
            RestoreProbe();
            var unique = new HashSet<GameObject>();
            foreach (var group in testEnemies.Values)
                foreach (var enemy in group)
                {
                    if (enemy == null || !unique.Add(enemy)) continue;
                    var authority = enemy.GetComponent<DynamicEnemyAuthority>();
                    var identity = enemy.GetComponent<NetworkIdentity>();
                    NetworkIdentity spawnedIdentity;
                    if (authority == null || !authority.KeepForDungeonGridTest || !authority.PassiveDungeonGridTest ||
                        identity == null || identity.netId == 0 ||
                        !NetworkServer.spawned.TryGetValue(identity.netId, out spawnedIdentity) || spawnedIdentity != identity)
                        continue;
                    probeEnemies.Add(enemy);
                }
            if (probeEnemies.Count == 0) return "No retained remote-test enemies found. Spawn with remote-test first.";
            try
            {
                foreach (var enemy in probeEnemies)
                {
                    var rootIdentity = enemy.GetComponent<NetworkIdentity>();
                    if (mode != "baseline")
                        foreach (var component in enemy.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (component == null || !component.enabled ||
                                component.GetComponentInParent<NetworkIdentity>() != rootIdentity) continue;
                            // Exact types only: do not suspend arbitrary mod subclasses.
                            Type type = component.GetType();
                            if (type != typeof(Game.SetupDemoEnemy) &&
                                type != typeof(Game.Entity.DaggerfallEntityBehaviour) &&
                                type != typeof(Game.MagicAndEffects.EntityEffectManager) &&
                                type != typeof(DaggerfallAudioSource) && type != typeof(Game.EnemySounds)) continue;
                            probeDisabledScripts.Add(component);
                            component.enabled = false;
                        }
                    if (mode == "physics")
                    {
                        foreach (var collider in enemy.GetComponentsInChildren<Collider>(true))
                        {
                            if (collider == null || !collider.enabled ||
                                collider.GetComponentInParent<NetworkIdentity>() != rootIdentity) continue;
                            probeDisabledColliders.Add(collider);
                            collider.enabled = false;
                        }
                        foreach (var body in enemy.GetComponentsInChildren<Rigidbody>(true))
                        {
                            if (body == null || !body.detectCollisions ||
                                body.GetComponentInParent<NetworkIdentity>() != rootIdentity) continue;
                            probeDisabledBodies.Add(body);
                            body.detectCollisions = false;
                        }
                    }
                }
                probeMode = mode;
                probeIdentityCount = NetworkServer.spawned.Count;
                probeSamples.Clear();
                probeSkippedFrames = 0;
                probeWarmupUntil = Time.realtimeSinceStartup + 5f;
                probeMeasureUntil = probeWarmupUntil + seconds;
                probeNextSafetyCheck = 0f;
                probeMeasuring = true;
                string message = "[DungeonCostProbe] " + mode + ": enemies=" + probeEnemies.Count +
                    ", suspended components=" + probeDisabledScripts.Count +
                    ", colliders=" + probeDisabledColliders.Count + ", bodies=" + probeDisabledBodies.Count +
                    ". Close console now; 5s warmup, then " + seconds + "s measurement. " +
                    "Network identities and GameObjects remain. Use bench status for results; bench restore to restore.";
                Debug.Log(message);
                return message;
            }
            catch (Exception exception)
            {
                RestoreProbe();
                Debug.LogException(exception);
                return "Probe setup failed and changed flags were restored. See log.";
            }
        }

        void RestoreProbe()
        {
            probeMeasuring = false;
            // Reverse the applied collision flags before resuming script callbacks.
            foreach (var body in probeDisabledBodies) if (body != null) body.detectCollisions = true;
            foreach (var collider in probeDisabledColliders) if (collider != null) collider.enabled = true;
            foreach (var component in probeDisabledScripts) if (component != null) component.enabled = true;
            probeDisabledBodies.Clear();
            probeDisabledColliders.Clear();
            probeDisabledScripts.Clear();
            probeEnemies.Clear();
            probeMode = "off";
        }

        void Update()
        {
            UpdateObjectProbe();
            if (probeMode == "off") return;
            float now = Time.realtimeSinceStartup;
            if (now >= probeNextSafetyCheck)
            {
                probeNextSafetyCheck = now + 0.5f;
                if (!NetworkServer.active || ProbeHasRemoteConnections() || ProbeHasOccupiedDungeon() ||
                    Game.Serialization.SaveLoadManager.Instance.LoadInProgress)
                {
                    probeLastResult = "Measurement cancelled: session/loading/occupancy changed; flags restored.";
                    RestoreProbe();
                    Debug.LogWarning("[DungeonCostProbe] " + probeLastResult);
                    return;
                }
            }
            if (!probeMeasuring || now < probeWarmupUntil) return;
            // Paused/unfocused frames are not useful gameplay performance samples.
            if (Time.timeScale <= 0f || (!Application.isBatchMode && !Application.isFocused))
                probeSkippedFrames++;
            else if (probeSamples.Count < 100000)
                probeSamples.Add(Time.unscaledDeltaTime * 1000f);
            if (now >= probeMeasureUntil) FinishProbe();
        }

        void FinishProbe()
        {
            probeMeasuring = false;
            int alteredFlags = 0, missingEnemies = 0;
            foreach (var script in probeDisabledScripts) if (script != null && script.enabled) alteredFlags++;
            foreach (var collider in probeDisabledColliders) if (collider != null && collider.enabled) alteredFlags++;
            foreach (var body in probeDisabledBodies) if (body != null && body.detectCollisions) alteredFlags++;
            foreach (var enemy in probeEnemies)
            {
                if (enemy == null) { missingEnemies++; continue; }
                var identity = enemy.GetComponent<NetworkIdentity>();
                NetworkIdentity live;
                if (identity == null || !NetworkServer.spawned.TryGetValue(identity.netId, out live) || live != identity)
                    missingEnemies++;
            }
            bool sameIdentityCount = NetworkServer.spawned.Count == probeIdentityCount;
            if (probeSamples.Count == 0)
                probeLastResult = "No usable frames. Keep the game focused and unpaused during measurement.";
            else
            {
                double total = 0;
                foreach (float sample in probeSamples) total += sample;
                probeSamples.Sort();
                double mean = total / probeSamples.Count;
                float p95 = probeSamples[Mathf.Clamp(Mathf.CeilToInt(probeSamples.Count * 0.95f) - 1, 0, probeSamples.Count - 1)];
                probeLastResult = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "mode={0}; enemies={1}; frames={2}; mean={3:F2}ms; FPS={4:F1}; p95={5:F2}ms; max={6:F2}ms; " +
                    "skipped={7}; reenabledFlags={8}; missingEnemies={9}; networkCountUnchanged={10}",
                    probeMode, probeEnemies.Count, probeSamples.Count, mean, mean > 0 ? 1000.0 / mean : 0,
                    p95, probeSamples[probeSamples.Count - 1], probeSkippedFrames, alteredFlags, missingEnemies, sameIdentityCount);
                if (alteredFlags != 0 || missingEnemies != 0 || !sameIdentityCount)
                    probeLastResult += "; INVALID COMPARISON: population or suspension changed.";
            }
            Debug.Log("[DungeonCostProbe] " + probeLastResult);
            // Keep this mode applied for visual FPS inspection until the next command/restore.
        }

        void OnDisable() { objectProbeCancel = true; ClearObjectProbe(); RestoreProbe(); }
        void OnDestroy() { objectProbeCancel = true; ClearObjectProbe(); RestoreProbe(); }

        // Synthetic host benchmark. No rendering, colliders, or DFU enemy components.
        readonly List<GameObject> objectProbeObjects = new List<GameObject>();
        readonly List<NetworkIdentity> objectProbeIdentities = new List<NetworkIdentity>();
        readonly List<float> objectProbeSamples = new List<float>(16384);
        static readonly Guid objectProbeAssetId = new Guid("08bfad37-c390-4f09-9895-65fd6fc8ea38");
        bool objectProbeBusy, objectProbeCancel, objectProbeMeasuring;
        float objectProbeWarmup, objectProbeDeadline, objectProbeNextSafety;
        int objectProbeStartNetworkCount, objectProbeStartDungeonCount, objectProbeSkipped;
        string objectProbeKind = "none", objectProbeMeasuredKind, objectProbeLast = "No object benchmark result yet.";
        string objectProbeTransformSettings = "none";
        readonly List<NetworkIdentity> objectProbeMeasuredEnemies = new List<NetworkIdentity>();

        List<GameObject> ObjectProbeRetainedEnemies()
        {
            var result = new HashSet<GameObject>();
            foreach (var group in testEnemies.Values)
                foreach (var enemy in group)
                    if (enemy != null) result.Add(enemy);
            return result.ToList();
        }

        int ObjectProbeDungeonCount()
        {
            int count = 0;
            foreach (var dungeon in tests) if (dungeon != null) count++;
            return count;
        }

        string ObjectProbeCommand(string[] args)
        {
            const string usage = "dungeongrid objects measure [5..120 seconds] | spawn empty|identity|transform <1..10000> | strip | clear | status";
            if (args.Length < 2) return usage;
            string action = args[1].ToLowerInvariant();
            if (action == "status") return "Object mode=" + objectProbeKind + "; objects=" + objectProbeObjects.Count +
                "; spawning=" + objectProbeBusy + "; measuring=" + objectProbeMeasuring + ". " + objectProbeLast;
            if (action == "clear")
            {
                objectProbeCancel = true;
                ClearObjectProbe();
                return "Synthetic objects cleared; dungeons and real enemies retained.";
            }
            if (ProbeHasRemoteConnections()) return "Object benchmark requires a solo host/server. Do not connect clients during the test.";
            if (running || objectProbeBusy) return "Wait until the active spawn operation finishes, or clear it.";
            if (probeMode != "off") return "Run dungeongrid bench restore first. Object comparisons require normal callbacks.";
            if (action == "strip")
            {
                if (objectProbeObjects.Count != 0) return "Clear synthetic objects first.";
                if (ProbeHasOccupiedDungeon()) return "Leave the retained test dungeons first.";
                var enemies = ObjectProbeRetainedEnemies();
                // Validate the whole set BEFORE removing any objects.
                foreach (var enemy in enemies)
                {
                    var authority = enemy.GetComponent<DynamicEnemyAuthority>();
                    var identity = enemy.GetComponent<NetworkIdentity>();
                    NetworkIdentity live;
                    if (authority == null || !authority.KeepForDungeonGridTest || !authority.PassiveDungeonGridTest ||
                        identity == null || identity.netId == 0 ||
                        !NetworkServer.spawned.TryGetValue(identity.netId, out live) || live != identity)
                        return "Strip refused: a retained enemy is not a spawned passive remote-test enemy. Nothing removed.";
                }
                objectProbeMeasuring = false;
                foreach (var enemy in enemies) NetworkServer.Destroy(enemy);
                foreach (var group in testEnemies.Values) group.Clear();
                return "Removed " + enemies.Count + " disposable remote-test enemies. Dungeons/treasures retained. " +
                    "This cannot restore those enemies; restart/recreate the test for another full-enemy baseline. Wait 5 seconds before measuring.";
            }
            if (action == "spawn")
            {
                int count;
                if (args.Length != 4 || !int.TryParse(args[3], out count) || count < 1 || count > 10000) return usage;
                string kind = args[2].ToLowerInvariant();
                if (kind != "empty" && kind != "identity" && kind != "transform") return usage;
                if (ObjectProbeRetainedEnemies().Count != 0)
                    return "Measure full enemies first, then use dungeongrid objects strip to retain only the test dungeons.";
                ClearObjectProbe();
                objectProbeKind = kind;
                objectProbeCancel = false;
                objectProbeBusy = true;
                StartCoroutine(SpawnObjectProbe(kind, count));
                return "Replacing the synthetic population with " + count + " " + kind + " objects. Wait for objects status to show spawning=False.";
            }
            if (action == "measure")
            {
                int seconds = 20;
                if (args.Length > 2 && (!int.TryParse(args[2], out seconds) || seconds < 5 || seconds > 120)) return usage;
                var enemies = ObjectProbeRetainedEnemies();
                if (enemies.Count != 0 && objectProbeObjects.Count != 0) return "Mixed population: clear synthetic objects first.";
                objectProbeMeasuredEnemies.Clear();
                foreach (var enemy in enemies)
                {
                    var authority = enemy.GetComponent<DynamicEnemyAuthority>();
                    var identity = enemy.GetComponent<NetworkIdentity>();
                    if (authority == null || !authority.KeepForDungeonGridTest || !authority.PassiveDungeonGridTest || identity == null)
                        return "Full-enemy measurements require passive remote-test enemies.";
                    objectProbeMeasuredEnemies.Add(identity);
                }
                if (ProbeHasOccupiedDungeon()) return "Leave the test dungeons before measuring.";
                objectProbeMeasuredKind = enemies.Count != 0 ? "full-remote-test" : objectProbeObjects.Count != 0 ? objectProbeKind : "background";
                objectProbeSamples.Clear(); objectProbeSkipped = 0;
                objectProbeStartNetworkCount = NetworkServer.spawned.Count;
                objectProbeStartDungeonCount = ObjectProbeDungeonCount();
                objectProbeWarmup = Time.realtimeSinceStartup + 5f;
                objectProbeDeadline = objectProbeWarmup + seconds;
                objectProbeMeasuring = true;
                objectProbeNextSafety = 0f;
                return "Measuring " + objectProbeMeasuredKind + ": close console, keep focused/still. 5s warmup + " + seconds + "s. Then objects status.";
            }
            return usage;
        }

        NetworkTransform ObjectProbeTemplateTransform()
        {
            if (NetworkManager.singleton == null) return null;
            foreach (var prefab in NetworkManager.singleton.spawnPrefabs)
            {
                if (prefab == null || prefab.GetComponent<DaggerfallEnemy>() == null) continue;
                var source = prefab.GetComponent<NetworkTransform>();
                if (source != null) return source;
            }
            return null;
        }

        void ConfigureObjectProbeTransform(NetworkTransform target, NetworkTransform source)
        {
            if (source != null)
            {
                target.clientAuthority = source.clientAuthority;
                target.sendInterval = source.sendInterval;
                target.syncPosition = source.syncPosition; target.syncRotation = source.syncRotation; target.syncScale = source.syncScale;
                target.interpolatePosition = source.interpolatePosition; target.interpolateRotation = source.interpolateRotation;
                target.interpolateScale = source.interpolateScale;
                target.bufferTimeMultiplier = source.bufferTimeMultiplier; target.bufferSizeLimit = source.bufferSizeLimit;
                target.catchupThreshold = source.catchupThreshold; target.catchupMultiplier = source.catchupMultiplier;
                target.onlySyncOnChange = source.onlySyncOnChange; target.bufferResetMultiplier = source.bufferResetMultiplier;
                target.positionSensitivity = source.positionSensitivity; target.rotationSensitivity = source.rotationSensitivity;
                target.scaleSensitivity = source.scaleSensitivity;
            }
            target.showOverlay = false; target.showGizmos = false;
            objectProbeTransformSettings = (source != null ? "enemy-prefab" : "component-defaults") +
                ", clientAuthority=" + target.clientAuthority + ", sendInterval=" + target.sendInterval +
                ", onlySyncOnChange=" + target.onlySyncOnChange;
        }

        IEnumerator SpawnObjectProbe(string kind, int count)
        {
            // Allow deferred destruction of the preceding population to finish.
            yield return null;
            NetworkTransform source = kind == "transform" ? ObjectProbeTemplateTransform() : null;
            objectProbeTransformSettings = "none";
            bool failed = false;
            for (int i = 0; i < count && !objectProbeCancel && NetworkServer.active; i++)
            {
                if (ProbeHasRemoteConnections()) { failed = true; break; }
                try
                {
                    var go = new GameObject("GridObjectProbe");
                    objectProbeObjects.Add(go);
                    // Assemble all components before Awake caches Mirror's behaviour list.
                    go.SetActive(false);
                    go.transform.position = new Vector3(i % 100, -4000f, i / 100);
                    NetworkIdentity identity = null;
                    if (kind != "empty")
                    {
                        identity = go.AddComponent<NetworkIdentity>();
                        objectProbeIdentities.Add(identity);
                        identity.visible = Visibility.ForceShown;
                        if (kind == "transform") ConfigureObjectProbeTransform(go.AddComponent<NetworkTransform>(), source);
                    }
                    go.SetActive(true);
                    if (identity != null)
                    {
                        int expected = kind == "transform" ? 1 : 0;
                        if (identity.NetworkBehaviours == null || identity.NetworkBehaviours.Length != expected)
                            throw new InvalidOperationException("Mirror behaviour initialization mismatch; no benchmark will run with an invalid object.");
                        NetworkServer.Spawn(go, objectProbeAssetId);
                        NetworkIdentity live;
                        if (identity.netId == 0 || !NetworkServer.spawned.TryGetValue(identity.netId, out live) || live != identity)
                            throw new InvalidOperationException("Synthetic identity did not enter NetworkServer.spawned.");
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception); failed = true; break;
                }
                if ((i + 1) % 100 == 0) yield return null;
            }
            objectProbeBusy = false;
            if (failed || objectProbeCancel || !NetworkServer.active)
            {
                ClearObjectProbe();
                Debug.LogWarning("[ObjectCostProbe] Spawn cancelled/failed; synthetic objects cleared.");
            }
            else Debug.Log("[ObjectCostProbe] Ready: " + kind + " count=" + objectProbeObjects.Count +
                "; NetworkTransform=" + objectProbeTransformSettings + ". Run dungeongrid objects measure 20.");
        }

        void ClearObjectProbe()
        {
            objectProbeMeasuring = false;
            foreach (var go in objectProbeObjects)
            {
                if (go == null) continue;
                var identity = go.GetComponent<NetworkIdentity>();
                NetworkIdentity live;
                if (NetworkServer.active && identity != null && identity.netId != 0 &&
                    NetworkServer.spawned.TryGetValue(identity.netId, out live) && live == identity)
                    NetworkServer.Destroy(go);
                else Destroy(go);
            }
            objectProbeObjects.Clear(); objectProbeIdentities.Clear(); objectProbeKind = "none";
        }

        void UpdateObjectProbe()
        {
            if (!objectProbeMeasuring && objectProbeObjects.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now >= objectProbeNextSafety)
            {
                objectProbeNextSafety = now + 0.5f;
                // No global searches or dungeon-bound rebuilding during sampling.
                if (!NetworkServer.active || ProbeHasRemoteConnections() || Game.Serialization.SaveLoadManager.Instance.LoadInProgress)
                {
                    objectProbeLast = "Cancelled: server/connection/load state changed.";
                    objectProbeCancel = true;
                    ClearObjectProbe();
                    return;
                }
            }
            if (!objectProbeMeasuring || now < objectProbeWarmup) return;
            if (Time.timeScale <= 0 || (!Application.isBatchMode && !Application.isFocused)) objectProbeSkipped++;
            else if (objectProbeSamples.Count < 100000) objectProbeSamples.Add(Time.unscaledDeltaTime * 1000f);
            if (now >= objectProbeDeadline) FinishObjectProbe();
        }

        void FinishObjectProbe()
        {
            // Validation and sorting occur after the last captured frame duration.
            objectProbeMeasuring = false;
            int missing = 0, unobserved = 0;
            foreach (var go in objectProbeObjects) if (go == null || !go.activeInHierarchy) missing++;
            foreach (var identity in objectProbeIdentities.Concat(objectProbeMeasuredEnemies))
            {
                NetworkIdentity live;
                if (identity == null || !NetworkServer.spawned.TryGetValue(identity.netId, out live) || live != identity) missing++;
                else if (NetworkServer.localConnection != null &&
                    (identity.observers == null || !identity.observers.ContainsKey(NetworkServer.localConnection.connectionId))) unobserved++;
            }
            bool valid = missing == 0 && NetworkServer.spawned.Count == objectProbeStartNetworkCount &&
                ObjectProbeDungeonCount() == objectProbeStartDungeonCount && !ProbeHasOccupiedDungeon() &&
                (objectProbeIdentities.Count == 0 || unobserved == 0);
            if (objectProbeSamples.Count == 0) { objectProbeLast = "No focused, unpaused samples; repeat."; return; }
            double total = 0;
            foreach (float sample in objectProbeSamples) total += sample;
            objectProbeSamples.Sort();
            double mean = total / objectProbeSamples.Count;
            float p95 = objectProbeSamples[Mathf.Clamp(Mathf.CeilToInt(objectProbeSamples.Count * 0.95f) - 1, 0, objectProbeSamples.Count - 1)];
            objectProbeLast = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "mode={0}; objects={1}; testEnemies={2}; dungeons={3}; networkTotal={4}; frames={5}; mean={6:F2}ms; FPS={7:F1}; " +
                "p95={8:F2}ms; max={9:F2}ms; skipped={10}; missing={11}; hostUnobserved={12}; valid={13}; NT={14}",
                objectProbeMeasuredKind, objectProbeObjects.Count, objectProbeMeasuredEnemies.Count, objectProbeStartDungeonCount,
                objectProbeStartNetworkCount, objectProbeSamples.Count, mean, mean > 0 ? 1000.0 / mean : 0,
                p95, objectProbeSamples[objectProbeSamples.Count - 1], objectProbeSkipped, missing, unobserved, valid,
                objectProbeTransformSettings);
            objectProbeLast += "; editor=" + Application.isEditor + "; development=" + Debug.isDebugBuild;
            Debug.Log("[ObjectCostProbe] " + objectProbeLast);
        }

        string Report()
        {
            var dungeons = FindObjectsOfType<DaggerfallDungeon>().Where(d => d.isSet).ToArray();
            int reservedOverlap = 0, geometryOverlap = 0;
            var actual = new Dictionary<DaggerfallDungeon, Bounds>();
            foreach (var dungeon in dungeons)
            {
                bool first = true; Bounds bounds = new Bounds();
                foreach (var collider in dungeon.GetComponentsInChildren<Collider>(true))
                {
                    if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger) continue;
                    if (collider.GetComponentInParent<DaggerfallEnemy>() != null) continue;
                    if (first) { bounds = collider.bounds; first = false; } else bounds.Encapsulate(collider.bounds);
                }
                if (!first) actual.Add(dungeon, bounds);
                Debug.Log($"[DungeonGridTest] '{dungeon.name}' origin={dungeon.GridPosition} retained={dungeon.KeepForGridTest} collisionBounds={(first ? "none" : bounds.ToString())}");
            }
            for (int i = 0; i < dungeons.Length; i++) for (int j = i + 1; j < dungeons.Length; j++)
            {
                if (LayoutBounds(dungeons[i].Summary.LocationData, dungeons[i].GridPosition).Intersects(LayoutBounds(dungeons[j].Summary.LocationData, dungeons[j].GridPosition))) reservedOverlap++;
                if (actual.ContainsKey(dungeons[i]) && actual.ContainsKey(dungeons[j]) && actual[dungeons[i]].Intersects(actual[dungeons[j]])) geometryOverlap++;
            }
            return $"Generated dungeons={dungeons.Length}, retained tests={tests.Count(d => d != null)}, spawning={running}, reserved-bounds overlaps={reservedOverlap}, collider-bounds overlaps={geometryOverlap}. Bounds overlap is conservative; details in log.";
        }
    }
}
