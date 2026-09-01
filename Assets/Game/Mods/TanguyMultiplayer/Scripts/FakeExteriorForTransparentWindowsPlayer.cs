// FakeExteriorForTransparentWindowsPlayer.cs
//
// Attach this component to the PlayerMultiplayer prefab/GameObject.
// It is local-only: remote PlayerMultiplayer objects do nothing.
//
// SAFE HIERARCHY VISUAL-CLONE VERSION
//
// Purpose:
// When Transparent Windows is installed and Mirror multiplayer is active,
// create a visual-only copy of the currently visible exterior at the MP
// interior Y offset while preserving the original transform hierarchy.
// This avoids seams/cracks caused by flattening every renderer with lossyScale. Frozen MobilePerson/NPC billboards are skipped by default.
// Then hide only the original exterior RENDERERS while
// the player is inside the interior, so the real exterior above is not visible
// from underneath.
//
// This version intentionally does NOT move real exterior GameObjects and does
// NOT disable colliders, doors, actions, NPC scripts, or any gameplay object.
// That avoids breaking DFU doors/collision after leaving an interior.
//
// Default offset: -250 Y. Change exteriorCopyYOffset to -200 if your interiors
// are placed at -200 instead.
//
// Important:
// - This does NOT NetworkServer.Spawn anything.
// - This does NOT add NetworkIdentity.
// - This does NOT move real exterior roots.
// - This does NOT disable colliders.
// - This only runs on the local PlayerMultiplayer instance.
// - This only runs while Mirror multiplayer is active.
// - This only creates the fake exterior while the local player is inside an interior.
// - It restores hidden source renderers when leaving the interior, disconnecting,
//   disabling, or destroying the local player object.

using System;
using System.Collections.Generic;
using System.Reflection;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;

public class FakeExteriorForTransparentWindowsPlayer : NetworkBehaviour
{
    [Header("Activation")]
    [Tooltip("If true, this only works when a loaded mod title/filename looks like Transparent Windows.")]
    public bool requireTransparentWindowsMod = true;

    [Tooltip("Enable this only while testing if Transparent Windows detection fails even though the mod is installed.")]
    public bool forceEnableForTesting = false;

    [Header("Fake Exterior Offset")]
    [Tooltip("Move copied exterior visuals by this Y amount. Use -250 for interiors at Y=-250, or -200 for interiors at Y=-200.")]
    public float exteriorCopyYOffset = -250f;

    [Tooltip("Only copy source visuals above this world Y. This helps avoid copying underground interiors/dungeons back into the fake exterior.")]
    public float minimumSourceWorldY = -50f;

    [Header("Build Timing")]
    [Tooltip("Delay after entering an interior before copying exterior visuals. 0 is fastest. Raise to 0.05-0.25 if Transparent Windows enables exterior one frame later.")]
    public float rebuildDelayAfterInteriorEnter = 0.0f;

    [Tooltip("If no exterior roots are found on the first try, retry for this many seconds while still inside.")]
    public float rootFindRetrySeconds = 3.0f;

    [Tooltip("Retry interval while looking for exterior roots after entering interior.")]
    public float rootFindRetryInterval = 0.20f;

    [Tooltip("Manual refresh key. Disabled by default because F9 is Daggerfall autosave.")]
    public KeyCode manualRefreshKey = KeyCode.None;

    [Header("What To Copy")]
    [Tooltip("Copy MeshRenderers such as buildings, walls, city meshes, and many static objects.")]
    public bool copyMeshRenderers = true;

    [Tooltip("Copy SpriteRenderers/billboards such as trees, flats, lamps, and static flats when they are currently visible.")]
    public bool copySpriteRenderers = true;

    [Tooltip("Copy MobilePerson/NPC billboard visuals. OFF by default because fake visual clones do not run NPC movement/AI, so they appear frozen.")]
    public bool copyMobilePersonBillboards = false;

    [Tooltip("When copyMobilePersonBillboards is false, skip fake copies of MobilePerson/NPC billboards so frozen NPCs do not appear in windows. The original NPC renderers are still hidden while inside if hideOriginalExteriorRenderersWhileFakeIsActive is true.")]
    public bool skipFrozenMobilePersonBillboards = true;

    [Tooltip("Copy Terrain visuals. No TerrainCollider is copied.")]
    public bool copyTerrain = true;

