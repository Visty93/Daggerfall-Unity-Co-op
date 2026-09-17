// Standalone compatibility adapter for Visty93/Daggerfall-Unity-Co-op.
// Original adapter code; no World of Daggerfall / Location Loader source or assets.
// Install on host AND clients under Assets, outside an Editor folder.
// No prefab edits, mod source/assets, or Harmony patches. Uses two Mirror messages.
using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using Mirror;
using DaggerfallWorkshop.Utility;
using UnityEngine;

public struct WodEnemySpawnRequest : NetworkMessage
{
    public ulong marker;
    public int worldX, worldZ, enemyType, gender, team;
    public bool allied, hostile;
    public float relativeY, yaw;
    public Vector3 scale;
    public string location;
}

public struct WodEnemySpawnReply : NetworkMessage
{
    public ulong marker;
    public uint netId;
    public byte status; // 1 = spawned/existing, 2 = dead this session, 3 = rejected/retry
    public string reason;
}

public sealed class WorldOfDaggerfallMultiplayerCompatibility : MonoBehaviour
{
    const string Prefix = "[WODMP v10] ";
    const float RetentionMetres = 3000f;
    const float DfUnitsPerMetre = 40f;
    const string SerializerType = "LocationLoader.LocationEnemySerializer";
    static WorldOfDaggerfallMultiplayerCompatibility instance;
    readonly HashSet<GameObject> pending = new HashSet<GameObject>();
    readonly List<DetachedEnemy> detached = new List<DetachedEnemy>();
    readonly List<GameObject> suppressed = new List<GameObject>();
    readonly HashSet<int> warned = new HashSet<int>();
    float nextScan;
    readonly Dictionary<uint, MotionProbe> motionProbes = new Dictionary<uint, MotionProbe>();
    sealed class MotionProbe
    {
        public Vector3 Position;
        public float Next;
        public int IdleSamples;
        public int CombatSamples;
    }
    bool wasMultiplayer;
    const float ActivationMetres = 100f;
    const float ServerRequestLimitMetres = 125f;
    readonly List<ClientMarker> clientMarkers = new List<ClientMarker>();
    readonly List<HostMarker> hostMarkers = new List<HostMarker>();
    readonly HashSet<int> startupPreserved = new HashSet<int>();

    sealed class HostMarker
    {
        public GameObject Enemy;
        public int WorldX, WorldZ;
    }
    readonly Dictionary<ulong, ServerMarker> serverMarkers = new Dictionary<ulong, ServerMarker>();
    readonly Dictionary<int, RequestBudget> requestBudgets = new Dictionary<int, RequestBudget>();

    sealed class ClientMarker
    {
        public GameObject Source;
        public WodEnemySpawnRequest Request;
        public float NextRequest;
        public bool Acknowledged;
        public bool PlacementStarted;
        public int Attempts;
    }
    sealed class ServerMarker
    {
        public GameObject Enemy;
        public EnemyEntity Entity;
        public bool Dead;
    }
    sealed class RequestBudget
    {
        public float Started;
        public int Count;
    }

    sealed class DetachedEnemy
    {
        public GameObject Enemy;
        public Transform Parent;
        public Component Serializer;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("WorldOfDaggerfallMultiplayerCompatibility");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<WorldOfDaggerfallMultiplayerCompatibility>();
    }

    void OnEnable()
    {
        GameManager.OnEnemySpawn += OnEnemySpawn;
        // Register before networking starts: cleanup can run before Mirror.active.
        GameObjectHelper.PreserveActorAtMultiplayerStart += PreserveWodAtStartup;
    }

    void OnDisable()
    {
        GameManager.OnEnemySpawn -= OnEnemySpawn;
        GameObjectHelper.PreserveActorAtMultiplayerStart -= PreserveWodAtStartup;
    }

    bool PreserveWodAtStartup(GameObject actor)
    {
        // This hook is consulted ONLY by MP startup cleanup. It neither changes SP
        // cleanup nor exempts general enemies from the normal MP lifetime rules.
        if (actor == null || actor.GetComponent<SetupDemoEnemy>() == null || FindWodParent(actor) == null)
            return false;
        if (startupPreserved.Add(actor.GetInstanceID()))
            Debug.Log(Prefix + "Preserved WOD enemy during host/connect cleanup: " + actor.name);
        return true;
    }

    static bool MultiplayerActive
    {
        get { return NetworkServer.active || NetworkClient.active; }
    }

    void Update()
    {
        bool active = MultiplayerActive;
        if (!active)
        {
            if (wasMultiplayer) RestoreLocalObjects();
            wasMultiplayer = false;
            return;
        }
        wasMultiplayer = true;
        MaintainDetachedEnemies();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        // Mirror clears handlers on disconnect. Reinstall while a session is active.
        if (NetworkServer.active)
            NetworkServer.ReplaceHandler<WodEnemySpawnRequest>(ReceiveSpawnRequest);
        if (NetworkClient.active)
            NetworkClient.ReplaceHandler<WodEnemySpawnReply>(ReceiveSpawnReply);
        WakeNearbyHostMarkers();
        SendNearbyRequests();
        RefreshWodClientEntityReferences();
        DiagnoseNearbyClientSpawns();

        // Covers enemies created before the network session started, and re-enabled
        // locations. The normal creation path also uses the event below.
        foreach (SetupDemoEnemy setup in FindObjectsOfType<SetupDemoEnemy>())
            Queue(setup.gameObject);
        suppressed.RemoveAll(go => go == null);
    }

    void OnEnemySpawn(GameObject enemy)
    {
        if (MultiplayerActive) Queue(enemy);
    }

