// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    Lypyl (lypyl@dfworkshop.net)
// 
// Notes:
//

#define KEEP_PREFAB_LINKS

using UnityEngine;
using System;
using System.Collections.Generic;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;
using Mirror;
using System.Collections;	
using System.Reflection;

namespace DaggerfallWorkshop.Utility
{
    /// <summary>
    /// Static helper methods to instantiate common types of Daggerfall gameobjects.
    /// </summary>
    public static class GameObjectHelper
    {
        static Dictionary<int, MobileEnemy> enemyDict;
        public static Dictionary<int, MobileEnemy> EnemyDict
        {
            get
            {
                if (enemyDict == null)
                    enemyDict = EnemyBasics.BuildEnemyDict();
                return enemyDict;
            }
        }


        // Multiplayer dungeon enemy import level override.
        // RDBLayout's classic random enemy selection still reads GameManager.Instance.PlayerEntity.Level
        // directly while building its 256-entry dungeon enemy tables. Passing monsterPower is not enough
        // for the classic path. For a client-requested dungeon, temporarily make that read return the
        // requester level while RDBLayout.AddRandomEnemies/AddFixedEnemies instantiate enemies, then restore
        // the host player's real level immediately afterwards.
        struct PlayerEntityLevelOverrideState
        {
            public bool Applied;
            public PlayerEntity Player;
            public FieldInfo LevelField;
            public int PreviousLevel;
        }

        static PlayerEntityLevelOverrideState PushPlayerEntityLevelOverrideForDungeonEnemyImport(int spawnScalingLevel)
        {
            PlayerEntityLevelOverrideState state = new PlayerEntityLevelOverrideState();

            if (!NetworkServer.active || spawnScalingLevel <= 0)
                return state;

            if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
                return state;

            FieldInfo levelField = typeof(DaggerfallEntity).GetField("level", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (levelField == null)
            {
                Debug.LogWarning("[DungeonEnemyLevelContext] Could not find DaggerfallEntity.level field. RDBLayout classic enemy selection may still use host level.");
                return state;
            }

            try
            {
                PlayerEntity player = GameManager.Instance.PlayerEntity;
                int previousLevel = Mathf.Clamp(player.Level, 1, 100);
                int overrideLevel = Mathf.Clamp(spawnScalingLevel, 1, 100);

                state.Applied = true;
                state.Player = player;
                state.LevelField = levelField;
                state.PreviousLevel = previousLevel;

                levelField.SetValue(player, overrideLevel);
                Debug.Log($"[DungeonEnemyLevelContext] Temporarily overriding host PlayerEntity.Level during RDB enemy import. previous={previousLevel}, override={overrideLevel}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DungeonEnemyLevelContext] Failed to override PlayerEntity.Level during RDB enemy import: {ex.Message}");
                state.Applied = false;
            }

            return state;
        }

        static void PopPlayerEntityLevelOverrideForDungeonEnemyImport(PlayerEntityLevelOverrideState state)
        {
            if (!state.Applied || state.Player == null || state.LevelField == null)
                return;

            try
            {
                state.LevelField.SetValue(state.Player, state.PreviousLevel);
                Debug.Log($"[DungeonEnemyLevelContext] Restored host PlayerEntity.Level after RDB enemy import. restored={state.PreviousLevel}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DungeonEnemyLevelContext] Failed to restore PlayerEntity.Level after RDB enemy import: {ex.Message}");
            }
        }

        // Animal sounds range. Matched to classic.
        const float animalSoundMaxDistance = 768 * MeshReader.GlobalScale;

        public static void AddAnimalAudioSource(GameObject go, int record)
        {
            DaggerfallAudioSource source = go.AddComponent<DaggerfallAudioSource>();
            source.AudioSource.maxDistance = animalSoundMaxDistance;

            SoundClips sound;
            switch (record)
            {
                case 0:
                case 1:
                    sound = SoundClips.AnimalHorse;
                    break;
                case 3:
                case 4:
                    sound = SoundClips.AnimalCow;
                    break;
                case 5:
                case 6:
                    sound = SoundClips.AnimalPig;
                    break;
                case 7:
                case 8:
                    sound = SoundClips.AnimalCat;
                    break;
                case 9:
                case 10:
                    sound = SoundClips.AnimalDog;
                    break;
                default:
                    sound = SoundClips.None;
                    break;
            }

            source.SetSound(sound, AudioPresets.PlayRandomlyIfPlayerNear);
        }

        public static void AssignAnimatedMaterialComponent(CachedMaterial[] cachedMaterials, GameObject go)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            // Look for any animated textures in this material set
            for (int i = 0; i < cachedMaterials.Length; i++)
            {
                CachedMaterial cm = cachedMaterials[i];
                int frameCount = cm.singleFrameCount;
                if (frameCount > 1)
                {
                    // Add texture animation component
                    AnimatedMaterial c = go.AddComponent<AnimatedMaterial>();

                    // Store material for each frame
                    CachedMaterial[] materials = new CachedMaterial[frameCount];
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        int archiveOut, recordOut, frameOut;
                        MaterialReader.ReverseTextureKey(cm.key, out archiveOut, out recordOut, out frameOut, cm.keyGroup);
                        dfUnity.MaterialReader.GetCachedMaterial(archiveOut, recordOut, frame, out materials[frame]);
                    }

                    // Assign animation properties
                    c.TargetMaterial = cm.material;
                    c.AnimationFrames = materials;
                    if (cm.framesPerSecond > 0)
                        c.FramesPerSecond = cm.framesPerSecond;
                }
            }
        }

        public static Material[] GetMaterialArray(CachedMaterial[] cachedMaterials)
        {
            // Extract a material array from cached material array
            Material[] materials = new Material[cachedMaterials.Length];
            for (int i = 0; i < cachedMaterials.Length; i++)
            {
                materials[i] = cachedMaterials[i].material;
            }

            return materials;
        }

        public static string GetGoModelName(uint modelID)
        {
            return string.Format("DaggerfallMesh [ID={0}]", modelID);
        }

        public static string GetGoFlatName(int textureArchive, int textureRecord)
        {
            return string.Format("DaggerfallBillboard [TEXTURE.{0:000}, Index={1}]", textureArchive, textureRecord);
        }

        /// <summary>
        /// Adds a single DaggerfallMesh game object to scene.
        /// </summary>
        /// <param name="modelID">ModelID of mesh to add.</param>
        /// <param name="parent">Optional parent of this object.</param>
        /// <param name="makeStatic">Flag to set object static flag.</param>
        /// <param name="useExistingObject">Add mesh to existing object rather than create new.</param>
        /// <param name="ignoreCollider">Force disable collider.</param>
        /// <param name="convexCollider">Make collider convex.</param>
        /// <returns>GameObject.</returns>
        public static GameObject CreateDaggerfallMeshGameObject(
            uint modelID,
            Transform parent,
            bool makeStatic = false,
            GameObject useExistingObject = null,
            bool ignoreCollider = false,
            bool convexCollider = false)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            // Create gameobject
            GameObject go = (useExistingObject != null) ? useExistingObject : new GameObject();
            if (parent != null)
                go.transform.parent = parent;
            go.name = GetGoModelName(modelID);

            // Add DaggerfallMesh component
            DaggerfallMesh dfMesh = go.GetComponent<DaggerfallMesh>();
            if (dfMesh == null)
                dfMesh = go.AddComponent<DaggerfallMesh>();

            // Get mesh filter and renderer components
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();

            // Assign mesh and materials
            CachedMaterial[] cachedMaterials;
            int[] textureKeys;
            bool hasAnimations;
            Mesh mesh = dfUnity.MeshReader.GetMesh(
                dfUnity,
                modelID,
                out cachedMaterials,
                out textureKeys,
                out hasAnimations,
                dfUnity.MeshReader.AddMeshTangents,
                dfUnity.MeshReader.AddMeshLightmapUVs);

            // Assign animated materials component if required
            if (hasAnimations)
                AssignAnimatedMaterialComponent(cachedMaterials, go);

            // Assign mesh and materials
            if (mesh)
            {
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterials = GetMaterialArray(cachedMaterials);
                dfMesh.SetDefaultTextures(textureKeys);
            }

            // Assign mesh to collider
            if (dfUnity.Option_AddMeshColliders && !ignoreCollider)
            {
                MeshCollider collider = go.GetComponent<MeshCollider>();
                if (collider == null) collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;

                // Enable convex collider if specified
                if (convexCollider)
                    collider.convex = true;
            }

            // Assign static
            if (makeStatic)
                TagStaticGeometry(go);

