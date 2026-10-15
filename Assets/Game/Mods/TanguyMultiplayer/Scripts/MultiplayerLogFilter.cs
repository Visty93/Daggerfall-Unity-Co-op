using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

// Quiet-mode logging for multiplayer/dedicated-server builds.
// This only changes log output. It never changes combat, health, AI, targeting,
// authority, RPCs, spawning, quests, or networking behaviour.
public static class MultiplayerLogFilter
{
    static readonly bool verbose = HasArg("-dfu-verbose-logs");

    // These are diagnostics/status traces rather than gameplay events. In quiet mode
    // they are hidden, while warnings/errors and real attack/health events remain.
    static readonly string[] quietLogPrefixes =
    {
        "[MeleeAnimation]",
        "[RpcPlayAttackAnimation]",
        "[SpawnHealthDbg]",
        "[DedicatedDungeonSpawnTracking]",
        "[DedicatedDungeonCollision]",
        "[EnemyWorldPosition]",
        "[WODMP CombatProbe]",
        "[HostilityTrace]",
        "[ExternalFoeDiag]",
        "[ExternalFoeStateDiag]",
        "[LootPauseProbe]",
        "[QuestNpcDbg]",
        "[CombatCosmetics]",
        "[OnStartClient] SetupDemoEnemy started on client:",
        "[DungeonEnemyAnchor]",
        "[MoveEnemyToRootAfterDelay]",
        "[RequestEnemySettingsWithRetry]",
        "[SetupDemoEnemy][HostilitySync]",
        "[SetupDemoEnemy][LateJoinVisualFallback]",
        "[EnemyDeathTrace][VerticalStep]",
        "[EnemyDeathTrace][DEA]"
    };

    static readonly Dictionary<string, WarningBucket> warningBuckets =
        new Dictionary<string, WarningBucket>(StringComparer.Ordinal);
    static readonly object warningLock = new object();
    const double WarningWindowSeconds = 10.0;

    // In quiet multiplayer/dedicated mode, keep the first occurrence of each exact
    // exception signature and silently suppress identical repeats. This prevents a
    // single per-frame mod exception from growing the server log without hiding new
    // exception types/messages/stacks. Verbose logging bypasses this suppression.
    static readonly HashSet<ulong> seenExceptionSignatures = new HashSet<ulong>();
    static readonly object exceptionLock = new object();

    static StackTraceLogType originalOrdinaryLogStackTrace;
    static bool capturedOrdinaryLogStackTrace;
    static bool ordinaryLogStackTraceQuieted;

    sealed class WarningBucket
    {
        public DateTime WindowStartedUtc;
        public int Suppressed;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (!(Debug.unityLogger.logHandler is FilteredHandler))
            Debug.unityLogger.logHandler = new FilteredHandler(Debug.unityLogger.logHandler);

        // Stack traces on every ordinary Debug.Log line are a major source of giant
        // Editor/Player/server logs. Capture the user's original setting, then quiet only
        // ordinary Log entries while multiplayer/dedicated mode is active. Warnings,
        // errors, asserts, and exceptions keep their normal stack traces.
        if (!capturedOrdinaryLogStackTrace)
        {
            originalOrdinaryLogStackTrace = Application.GetStackTraceLogType(LogType.Log);
            capturedOrdinaryLogStackTrace = true;
        }

        UpdateOrdinaryLogStackTracePolicy(DfuDedicatedServerOptions.Requested);

        if (DfuDedicatedServerOptions.Requested)
            Debug.Log("[DFUDedicated] Logging: " + (verbose
                ? "verbose diagnostics"
                : "quiet diagnostics; warnings/errors and real combat/health events retained"));
    }

    static bool HasArg(string wanted)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool ShouldSuppress(LogType type, string message, bool multiplayer, bool dedicated, bool verboseLogging)
    {
        if (verboseLogging || string.IsNullOrEmpty(message))
            return false;

        // Real errors/assertions/exceptions are never filtered.
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            return false;

        bool networkLogging = multiplayer || dedicated;

        if (type == LogType.Warning)
        {
            // TrafficTrace was deliberately diagnostic instrumentation, but is emitted
            // as a Warning. Do not let it bury real warnings in release logs.
            if (networkLogging && message.StartsWith("[QuestNetSync][TrafficTrace]", StringComparison.Ordinal))
                return true;

            // EnemySenses.Start() can run before PlayerMultiplayer registration/cache is ready.
            // The normal retarget path recovers immediately afterwards, so these per-enemy
            // startup warnings are status noise rather than a fault. Verbose mode restores them.
            if (networkLogging &&
                (message.StartsWith("[EnemySenses] (Server) No PlayerMultiplayer found.", StringComparison.Ordinal) ||
                 message.StartsWith("[EnemySenses] (Client) No PlayerMultiplayer found initially.", StringComparison.Ordinal)))
                return true;

            // Other warnings are preserved. Known high-volume warnings are rate-limited
            // separately by FilteredHandler rather than discarded here.
            return false;
        }

        if (type != LogType.Log || !networkLogging)
            return false;

        // Keep actual combat/health events. In particular, DO NOT broadly filter:
        // [EnemyAttack], [BowDamage], [ApplyDamageToNonPlayer], [DedicatedHealthReport],
        // [HealthSync], Enemy Health Changed, or HealthDrop. CombatCosmetics is verbose-only.
        foreach (string prefix in quietLogPrefixes)
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                return true;

        // A few EnemySenses lines are pure polling/status messages. Changed-target
        // events and all warnings remain visible.
        if (message.StartsWith("[EnemySenses] (Server) Initial Player Set:", StringComparison.Ordinal) ||
            message.StartsWith("[EnemySenses] (Client) Initial Player Set:", StringComparison.Ordinal) ||
            message.StartsWith("[EnemySenses] (Client) Target synced:", StringComparison.Ordinal) ||
            message.StartsWith("[EnemySenses] (Client) MP player candidate synced:", StringComparison.Ordinal))
            return true;

        // Large DFU environment/settings dumps are useful for diagnostics but noisy on
        // a public dedicated server. Normal startup milestones still remain.
        if (dedicated && message.StartsWith("DFTFU ", StringComparison.Ordinal) &&
            message.IndexOf(" | ", StringComparison.Ordinal) >= 0)
            return true;

        return false;
    }

