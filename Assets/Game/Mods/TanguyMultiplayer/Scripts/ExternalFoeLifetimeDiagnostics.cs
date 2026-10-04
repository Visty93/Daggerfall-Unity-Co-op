using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

/// <summary>
/// Temporary diagnostic helper for caller-supplied FoeSpawner actors.
/// Plain MonoBehaviour only: no network state, no gameplay changes.
/// Attach only to the external-FoeSpawner test actors.
/// </summary>
public sealed class ExternalFoeLifetimeDiagnostics : MonoBehaviour
{
    const string Prefix = "[ExternalFoeDiag]";

    string role = string.Empty;
    uint requesterNetId;
    bool configured;
    float configuredAt;
    int configuredFrame;
    float nextHeartbeat;

    bool lastActiveSelf;
    bool lastActiveInHierarchy;
    int lastParentId;
    uint lastNetId;
    bool lastServerRegistered;
    int lastHealth = int.MinValue;
    bool lastAuthorityDeactivated;
    bool lastLocalCull;
    int lastOwnerConnectionId = int.MinValue;
    int lastWorldX = int.MinValue;
    int lastWorldZ = int.MinValue;

    static bool applicationQuitting;

    public static ExternalFoeLifetimeDiagnostics Attach(GameObject go, string role, uint requester = 0)
    {
        if (go == null)
            return null;

        ExternalFoeLifetimeDiagnostics diag = go.GetComponent<ExternalFoeLifetimeDiagnostics>();
        if (diag == null)
            diag = go.AddComponent<ExternalFoeLifetimeDiagnostics>();

        diag.Configure(role, requester);
        return diag;
    }

    public void Configure(string newRole, uint requester = 0)
    {
        if (!configured)
        {
            role = string.IsNullOrEmpty(newRole) ? "UNSPECIFIED" : newRole;
            requesterNetId = requester;
            configured = true;
            configuredAt = Time.unscaledTime;
            configuredFrame = Time.frameCount;
            nextHeartbeat = Time.unscaledTime;
            PrimeState();
            Snapshot("CONFIGURED", true, false);
        }
        else
        {
            if (requester != 0)
                requesterNetId = requester;
            if (!string.IsNullOrEmpty(newRole) && role.IndexOf(newRole, StringComparison.Ordinal) < 0)
                role = role + "+" + newRole;
        }
    }

    public void Mark(string phase)
    {
        if (!configured)
            Configure("UNSPECIFIED", 0);
        Snapshot(phase, true, false);
    }

    void Start()
    {
        if (configured)
            Snapshot("START", true, false);
    }

    void Update()
    {
        if (!configured)
            return;

        bool changed = DetectStateChange();
        if (changed)
            Snapshot("STATE_CHANGE", true, false);

        float age = Time.unscaledTime - configuredAt;
        float interval = age < 4f ? 0.5f : 1.0f;
        if (Time.unscaledTime >= nextHeartbeat)
        {
            Snapshot("HEARTBEAT", false, false);
            nextHeartbeat = Time.unscaledTime + interval;
        }
    }

    void OnEnable()
    {
        if (configured)
            Snapshot("ON_ENABLE", true, false);
    }

    void OnDisable()
    {
        if (configured)
            Snapshot("ON_DISABLE", true, true);
    }

    void OnDestroy()
    {
        if (configured)
            Snapshot("ON_DESTROY", true, true);
    }

    void OnApplicationQuit()
    {
        applicationQuitting = true;
    }