            return go;
        }

        // TEMP: Changes a Daggerfall mesh to another ID
        // This will eventually be integrated with a future self-assembling mesh prefab
        public static void ChangeDaggerfallMeshGameObject(DaggerfallMesh dfMesh, uint newModelID)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            // Get new mesh
            CachedMaterial[] cachedMaterials;
            int[] textureKeys;
            bool hasAnimations;
            Mesh mesh = dfUnity.MeshReader.GetMesh(
                dfUnity,
                newModelID,
                out cachedMaterials,
                out textureKeys,
                out hasAnimations,
                dfUnity.MeshReader.AddMeshTangents,
                dfUnity.MeshReader.AddMeshLightmapUVs);

            // Get mesh filter and renderer components
            MeshFilter meshFilter = dfMesh.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = dfMesh.GetComponent<MeshRenderer>();

            // Update mesh
            if (mesh && meshFilter && meshRenderer)
            {
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterials = GetMaterialArray(cachedMaterials);
            }

            // Update collider
            MeshCollider collider = dfMesh.GetComponent<MeshCollider>();
            {
                collider.sharedMesh = mesh;
            }

            // Update name
            dfMesh.name = GetGoModelName(newModelID);
        }

        public static GameObject CreateCombinedMeshGameObject(
            ModelCombiner combiner,
            string meshName,
            Transform parent,
            bool makeStatic = false)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            // Create gameobject
            GameObject go = new GameObject(meshName);
            if (parent)
                go.transform.parent = parent;

            // Assign components
            DaggerfallMesh dfMesh = go.AddComponent<DaggerfallMesh>();
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();

            // Assign mesh and materials
            CachedMaterial[] cachedMaterials;
            int[] textureKeys;
            bool hasAnimations;
            Mesh mesh = dfUnity.MeshReader.GetCombinedMesh(
                dfUnity,
                combiner,
                out cachedMaterials,
                out textureKeys,
                out hasAnimations,
                dfUnity.MeshReader.AddMeshTangents,
                dfUnity.MeshReader.AddMeshLightmapUVs);

            // Assign animated materials component if required
            if (hasAnimations)
                AssignAnimatedMaterialComponent(cachedMaterials, go);

            // Assign mesh and materials array
            if (mesh)
            {
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterials = GetMaterialArray(cachedMaterials);
                dfMesh.SetDefaultTextures(textureKeys);
            }

            // Assign collider
            if (dfUnity.Option_AddMeshColliders)
            {
                MeshCollider collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
            }

            // Assign static
            if (makeStatic)
                TagStaticGeometry(go);

            return go;
        }

        public static GameObject CreateDaggerfallBillboardGameObject(int archive, int record, Transform parent)
        {
            string flatName = GetGoFlatName(archive, record);
            GameObject go = new GameObject(flatName);
            if (parent) go.transform.parent = parent;

            Billboard dfBillboard = go.AddComponent<DaggerfallBillboard>();
            dfBillboard.SetMaterial(archive, record);

            if (PlayerActivate.HasCustomActivation(flatName)) 
            {
                // Add box collider to flats with actions for raycasting - only flats that can be activated directly need this, so this can possibly be restricted in future
                // Skip this for flats that already have a collider assigned from elsewhere (e.g. NPC flats)
                if (!go.GetComponent<Collider>())
                {
                    Collider col = go.AddComponent<BoxCollider>();
                    col.isTrigger = true;
                }
            }

            return go;
        }

        public static void AlignBillboardToGround(GameObject go, Vector2 size, float distance = 2f)
        {
            // Cast ray down to find ground below
            RaycastHit hit;
            Ray ray = new Ray(go.transform.position + new Vector3(0, 0.2f, 0), Vector3.down);
            if (!Physics.Raycast(ray, out hit, distance))
                return;

            // Position bottom just above ground by adjusting parent gameobject
            go.transform.position = new Vector3(hit.point.x, hit.point.y + size.y * 0.52f, hit.point.z);
        }

        public static void AlignControllerToGround(CharacterController controller, float distance = 3f)
        {
            // Exit if no controller specified
            if (controller == null)
                return;

            // Cast ray down from slightly above midpoint to find ground below
            RaycastHit hit;
            Ray ray = new Ray(controller.transform.position + new Vector3(0, 0.2f, 0), Vector3.down);
            if (!Physics.Raycast(ray, out hit, distance))
                return;

            // Position bottom just above ground by adjusting parent gameobject
            controller.transform.position = new Vector3(hit.point.x, hit.point.y + controller.height * 0.52f, hit.point.z);
        }

        /// <summary>
        /// Instantiate a GameObject from prefab.
        /// </summary>
        /// <param name="prefab">The source GameObject prefab to clone.</param>
        /// <param name="name">Optional name to set. Use string.Empty for default.</param>
        /// <param name="parent">Optional parent to set. Use null for default.</param>
        /// <param name="position">Optional position to set. Use Vector3.zero for default.</param>
        /// <returns>GameObject.</returns>
        public static GameObject InstantiatePrefab(GameObject prefab, string name, Transform parent, Vector3 position)
        {
            GameObject go = null;

#if UNITY_EDITOR && KEEP_PREFAB_LINKS
            if (prefab != null)
            {
                //go = GameObject.Instantiate(prefab);
                go = UnityEditor.PrefabUtility.InstantiatePrefab(prefab as GameObject) as GameObject;
                if (!string.IsNullOrEmpty(name)) go.name = name;
                if (parent != null) go.transform.parent = parent;
                go.transform.position = position;
            }
#else
            if (prefab != null)
            {
                go = GameObject.Instantiate(prefab);
                if (!string.IsNullOrEmpty(name)) go.name = name;
                if (parent != null) go.transform.parent = parent;
                go.transform.position = position;
            }
#endif

            return go;
        }

        /// <summary>
        /// Gets best parent for an object at spawn time.
        /// Objects should always be placed to some child object in world rather than directly into root of scene.
        /// </summary>
        /// <returns>Best parent transform, or null as fallback.</returns>
        public static Transform GetBestParent()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;

            // Place in world near player depending on local area
            if (playerEnterExit.IsPlayerInsideBuilding)
            {
                return playerEnterExit.Interior.transform;
            }
            else if (playerEnterExit.IsPlayerInsideDungeon)
            {
                return playerEnterExit.Dungeon.transform;
            }
            else if (!playerEnterExit.IsPlayerInside && GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
            {
                return GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject.transform;
            }
            else if (!playerEnterExit.IsPlayerInside)
            {
                return GameManager.Instance.StreamingTarget.transform;
            }
            else
            {
                return null;
            }
        }

        public static void TagStaticGeometry(GameObject go)
        {
            if (go)
            {
                go.tag = DaggerfallUnity.staticGeometryTag;
            }
        }

        public static bool IsStaticGeometry(GameObject go)
        {
            if (go)
            {
                return go.CompareTag(DaggerfallUnity.staticGeometryTag);
            }

            return false;
        }

        #region RMB & RDB Block Helpers

        /// <summary>
        /// Layout RMB block gamne object from name only.
        /// This will be missing information like building data and should only be used standalone.
        /// </summary>
        public static GameObject CreateRMBBlockGameObject(
            string blockName,
            int layoutX,
            int layoutY,
            int mapId,
            int locationIndex,
            bool addGroundPlane = true,
            DaggerfallRMBBlock cloneFrom = null,
            DaggerfallBillboardBatch natureBillboardBatch = null,
            DaggerfallBillboardBatch lightsBillboardBatch = null,
            DaggerfallBillboardBatch animalsBillboardBatch = null,
            TextureAtlasBuilder miscBillboardAtlas = null,
            DaggerfallBillboardBatch miscBillboardBatch = null,
            ClimateNatureSets climateNature = ClimateNatureSets.TemperateWoodland,
            ClimateSeason climateSeason = ClimateSeason.Summer)
        {
            // Get block data from name
            DFBlock blockData;
            if (!RMBLayout.GetBlockData(blockName, out blockData))
                return null;

            // Create base object from block data
            GameObject go = CreateRMBBlockGameObject(
                blockData,
                layoutX,
                layoutY,
                mapId,
                locationIndex,
                addGroundPlane,
                cloneFrom,
                natureBillboardBatch,
                lightsBillboardBatch,
                animalsBillboardBatch,
                miscBillboardAtlas,
                miscBillboardBatch,
                climateNature,
                climateSeason);

            return go;
        }

        /// <summary>
        /// Layout RMB block game object from DFBlock data.
        /// </summary>
        public static GameObject CreateRMBBlockGameObject(
            DFBlock blockData,
            int layoutX,
            int layoutY,
            int mapId,
            int locationIndex,
            bool addGroundPlane = true,
            DaggerfallRMBBlock cloneFrom = null,
            DaggerfallBillboardBatch natureBillboardBatch = null,
            DaggerfallBillboardBatch lightsBillboardBatch = null,
            DaggerfallBillboardBatch animalsBillboardBatch = null,
            TextureAtlasBuilder miscBillboardAtlas = null,
            DaggerfallBillboardBatch miscBillboardBatch = null,
            ClimateNatureSets climateNature = ClimateNatureSets.TemperateWoodland,
            ClimateSeason climateSeason = ClimateSeason.Summer)
        {
            // Get DaggerfallUnity
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            if (!dfUnity.IsReady)
                return null;

            // Create base object
            GameObject go = RMBLayout.CreateBaseGameObject(ref blockData, layoutX, layoutY, cloneFrom);

            // Create flats node
            GameObject flatsNode = new GameObject("Flats");
            flatsNode.transform.parent = go.transform;

            // Create lights node
            GameObject lightsNode = new GameObject("Lights");
            lightsNode.transform.parent = go.transform;

            // If billboard batching is enabled but user has not specified
            // a batch, then make our own auto batch for this block
            bool autoLightsBatch = false;
            bool autoNatureBatch = false;
            bool autoAnimalsBatch = false;
            if (dfUnity.Option_BatchBillboards)
            {
                if (natureBillboardBatch == null)
                {
                    autoNatureBatch = true;
                    int natureArchive = ClimateSwaps.GetNatureArchive(climateNature, climateSeason);
                    natureBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(natureArchive, flatsNode.transform);
                }
                if (lightsBillboardBatch == null)
                {
                    autoLightsBatch = true;
                    lightsBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(TextureReader.LightsTextureArchive, flatsNode.transform);
                }
                if (animalsBillboardBatch == null)
                {
                    autoAnimalsBatch = true;
                    animalsBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(TextureReader.AnimalsTextureArchive, flatsNode.transform);
                }
            }

            // Layout light billboards and gameobjects
            RMBLayout.AddLights(ref blockData, flatsNode.transform, lightsNode.transform, lightsBillboardBatch);

            // Layout nature billboards
            RMBLayout.AddNatureFlats(ref blockData, flatsNode.transform, natureBillboardBatch, climateNature, climateSeason);

            // Layout all other flats
            RMBLayout.AddMiscBlockFlats(ref blockData, flatsNode.transform, mapId, locationIndex, animalsBillboardBatch, miscBillboardAtlas, miscBillboardBatch);

            // Layout any subrecord exterior flats
            RMBLayout.AddExteriorBlockFlats(ref blockData, flatsNode.transform, lightsNode.transform, mapId, locationIndex, climateNature, climateSeason);

            // Add ground plane
            if (addGroundPlane)
                RMBLayout.AddGroundPlane(ref blockData, go.transform);

            // Apply auto batches
            if (autoNatureBatch) natureBillboardBatch.Apply();
            if (autoLightsBatch) lightsBillboardBatch.Apply();
            if (autoAnimalsBatch) animalsBillboardBatch.Apply();

            return go;
        }

        /// <summary>
        /// Layout a complete RDB block game object.
        /// </summary>
        /// <param name="blockName">Name of block to create.</param>
        /// <param name="textureTable">Optional texture table for dungeon.</param>
        /// <param name="allowExitDoors">Add exit doors to block (for start blocks).</param>
        /// <param name="dungeonType">Dungeon type for random encounters.</param>
        /// <param name="seed">Seed for random encounters.</param>
        /// <param name="cloneFrom">Clone and build on a prefab object template.</param>
        /// <param name="importEnemies">Import enemies from game data.</param>
public static GameObject CreateRDBBlockGameObject(
    string blockName,
    int[] textureTable = null,
    bool allowExitDoors = true,
    DFRegion.DungeonTypes dungeonType = DFRegion.DungeonTypes.HumanStronghold,
    float monsterPower = 0.5f,
    int monsterVariance = 4,
    int seed = 0,
    DaggerfallRDBBlock cloneFrom = null,
    bool importEnemies = true,
    int spawnScalingLevel = 0)
{
    DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
    if (!dfUnity.IsReady)
    {
        Debug.LogError("[CreateRDBBlockGameObject] DaggerfallUnity is not ready!");
        return null;
    }

    Debug.Log("[CreateRDBBlockGameObject] Creating RDB block...");

    Dictionary<int, RDBLayout.ActionLink> actionLinkDict = new Dictionary<int, RDBLayout.ActionLink>();

    DFBlock blockData;
    GameManager.Instance.PlayerEnterExit.IsCreatingDungeonObjects = true;
    GameObject go = RDBLayout.CreateBaseGameObject(blockName, actionLinkDict, out blockData, textureTable, allowExitDoors, cloneFrom);

    RDBLayout.AddActionDoors(go, actionLinkDict, ref blockData, textureTable);
    RDBLayout.AddLights(go, ref blockData);

    DFBlock.RdbObject[] editorObjects;
    GameObject[] startMarkers;
    GameObject[] enterMarkers;
    RDBLayout.AddFlats(go, actionLinkDict, ref blockData, out editorObjects, out startMarkers, out enterMarkers, dungeonType);

    DaggerfallRDBBlock dfBlock = go.GetComponent<DaggerfallRDBBlock>();
    if (dfBlock != null)
        dfBlock.SetMarkers(startMarkers, enterMarkers);

    Debug.Log("[CreateRDBBlockGameObject] Finished setting up block elements.");

if (importEnemies && (!NetworkClient.active || NetworkServer.active))
{
    DaggerfallDungeon dungeonParent = go.GetComponentInParent<DaggerfallDungeon>();
    GameManager.Instance.StartCoroutine(DelayedEnemySpawn(go, editorObjects, blockData, startMarkers, dungeonType, monsterPower, monsterVariance, seed, dungeonParent, spawnScalingLevel));
}

    RDBLayout.LinkActionNodes(actionLinkDict);
    GameManager.Instance.PlayerEnterExit.IsCreatingDungeonObjects = false;

    Debug.Log("[CreateRDBBlockGameObject] Finished block creation.");

    return go;
}


private static IEnumerator DelayedEnemySpawn(GameObject go, DFBlock.RdbObject[] editorObjects, DFBlock blockData,
    GameObject[] startMarkers, DFRegion.DungeonTypes dungeonType, float monsterPower, int monsterVariance, int seed,
    DaggerfallDungeon dungeonParent, int spawnScalingLevel)
{
    // Wait until the dungeon signals it has finished positioning
    float timeout = 5f;
    float elapsed = 0f;

    while (dungeonParent != null && !dungeonParent.isSet && elapsed < timeout)
    {
        yield return new WaitForSeconds(0.1f);
        elapsed += 0.1f;
    }

    if (dungeonParent != null && !dungeonParent.isSet)
    {
        Debug.LogWarning("[DelayedEnemySpawn] Timeout waiting for dungeon to be initialized. Proceeding anyway.");
    }

    Debug.Log($"[DelayedEnemySpawn] Spawning enemies after dungeon is ready with spawnScalingLevel={spawnScalingLevel}...");

    int previousDungeonSpawnScalingLevel = SetupDemoEnemy.PushDungeonSpawnScalingLevel(spawnScalingLevel);
    PlayerEntityLevelOverrideState playerLevelOverrideState = PushPlayerEntityLevelOverrideForDungeonEnemyImport(spawnScalingLevel);
    try
    {
        RDBLayout.AddFixedEnemies(go, editorObjects, ref blockData, startMarkers);
        RDBLayout.AddRandomEnemies(go, editorObjects, dungeonType, monsterPower, ref blockData, startMarkers, monsterVariance, seed);
    }
    finally
    {
        PopPlayerEntityLevelOverrideForDungeonEnemyImport(playerLevelOverrideState);
        SetupDemoEnemy.RestoreDungeonSpawnScalingLevel(previousDungeonSpawnScalingLevel);
    }

    foreach (var enemy in go.GetComponentsInChildren<DaggerfallEnemy>())
    {
        if (enemy == null) continue;

        // Singleplayer/local dungeon enemies must remain normal children of the dungeon/RDB block.
        // Only multiplayer server-spawned dungeon enemies are moved to scene root and given NetworkIdentity.
        if (!NetworkServer.active)
            continue;

        Transform previousParent = enemy.transform.parent;
        if (previousParent != null)
        {
            GameManager.Instance.StartCoroutine(MoveEnemyToRootAfterDelay(enemy, previousParent, 10));
        }
        else
        {
            enemy.transform.SetParent(null);
            Debug.Log($"[DelayedEnemySpawn] {enemy.name} has no parent, setting to root.");
        }

        if (enemy.GetComponent<NetworkIdentity>() == null)
        {
            enemy.gameObject.AddComponent<NetworkIdentity>();
            Debug.LogWarning($"[DelayedEnemySpawn] Added NetworkIdentity to {enemy.name}");
        }

        if (NetworkServer.active)
        {
            // Dungeon enemies are created by DFU/RDB code, not by the newer CreateFoe wave path.
            // Stamp these SyncVars and capture authoritative HP BEFORE NetworkServer.Spawn(), so
            // build clients do not briefly spawn them with local/default 0 HP before settings arrive.
            global::EnemyWorldPosition enemyWorldPosition = enemy.GetComponent<global::EnemyWorldPosition>();
            SetupDemoEnemy setupDemoEnemy = enemy.GetComponent<SetupDemoEnemy>();
            if (setupDemoEnemy != null)
            {
                setupDemoEnemy.isDungeonEnemy = true;
                if (NetworkServer.active && spawnScalingLevel > 0 && setupDemoEnemy.SpawnScalingLevel <= 0)
                    setupDemoEnemy.SpawnScalingLevel = Mathf.Clamp(spawnScalingLevel, 1, 100);

                if (enemyWorldPosition != null && dungeonParent != null)
                {
                    uint dungeonRequesterNetId = dungeonParent.RequesterNetId;
                    int dungeonAnchorWorldX = dungeonParent.DungeonAnchorWorldX;
                    int dungeonAnchorWorldZ = dungeonParent.DungeonAnchorWorldZ;
                    bool hasDungeonAnchor = dungeonParent.HasDungeonWorldAnchor;

                    enemyWorldPosition.SetDungeonSpawnContext(
                        dungeonRequesterNetId,
                        dungeonAnchorWorldX,
                        dungeonAnchorWorldZ,
                        hasDungeonAnchor);

                    Debug.Log($"[DelayedEnemySpawn][DungeonWorldAnchor:PreSpawn] enemy='{enemy.name}' requester={dungeonRequesterNetId} hasAnchor={hasDungeonAnchor} anchorDF={dungeonAnchorWorldX}/{dungeonAnchorWorldZ}");
                }
                else if (enemyWorldPosition != null)
                {
                    // The RDB block is often not parented under DaggerfallDungeon yet at this exact point.
                    // Do not stamp requester=0/anchor=0 here. A metadata-only post-spawn coroutine below
                    // will copy the real dungeon requester once the parent exists.
                    Debug.Log($"[DelayedEnemySpawn][DungeonWorldAnchor:Deferred] enemy='{enemy.name}' waiting for dungeon parent context.");
                }
                else
                {
                    Debug.LogWarning($"[DelayedEnemySpawn] Enemy '{enemy.name}' has no EnemyWorldPosition before NetworkServer.Spawn().");
                }

                setupDemoEnemy.ServerCaptureAuthoritativeSpawnHealth();

                Debug.Log($"[DelayedEnemySpawn][PreSpawnStamp] enemy='{enemy.name}' isDungeonEnemy={setupDemoEnemy.isDungeonEnemy} syncedSpawnHealth={setupDemoEnemy.SyncedSpawnHealth}");
            }
            else
            {
                Debug.LogWarning($"[DelayedEnemySpawn] Enemy '{enemy.name}' has no SetupDemoEnemy before NetworkServer.Spawn().");
            }

            NetworkServer.Spawn(enemy.gameObject);
            Debug.Log($"[DelayedEnemySpawn] Spawned {enemy.name} on server.");

            // Metadata-only: after LayoutDungeon() parents the RDB block, copy the dungeon
            // requester/anchor onto the enemy's EnemyWorldPosition. No transform/parent/Y changes.
            if (enemyWorldPosition != null)
                GameManager.Instance.StartCoroutine(ApplyDungeonRequesterContextAfterParent(enemy, go));
        }
    }
}