    static void UpdateOrdinaryLogStackTracePolicy(bool networkLogging)
    {
        if (verbose || !capturedOrdinaryLogStackTrace)
            return;

        bool shouldQuiet = networkLogging || DfuDedicatedServerOptions.Requested;
        if (shouldQuiet && !ordinaryLogStackTraceQuieted)
        {
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            ordinaryLogStackTraceQuieted = true;
        }
        else if (!shouldQuiet && ordinaryLogStackTraceQuieted)
        {
            Application.SetStackTraceLogType(LogType.Log, originalOrdinaryLogStackTrace);
            ordinaryLogStackTraceQuieted = false;
        }
    }

    // Returns true when this known noisy warning should be hidden for the current
    // rate-limit window. It also returns a summary message to emit before the next
    // allowed warning, so the fact that repetition occurred is never lost.
    static bool RateLimitWarning(string message, out string summary)
    {
        summary = null;
        string key = null;

        if (message.StartsWith("[MeleeAnimation] ERROR: Client is running MeleeAnimation() when it should not!", StringComparison.Ordinal))
            key = "unexpected-client-melee-animation";
        else if (message.StartsWith("Location loader: Enemy data not found for loadId", StringComparison.Ordinal))
            key = "location-loader-missing-enemy-data";

        if (key == null)
            return false;

        DateTime now = DateTime.UtcNow;
        lock (warningLock)
        {
            WarningBucket bucket;
            if (!warningBuckets.TryGetValue(key, out bucket))
            {
                warningBuckets[key] = new WarningBucket { WindowStartedUtc = now, Suppressed = 0 };
                return false; // first warning is always kept
            }

            double elapsed = (now - bucket.WindowStartedUtc).TotalSeconds;
            if (elapsed < WarningWindowSeconds)
            {
                bucket.Suppressed++;
                return true;
            }

            if (bucket.Suppressed > 0)
                summary = "[MultiplayerLogFilter] Suppressed " + bucket.Suppressed +
                    " repeated '" + key + "' warning(s) during the previous " +
                    Math.Round(elapsed, 1).ToString("0.0") + "s.";

            bucket.WindowStartedUtc = now;
            bucket.Suppressed = 0;
            return false; // keep the first warning of the new window
        }
    }

    static ulong HashException(Exception exception)
    {
        // 64-bit FNV-1a keeps memory bounded even if many different exceptions occur.
        // Include type, message, and throw-site stack so unrelated NullReferenceExceptions
        // are not treated as the same problem.
        const ulong offsetBasis = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;

        AddStringToHash(ref hash, exception.GetType().FullName ?? exception.GetType().Name, prime);
        AddStringToHash(ref hash, "\n", prime);
        AddStringToHash(ref hash, exception.Message ?? string.Empty, prime);
        AddStringToHash(ref hash, "\n", prime);
        AddStringToHash(ref hash, exception.StackTrace ?? string.Empty, prime);

        return hash;
    }

    static void AddStringToHash(ref ulong hash, string value, ulong prime)
    {
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= prime;
        }
    }

    static bool ShouldSuppressRepeatedException(Exception exception, bool networkLogging)
    {
        if (verbose || !networkLogging || exception == null)
            return false;

        ulong signature = HashException(exception);
        lock (exceptionLock)
        {
            // First occurrence is kept. Every identical repeat after that is silent.
            return !seenExceptionSignatures.Add(signature);
        }
    }

    sealed class FilteredHandler : ILogHandler
    {
        readonly ILogHandler inner;
        public FilteredHandler(ILogHandler inner) { this.inner = inner; }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            bool dedicated = DfuDedicatedServerOptions.Requested;
            bool multiplayer = NetworkServer.active || NetworkClient.active;

            if (ShouldSuppressRepeatedException(exception, multiplayer || dedicated))
                return;

            inner.LogException(exception, context);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string message;
            try { message = args == null || args.Length == 0 ? format : string.Format(format, args); }
            catch (FormatException) { inner.LogFormat(logType, context, format, args); return; }

            bool dedicated = DfuDedicatedServerOptions.Requested;
            bool multiplayer = NetworkServer.active || NetworkClient.active;

            UpdateOrdinaryLogStackTracePolicy(multiplayer || dedicated);

            if (ShouldSuppress(logType, message, multiplayer, dedicated, verbose))
                return;

            if (!verbose && logType == LogType.Warning && (multiplayer || dedicated))
            {
                string summary;
                if (RateLimitWarning(message, out summary))
                    return;
                if (!string.IsNullOrEmpty(summary))
                    inner.LogFormat(LogType.Warning, context, "{0}", summary);
            }

            inner.LogFormat(logType, context, format, args);
        }
    }
}