    static Transform FindWodParent(GameObject enemy)
    {
        if (enemy == null) return null;
        for (Transform parent = enemy.transform.parent; parent != null; parent = parent.parent)
            if (parent.name.IndexOf("WOD_", StringComparison.OrdinalIgnoreCase) >= 0)
                return parent;
        return null;
    }

    static Component FindSerializer(GameObject enemy)
    {
        foreach (Component component in enemy.GetComponents<Component>())
            if (component != null && (component.GetType().FullName == SerializerType || component.GetType().Name == "LocationEnemySerializer"))
                return component;
        return null;
    }

    void Queue(GameObject enemy)
    {
        if (enemy == null || !enemy.activeInHierarchy || FindWodParent(enemy) == null)
            return;
        NetworkIdentity identity = enemy.GetComponent<NetworkIdentity>();
        if (identity != null && identity.netId != 0) return;
        foreach (HostMarker marker in hostMarkers)
            if (marker.Enemy == enemy) return;
        if (pending.Add(enemy)) StartCoroutine(Prepare(enemy));
    }

    IEnumerator Prepare(GameObject enemy)
    {
        // CreateEnemy's event precedes Location Loader's LoadID, team and serializer
        // assignment. Also allow terrain placement and save restoration to finish.
        for (int frame = 0; frame < 10; frame++) yield return null;
        pending.Remove(enemy);
        if (!MultiplayerActive || enemy == null || !enemy.activeInHierarchy) yield break;
        // Capture coordinates only in the loaded exterior frame. An interior's
        // PlayerObject pose cannot be used to locate exterior camp markers.
        if (GameManager.Instance != null && GameManager.Instance.PlayerEnterExit != null &&
            GameManager.Instance.PlayerEnterExit.IsPlayerInside) yield break;
        Transform location = FindWodParent(enemy);
        Component serializer = FindSerializer(enemy);
        if (location == null) yield break;
        // Do not depend on a particular serializer namespace or on its presence
        // on pure clients. A WOD ancestor + SetupDemoEnemy is enough.
        if (enemy.GetComponent<SetupDemoEnemy>() == null) yield break;

        NetworkIdentity identity = enemy.GetComponent<NetworkIdentity>();
        if (identity != null && identity.netId != 0) yield break;
        DaggerfallEntityBehaviour behaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
        if (entity == null)
        {
            if (warned.Add(enemy.GetInstanceID()))
                Debug.LogWarning(Prefix + "Waiting for local WOD entity setup: " + enemy.name);
            yield break;
        }

        if (!NetworkServer.active)
        {
            CaptureClientMarker(enemy, location, serializer, entity);
            yield break;
        }
        if (entity.CurrentHealth <= 0) yield break;

        SetupDemoEnemy setup = enemy.GetComponent<SetupDemoEnemy>();
        if (identity == null || identity.assetId == Guid.Empty || setup == null)
        {
            if (warned.Add(enemy.GetInstanceID()))
                Debug.LogError(Prefix + "Cannot register '" + enemy.name + "': missing network enemy prefab identity/setup.");
            yield break;
        }

        global::DynamicEnemyAuthority authority = enemy.GetComponent<global::DynamicEnemyAuthority>();
        global::EnemyWorldPosition world = enemy.GetComponent<global::EnemyWorldPosition>();
        if (authority == null || world == null || GameManager.Instance == null ||
            GameManager.Instance.PlayerGPS == null || GameManager.Instance.PlayerObject == null)
        {
            if (warned.Add(enemy.GetInstanceID()))
                Debug.LogError(Prefix + "Waiting for enemy networking/world-coordinate components: " + enemy.name);
            yield break;
        }

        // Capture in the host's actual streamed terrain frame BEFORE detaching.
        // No modulo/nearest-tile correction: this location can be several tiles away.
        Vector3 offset = enemy.transform.position - GameManager.Instance.PlayerObject.transform.position;
        int worldX = GameManager.Instance.PlayerGPS.WorldX + Mathf.RoundToInt(offset.x * DfUnitsPerMetre);
        int worldZ = GameManager.Instance.PlayerGPS.WorldZ + Mathf.RoundToInt(offset.z * DfUnitsPerMetre);
        ulong marker = MarkerId(enemy, worldX, worldZ);
        ServerMarker existing;
        if (serverMarkers.TryGetValue(marker, out existing) && (existing.Enemy != null || existing.Dead))
        {
            SuppressLocalCopy(enemy, serializer);
            Debug.Log(Prefix + "Suppressed duplicate host marker " + marker);
            yield break;
        }
        NetworkConnection initialOwner = ClosestExteriorPlayerWithinActivation(worldX, worldZ);
        if (initialOwner == null)
        {
            hostMarkers.Add(new HostMarker { Enemy = enemy, WorldX = worldX, WorldZ = worldZ });
            // Keep the native object and serializer under the location until needed.
            // It has no netId yet, so ordinary network destruction does not apply.
            enemy.SetActive(false);
            Debug.Log(Prefix + "Host marker waiting within " + ActivationMetres + "m: " + enemy.name);
            yield break;
        }
        StampTeam(setup, entity.Team, setup.AlliedToPlayer || entity.Team == MobileTeams.PlayerAlly);
        authority.HorizontalRetentionDistanceDF = RetentionMetres * DfUnitsPerMetre;
        world.SetDistantExteriorCoordinates(worldX, worldZ);

        Transform originalParent = enemy.transform.parent;
        enemy.transform.SetParent(null, true);
        setup.isDungeonEnemy = false;
        setup.syncedInitialY = enemy.transform.position.y;
        // ServerCaptureAuthoritativeSpawnHealth requires isServer, which is false
        // until Spawn. Stamp the payload directly; OnStartServer captures it again.
        setup.SyncedSpawnHealth = entity.CurrentHealth;
        EnemyMotor motor = enemy.GetComponent<EnemyMotor>();
        if (motor != null)
        {
            setup.SyncedMotorIsHostile = motor.IsHostile;
            setup.SpawnedMotorIsHostile = motor.IsHostile;
            setup.CurrentMotorIsHostile = motor.IsHostile;
            setup.LastAppliedMotorIsHostile = motor.IsHostile;
        }

        try
        {
            authority.PreserveInitialExteriorOwner(initialOwner);
            NetworkServer.Spawn(enemy, initialOwner);
        }
        catch (Exception exception)
        {
            Debug.LogError(Prefix + "Spawn failed for '" + enemy.name + "': " + exception);
        }

        if (identity.netId == 0)
        {
            enemy.transform.SetParent(originalParent, true);
            if (warned.Add(enemy.GetInstanceID()))
                Debug.LogError(Prefix + "Spawn did not assign a netId to '" + enemy.name + "'. Check Mirror's preceding error.");
            yield break;
        }

        TrackServerMarker(marker, enemy);
        detached.Add(new DetachedEnemy { Enemy = enemy, Parent = originalParent, Serializer = serializer });
        world.ResumeRetainedActorNetworking();
        // Resume's ordinary initializer uses nearest-tile wrapping; restore the
        // full location coordinates before another frame/authority check can run.
        world.SetDistantExteriorCoordinates(worldX, worldZ);
        authority.ResumeRetainedActorNetworking();
        Debug.Log(Prefix + "Registered '" + enemy.name + "' from " + location.name +
            " at scene root; netId=" + identity.netId + ", position=" + enemy.transform.position +
            ", DF=" + worldX + "/" + worldZ + ", horizontal retention=" + RetentionMetres + "m (Y ignored)");
    }