// Metadata-only helper. Does not move enemies, does not change parents, does not touch Y.
// It only copies the already-known DaggerfallDungeon requester/DF anchor into EnemyWorldPosition
// after the RDB block has been parented under the dungeon object.
private static IEnumerator ApplyDungeonRequesterContextAfterParent(DaggerfallEnemy enemy, GameObject blockRoot)
{
    if (enemy == null || blockRoot == null)
        yield break;

    DaggerfallDungeon dungeonParent = null;

    // Usually one frame is enough for LayoutDungeon() to parent and position the block.
    // Keep this short; destroy grace periods are much longer than this.
    for (int i = 0; i < 10; i++)
    {
        dungeonParent = blockRoot.GetComponentInParent<DaggerfallDungeon>();
        if (dungeonParent != null)
            break;

        yield return null;
    }

    if (enemy == null)
        yield break;

    if (dungeonParent == null)
    {
        Debug.LogWarning($"[DelayedEnemySpawn][DungeonWorldAnchor:PostSpawn] enemy='{enemy.name}' could not find DaggerfallDungeon parent. Requester context not applied.");
        yield break;
    }

    global::EnemyWorldPosition enemyWorldPosition = enemy.GetComponent<global::EnemyWorldPosition>();
    if (enemyWorldPosition == null)
    {
        Debug.LogWarning($"[DelayedEnemySpawn][DungeonWorldAnchor:PostSpawn] enemy='{enemy.name}' has no EnemyWorldPosition.");
        yield break;
    }

    SetupDemoEnemy setupDemoEnemy = enemy.GetComponent<SetupDemoEnemy>();
    if (setupDemoEnemy != null && NetworkServer.active)
        setupDemoEnemy.isDungeonEnemy = true;

    enemyWorldPosition.SetDungeonSpawnContext(
        dungeonParent.RequesterNetId,
        dungeonParent.DungeonAnchorWorldX,
        dungeonParent.DungeonAnchorWorldZ,
        dungeonParent.HasDungeonWorldAnchor);

    Debug.Log($"[DelayedEnemySpawn][DungeonWorldAnchor:PostSpawn] enemy='{enemy.name}' requester={dungeonParent.RequesterNetId} hasAnchor={dungeonParent.HasDungeonWorldAnchor} anchorDF={dungeonParent.DungeonAnchorWorldX}/{dungeonParent.DungeonAnchorWorldZ}");
}



