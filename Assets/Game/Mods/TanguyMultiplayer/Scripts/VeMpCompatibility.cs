// Multiplayer compatibility for Vanilla Enhanced by carademono and contributors.
// Implemented against the publicly available Vanilla Enhanced source code.
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
/// Villager Variety civilian clothing is also made deterministic only for civilians already
/// tracked by MobileNpcSync; its extra local random clothing roll is not added to the network.
/// Ordinary single-player actors, unsynchronized civilians, enemy authority, movement,
/// networking, collision, and gameplay state are not modified.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class VanillaEnhancedMultiplayerCompat : MonoBehaviour
{
    internal const string LogPrefix = "[VanillaEnhancedMultiplayerCompat] ";

    const string RemoteNpcNamePrefix = "MobileNPC_RemoteSoftSync";
    const float RemoteNpcScanInterval = 0.25f;

    static VanillaEnhancedMultiplayerCompat instance;
    static readonly HashSet<int> diagnosedPlayers = new HashSet<int>();

    // Villager Variety chooses one extra clothing variant locally inside its custom
    // MobilePersonAsset.SetPerson(). MobileNpcSync already synchronizes the base DFU
    // identity (race/gender/outfit/face/name), but not this mod-private random roll.
    // Cache the deterministic re-application per live NPC so this stays a one-time
    // cosmetic correction rather than doing texture work every 0.25 seconds.
    static readonly Dictionary<int, int> deterministicNpcAppearanceSignatures =
        new Dictionary<int, int>();

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

    bool activationLogged;
    bool playerDiscoveryLogged;
    bool villagerClothingSyncLogged;
    float nextRemoteNpcScanRealtime;

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
                "Active in multiplayer. Scanning PlayerMultiplayer visuals for a custom MobileUnit replacement.");
        }

        ReconcileMultiplayerPlayerVisuals(ref playerDiscoveryLogged);

        float now = Time.realtimeSinceStartup;
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