    [Tooltip("Copy only currently active/enabled visuals. This respects whatever Transparent Windows has already hidden.")]
    public bool copyOnlyCurrentlyVisible = true;

    [Tooltip("Skip renderers under Mirror network objects and player objects. Recommended ON.")]
    public bool skipNetworkAndPlayerObjects = true;

    [Tooltip("Safety cap to avoid accidentally copying the whole game if root detection is too broad.")]
    public int maxRenderersToCopy = 20000;

    [Header("Original Exterior Visibility")]
    [Tooltip("After the fake copy is built, hide only the original exterior renderers/terrain while inside. Colliders, doors, actions, and scripts are left untouched.")]
    public bool hideOriginalExteriorRenderersWhileFakeIsActive = true;

    [Header("Exterior Root Name Hints")]
    [Tooltip("Top-level scene roots whose names contain one of these strings will be scanned for exterior visuals. Add the real exterior parent name here if the log says none were found.")]
    public string[] exteriorRootNameHints = new string[]
    {
        "Exterior",
        "StreamingWorld",
        "DaggerfallLocation",
        "Location",
        "Terrain",
        "Buildings",
        "Flats",
        "City",
        "Town",
        "Wilderness",
        "Block",
        "RMB"
    };

    [Tooltip("If no hinted roots are found, scan all safe top-level roots. Useful for unknown hierarchy names, but can copy too much.")]
    public bool scanAllSafeRootsIfNoHintRootsFound = true;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string FakeRootName = "__LOCAL_FAKE_EXTERIOR_TRANSPARENT_WINDOWS_VISUAL_ONLY__";

    // Maps original exterior transforms to local visual-only clone transforms for the current build.
    // Preserving the hierarchy is important. Flattening every renderer with world position + lossyScale
    // can create small cracks/seams between DFU exterior blocks/tiles.
    private readonly Dictionary<Transform, Transform> transformCloneMap = new Dictionary<Transform, Transform>();

    private GameObject fakeRoot;
    private bool wasInsideInterior;
    private bool wasEverLocalPlayer;
    private bool transparentWindowsChecked;
    private bool transparentWindowsLoaded;
    private float scheduledRebuildTime = -1f;
    private float retryUntilTime = -1f;
    private float nextRetryTime = -1f;
    private float nextTransparentWindowsCheckTime;
    private int currentCopiedRendererCountForBuild;

    private readonly List<RendererRestoreState> hiddenRenderers = new List<RendererRestoreState>();
    private readonly List<TerrainRestoreState> hiddenTerrains = new List<TerrainRestoreState>();

    private struct RendererRestoreState
    {
        public Renderer renderer;
        public bool enabled;
    }

    private struct TerrainRestoreState
    {
        public Terrain terrain;
        public bool enabled;
        public bool drawHeightmap;
        public bool drawTreesAndFoliage;
    }

    private void Update()
    {
        // Remote PlayerMultiplayer objects must never create, hide, restore, or destroy local visuals.
        if (!isLocalPlayer)
            return;

        wasEverLocalPlayer = true;

        // Only in Mirror multiplayer. Singleplayer exteriors/interiors remain untouched.
        if (!IsMultiplayerActive())
        {
            CleanupLocalFakeExterior("multiplayer inactive");
            ResetStateFlags();
            return;
        }

        if (!IsTransparentWindowsAvailable())
        {
            CleanupLocalFakeExterior("Transparent Windows not available");
            ResetStateFlags();
            return;
        }

        bool insideInterior = IsLocalPlayerInsideInterior();

        // Only create/keep fake exterior while inside an interior.
        if (!insideInterior)
        {
            if (wasInsideInterior || fakeRoot != null || hiddenRenderers.Count > 0 || hiddenTerrains.Count > 0)
                CleanupLocalFakeExterior("not inside interior");

            wasInsideInterior = false;
            scheduledRebuildTime = -1f;
            retryUntilTime = -1f;
            nextRetryTime = -1f;
            return;
        }

        // Just entered interior.
        if (insideInterior && !wasInsideInterior)
        {
            scheduledRebuildTime = Time.time + Mathf.Max(0f, rebuildDelayAfterInteriorEnter);
            retryUntilTime = Time.time + Mathf.Max(0f, rootFindRetrySeconds);
            nextRetryTime = scheduledRebuildTime;
            Log("Entered interior in MP. Scheduled visual-only fake exterior rebuild.");
        }

        wasInsideInterior = insideInterior;

        if (scheduledRebuildTime > 0f && Time.time >= scheduledRebuildTime)
        {
            scheduledRebuildTime = -1f;
            bool success = RebuildLocalFakeExterior();

            if (!success && Time.time < retryUntilTime)
                nextRetryTime = Time.time + Mathf.Max(0.05f, rootFindRetryInterval);
            else
                nextRetryTime = -1f;
        }

        if (nextRetryTime > 0f && Time.time >= nextRetryTime && fakeRoot == null && Time.time < retryUntilTime)
        {
            nextRetryTime = Time.time + Mathf.Max(0.05f, rootFindRetryInterval);
            bool success = RebuildLocalFakeExterior();
            if (success)
                nextRetryTime = -1f;
        }

        if (manualRefreshKey != KeyCode.None && Input.GetKeyDown(manualRefreshKey))
        {
            Log("Manual fake exterior refresh requested.");
            RebuildLocalFakeExterior();
        }
    }

