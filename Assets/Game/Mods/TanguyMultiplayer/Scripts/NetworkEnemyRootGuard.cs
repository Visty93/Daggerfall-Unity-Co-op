// Generic post-spawn parenting safeguard for the co-op game.
// Does not spawn/convert enemies, assign authority, or change quest/location policy.
using System;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

/// <summary>
/// Keeps ordinary, registered multiplayer enemies at scene root, including when
/// an external mod reparents them after the normal spawning helper returns.
/// Parent changes are handled late, after existing ownership/membership adapters.
/// </summary>
[DefaultExecutionOrder(31000)]
public sealed class NetworkEnemyRootGuard : MonoBehaviour
{
    const float RepeatedLogInterval = 5f;
    NetworkIdentity identity;
    DynamicEnemyAuthority authority;
    bool detaching;
    bool logged;
    float nextLog;

    // Explicit per-object exception for controllers that genuinely require hierarchy.
    // No names of particular mods, parent objects, or encounter types are hardcoded.
    public bool PreserveHierarchy;
    internal bool ApiPreserveHierarchy;
    public static bool GuardEnabled = true;
    public static long DetachCount { get; private set; }

    // Adapters may inspect/capture the old membership before it is lost. Returning
    // true from KeepParent opts the actor out. Exceptions conservatively keep it.
    public static event Func<NetworkEnemyRootGuard, Transform, bool> KeepParent;
    public static event Action<NetworkEnemyRootGuard, Transform> BeforeDetach;
    public static event Action<NetworkEnemyRootGuard, Transform> AfterDetach;

    public Transform PreviousParent { get; private set; }
    public Vector3 PreviousParentPosition { get; private set; }
    public Quaternion PreviousParentRotation { get; private set; }

    void Awake()
    {
        identity = GetComponent<NetworkIdentity>();
        authority = GetComponent<DynamicEnemyAuthority>();
    }

    void OnTransformParentChanged()
    {
        if (!detaching) enabled = transform.parent != null;
    }

    internal void Schedule()
    {
        if (transform.parent != null) enabled = true;
    }

    internal static void ResetCounters() { DetachCount = 0; }

    static bool Registered(NetworkIdentity candidate)
    {
        if (candidate == null || candidate.netId == 0) return false;
        NetworkIdentity registered;
        return (NetworkServer.active && NetworkServer.spawned.TryGetValue(candidate.netId, out registered) && registered == candidate) ||
               (NetworkClient.active && NetworkClient.spawned.TryGetValue(candidate.netId, out registered) && registered == candidate);
    }

    bool ShouldKeep(Transform parent)
    {
        if (PreserveHierarchy || ApiPreserveHierarchy || authority == null || authority.HasLocalOwnershipClaim ||
            authority.PersistentOwnerPlayerNetId != 0) return true;
        var policy = KeepParent;
        if (policy == null) return false;
        foreach (Func<NetworkEnemyRootGuard, Transform, bool> callback in policy.GetInvocationList())
        {
            try { if (callback(this, parent)) return true; }
            catch (Exception error)
            {
                Debug.LogWarning("[MPEnemyRoot] Parent policy failed; retaining hierarchy: " + error.Message);
                return true;
            }
        }
        return false;
    }

    static bool Notify(Action<NetworkEnemyRootGuard, Transform> handlers, NetworkEnemyRootGuard guard, Transform parent)
    {
        if (handlers == null) return true;
        bool success = true;
        foreach (Action<NetworkEnemyRootGuard, Transform> callback in handlers.GetInvocationList())
        {
            try { callback(guard, parent); }
            catch (Exception error)
            {
                success = false;
                Debug.LogWarning("[MPEnemyRoot] Hierarchy callback failed: " + error.Message);
            }
        }
        return success;
    }

