// Multiplayer compatibility for Vanilla Enhanced by carademono and contributors.
// Core compatibility was implemented against the formerly public Vanilla Enhanced source code.
// Vanilla Enhanced is not included and no Vanilla Enhanced assets are redistributed.
// https://www.nexusmods.com/daggerfallunity/mods/273


using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

/// <summary>
/// Compatibility bridge for Vanilla Enhanced visual-replacement mods in multiplayer.
///
/// This helper is intentionally inert unless multiplayer is active (Mirror client or host/server).
/// It does not depend on any mod title. Player bridging is only installed when DFU's current
/// DaggerfallEnemy.MobileUnit is actually different from SpriteMultiplayer's concrete unit.
///
/// Important player detail:
/// Vanilla Enhanced's enemy-variety replacement is a custom MobileUnit implementation,
/// not necessarily a DaggerfallMobileUnit. SpriteMultiplayer, however, stores a concrete
/// DaggerfallMobileUnit reference. A direct rebind therefore cannot work for those replacements.
///
/// For that case this file installs a disabled DaggerfallMobileUnit-compatible proxy on the
/// current VE visual. SpriteMultiplayer keeps using its existing API, while the proxy forwards
/// animation state, freeze state, and SetEnemy() setup synchronously to DaggerfallEnemy.MobileUnit.
/// The VE replacement remains the object that actually renders and animates.
///
/// Networked EnemyMotor instances are also kept bound to DaggerfallEnemy.MobileUnit when a
/// visual replacement occurs, so their existing Idle/Move/Hurt decisions reach the visible unit.
/// For custom static NPCs in network dungeons, this helper reconstructs the original RDB flat
/// centre Y directly from the authored block record. DFU's generic custom-flat importer shifts
/// dungeon replacements down by half of the original texture height. For the reported sunk
/// Animated People case, restore a downward mismatch to the authored RDB Y. This is a targeted
/// compatibility correction; the third-party initialization cause is not yet verified.
/// V6 defers the first correction across a frame boundary so normal Start() can run first. No floor
/// raycast, renderer-bound estimate, or hardcoded height is used.
/// Villager Variety civilian clothing is made deterministic only for civilians already tracked by
/// MobileNpcSync; its extra local random clothing roll is not added to the network. Ordinary
/// single-player actors, unsynchronized civilians, enemy authority, movement, networking,
/// collision, and gameplay state are not modified.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class VanillaEnhancedMultiplayerCompat : MonoBehaviour
{
    internal const string LogPrefix = "[VanillaEnhancedMultiplayerCompat] ";

    const string RemoteNpcNamePrefix = "MobileNPC_RemoteSoftSync";
    const float RemoteNpcScanInterval = 0.25f;
    const float NetworkedEnemyScanInterval = 0.10f;
    const float DungeonStaticNpcScanInterval = 0.10f;
    const float DungeonStaticNpcMinDownwardShift = 0.20f;
    const float DungeonStaticNpcMaxDownwardShift = 3.00f;
    const float DungeonStaticNpcRdbXZTolerance = 0.05f;

    static VanillaEnhancedMultiplayerCompat instance;
    static readonly HashSet<int> diagnosedPlayers = new HashSet<int>();

    // Villager Variety chooses one extra clothing variant locally inside its custom
    // MobilePersonAsset.SetPerson(). MobileNpcSync already synchronizes the base DFU
    // identity (race/gender/outfit/face/name), but not this mod-private random roll.
    // Cache the deterministic re-application per live NPC so this stays a one-time
    // cosmetic correction rather than doing texture work every 0.25 seconds.
    static readonly Dictionary<int, int> deterministicNpcAppearanceSignatures =
        new Dictionary<int, int>();


    sealed class DungeonStaticNpcRdbPlacement
    {
        public StaticNPC npc;
        public DaggerfallDungeon dungeon;
        public float authoredLocalY;
        public string blockName;
        public int archive;
        public int record;
        public Transform parent;
        public bool correctionLogged;
        public int firstObservedFrame;
        public bool afterStartLogged;
    }

    static readonly Dictionary<int, DungeonStaticNpcRdbPlacement> dungeonStaticNpcRdbPlacements =
        new Dictionary<int, DungeonStaticNpcRdbPlacement>();

    // Log once per observed state and live object, including failed resolution. This makes
    // a silent guard failure distinguishable from a child mesh/pivot problem in Player.log.
    static readonly Dictionary<StaticNPC, string> dungeonStaticNpcDiagnostics = new Dictionary<StaticNPC, string>();

    const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags InstanceAny =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static bool npcReflectionInitialized;
    static bool npcReflectionAvailable;
    static bool npcReflectionWarningLogged;
    static FieldInfo mobileNpcLocalInstanceField;
    static FieldInfo mobileNpcLocalRecordsField;
    static FieldInfo mobileNpcRemoteGhostsField;
    static FieldInfo localRecordNpcField;
    static FieldInfo localRecordNpcIdField;
    static FieldInfo localRecordLocationKeyField;
    static FieldInfo remoteRecordNpcField;
    static FieldInfo remoteRecordNpcIdField;
    static FieldInfo remoteRecordLocationKeyField;
    static FieldInfo remoteRecordOwnerPlayerIdField;

    // EnemyMotor caches its MobileUnit once during Start(). Visual replacement mods can later
    // make DaggerfallEnemy.MobileUnit point at a different, custom MobileUnit while EnemyMotor
    // keeps driving the old billboard. Keep the private cache aligned with DFU's current
    // canonical MobileUnit, but only for actually networked enemy objects while MP is active.
    static bool enemyMotorReflectionInitialized;
    static bool enemyMotorReflectionAvailable;
    static bool enemyMotorReflectionWarningLogged;
    static FieldInfo enemyMotorMobileField;

    bool activationLogged;
    bool playerDiscoveryLogged;
    bool villagerClothingSyncLogged;
    float nextRemoteNpcScanRealtime;
    float nextNetworkedEnemyScanRealtime;
    float nextDungeonStaticNpcScanRealtime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (instance != null)
            return;

        GameObject go = new GameObject("Vanilla Enhanced Multiplayer Compat");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<VanillaEnhancedMultiplayerCompat>();
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

    void Update()
    {
        if (!IsMultiplayerActive())
            return;

        if (!activationLogged)
        {
            activationLogged = true;
            Debug.Log(
                LogPrefix +
                "Active in multiplayer (NPC height v6). Scanning PlayerMultiplayer visuals for a custom MobileUnit replacement.");
        }

        ReconcileMultiplayerPlayerVisuals(ref playerDiscoveryLogged);

        float now = Time.realtimeSinceStartup;

        if (now >= nextNetworkedEnemyScanRealtime)
        {
            nextNetworkedEnemyScanRealtime = now + NetworkedEnemyScanInterval;
            ReconcileNetworkedEnemyMotorVisuals();
        }

        if (now >= nextRemoteNpcScanRealtime)
        {
            nextRemoteNpcScanRealtime = now + RemoteNpcScanInterval;
            ReconcileCivilianVisuals(ref villagerClothingSyncLogged);
        }
    }

    // Run a second light player pass after ordinary Update/coroutine work. This catches a
    // replacement component or player object that appeared during profile setup this frame.
    void LateUpdate()
    {
        if (!IsMultiplayerActive())
            return;

        ReconcileMultiplayerPlayerVisuals(ref playerDiscoveryLogged);

        // Observe NPCs after ordinary Update work. The per-object frame gate below also
        // prevents a newly instantiated NPC from being moved before its first Start().
        float now = Time.realtimeSinceStartup;
        if (now >= nextDungeonStaticNpcScanRealtime)
        {
            nextDungeonStaticNpcScanRealtime = now + DungeonStaticNpcScanInterval;
            ReconcileDungeonStaticNpcRdbPositions();
        }
    }

    static bool IsMultiplayerActive()
    {
        // NetworkClient.isConnected alone proved too narrow for the host path.
        // NetworkServer.active covers Host mode; NetworkClient.active/isConnected covers clients.
        return NetworkServer.active || NetworkClient.active || NetworkClient.isConnected;
    }

    /// <summary>
    /// Only touches actual PlayerMultiplayer scene actors while Mirror is active.
    /// Discovery intentionally does not depend on NetworkClient.spawned because the host path
    /// can have a valid PlayerMultiplayer before/without the client dictionary state we expected.
    /// </summary>
    static void ReconcileMultiplayerPlayerVisuals(ref bool playerDiscoveryLogged)
    {
        PlayerMultiplayer[] players = GameObject.FindObjectsOfType<PlayerMultiplayer>();
        if (players == null || players.Length == 0)
            return;

        if (!playerDiscoveryLogged)
        {
            playerDiscoveryLogged = true;
            Debug.Log(LogPrefix + "Found " + players.Length + " active PlayerMultiplayer object(s).");
        }

        for (int p = 0; p < players.Length; p++)
        {
            PlayerMultiplayer player = players[p];
            if (!player)
                continue;

            NetworkIdentity identity = player.GetComponent<NetworkIdentity>();
            if (!identity)
                identity = player.GetComponentInParent<NetworkIdentity>();

            SpriteMultiplayer spriteController =
                player.GetComponentInChildren<SpriteMultiplayer>(true);
            if (!spriteController)
                continue;

            DaggerfallEnemy daggerfallEnemy =
                FindPlayerDaggerfallEnemy(identity, spriteController, null);
            if (!daggerfallEnemy || !daggerfallEnemy.MobileUnit)
                continue;

            MobileUnit canonical = daggerfallEnemy.MobileUnit;
            DaggerfallMobileUnit currentSprite = spriteController.sprite;

            int diagnosticKey = player.GetInstanceID();
            if (!diagnosedPlayers.Contains(diagnosticKey))
            {
                diagnosedPlayers.Add(diagnosticKey);
                Debug.Log(
                    LogPrefix + "Player visual diagnostic on '" + player.gameObject.name + "': " +
                    "SpriteMultiplayer.sprite=" +
                    (currentSprite ? currentSprite.GetType().FullName : "null") +
                    ", DaggerfallEnemy.MobileUnit=" + canonical.GetType().FullName +
                    ", same=" + ((MobileUnit)currentSprite == canonical) +
                    ", canonicalActive=" + canonical.gameObject.activeInHierarchy + ".");
            }

            // Normal DFU/Tanguy path: both systems already reference the same concrete unit.
            // Do absolutely nothing.
            if (currentSprite != null && (MobileUnit)currentSprite == canonical)
                continue;

            // A custom replacement that still derives from DaggerfallMobileUnit can be rebound
            // directly. This is not the VillainVariety case but remains a safe compatibility path.
            DaggerfallMobileUnit concreteCanonical = canonical as DaggerfallMobileUnit;
            if (concreteCanonical != null &&
                !(concreteCanonical is VanillaEnhancedMultiplayerMobileProxy))
            {
                EnsureConcreteMobileMesh(identity, concreteCanonical);

                if (spriteController.sprite != concreteCanonical)
                {
                    spriteController.sprite = concreteCanonical;
                    Debug.Log(
                        LogPrefix + "Directly rebound MP player visual to concrete replacement '" +
                        canonical.GetType().FullName + "'.");
                }

                continue;
            }

            // Custom replacement is MobileUnit but not DaggerfallMobileUnit (e.g.
            // VillainVarietyMobileUnit). SpriteMultiplayer is hard-typed to DaggerfallMobileUnit,
            // so install a concrete proxy which forwards its existing calls to the visible unit.
            VanillaEnhancedMultiplayerMobileProxy proxy =
                canonical.GetComponent<VanillaEnhancedMultiplayerMobileProxy>();

            if (!proxy)
                proxy = canonical.gameObject.AddComponent<VanillaEnhancedMultiplayerMobileProxy>();

            proxy.Bind(identity, spriteController);

            if (spriteController.sprite != proxy)
            {
                DaggerfallMobileUnit oldSprite = spriteController.sprite;
                spriteController.sprite = proxy;

                Debug.Log(
                    LogPrefix + "Installed MP animation bridge. SpriteMultiplayer concrete unit '" +
                    (oldSprite ? oldSprite.GetType().FullName : "null") +
                    "' -> custom MobileUnit '" + canonical.GetType().FullName + "'.");
            }

            // Never run inherited DaggerfallMobileUnit.Update()/AnimateEnemy() on the proxy.
            if (proxy.enabled)
                proxy.enabled = false;
        }
    }

    /// <summary>
    /// Finds the DaggerfallEnemy whose MobileUnit is DFU's current visual source of truth.
    /// The preferred local/parent checks match the existing MP prefab layout, then a hierarchy
    /// scan tolerates replacement mods moving the visual underneath another child.
    /// </summary>
    internal static DaggerfallEnemy FindPlayerDaggerfallEnemy(
        NetworkIdentity identity,
        SpriteMultiplayer spriteController,
        VanillaEnhancedMultiplayerMobileProxy ignoreProxy)
    {
        DaggerfallEnemy candidate = null;

        if (spriteController)
            candidate = spriteController.GetComponent<DaggerfallEnemy>();

        if (IsUsableEnemy(candidate, ignoreProxy))
            return candidate;

        if (spriteController)
            candidate = spriteController.GetComponentInParent<DaggerfallEnemy>();

        if (IsUsableEnemy(candidate, ignoreProxy))
            return candidate;

        if (!identity)
            return null;

        DaggerfallEnemy[] enemies = identity.GetComponentsInChildren<DaggerfallEnemy>(true);
        DaggerfallEnemy firstWithMobile = null;

        for (int i = 0; i < enemies.Length; i++)
        {
            DaggerfallEnemy enemy = enemies[i];
            if (!IsUsableEnemy(enemy, ignoreProxy))
                continue;

            if (firstWithMobile == null)
                firstWithMobile = enemy;

            MobileUnit mobile = enemy.MobileUnit;
            if (mobile != null && mobile.gameObject.activeInHierarchy)
                return enemy;
        }

        return firstWithMobile;
    }

    static bool IsUsableEnemy(
        DaggerfallEnemy enemy,
        VanillaEnhancedMultiplayerMobileProxy ignoreProxy)
    {
        if (!enemy || !enemy.MobileUnit)
            return false;

        if (ignoreProxy != null && enemy.MobileUnit == ignoreProxy)
            return false;

        return true;
    }

    /// <summary>
    /// Fallback only for a custom replacement that actually derives from DaggerfallMobileUnit.
    /// Villain/Monster Variety takes the proxy path instead.
    /// </summary>
    static void EnsureConcreteMobileMesh(
        NetworkIdentity identity,
        DaggerfallMobileUnit canonical)
    {
        if (!identity || !canonical)
            return;

        MeshFilter targetFilter = canonical.GetComponent<MeshFilter>();
        if (!targetFilter)
            targetFilter = canonical.gameObject.AddComponent<MeshFilter>();

        if (targetFilter.sharedMesh != null)
            return;

        Mesh sourceMesh = FindValidPlayerMobileMesh(identity, canonical);
        Mesh repairedMesh;

        if (sourceMesh != null)
        {
            repairedMesh = UnityEngine.Object.Instantiate(sourceMesh);
            repairedMesh.name = "MobileEnemyMesh (VE MP Compat Clone)";
        }
        else
        {
            repairedMesh = CreateDfuMobileQuadMesh();
        }

        targetFilter.sharedMesh = repairedMesh;

        Debug.Log(
            LogPrefix + "Repaired missing concrete MP billboard mesh on '" +
            canonical.gameObject.name + "'. VE material/textures were left untouched.");
    }

    static Mesh FindValidPlayerMobileMesh(
        NetworkIdentity identity,
        DaggerfallMobileUnit canonical)
    {
        DaggerfallMobileUnit[] units =
            identity.GetComponentsInChildren<DaggerfallMobileUnit>(true);

        for (int i = 0; i < units.Length; i++)
        {
            DaggerfallMobileUnit unit = units[i];
            if (!unit || unit == canonical || unit is VanillaEnhancedMultiplayerMobileProxy)
                continue;

            MeshFilter filter = unit.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                return filter.sharedMesh;
        }

        return null;
    }

    static Mesh CreateDfuMobileQuadMesh()
    {
        const float hx = 0.5f;
        const float hy = 0.5f;

        Vector3[] vertices = new Vector3[4];
        vertices[0] = new Vector3(hx, hy, 0f);
        vertices[1] = new Vector3(-hx, hy, 0f);
        vertices[2] = new Vector3(hx, -hy, 0f);
        vertices[3] = new Vector3(-hx, -hy, 0f);

        int[] indices = new int[6]
        {
            0, 1, 2,
            3, 2, 1,
        };

        Vector3 normal = Vector3.Normalize(Vector3.up + Vector3.forward);
        Vector3[] normals = new Vector3[4];
        normals[0] = normal;
        normals[1] = normal;
        normals[2] = normal;
        normals[3] = normal;

        Mesh mesh = new Mesh();
        mesh.name = "MobileEnemyMesh (VE MP Compat)";
        mesh.vertices = vertices;
        mesh.triangles = indices;
        mesh.normals = normals;
        return mesh;
    }

    /// <summary>
    /// EnemyMotor caches GetComponentInChildren&lt;MobileUnit&gt;() during Start(). If Vanilla Enhanced
    /// replaces that visual afterwards, DaggerfallEnemy.MobileUnit becomes the new source of truth
    /// but EnemyMotor can continue sending Idle/Move/Hurt/etc. to the old invisible billboard.
    ///
    /// Do not change movement, authority, hostility, AI, or animation decisions here. This only
    /// redirects EnemyMotor's existing animation calls to the same MobileUnit DFU currently owns.
    /// The NetworkIdentity requirement keeps ordinary single-player enemies completely untouched.
    /// </summary>
    static void ReconcileNetworkedEnemyMotorVisuals()
    {
        if (!EnsureEnemyMotorReflection())
            return;

        EnemyMotor[] motors = GameObject.FindObjectsOfType<EnemyMotor>();
        if (motors == null || motors.Length == 0)
            return;

        for (int i = 0; i < motors.Length; i++)
        {
            EnemyMotor motor = motors[i];
            if (!motor)
                continue;

            // PlayerMultiplayer has its own SpriteMultiplayer/proxy bridge above. Never treat a
            // player visual as an ordinary networked enemy even if a future prefab gains EnemyMotor.
            if (motor.GetComponentInParent<PlayerMultiplayer>() != null)
                continue;

            NetworkIdentity identity = motor.GetComponent<NetworkIdentity>();
            if (!identity)
                identity = motor.GetComponentInParent<NetworkIdentity>();

            // EnemyMotor exists in the shared DFU code path, so MP being active by itself is not
            // enough to identify a multiplayer enemy. Only touch objects that Mirror actually owns.
            if (!identity || identity.netId == 0)
                continue;

            DaggerfallEnemy daggerfallEnemy = motor.GetComponent<DaggerfallEnemy>();
            if (!daggerfallEnemy)
                daggerfallEnemy = motor.GetComponentInChildren<DaggerfallEnemy>();
            if (!daggerfallEnemy || !daggerfallEnemy.MobileUnit)
                continue;

            MobileUnit canonical = daggerfallEnemy.MobileUnit;
            MobileUnit cached = enemyMotorMobileField.GetValue(motor) as MobileUnit;

            if (cached == canonical)
                continue;

            // A null cache can occur during spawn ordering. Setting it to DFU's current canonical
            // unit is the same reference EnemyMotor would want once initialization is complete.
            enemyMotorMobileField.SetValue(motor, canonical);

            Debug.Log(
                LogPrefix + "Rebound networked EnemyMotor visual on '" + motor.gameObject.name +
                "': " + (cached ? cached.GetType().FullName : "null") + " -> " +
                canonical.GetType().FullName + ".");
        }
    }

    static bool EnsureEnemyMotorReflection()
    {
        if (enemyMotorReflectionInitialized)
            return enemyMotorReflectionAvailable;

        enemyMotorReflectionInitialized = true;
        enemyMotorMobileField = typeof(EnemyMotor).GetField("mobile", InstanceAny);
        enemyMotorReflectionAvailable =
            enemyMotorMobileField != null &&
            typeof(MobileUnit).IsAssignableFrom(enemyMotorMobileField.FieldType);

        if (!enemyMotorReflectionAvailable && !enemyMotorReflectionWarningLogged)
        {
            enemyMotorReflectionWarningLogged = true;
            Debug.LogWarning(
                LogPrefix +
                "Could not access EnemyMotor.mobile. VE enemy idle/move compatibility is disabled, " +
                "but the player and civilian compatibility paths remain active.");
        }

        return enemyMotorReflectionAvailable;
    }


    /// <summary>
    /// Correct only authored RDB StaticNPC replacement objects inside actually networked dungeons.
    ///
    /// DFU RDBLayout passes the original flat centre position to MeshReplacement. The generic
    /// MeshReplacement.AlignToBase() path then subtracts half of the source texture height for
    /// dungeon replacements before assigning localPosition. That is correct for a replacement whose
    /// root is at its base. The reported bug is consistent with a centre-origin animated-person
    /// replacement remaining at that shifted position; diagnostics below test this on each peer.
    ///
    /// Do not infer the intended Y from the floor, renderer bounds, or the current transform. Resolve
    /// the exact RDB object that created this StaticNPC using its layout hash, archive/record, block,
    /// and X/Z position, then restore only a substantial downward mismatch to the authored centre Y.
    /// </summary>
    static void ReconcileDungeonStaticNpcRdbPositions()
    {
        CleanupDungeonStaticNpcRdbPlacements();

        DaggerfallDungeon[] dungeons = GameObject.FindObjectsOfType<DaggerfallDungeon>();
        if (dungeons == null || dungeons.Length == 0)
            return;

        for (int d = 0; d < dungeons.Length; d++)
        {
            DaggerfallDungeon dungeon = dungeons[d];
            if (!dungeon)
                continue;

            NetworkIdentity dungeonIdentity = dungeon.GetComponent<NetworkIdentity>();
            if (!dungeonIdentity || dungeonIdentity.netId == 0)
                continue;

            StaticNPC[] npcs = dungeon.GetComponentsInChildren<StaticNPC>(true);
            if (npcs == null || npcs.Length == 0)
                continue;

            for (int i = 0; i < npcs.Length; i++)
            {
                StaticNPC npc = npcs[i];
                if (!npc || !npc.gameObject.activeInHierarchy)
                    continue;

                // RDBLayout-authored dungeon people only. Quest-injected NPCs are Context.Custom.
                if (npc.Data.context != StaticNPC.Context.Dungeon)
                {
                    LogDungeonNpcStatus(npc, "skipped-context-" + npc.Data.context, null);
                    continue;
                }

                string customVisualType;
                if (!HasCustomDungeonStaticNpcVisual(npc, out customVisualType))
                {
                    LogDungeonNpcStatus(npc, "skipped-no-AnimatedPeople-component", null);
                    continue;
                }

                int key = npc.GetInstanceID();
                DungeonStaticNpcRdbPlacement placement;
                if (!dungeonStaticNpcRdbPlacements.TryGetValue(key, out placement) || placement == null ||
                    placement.npc != npc || placement.parent != npc.transform.parent)
                {
                    string reason;
                    if (!TryResolveDungeonStaticNpcRdbPlacement(npc, dungeon, out placement, out reason))
                    {
                        LogDungeonNpcStatus(npc, reason, null);
                        continue;
                    }

                    dungeonStaticNpcRdbPlacements[key] = placement;
                    placement.firstObservedFrame = Time.frameCount;
                    LogDungeonNpcStatus(npc, "RDB-discovered-before-height-check", placement);
                    continue;
                }

                // Dynamic objects can be discovered before Unity calls their Start(). V5
                // immediately restored the importer offset, which could allow subsequent
                // mod initialization to add its own lift on top. Observe at least one frame
                // boundary before intervening; do not use a guessed delay in seconds.
                if (Time.frameCount <= placement.firstObservedFrame)
                    continue;

                if (!placement.afterStartLogged)
                {
                    placement.afterStartLogged = true;
                    LogDungeonNpcStatus(npc, "RDB-after-start-height-check", placement);
                }

                float currentLocalY = npc.transform.localPosition.y;
                float downwardShift = placement.authoredLocalY - currentLocalY;

                // This compatibility path is only for the observed sunk replacement case.
                // Never pull an NPC down and never react to tiny animation/pivot noise.
                if (downwardShift < DungeonStaticNpcMinDownwardShift ||
                    downwardShift > DungeonStaticNpcMaxDownwardShift)
                {
                    if (!placement.correctionLogged)
                        LogDungeonNpcStatus(npc, "RDB-no-downward-correction", placement);
                    continue;
                }

                Vector3 local = npc.transform.localPosition;
                local.y = placement.authoredLocalY;
                npc.transform.localPosition = local;

                if (placement.correctionLogged)
                    continue;
                placement.correctionLogged = true;
                Debug.Log(
                    LogPrefix + "Restored custom dungeon StaticNPC '" + npc.gameObject.name +
                    "' to exact RDB flat Y. shift=" + downwardShift.ToString("0.000") +
                    "m, localY=" + currentLocalY.ToString("0.000") + "->" +
                    placement.authoredLocalY.ToString("0.000") +
                    ", flat=" + placement.archive + "." + placement.record +
                    ", block='" + placement.blockName + "'" +
                    ", visual=" + customVisualType +
                    ", dungeon='" + dungeon.gameObject.name +
                    "' netId=" + dungeonIdentity.netId + ".");
            }
        }
    }

    static bool TryResolveDungeonStaticNpcRdbPlacement(
        StaticNPC npc,
        DaggerfallDungeon dungeon,
        out DungeonStaticNpcRdbPlacement placement,
        out string reason)
    {
        placement = null;
        reason = "RDB-missing-npc-or-dungeon";
        if (!npc || !dungeon)
            return false;

        DaggerfallRDBBlock rdbBlock = npc.GetComponentInParent<DaggerfallRDBBlock>();
        if (!rdbBlock)
        {
            reason = "RDB-no-parent-block";
            return false;
        }

        string blockName;
        if (!TryGetRdbBlockName(rdbBlock.gameObject.name, out blockName))
        {
            reason = "RDB-unrecognised-block-name: " + rdbBlock.gameObject.name;
            return false;
        }

        DaggerfallConnect.DFBlock blockData;
        try
        {
            blockData = DaggerfallUnity.Instance.ContentReader.BlockFileReader.GetBlock(blockName);
        }
        catch (Exception ex)
        {
            reason = "RDB-read-failed: " + blockName + " " + ex.Message;
            return false;
        }

        int wantedHash = npc.Data.hash;
        int wantedArchive = npc.Data.billboardArchiveIndex;
        int wantedRecord = npc.Data.billboardRecordIndex;
        float scale = DaggerfallWorkshop.MeshReader.GlobalScale;
        // RDB coordinates belong to the block's Flats node. Convert through that frame
        // rather than assuming the NPC has never been reparented under a mod wrapper.
        Transform flats = rdbBlock.transform.Find("Flats");
        if (!flats || (npc.transform != flats && !npc.transform.IsChildOf(flats)))
        {
            reason = "RDB-no-Flats-ancestor";
            return false;
        }
        Vector3 currentLocal = flats.InverseTransformPoint(npc.transform.position);

        bool found = false;
        float bestXZError = float.MaxValue;
        float bestY = 0f;

        DaggerfallConnect.DFBlock.RdbObjectRoot[] groups = blockData.RdbBlock.ObjectRootList;
        if (groups == null)
        {
            reason = "RDB-empty-block: " + blockName;
            return false;
        }

        for (int g = 0; g < groups.Length; g++)
        {
            DaggerfallConnect.DFBlock.RdbObject[] objects = groups[g].RdbObjects;
            if (objects == null)
                continue;

            for (int o = 0; o < objects.Length; o++)
            {
                DaggerfallConnect.DFBlock.RdbObject obj = objects[o];
                if (obj.Type != DaggerfallConnect.DFBlock.RdbResourceTypes.Flat)
                    continue;

                if (obj.Resources.FlatResource.TextureArchive != wantedArchive ||
                    obj.Resources.FlatResource.TextureRecord != wantedRecord)
                    continue;

                int hash = StaticNPC.GetPositionHash(obj.XPos, obj.YPos, obj.ZPos);
                if (hash != wantedHash)
                    continue;

                float expectedX = obj.XPos * scale;
                float expectedZ = obj.ZPos * scale;
                float xzError = Mathf.Abs(currentLocal.x - expectedX) + Mathf.Abs(currentLocal.z - expectedZ);

                if (xzError < bestXZError)
                {
                    bestXZError = xzError;
                    bestY = -obj.YPos * scale;
                    found = true;
                }
            }
        }

        if (!found || bestXZError > DungeonStaticNpcRdbXZTolerance)
        {
            reason = found ? "RDB-XZ-mismatch" : "RDB-no-matching-flat";
            return false;
        }

        Vector3 authoredWorld = flats.TransformPoint(new Vector3(currentLocal.x, bestY, currentLocal.z));
        Vector3 authoredLocal = npc.transform.parent
            ? npc.transform.parent.InverseTransformPoint(authoredWorld) : authoredWorld;
        // Only a Y correction is supported. Do not move an actor in a rotated frame.
        if (Mathf.Abs(authoredLocal.x - npc.transform.localPosition.x) +
            Mathf.Abs(authoredLocal.z - npc.transform.localPosition.z) > DungeonStaticNpcRdbXZTolerance)
        {
            reason = "RDB-parent-frame-not-vertical";
            return false;
        }

        placement = new DungeonStaticNpcRdbPlacement();
        placement.npc = npc;
        placement.dungeon = dungeon;
        placement.authoredLocalY = authoredLocal.y;
        placement.parent = npc.transform.parent;
        placement.blockName = blockName;
        placement.archive = wantedArchive;
        placement.record = wantedRecord;
        reason = "RDB-matched";
        return true;
    }

    static bool TryGetRdbBlockName(string gameObjectName, out string blockName)
    {
        blockName = null;
        if (string.IsNullOrEmpty(gameObjectName))
            return false;

        const string prefix = "DaggerfallBlock [";
        if (!gameObjectName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        int closing = gameObjectName.IndexOf(']', prefix.Length);
        int length = closing - prefix.Length;
        if (length <= 0)
            return false;

        blockName = gameObjectName.Substring(prefix.Length, length);
        return blockName.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase);
    }

    static void CleanupDungeonStaticNpcRdbPlacements()
    {
        List<StaticNPC> deadDiagnostics = null;
        foreach (KeyValuePair<StaticNPC, string> pair in dungeonStaticNpcDiagnostics)
        {
            if (pair.Key)
                continue;
            if (deadDiagnostics == null)
                deadDiagnostics = new List<StaticNPC>();
            deadDiagnostics.Add(pair.Key);
        }
        if (deadDiagnostics != null)
            foreach (StaticNPC npc in deadDiagnostics)
                dungeonStaticNpcDiagnostics.Remove(npc);

        List<int> dead = null;
        foreach (KeyValuePair<int, DungeonStaticNpcRdbPlacement> pair in dungeonStaticNpcRdbPlacements)
        {
            DungeonStaticNpcRdbPlacement placement = pair.Value;
            if (placement != null && placement.npc && placement.dungeon)
                continue;

            if (dead == null)
                dead = new List<int>();
            dead.Add(pair.Key);
        }

        if (dead == null)
            return;

        for (int i = 0; i < dead.Count; i++)
        {
            dungeonStaticNpcRdbPlacements.Remove(dead[i]);
        }
    }

    static void LogDungeonNpcStatus(StaticNPC npc, string status, DungeonStaticNpcRdbPlacement placement)
    {
        string previous;
        if (dungeonStaticNpcDiagnostics.TryGetValue(npc, out previous) && previous == status)
            return;
        dungeonStaticNpcDiagnostics[npc] = status;

        string components = "";
        foreach (MonoBehaviour behaviour in npc.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour)
                components += behaviour.GetType().FullName + ";";

        string meshes = "";
        foreach (MeshFilter filter in npc.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh)
                meshes += filter.name + "(local=" + filter.transform.localPosition.ToString("F3") +
                    ", meshCentre=" + filter.sharedMesh.bounds.center.ToString("F3") +
                    ", meshSize=" + filter.sharedMesh.bounds.size.ToString("F3") + ");";

        Debug.Log(LogPrefix + "[NPC-height-v6] " + status + " '" + npc.name +
            "' context=" + npc.Data.context +
            ", flat=" + npc.Data.billboardArchiveIndex + "." + npc.Data.billboardRecordIndex +
            ", hash=" + npc.Data.hash + ", local=" + npc.transform.localPosition.ToString("F3") +
            ", world=" + npc.transform.position.ToString("F3") +
            (placement != null ? ", authoredLocalY=" + placement.authoredLocalY.ToString("F3") : "") +
            ", components=" + components + ", meshes=" + meshes);
    }

    static bool HasCustomDungeonStaticNpcVisual(StaticNPC npc, out string customVisualType)
    {
        customVisualType = null;
        if (!npc)
            return false;

        // A replacement suffix alone also identifies unrelated 3D NPC mods, whose base
        // pivot is intentional. Only apply the centre-height repair to Animated People.
        MonoBehaviour[] behaviours = npc.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (!behaviour || behaviour == npc)
                continue;

            string fullName = behaviour.GetType().FullName ?? behaviour.GetType().Name;
            if (fullName.IndexOf("AnimatedPeople", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf("AnimatedPerson", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                customVisualType = fullName;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps MobileNpcSync's manually-created remote ghosts bound to the custom visual asset,
    /// then makes Villager Variety's additional random clothing selection deterministic on
    /// both the owner NPC and every remote ghost. Ordinary unsynchronized civilians are not
    /// rerolled, and this method is never entered outside multiplayer.
    /// </summary>
    static void ReconcileCivilianVisuals(ref bool villagerClothingSyncLogged)
    {
        RepairRemoteSoftSyncCivilianVisuals();
        ReconcileDeterministicVillagerClothing(ref villagerClothingSyncLogged);
    }

    /// <summary>
    /// Existing v3 remote-ghost repair. Keep this independent from clothing synchronization.
    /// </summary>
    static void RepairRemoteSoftSyncCivilianVisuals()
    {
        MobilePersonNPC[] npcs = GameObject.FindObjectsOfType<MobilePersonNPC>();
        if (npcs == null || npcs.Length == 0)
            return;

        for (int i = 0; i < npcs.Length; i++)
        {
            MobilePersonNPC npc = npcs[i];
            if (!npc || !IsRemoteSoftSyncNpc(npc))
                continue;

            MobilePersonAsset canonicalAsset = GetCanonicalPersonAsset(npc);
            if (!canonicalAsset)
                continue;

            if (npc.Asset != canonicalAsset)
                npc.Asset = canonicalAsset;

            GameObject visualObject = canonicalAsset.gameObject;
            if (visualObject && !visualObject.activeSelf)
                visualObject.SetActive(true);

            Renderer[] renderers = canonicalAsset.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer && !renderer.enabled)
                    renderer.enabled = true;
            }
        }
    }

    /// <summary>
    /// MobileNpcSync already has a stable cross-client NPC identity. Read those private records
    /// rather than adding another network field just for a cosmetic mod-private random choice.
    /// </summary>
    static void ReconcileDeterministicVillagerClothing(ref bool villagerClothingSyncLogged)
    {
        if (!EnsureNpcReflection())
            return;

        MobileNpcSync localSync = mobileNpcLocalInstanceField.GetValue(null) as MobileNpcSync;

        // Owner-side civilians. These are ordinary PopulationManager objects, but only objects
        // already registered in MobileNpcSync are touched here.
        if (localSync)
        {
            IDictionary localRecords = mobileNpcLocalRecordsField.GetValue(localSync) as IDictionary;
            if (localRecords != null)
            {
                string ownerPlayerId = GetLocalNpcOwnerPlayerId(localSync);

                foreach (DictionaryEntry entry in localRecords)
                {
                    object record = entry.Value;
                    if (record == null)
                        continue;

                    MobilePersonNPC npc = localRecordNpcField.GetValue(record) as MobilePersonNPC;
                    int npcId = GetRecordInt(localRecordNpcIdField, record, -1);
                    string locationKey = localRecordLocationKeyField.GetValue(record) as string;

                    TryApplyDeterministicVillagerClothing(
                        npc,
                        ownerPlayerId,
                        locationKey,
                        npcId,
                        ref villagerClothingSyncLogged);
                }
            }
        }

        // Receiver-side ghosts. The record contains the owner's exact player id, location key,
        // and npc id from the spawn packet, so it reconstructs the same seed as the owner.
        IDictionary remoteGhosts = mobileNpcRemoteGhostsField.GetValue(null) as IDictionary;
        if (remoteGhosts == null)
            return;

        foreach (DictionaryEntry entry in remoteGhosts)
        {
            object record = entry.Value;
            if (record == null)
                continue;

            MobilePersonNPC npc = remoteRecordNpcField.GetValue(record) as MobilePersonNPC;
            int npcId = GetRecordInt(remoteRecordNpcIdField, record, -1);
            string locationKey = remoteRecordLocationKeyField.GetValue(record) as string;
            string ownerPlayerId = remoteRecordOwnerPlayerIdField.GetValue(record) as string;

            TryApplyDeterministicVillagerClothing(
                npc,
                ownerPlayerId,
                locationKey,
                npcId,
                ref villagerClothingSyncLogged);
        }
    }

    static bool EnsureNpcReflection()
    {
        if (npcReflectionInitialized)
            return npcReflectionAvailable;

        npcReflectionInitialized = true;

        Type syncType = typeof(MobileNpcSync);
        Type localRecordType = syncType.GetNestedType("LocalRecord", BindingFlags.NonPublic);
        Type remoteRecordType = syncType.GetNestedType("RemoteRecord", BindingFlags.NonPublic);

        mobileNpcLocalInstanceField = syncType.GetField("localInstance", StaticPrivate);
        mobileNpcLocalRecordsField = syncType.GetField("localRecordsByNpc", InstanceAny);
        mobileNpcRemoteGhostsField = syncType.GetField("remoteGhosts", StaticPrivate);

        if (localRecordType != null)
        {
            localRecordNpcField = localRecordType.GetField("npc", InstanceAny);
            localRecordNpcIdField = localRecordType.GetField("npcId", InstanceAny);
            localRecordLocationKeyField = localRecordType.GetField("locationKey", InstanceAny);
        }

        if (remoteRecordType != null)
        {
            remoteRecordNpcField = remoteRecordType.GetField("npc", InstanceAny);
            remoteRecordNpcIdField = remoteRecordType.GetField("npcId", InstanceAny);
            remoteRecordLocationKeyField = remoteRecordType.GetField("locationKey", InstanceAny);
            remoteRecordOwnerPlayerIdField = remoteRecordType.GetField("ownerPlayerId", InstanceAny);
        }

        npcReflectionAvailable =
            mobileNpcLocalInstanceField != null &&
            mobileNpcLocalRecordsField != null &&
            mobileNpcRemoteGhostsField != null &&
            localRecordNpcField != null &&
            localRecordNpcIdField != null &&
            localRecordLocationKeyField != null &&
            remoteRecordNpcField != null &&
            remoteRecordNpcIdField != null &&
            remoteRecordLocationKeyField != null &&
            remoteRecordOwnerPlayerIdField != null;

        if (!npcReflectionAvailable && !npcReflectionWarningLogged)
        {
            npcReflectionWarningLogged = true;
            Debug.LogWarning(
                LogPrefix +
                "Could not read MobileNpcSync identity records. VE civilian clothing sync is disabled, " +
                "but the player animation bridge and remote civilian visual repair remain active.");
        }

        return npcReflectionAvailable;
    }

    static int GetRecordInt(FieldInfo field, object record, int fallback)
    {
        if (field == null || record == null)
            return fallback;

        object value = field.GetValue(record);
        return value is int ? (int)value : fallback;
    }

    static string GetLocalNpcOwnerPlayerId(MobileNpcSync localSync)
    {
        if (localSync && localSync.netId != 0)
            return localSync.netId.ToString();

        return PlayerMultiplayer.id ?? string.Empty;
    }

    static MobilePersonAsset GetCanonicalPersonAsset(MobilePersonNPC npc)
    {
        if (!npc)
            return null;

        MobilePersonMotor motor = npc.Motor;
        if (!motor)
            motor = npc.GetComponent<MobilePersonMotor>();

        if (motor && motor.MobileAsset)
            return motor.MobileAsset;

        return npc.Asset;
    }

    static bool IsVillagerVarietyAsset(MobilePersonAsset asset)
    {
        if (!asset)
            return false;

        Type type = asset.GetType();
        string fullName = type.FullName ?? type.Name;

        // Runtime component detection is more reliable than a mod-title string and means this
        // code is inert when Villager Variety / the VE civilian replacement is not actually used.
        return fullName.StartsWith("VillagerVariety.", StringComparison.Ordinal) ||
               fullName.IndexOf(
                   "VillagerVarietyMobilePerson",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void TryApplyDeterministicVillagerClothing(
        MobilePersonNPC npc,
        string ownerPlayerId,
        string locationKey,
        int npcId,
        ref bool villagerClothingSyncLogged)
    {
        if (!npc || npcId < 0 || string.IsNullOrEmpty(ownerPlayerId) || string.IsNullOrEmpty(locationKey))
            return;

        MobilePersonAsset asset = GetCanonicalPersonAsset(npc);
        if (!IsVillagerVarietyAsset(asset))
            return;

        if (npc.Asset != asset)
            npc.Asset = asset;

        int faceVariant = npc.GetPersonFaceVariant();
        int seed = BuildStableNpcSeed(ownerPlayerId, locationKey, npcId);
        int signature = BuildNpcAppearanceSignature(npc, asset, seed, faceVariant);
        int instanceId = npc.GetInstanceID();

        int previousSignature;
        if (deterministicNpcAppearanceSignatures.TryGetValue(instanceId, out previousSignature) &&
            previousSignature == signature)
            return;

        bool wasIdle = asset.IsIdle;
        UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;

        try
        {
            UnityEngine.Random.InitState(seed);

            // MobilePersonNPC.ApplySyncedPerson() reuses the already-synchronized DFU identity
            // and calls the active MobilePersonAsset.SetPerson(). In Villager Variety that reaches
            // its additional Random.Range(NUM_VARIANTS) clothing choice. Because every peer enters
            // with the same seed, that otherwise-local choice is now identical everywhere.
            //
            // The climate-variant implementation intentionally ignores its first SetPerson call.
            // If this particular custom asset has not consumed that call yet, consume it here and
            // then re-seed before the real visual call so owner and receivers still choose exactly
            // the same variant.
            if (VillagerAssetNeedsClimateWarmup(asset))
            {
                ApplyCurrentSyncedPerson(npc, faceVariant);
                UnityEngine.Random.InitState(seed);
            }

            ApplyCurrentSyncedPerson(npc, faceVariant);

            // Villager Variety's SetPerson() resets its visual animation to Move. Preserve the
            // actual synchronized/owner idle presentation while only changing clothing.
            if (asset)
                asset.IsIdle = wasIdle;

            deterministicNpcAppearanceSignatures[instanceId] = signature;

            if (!villagerClothingSyncLogged)
            {
                villagerClothingSyncLogged = true;
                Debug.Log(
                    LogPrefix +
                    "Deterministic Villager Variety clothing sync active for MobileNpcSync civilians.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                LogPrefix + "Could not apply deterministic VE civilian clothing to '" +
                npc.gameObject.name + "': " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            UnityEngine.Random.state = previousRandomState;

            if (asset)
                asset.IsIdle = wasIdle;
        }
    }

    static void ApplyCurrentSyncedPerson(MobilePersonNPC npc, int faceVariant)
    {
        npc.ApplySyncedPerson(
            npc.Race,
            npc.Gender,
            npc.PersonOutfitVariant,
            npc.IsGuard,
            faceVariant,
            npc.PersonFaceRecordId,
            npc.NameNPC);
    }

    static bool VillagerAssetNeedsClimateWarmup(MobilePersonAsset asset)
    {
        if (!asset)
            return false;

        Type assetType = asset.GetType();
        FieldInfo skippedFirstTextureField =
            assetType.GetField("skippedFirstTexture", InstanceAny);

        if (skippedFirstTextureField == null || skippedFirstTextureField.FieldType != typeof(bool))
            return false;

        bool alreadySkipped = (bool)skippedFirstTextureField.GetValue(asset);
        if (alreadySkipped)
            return false;

        // The custom asset skips the first call only when its climate variant is non-empty.
        // Query that exact mod method through reflection so this helper has no compile-time
        // dependency on Villager Variety and remains inert when that mod is absent.
        Type modType = assetType.Assembly.GetType("VillagerVariety.VillagerVarietyMod");
        if (modType == null)
            return false;

        MethodInfo climateMethod = modType.GetMethod(
            "GetClimateVariant",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (climateMethod == null || climateMethod.ReturnType != typeof(string))
            return false;

        string climateVariant = climateMethod.Invoke(null, null) as string;
        return !string.IsNullOrEmpty(climateVariant);
    }

    static int BuildStableNpcSeed(string ownerPlayerId, string locationKey, int npcId)
    {
        unchecked
        {
            uint hash = 2166136261u;
            AddStableString(ref hash, ownerPlayerId);
            AddStableByte(ref hash, 0xff);
            AddStableString(ref hash, locationKey);
            AddStableByte(ref hash, 0xfe);
            AddStableInt(ref hash, npcId);

            int seed = (int)(hash & 0x7fffffff);
            return seed == 0 ? 1 : seed;
        }
    }

    static int BuildNpcAppearanceSignature(
        MobilePersonNPC npc,
        MobilePersonAsset asset,
        int seed,
        int faceVariant)
    {
        unchecked
        {
            uint hash = (uint)seed;
            AddStableInt(ref hash, (int)npc.Race);
            AddStableInt(ref hash, (int)npc.Gender);
            AddStableInt(ref hash, npc.PersonOutfitVariant);
            AddStableInt(ref hash, npc.IsGuard ? 1 : 0);
            AddStableInt(ref hash, faceVariant);
            AddStableInt(ref hash, npc.PersonFaceRecordId);

            // Local cache only: if DFU/mod replaces this visual component in-place, force one
            // fresh deterministic setup even though the synchronized NPC identity did not change.
            AddStableInt(ref hash, asset ? asset.GetInstanceID() : 0);

            if (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.WorldTime != null)
                AddStableInt(ref hash, (int)DaggerfallUnity.Instance.WorldTime.Now.SeasonValue);

            return (int)hash;
        }
    }

    static void AddStableString(ref uint hash, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        unchecked
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                AddStableByte(ref hash, (byte)(c & 0xff));
                AddStableByte(ref hash, (byte)((c >> 8) & 0xff));
            }
        }
    }

    static void AddStableInt(ref uint hash, int value)
    {
        unchecked
        {
            AddStableByte(ref hash, (byte)(value & 0xff));
            AddStableByte(ref hash, (byte)((value >> 8) & 0xff));
            AddStableByte(ref hash, (byte)((value >> 16) & 0xff));
            AddStableByte(ref hash, (byte)((value >> 24) & 0xff));
        }
    }

    static void AddStableByte(ref uint hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 16777619u;
        }
    }

    static bool IsRemoteSoftSyncNpc(MobilePersonNPC npc)
    {
        if (!npc)
            return false;

        GameObject go = npc.gameObject;
        if (!go)
            return false;

        return go.name.StartsWith(RemoteNpcNamePrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// Concrete DaggerfallMobileUnit adapter required only because SpriteMultiplayer.sprite is
/// hard-typed as DaggerfallMobileUnit while Vanilla Enhanced can replace DFU's mobile with a
/// different MobileUnit implementation (for example VillainVarietyMobileUnit).
///
/// This component is intentionally disabled. MobileUnit.ChangeEnemyState() and SetEnemy() are
/// still callable while disabled; their virtual implementation hooks below forward the request to
/// DaggerfallEnemy.MobileUnit, which is resolved again on every call so VE can replace it at any
/// point during profile setup without leaving a stale bridge target.
/// </summary>
[DefaultExecutionOrder(-9999)]
public sealed class VanillaEnhancedMultiplayerMobileProxy : DaggerfallMobileUnit
{
    NetworkIdentity identity;
    SpriteMultiplayer owner;
    MobileUnit lastResolvedTarget;
    float nextWarningRealtime;

    internal void Bind(NetworkIdentity newIdentity, SpriteMultiplayer newOwner)
    {
        identity = newIdentity;
        owner = newOwner;

        // Never let inherited DaggerfallMobileUnit.Update()/AnimateEnemy() run on the proxy.
        if (enabled)
            enabled = false;

        ResolveTarget();
    }

    void OnEnable()
    {
        // SpriteMultiplayer deliberately enables its sprite again on revive. For the adapter this
        // must be a no-op: the actual VE MobileUnit stays enabled and owns the animation loop.
        enabled = false;
    }

    MobileUnit ResolveTarget()
    {
        DaggerfallEnemy enemy =
            VanillaEnhancedMultiplayerCompat.FindPlayerDaggerfallEnemy(identity, owner, this);

        MobileUnit current = enemy ? enemy.MobileUnit : null;
        if (current == this)
            current = null;

        if (current != null && current != lastResolvedTarget)
        {
            lastResolvedTarget = current;
            Debug.Log(
                VanillaEnhancedMultiplayerCompat.LogPrefix +
                "MP player proxy target is now '" + current.GetType().FullName +
                "' on '" + current.gameObject.name + "'.");
        }

        return lastResolvedTarget;
    }

    public override bool IsSetup
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.IsSetup : base.IsSetup;
        }
        protected set { base.IsSetup = value; }
    }

    public override MobileEnemy Enemy
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.Enemy : base.Enemy;
        }
        protected set { base.Enemy = value; }
    }

    public override MobileStates EnemyState
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.EnemyState : base.EnemyState;
        }
        protected set { base.EnemyState = value; }
    }

    public override byte ClassicSpawnDistanceType
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.ClassicSpawnDistanceType : base.ClassicSpawnDistanceType;
        }
        protected set { base.ClassicSpawnDistanceType = value; }
    }

    public override bool SpecialTransformationCompleted
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.SpecialTransformationCompleted : base.SpecialTransformationCompleted;
        }
        protected set { base.SpecialTransformationCompleted = value; }
    }

    public override bool IsBackFacing
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.IsBackFacing : base.IsBackFacing;
        }
    }

    public override bool DoMeleeDamage
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.DoMeleeDamage : base.DoMeleeDamage;
        }
        set
        {
            base.DoMeleeDamage = value;
            MobileUnit target = ResolveTarget();
            if (target != null)
                target.DoMeleeDamage = value;
        }
    }

    public override bool ShootArrow
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.ShootArrow : base.ShootArrow;
        }
        set
        {
            base.ShootArrow = value;
            MobileUnit target = ResolveTarget();
            if (target != null)
                target.ShootArrow = value;
        }
    }

    public override bool FreezeAnims
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.FreezeAnims : base.FreezeAnims;
        }
        set
        {
            base.FreezeAnims = value;
            MobileUnit target = ResolveTarget();
            if (target != null)
                target.FreezeAnims = value;
        }
    }

    public override int FrameSpeedDivisor
    {
        get
        {
            MobileUnit target = ResolveTarget();
            return target != null ? target.FrameSpeedDivisor : base.FrameSpeedDivisor;
        }
        set
        {
            base.FrameSpeedDivisor = value;
            MobileUnit target = ResolveTarget();
            if (target != null)
                target.FrameSpeedDivisor = value;
        }
    }

    public override Vector3 GetSize()
    {
        MobileUnit target = ResolveTarget();
        if (target != null && target.IsSetup)
        {
            try
            {
                return target.GetSize();
            }
            catch (Exception ex)
            {
                Warn("GetSize", ex);
            }
        }

        return Vector3.zero;
    }

    /// <summary>
    /// Called by MobileUnit.SetEnemy() on this proxy. Forward the complete setup to VE's current
    /// MobileUnit instead of executing DaggerfallMobileUnit.AssignMeshAndMaterial() on the proxy.
    /// This also keeps mounted/lycanthrope SpriteMultiplayer paths on the VE visual.
    /// </summary>
    protected override void ApplyEnemy(DaggerfallUnity dfUnity)
    {
        MobileUnit target = ResolveTarget();
        if (target == null || target == this)
            return;

        try
        {
            // SetEnemy() already copied these values into the proxy summary before reaching here.
            MobileEnemy enemy = base.Enemy;
            target.SetEnemy(
                dfUnity,
                enemy,
                enemy.Reactions,
                base.ClassicSpawnDistanceType);
        }
        catch (Exception ex)
        {
            Warn("SetEnemy forwarding", ex);
        }
    }

    /// <summary>
    /// This is the key bridge. MobileUnit.ChangeEnemyState() is non-virtual, but it calls this
    /// virtual method after assigning the requested state. Therefore every existing
    /// SpriteMultiplayer Idle/Move/Attack/Hurt/Bow call is forwarded synchronously without
    /// modifying SpriteMultiplayer or Mirror.
    /// </summary>
    protected override void ApplyEnemyStateChange(
        MobileStates currentState,
        MobileStates newState)
    {
        MobileUnit target = ResolveTarget();
        if (target == null || target == this)
            return;

        try
        {
            target.ChangeEnemyState(newState);
        }
        catch (Exception ex)
        {
            // These are remote-player presentation calls. Never let an incompatible visual
            // replacement throw out through a Mirror RPC and disconnect the peer.
            Warn("state " + newState + " forwarding", ex);
        }
    }

    void Warn(string operation, Exception ex)
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextWarningRealtime)
            return;

        nextWarningRealtime = now + 2f;
        Debug.LogWarning(
            VanillaEnhancedMultiplayerCompat.LogPrefix +
            "VE MP proxy failed during " + operation + ": " +
            ex.GetType().Name + ": " + ex.Message);
    }
}