    void PrimeState()
    {
        lastActiveSelf = gameObject.activeSelf;
        lastActiveInHierarchy = gameObject.activeInHierarchy;
        lastParentId = transform.parent != null ? transform.parent.GetInstanceID() : 0;

        NetworkIdentity ni = GetComponent<NetworkIdentity>();
        lastNetId = ni != null ? ni.netId : 0U;
        lastServerRegistered = IsServerRegistered(lastNetId);

        DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity enemy = behaviour != null ? behaviour.Entity as EnemyEntity : null;
        lastHealth = enemy != null ? enemy.CurrentHealth : int.MinValue;

        DynamicEnemyAuthority dea = GetComponent<DynamicEnemyAuthority>();
        lastAuthorityDeactivated = ReadPrivateBool(dea, "authorityDeactivated");
        lastLocalCull = ReadPrivateBool(dea, "localPerPlayerCulled");
        lastOwnerConnectionId = ReadCurrentOwnerConnectionId(dea);

        EnemyWorldPosition world = GetComponent<EnemyWorldPosition>();
        lastWorldX = world != null ? world.worldX : int.MinValue;
        lastWorldZ = world != null ? world.worldZ : int.MinValue;
    }

    bool DetectStateChange()
    {
        bool changed = false;

        if (lastActiveSelf != gameObject.activeSelf)
        {
            lastActiveSelf = gameObject.activeSelf;
            changed = true;
        }
        if (lastActiveInHierarchy != gameObject.activeInHierarchy)
        {
            lastActiveInHierarchy = gameObject.activeInHierarchy;
            changed = true;
        }

        int parentId = transform.parent != null ? transform.parent.GetInstanceID() : 0;
        if (lastParentId != parentId)
        {
            lastParentId = parentId;
            changed = true;
        }

        NetworkIdentity ni = GetComponent<NetworkIdentity>();
        uint netId = ni != null ? ni.netId : 0U;
        if (lastNetId != netId)
        {
            lastNetId = netId;
            changed = true;
        }

        bool serverRegistered = IsServerRegistered(netId);
        if (lastServerRegistered != serverRegistered)
        {
            lastServerRegistered = serverRegistered;
            changed = true;
        }

        DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity enemy = behaviour != null ? behaviour.Entity as EnemyEntity : null;
        int health = enemy != null ? enemy.CurrentHealth : int.MinValue;
        if (lastHealth != health)
        {
            lastHealth = health;
            changed = true;
        }

        DynamicEnemyAuthority dea = GetComponent<DynamicEnemyAuthority>();
        bool authorityDeactivated = ReadPrivateBool(dea, "authorityDeactivated");
        bool localCull = ReadPrivateBool(dea, "localPerPlayerCulled");
        int ownerConnectionId = ReadCurrentOwnerConnectionId(dea);
        if (lastAuthorityDeactivated != authorityDeactivated)
        {
            lastAuthorityDeactivated = authorityDeactivated;
            changed = true;
        }
        if (lastLocalCull != localCull)
        {
            lastLocalCull = localCull;
            changed = true;
        }
        if (lastOwnerConnectionId != ownerConnectionId)
        {
            lastOwnerConnectionId = ownerConnectionId;
            changed = true;
        }

        EnemyWorldPosition world = GetComponent<EnemyWorldPosition>();
        int wx = world != null ? world.worldX : int.MinValue;
        int wz = world != null ? world.worldZ : int.MinValue;
        if (lastWorldX != wx || lastWorldZ != wz)
        {
            lastWorldX = wx;
            lastWorldZ = wz;
            changed = true;
        }

        return changed;
    }