    private void OnDisable()
    {
        if (wasEverLocalPlayer)
            CleanupLocalFakeExterior("local player component disabled");
    }

    private void OnDestroy()
    {
        if (wasEverLocalPlayer)
            CleanupLocalFakeExterior("local player component destroyed");
    }

    private void OnApplicationQuit()
    {
        if (wasEverLocalPlayer)
            RestoreHiddenSourceVisuals("application quit");
    }

    private bool IsMultiplayerActive()
    {
        return NetworkClient.active || NetworkServer.active;
    }

    private bool IsTransparentWindowsAvailable()
    {
        if (forceEnableForTesting)
            return true;

        if (!requireTransparentWindowsMod)
            return true;

        if (transparentWindowsChecked && transparentWindowsLoaded)
            return true;

        if (Time.time < nextTransparentWindowsCheckTime)
            return transparentWindowsLoaded;

        nextTransparentWindowsCheckTime = Time.time + 2f;
        transparentWindowsChecked = true;
        transparentWindowsLoaded = DetectTransparentWindowsLoaded();

        if (transparentWindowsLoaded)
            Log("Transparent Windows detected. Fake exterior system enabled for local MP player.");
        else
            Log("Transparent Windows not detected yet. Fake exterior disabled.");

        return transparentWindowsLoaded;
    }

    private bool DetectTransparentWindowsLoaded()
    {
        try
        {
            ModManager modManager = ModManager.Instance;
            if (modManager == null)
                return false;

            object manager = modManager;
            Type managerType = manager.GetType();

            MethodInfo getMod = managerType.GetMethod("GetMod", new Type[] { typeof(string) });
            if (getMod != null)
            {
                object mod = getMod.Invoke(manager, new object[] { "Transparent Windows" });
                if (mod != null)
                    return true;
            }

            MethodInfo getAllModTitles = managerType.GetMethod("GetAllModTitles", Type.EmptyTypes);
            if (getAllModTitles != null)
            {
                object titlesObj = getAllModTitles.Invoke(manager, null);
                string[] titles = titlesObj as string[];

                if (titles != null)
                {
                    for (int i = 0; i < titles.Length; i++)
                    {
                        if (LooksLikeTransparentWindows(titles[i]))
                            return true;
                    }
                }
            }

            string[] possibleListMethods = new string[]
            {
                "GetAllMods",
                "GetMods",
                "GetLoadedMods"
            };

            for (int i = 0; i < possibleListMethods.Length; i++)
            {
                MethodInfo method = managerType.GetMethod(possibleListMethods[i], Type.EmptyTypes);
                if (method == null)
                    continue;

                object listObj = method.Invoke(manager, null);
                if (ListContainsTransparentWindows(listObj))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FakeExteriorTW] Transparent Windows detection failed: " + ex.Message);
        }

        return false;
    }

    private bool ListContainsTransparentWindows(object listObj)
    {
        if (listObj == null)
            return false;

        System.Collections.IEnumerable enumerable = listObj as System.Collections.IEnumerable;
        if (enumerable == null)
            return false;

        foreach (object item in enumerable)
        {
            if (item == null)
                continue;

            if (LooksLikeTransparentWindows(item.ToString()))
                return true;

            Type t = item.GetType();
            string[] propNames = new string[] { "Title", "Name", "ModTitle", "Filename", "FileName", "Path" };

            for (int i = 0; i < propNames.Length; i++)
            {
                PropertyInfo p = t.GetProperty(propNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null || p.PropertyType != typeof(string))
                    continue;

                string value = p.GetValue(item, null) as string;
                if (LooksLikeTransparentWindows(value))
                    return true;
            }
        }

        return false;
    }

