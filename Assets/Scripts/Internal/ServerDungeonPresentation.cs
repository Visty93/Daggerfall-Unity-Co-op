// Dungeon-only deferred presentation. General rendering classes and their APIs are unchanged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Mirror;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop
{
    public sealed class ServerDungeonPresentation : MonoBehaviour
    {
        public static ServerDungeonPresentation Current { get; private set; }
        private sealed class BuildScope : IDisposable
        {
            private readonly ServerDungeonPresentation previous;
            private bool disposed;
            public BuildScope(ServerDungeonPresentation owner) { previous = Current; Current = owner; }
            public void Dispose() { if (!disposed) { Current = previous; disposed = true; } }
        }
        public static IDisposable BeginBuild(ServerDungeonPresentation owner) { return new BuildScope(owner); }

        private sealed class ModelState
        {
            public int[] textureTable;
            public DFLocation.ClimateBaseType climate;
        }
        private sealed class BlockState
        {
            public GameObject root;
            public string name;
            public short water;
            public bool lightsCreated;
            public bool waterCreated;
        }
        private readonly Dictionary<DaggerfallMesh, ModelState> modelStates = new Dictionary<DaggerfallMesh, ModelState>();
        private readonly Dictionary<int, TextureFile> metadata = new Dictionary<int, TextureFile>();
        private readonly List<BlockState> blocks = new List<BlockState>();
        private readonly Dictionary<Renderer, bool> renderers = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Behaviour, bool> behaviours = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<ParticleSystem, bool> particles = new Dictionary<ParticleSystem, bool>();
        [SerializeField] private bool presentationLoaded;
        [SerializeField] private int deferredModelCount;
        [SerializeField] private int deferredBillboardCount;
        private DaggerfallDungeon dungeon;
        private bool built;
        private float nextCheck;
        public bool PresentationLoaded { get { return presentationLoaded; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallEntryHook()
        {
            Current = null;
            PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonEntered;
            PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonEntered;
        }

        private static void OnDungeonEntered(PlayerEnterExit.TransitionEventArgs args)
        {
            if (!NetworkServer.active || !NetworkClient.active || args.DaggerfallDungeon == null) return;
            var presentation = args.DaggerfallDungeon.GetComponent<ServerDungeonPresentation>();
            if (presentation != null) presentation.ShowForLocalPlayer();
        }

        public void RegisterBlock(GameObject root, string blockName, short waterLevel)
        {
            blocks.Add(new BlockState { root = root, name = blockName, water = waterLevel });
        }

        public void FinishBuild()
        {
            metadata.Clear();
            dungeon = GetComponent<DaggerfallDungeon>();
            built = true;
            deferredModelCount = modelStates.Count;
            deferredBillboardCount = GetComponentsInChildren<ServerDungeonBillboard>(true).Length;
            HideUnvisitedPresentation();
            StartCoroutine(CaptureAfterStart());
            if (LocalPlayerIsHere()) ShowForLocalPlayer();
            Debug.Log($"[ServerDungeonPresentation] dungeon='{name}' loaded={presentationLoaded} deferredModels={deferredModelCount} metadataBillboards={deferredBillboardCount}");
        }

        private IEnumerator CaptureAfterStart()
        {
            // Include objects attached by OnSetDungeon subscribers/Start without polling the hierarchy.
            yield return null;
            yield return null;
            if (!presentationLoaded) HideUnvisitedPresentation();
        }

        private bool LocalPlayerIsHere()
        {
            if (NetworkServer.active && !NetworkClient.active) return false;
            var enterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            return enterExit != null && enterExit.IsPlayerInsideDungeon && enterExit.Dungeon == dungeon;
        }

        private void LateUpdate()
        {
            if (!built || presentationLoaded || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;
            if (LocalPlayerIsHere()) ShowForLocalPlayer();
        }

        private bool IsEnvironment(Component component)
        {
            // Enemy presentation and combat animation stay under their existing owner rules.
            return component.GetComponentInParent<DaggerfallEnemy>() == null &&
                component.GetComponentInParent<global::EnemyWorldPosition>() == null &&
                component.GetComponentInParent<global::PlayerMultiplayer>() == null;
        }

        private void HideUnvisitedPresentation()
        {
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!IsEnvironment(renderer)) continue;
                if (!renderers.ContainsKey(renderer)) renderers.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
            foreach (Behaviour behaviour in GetComponentsInChildren<Behaviour>(true))
            {
                // No broad MonoBehaviour disabling: actions, physics, loot and mod logic remain intact.
                bool billboard = behaviour.GetType() == typeof(DaggerfallBillboard) || behaviour is ServerDungeonBillboard;
                bool cosmetic = billboard || behaviour is AudioSource || behaviour is DaggerfallAudioSource ||
                    behaviour is Light || behaviour is DaggerfallLight || behaviour is DungeonLightHandler;
                if (!cosmetic || !IsEnvironment(behaviour)) continue;
                if (!behaviours.ContainsKey(behaviour)) behaviours.Add(behaviour, behaviour.enabled);
                if (billboard || behaviour is DaggerfallLight) ((MonoBehaviour)behaviour).StopAllCoroutines();
                behaviour.enabled = false;
            }
            foreach (ParticleSystem particle in GetComponentsInChildren<ParticleSystem>(true))
            {
                if (!IsEnvironment(particle)) continue;
                if (!particles.ContainsKey(particle)) particles.Add(particle, particle.isPlaying);
                if (particle.isPlaying) particle.Pause(false);
            }
        }

        private void ShowForLocalPlayer()
        {
            if (!built || presentationLoaded || (NetworkServer.active && !NetworkClient.active)) return;
            foreach (var entry in modelStates)
            {
                DaggerfallMesh model = entry.Key;
                if (model == null) continue;
                // A mod may have explicitly supplied materials after construction.
                // Preserve those rather than replacing them with our saved defaults.
                bool hasMaterials = false;
                foreach (Material existing in model.GetComponent<MeshRenderer>().sharedMaterials)
                    if (existing != null) { hasMaterials = true; break; }
                if (hasMaterials) continue;
                int[] keys = model.GetDefaultTextures();
                var cached = new CachedMaterial[keys.Length];
                Material[] materials = new Material[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    MaterialReader.ReverseTextureKey(keys[i], out int archive, out int record, out int frame);
                    DaggerfallUnity.Instance.MaterialReader.GetCachedMaterial(archive, record, frame, out cached[i]);
                    int finalArchive = entry.Value.textureTable != null
                        ? DungeonTextureTables.ApplyTextureTable(archive, entry.Value.textureTable, entry.Value.climate) : archive;
                    materials[i] = DaggerfallUnity.Instance.MaterialReader.GetMaterial(finalArchive, record);
                }
                model.GetComponent<MeshRenderer>().sharedMaterials = materials;
                GameObjectHelper.AssignAnimatedMaterialComponent(cached, model.gameObject);
            }
            foreach (ServerDungeonBillboard billboard in GetComponentsInChildren<ServerDungeonBillboard>(true))
                if (IsEnvironment(billboard)) billboard.LoadPresentation();
            foreach (BlockState block in blocks)
            {
                if (block.root == null) continue;
                if (!block.lightsCreated)
                {
                    // AddLights builds in origin space. Reparent its finished node with local coordinates.
                    GameObject staging = new GameObject("Dungeon light staging");
                    try
                    {
                        DFBlock data = DaggerfallUnity.Instance.ContentReader.BlockFileReader.GetBlock(block.name);
                        RDBLayout.AddLights(staging, ref data);
                        while (staging.transform.childCount > 0)
                            staging.transform.GetChild(0).SetParent(block.root.transform, false);
                        block.lightsCreated = true;
                    }
                    finally { Destroy(staging); }
                }
                if (!block.waterCreated)
                {
                    RDBLayout.AddWater(block.root, block.root.transform.position, block.water);
                    block.waterCreated = true;
                }
            }
            foreach (var entry in behaviours) if (entry.Key != null) entry.Key.enabled = entry.Value;
            foreach (var entry in renderers)
            {
                if (entry.Key == null) continue;
                bool show = entry.Value;
                var billboard = entry.Key.GetComponent<Billboard>();
                if (billboard != null && billboard.Summary.FlatType == FlatTypes.Editor &&
                    GameManager.Instance != null && GameManager.Instance.StartGameBehaviour != null &&
                    !GameManager.Instance.StartGameBehaviour.ShowEditorFlats) show = false;
                entry.Key.enabled = show;
            }
            foreach (var entry in particles) if (entry.Key != null && entry.Value) entry.Key.Play(false);
            presentationLoaded = true;
            modelStates.Clear();
            renderers.Clear();
            behaviours.Clear();
            particles.Clear();
            blocks.Clear();
            enabled = false; // Visited host dungeons keep the normal loaded behavior. No leave/unload machinery.
            Debug.Log($"[ServerDungeonPresentation] Loaded existing dungeon presentation: '{name}'. Gameplay layout retained.");
        }

        public static void SetDungeonTextures(DaggerfallMesh model, int[] table)
        {
            ModelState state;
            if (Current != null && Current.modelStates.TryGetValue(model, out state))
            {
                state.textureTable = table != null ? (int[])table.Clone() : null;
                var gps = GameManager.Instance != null ? GameManager.Instance.PlayerGPS : null;
                state.climate = gps != null ? gps.ClimateSettings.ClimateType : default(DFLocation.ClimateBaseType);
            }
            else model.SetDungeonTextures(table);
        }

        public static GameObject CreateBillboard(int archive, int record, Transform parent)
        {
            if (Current == null) return GameObjectHelper.CreateDaggerfallBillboardGameObject(archive, record, parent);
            string flatName = GameObjectHelper.GetGoFlatName(archive, record);
            var go = new GameObject(flatName);
            if (parent != null) go.transform.parent = parent;
            var billboard = go.AddComponent<ServerDungeonBillboard>();
            billboard.SetMaterial(archive, record);
            if (PlayerActivate.HasCustomActivation(flatName) && go.GetComponent<Collider>() == null)
                go.AddComponent<BoxCollider>().isTrigger = true;
            return go;
        }

        public bool TryGetBillboardSize(int archive, int record, out Vector2 size)
        {
            size = Vector2.zero;
            TextureFile texture;
            if (!metadata.TryGetValue(archive, out texture))
            {
                texture = new TextureFile();
                if (!texture.Load(Path.Combine(DaggerfallUnity.Instance.Arena2Path, TextureFile.IndexToFileName(archive)), FileUsage.UseMemory, true)) return false;
                metadata.Add(archive, texture);
            }
            var dimensions = texture.GetSize(record);
            var scale = texture.GetScale(record);
            if (dimensions.Width <= 0 || dimensions.Height <= 0) return false;
            size = new Vector2(
                dimensions.Width + (int)(dimensions.Width * ((float)scale.Width / BlocksFile.ScaleDivisor)),
                dimensions.Height + (int)(dimensions.Height * ((float)scale.Height / BlocksFile.ScaleDivisor))) * MeshReader.GlobalScale;
            return true;
        }
        private static Mesh GetMeshGeometry(
            DaggerfallUnity dfUnity,
            uint modelID,
            out int[] textureKeysOut,
            bool solveTangents = false,
            bool lightmapUVs = false)
        {
            textureKeysOut = null;

            // Ready check
            if (!dfUnity.IsReady)
                return null;

            // Get model data
            ModelData model;
            if (!dfUnity.MeshReader.GetModelData(modelID, out model))
            {
                DaggerfallUnity.LogMessage(string.Format("Unknown ModelID {0}.", modelID.ToString()), true);
                return null;
            }

            // Retain texture keys without loading materials
            textureKeysOut = new int[model.SubMeshes.Length];
            for (int i = 0; i < model.SubMeshes.Length; i++)
            {
                int archive = model.DFMesh.SubMeshes[i].TextureArchive;
                int record = model.DFMesh.SubMeshes[i].TextureRecord;
                textureKeysOut[i] = MaterialReader.MakeTextureKey((short)archive, (byte)record, (byte)0);
            }

            // Create mesh
            Mesh mesh = new Mesh();
            mesh.name = modelID.ToString();
            mesh.vertices = model.Vertices;
            mesh.normals = model.Normals;
            mesh.uv = model.UVs;
            mesh.subMeshCount = model.SubMeshes.Length;

            // Set submesh triangles
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = model.SubMeshes[s];
                int[] triangles = new int[sub.PrimitiveCount * 3];
                for (int t = 0; t < sub.PrimitiveCount * 3; t++)
                {
                    triangles[t] = model.Indices[sub.StartIndex + t];
                }
                mesh.SetTriangles(triangles, s);
            }

            // Finalise mesh
            if (solveTangents) MeshReader.TangentSolver(mesh);
#if UNITY_EDITOR
            if (lightmapUVs) UnityEditor.Unwrapping.GenerateSecondaryUVSet(mesh);
#endif
            mesh.RecalculateBounds();
            
            return mesh;
        }

        private static Mesh GetCombinedMeshGeometry(
            DaggerfallUnity dfUnity,
            ModelCombiner combiner,
            out int[] textureKeysOut,
            bool solveTangents = false,
            bool lightmapUVs = false)
        {
            textureKeysOut = null;

            // Ready check
            if (!dfUnity.IsReady)
                return null;

            // Get combined model
            ModelCombiner.CombinedModel combinedModel;
            if (!combiner.GetCombinedModel(out combinedModel))
                return null;

            // Retain texture keys without loading materials
            textureKeysOut = new int[combinedModel.SubMeshes.Length];
            for (int i = 0; i < combinedModel.SubMeshes.Length; i++)
            {
                int archive = combinedModel.SubMeshes[i].TextureArchive;
                int record = combinedModel.SubMeshes[i].TextureRecord;
                textureKeysOut[i] = MaterialReader.MakeTextureKey((short)archive, (byte)record, (byte)0);
            }

            // Create mesh
            Mesh mesh = new Mesh();
            mesh.name = "CombinedMesh";
            mesh.vertices = combinedModel.Vertices;
            mesh.normals = combinedModel.Normals;
            mesh.uv = combinedModel.UVs;
            mesh.subMeshCount = combinedModel.SubMeshes.Length;

            // Set submesh triangles
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = combinedModel.SubMeshes[s];
                int[] triangles = new int[sub.PrimitiveCount * 3];
                for (int t = 0; t < sub.PrimitiveCount * 3; t++)
                {
                    triangles[t] = combinedModel.Indices[sub.StartIndex + t];
                }
                mesh.SetTriangles(triangles, s);
            }

            // Finalise mesh
            if (solveTangents) MeshReader.TangentSolver(mesh);
#if UNITY_EDITOR
            if (lightmapUVs) UnityEditor.Unwrapping.GenerateSecondaryUVSet(mesh);
#endif
            mesh.RecalculateBounds();

            return mesh;
        }

        public static GameObject CreateModel(
            uint modelID,
            Transform parent,
            bool makeStatic = false,
            GameObject useExistingObject = null,
            bool ignoreCollider = false,
            bool convexCollider = false)
        {
            if (Current == null) return GameObjectHelper.CreateDaggerfallMeshGameObject(modelID, parent, makeStatic, useExistingObject, ignoreCollider, convexCollider);

            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;

            // Create gameobject
            GameObject go = (useExistingObject != null) ? useExistingObject : new GameObject();
            if (parent != null)
                go.transform.parent = parent;
            go.name = GameObjectHelper.GetGoModelName(modelID);

            // Add DaggerfallMesh component
            DaggerfallMesh dfMesh = go.GetComponent<DaggerfallMesh>();
            if (dfMesh == null)
                dfMesh = go.AddComponent<DaggerfallMesh>();

            // Get mesh filter and renderer components
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();

            // Assign mesh and materials
            int[] textureKeys;
            Mesh mesh = GetMeshGeometry(
                dfUnity,
                modelID,
                out textureKeys,
                dfUnity.MeshReader.AddMeshTangents,
                dfUnity.MeshReader.AddMeshLightmapUVs);


            // Assign mesh and materials
            if (mesh)
            {
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterials = new Material[0];
                dfMesh.SetDefaultTextures(textureKeys);
                Current.modelStates[dfMesh] = new ModelState();
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
                GameObjectHelper.TagStaticGeometry(go);

            return go;
        }

        public static GameObject CreateCombinedModel(
            ModelCombiner combiner,
            string meshName,
            Transform parent,
            bool makeStatic = false)
        {
            if (Current == null) return GameObjectHelper.CreateCombinedMeshGameObject(combiner, meshName, parent, makeStatic);

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
            int[] textureKeys;
            Mesh mesh = GetCombinedMeshGeometry(
                dfUnity,
                combiner,
                out textureKeys,
                dfUnity.MeshReader.AddMeshTangents,
                dfUnity.MeshReader.AddMeshLightmapUVs);


            // Assign mesh and materials array
            if (mesh)
            {
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterials = new Material[0];
                dfMesh.SetDefaultTextures(textureKeys);
                Current.modelStates[dfMesh] = new ModelState();
            }

            // Assign collider
            if (dfUnity.Option_AddMeshColliders)
            {
                MeshCollider collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
            }

            // Assign static
            if (makeStatic)
                GameObjectHelper.TagStaticGeometry(go);

            return go;
        }
    }
}