    void Snapshot(string phase, bool includeComponents, bool includeStack)
    {
        try
        {
            NetworkIdentity ni = GetComponent<NetworkIdentity>();
            DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemy = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            SetupDemoEnemy setup = GetComponent<SetupDemoEnemy>();
            EnemyMotor motor = GetComponent<EnemyMotor>();
            EnemyWorldPosition world = GetComponent<EnemyWorldPosition>();
            DynamicEnemyAuthority dea = GetComponent<DynamicEnemyAuthority>();

            uint netId = ni != null ? ni.netId : 0U;
            bool serverRegistered = IsServerRegistered(netId);
            int connectionId = ni != null && ni.connectionToClient != null ? ni.connectionToClient.connectionId : -1;

            bool authorityDeactivated = ReadPrivateBool(dea, "authorityDeactivated");
            bool localCull = ReadPrivateBool(dea, "localPerPlayerCulled");
            bool destroyCountdown = ReadPrivateObject(dea, "destroyCoroutine") != null;
            int currentOwnerConnectionId = ReadCurrentOwnerConnectionId(dea);
            float deaSpawnTime = ReadPrivateFloat(dea, "spawnTime");

            string entityText = "entity=<none>";
            if (enemy != null)
            {
                MobileEnemy mobile = enemy.MobileEnemy;
                entityText =
                    $"entityId={mobile.ID} hp={enemy.CurrentHealth}/{enemy.MaxHealth} " +
                    $"team={enemy.Team} mobileTeam={mobile.Team} reaction={mobile.Reactions} gender={mobile.Gender}";
            }

            string setupText = setup != null
                ? $"setupType={setup.EnemyType} setupReaction={setup.EnemyReaction} setupGender={setup.EnemyGender} " +
                  $"allied={setup.AlliedToPlayer} teamOverride={setup.SpawnTeamOverride} extName='{setup.ExternalSpawnDisplayName}' " +
                  $"extVisual={setup.ExternalVisualEnemyId} dungeonEnemy={setup.isDungeonEnemy}"
                : "setup=<none>";

            string motorText = motor != null
                ? $"motorEnabled={motor.enabled} canAct={motor.CanAct} hostile={motor.IsHostile}"
                : "motor=<none>";

            string worldText = world != null
                ? $"world={world.worldX}/{world.worldZ} initialized={world.initialized} requester={world.requesterNetId} " +
                  $"bakedRequester={world.worldBakedFromRequester} interior={world.isInteriorSpawn} dungeon={world.isDungeonSpawn} " +
                  $"intended={Fmt(world.intendedSpawnPos)} playerUnity={Fmt(world.playerUnityPosition)} spawnUnity={Fmt(world.spawnEnemyUnityPosition)}"
                : "world=<none>";

            string deaText = dea != null
                ? $"DEA[server={dea.isServer} client={dea.isClient} authority={dea.hasAuthority} deactivated={authorityDeactivated} " +
                  $"localCull={localCull} destroyCountdown={destroyCountdown} currentOwnerConn={currentOwnerConnectionId} " +
                  $"persistentOwner={dea.PersistentOwnerPlayerNetId} spawnAge={(Time.time - deaSpawnTime):0.000} " +
                  $"switchDF={dea.switchDistance} deactivateDF={dea.deactivateDistance} destroyDF={dea.destroyDistance} maxY={dea.maxYDistance} " +
                  $"retentionDF={dea.HorizontalRetentionDistanceDF}]"
                : "DEA=<none>";

            string componentText = includeComponents ? " components=" + ComponentList(gameObject) : string.Empty;
            string stackText = includeStack ? "\nSTACK:\n" + Environment.StackTrace : string.Empty;

            Debug.Log(
                $"{Prefix}[{phase}] role={role} age={(Time.unscaledTime - configuredAt):0.000}s frame={Time.frameCount} bornFrame={configuredFrame} " +
                $"name='{gameObject.name}' instance={gameObject.GetInstanceID()} requesterHint={requesterNetId} " +
                $"activeSelf={gameObject.activeSelf} activeHierarchy={gameObject.activeInHierarchy} parent='{ParentPath(transform)}' " +
                $"pos={Fmt(transform.position)} rotY={transform.eulerAngles.y:0.0} scale={Fmt(transform.lossyScale)} scene='{gameObject.scene.name}' " +
                $"NI[netId={netId} server={((ni != null) ? ni.isServer : false)} client={((ni != null) ? ni.isClient : false)} " +
                $"authority={((ni != null) ? ni.hasAuthority : false)} ownerConn={connectionId} serverRegistered={serverRegistered}] " +
                entityText + " " + setupText + " " + motorText + " " + worldText + " " + deaText +
                $" quitting={applicationQuitting}" + componentText + stackText,
                this);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{Prefix}[SnapshotFailed] phase={phase} role={role} error={ex}");
        }
    }