    void LateUpdate()
    {
        // Rooted objects do not need another guard tick. The existing per-enemy
        // service hook wakes this component again if a mod reparents the actor.
        enabled = false;
        if (!GuardEnabled || (!NetworkServer.active && !NetworkClient.active) || !Registered(identity)) return;
        Transform parent = transform.parent;
        if (parent == null || GetComponentInParent<PlayerMultiplayer>() != null || ShouldKeep(parent)) return;

        PreviousParent = parent;
        PreviousParentPosition = parent.position;
        PreviousParentRotation = parent.rotation;
        if (!Notify(BeforeDetach, this, parent)) return;
        // A callback can transfer membership, install an exception, or reparent it.
        if (transform.parent != parent || ShouldKeep(parent)) return;

        detaching = true;
        try
        {
            // Unity preserves the world pose/scale. Do not rewrite localPosition,
            // DF coordinates, spawn stamps, authority, health, AI or network buffers.
            transform.SetParent(null, true);
        }
        finally { detaching = false; }
        DetachCount++;
        if (!logged || Time.unscaledTime >= nextLog)
        {
            logged = true; nextLog = Time.unscaledTime + RepeatedLogInterval;
            Debug.Log("[MPEnemyRoot] Detached network enemy '" + gameObject.name + "' net=" + identity.netId +
                " from '" + parent.name + "'; world transform preserved.");
        }
        Notify(AfterDetach, this, parent);
        // An after-callback may intentionally parent it again. Reconsider next frame,
        // rather than recursively fighting a script inside the same parent callback.
        Schedule();
    }
}

internal sealed class NetworkEnemyRootGuardService : MonoBehaviour
{
    static NetworkEnemyRootGuardService instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("Network Enemy Root Guard");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<NetworkEnemyRootGuardService>();
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        GameManager.OnEnemySpawn += OnEnemySpawn;
        DynamicEnemyAuthority.LocalActorUpdate += OnActorUpdate;
        global::Mirror.NetworkManager.ClientSpawnApplied += OnClientSpawn;
        global::Mirror.NetworkManager.SessionStopped += NetworkEnemyRootGuard.ResetCounters;
    }

    void OnDestroy()
    {
        if (instance != this) return;
        GameManager.OnEnemySpawn -= OnEnemySpawn;
        DynamicEnemyAuthority.LocalActorUpdate -= OnActorUpdate;
        global::Mirror.NetworkManager.ClientSpawnApplied -= OnClientSpawn;
        global::Mirror.NetworkManager.SessionStopped -= NetworkEnemyRootGuard.ResetCounters;
        instance = null;
    }

    static void OnEnemySpawn(GameObject enemy) { AttachIfParented(enemy); }
    static void OnClientSpawn(NetworkIdentity enemy) { if (enemy != null) AttachIfParented(enemy.gameObject); }
    static void OnActorUpdate(DynamicEnemyAuthority enemy)
    {
        // O(1) root fast path; no FindObjectsOfType, network dictionary scans, or
        // per-frame allocations across the dungeon/exterior enemy population.
        if (enemy == null || enemy.transform.parent == null || !NetworkEnemyRootGuard.GuardEnabled ||
            (!NetworkServer.active && !NetworkClient.active)) return;
        AttachIfParented(enemy.gameObject);
    }

    static void AttachIfParented(GameObject enemy)
    {
        if (enemy == null || enemy.transform.parent == null || !NetworkEnemyRootGuard.GuardEnabled ||
            (!NetworkServer.active && !NetworkClient.active)) return;
        if (enemy.GetComponentInParent<PlayerMultiplayer>() != null) return;
        if (enemy.GetComponent<DynamicEnemyAuthority>() == null ||
            (enemy.GetComponent<DaggerfallEnemy>() == null && enemy.GetComponent<SetupDemoEnemy>() == null)) return;
        var guard = enemy.GetComponent<NetworkEnemyRootGuard>();
        if (guard == null) guard = enemy.AddComponent<NetworkEnemyRootGuard>();
        guard.Schedule();
    }
}