    private bool LooksLikeTransparentWindows(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        string lower = value.ToLowerInvariant();
        return lower.Contains("transparent") && lower.Contains("window");
    }

    private bool IsLocalPlayerInsideInterior()
    {
        PlayerEnterExit playerEnterExit = null;

        try
        {
            if (GameManager.Instance != null)
                playerEnterExit = GameManager.Instance.PlayerEnterExit;
        }
        catch
        {
            playerEnterExit = null;
        }

        if (playerEnterExit == null)
            playerEnterExit = FindObjectOfType<PlayerEnterExit>();

        if (playerEnterExit == null)
            return false;

        try
        {
            // Building/interior only, not dungeon.
            if (playerEnterExit.IsPlayerInsideDungeon)
                return false;

            if (playerEnterExit.IsPlayerInsideBuilding)
                return true;

            if (playerEnterExit.IsPlayerInside && !playerEnterExit.IsPlayerInsideDungeon)
                return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FakeExteriorTW] PlayerEnterExit interior check failed: " + ex.Message);
        }

        return false;
    }

    private bool RebuildLocalFakeExterior()
    {
        if (!isLocalPlayer)
            return false;

        if (!IsLocalPlayerInsideInterior())
        {
            CleanupLocalFakeExterior("rebuild cancelled - not inside interior");
            return false;
        }

        CleanupLocalFakeExterior("rebuild");
        transformCloneMap.Clear();

        List<GameObject> exteriorRoots = FindExteriorRoots();

        if (exteriorRoots.Count == 0)
        {
            Debug.LogWarning("[FakeExteriorTW] No exterior roots found. Add the real top-level exterior parent name to exteriorRootNameHints.");
            return false;
        }

        fakeRoot = new GameObject(FakeRootName);
        fakeRoot.transform.position = Vector3.zero;
        fakeRoot.transform.rotation = Quaternion.identity;
        fakeRoot.transform.localScale = Vector3.one;

        int copiedRenderers = 0;
        int copiedTerrains = 0;
        int sourceRenderersHidden = 0;
        int sourceTerrainsHidden = 0;
        bool hitLimit = false;
        currentCopiedRendererCountForBuild = 0;

        for (int i = 0; i < exteriorRoots.Count; i++)
        {
            if (exteriorRoots[i] == null)
                continue;

            copiedRenderers += CopyRenderersFromRoot(exteriorRoots[i].transform, exteriorRoots[i].transform, ref hitLimit);

            if (!hitLimit && copyTerrain)
                copiedTerrains += CopyTerrainsFromRoot(exteriorRoots[i].transform, exteriorRoots[i].transform);

            if (hitLimit)
                break;
        }

        if (copiedRenderers == 0 && copiedTerrains == 0)
        {
            DestroyFakeRootOnly("no visuals copied");
            Debug.LogWarning("[FakeExteriorTW] Exterior roots were found, but no visible visuals were copied. Transparent Windows may not have enabled exterior visuals yet, or filters are too strict.");
            return false;
        }

        if (hideOriginalExteriorRenderersWhileFakeIsActive)
        {
            for (int i = 0; i < exteriorRoots.Count; i++)
            {
                if (exteriorRoots[i] == null)
                    continue;

                sourceRenderersHidden += HideSourceRenderersUnderRoot(exteriorRoots[i].transform);
                sourceTerrainsHidden += HideSourceTerrainsUnderRoot(exteriorRoots[i].transform);
            }
        }

        Log("Visual-only fake exterior rebuilt. roots=" + exteriorRoots.Count +
            " renderers=" + copiedRenderers +
            " terrains=" + copiedTerrains +
            " hiddenRenderers=" + sourceRenderersHidden +
            " hiddenTerrains=" + sourceTerrainsHidden +
            " offsetY=" + exteriorCopyYOffset +
            " copyMobilePersonBillboards=" + copyMobilePersonBillboards +
            (hitLimit ? " HIT_RENDERER_LIMIT" : ""));

        return true;
    }

    private List<GameObject> FindExteriorRoots()
    {
        List<GameObject> results = new List<GameObject>();
        List<GameObject> safeFallbackRoots = new List<GameObject>();

        Scene activeScene = SceneManager.GetActiveScene();
        GameObject[] roots = activeScene.GetRootGameObjects();

        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null)
                continue;

            if (root == gameObject)
                continue;

            if (fakeRoot != null && root == fakeRoot)
                continue;

            if (root.name == FakeRootName)
                continue;

            if (IsBadRoot(root))
                continue;

            if (root.transform.position.y < minimumSourceWorldY)
                continue;

            safeFallbackRoots.Add(root);

            string lower = root.name.ToLowerInvariant();

            for (int h = 0; h < exteriorRootNameHints.Length; h++)
            {
                string hint = exteriorRootNameHints[h];
                if (string.IsNullOrEmpty(hint))
                    continue;

                if (lower.Contains(hint.ToLowerInvariant()))
                {
                    results.Add(root);
                    break;
                }
            }
        }

        if (results.Count == 0 && scanAllSafeRootsIfNoHintRootsFound)
        {
            Log("No hinted exterior roots found, using safe fallback roots count=" + safeFallbackRoots.Count);
            results.AddRange(safeFallbackRoots);
        }

        return results;
    }

    private bool IsBadRoot(GameObject root)
    {
        if (root == null)
            return true;

        string n = root.name.ToLowerInvariant();

        if (n.Contains("player")) return true;
        if (n.Contains("network")) return true;
        if (n.Contains("dungeon")) return true;
        if (n.Contains("interior")) return true;
        if (n.Contains("camera")) return true;
        if (n.Contains("manager")) return true;
        if (n.Contains("canvas")) return true;
        if (n.Contains("hud")) return true;
        if (n.Contains("ui")) return true;
        if (n.Contains("audio")) return true;

        return false;
    }

    private int CopyRenderersFromRoot(Transform sourceRoot, Transform exteriorRoot, ref bool hitLimit)
    {
        int count = 0;

        Renderer[] renderers = sourceRoot.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            if (hitLimit)
                break;

            if (maxRenderersToCopy > 0 && currentCopiedRendererCountForBuild >= maxRenderersToCopy)
            {
                hitLimit = true;
                Debug.LogWarning("[FakeExteriorTW] Reached maxRenderersToCopy=" + maxRenderersToCopy + ". Increase limit or narrow exteriorRootNameHints.");
                break;
            }

            Renderer srcRenderer = renderers[i];
            if (!ShouldCopyRenderer(srcRenderer))
                continue;

            if (CopyOneRenderer(srcRenderer, exteriorRoot))
            {
                count++;
                currentCopiedRendererCountForBuild++;
            }
        }

        return count;
    }

    private bool ShouldCopyRenderer(Renderer renderer)
    {
        if (renderer == null)
            return false;

        if (renderer.transform == null)
            return false;

        if (fakeRoot != null && renderer.transform.IsChildOf(fakeRoot.transform))
            return false;

        if (copyOnlyCurrentlyVisible)
        {
            if (!renderer.enabled)
                return false;

            if (!renderer.gameObject.activeInHierarchy)
                return false;
        }

        if (renderer.transform.position.y < minimumSourceWorldY)
            return false;

        if (skipNetworkAndPlayerObjects && HasNetworkOrPlayerComponentInParents(renderer.transform))
            return false;

        // MobilePerson/NPC billboards are animated/moved by their source gameplay objects.
        // A visual-only clone would stay at its copied position and look frozen, so skip it by default.
        if (skipFrozenMobilePersonBillboards && !copyMobilePersonBillboards && IsMobilePersonOrNpcBillboard(renderer.transform))
            return false;

        if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
            return false;

        if (renderer is MeshRenderer)
            return copyMeshRenderers && renderer.GetComponent<MeshFilter>() != null;

        if (renderer is SpriteRenderer)
            return copySpriteRenderers;

        // Skip SkinnedMeshRenderer, ParticleSystemRenderer, LineRenderer, etc.
        return false;
    }

    private bool IsMobilePersonOrNpcBillboard(Transform t)
    {
        while (t != null)
        {
            if (fakeRoot != null && t.IsChildOf(fakeRoot.transform))
                return false;

            string objectName = t.name;
            if (!string.IsNullOrEmpty(objectName))
            {
                string lowerName = objectName.ToLowerInvariant();

                // DFU exterior town NPCs commonly show up as MobilePersonBillboard.
                // These are gameplay-driven/mobile sprites, not static flats like trees/lamps.
                if (lowerName.Contains("mobileperson") ||
                    lowerName.Contains("mobile person") ||
                    lowerName.Contains("personbillboard") ||
                    lowerName.Contains("person billboard") ||
                    lowerName.Contains("townsperson") ||
                    lowerName.Contains("npc"))
                {
                    return true;
                }
            }

            Component[] comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null)
                    continue;

                string typeName = c.GetType().Name;
                if (string.IsNullOrEmpty(typeName))
                    continue;

                if (typeName.Contains("MobilePerson") ||
                    typeName.Contains("PersonBillboard") ||
                    typeName.Contains("NPC") ||
                    typeName.Contains("Npc"))
                {
                    return true;
                }
            }

            t = t.parent;
        }

        return false;
    }

    private bool HasNetworkOrPlayerComponentInParents(Transform t)
    {
        while (t != null)
        {
            Component[] comps = t.GetComponents<Component>();

            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null)
                    continue;

                string typeName = c.GetType().Name;

                if (typeName.Contains("NetworkIdentity")) return true;
                if (typeName.Contains("NetworkTransform")) return true;
                if (typeName.Contains("NetworkBehaviour")) return true;
                if (typeName.Contains("PlayerMultiplayer")) return true;
                if (typeName.Contains("PlayerAdvanced")) return true;
                if (typeName.Contains("PlayerMotor")) return true;
                if (typeName.Contains("PlayerEntity")) return true;
                if (typeName.Contains("CharacterController")) return true;
            }

            t = t.parent;
        }

        return false;
    }

    private bool CopyOneRenderer(Renderer srcRenderer, Transform exteriorRoot)
    {
        if (srcRenderer == null || exteriorRoot == null || fakeRoot == null)
            return false;

        Transform copyTransform = GetOrCreateFakeTransform(srcRenderer.transform, exteriorRoot);
        if (copyTransform == null)
            return false;

        copyTransform.gameObject.layer = srcRenderer.gameObject.layer;
        copyTransform.gameObject.tag = "Untagged";

        MeshRenderer srcMeshRenderer = srcRenderer as MeshRenderer;
        if (srcMeshRenderer != null)
        {
            MeshFilter srcMeshFilter = srcRenderer.GetComponent<MeshFilter>();
            if (srcMeshFilter == null || srcMeshFilter.sharedMesh == null)
                return false;

            MeshFilter existingFilter = copyTransform.GetComponent<MeshFilter>();
            if (existingFilter == null)
                existingFilter = copyTransform.gameObject.AddComponent<MeshFilter>();
            existingFilter.sharedMesh = srcMeshFilter.sharedMesh;

            MeshRenderer dstMeshRenderer = copyTransform.GetComponent<MeshRenderer>();
            if (dstMeshRenderer == null)
                dstMeshRenderer = copyTransform.gameObject.AddComponent<MeshRenderer>();

            dstMeshRenderer.sharedMaterials = srcMeshRenderer.sharedMaterials;
            dstMeshRenderer.shadowCastingMode = srcMeshRenderer.shadowCastingMode;
            dstMeshRenderer.receiveShadows = srcMeshRenderer.receiveShadows;
            dstMeshRenderer.lightProbeUsage = srcMeshRenderer.lightProbeUsage;
            dstMeshRenderer.reflectionProbeUsage = srcMeshRenderer.reflectionProbeUsage;
            dstMeshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            dstMeshRenderer.enabled = true;

            TryCopyRendererLightmapData(srcMeshRenderer, dstMeshRenderer);
            return true;
        }

        SpriteRenderer srcSpriteRenderer = srcRenderer as SpriteRenderer;
        if (srcSpriteRenderer != null)
        {
            SpriteRenderer dstSpriteRenderer = copyTransform.GetComponent<SpriteRenderer>();
            if (dstSpriteRenderer == null)
                dstSpriteRenderer = copyTransform.gameObject.AddComponent<SpriteRenderer>();

            dstSpriteRenderer.sprite = srcSpriteRenderer.sprite;
            dstSpriteRenderer.sharedMaterial = srcSpriteRenderer.sharedMaterial;
            dstSpriteRenderer.color = srcSpriteRenderer.color;
            dstSpriteRenderer.flipX = srcSpriteRenderer.flipX;
            dstSpriteRenderer.flipY = srcSpriteRenderer.flipY;
            dstSpriteRenderer.drawMode = srcSpriteRenderer.drawMode;
            dstSpriteRenderer.size = srcSpriteRenderer.size;
            dstSpriteRenderer.sortingLayerID = srcSpriteRenderer.sortingLayerID;
            dstSpriteRenderer.sortingOrder = srcSpriteRenderer.sortingOrder;
            dstSpriteRenderer.enabled = true;

            return true;
        }

        return false;
    }

    private Transform GetOrCreateFakeTransform(Transform source, Transform exteriorRoot)
    {
        if (source == null || exteriorRoot == null || fakeRoot == null)
            return null;

        Transform existing;
        if (transformCloneMap.TryGetValue(source, out existing) && existing != null)
            return existing;

        Transform fakeParent;

        if (source == exteriorRoot)
        {
            fakeParent = fakeRoot.transform;
        }
        else
        {
            // If the source is somehow outside the selected exterior root, do not walk into unrelated scene roots.
            if (!source.IsChildOf(exteriorRoot))
                fakeParent = fakeRoot.transform;
            else
                fakeParent = GetOrCreateFakeTransform(source.parent, exteriorRoot);
        }

        GameObject copy = new GameObject("FakeExteriorNode_" + source.gameObject.name);
        Transform copyTransform = copy.transform;
        copyTransform.SetParent(fakeParent, false);

        if (source == exteriorRoot || !source.IsChildOf(exteriorRoot))
        {
            // Top of this cloned exterior tree: move the whole root down once.
            // Children keep exact local transforms, which prevents seams between chunks/tiles.
            copyTransform.position = source.position + new Vector3(0f, exteriorCopyYOffset, 0f);
            copyTransform.rotation = source.rotation;
            copyTransform.localScale = source.localScale;
        }
        else
        {
            copyTransform.localPosition = source.localPosition;
            copyTransform.localRotation = source.localRotation;
            copyTransform.localScale = source.localScale;
        }

        copy.layer = source.gameObject.layer;
        copy.tag = "Untagged";
        transformCloneMap[source] = copyTransform;
        return copyTransform;
    }

    private void TryCopyRendererLightmapData(Renderer src, Renderer dst)
    {
        try
        {
            dst.lightmapIndex = src.lightmapIndex;
            dst.realtimeLightmapIndex = src.realtimeLightmapIndex;
            dst.lightmapScaleOffset = src.lightmapScaleOffset;
            dst.realtimeLightmapScaleOffset = src.realtimeLightmapScaleOffset;
        }
        catch
        {
            // Not important for all Unity/DFU versions.
        }
    }

    private int CopyTerrainsFromRoot(Transform sourceRoot, Transform exteriorRoot)
    {
        int count = 0;

        Terrain[] terrains = sourceRoot.GetComponentsInChildren<Terrain>(true);

        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain srcTerrain = terrains[i];
            if (srcTerrain == null || srcTerrain.terrainData == null)
                continue;

            if (copyOnlyCurrentlyVisible)
            {
                if (!srcTerrain.enabled)
                    continue;

                if (!srcTerrain.gameObject.activeInHierarchy)
                    continue;
            }

            if (srcTerrain.transform.position.y < minimumSourceWorldY)
                continue;

            if (skipNetworkAndPlayerObjects && HasNetworkOrPlayerComponentInParents(srcTerrain.transform))
                continue;

            Transform copyTransform = GetOrCreateFakeTransform(srcTerrain.transform, exteriorRoot);
            if (copyTransform == null)
                continue;

            copyTransform.gameObject.layer = srcTerrain.gameObject.layer;
            copyTransform.gameObject.tag = "Untagged";

            Terrain dstTerrain = copyTransform.GetComponent<Terrain>();
            if (dstTerrain == null)
                dstTerrain = copyTransform.gameObject.AddComponent<Terrain>();
            dstTerrain.terrainData = srcTerrain.terrainData;
            dstTerrain.materialTemplate = srcTerrain.materialTemplate;
            dstTerrain.drawTreesAndFoliage = srcTerrain.drawTreesAndFoliage;
            dstTerrain.drawHeightmap = srcTerrain.drawHeightmap;
            dstTerrain.heightmapPixelError = srcTerrain.heightmapPixelError;
            dstTerrain.basemapDistance = srcTerrain.basemapDistance;
            dstTerrain.enabled = true;

            // Intentionally no TerrainCollider.
            count++;
        }

        return count;
    }

    private int HideSourceRenderersUnderRoot(Transform sourceRoot)
    {
        int count = 0;
        Renderer[] renderers = sourceRoot.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;

            if (fakeRoot != null && r.transform.IsChildOf(fakeRoot.transform))
                continue;

            if (!ShouldHideSourceRenderer(r))
                continue;

            hiddenRenderers.Add(new RendererRestoreState
            {
                renderer = r,
                enabled = r.enabled
            });

            r.enabled = false;
            count++;
        }

        return count;
    }

    private bool ShouldHideSourceRenderer(Renderer renderer)
    {
        if (renderer == null)
            return false;

        if (!renderer.gameObject.activeInHierarchy)
            return false;

        // Only hide visible renderers we could reasonably have copied.
        if (!renderer.enabled)
            return false;

        if (renderer.transform.position.y < minimumSourceWorldY)
            return false;

        if (skipNetworkAndPlayerObjects && HasNetworkOrPlayerComponentInParents(renderer.transform))
            return false;

        if (renderer is MeshRenderer)
            return copyMeshRenderers && renderer.GetComponent<MeshFilter>() != null;

        if (renderer is SpriteRenderer)
            return copySpriteRenderers;

        return false;
    }

    private int HideSourceTerrainsUnderRoot(Transform sourceRoot)
    {
        if (!copyTerrain)
            return 0;

        int count = 0;
        Terrain[] terrains = sourceRoot.GetComponentsInChildren<Terrain>(true);

        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain t = terrains[i];
            if (t == null)
                continue;

            if (!t.enabled && !t.drawHeightmap && !t.drawTreesAndFoliage)
                continue;

            if (!t.gameObject.activeInHierarchy)
                continue;

            if (t.transform.position.y < minimumSourceWorldY)
                continue;

            if (skipNetworkAndPlayerObjects && HasNetworkOrPlayerComponentInParents(t.transform))
                continue;

            hiddenTerrains.Add(new TerrainRestoreState
            {
                terrain = t,
                enabled = t.enabled,
                drawHeightmap = t.drawHeightmap,
                drawTreesAndFoliage = t.drawTreesAndFoliage
            });

            t.drawHeightmap = false;
            t.drawTreesAndFoliage = false;
            t.enabled = false;
            count++;
        }

        return count;
    }

    private void CleanupLocalFakeExterior(string reason)
    {
        RestoreHiddenSourceVisuals(reason);
        DestroyFakeRootOnly(reason);
    }

    private void RestoreHiddenSourceVisuals(string reason)
    {
        int restoredRenderers = 0;
        int restoredTerrains = 0;

        for (int i = hiddenRenderers.Count - 1; i >= 0; i--)
        {
            RendererRestoreState state = hiddenRenderers[i];
            if (state.renderer != null)
            {
                state.renderer.enabled = state.enabled;
                restoredRenderers++;
            }
        }

        hiddenRenderers.Clear();

        for (int i = hiddenTerrains.Count - 1; i >= 0; i--)
        {
            TerrainRestoreState state = hiddenTerrains[i];
            if (state.terrain != null)
            {
                state.terrain.enabled = state.enabled;
                state.terrain.drawHeightmap = state.drawHeightmap;
                state.terrain.drawTreesAndFoliage = state.drawTreesAndFoliage;
                restoredTerrains++;
            }
        }

        hiddenTerrains.Clear();

        if (restoredRenderers > 0 || restoredTerrains > 0)
            Log("Restored source exterior visuals: " + reason + " renderers=" + restoredRenderers + " terrains=" + restoredTerrains);
    }

    private void DestroyFakeRootOnly(string reason)
    {
        if (fakeRoot != null)
        {
            Destroy(fakeRoot);
            fakeRoot = null;
            Log("Destroyed fake exterior: " + reason);
        }

        transformCloneMap.Clear();

        // Remove only our local fake root by exact name. Do this only from local owner.
        GameObject existing = GameObject.Find(FakeRootName);
        if (existing != null)
        {
            Destroy(existing);
            Log("Destroyed orphan fake exterior root: " + reason);
        }
    }

    private void ResetStateFlags()
    {
        wasInsideInterior = false;
        scheduledRebuildTime = -1f;
        retryUntilTime = -1f;
        nextRetryTime = -1f;
    }

    private void Log(string message)
    {
        if (verboseLogging)
            Debug.Log("[FakeExteriorTW] " + message);
    }
}