    bool AnyExteriorPlayerWithinActivation(int worldX, int worldZ)
    {
        return ClosestExteriorPlayerWithinActivation(worldX, worldZ) != null;
    }

    NetworkConnection ClosestExteriorPlayerWithinActivation(int worldX, int worldZ)
    {
        NetworkConnection best = null;
        double bestDistanceSquared = ActivationMetres * ActivationMetres;
        foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
        {
            if (identity == null || identity.GetComponent<PlayerMultiplayer>() == null ||
                identity.connectionToClient == null || !identity.connectionToClient.isReady) continue;
            var position = identity.GetComponent<PositionMultiplayer>();
            if (position == null ||
                position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.BuildingInterior ||
                position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.DungeonInterior) continue;
            double dx = ((double)worldX - position.x) / DfUnitsPerMetre;
            double dz = ((double)worldZ - position.z) / DfUnitsPerMetre;
            double distanceSquared = dx * dx + dz * dz;
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = identity.connectionToClient;
            }
        }
        return best;
    }

    void WakeNearbyHostMarkers()
    {
        if (!NetworkServer.active) return;
        for (int i = hostMarkers.Count - 1; i >= 0; i--)
        {
            HostMarker marker = hostMarkers[i];
            if (marker.Enemy == null) { hostMarkers.RemoveAt(i); continue; }
            Transform parent = marker.Enemy.transform.parent;
            if (parent == null || !parent.gameObject.activeInHierarchy) continue;
            if (GameManager.Instance != null && GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInside) continue;
            if (!AnyExteriorPlayerWithinActivation(marker.WorldX, marker.WorldZ)) continue;
            hostMarkers.RemoveAt(i);
            marker.Enemy.SetActive(true);
            Queue(marker.Enemy);
        }
    }

    static void StampTeam(SetupDemoEnemy setup, MobileTeams team, bool allied)
    {
        setup.SpawnTeamOverride = (int)team;
        setup.AlliedToPlayer = allied || team == MobileTeams.PlayerAlly;
        setup.ApplySpawnTeamOverride();
    }

    static ulong MarkerId(GameObject enemy, int worldX, int worldZ)
    {
        var dfEnemy = enemy.GetComponent<DaggerfallEnemy>();
        if (dfEnemy != null && dfEnemy.LoadID != 0) return dfEnemy.LoadID;
        // Fallback for client loaders that omit the serializer/load ID. Stable for
        // retries from the same loaded marker; not a claim of cross-mod global IDs.
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            hash = (hash ^ (uint)(worldX / 40)) * 1099511628211UL;
            hash = (hash ^ (uint)(worldZ / 40)) * 1099511628211UL;
            hash = (hash ^ (uint)enemy.GetComponent<SetupDemoEnemy>().EnemyType) * 1099511628211UL;
            return hash == 0 ? 1UL : hash;
        }
    }

    void CaptureClientMarker(GameObject enemy, Transform location, Component serializer, EnemyEntity entity)
    {
        var game = GameManager.Instance;
        if (game == null || game.PlayerGPS == null || game.PlayerObject == null) return;
        var setup = enemy.GetComponent<SetupDemoEnemy>();
        Vector3 delta = enemy.transform.position - game.PlayerObject.transform.position;
        int x = game.PlayerGPS.WorldX + Mathf.RoundToInt(delta.x * DfUnitsPerMetre);
        int z = game.PlayerGPS.WorldZ + Mathf.RoundToInt(delta.z * DfUnitsPerMetre);
        ulong id = MarkerId(enemy, x, z);
        foreach (ClientMarker old in clientMarkers)
            if (old.Source == enemy) return;
        var request = new WodEnemySpawnRequest {
            marker = id, worldX = x, worldZ = z, enemyType = (int)setup.EnemyType,
            gender = (int)setup.EnemyGender, team = (int)entity.Team,
            allied = setup.AlliedToPlayer || entity.Team == MobileTeams.PlayerAlly,
            hostile = enemy.GetComponent<EnemyMotor>() != null && enemy.GetComponent<EnemyMotor>().IsHostile,
            relativeY = delta.y, yaw = enemy.transform.eulerAngles.y,
            scale = enemy.transform.lossyScale,
            location = location.name.Length <= 128 ? location.name : location.name.Substring(0, 128)
        };
        clientMarkers.Add(new ClientMarker { Source = enemy, Request = request });
        SuppressLocalCopy(enemy, serializer);
        Debug.Log(Prefix + "Captured client marker " + id + " '" + enemy.name +
            "', team=" + entity.Team + "; local copy suppressed, request when within " + ActivationMetres + "m.");
    }

    void SuppressLocalCopy(GameObject enemy, Component serializer)
    {
        // This is a redundant local marker, not a slain actor. Detach it from the
        // loader's save registry before disabling so zero-HP client setup cannot
        // incorrectly mark this camp occupant as killed.
        if (serializer != null)
        {
            var invalidate = serializer.GetType().GetMethod("InvalidateSave");
            if (invalidate != null) invalidate.Invoke(serializer, null);
        }
        if (!suppressed.Contains(enemy)) suppressed.Add(enemy);
        enemy.SetActive(false);
    }

    void SendNearbyRequests()
    {
        if (NetworkServer.active || !NetworkClient.ready) return;
        var game = GameManager.Instance;
        if (game == null || game.PlayerGPS == null || game.PlayerObject == null ||
            (game.PlayerEnterExit != null && game.PlayerEnterExit.IsPlayerInside)) return;
        int sent = 0;
        for (int i = clientMarkers.Count - 1; i >= 0; i--)
        {
            ClientMarker marker = clientMarkers[i];
            if (marker.Source == null) { clientMarkers.RemoveAt(i); continue; }
            // Location Loader sometimes recursively re-enables its location objects.
            if (marker.Source.activeSelf) marker.Source.SetActive(false);
            if (marker.Acknowledged || Time.unscaledTime < marker.NextRequest) continue;
            double dx = ((double)marker.Request.worldX - game.PlayerGPS.WorldX) / DfUnitsPerMetre;
            double dz = ((double)marker.Request.worldZ - game.PlayerGPS.WorldZ) / DfUnitsPerMetre;
            if (dx * dx + dz * dz > ActivationMetres * ActivationMetres) continue;
            marker.Request.relativeY = marker.Source.transform.position.y - game.PlayerObject.transform.position.y;
            NetworkClient.Send(marker.Request);
            marker.NextRequest = Time.unscaledTime + 5f;
            if (++marker.Attempts == 1 || marker.Attempts == 4)
                Debug.Log(Prefix + "Requested host spawn for marker " + marker.Request.marker +
                    " at DF=" + marker.Request.worldX + "/" + marker.Request.worldZ +
                    " (attempt " + marker.Attempts + ").");
            if (++sent >= 8) break;
        }
    }

    void ReceiveSpawnReply(WodEnemySpawnReply reply)
    {
        foreach (ClientMarker marker in clientMarkers)
        {
            if (marker.Request.marker != reply.marker) continue;
            if (reply.status == 1 || reply.status == 2) marker.Acknowledged = true;
            // Existing shared occupants may already have moved or fought. Never
            // pull them back to the original marker when another player arrives.
            if (reply.status == 1 && reply.reason == "spawned" && !marker.PlacementStarted)
            {
                marker.PlacementStarted = true;
                StartCoroutine(SettleNewClientSpawn(marker, reply.netId));
            }
            if (reply.status == 3)
            {
                marker.NextRequest = Time.unscaledTime + 10f;
                if (marker.Attempts <= 2)
                    Debug.LogWarning(Prefix + "Host deferred marker " + reply.marker + ": " + reply.reason);
            }
            else
                Debug.Log(Prefix + "Host acknowledged marker " + reply.marker + ", netId=" + reply.netId +
                    (reply.status == 2 ? " (already dead this session)." : " (" + reply.reason + ")."));
        }
    }

    IEnumerator SettleNewClientSpawn(ClientMarker marker, uint netId)
    {
        // The retained local marker follows its terrain through origin shifts.
        // Do not reuse the host's Unity coordinates or query the host's colliders.
        float deadline = Time.unscaledTime + 5f;
        int stableFrames = 0;
        Collider previousFloor = null;
        float previousFloorY = 0f;
        yield return null;
        yield return null;
        while (NetworkClient.active && !NetworkServer.active && Time.unscaledTime < deadline)
        {
            if (!clientMarkers.Contains(marker) || marker.Source == null || marker.Source.transform.parent == null ||
                !marker.Source.transform.parent.gameObject.activeInHierarchy) yield break;
            var game = GameManager.Instance;
            if (game == null || game.PlayerEnterExit == null || game.PlayerObject == null ||
                game.PlayerGPS == null || game.PlayerEnterExit.IsPlayerInside) yield break;
            NetworkIdentity identity;
            if (!NetworkClient.spawned.TryGetValue(netId, out identity) || identity == null)
            { yield return null; continue; }
            var setup = identity.GetComponent<SetupDemoEnemy>();
            var cc = identity.GetComponent<CharacterController>();
            var sourceCC = marker.Source.GetComponent<CharacterController>();
            var world = identity.GetComponent<EnemyWorldPosition>();
            if (world == null || !world.PreserveDistantExteriorCoordinates) yield break;
            if (!identity.hasAuthority || setup == null || !setup.HasReceivedInitialServerSettings() ||
                cc == null || !cc.enabled || sourceCC == null)
            { yield return null; continue; }
            var mobile = setup.GetMobileBillboardChild();
            if (mobile == null) { yield return null; continue; }
            if (mobile.Enemy.Behaviour == MobileBehaviour.Flying ||
                mobile.Enemy.Behaviour == MobileBehaviour.Aquatic) yield break;
            var senses = identity.GetComponent<EnemySenses>();
            var body = identity.GetComponent<DaggerfallEntityBehaviour>();
            if (body == null || body.Entity == null || body.Entity.CurrentHealth <= 0) yield break;
            if (senses != null && senses.Target != null)
            {
                Debug.Log(Prefix + "Placement skipped netId=" + netId + ": already in combat.");
                yield break;
            }
            Vector3 original = marker.Source.transform.position;
            Vector3 player = game.PlayerObject.transform.position;
            double dx = ((double)marker.Request.worldX - game.PlayerGPS.WorldX) / DfUnitsPerMetre;
            double dz = ((double)marker.Request.worldZ - game.PlayerGPS.WorldZ) / DfUnitsPerMetre;
            if (dx * dx + dz * dz > ServerRequestLimitMetres * ServerRequestLimitMetres) yield break;
            // Reject a marker in an obsolete terrain frame rather than teleporting
            // the actor to an unrelated tile with similar local coordinates.
            if (Math.Abs(original.x - player.x - dx) > 3 || Math.Abs(original.z - player.z - dz) > 3)
            { yield return null; continue; }
            float sourceFeet = marker.Source.transform.TransformPoint(sourceCC.center).y -
                sourceCC.height * Mathf.Abs(marker.Source.transform.lossyScale.y) * 0.5f;
            RaycastHit best = new RaycastHit();
            float bestScore = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(new Vector3(original.x, sourceFeet + 1.5f, original.z),
                Vector3.down, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.normal.y < 0.7f ||
                    hit.collider.GetComponentInParent<DaggerfallEntityBehaviour>() != null ||
                    hit.collider.GetComponentInParent<PlayerMultiplayer>() != null ||
                    hit.collider is CharacterController) continue;
                float score = Mathf.Abs(hit.point.y - sourceFeet);
                if (score < bestScore) { best = hit; bestScore = score; }
            }
            if (best.collider == null)
            { stableFrames = 0; yield return null; continue; }
            if (best.collider == previousFloor && Mathf.Abs(best.point.y - previousFloorY) < 0.02f)
                stableFrames++;
            else stableFrames = 1;
            previousFloor = best.collider;
            previousFloorY = best.point.y;
            if (stableFrames < 3) { yield return null; continue; }
            float footOffset = identity.transform.TransformPoint(cc.center).y - identity.transform.position.y -
                cc.height * Mathf.Abs(identity.transform.lossyScale.y) * 0.5f;
            Vector3 desired = new Vector3(original.x, best.point.y - footOffset + 0.03f, original.z);
            Vector3 before = identity.transform.position;
            // One synchronous relocation; normal owner NetworkTransform replication
            // and its existing startup pose pump distribute the result.
            cc.enabled = false;
            try { identity.transform.position = desired; }
            finally { if (cc != null) cc.enabled = true; }
            var motor = identity.GetComponent<EnemyMotor>();
            if (motor != null) motor.LastGroundedY = desired.y;
            world.NoteExternalSeamFrameCorrection();
            Debug.Log(Prefix + "Placement netId=" + netId + " before=" + before.ToString("F3") +
                " marker=" + original.ToString("F3") + " after=" + desired.ToString("F3") +
                " support=" + best.collider.name);
            yield break;
        }
        if (NetworkClient.active)
            Debug.LogWarning(Prefix + "Placement skipped netId=" + netId +
                ": no stable nearby floor/ready owner within startup window; pose unchanged.");
    }

    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

    void Reply(NetworkConnection connection, WodEnemySpawnRequest request, byte status, uint id, string reason)
    {
        connection.Send(new WodEnemySpawnReply { marker = request.marker, status = status, netId = id, reason = reason });
    }

    void ReceiveSpawnRequest(NetworkConnection connection, WodEnemySpawnRequest request)
    {
        if (!NetworkServer.active || connection == null || !connection.isReady || connection.identity == null) return;
        var player = connection.identity.GetComponent<PlayerMultiplayer>();
        var position = connection.identity.GetComponent<PositionMultiplayer>();
        if (player == null || position == null) return;
        RequestBudget budget;
        if (!requestBudgets.TryGetValue(connection.connectionId, out budget))
            requestBudgets[connection.connectionId] = budget = new RequestBudget();
        if (Time.unscaledTime - budget.Started >= 10f) { budget.Started = Time.unscaledTime; budget.Count = 0; }
        if (++budget.Count > 80) return;
        double dx = ((double)request.worldX - position.x) / DfUnitsPerMetre;
        double dz = ((double)request.worldZ - position.z) / DfUnitsPerMetre;
        bool invalid = request.marker == 0 || request.location == null || request.location.Length > 128 ||
            request.location.IndexOf("WOD_", StringComparison.OrdinalIgnoreCase) < 0 ||
            !GameObjectHelper.EnemyDict.ContainsKey(request.enemyType) ||
            !Enum.IsDefined(typeof(MobileGender), request.gender) || !Enum.IsDefined(typeof(MobileTeams), request.team) ||
            !Finite(request.relativeY) || Mathf.Abs(request.relativeY) > 200f || !Finite(request.yaw) ||
            !Finite(request.scale.x) || !Finite(request.scale.y) || !Finite(request.scale.z) ||
            request.scale.x < 0.1f || request.scale.y < 0.1f || request.scale.z < 0.1f ||
            request.scale.x > 10f || request.scale.y > 10f || request.scale.z > 10f ||
            dx * dx + dz * dz > ServerRequestLimitMetres * ServerRequestLimitMetres ||
            position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.BuildingInterior ||
            position.PartyCurrentLocationState == PositionMultiplayer.PartyLocationState.DungeonInterior;
        if (invalid) { Reply(connection, request, 3, 0, "invalid marker or requester not near the exterior camp"); return; }
        ServerMarker existing;
        if (serverMarkers.TryGetValue(request.marker, out existing))
        {
            if (existing.Entity != null && existing.Entity.CurrentHealth <= 0) existing.Dead = true;
            if (existing.Dead) { Reply(connection, request, 2, 0, "dead"); return; }
            if (existing.Enemy != null)
            {
                var identity = existing.Enemy.GetComponent<NetworkIdentity>();
                if (identity != null && identity.netId != 0) { Reply(connection, request, 1, identity.netId, "existing"); return; }
            }
        }
        SpawnForClient(connection, position, request, (float)dx, (float)dz);
    }

    void SpawnForClient(NetworkConnection connection, PositionMultiplayer playerPosition, WodEnemySpawnRequest request, float dx, float dz)
    {
        GameObject enemy = null;
        try
        {
            // Instantiate the registered enemy prefab directly. CreateEnemy's ground
            // raycast would use the HOST'S terrain, which may be kilometres away.
            var prefab = DaggerfallUnity.Instance.Option_EnemyPrefab;
            if (prefab == null) throw new InvalidOperationException("Enemy prefab unavailable");
            enemy = Instantiate(prefab.gameObject);
            enemy.name = "WOD remote " + ((MobileTypes)request.enemyType);
            enemy.transform.SetParent(null, true);
            Vector3 pose = playerPosition.transform.position + new Vector3(dx, request.relativeY, dz);
            enemy.transform.SetPositionAndRotation(pose, Quaternion.Euler(0, request.yaw, 0));
            enemy.transform.localScale = request.scale;
            var setup = enemy.GetComponent<SetupDemoEnemy>();
            var identity = enemy.GetComponent<NetworkIdentity>();
            var world = enemy.GetComponent<EnemyWorldPosition>();
            var authority = enemy.GetComponent<DynamicEnemyAuthority>();
            if (setup == null || identity == null || identity.assetId == Guid.Empty || world == null || authority == null)
                throw new InvalidOperationException("Registered prefab lacks multiplayer components");
            setup.SpawnTeamOverride = request.team;
            var requester = connection.identity.GetComponent<PlayerMultiplayer>();
            if (requester == null) throw new InvalidOperationException("Spawn requester unavailable");
            int spawnLevel = Mathf.Clamp(requester.PlayerMPLevel, 1, 100);
            setup.ApplyEnemySettingsWithScalingLevel((MobileTypes)request.enemyType,
                request.hostile ? MobileReactions.Hostile : MobileReactions.Passive,
                (MobileGender)request.gender, 0,
                request.allied || (MobileTeams)request.team == MobileTeams.PlayerAlly, spawnLevel);
            var behaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
            var entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (entity == null) throw new InvalidOperationException("Enemy entity did not initialize");
            entity.WorldContext = WorldContext.Exterior;
            StampTeam(setup, (MobileTeams)request.team, request.allied);
            setup.isDungeonEnemy = false;
            setup.syncedInitialY = pose.y;
            setup.SyncedSpawnHealth = entity.CurrentHealth;
            var motor = enemy.GetComponent<EnemyMotor>();
            if (motor != null) motor.IsHostile = request.hostile;
            setup.SyncedMotorIsHostile = setup.SpawnedMotorIsHostile =
                setup.CurrentMotorIsHostile = setup.LastAppliedMotorIsHostile = request.hostile;
            world.isInteriorSpawn = false;
            world.isDungeonSpawn = false;
            world.requesterNetId = connection.identity.netId;
            world.SetDistantExteriorCoordinates(request.worldX, request.worldZ);
            authority.HorizontalRetentionDistanceDF = RetentionMetres * DfUnitsPerMetre;
            GameManager.Instance.RaiseOnEnemySpawnEvent(enemy);
            authority.PreserveInitialExteriorOwner(connection);
            NetworkServer.Spawn(enemy, connection);
            if (identity.netId == 0) throw new InvalidOperationException("Mirror did not assign a netId");
            world.ResumeRetainedActorNetworking();
            world.SetDistantExteriorCoordinates(request.worldX, request.worldZ);
            authority.ResumeRetainedActorNetworking();
            TrackServerMarker(request.marker, enemy);
            Reply(connection, request, 1, identity.netId, "spawned");
            Debug.Log(Prefix + "Spawned client-requested marker " + request.marker + ", netId=" + identity.netId +
                ", requester=" + connection.identity.netId + ", scalingLevel=" + spawnLevel + ", team=" + entity.Team + ", allied=" + setup.AlliedToPlayer);
        }
        catch (Exception exception)
        {
            if (enemy != null)
            {
                var identity = enemy.GetComponent<NetworkIdentity>();
                if (identity != null && identity.netId != 0) NetworkServer.Destroy(enemy);
                else Destroy(enemy);
            }
            Debug.LogError(Prefix + "Remote spawn failed: " + exception);
            Reply(connection, request, 3, 0, "host spawn failed; inspect host log");
        }
    }

    void TrackServerMarker(ulong id, GameObject enemy)
    {
        var behaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
        serverMarkers[id] = new ServerMarker { Enemy = enemy, Entity = behaviour != null ? behaviour.Entity as EnemyEntity : null };
    }

    void MaintainDetachedEnemies()
    {
        // Root enemies belong to the MP lifetime policy, NOT to the host's terrain.
        // Keep their original serializer alive; it still tracks health/death saves.
        // Neither disabling nor destroying the old parent removes these enemies.
        detached.RemoveAll(entry => entry.Enemy == null);
        // Location Loader may recursively re-enable children after terrain updates.
        foreach (GameObject localCopy in suppressed)
            if (localCopy != null && localCopy.activeSelf) localCopy.SetActive(false);
        foreach (HostMarker marker in hostMarkers)
            if (marker.Enemy != null && marker.Enemy.activeSelf) marker.Enemy.SetActive(false);
        foreach (ServerMarker marker in serverMarkers.Values)
            if (marker.Entity != null && marker.Entity.CurrentHealth <= 0)
                marker.Dead = true;
    }

    static readonly System.Reflection.FieldInfo MotorEntityField = typeof(EnemyMotor).GetField(
        "entity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo SensesEntityField = typeof(EnemySenses).GetField(
        "enemyEntity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    bool warnedEntityFields;

    void RefreshWodClientEntityReferences()
    {
        // Spawn SyncVar fallback can create an entity before Start caches it. The
        // later full settings RPC replaces that entity. Refresh only initialized
        // WOD client copies; keep targets, allegiance, motor delegates and pose intact.
        if (!NetworkClient.active || NetworkServer.active) return;
        if (MotorEntityField == null || SensesEntityField == null ||
            MotorEntityField.FieldType != typeof(EnemyEntity) ||
            SensesEntityField.FieldType != typeof(EnemyEntity))
        {
            if (!warnedEntityFields)
                Debug.LogWarning(Prefix + "Cannot refresh WOD AI references: expected enemy fields unavailable.");
            warnedEntityFields = true;
            return;
        }
        foreach (NetworkIdentity identity in NetworkClient.spawned.Values)
        {
            if (identity == null) continue;
            var world = identity.GetComponent<EnemyWorldPosition>();
            if (world == null || !world.PreserveDistantExteriorCoordinates) continue;
            var setup = identity.GetComponent<SetupDemoEnemy>();
            if (setup == null || !setup.HasReceivedInitialServerSettings()) continue;
            var behaviour = identity.GetComponent<DaggerfallEntityBehaviour>();
            var live = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (live == null) continue;
            var motor = identity.GetComponent<EnemyMotor>();
            var senses = identity.GetComponent<EnemySenses>();
            bool motorChanged = RefreshCachedEntity(motor, MotorEntityField, live);
            bool sensesChanged = RefreshCachedEntity(senses, SensesEntityField, live);
            if (motorChanged)
                motor.hasBowAttack = live.MobileEnemy.HasRangedAttack1 &&
                    (!live.MobileEnemy.CastsMagic || live.MobileEnemy.HasRangedAttack2);
            if (motorChanged || sensesChanged)
                Debug.Log(Prefix + "Refreshed client AI entity netId=" + identity.netId +
                    " motor=" + motorChanged + " senses=" + sensesChanged + " team=" + live.Team);
        }
    }

    static bool RefreshCachedEntity(Component component, System.Reflection.FieldInfo field, EnemyEntity live)
    {
        if (component == null) return false;
        var cached = field.GetValue(component) as EnemyEntity;
        // Null is left to the component's own Start/initialization coroutine.
        if (cached == null || ReferenceEquals(cached, live)) return false;
        field.SetValue(component, live);
        return true;
    }

    // Temporary, bounded diagnostics: idle samples cannot exhaust the combat budget.
    // Includes ordinary nearby owned enemies as a control. No gameplay mutations.
    void DiagnoseNearbyClientSpawns()
    {
        var game = GameManager.Instance;
        if (game == null || game.PlayerGPS == null || game.PlayerObject == null) return;
        var spawned = NetworkServer.active ? NetworkServer.spawned : NetworkClient.spawned;
        foreach (NetworkIdentity identity in spawned.Values)
        {
            if (identity == null) continue;
            var world = identity.GetComponent<EnemyWorldPosition>();
            if (world == null) continue;
            bool wod = world.PreserveDistantExteriorCoordinates;
            // Ordinary client-owned enemies provide a working comparison on that client.
            if (!wod && (NetworkServer.active || !identity.hasAuthority)) continue;
            double dx = ((double)world.worldX - game.PlayerGPS.WorldX) / DfUnitsPerMetre;
            double dz = ((double)world.worldZ - game.PlayerGPS.WorldZ) / DfUnitsPerMetre;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            // On the host, also inspect far-away client spawns; its authority
            // decision is exactly what we need to compare with the nearby client.
            if (!NetworkServer.active && distance > 100) continue;
            MotionProbe probe;
            if (!motionProbes.TryGetValue(identity.netId, out probe))
            {
                motionProbes[identity.netId] = new MotionProbe {
                    Position = identity.transform.position, Next = Time.unscaledTime + 2f };
                continue;
            }
            var motor = identity.GetComponent<EnemyMotor>();
            var senses = identity.GetComponent<EnemySenses>();
            bool combat = senses != null && senses.Target != null;
            if (Time.unscaledTime < probe.Next) continue;
            if (combat ? probe.CombatSamples >= 6 : probe.IdleSamples >= 2) continue;
            probe.Next = Time.unscaledTime + 2f;
            if (combat) probe.CombatSamples++;
            else probe.IdleSamples++;
            var controller = identity.GetComponent<CharacterController>();
            var setup = identity.GetComponent<SetupDemoEnemy>();
            var authority = identity.GetComponent<DynamicEnemyAuthority>();
            var behaviour = identity.GetComponent<DaggerfallEntityBehaviour>();
            var entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            string target = senses != null && senses.Target != null ? senses.Target.name : "none";
            string owner = identity.connectionToClient != null && identity.connectionToClient.identity != null
                ? identity.connectionToClient.identity.netId.ToString() : "none/local-unknown";
            string action = motor != null && motor.TakeActionHandler != null
                ? motor.TakeActionHandler.Method.DeclaringType + "." + motor.TakeActionHandler.Method.Name : "none";
            Debug.Log("[WODMP CombatProbe] kind=" + (wod ? "WOD" : "ordinary") +
                " phase=" + (combat ? "targeted" : "idle") + " peer=" + (NetworkServer.active ? "host" : "client") +
                " netId=" + identity.netId + " requester=" + world.requesterNetId + " owner=" + owner +
                " hasAuthority=" + identity.hasAuthority + " localDFDistance=" + distance.ToString("F1") +
                " DF=" + world.worldX + "/" + world.worldZ + " pos=" + identity.transform.position +
                " moved=" + Vector3.Distance(identity.transform.position, probe.Position).ToString("F3") +
                " dormant=" + (authority != null && authority.IsAuthorityDeactivatedForRetention) +
                " AIoff=" + game.DisableAI + " timeScale=" + Time.timeScale +
                " setupReady=" + (setup != null && setup.HasReceivedInitialServerSettings()) +
                " motor=" + (motor != null && motor.enabled) + " canAct=" + (motor != null && motor.CanAct) +
                " hostile=" + (motor != null && motor.IsHostile) + " action=" + action +
                " senses=" + (senses != null && senses.enabled) + " target=" + target +
                " giveUp=" + (motor != null ? motor.GiveUpTimer.ToString() : "missing") +
                " predicted=" + (senses != null ? senses.PredictedTargetPos.ToString("F2") : "missing") +
                " predReset=" + (senses != null && senses.PredictedTargetPos == EnemySenses.ResetPlayerPos) +
                " detected=" + (senses != null && senses.DetectedTarget) +
                " sight=" + (senses != null && senses.TargetInSight) +
                " targetDistance=" + (senses != null ? senses.DistanceToTarget.ToString("F2") : "missing") +
                " playerProxy=" + (senses != null && senses.PlayerTarget != null ? senses.PlayerTarget.name : "none") +
                " obstacle=" + (motor != null && motor.ObstacleDetected) +
                " fallDetected=" + ProbeField(motor, "fallDetected") +
                " bashing=" + (motor != null && motor.Bashing) +
                " destination=" + ProbeField(motor, "destination") +
                " pursuing=" + ProbeField(motor, "pursuing") +
                " pausePursuit=" + ProbeField(motor, "pausePursuit") +
                " motorEntity=" + ProbeEntity(motor, "entity", entity) +
                " sensesEntity=" + ProbeEntity(senses, "enemyEntity", entity) +
                " cc=" + (controller != null && controller.enabled) +
                " velocity=" + (controller != null ? controller.velocity.ToString("F3") : "missing") +
                " collisionFlags=" + (controller != null ? controller.collisionFlags.ToString() : "missing") +
                " ccShape=" + (controller != null ? controller.height + "/" + controller.radius + "/" + controller.center : "missing") +
                " scale=" + identity.transform.lossyScale +
                " grounded=" + (controller != null && controller.isGrounded) +
                " collisions=" + (controller != null && controller.detectCollisions) +
                " hp=" + (entity != null ? entity.CurrentHealth.ToString() : "missing") +
                " team=" + (entity != null ? entity.Team.ToString() : "missing") +
                " mobileTeam=" + (entity != null ? entity.MobileEnemy.Team.ToString() : "missing"));
            probe.Position = identity.transform.position;
        }
    }

    // Read-only inspection of this fork's cached AI state. Missing fields are
    // reported explicitly so diagnostics remain safe across source revisions.
    static object ProbeField(Component component, string name)
    {
        if (component == null) return "missing-component";
        var field = component.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        return field != null ? field.GetValue(component) : "missing-field";
    }

    static string ProbeEntity(Component component, string field, EnemyEntity live)
    {
        var cached = ProbeField(component, field) as EnemyEntity;
        if (cached == null) return "missing";
        return (ReferenceEquals(cached, live) ? "current" : "STALE") +
            ":speed=" + cached.Stats.LiveSpeed + ":hp=" + cached.CurrentHealth +
            ":team=" + cached.Team;
    }

    void RestoreLocalObjects()
    {
        foreach (DetachedEnemy entry in detached)
            if (entry.Enemy != null && entry.Parent != null)
                entry.Enemy.transform.SetParent(entry.Parent, true);
        detached.Clear();
        foreach (HostMarker marker in hostMarkers)
            if (marker.Enemy != null) marker.Enemy.SetActive(true);
        hostMarkers.Clear();
        startupPreserved.Clear();
        foreach (GameObject enemy in suppressed)
            if (enemy != null) enemy.SetActive(true);
        suppressed.Clear();
        warned.Clear();
        clientMarkers.Clear();
        serverMarkers.Clear();
        requestBudgets.Clear();
        motionProbes.Clear();
    }
}
