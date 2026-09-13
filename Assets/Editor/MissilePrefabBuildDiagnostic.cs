#if UNITY_EDITOR
// Install at Assets/Editor/MissilePrefabBuildDiagnostic.cs.
// Read-only diagnostic: no asset saves, reimports, component edits, or forced asset loads.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaggerfallBuildDiagnostics
{
    [InitializeOnLoad]
    public static class MissilePrefabBuildDiagnostic
    {
        static readonly string[] Names = { "ArrowMissile", "ColdMissile", "FireMissile", "MagicMissile", "PoisonMissile", "ShockMissile" };
        static bool recording;
        static bool capturedFailure;
        static string pendingFailure;

        static MissilePrefabBuildDiagnostic()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            Application.logMessageReceived += OnLog;
            EditorApplication.delayCall += AfterReload;
        }

        static void AfterReload() { Capture("EDITOR-INITIALIZED-OR-RELOADED"); }
        static void BeforeReload() { Capture("BEFORE-ASSEMBLY-RELOAD"); }
        static void OnPlayMode(PlayModeStateChange state) { Capture("PLAYMODE-" + state); }

        [MenuItem("Tools/Missile Prefab Diagnostic/Capture Now")]
        public static void CaptureNow() { Capture("MANUAL"); }

        [MenuItem("Tools/Missile Prefab Diagnostic/Show Log")]
        public static void ShowLog()
        {
            if (File.Exists(LogPath)) EditorUtility.RevealInFinder(LogPath);
            else Capture("MANUAL");
        }

        static string LogPath
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/MissilePrefabDiagnostic.log")); }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (recording || capturedFailure || message == null) return;
            if (message.IndexOf("references a missing script", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf("TLS Allocator ALLOC_TEMP_THREAD", StringComparison.Ordinal) < 0) return;
            capturedFailure = true;
            pendingFailure = message + "\n" + stack;
            // Record the original error now, but do not re-enter Unity serialization
            // while its prefab/build error handler is running.
            Append("\nFIRST-FAILURE " + DateTime.UtcNow.ToString("O") + "\n" + pendingFailure + "\n");
            EditorApplication.delayCall += CaptureAfterFailure;
        }

        static void CaptureAfterFailure()
        {
            Capture("AFTER-FAILURE-EDITOR-CALLBACK");
            pendingFailure = null;
        }

        public static void BeginBuild()
        {
            capturedFailure = false;
            Capture("BEFORE-BUILD");
        }

        public static void Capture(string stage)
        {
            if (recording) return;
            recording = true;
            var output = new StringBuilder();
            try
            {
                output.AppendLine("\n=== " + stage + " " + DateTime.UtcNow.ToString("O") + " ===");
                output.AppendLine("Unity=" + Application.unityVersion + " playing=" + EditorApplication.isPlaying +
                    " compiling=" + EditorApplication.isCompiling + " building=" + BuildPipeline.isBuildingPlayer);

                // Disk references are compared with already-loaded objects below.
                // Deliberately don't LoadAssetAtPath: loading could conceal a stale-state bug.
                foreach (string name in Names)
                {
                    string path = "Assets/Prefabs/Missiles/" + name + ".prefab";
                    output.AppendLine("DISK " + path);
                    if (!File.Exists(path)) { output.AppendLine("  FILE NOT FOUND"); continue; }
                    string yaml = File.ReadAllText(path);
                    foreach (Match match in Regex.Matches(yaml, @"m_Script:\s*\{[^}]*\}"))
                    {
                        Match guid = Regex.Match(match.Value, @"guid:\s*([a-fA-F0-9]+)");
                        string resolved = guid.Success ? AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value) : "<no GUID>";
                        output.AppendLine("  " + match.Value + " -> " + resolved);
                    }
                }

                int count = 0;
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || !IsMissileName(go.name)) continue;
                    count++;
                    output.AppendLine("LOADED name=" + go.name + " instance=" + go.GetInstanceID() +
                        " persistent=" + EditorUtility.IsPersistent(go) + " asset=" + AssetDatabase.GetAssetPath(go) +
                        " scene=" + go.scene.path + " flags=" + go.hideFlags);
                    Component[] components = go.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        Component component = components[i];
                        if (component == null)
                        {
                            output.AppendLine("  component[" + i + "]=NULL/MISSING");
                            continue;
                        }
                        Type type = component.GetType();
                        output.AppendLine("  component[" + i + "]=" + type.FullName + " assembly=" + type.Assembly.FullName);
                        MonoBehaviour behaviour = component as MonoBehaviour;
                        if (behaviour == null) continue;
                        MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
                        output.AppendLine("    script=" + (script != null ? AssetDatabase.GetAssetPath(script) : "<null>") +
                            " resolvedClass=" + (script != null && script.GetClass() != null ? script.GetClass().FullName : "<null>"));
                    }
                }
                output.AppendLine("Loaded matching objects=" + count);
            }
            catch (Exception ex) { output.AppendLine("DIAGNOSTIC EXCEPTION: " + ex); }
            finally
            {
                Append(output.ToString());
                recording = false;
            }
        }

        static bool IsMissileName(string name)
        {
            foreach (string target in Names)
                if (name == target || name == target + "(Clone)") return true;
            return false;
        }

        static void Append(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, text + Environment.NewLine);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Missile diagnostic write failed: " + ex.Message); }
        }
    }

    public sealed class MissileBuildStartDiagnostic : IPreprocessBuildWithReport
    {
        public int callbackOrder { get { return int.MinValue; } }
        public void OnPreprocessBuild(BuildReport report) { MissilePrefabBuildDiagnostic.BeginBuild(); }
    }

    public sealed class MissileBuildSceneDiagnostic : IProcessSceneWithReport
    {
        public int callbackOrder { get { return int.MaxValue; } }
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report != null) MissilePrefabBuildDiagnostic.Capture("BUILD-SCENE " + scene.path);
        }
    }
}
#endif
