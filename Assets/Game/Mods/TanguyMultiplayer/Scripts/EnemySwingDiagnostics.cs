using System;
using System.IO;
using System.Text;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

// Opt-in evidence capture for the mystery enemy attack animation.
// It never changes AI, state, targeting, damage, authority or networking.
public sealed class EnemySwingDiagnostics : MonoBehaviour
{
    static bool requested;
    static bool failed;
    static StreamWriter writer;
    static float started = -1;
    static int lines;
    const int MaxLines = 10000;
    const float MaxSeconds = 900f;       // 15 minutes
    const float SampleSeconds = 5f;      // low-volume background snapshot
    float nextSample;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        try
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            requested = File.Exists(Path.Combine(root, "SwingDiagnostics.enabled")) ||
                HasArg("-dfu-swing-diagnostics");
            if (!requested) return;

            var go = new GameObject("Enemy swing diagnostics");
            DontDestroyOnLoad(go);
            go.AddComponent<EnemySwingDiagnostics>();
        }
        catch (Exception e) { Disable(e); }
    }

    static bool HasArg(string wanted)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static bool Active()
    {
        if (!requested || failed || lines >= MaxLines ||
            (!NetworkServer.active && !NetworkClient.active)) return false;
        if (started < 0) started = Time.realtimeSinceStartup;
        return Time.realtimeSinceStartup - started <= MaxSeconds;
    }

    // Hooked from MobileUnit.ChangeEnemyState path. This catches every actual entry
    // to or exit from PrimaryAttack, including transitions that did not come from the
    // normal EnemyAttack RPC path.
    public static void RecordTransition(MobileUnit mobile, MobileStates before, MobileStates after)
    {
        if (!Active()) return;
        try
        {
            if (before != MobileStates.PrimaryAttack && after != MobileStates.PrimaryAttack) return;
            string stack = new System.Diagnostics.StackTrace(1, false).ToString()
                .Replace("\r", "").Replace("\n", " | ");
            Write("STATE " + before + "->" + after + " " + Describe(mobile) + " caller=" + stack);
        }
        catch (Exception e) { Disable(e); }
    }

    // Should be zero after the FixedUpdate client-initiation fix. If this appears,
    // another caller is still trying to start local melee on a pure client.
    public static void RecordUnexpectedClientMelee(EnemyAttack attack)
    {
        if (!Active() || attack == null) return;
        try
        {
            string stack = new System.Diagnostics.StackTrace(1, false).ToString()
                .Replace("\r", "").Replace("\n", " | ");
            Write("UNEXPECTED_CLIENT_MELEE " + Describe(attack.gameObject) + " caller=" + stack);
        }
        catch (Exception e) { Disable(e); }
    }

    // Called only after server/SP melee range/sight/yaw checks passed.
    public static void RecordAcceptedMelee(EnemyAttack attack)
    {
        if (!Active() || attack == null) return;
        try { Write("MELEE_ACCEPTED " + Describe(attack.gameObject)); }
        catch (Exception e) { Disable(e); }
    }

    // phase is SERVER_SEND or CLIENT_RECEIVE. Matching netId/timestamps tell us
    // whether a visible client swing came from the server RPC or another state path.
    public static void RecordAttackRpc(EnemyAttack attack, string phase)
    {
        if (!Active() || attack == null) return;
        try { Write("RPC_" + phase + " " + Describe(attack.gameObject)); }
        catch (Exception e) { Disable(e); }
    }

    void Update()
    {
        if (!Active() || Time.realtimeSinceStartup < nextSample) return;
        nextSample = Time.realtimeSinceStartup + SampleSeconds;
        try
        {
            // Low-frequency fallback sample. Event hooks above capture short swings;
            // this snapshot helps reveal stale target/sight/state when nothing fires.
            foreach (var mobile in UnityEngine.Object.FindObjectsOfType<MobileUnit>())
            {
                if (lines >= MaxLines) break;
                if (!mobile.IsSetup || mobile.GetComponentInParent<EnemySenses>() == null) continue;
                Write("SAMPLE " + Describe(mobile));
            }
            if (writer != null) writer.Flush();
        }
        catch (Exception e) { Disable(e); }
    }

    static string Describe(GameObject go)
    {
        if (!go) return "object=<null>";
        MobileUnit mobile = go.GetComponentInChildren<MobileUnit>();
        if (!mobile) mobile = go.GetComponentInParent<MobileUnit>();
        if (mobile) return Describe(mobile);

        var senses = go.GetComponent<EnemySenses>();
        var identity = go.GetComponent<NetworkIdentity>();
        return "net=" + (identity ? identity.netId.ToString() : "none") +
            " name=" + go.name +
            " authority=" + (identity && identity.hasAuthority) +
            " target=" + (senses && senses.Target ? senses.Target.name : "none") +
            " targetNet=" + TargetId(senses) +
            " sight=" + (senses && senses.TargetInSight) +
            " distance=" + (senses ? senses.DistanceToTarget.ToString("F3") : "none");
    }

    static string Describe(MobileUnit mobile)
    {
        var senses = mobile.GetComponentInParent<EnemySenses>();
        var motor = mobile.GetComponentInParent<EnemyMotor>();
        var identity = mobile.GetComponentInParent<NetworkIdentity>();
        var classic = mobile as DaggerfallMobileUnit;
        var records = new StringBuilder();
        var anims = mobile.Summary.StateAnims;
        if (anims != null)
            foreach (var anim in anims) records.Append(anim.Record).Append(',');

        return "net=" + (identity ? identity.netId.ToString() : "none") +
            " instance=" + mobile.GetInstanceID() + " name=" + mobile.name +
            " type=" + mobile.Enemy.ID + " state=" + mobile.EnemyState +
            " frame=" + (classic ? classic.CurrentFrame.ToString() : "custom") +
            " records=" + records + " frozen=" + mobile.FreezeAnims +
            " pos=" + mobile.transform.position.ToString("F3") +
            " authority=" + (identity && identity.hasAuthority) +
            " target=" + (senses && senses.Target ? senses.Target.name : "none") +
            " targetNet=" + TargetId(senses) +
            " sight=" + (senses && senses.TargetInSight) +
            " distance=" + (senses ? senses.DistanceToTarget.ToString("F3") : "none") +
            " bashing=" + (motor && motor.Bashing) +
            " door=" + (senses && senses.LastKnownDoor ? senses.LastKnownDoor.name : "none");
    }

    static string TargetId(EnemySenses senses)
    {
        if (!senses || !senses.Target) return "none";
        var id = senses.Target.GetComponentInParent<NetworkIdentity>();
        return id ? id.netId.ToString() : "local";
    }

    static void Write(string message)
    {
        if (lines >= MaxLines) return;
        if (writer == null)
        {
            string path = Path.Combine(Application.persistentDataPath,
                "EnemySwing_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_PID" +
                System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
            writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.AutoFlush = true;
            Debug.Log("[EnemySwingDiagnostics] Writing " + path);
        }
        writer.WriteLine(DateTime.UtcNow.ToString("O") + " t=" + Time.realtimeSinceStartup.ToString("F3") +
            " server=" + NetworkServer.active + " client=" + NetworkClient.active + " " + message);
        lines++;
    }

    static void Disable(Exception e)
    {
        if (!failed) Debug.LogWarning("[EnemySwingDiagnostics] Capture disabled: " + e.Message);
        failed = true;
        if (writer != null) { try { writer.Dispose(); } catch { } writer = null; }
    }

    void OnApplicationQuit()
    {
        if (writer != null) { writer.Dispose(); writer = null; }
    }
}
