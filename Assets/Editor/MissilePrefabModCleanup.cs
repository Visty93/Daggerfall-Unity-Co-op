#if UNITY_EDITOR
// Install as Assets/Editor/MissilePrefabModCleanup.cs.
// Cleans the specific runtime-only LSO component observed on loaded missile assets.
// v2: also handles runtime components lost during an assembly reload.
// Does not save, reimport, or delete prefab files. Does not run during gameplay.
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DaggerfallBuildDiagnostics
{
    [InitializeOnLoad]
    public static class MissilePrefabModCleanup
    {
        static readonly string[] PrefabPaths =
        {
            "Assets/Prefabs/Missiles/ArrowMissile.prefab",
            "Assets/Prefabs/Missiles/ColdMissile.prefab",
            "Assets/Prefabs/Missiles/FireMissile.prefab",
            "Assets/Prefabs/Missiles/MagicMissile.prefab",
            "Assets/Prefabs/Missiles/PoisonMissile.prefab",
            "Assets/Prefabs/Missiles/ShockMissile.prefab"
        };
        static bool cleaning;

        static MissilePrefabModCleanup()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += CleanupAfterReload;
        }

        static void CleanupAfterReload()
        {
            if (!BuildPipeline.isBuildingPlayer) Cleanup("editor reload");
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                Cleanup("leaving Play Mode");
        }

        [MenuItem("Tools/Missile Prefab Diagnostic/Clean LSO Runtime Components")]
        public static void CleanNow() { Cleanup("manual"); }

        public static void Cleanup(string reason)
        {
            // Never remove the mod's live gameplay behaviour, including while entering Play Mode.
            if (cleaning || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            cleaning = true;
            int removed = 0;
            try
            {
                // Only inspect assets already loaded; never touch spawned scene objects.
                foreach (GameObject prefab in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (prefab == null || !EditorUtility.IsPersistent(prefab)) continue;
                    string path = AssetDatabase.GetAssetPath(prefab);
                    if (Array.IndexOf(PrefabPaths, path) < 0) continue;

                    foreach (MonoBehaviour component in prefab.GetComponents<MonoBehaviour>())
                    {
                        // Unknown missing scripts are deliberately not removed.
                        if (component == null) continue;
                        Type type = component.GetType();
                        if (type.FullName != "FriendlyFire") continue;
                        string assembly = type.Assembly.GetName().Name;
                        if (!string.Equals(assembly, "Language Skills Overhaul.dll", StringComparison.Ordinal) &&
                            !string.Equals(assembly, "Language Skills Overhaul", StringComparison.Ordinal)) continue;
                        if (MonoScript.FromMonoBehaviour(component) != null) continue;

                        // Unity requires allowDestroyingAssets for a component on a persistent object.
                        // Pass only the identified component, never the prefab GameObject.
                        UnityEngine.Object.DestroyImmediate(component, true);
                        removed++;
                        Debug.Log("[MPMissilePrefabCleanup] Removed runtime LSO FriendlyFire from " + path +
                            " (" + reason + ").");
                    }
                    removed += CleanMissingRuntimeSlots(prefab, path, reason);
                }
            }
            finally
            {
                cleaning = false;
            }
            if (removed > 0)
                Debug.Log("[MPMissilePrefabCleanup] Cleaned " + removed + " component(s). No prefab files saved.");

        }

        static int CleanMissingRuntimeSlots(GameObject prefab, string path, string reason)
        {
            Component[] loaded = prefab.GetComponents<Component>();
            bool hasMissing = false;
            foreach (Component item in loaded) if (item == null) hasMissing = true;
            if (!hasMissing) return 0;

            // Read the saved root's component list. Refuse variants/multi-object layouts.
            // Every original slot must still resolve to its exact saved local file ID.
            string yaml = File.ReadAllText(path);
            MatchCollection lists = Regex.Matches(yaml,
                @"(?m)^  m_Component:\r?\n((?:  - component: \{fileID: -?\d+\}\r?\n)+)");
            if (lists.Count != 1) return RefuseMissingCleanup(path);
            MatchCollection ids = Regex.Matches(lists[0].Groups[1].Value, @"fileID: (-?\d+)");
            if (ids.Count == 0 || loaded.Length <= ids.Count) return RefuseMissingCleanup(path);
            string assetGuid = AssetDatabase.AssetPathToGUID(path);
            for (int i = 0; i < ids.Count; i++)
            {
                long expectedId;
                string actualGuid;
                long actualId;
                if (loaded[i] == null || !long.TryParse(ids[i].Groups[1].Value, out expectedId) ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(loaded[i], out actualGuid, out actualId) ||
                    actualGuid != assetGuid || actualId != expectedId)
                    return RefuseMissingCleanup(path);
            }
            // Only extra, trailing missing slots absent from the saved prefab qualify.
            for (int i = ids.Count; i < loaded.Length; i++)
                if (loaded[i] != null) return RefuseMissingCleanup(path);

            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(prefab);
            Debug.Log("[MPMissilePrefabCleanup] Removed " + count + " extra missing runtime component(s) from " +
                path + " (" + reason + "); original component IDs verified against disk.");
            return count;
        }

        static int RefuseMissingCleanup(string path)
        {
            Debug.LogWarning("[MPMissilePrefabCleanup] Skipped missing slots on " + path +
                ": could not verify that only extra runtime components are missing.");
            return 0;
        }
    }

    public sealed class MissilePrefabModCleanupBeforeBuild : IPreprocessBuildWithReport
    {
        // Let the diagnostic at int.MinValue capture the state first.
        public int callbackOrder { get { return int.MinValue + 1; } }
        public void OnPreprocessBuild(BuildReport report)
        {
            MissilePrefabModCleanup.Cleanup("before build");
        }
    }
}
#endif