// Coroutine to delay the movement by a set number of frames
private static IEnumerator MoveEnemyToRootAfterDelay(DaggerfallEnemy enemy, Transform previousParent, int frameDelay)
{
    Debug.Log($"[MoveEnemyToRootAfterDelay] Waiting {frameDelay} frames to move {enemy.name}...");

    for (int i = 0; i < frameDelay; i++)
        yield return null;

    if (enemy == null || previousParent == null)
    {
        Debug.LogWarning($"[MoveEnemyToRootAfterDelay] {enemy?.name} or parent is null, skipping.");
        yield break;
    }

    Vector3 worldPosition = enemy.transform.position;
    Quaternion worldRotation = enemy.transform.rotation;

    enemy.transform.SetParent(null); // Move to root
    enemy.transform.position = worldPosition;
    enemy.transform.rotation = worldRotation;

    SetupDemoEnemy setupDemoEnemy = enemy.GetComponent<SetupDemoEnemy>();
    if (setupDemoEnemy != null && NetworkServer.active)
    {
        setupDemoEnemy.syncedInitialY = enemy.transform.position.y;
        setupDemoEnemy.isDungeonEnemy = true; // Will sync after repositioning
        Debug.Log($"[Server] Synced initial Y = {setupDemoEnemy.syncedInitialY} for {enemy.name}");

        if (setupDemoEnemy.SyncedSpawnHealth <= 0)
            setupDemoEnemy.ServerCaptureAuthoritativeSpawnHealth();
    }

    Debug.Log($"[MoveEnemyToRootAfterDelay] Moved {enemy.name} to root at {enemy.transform.position}, rotation {enemy.transform.rotation}");
}



        #endregion

        #region Treasure Helpers

        /// <summary>
        /// Creates a generic loot container.
        /// </summary>
        /// <param name="containerType">Type of container.</param>
        /// <param name="containerImage">Icon to display in loot UI.</param>
        /// <param name="position">Position to spawn container.</param>
        /// <param name="parent">Parent GameObject.</param>
        /// <param name="textureArchive">Texture archive for billboard containers.</param>
        /// <param name="textureRecord">Texture record for billboard containers.</param>
        /// <param name="loadID">Unique LoadID for save system.</param>
        /// <returns>DaggerfallLoot.</returns>
        public static DaggerfallLoot CreateLootContainer(
            LootContainerTypes containerType,
            InventoryContainerImages containerImage,
            Vector3 position,
            Transform parent,
            int textureArchive,
            int textureRecord,
            ulong loadID = 0,
            EnemyEntity enemyEntity = null,
            bool adjustPosition = true)
        {
            // Setup initial loot container prefab
            GameObject go = InstantiatePrefab(DaggerfallUnity.Instance.Option_LootContainerPrefab.gameObject, containerType.ToString(), parent, position);

            // Setup appearance
            if (MeshReplacement.ImportCustomFlatGameobject(textureArchive, textureRecord, Vector3.zero, go.transform))
            {
                // Use imported model instead of billboard
                GameObject.Destroy(go.GetComponent<Billboard>());
                GameObject.Destroy(go.GetComponent<MeshRenderer>());
            }
            else
            {
                // Setup billboard component
                Billboard dfBillboard = go.GetComponent<Billboard>();
                dfBillboard.SetMaterial(textureArchive, textureRecord);

                // Now move up loot icon by half own size so bottom is aligned with position
                if (adjustPosition)
                    position.y += (dfBillboard.Summary.Size.y / 2f);
            }

            // Setup DaggerfallLoot component to make lootable
            DaggerfallLoot loot = go.GetComponent<DaggerfallLoot>();
            if (loot)
            {
                loot.LoadID = loadID;
                loot.ContainerType = containerType;
                loot.ContainerImage = containerImage;
                loot.TextureArchive = textureArchive;
                loot.TextureRecord = textureRecord;
                if (enemyEntity != null)
                {
                    loot.entityName = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                    loot.isEnemyClass = (enemyEntity.EntityType == EntityTypes.EnemyClass);
                }
            }

            loot.transform.position = position;

            return loot;
        }

        /// <summary>
        /// Creates a loot container for items dropped by the player.
        /// </summary>
        /// <param name="player">Player object, must have PlayerEnterExit and PlayerMotor attached.</param>
        /// <param name="loadID">Unique LoadID for save system.</param>
        /// <returns>DaggerfallLoot.</returns>
        public static DaggerfallLoot CreateDroppedLootContainer(GameObject player, ulong loadID, int iconArchive = DaggerfallLootDataTables.randomTreasureArchive, int iconRecord = -1)
        {
            // Player must have a PlayerEnterExit component
            PlayerEnterExit playerEnterExit = player.GetComponent<PlayerEnterExit>();
            if (!playerEnterExit)
                throw new Exception("CreateDroppedLootContainer() player game object must have PlayerEnterExit component.");

            // Player must have a PlayerMotor component
            PlayerMotor playerMotor = player.GetComponent<PlayerMotor>();
            if (!playerMotor)
                throw new Exception("CreateDroppedLootContainer() player game object must have PlayerMotor component.");

            // Get parent by context
            Transform parent = null;
            if (GameManager.Instance.IsPlayerInside)
            {
                if (GameManager.Instance.IsPlayerInsideDungeon)
                    parent = playerEnterExit.Dungeon.transform;
                else
                    parent = playerEnterExit.Interior.transform;
            }
            else
            {
                parent = GameManager.Instance.StreamingTarget.transform;
            }

            // Randomise container texture, if not manually set
            if (iconRecord == -1)
            {
                int iconIndex = UnityEngine.Random.Range(0, DaggerfallLootDataTables.randomTreasureIconIndices.Length);
                iconRecord = DaggerfallLootDataTables.randomTreasureIconIndices[iconIndex];
            }

            // Find ground position below player
            Vector3 position = playerMotor.FindGroundPosition();

            // Create loot container
            DaggerfallLoot loot = CreateLootContainer(
                LootContainerTypes.DroppedLoot,
                InventoryContainerImages.Chest,
                position,
                parent,
                iconArchive,
                iconRecord,
                loadID);

            // Set properties
            loot.LoadID = loadID;
            loot.customDrop = true;
            loot.playerOwned = true;
            loot.WorldContext = playerEnterExit.WorldContext;

            // If dropped outside ask StreamingWorld to track loose object
            if (!GameManager.Instance.IsPlayerInside)
            {
                GameManager.Instance.StreamingWorld.TrackLooseObject(loot.gameObject, true);
            }

            return loot;
        }

        /// <summary>
        /// Creates a loot container for enemies slain by the player.
        /// </summary>
        /// <param name="player">Player object, must have PlayerEnterExit attached.</param>
        /// <param name="enemy">Enemy object, must have EnemyMotor attached.</param>
        /// <param name="corpseTexture">Packed corpse texture index from entity summary.</param>
        /// <param name="loadID">Unique LoadID for save system.</param>
        /// <returns>DaggerfallLoot.</returns>
        public static DaggerfallLoot CreateLootableCorpseMarker(GameObject player, GameObject enemy, EnemyEntity enemyEntity, int corpseTexture, ulong loadID)
        {
            // Player must have a PlayerEnterExit component
            PlayerEnterExit playerEnterExit = player.GetComponent<PlayerEnterExit>();
            if (!playerEnterExit)
                throw new Exception("CreateLootableCorpseMarker() player game object must have PlayerEnterExit component.");

            // Enemy must have an EnemyMotor component
            EnemyMotor enemyMotor = enemy.GetComponent<EnemyMotor>();
            if (!enemyMotor)
                throw new Exception("CreateLootableCorpseMarker() enemy game object must have EnemyMotor component.");

            // Get parent by context
            Transform parent = null;
            if (GameManager.Instance.IsPlayerInside)
            {
                if (GameManager.Instance.IsPlayerInsideDungeon)
                    parent = playerEnterExit.Dungeon.transform;
                else
                    parent = playerEnterExit.Interior.transform;
            }
            else
            {
                parent = GameManager.Instance.StreamingTarget.transform;
            }

            // Get corpse marker texture indices
            int archive, record;
            EnemyBasics.ReverseCorpseTexture(corpseTexture, out archive, out record);

            // Find ground position below enemy
            Vector3 position = enemyMotor.FindGroundPosition();

            // Create loot container
            DaggerfallLoot loot = CreateLootContainer(
                LootContainerTypes.CorpseMarker,
                InventoryContainerImages.Corpse2,
                position,
                parent,
                archive,
                record,
                loadID,
                enemyEntity);

            // Set properties
            loot.LoadID = loadID;
            loot.customDrop = true;
            loot.playerOwned = false;
            loot.WorldContext = playerEnterExit.WorldContext;

            // If this corpse was created from a network enemy, copy the enemy world-position
            // metadata onto a local-only culler. Corpse markers are not network enemies, so
            // DynamicEnemyAuthority can no longer hide/ghost them after the enemy dies.
            TryAttachMultiplayerCorpseLocalCuller(loot, enemy);

            // Multiplayer corpse loot sync:
            // Register this locally-created corpse marker using the dead enemy's NetworkIdentity.netId.
            // This avoids position-matching bugs when two similar corpses are very close together.
            global::LootCatcher.RegisterLocalCorpseLootFromEnemy(enemy, loot);

            // If dropped outside ask StreamingWorld to track loose object
            if (!GameManager.Instance.IsPlayerInside)
            {
                GameManager.Instance.StreamingWorld.TrackLooseObject(loot.gameObject, true);
            }

            return loot;
        }

        private static void TryAttachMultiplayerCorpseLocalCuller(DaggerfallLoot loot, GameObject enemy)
        {
            if (loot == null || enemy == null)
                return;

            if (!NetworkClient.active && !NetworkServer.active)
                return;

            EnemyWorldPosition enemyWorldPosition = enemy.GetComponent<EnemyWorldPosition>();
            if (enemyWorldPosition == null)
                return;

            global::MPCorpseLocalCuller culler = loot.GetComponent<global::MPCorpseLocalCuller>();
            if (culler == null)
                culler = loot.gameObject.AddComponent<global::MPCorpseLocalCuller>();

            culler.InitializeFromEnemyWorldPosition(enemyWorldPosition);
        }

        /// <summary>
        /// Destroys/Disables a loot container.
        /// Ignores unsupported or persistent container types.
        /// Custom drop containers will be destroyed from world.
        /// Fixed containers will be disabled so their empty state continues to be serialized.
        /// </summary>
        /// <param name="loot">DaggerfallLoot.</param>
        public static void RemoveLootContainer(DaggerfallLoot loot)
        {
            // Only certain container types can be removed from world
            // Other container types (e.g. corpse markers and geometry-based containers) will persist
            if (loot.ContainerType == LootContainerTypes.RandomTreasure ||
                loot.ContainerType == LootContainerTypes.DroppedLoot)
            {
                // Destroy or disable based on custom flag
                if (loot.customDrop)
                    GameObject.Destroy(loot.gameObject);
                else
                    loot.gameObject.SetActive(false);
            }
        }

        #endregion

        #region Quest Resource Helpers

        /// <summary>
        /// Gets the most appropriate parent transform based on player context for a freely spawned object.
        /// Buildings, exteriors, and dungeons all have different parents.
        /// </summary>
        /// <returns>Parent transform.</returns>
        public static Transform GetSpawnParentTransform()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInsideBuilding)
            {
                return playerEnterExit.Interior.transform;
            }
            else if (playerEnterExit.IsPlayerInsideDungeon)
            {
                return playerEnterExit.Dungeon.transform;
            }
            else if (!playerEnterExit.IsPlayerInside && GameManager.Instance.PlayerGPS.IsPlayerInLocationRect)
            {
                return GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject.transform;
            }
            else
            {
                return GameManager.Instance.StreamingWorld.StreamingTarget;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // TEMP DIAGNOSTIC: quest NPC placement / injection debugging.
        // Remove after confirming which layer loses the NPC.
        // ─────────────────────────────────────────────────────────────────────────────
        const bool QuestNpcDiagEnabled = true;

        static void QuestNpcDbg(string msg)
        {
            if (!QuestNpcDiagEnabled)
                return;

            try
            {
                Debug.Log("[QuestNpcDbg][GOH] " + msg);
            }
            catch { }
        }

        static string QuestNpcPath(Transform t)
        {
            if (t == null)
                return "<null>";

            try
            {
                string path = t.name;
                while (t.parent != null)
                {
                    t = t.parent;
                    path = t.name + "/" + path;
                }
                return path;
            }
            catch
            {
                return "<path-error>";
            }
        }

        static string QuestNpcResourceState(QuestResource resource)
        {
            if (resource == null)
                return "resource=<null>";

            string typeName = resource.GetType().Name;
            string symbol = resource.Symbol != null ? resource.Symbol.Name : "<null>";
            ulong uid = resource.ParentQuest != null ? resource.ParentQuest.UID : 0UL;
            string state = string.Format("uid={0} symbol={1} type={2} hidden={3} clicked={4} qrb={5}",
                uid,
                symbol,
                typeName,
                resource.IsHidden,
                resource.HasPlayerClicked,
                resource.QuestResourceBehaviour != null ? QuestNpcPath(resource.QuestResourceBehaviour.transform) : "<null>");

            Person person = resource as Person;
            if (person != null)
            {
                state += string.Format(" personDestroyed={0} questor={1} individual={2} atHome={3} display='{4}'",
                    person.IsDestroyed,
                    person.IsQuestor,
                    person.IsIndividualNPC,
                    person.IsIndividualAtHome,
                    person.DisplayName);
            }

            return state;
        }

        static string QuestNpcMarkerTargets(QuestMarker marker)
        {
            try
            {
                if (marker.targetResources == null)
                    return "<none>";

                List<string> parts = new List<string>();
                foreach (Symbol s in marker.targetResources)
                    parts.Add(s != null ? s.Name : "<null>");

                return string.Join(",", parts.ToArray());
            }
            catch
            {
                return "<marker-error>";
            }
        }

        /// <summary>
        /// Finds SiteLinks matching this interior and walks Place markers to inject quest resources.
        /// Some of this handling will be split and relocated for other builders.
        /// Just working through the steps in buildings interiors for now.
        /// This will be moved to a different setup class later.
        /// </summary>
        public static void AddQuestResourceObjects(SiteTypes siteType, Transform parent, int buildingKey = 0, bool enableNPCs = true, bool enableFoes = true, bool enableItems = true)
        {
            int currentMapId = 0;
            try { currentMapId = GameManager.Instance.PlayerGPS.CurrentMapID; } catch { currentMapId = 0; }

            QuestNpcDbg(string.Format("AddQuestResourceObjects START siteType={0} map={1} buildingKey={2} parent={3} enableNPCs={4} enableFoes={5} enableItems={6}",
                siteType, currentMapId, buildingKey, QuestNpcPath(parent), enableNPCs, enableFoes, enableItems));

            // Collect any SiteLinks associdated with this site
            SiteLink[] siteLinks = QuestMachine.Instance.GetSiteLinks(siteType, currentMapId, buildingKey);
            if (siteLinks == null || siteLinks.Length == 0)
            {
                QuestNpcDbg(string.Format("AddQuestResourceObjects NO SITELINKS siteType={0} map={1} buildingKey={2}", siteType, currentMapId, buildingKey));
                return;
            }

            QuestNpcDbg(string.Format("AddQuestResourceObjects siteLinks={0}", siteLinks.Length));

            // Walk through all found SiteLinks
            foreach (SiteLink link in siteLinks)
            {
                // Get the Quest object referenced by this link
                Quest quest = QuestMachine.Instance.GetQuest(link.questUID);
                if (quest == null)
                    throw new Exception(string.Format("Could not find active quest for UID {0}", link.questUID));

                // Get the Place resource referenced by this link
                Place place = quest.GetPlace(link.placeSymbol);
                if (place == null)
                    throw new Exception(string.Format("Could not find Place symbol {0} in quest UID {1}", link.placeSymbol, link.questUID));

                SiteDetails diagSite = place.SiteDetails;
                QuestNpcDbg(string.Format("SiteLink uid={0} quest={1} place={2} linkType={3} linkMap={4} linkBuilding={5} placeType={6} placeMap={7} placeBuilding={8} placeName='{9}/{10}' selectedTargets={11}",
                    link.questUID,
                    quest != null ? quest.QuestName : "<null>",
                    link.placeSymbol != null ? link.placeSymbol.Name : "<null>",
                    link.siteType,
                    link.mapId,
                    link.buildingKey,
                    diagSite.siteType,
                    diagSite.mapId,
                    diagSite.buildingKey,
                    diagSite.regionName,
                    diagSite.locationName,
                    QuestNpcMarkerTargets(diagSite.selectedMarker)));

                // Get all quest resource behaviours already in scene
                // Slightly expensive but only runs once at layout time or when "place thing" is called
                // Helps ensure a resource is not injected twice
                QuestResourceBehaviour[] resourceBehaviours = Resources.FindObjectsOfTypeAll<QuestResourceBehaviour>();

                // Add any resources for the selected marker for this place
                AddMarkerResourceObjects(siteType, parent, enableNPCs, enableFoes, enableItems, quest, resourceBehaviours, place.SiteDetails.selectedMarker);

                // Add any resources from other non-selected markers
                if (place.SiteDetails.questSpawnMarkers != null)
                    foreach (QuestMarker marker in place.SiteDetails.questSpawnMarkers)
                        AddMarkerResourceObjects(siteType, parent, enableNPCs, enableFoes, enableItems, quest, resourceBehaviours, marker);
            }
        }

        private static void AddMarkerResourceObjects(SiteTypes siteType, Transform parent, bool enableNPCs, bool enableFoes, bool enableItems, Quest quest, QuestResourceBehaviour[] resourceBehaviours, QuestMarker marker)
        {
            QuestNpcDbg(string.Format("Marker CHECK siteType={0} parent={1} targets={2} markerFlat=({3:0.00},{4:0.00},{5:0.00}) dungeon=({6},{7})",
                siteType,
                QuestNpcPath(parent),
                QuestNpcMarkerTargets(marker),
                marker.flatPosition.x,
                marker.flatPosition.y,
                marker.flatPosition.z,
                marker.dungeonX,
                marker.dungeonZ));

            if (marker.targetResources != null)
            {
                foreach (Symbol target in marker.targetResources)
                {
                    // Get target resource
                    QuestResource resource = quest.GetResource(target);
                    if (resource == null)
                    {
                        QuestNpcDbg(string.Format("Resource MISSING uid={0} quest={1} target={2}", quest != null ? quest.UID : 0UL, quest != null ? quest.QuestName : "<null>", target != null ? target.Name : "<null>"));
                        continue;
                    }

                    QuestNpcDbg("Resource FOUND " + QuestNpcResourceState(resource));

                    // Items need a site-aware duplicate check in multiplayer. Resources.FindObjectsOfTypeAll()
                    // also returns inactive QuestResourceBehaviours left behind by a dungeon/interior
                    // transition. Treating any same UID+symbol item anywhere as "already injected" can
                    // suppress the real item in the newly-active destination dungeon. This is especially
                    // visible when a client initiates a quest TeleportPc: an inactive/off-site copy can
                    // survive long enough to block destination reinjection.
                    bool alreadyInjected =
                        resource is Item && enableItems && (NetworkClient.active || NetworkServer.active)
                            ? IsQuestItemAlreadyInjectedAtCurrentSite(
                                resourceBehaviours,
                                (Item)resource,
                                parent)
                            : IsAlreadyInjected(resourceBehaviours, resource);

                    // Skip resources already injected into scene
                    if (alreadyInjected)
                    {
                        QuestNpcDbg("Resource SKIP already injected " + QuestNpcResourceState(resource));
                        continue;
                    }

                    // Inject to scene based on resource type
                    if (resource is Person && enableNPCs)
                    {
                        QuestNpcDbg("AddQuestNPC CALL " + QuestNpcResourceState(resource));
                        AddQuestNPC(siteType, quest, marker, (Person)resource, parent);
                    }
                    else if (resource is Person && !enableNPCs)
                    {
                        QuestNpcDbg("Person SKIP enableNPCs=false " + QuestNpcResourceState(resource));
                    }
                    else if (resource is Foe && enableFoes)
                    {
                        Foe foe = (Foe)resource;
                        if (foe.KillCount < foe.SpawnCount)
                            AddQuestFoe(siteType, quest, marker, foe, parent);
                        else
                            QuestNpcDbg("Foe SKIP killCount>=spawnCount " + QuestNpcResourceState(resource));
                    }
                    else if (resource is Foe && !enableFoes)
                    {
                        QuestNpcDbg("Foe SKIP enableFoes=false " + QuestNpcResourceState(resource));
                    }
                    else if (resource is Item && enableItems)
                    {
                        AddQuestItem(siteType, quest, marker, (Item)resource, parent);
                    }
                    else if (resource is Item && !enableItems)
                    {
                        QuestNpcDbg("Item SKIP enableItems=false " + QuestNpcResourceState(resource));
                    }
                }
            }
        }

        /// <summary>
        /// Multiplayer item duplicate check scoped to the currently-active site hierarchy.
        /// A world quest item that was genuinely clicked must stay gone, but an unclicked
        /// inactive/off-site object from a previous dungeon/interior transform must not block
        /// injection into the current destination.
        /// </summary>
        static bool IsQuestItemAlreadyInjectedAtCurrentSite(
            QuestResourceBehaviour[] resourceBehaviours,
            Item item,
            Transform currentSiteParent)
        {
            if (resourceBehaviours == null || resourceBehaviours.Length == 0 ||
                item == null || currentSiteParent == null)
                return false;

            ulong questUid = item.ParentQuest != null ? item.ParentQuest.UID : 0UL;
            bool itemWasClicked = false;
            bool itemIsHidden = false;
            bool itemIsCarriedLocally = false;
            try
            {
                QuestResource.ResourceSaveData_v1 rsd = item.GetResourceSaveData();
                itemWasClicked = rsd.hasPlayerClicked;
                itemIsHidden = rsd.isHidden;
            }
            catch { }

            // QuestNetSync can deliberately defer HasPlayerClicked on a real physical
            // world-item pickup so the explicit ClickedItem event remains the sole owner
            // of that click. During this window the quest Item is already rebound to the
            // exact DaggerfallUnityItem now carried by the local player.
            //
            // If that exact object is physically in PlayerEntity.Items, the world copy
            // has been consumed and must never be reactivated just because
            // hasPlayerClicked is temporarily false.
            try
            {
                if (GameManager.Instance != null &&
                    GameManager.Instance.PlayerEntity != null &&
                    GameManager.Instance.PlayerEntity.Items != null &&
                    item.DaggerfallUnityItem != null)
                {
                    itemIsCarriedLocally =
                        GameManager.Instance.PlayerEntity.Items.Contains(
                            item.DaggerfallUnityItem);
                }
            }
            catch { }

            for (int i = 0; i < resourceBehaviours.Length; i++)
            {
                QuestResourceBehaviour qrb = resourceBehaviours[i];
                if (qrb == null || qrb.TargetSymbol != item.Symbol)
                    continue;

                ulong existingQuestUid = GetQuestUIDFromResourceBehaviour(qrb);
                if (questUid != 0UL && existingQuestUid != 0UL && existingQuestUid != questUid)
                    continue;

                // A genuinely picked-up world item must never be resurrected just because
                // its old scene object is inactive. The explicit item-click/inventory paths
                // own that state.
                //
                // QNS can temporarily defer HasPlayerClicked while the exact item is already
                // carried. That is also a consumed world item, not an unclicked item that
                // should be restored into the dungeon.
                if (itemWasClicked || itemIsHidden || itemIsCarriedLocally)
                {
                    if (itemIsHidden && !itemWasClicked)
                    {
                        Debug.Log(
                            $"[QuestItemInjectMP] Kept logically hidden world quest item inactive " +
                            $"uid={questUid} symbol='{(item.Symbol != null ? item.Symbol.Name : string.Empty)}' " +
                            $"carried={itemIsCarriedLocally} path='{QuestNpcPath(qrb.transform)}'");
                    }
                    else if (itemIsCarriedLocally && !itemWasClicked)
                    {
                        Debug.Log(
                            $"[QuestItemInjectMP] Kept carried world quest item inactive during deferred click " +
                            $"uid={questUid} symbol='{(item.Symbol != null ? item.Symbol.Name : string.Empty)}' " +
                            $"path='{QuestNpcPath(qrb.transform)}'");
                    }
                    return true;
                }

                Transform existingTransform = qrb.transform;
                bool belongsToCurrentSite = false;
                if (existingTransform != null)
                {
                    Transform cursor = existingTransform;
                    while (cursor != null)
                    {
                        if (object.ReferenceEquals(cursor, currentSiteParent))
                        {
                            belongsToCurrentSite = true;
                            break;
                        }
                        cursor = cursor.parent;
                    }
                }

                if (belongsToCurrentSite)
                {
                    // The correct object exists under this dungeon/interior but can have
                    // been deactivated during the network transition. If the quest item has
                    // never been clicked, restore that object instead of manufacturing a
                    // second copy.
                    if (qrb.gameObject != null && !qrb.gameObject.activeSelf)
                    {
                        qrb.gameObject.SetActive(true);
                        Debug.Log(
                            $"[QuestItemInjectMP] Reactivated unclicked current-site quest item " +
                            $"uid={questUid} symbol='{(item.Symbol != null ? item.Symbol.Name : string.Empty)}' " +
                            $"path='{QuestNpcPath(qrb.transform)}'");
                    }

                    return true;
                }

                // Same quest UID+symbol exists, but only in an old/off-site hierarchy.
                // Do not let it block AddQuestItem() for the actual current dungeon.
                Debug.Log(
                    $"[QuestItemInjectMP] Ignoring stale off-site quest item while injecting current site " +
                    $"uid={questUid} symbol='{(item.Symbol != null ? item.Symbol.Name : string.Empty)}' " +
                    $"oldPath='{QuestNpcPath(qrb.transform)}' currentParent='{QuestNpcPath(currentSiteParent)}' " +
                    $"oldActiveSelf={(qrb.gameObject != null && qrb.gameObject.activeSelf)} " +
                    $"oldActiveHierarchy={(qrb.gameObject != null && qrb.gameObject.activeInHierarchy)}");
            }

            return false;
        }

        /// <summary>
        /// Tests if a resource is assigned inside a QuestResourceBehaviour array.
        /// </summary>
        /// <param name="resourceBehaviours">Array of quest resource behaviours in scene.</param>
        /// <param name="resource">QuestResource to check if already in scene.</param>
        /// <returns>True if QuestResource already assigned to a QuestResourceBehaviour.</returns>
        static bool IsAlreadyInjected(QuestResourceBehaviour[] resourceBehaviours, QuestResource resource)
        {
            if (resourceBehaviours == null || resourceBehaviours.Length == 0 || resource == null)
                return false;

            bool mpActive = NetworkClient.active || NetworkServer.active;
            ulong resourceQuestUid = (resource.ParentQuest != null) ? resource.ParentQuest.UID : 0UL;
            bool resourceIsFoe = resource is Foe;

            // In multiplayer, quest foes are effectively "per player" even when the synced quest UID
            // and symbol are identical. The same quest's _target_ can exist once for the host and once
            // for a client. Use requesterNetId to distinguish those copies.
            uint currentRequesterNetId = mpActive ? GetCurrentRequesterNetId() : 0U;

            foreach (QuestResourceBehaviour resourceBehaviour in resourceBehaviours)
            {
                if (resourceBehaviour == null)
                    continue;

                // Single-player / vanilla behaviour: symbol-only. Do not alter SP injection rules.
                if (!mpActive)
                {
                    if (resourceBehaviour.TargetSymbol == resource.Symbol)
                    {
                        QuestNpcDbg(string.Format("IsAlreadyInjected TRUE SP symbol={0} existingPath={1} activeSelf={2} activeHierarchy={3}",
                            resource.Symbol != null ? resource.Symbol.Name : "<null>",
                            QuestNpcPath(resourceBehaviour.transform),
                            resourceBehaviour.gameObject.activeSelf,
                            resourceBehaviour.gameObject.activeInHierarchy));
                        return true;
                    }
                    continue;
                }

                if (resourceBehaviour.TargetSymbol != resource.Symbol)
                    continue;

                ulong existingQuestUid = GetQuestUIDFromResourceBehaviour(resourceBehaviour);

                // If both UIDs are known and they differ, this is not the same quest resource.
                if (existingQuestUid != 0UL && resourceQuestUid != 0UL && existingQuestUid != resourceQuestUid)
                    continue;

                if (resourceIsFoe)
                {
                    uint existingRequesterNetId = GetRequesterNetIdFromResourceBehaviour(resourceBehaviour);

                    // If both requester IDs are known, only block the same player's copy.
                    // This prevents a client's _target_ from blocking the host's _target_ and vice versa.
                    if (existingRequesterNetId != 0U && currentRequesterNetId != 0U)
                    {
                        if (existingRequesterNetId == currentRequesterNetId)
                            return true;

                        continue;
                    }

                    // If requester metadata is missing, fall back to object identity when available.
                    // This avoids treating another player's same-symbol quest foe as "already injected".
                    QuestResource existingResource = GetQuestResourceFromResourceBehaviour(resourceBehaviour);
                    if (existingResource != null && object.ReferenceEquals(existingResource, resource))
                        return true;

                    continue;
                }

                // Non-foe quest resources should still be unique per synced quest instance.
                if (existingQuestUid != 0UL && resourceQuestUid != 0UL && existingQuestUid == resourceQuestUid)
                {
                    QuestNpcDbg(string.Format("IsAlreadyInjected TRUE MP nonFoe symbol={0} uid={1} existingPath={2} activeSelf={3} activeHierarchy={4} targetQuest={5}",
                        resource.Symbol != null ? resource.Symbol.Name : "<null>",
                        resourceQuestUid,
                        QuestNpcPath(resourceBehaviour.transform),
                        resourceBehaviour.gameObject.activeSelf,
                        resourceBehaviour.gameObject.activeInHierarchy,
                        resourceBehaviour.TargetQuest != null ? resourceBehaviour.TargetQuest.QuestName : "<null>"));
                    return true;
                }
            }

            return false;
        }

        static uint GetCurrentRequesterNetId()
        {
            try
            {
                PlayerMultiplayer localPm = PlayerMultiplayer.GetLocalPlayer();
                if (localPm != null)
                    return localPm.netId;

                if (NetworkClient.active && NetworkClient.localPlayer != null)
                    return NetworkClient.localPlayer.netId;

                if (NetworkServer.active && NetworkServer.localConnection != null &&
                    NetworkServer.localConnection.identity != null)
                    return NetworkServer.localConnection.identity.netId;

                PlayerMultiplayer[] players = GameObject.FindObjectsOfType<PlayerMultiplayer>();
                for (int i = 0; i < players.Length; i++)
                {
                    if (players[i] != null && players[i].isLocalPlayer)
                        return players[i].netId;
                }

                // Host fallback: use the server-side local player if one is present.
                for (int i = 0; i < players.Length; i++)
                {
                    if (players[i] != null && players[i].isServer && players[i].connectionToClient == NetworkServer.localConnection)
                        return players[i].netId;
                }
            }
            catch
            {
            }

            return 0U;
        }

        static uint GetRequesterNetIdFromResourceBehaviour(QuestResourceBehaviour resourceBehaviour)
        {
            if (resourceBehaviour == null)
                return 0U;

            try
            {
                EnemyWorldPosition ewp = resourceBehaviour.GetComponent<EnemyWorldPosition>();
                if (ewp == null)
                    ewp = resourceBehaviour.GetComponentInParent<EnemyWorldPosition>();

                if (ewp != null)
                    return ewp.requesterNetId;
            }
            catch
            {
            }

            return 0U;
        }

        static QuestResource GetQuestResourceFromResourceBehaviour(QuestResourceBehaviour resourceBehaviour)
        {
            if (resourceBehaviour == null)
                return null;

            try
            {
                Type t = resourceBehaviour.GetType();
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                object resObj = null;
                string[] propNames = { "Resource", "TargetResource", "QuestResource" };
                for (int i = 0; i < propNames.Length && resObj == null; i++)
                {
                    var p = t.GetProperty(propNames[i], flags);
                    if (p != null)
                        resObj = p.GetValue(resourceBehaviour, null);
                }

                string[] fieldNames = { "resource", "targetResource", "questResource" };
                for (int i = 0; i < fieldNames.Length && resObj == null; i++)
                {
                    var f = t.GetField(fieldNames[i], flags);
                    if (f != null)
                        resObj = f.GetValue(resourceBehaviour);
                }

                return resObj as QuestResource;
            }
            catch
            {
                return null;
            }
        }

        static ulong GetQuestUIDFromResourceBehaviour(QuestResourceBehaviour resourceBehaviour)
        {
            QuestResource qr = GetQuestResourceFromResourceBehaviour(resourceBehaviour);
            if (qr != null && qr.ParentQuest != null)
                return qr.ParentQuest.UID;

            if (resourceBehaviour == null)
                return 0UL;

            try
            {
                Type t = resourceBehaviour.GetType();
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                // Some builds/modded versions expose QuestUID directly.
                var pUid = t.GetProperty("QuestUID", flags);
                if (pUid != null)
                {
                    object value = pUid.GetValue(resourceBehaviour, null);
                    if (value != null)
                        return Convert.ToUInt64(value);
                }

                var fUid = t.GetField("questUID", flags) ?? t.GetField("QuestUID", flags);
                if (fUid != null)
                {
                    object value = fUid.GetValue(resourceBehaviour);
                    if (value != null)
                        return Convert.ToUInt64(value);
                }
            }
            catch
            {
            }

            return 0UL;
        }

        /// <summary>
        /// Add a quest NPC to marker position.
        /// </summary>
        static void AddQuestNPC(SiteTypes siteType, Quest quest, QuestMarker marker, Person person, Transform parent)
        {
            QuestNpcDbg(string.Format("AddQuestNPC START uid={0} quest={1} symbol={2} name='{3}' hidden={4} destroyed={5} parent={6} siteType={7} markerFlat=({8:0.00},{9:0.00},{10:0.00}) dungeon=({11},{12})",
                quest != null ? quest.UID : 0UL,
                quest != null ? quest.QuestName : "<null>",
                person != null && person.Symbol != null ? person.Symbol.Name : "<null>",
                person != null ? person.DisplayName : "<null>",
                person != null ? person.IsHidden : false,
                person != null ? person.IsDestroyed : false,
                QuestNpcPath(parent),
                siteType,
                marker.flatPosition.x,
                marker.flatPosition.y,
                marker.flatPosition.z,
                marker.dungeonX,
                marker.dungeonZ));

            // Get billboard texture data
            FactionFile.FlatData flatData;
            if (person.IsIndividualNPC)
            {
                // Individuals are always flat1 no matter gender
                flatData = FactionFile.GetFlatData(person.FactionData.flat1);
            }
            else if (person.IsQuestor)
            {
                // When person a questor use saved flat indices from questor data
                flatData.archive = person.QuestorData.billboardArchiveIndex;
                flatData.record = person.QuestorData.billboardRecordIndex;
            }
            else if (person.Gender == Genders.Male)
            {
                // Male has flat1
                flatData = FactionFile.GetFlatData(person.FactionData.flat1);
            }
            else
            {
                // Female has flat2
                flatData = FactionFile.GetFlatData(person.FactionData.flat2);
            }
                        
            Vector3 dungeonBlockPosition = new Vector3(marker.dungeonX * RDBLayout.RDBSide, 0, marker.dungeonZ * RDBLayout.RDBSide);
            Vector3 targetPosition = dungeonBlockPosition + marker.flatPosition;
            Billboard dfBillboard;
            bool inDungeon = siteType == SiteTypes.Dungeon;

            // Import or create target GameObject
            GameObject go = MeshReplacement.ImportCustomFlatGameobject(flatData.archive, flatData.record, targetPosition, parent, inDungeon);
            if (go == null)
            {
                go = CreateDaggerfallBillboardGameObject(flatData.archive, flatData.record, parent);
                go.name = string.Format("Quest NPC [{0}]", person.DisplayName);

                // Set position and adjust up by half height if not inside a dungeon
                go.transform.localPosition = targetPosition;
                dfBillboard = go.GetComponent<Billboard>();
                if (!inDungeon)
                    go.transform.localPosition += new Vector3(0, dfBillboard.Summary.Size.y / 2, 0);

                // Align injected NPC with ground
                AlignBillboardToGround(go, dfBillboard.Summary.Size, 4);
            }
            else
            {
                dfBillboard = go.GetComponent<Billboard>();
            }            
            
            if (dfBillboard != null)
            {
                // Add people data to billboard
                dfBillboard.SetRMBPeopleData(person.FactionIndex, person.FactionData.flags);
            }

            // Add QuestResourceBehaviour to GameObject
            QuestResourceBehaviour questResourceBehaviour = go.AddComponent<QuestResourceBehaviour>();
            questResourceBehaviour.AssignResource(person);

            // Set QuestResourceBehaviour in Person object
            person.QuestResourceBehaviour = questResourceBehaviour;

            // Add StaticNPC behaviour
            StaticNPC npc = go.AddComponent<StaticNPC>();
            npc.SetLayoutData((int)marker.flatPosition.x, (int)marker.flatPosition.y, (int)marker.flatPosition.z, person);

            // Set tag
            go.tag = QuestMachine.questPersonTag;

            QuestNpcDbg(string.Format("AddQuestNPC CREATED uid={0} symbol={1} go={2} localPos={3} worldPos={4} activeSelf={5} activeHierarchy={6}",
                quest != null ? quest.UID : 0UL,
                person != null && person.Symbol != null ? person.Symbol.Name : "<null>",
                QuestNpcPath(go != null ? go.transform : null),
                go != null ? go.transform.localPosition.ToString() : "<null>",
                go != null ? go.transform.position.ToString() : "<null>",
                go != null ? go.activeSelf : false,
                go != null ? go.activeInHierarchy : false));
        }



        // MP quest foes placed inside network dungeons must use the dungeon requester/anchor
        // DF world X/Z, just like imported RDB dungeon enemies. If they use normal
        // requester + Unity offset math, an underground quest foe at e.g. -500Y and
        // far from the player in local dungeon X/Z can look hundreds of DF units away
        // and DynamicEnemyAuthority will deactivate/destroy it.
        static bool TryGetDungeonAnchorForQuestFoe(Transform parent, Vector3 worldPosition, out int anchorWorldX, out int anchorWorldZ)
        {
            anchorWorldX = 0;
            anchorWorldZ = 0;

            DaggerfallDungeon dungeonParent = null;

            try
            {
                if (parent != null)
                    dungeonParent = parent.GetComponentInParent<DaggerfallDungeon>();
            }
            catch { dungeonParent = null; }

            try
            {
                if (dungeonParent == null &&
                    GameManager.Instance != null &&
                    GameManager.Instance.PlayerEnterExit != null &&
                    GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
                {
                    dungeonParent = GameManager.Instance.PlayerEnterExit.Dungeon;
                }
            }
            catch { }

            // Host/client-requested quest foes can already be at scene root by the time
            // this runs. In that case identify the active network dungeon by closest Y slot.
            if (dungeonParent == null && NetworkServer.active)
            {
                float bestYDistance = float.MaxValue;
                DaggerfallDungeon[] dungeons = GameObject.FindObjectsOfType<DaggerfallDungeon>();
                for (int i = 0; i < dungeons.Length; i++)
                {
                    DaggerfallDungeon candidate = dungeons[i];
                    if (candidate == null)
                        continue;

                    float candidateY = Mathf.Abs(candidate.PositionY) > 0.01f ? candidate.PositionY : candidate.transform.position.y;
                    float yDistance = Mathf.Abs(worldPosition.y - candidateY);

                    if (yDistance < bestYDistance)
                    {
                        bestYDistance = yDistance;
                        dungeonParent = candidate;
                    }
                }
            }

            if (dungeonParent != null && dungeonParent.HasDungeonWorldAnchor)
            {
                anchorWorldX = dungeonParent.DungeonAnchorWorldX;
                anchorWorldZ = dungeonParent.DungeonAnchorWorldZ;
                return true;
            }

            return false;
        }

/// <summary>
/// Adds a single quest foe to marker position.
/// </summary>
// === GameObjectHelper.AddQuestFoe (updated) ===
static void AddQuestFoe(SiteTypes siteType, Quest quest, QuestMarker marker, Foe foe, Transform parent)
{
    // Block during load or on clients (only allow host or single-player)
    if (SaveLoadManager.Instance.LoadInProgress)
        return;

    // Determine enemy gender
    MobileGender mobileGender = MobileGender.Unspecified;
    if (foe.Gender == Genders.Male) mobileGender = MobileGender.Male;
    else if (foe.Gender == Genders.Female) mobileGender = MobileGender.Female;

    // Calculate local spawn position (original logic)
    Vector3 dungeonBlockPosition = new Vector3(marker.dungeonX * RDBLayout.RDBSide, 0, marker.dungeonZ * RDBLayout.RDBSide);
    Vector3 spawnPosition = dungeonBlockPosition + marker.flatPosition;

    // Convert to WORLD position for networked spawn (host may not have same parent graph)
    Vector3 worldPosition = (parent != null) ? parent.TransformPoint(spawnPosition) : spawnPosition;

    // --- Client-only: ask host to spawn the REAL quest foe and return ---
    if (NetworkClient.active && !NetworkServer.active)
    {
        var pm = PlayerMultiplayer.GetLocalPlayerForCommand("GameObjectHelper.AddQuestFoe");
        if (pm != null)
        {
            // Buildings-only interior flag at request time (never mark dungeons as "interior")
            bool isInteriorAtRequest =
                (siteType != SiteTypes.Dungeon) &&
                (GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true);

            // Single marker quest foes should use the Foe resource restraint state.
            // Restrained foes stay passive; normal quest foes are hostile.
            MobileReactions questFoeReaction = foe.IsRestrained ? MobileReactions.Passive : MobileReactions.Hostile;

            pm.CmdSpawnQuestFoe(
                worldPosition,
                quest.UID,
                foe.Symbol.Original,
                foe.FoeType,
                (int)mobileGender,
                (int)siteType,
                isInteriorAtRequest,
                questFoeReaction
            );

            Debug.Log($"[AddQuestFoe] Client requested single quest foe '{foe.Symbol.Original}' type={foe.FoeType} reaction={questFoeReaction} restrained={foe.IsRestrained}");
        }
        else
        {
            Debug.LogWarning("[AddQuestFoe] Client has no PlayerMultiplayer to send spawn request.");
        }
        return; // Do not create a local SP-only enemy on clients
    }

    // --- Host or Single-player path ---
    // In multiplayer host mode, spawn directly at scene root using world coordinates.
    // If we spawn under the building interior parent and then reparent after NetworkServer.Spawn(),
    // the first entrant's existing quest foe can leave the second entrant's foe at raw marker
    // coordinates such as 3/3/-4 until the interior is re-entered. Single-player keeps vanilla
    // parent/local-position behaviour.
    bool mpHostActive = NetworkServer.active;
    Transform spawnParent = mpHostActive ? null : parent;
    Vector3 createPosition = mpHostActive ? worldPosition : spawnPosition;
    GameObject go = CreateEnemy("Quest Foe", foe.FoeType, createPosition, mobileGender, spawnParent);

    if (mpHostActive)
    {
        go.transform.SetParent(null, false);
        go.transform.position = worldPosition;
    }

    // Host path was creating marker quest foes through CreateEnemy() without explicitly
    // restoring the intended quest reaction. The prefab/default path can leave the live
    // EnemyMotor passive, which also makes the initial network hostility payload passive.
    //
    // Use the quest Foe restraint flag as the source of truth:
    // - restrained quest foes remain passive until the quest/player unrestrains or attacks them
    // - normal single marker quest foes spawn hostile
    MobileReactions hostQuestFoeReaction = foe.IsRestrained ? MobileReactions.Passive : MobileReactions.Hostile;
    SetupDemoEnemy setupHostQuestFoe = go.GetComponent<SetupDemoEnemy>();
    if (setupHostQuestFoe != null)
    {
        setupHostQuestFoe.ApplyEnemySettings(foe.FoeType, hostQuestFoeReaction, mobileGender);

        bool shouldBeHostile = hostQuestFoeReaction == MobileReactions.Hostile;
        EnemyMotor motor = go.GetComponent<EnemyMotor>();
        if (motor != null)
            motor.IsHostile = shouldBeHostile;

        setupHostQuestFoe.SyncedMotorIsHostile = shouldBeHostile;
        setupHostQuestFoe.SpawnedMotorIsHostile = shouldBeHostile;
        setupHostQuestFoe.CurrentMotorIsHostile = shouldBeHostile;
        setupHostQuestFoe.LastAppliedMotorIsHostile = shouldBeHostile;

        Debug.Log($"[AddQuestFoe] Host single quest foe '{foe.Symbol.Original}' type={foe.FoeType} reaction={hostQuestFoeReaction} restrained={foe.IsRestrained}");
    }

    // Assign unique ID and mark as quest-spawned
    DaggerfallEnemy enemy = go.GetComponent<DaggerfallEnemy>();
    if (enemy)
    {
        enemy.LoadID = DaggerfallUnity.NextUID;
        enemy.QuestSpawn = true;
    }

    // Attach quest resource tracking
    QuestResourceBehaviour questResourceBehaviour = go.AddComponent<QuestResourceBehaviour>();
    questResourceBehaviour.AssignResource(foe);
    foe.QuestResourceBehaviour = questResourceBehaviour;
    foe.RearmInjured();

    // Stamp world-position context on host for parity.
    // Buildings use normal requester+Unity-offset world coords.
    // Dungeons must use dungeon-anchor mode, otherwise the artificial underground/local
    // dungeon offset gets converted into huge DF distance and the foe deactivates/destroys.
    var ewpHost = go.GetComponent<EnemyWorldPosition>();
    if (ewpHost != null)
    {
        uint hostNetId = 0;
        PlayerMultiplayer localPm = PlayerMultiplayer.GetLocalPlayer();
        if (localPm != null)
            hostNetId = localPm.netId;

        if (siteType == SiteTypes.Dungeon)
        {
            int anchorX, anchorZ;
            bool hasAnchor = TryGetDungeonAnchorForQuestFoe(parent, worldPosition, out anchorX, out anchorZ);
            ewpHost.SetDungeonSpawnContext(hostNetId, anchorX, anchorZ, hasAnchor);
            Debug.Log($"[AddQuestFoe][DungeonWorldAnchor] Host stamped dungeon quest foe '{foe.Symbol.Original}' requester={hostNetId} hasAnchor={hasAnchor} anchorDF={anchorX}/{anchorZ}");
        }
        else
        {
            bool hostIsInterior =
                (GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true);

            ewpHost.SetSpawnContext(hostIsInterior, hostNetId);
        }

        ewpHost.intendedSpawnPos = worldPosition;

        // Badly named flag, but DynamicEnemyAuthority uses this as the "fixed spawn
        // needs settle/resnap" marker. Keep it true for single marker quest foes so
        // they are held at intendedSpawnPos for a few frames before physics/motor can
        // pull them through stacked interior floors.
        //
        // Movement is protected by the hostility fix above: hostile marker foes restore
        // EnemyMotor.IsHostile before/after settle, so they can move normally after the
        // short spawn settle window.
        ewpHost.isCreateFoeWaveSpawn = true;

        // Unlike the settle marker, this is a real ongoing quest restraint. Keep the
        // marker foe fixed only when the quest Foe resource explicitly says so.
        ewpHost.isFixedQuestFoeRestrained = foe.IsRestrained;
    }

    if (NetworkServer.active)
    {
        // Sync to clients
        NetworkServer.Spawn(go);
        Debug.Log("[AddQuestFoe] Quest enemy spawned and synced to clients.");

        // Reparent to SCENE ROOT only if something still managed to parent it.
        // In multiplayer host mode AddQuestFoe now spawns root/world-space immediately.
        DaggerfallEnemy enemyComponent = go.GetComponent<DaggerfallEnemy>();
        if (enemyComponent != null && enemyComponent.transform.parent != null)
            enemyComponent.StartCoroutine(MoveQuestEnemyToRoot(enemyComponent, siteType, 2));

        // Broadcast quest binding so clients attach QuestResourceBehaviour locally
        var ni = go.GetComponent<NetworkIdentity>();
        if (ni != null)
        {
            var broadcasters = GameObject.FindObjectsOfType<PlayerMultiplayer>();
            PlayerMultiplayer serverBroadcaster = null;
            foreach (var bpm in broadcasters)
            {
                if (bpm != null && bpm.isServer) { serverBroadcaster = bpm; break; }
            }
            if (serverBroadcaster != null)
                serverBroadcaster.RpcBindQuestFoe(ni.netId, quest.UID, foe.Symbol.Name);
            else
                Debug.LogWarning("[AddQuestFoe] No server-side PlayerMultiplayer found to broadcast RpcBindQuestFoe.");
        }
    }
}





private static IEnumerator MoveQuestEnemyToRoot(DaggerfallEnemy enemy, SiteTypes siteType, int delayFrames = 2)
{
    if (!enemy) yield break;

    Debug.Log($"[MoveQuestEnemyToRoot] Delaying move of {enemy.name} by {delayFrames} frames.");

    for (int i = 0; i < delayFrames; i++)
        yield return null;

    if (!enemy) yield break;

    // Reparent to SCENE ROOT while preserving world transform
    enemy.transform.SetParent(null, true);

    // Mark as dungeon enemy only when applicable
    SetupDemoEnemy demo = enemy.GetComponent<SetupDemoEnemy>();
    if (demo != null && NetworkServer.active)
    {
        demo.isDungeonEnemy = (siteType == SiteTypes.Dungeon);

        // Metadata only: dungeon quest foes must use requester DF X/Z directly,
        // not requester + underground/local Unity offset. Do not move or reparent here.
        if (demo.isDungeonEnemy)
        {
            global::EnemyWorldPosition ewp = enemy.GetComponent<global::EnemyWorldPosition>();
            if (ewp != null)
                ewp.SetDungeonSpawnContext(ewp.requesterNetId, 0, 0, false);
        }

        Debug.Log($"[MoveQuestEnemyToRoot] '{enemy.name}' moved to scene root. isDungeonEnemy={demo.isDungeonEnemy}");
    }
}


        /// <summary>
        /// Adds a quest item to marker position.
        /// </summary>
        static void AddQuestItem(SiteTypes siteType, Quest quest, QuestMarker marker, Item item, Transform parent = null)
        {
            // Texture indices for quest items are from world texture record
            int textureArchive = item.DaggerfallUnityItem.WorldTextureArchive;
            int textureRecord = item.DaggerfallUnityItem.WorldTextureRecord;

            // Create billboard
            GameObject go = CreateDaggerfallBillboardGameObject(textureArchive, textureRecord, parent);
            Billboard dfBillboard = go.GetComponent<Billboard>();

            // Set name
            go.name = string.Format("Quest Item [{0} | {1}]", item.Symbol.Original, item.DaggerfallUnityItem.LongName);

            // Marker position
            Vector3 dungeonBlockPosition = new Vector3(marker.dungeonX * RDBLayout.RDBSide, 0, marker.dungeonZ * RDBLayout.RDBSide);
            Vector3 position = dungeonBlockPosition + marker.flatPosition;

            // Dungeon flats have a different origin (centre point) than elsewhere (base point)
            // Find bottom of marker in world space as it should be aligned to placement surface (e.g. ground, table, shelf, etc.)
            if (siteType == SiteTypes.Dungeon)
                position.y += (-DaggerfallLoot.randomTreasureMarkerDim / 2 * MeshReader.GlobalScale);

            // Move up item icon by half own size
            position.y += (dfBillboard.Summary.Size.y / 2f);

            // Assign final position
            go.transform.localPosition = position;

            // Parent to scene marker (if any)
            // This ensures mobile quest objects parented to action marker translates correctly
            DaggerfallMarker sceneMarker = GetDaggerfallMarker(marker.markerID);
            if (sceneMarker)
                go.transform.parent = sceneMarker.transform;

            // Add QuestResourceBehaviour to GameObject
            QuestResourceBehaviour questResourceBehaviour = go.AddComponent<QuestResourceBehaviour>();
            questResourceBehaviour.AssignResource(item);

            // Set QuestResourceBehaviour in Item object
            item.QuestResourceBehaviour = questResourceBehaviour;

            // Assign a trigger collider for clicks
            SphereCollider collider = go.AddComponent<SphereCollider>();
            collider.isTrigger = true;
        }

        /// <summary>
        /// Get special marker in scene matching markerID.
        /// </summary>
        static DaggerfallMarker GetDaggerfallMarker(ulong markerID)
        {
            DaggerfallMarker result = null;
            DaggerfallMarker[] markers = GameObject.FindObjectsOfType<DaggerfallMarker>();
            foreach(DaggerfallMarker marker in markers)
            {
                if (marker.MarkerID == markerID)
                {
                    // Workaround for edge case of duplicate markerIDs in existing saves
                    // When same block used more than once in dungeon it becomes possible to have duplicate marker IDs for quest placement
                    // The below ensures marker is always unique or null to prevent bad parenting behaviour
                    // Only real impact of this change is that quest items will not translate with parent marker object if action record present on marker
                    // This is a very rare situation and mainly used when raising treasure room cage for totem in Daggerfall castle (a unique block)
                    // In vast majority of cases parenting is not even required, so minimal harm just filtering duplicates here
                    // The way marker IDs are generated should still be improved in future
                    if (result == null)
                        result = marker;
                    else
                        return null;
                }
            }

            return result;
        }

        #endregion

        #region Enemy Helpers

        /// <summary>
        /// Resolves an unspecified RDB dungeon enemy gender once on the multiplayer server.
        /// Single-player keeps the original Unspecified value and behaviour unchanged.
        /// Explicit Male/Female values are never modified.
        /// </summary>
        public static MobileGender ResolveDungeonEnemyGenderForNetwork(MobileGender gender)
        {
            if (!NetworkServer.active || gender != MobileGender.Unspecified)
                return gender;

            // Resolve once before SetupDemoEnemy creates the billboard/entity so Mirror can
            // synchronize a final value instead of every peer resolving Unspecified locally.
            return UnityEngine.Random.value < 0.5f
                ? MobileGender.Male
                : MobileGender.Female;
        }

        /// <summary>
        /// Create an enemy in the world and perform common setup tasks.
        /// </summary>
        public static GameObject CreateEnemy(string name, MobileTypes mobileType, Vector3 localPosition, MobileGender mobileGender = MobileGender.Unspecified, Transform parent = null, MobileReactions mobileReaction = MobileReactions.Hostile)
        {
            // Create target GameObject
            string displayName = string.Format("{0} [{1}]", name, mobileType.ToString());
            GameObject go = InstantiatePrefab(DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject, displayName, parent, Vector3.zero);
            SetupDemoEnemy setupEnemy = go.GetComponent<SetupDemoEnemy>();

            // Set position
            go.transform.localPosition = localPosition;

            // Assign humanoid gender randomly if unspecfied
            // This does not affect monsters like rats, bats, etc
            MobileGender gender;
            if (mobileGender == MobileGender.Unspecified)
            {
                if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
                    gender = MobileGender.Male;
                else
                    gender = MobileGender.Female;
            }
            else
            {
                gender = mobileGender;
            }

            // Configure enemy
            setupEnemy.ApplyEnemySettings(mobileType, mobileReaction, gender);

            // Align non-flying units with ground
            MobileUnit mobileUnit = setupEnemy.GetMobileBillboardChild();
            if (mobileUnit.Enemy.Behaviour != MobileBehaviour.Flying)
                AlignControllerToGround(go.GetComponent<CharacterController>());

            GameManager.Instance?.RaiseOnEnemySpawnEvent(go);

            return go;
        }

        /// <summary>
        /// Creates enemy GameObjects based on spawn count (minimum of 1, maximum of 8).
        /// Only use this when live enemy is to be first added to scene. Do not use when linking to site or deserializing.
        /// GameObjects created will be disabled, at position specified, parentless, and have a new UID for LoadID.
        /// Caller must otherwise complete GameObject setup to suit their needs before enabling.
        /// </summary>
        /// <param name="reaction">Foe is hostile by default but can optionally set to passive.</param>
        /// <returns>GameObject[] array of 1-N foes. Array can be null or empty if create fails.</returns>
public static GameObject[] CreateFoeGameObjects(
    Vector3 position,
    MobileTypes foeType,
    int spawnCount = 1,
    MobileReactions reaction = MobileReactions.Hostile,
    Foe foeResource = null,
    bool alliedToPlayer = false,
    int requesterLevel = 0)
{
    List<GameObject> gameObjects = new List<GameObject>();
    int totalSpawns = Mathf.Clamp(spawnCount, 1, 8);
    int spawnScalingLevel = requesterLevel > 0 ? Mathf.Clamp(requesterLevel, 1, 100) : DaggerfallDungeon.GetLocalPlayerLevelFallback();

    Debug.Log($"[CreateFoeGameObjects] Spawning {totalSpawns} enemies of type {foeType} at {position} spawnScalingLevel={spawnScalingLevel}");

    // Multiplayer logic
    if (Mirror.NetworkClient.active)
    {
        // Host can issue ClientRpcs from any server-side PlayerMultiplayer, but a pure client
        // must call Commands only on its own local player object.
        PlayerMultiplayer multiplayer = Mirror.NetworkServer.active
            ? (PlayerMultiplayer.GetLocalPlayer() ?? GameObject.FindObjectOfType<PlayerMultiplayer>())
            : PlayerMultiplayer.GetLocalPlayerForCommand("GameObjectHelper.CreateFoeGameObjects");

        if (multiplayer != null)
        {
            if (Mirror.NetworkServer.active)
            {
                // Host path unchanged except for dungeon metadata stamping.
                Debug.Log("[CreateFoeGameObjects] Host: spawning and calling RpcCreateFoes.");
                GameObject[] spawnedEnemies = CreateFoeGameObjectsInternal(position, foeType, totalSpawns, reaction, foeResource, alliedToPlayer, spawnScalingLevel);

                // FoeSpawner/CreateFoe enemies requested while the host is inside a dungeon
                // must use the same dungeon metadata as normal generated dungeon enemies.
                // This does not change reaction, hostility, transform placement, or spawn timing.
                bool isDungeonFromHost =
                    GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideDungeon == true;

                if (isDungeonFromHost && spawnedEnemies != null)
                {
                    for (int i = 0; i < spawnedEnemies.Length; i++)
                    {
                        GameObject enemy = spawnedEnemies[i];
                        if (enemy == null)
                            continue;

                        SetupDemoEnemy setupEnemy = enemy.GetComponent<SetupDemoEnemy>();
                        if (setupEnemy != null)
                            setupEnemy.isDungeonEnemy = true;

                        EnemyWorldPosition enemyWorldPosition = enemy.GetComponent<EnemyWorldPosition>();
                        if (enemyWorldPosition != null)
                            enemyWorldPosition.SetDungeonSpawnContext(multiplayer.netId, 0, 0, false);
                    }
                }

                multiplayer.RpcCreateFoes(position, foeType, totalSpawns, reaction, alliedToPlayer);
                return spawnedEnemies;
            }
            else
            {
                // --- Client path: compute positions locally and send to host ---
                ulong questUID = 0UL;
                string foeSymbolName = string.Empty;

                if (foeResource != null && foeResource.ParentQuest != null)
                {
                    questUID = foeResource.ParentQuest.UID;
                    foeSymbolName = foeResource.Symbol.Name;
                }

                bool isInteriorFromClient =
                    GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideBuilding == true;

                bool isDungeonFromClient =
                    GameManager.Instance?.PlayerEnterExit?.IsPlayerInsideDungeon == true;

                // A single-spawn caller has already chosen its exact world position.
                // This is used by FoeSpawner after TryFindSpawnPoint() and by the
                // console mobile-spawn command for its point in front of the local player.
                // Do not replace that position with the generic random wave picker.
                // Multi-enemy waves still use the existing client-side placement logic.
                Vector3[] preferredPositions;
                if (totalSpawns == 1)
                {
                    preferredPositions = new Vector3[] { position };
                    Debug.Log($"[CreateFoeGameObjects] Pure client: preserving exact single-spawn position {position}.");
                }
                else
                {
                    preferredPositions = ComputeClientWavePositions(
                        totalSpawns,
                        isInteriorFromClient
                    ).ToArray();
                }

                Debug.Log("[CreateFoeGameObjects] Client: requesting CmdCreateFoes (with quest context + positions).");
                multiplayer.CmdCreateFoes(
                    position,
                    foeType,
                    totalSpawns,
                    reaction,
                    alliedToPlayer,
                    questUID,
                    foeSymbolName,
                    preferredPositions,
                    isInteriorFromClient,
                    isDungeonFromClient,
                    spawnScalingLevel
                );
                return null; // clients never spawn directly
            }
        }
        else
        {
            Debug.LogError("[CreateFoeGameObjects] No PlayerMultiplayer instance found!");
            return null;
        }
    }

    // Single-player logic (spawn normally)
    return CreateFoeGameObjectsInternal(position, foeType, totalSpawns, reaction, foeResource, alliedToPlayer, spawnScalingLevel);
}

private static List<Vector3> ComputeClientWavePositions(int count, bool isInteriorLikeBuilding)
{
    // DFU-ish constants
    const float overlapSphereRadius = 0.65f;
    const float separationDistance = 1.25f;
    const float maxFloorDistance = 4f;

    float minDistance = isInteriorLikeBuilding ? 5f  : 8f;
    float maxDistance = isInteriorLikeBuilding ? 20f : 25f;

    var results = new List<Vector3>(count);

    if (GameManager.Instance?.PlayerObject == null || GameManager.Instance?.MainCamera == null)
        return results;

    Transform player = GameManager.Instance.PlayerObject.transform;
    float fov = GameManager.Instance.MainCamera.fieldOfView;

    // Try a few attempts per slot to find clean space
    for (int want = 0; want < count; want++)
    {
        bool placed = false;

        for (int attempts = 0; attempts < 8 && !placed; attempts++)
        {
            // Pick left/right outside FOV, same as DFU
            float directionAngle = fov + UnityEngine.Random.Range(0f, 4f);
            Quaternion rotation = (UnityEngine.Random.value > 0.5f)
                ? Quaternion.Euler(0, -directionAngle, 0)
                : Quaternion.Euler(0,  directionAngle, 0);

            Vector3 angle = (rotation * Vector3.forward).normalized;
            Vector3 spawnDirection = player.TransformDirection(angle).normalized;

            // Ray forward from player
            Vector3 currentPoint;
            if (Physics.Raycast(new Ray(player.position, spawnDirection), out RaycastHit initialHit, maxDistance))
            {
                float cos_normal = Vector3.Dot(-spawnDirection, initialHit.normal.normalized);
                if (cos_normal <= 1e-6f)
                    continue;

                float separationForward = separationDistance / cos_normal;
                float distanceSlack = initialHit.distance - separationForward - minDistance;
                if (distanceSlack < 0f)
                    continue;

                float extraDistance = UnityEngine.Random.Range(0f, Mathf.Min(2f, distanceSlack));
                currentPoint = initialHit.point - spawnDirection * (separationForward + extraDistance);
            }
            else
            {
                // Open area: pick a point along direction
                currentPoint = player.position + spawnDirection * UnityEngine.Random.Range(minDistance, maxDistance);
            }

            // Find floor below that point
            if (!Physics.Raycast(new Ray(currentPoint, Vector3.down), out RaycastHit floorHit, maxFloorDistance))
                continue;

            Vector3 testPoint = floorHit.point + Vector3.up * separationDistance;

            // Keep space clear
            if (Physics.OverlapSphere(testPoint, overlapSphereRadius).Length > 0)
                continue;

            results.Add(testPoint);
            placed = true;
        }

        // Fallback: if we failed, put it a bit in front of player to avoid 0/0/0
        if (!placed)
            results.Add(player.position + player.forward * Mathf.Lerp(minDistance, maxDistance, 0.5f));
    }

    return results;
}




public static GameObject[] CreateFoeGameObjectsInternal(Vector3 position, MobileTypes foeType, int spawnCount, MobileReactions reaction, Foe foeResource, bool alliedToPlayer, int spawnScalingLevel = 0)
{
    List<GameObject> gameObjects = new List<GameObject>();

    for (int i = 0; i < spawnCount; i++)
    {
        string name = $"DaggerfallEnemy [{foeType}]";
        GameObject go = GameObjectHelper.InstantiatePrefab(DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject, name, GetBestParent(), position);
        SetupDemoEnemy setupEnemy = go.GetComponent<SetupDemoEnemy>();

        if (setupEnemy != null)
        {
            MobileGender gender = (UnityEngine.Random.Range(0f, 1f) < 0.55f) ? MobileGender.Male : MobileGender.Female;
            setupEnemy.ApplyEnemySettings(foeType, reaction, gender, (byte)(alliedToPlayer ? 1 : 0), alliedToPlayer, MobileTeams.CityWatch, spawnScalingLevel);

            MobileUnit mobileUnit = setupEnemy.GetMobileBillboardChild();
            if (mobileUnit.Enemy.Behaviour != MobileBehaviour.Flying)
                GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
        }

        bool mpHostActive = NetworkServer.active;

        // Only multiplayer server-spawned enemies need NetworkIdentity.
        // Single-player enemies must stay as normal DFU objects under GetBestParent().
        if (mpHostActive && go.GetComponent<NetworkIdentity>() == null)
        {
            go.AddComponent<NetworkIdentity>();
            Debug.LogWarning($"[CreateFoeGameObjectsInternal] Added NetworkIdentity to {name}");
        }

        // Assign unique ID
        DaggerfallEnemy enemy = go.GetComponent<DaggerfallEnemy>();
        if (enemy)
        {
            enemy.LoadID = DaggerfallUnity.NextUID;
            if (foeResource != null)
                enemy.QuestSpawn = true;
        }

        // Quest wave foes must carry QuestResourceBehaviour (vanilla parity).
        // Without this, kills won't satisfy quest conditions for host/single-player and clients.
        if (foeResource != null)
        {
            QuestResourceBehaviour qrb = go.GetComponent<QuestResourceBehaviour>();
            if (qrb == null)
                qrb = go.AddComponent<QuestResourceBehaviour>();

            qrb.AssignResource(foeResource);
            foeResource.QuestResourceBehaviour = qrb;
            foeResource.RearmInjured();
        }

        // Multiplayer host path:
        // move networked enemies to scene root while preserving world position.
        // Single-player path:
        // keep the original parent from GetBestParent() so normal DFU cleanup works.
        if (mpHostActive)
        {
            Transform previousParent = go.transform.parent;
            if (previousParent != null)
            {
                Vector3 worldPosition = previousParent.TransformPoint(go.transform.localPosition);
                go.transform.SetParent(null);
                go.transform.position = worldPosition;
            }
            else
            {
                go.transform.SetParent(null);
            }
        }

        GameManager.Instance?.RaiseOnEnemySpawnEvent(go);
        gameObjects.Add(go);

        // Only the host should call NetworkServer.Spawn
        if (mpHostActive)
        {
            NetworkServer.Spawn(go);

            // Also bind quest foe on clients so their local quest system recognizes it.
            // (Host/single-player already has qrb above.)
            if (foeResource != null && foeResource.ParentQuest != null)
            {
                NetworkIdentity ni = go.GetComponent<NetworkIdentity>();
                if (ni != null)
                {
                    // Find any PlayerMultiplayer on server to issue the RPC.
                    PlayerMultiplayer pm = UnityEngine.Object.FindObjectOfType<PlayerMultiplayer>();
                    if (pm != null)
                        pm.RpcBindQuestFoe(ni.netId, foeResource.ParentQuest.UID, foeResource.Symbol.Name);
                }
            }
        }
    }

    return gameObjects.ToArray();
}

        /// <summary>
        /// Create a new foe spawner.
        /// The spawner will self-destroy once it has emitted foes into world around player.
        /// </summary>
        /// <param name="lineOfSightCheck">Should spawner try to place outside of player's field of view.</param>
        /// <param name="foeType">Type of foe to spawn.</param>
        /// <param name="spawnCount">Number of duplicate foes to spawn.</param>
        /// <param name="minDistance">Minimum distance from player.</param>
        /// <param name="maxDistance">Maximum distance from player.</param>
        /// <param name="parent">Parent GameObject. If none specified the most suitable parent will be selected automatically.</param>
        /// <returns>FoeSpawner GameObject.</returns>
        public static GameObject CreateFoeSpawner(bool lineOfSightCheck = true, MobileTypes foeType = MobileTypes.None, int spawnCount = 0, float minDistance = 4, float maxDistance = 20, Transform parent = null, bool alliedToPlayer = false, int requesterLevel = 0)
        {
			

            // Create new foe spawner
            GameObject go = new GameObject();
            FoeSpawner spawner = go.AddComponent<FoeSpawner>();
            spawner.LineOfSightCheck = lineOfSightCheck;
            spawner.FoeType = foeType;
            spawner.SpawnCount = spawnCount;
            spawner.MinDistance = minDistance;
            spawner.MaxDistance = maxDistance;
            spawner.Parent = parent;
            spawner.AlliedToPlayer = alliedToPlayer;
            spawner.RequesterLevel = requesterLevel > 0 ? Mathf.Clamp(requesterLevel, 1, 100) : DaggerfallDungeon.GetLocalPlayerLevelFallback();

            // Assign position on top of player
            // Spawner can be placed anywhere to work, but rest system considers a spawner to be an enemy "in potentia" for purposes of breaking rest and travel
            // Placing spawner on player at moment of creation will trigger the nearby enemy check even while spawn is pending
            spawner.transform.position = GameManager.Instance.PlayerObject.transform.position;

            return go;
        }

        #endregion

        /// <summary>
        /// Create a billboard batch.
        /// </summary>
        /// <param name="archive">Archive this batch is to use.</param>
        /// <param name="parent">Parent transform.</param>
        /// <returns>Billboard batch GameObject.</returns>
        public static DaggerfallBillboardBatch CreateBillboardBatchGameObject(int archive, Transform parent = null)
        {
            // Create new billboard batch object parented to terrain
            GameObject billboardBatchObject = new GameObject();
            billboardBatchObject.transform.parent = parent;
            billboardBatchObject.transform.localPosition = Vector3.zero;
            DaggerfallBillboardBatch c = billboardBatchObject.AddComponent<DaggerfallBillboardBatch>();

            // Setup batch
            c.SetMaterial(archive);

            return c;
        }

        /// <summary>
        /// Create a billboard batch with custom material/
        /// </summary>
        /// <param name="material">Custom atlas material.</param>
        /// <param name="parent">Parent transform.</param>
        /// <returns>Billboard batch GameObject.</returns>
        public static DaggerfallBillboardBatch CreateBillboardBatchGameObject(Material material, Transform parent = null)
        {
            // Create new billboard batch object parented to terrain
            GameObject billboardBatchObject = new GameObject();
            billboardBatchObject.transform.parent = parent;
            billboardBatchObject.transform.localPosition = Vector3.zero;
            DaggerfallBillboardBatch c = billboardBatchObject.AddComponent<DaggerfallBillboardBatch>();

            // Setup batch
            c.SetMaterial(material);

            return c;
        }

        public static bool FindMultiNameLocation(string multiName, out DFLocation locationOut)
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            locationOut = new DFLocation();

            if (string.IsNullOrEmpty(multiName))
                return false;

            // Split combined name
            string[] parts = multiName.Split('/');
            if (parts.Length != 2)
            {
                DaggerfallUnity.LogMessage(string.Format("Multi name '{0}' does not follow the structure RegionName/LocationName.", multiName), true);
                return false;
            }

            // Get location
            if (!dfUnity.ContentReader.GetLocation(parts[0], parts[1], out locationOut))
                return false;

            return true;
        }

        public static GameObject CreateDaggerfallLocationGameObject(string multiName, Transform parent)
        {
            // Get city
            DFLocation location;
            if (!FindMultiNameLocation(multiName, out location))
                return null;

            GameObject go = new GameObject(string.Format("DaggerfallLocation [Region={0}, Name={1}]", location.RegionName, location.Name));
            if (parent) go.transform.parent = parent;
            DaggerfallLocation c = go.AddComponent<DaggerfallLocation>() as DaggerfallLocation;
            c.SetLocation(location);

            return go;
        }

        /// <summary>
        /// Removes old singleplayer/non-networked enemies before entering multiplayer.
        /// This prevents SP dungeon/city enemies from surviving after host/client start and mixing with networked enemies.
        /// Does not touch spawned network enemies with a valid netId.
        /// </summary>
        public static int DestroyNonNetworkedEnemiesForMultiplayerStart()
        {
            int destroyed = 0;
            DaggerfallEnemy[] enemies = UnityEngine.Object.FindObjectsOfType<DaggerfallEnemy>();
            foreach (DaggerfallEnemy enemy in enemies)
            {
                if (enemy == null)
                    continue;

                NetworkIdentity identity = enemy.GetComponent<NetworkIdentity>();
                if (identity != null && identity.netId != 0)
                    continue;

                UnityEngine.Object.Destroy(enemy.gameObject);
                destroyed++;
            }

            if (destroyed > 0)
                Debug.Log($"[MultiplayerStartupCleanup] Destroyed {destroyed} non-networked enemies before/after multiplayer start.");

            return destroyed;
        }

        public static GameObject CreateDaggerfallDungeonGameObject(string multiName, Transform parent, bool importEnemies = true)
        {
            // Get dungeon
            DaggerfallDungeon daggerfallDungeon = null;
            DFLocation location;
            if (!FindMultiNameLocation(multiName, out location))
                return null;
            
            GameObject daggerfallDungeonObject;
            daggerfallDungeon = CreateDaggerfallDungeonGameObject(location, parent, out daggerfallDungeonObject);
            daggerfallDungeon.SetDungeon(location, importEnemies);

            return daggerfallDungeonObject;
        }

        public static DaggerfallDungeon CreateDaggerfallDungeonGameObject(DFLocation location, Transform parent, out GameObject go)
        {
            go = null;
            if (!location.HasDungeon)
            {
                string multiName = string.Format("{0}/{1}", location.RegionName, location.Name);
                DaggerfallUnity.LogMessage(string.Format("Location '{0}' does not contain a dungeon map", multiName), true);
                return null;
            }

            go = new GameObject(DaggerfallDungeon.GetSceneName(location));
            if (parent)
                go.transform.parent = parent;
            DaggerfallDungeon daggerfallDungeon = go.AddComponent<DaggerfallDungeon>();

            return daggerfallDungeon;
        }

        public static GameObject CreateDaggerfallTerrainGameObject(Transform parent)
        {
            // Create Unity Terrain game object
            GameObject go = Terrain.CreateTerrainGameObject(null);
            go.gameObject.transform.parent = parent;
            go.gameObject.transform.localPosition = Vector3.zero;

            // Add DaggerfallTerrain component
            go.AddComponent<DaggerfallTerrain>();

            return go;
        }

        /// <summary>
        /// Gets static door array from door information stored in model data.
        /// </summary>
        /// <param name="modelData">Model data for doors.</param>
        /// <param name="blockIndex">Block index for RMB doors.</param>
        /// <param name="recordIndex">Record index of interior.</param>
        /// <param name="buildingMatrix">Individual building matrix.</param>
        /// <returns>Array of doors in this model data.</returns>
        public static StaticDoor[] GetStaticDoors(ref ModelData modelData, int blockIndex, int recordIndex, Matrix4x4 buildingMatrix)
        {
            // Exit if no doors
            if (modelData.Doors == null)
                return null;

            // Add door triggers
            StaticDoor[] staticDoors = new StaticDoor[modelData.Doors.Length];
            for (int i = 0; i < modelData.Doors.Length; i++)
            {
                // Get door and diagonal verts
                ModelDoor door = modelData.Doors[i];
                Vector3 v0 = door.Vert0;
                Vector3 v2 = door.Vert2;

                // Get absolute door size and make thickness uniform from largest width or depth
                float width = Mathf.Abs(v2.x - v0.x);
                float height = Mathf.Abs(v2.y - v0.y);
                float depth = Mathf.Abs(v2.z - v0.z);
                float thickness = Mathf.Max(width, depth);
                Vector3 size = new Vector3(thickness, Mathf.Max(height, thickness), Mathf.Min(height, thickness));

                // Add door to array
                StaticDoor newDoor = new StaticDoor()
                {
                    buildingMatrix = buildingMatrix,
                    doorType = door.Type,
                    blockIndex = blockIndex,
                    recordIndex = recordIndex,
                    doorIndex = door.Index,
                    centre = (v0 + v2) / 2f,
                    normal = door.Normal,
                    size = size,
                };
                staticDoors[i] = newDoor;
            }

            return staticDoors;
        }

        // Helper to extract quaternion from matrix
        public static Quaternion QuaternionFromMatrix(Matrix4x4 m)
        {
            // Adapted from: http://www.euclideanspace.com/maths/geometry/rotations/conversions/matrixToQuaternion/index.htm
            Quaternion q = new Quaternion();
            q.w = Mathf.Sqrt(Mathf.Max(0, 1 + m[0, 0] + m[1, 1] + m[2, 2])) / 2;
            q.x = Mathf.Sqrt(Mathf.Max(0, 1 + m[0, 0] - m[1, 1] - m[2, 2])) / 2;
            q.y = Mathf.Sqrt(Mathf.Max(0, 1 - m[0, 0] + m[1, 1] - m[2, 2])) / 2;
            q.z = Mathf.Sqrt(Mathf.Max(0, 1 - m[0, 0] - m[1, 1] + m[2, 2])) / 2;
            q.x *= Mathf.Sign(q.x * (m[2, 1] - m[1, 2]));
            q.y *= Mathf.Sign(q.y * (m[0, 2] - m[2, 0]));
            q.z *= Mathf.Sign(q.z * (m[1, 0] - m[0, 1]));
            return q;
        }
    }
}