    static string Fmt(Vector3 v)
    {
        return $"({v.x:0.000},{v.y:0.000},{v.z:0.000})";
    }

    static string ParentPath(Transform t)
    {
        if (t == null || t.parent == null)
            return "<root>";

        StringBuilder sb = new StringBuilder();
        Transform p = t.parent;
        int depth = 0;
        while (p != null && depth < 8)
        {
            if (sb.Length > 0)
                sb.Insert(0, "/");
            sb.Insert(0, p.name);
            p = p.parent;
            depth++;
        }
        return sb.ToString();
    }

    static string ComponentList(GameObject go)
    {
        if (go == null)
            return "<destroyed>";

        Component[] components = go.GetComponents<Component>();
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];
            if (i > 0)
                sb.Append(",");
            if (c == null)
            {
                sb.Append("<missing>");
                continue;
            }
            sb.Append(c.GetType().FullName);
            Behaviour behaviour = c as Behaviour;
            if (behaviour != null)
                sb.Append(behaviour.enabled ? "(on)" : "(off)");
        }
        return sb.ToString();
    }

    static bool IsServerRegistered(uint netId)
    {
        if (!NetworkServer.active || netId == 0U)
            return false;
        try { return NetworkServer.spawned.ContainsKey(netId); }
        catch { return false; }
    }

    static bool ReadPrivateBool(object target, string fieldName)
    {
        object value = ReadPrivateObject(target, fieldName);
        return value is bool && (bool)value;
    }

    static float ReadPrivateFloat(object target, string fieldName)
    {
        object value = ReadPrivateObject(target, fieldName);
        return value is float ? (float)value : 0f;
    }

    static object ReadPrivateObject(object target, string fieldName)
    {
        if (target == null)
            return null;
        try
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field != null ? field.GetValue(target) : null;
        }
        catch { return null; }
    }

    static int ReadCurrentOwnerConnectionId(DynamicEnemyAuthority dea)
    {
        object owner = ReadPrivateObject(dea, "currentOwner");
        NetworkConnectionToClient connection = owner as NetworkConnectionToClient;
        return connection != null ? connection.connectionId : -1;
    }

    public static void LogEnemySpawnSubscribers(string phase, GameObject enemy)
    {
        try
        {
            FieldInfo field = typeof(GameManager).GetField(
                "OnEnemySpawn",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Delegate handlers = field != null ? field.GetValue(null) as Delegate : null;
            Delegate[] invocationList = handlers != null ? handlers.GetInvocationList() : new Delegate[0];

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < invocationList.Length; i++)
            {
                Delegate handler = invocationList[i];
                MethodInfo method = handler.Method;
                if (i > 0)
                    sb.Append(" | ");
                sb.Append(method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>");
                sb.Append(".");
                sb.Append(method.Name);
                if (handler.Target != null)
                {
                    sb.Append(" target=");
                    sb.Append(handler.Target.GetType().FullName);
                }
            }

            Debug.Log(
                $"[ExternalFoeEventDiag][{phase}] enemy='{(enemy != null ? enemy.name : "<null>")}' " +
                $"subscriberCount={invocationList.Length} subscribers={sb}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExternalFoeEventDiag][{phase}] Could not inspect OnEnemySpawn subscribers: {ex.Message}");
        }
    }

    public static void LogComponents(string phase, GameObject go)
    {
        Debug.Log(
            $"[ExternalFoeEventDiag][{phase}] enemy='{(go != null ? go.name : "<null>")}' " +
            $"components={(go != null ? ComponentList(go) : "<destroyed>")}");
    }
}
