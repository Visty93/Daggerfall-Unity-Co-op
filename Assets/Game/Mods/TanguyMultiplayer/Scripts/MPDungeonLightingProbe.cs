// Temporary Editor diagnostic. Copy alongside the multiplayer scripts, then restart Play mode.
// Logs component metadata and DFU event subscribers. Does not change lights or invoke mod methods.
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

public sealed class MPDungeonLightingProbe : MonoBehaviour
{
    private const string Tag = "[MPDungeonLightProbe] ";
    private const int AutomaticCaptureLimit = 16;
    private static MPDungeonLightingProbe instance;
    private string reportPath;
    private int nextCapture;
    private int automaticCaptures;
    private int relatedErrors;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (instance != null) return;
        GameObject go = new GameObject("MPDungeonLightingProbe");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<MPDungeonLightingProbe>();
    }

    private void Awake()
    {
        try
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            reportPath = Path.Combine(Application.persistentDataPath,
                "MPDungeonLightingProbe_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff",
                    CultureInfo.InvariantCulture) + ".log");
        }
        catch (Exception ex)
        {
            Debug.LogWarning(Tag + "Separate report unavailable: " + ex.Message);
        }

        DaggerfallDungeon.OnSetDungeon += OnDungeonSet;
        PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonEntered;
        PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExited;
        PlayerEnterExit.OnRespawnerComplete += OnRespawnerComplete;
        Application.logMessageReceived += OnLogMessage;

        Write("START v1; Unity=" + Application.unityVersion + "; UTC=" + DateTime.UtcNow.ToString("O"));
        Write("Report: " + (reportPath ?? "use the Unity Console / Editor.log"));
        Write("F8 captures the current dungeon. Automatic stages: event listener, next frame, +0.5s, +2s.");
        Write("Observer only: no light settings, hierarchy, event dispatch or mod methods are changed.");
    }

    private void OnDestroy()
    {
        DaggerfallDungeon.OnSetDungeon -= OnDungeonSet;
        PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonEntered;
        PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExited;
        PlayerEnterExit.OnRespawnerComplete -= OnRespawnerComplete;
        Application.logMessageReceived -= OnLogMessage;
        if (instance == this) instance = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
            BeginCapture("manual-F8", CurrentDungeon(), true);
    }

    private void OnDungeonSet(DaggerfallDungeon dungeon)
    {
        BeginCapture("OnSetDungeon", dungeon, false);
    }

    private void OnDungeonEntered(PlayerEnterExit.TransitionEventArgs args)
    {
        BeginCapture("OnTransitionDungeonInterior", args != null ? args.DaggerfallDungeon : null, false);
    }

    private void OnRespawnerComplete()
    {
        BeginCapture("OnRespawnerComplete", CurrentDungeon(), false);
    }

    private void OnDungeonExited(PlayerEnterExit.TransitionEventArgs args)
    {
        Write("EVENT OnTransitionDungeonExterior");
    }

    private void BeginCapture(string reason, DaggerfallDungeon dungeon, bool manual)
    {
        if (!manual && automaticCaptures >= AutomaticCaptureLimit) return;
        if (!manual) automaticCaptures++;
        int capture = ++nextCapture;
        Write("BEGIN capture=" + capture + " reason=" + reason);
        WriteSubscribers(typeof(DaggerfallDungeon), "OnSetDungeon");
        WriteSubscribers(typeof(PlayerEnterExit), "OnTransitionDungeonInterior");
        WriteSubscribers(typeof(PlayerEnterExit), "OnRespawnerComplete");
        Snapshot(capture, "at-listener", dungeon);
        if (dungeon != null) StartCoroutine(CaptureLater(capture, dungeon));
        if (!manual && automaticCaptures == AutomaticCaptureLimit)
            Write("Automatic capture limit reached. F8 remains available.");
    }

    private IEnumerator CaptureLater(int capture, DaggerfallDungeon dungeon)
    {
        yield return null;
        if (dungeon == null) yield break;
        Snapshot(capture, "next-frame", dungeon);
        yield return new WaitForSecondsRealtime(0.5f);
        if (dungeon == null) yield break;
        Snapshot(capture, "after-0.5s", dungeon);
        yield return new WaitForSecondsRealtime(1.5f);
        if (dungeon != null) Snapshot(capture, "after-2s", dungeon);
    }

    private static DaggerfallDungeon CurrentDungeon()
    {
        GameManager gm = GameManager.Instance;
        return gm != null && gm.PlayerEnterExit != null ? gm.PlayerEnterExit.Dungeon : null;
    }

    private void Snapshot(int capture, string stage, DaggerfallDungeon dungeon)
    {
        try
        {
            GameManager gm = GameManager.Instance;
            GameObject player = gm != null ? gm.PlayerObject : null;
            GameObject usualParent = gm != null ? gm.DungeonParent : null;
            DaggerfallDungeon current = CurrentDungeon();
            string prefix = "capture=" + capture + " stage=" + stage + " ";
            Write(prefix + "context insideDungeon=" + (gm != null && gm.IsPlayerInsideDungeon) +
                " current=" + ObjectPath(current != null ? current.transform : null) +
                " player=" + ObjectPath(player != null ? player.transform : null) +
                " playerWorld=" + (player != null ? Vec(player.transform.position) : "<none>"));
            if (dungeon == null)
            {
                Write(prefix + "target=<none>");
                return;
            }

            Write(prefix + "target=" + ObjectPath(dungeon.transform) +
                " id=" + dungeon.GetInstanceID() + " world=" + Vec(dungeon.transform.position) +
                " active=" + dungeon.gameObject.activeInHierarchy + " isCurrent=" + (current == dungeon) +
                " underUsualDungeonParent=" + (usualParent != null && dungeon.transform.IsChildOf(usualParent.transform)));
            Light[] lights = dungeon.GetComponentsInChildren<Light>(true);
            Write(prefix + "TARGET " + Counts(lights));
            Write(prefix + "USUAL-PARENT path=" + ObjectPath(usualParent != null ? usualParent.transform : null) +
                " " + Counts(usualParent != null ? usualParent.GetComponentsInChildren<Light>(true) : new Light[0]));

            List<Light> nearest = new List<Light>(lights);
            Vector3 origin = player != null ? player.transform.position : dungeon.transform.position;
            nearest.Sort(delegate(Light a, Light b)
            {
                return DistanceSquared(a, origin).CompareTo(DistanceSquared(b, origin));
            });
            int[] emitted = new int[4];
            int[] limits = { 2, 3, 1, 1 };
            foreach (Light light in nearest)
            {
                if (light == null) continue;
                int kind = Kind(light);
                if (emitted[kind] >= limits[kind]) continue;
                emitted[kind]++;
                Vector3 delta = light.transform.position - origin;
                Color c = light.color;
                Write(prefix + "LIGHT id=" + light.GetInstanceID() + " kind=" + KindName(kind) +
                    " active=" + light.gameObject.activeInHierarchy + " enabled=" + light.enabled +
                    " world=" + Vec(light.transform.position) + " local=" + Vec(light.transform.localPosition) +
                    " d3=" + Number(delta.magnitude) + " dXZ=" + Number(new Vector2(delta.x, delta.z).magnitude) +
                    " rgb=" + Number(c.r) + "/" + Number(c.g) + "/" + Number(c.b) +
                    " intensity=" + Number(light.intensity) + " range=" + Number(light.range) +
                    " shadows=" + light.shadows + " scripts=" + Scripts(light.gameObject) +
                    " path=" + ObjectPath(light.transform));
            }
        }
        catch (Exception ex)
        {
            Write("PROBE-ERROR capture=" + capture + " stage=" + stage + " " + ex);
        }
    }

    private static string Counts(Light[] lights)
    {
        int active = 0, enabled = 0, guards = 0, markers = 0, flickers = 0, nativeScript = 0;
        int[] kinds = new int[4];
        foreach (Light light in lights)
        {
            if (light == null) continue;
            kinds[Kind(light)]++;
            if (light.gameObject.activeInHierarchy) active++;
            if (light.gameObject.activeInHierarchy && light.enabled) enabled++;
            if (HasScript(light.gameObject, "ImprovedDungeonLightAppearanceGuard")) guards++;
            if (HasScript(light.gameObject, "ImprovedDungeonLightMarker")) markers++;
            if (HasScript(light.gameObject, "LightFlicker")) flickers++;
            if (light.GetComponent<DaggerfallLight>() != null) nativeScript++;
        }
        return "lights=" + lights.Length + " activeObjects=" + active + " activeEnabled=" + enabled +
            " native=" + kinds[0] + " replacement=" + kinds[1] + " independent=" + kinds[2] +
            " other=" + kinds[3] + " withNativeScript=" + nativeScript +
            " guards=" + guards + " markers=" + markers + " flickers=" + flickers;
    }

    private static int Kind(Light light)
    {
        if (light.name.IndexOf("DaggerfallLight [Dungeon]", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
        if (light.name.IndexOf("ImprovedIndependentModelLight", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        if (light.name.StartsWith("Point light", StringComparison.OrdinalIgnoreCase)) return 1;
        for (Transform t = light.transform.parent; t != null; t = t.parent)
        {
            if (t.name.IndexOf("[Replacement]", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            DaggerfallBillboard billboard = t.GetComponent<DaggerfallBillboard>();
            if (billboard != null && billboard.Summary.Archive == 210) return 1;
        }
        return 3;
    }

    private static string KindName(int kind)
    {
        return kind == 0 ? "native" : kind == 1 ? "replacement" : kind == 2 ? "independent" : "other";
    }

    private static bool HasScript(GameObject go, string typeName)
    {
        foreach (MonoBehaviour script in go.GetComponents<MonoBehaviour>())
            if (script != null && script.GetType().Name == typeName) return true;
        return false;
    }

    private static string Scripts(GameObject go)
    {
        StringBuilder result = new StringBuilder();
        foreach (MonoBehaviour script in go.GetComponents<MonoBehaviour>())
        {
            if (result.Length > 0) result.Append(',');
            if (script == null) result.Append("<missing-script>");
            else result.Append(script.GetType().FullName).Append(script.enabled ? ":on" : ":off");
        }
        return result.ToString();
    }

    private void WriteSubscribers(Type publisher, string eventName)
    {
        try
        {
            // Inspect known DFU event backing fields only; no closed-mod code is read or invoked.
            FieldInfo field = publisher.GetField(eventName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Delegate handlers = field != null ? field.GetValue(null) as Delegate : null;
            if (handlers == null)
            {
                Write("HANDLERS " + publisher.Name + "." + eventName + "=<none or unavailable>");
                return;
            }
            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                MethodInfo method = list[i].Method;
                Write("HANDLER " + publisher.Name + "." + eventName + " #" + i + " " +
                    (method.DeclaringType != null ? method.DeclaringType.FullName : "<unknown>") + "." + method.Name);
            }
        }
        catch (Exception ex)
        {
            Write("HANDLERS " + eventName + " unavailable: " + ex.GetType().Name);
        }
    }

    private void OnLogMessage(string message, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (message.StartsWith(Tag, StringComparison.Ordinal) || relatedErrors >= 12) return;
        string text = message + "\n" + stackTrace;
        if (text.IndexOf("ImprovedInteriorLighting", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("ImproveDungeonLighting", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("ImprovedDungeonLight", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("LightFlicker", StringComparison.OrdinalIgnoreCase) < 0) return;
        relatedErrors++;
        Append("RELATED-ERROR " + type + " " + text);
    }

    private void Write(string message)
    {
        Append(message);
        Debug.Log(Tag + message);
    }

    private void Append(string message)
    {
        if (string.IsNullOrEmpty(reportPath)) return;
        try
        {
            File.AppendAllText(reportPath, "frame=" + Time.frameCount + " t=" + Number(Time.realtimeSinceStartup) +
                " " + Tag + message + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            reportPath = null;
            Debug.LogWarning(Tag + "Separate report writing stopped. Use the Unity Console / Editor.log.");
        }
    }

    private static float DistanceSquared(Light light, Vector3 origin)
    {
        return light != null ? (light.transform.position - origin).sqrMagnitude : float.MaxValue;
    }

    private static string Number(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Vec(Vector3 value)
    {
        return "(" + Number(value.x) + "," + Number(value.y) + "," + Number(value.z) + ")";
    }

    private static string ObjectPath(Transform transform)
    {
        if (transform == null) return "<none>";
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }
}
#endif
