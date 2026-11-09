// Daggerfall Unity Co-op - existing network enemy integration.
// Works with enemies the co-op runtime has already spawned; never spawns or replaces one.
using System;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using Mirror;
using UnityEngine;

namespace DaggerfallCoop.API
{
    // This is a local snapshot. Resolve the ID again after network despawn/recreation.
    public struct ModNetworkEnemy
    {
        public uint NetId;
        public GameObject GameObject;
        public bool IsSimulationOwner;
        public uint OwnerPlayerId; // 0: server-owned or not known on this peer.
    }

    public static partial class MultiplayerAPI
    {
        // Fires after the DFU enemy and its initial network settings are ready.
        public static event Action<ModNetworkEnemy> NetworkEnemyReady
        {
            add { MultiplayerAPIExistingEnemies.Service.AddReady(value); }
            remove { MultiplayerAPIExistingEnemies.Service.RemoveReady(value); }
        }

        // Only the LOCAL simulation-authority flag is guaranteed to be current on observers.
        public static event Action<ModNetworkEnemy> NetworkEnemyAuthorityChanged
        {
            add { MultiplayerAPIExistingEnemies.Service.AddAuthorityChanged(value); }
            remove { MultiplayerAPIExistingEnemies.Service.RemoveAuthorityChanged(value); }
        }

        public static event Action<ModNetworkEnemy> NetworkEnemyRemoved
        {
            add { MultiplayerAPIExistingEnemies.Service.AddRemoved(value); }
            remove { MultiplayerAPIExistingEnemies.Service.RemoveRemoved(value); }
        }

        // This registration runs for *existing* network enemies, including observer copies.
        // filter must use network/DFU state available on both peers, not presence of a
        // mod component that might be missing on the remote copy.
        // attach runs once per local enemy instance, after initialization; dispose the
        // returned handle on unregister, despawn or session end. Null is permitted.
        public static bool RegisterExistingEnemyBehavior(string registrationId,
            Func<ModNetworkEnemy, bool> filter,
            Func<ModNetworkEnemy, IDisposable> attach,
            Action<ModNetworkEnemy> authorityChanged = null)
        {
            return MultiplayerAPIExistingEnemies.Service.Register(
                registrationId, filter, attach, authorityChanged);
        }

        public static void UnregisterExistingEnemyBehavior(string registrationId)
        {
            MultiplayerAPIExistingEnemies.Service.Unregister(registrationId);
        }

        public static bool TryGetNetworkEnemy(uint enemyNetId, out ModNetworkEnemy enemy)
        {
            return MultiplayerAPIExistingEnemies.Service.TryGet(enemyNetId, out enemy);
        }

        // For regular co-op enemy AI, use this instead of a generic object-authority
        // check: EnemyMotor.hasAuthority is the client-side AI simulation decision.
        public static bool CanSimulateNetworkEnemy(GameObject enemy)
        {
            return MultiplayerAPIExistingEnemies.CanSimulate(enemy);
        }
    }

    [DefaultExecutionOrder(-24500)]
    internal sealed class MultiplayerAPIExistingEnemies : MonoBehaviour
    {
        const float ScanInterval = 0.20f;
        static MultiplayerAPIExistingEnemies instance;
        readonly Dictionary<uint, Tracked> tracked = new Dictionary<uint, Tracked>();
        readonly Dictionary<string, Registration> registrations =
            new Dictionary<string, Registration>(StringComparer.Ordinal);
        readonly HashSet<uint> seen = new HashSet<uint>();
        readonly List<uint> stale = new List<uint>();
        readonly List<Registration> removedRegistrations = new List<Registration>();
        float nextScan;

        Action<ModNetworkEnemy> ready;
        Action<ModNetworkEnemy> changed;
        Action<ModNetworkEnemy> removed;

        sealed class Registration
        {
            public string Id;
            public Func<ModNetworkEnemy, bool> Filter;
            public Func<ModNetworkEnemy, IDisposable> Attach;
            public Action<ModNetworkEnemy> AuthorityChanged;
        }

        sealed class Tracked
        {
            public NetworkIdentity Identity;
            public ModNetworkEnemy Snapshot;
            public readonly Dictionary<string, IDisposable> Handles =
                new Dictionary<string, IDisposable>(StringComparer.Ordinal);
            // Even a declined filter / null handle is evaluated only once per instance.
            public readonly HashSet<string> Attempted = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Matched = new HashSet<string>(StringComparer.Ordinal);
        }

        internal static MultiplayerAPIExistingEnemies Service
        {
            get
            {
                if (instance != null) return instance;
                var existing = UnityEngine.Object.FindObjectOfType<MultiplayerAPIExistingEnemies>();
                if (existing != null) return instance = existing;
                var go = new GameObject("Co-op Existing Enemy API");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<MultiplayerAPIExistingEnemies>();
                return instance;
            }
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            MultiplayerAPI.SessionEnded += OnSessionEnded;
        }

        void OnDestroy()
        {
            if (instance != this) return;
            MultiplayerAPI.SessionEnded -= OnSessionEnded;
            Clear();
            instance = null;
        }

        void OnSessionEnded() { Clear(); nextScan = 0f; }
        void Clear()
        {
            foreach (Tracked item in new List<Tracked>(tracked.Values)) Dispose(item);
            tracked.Clear();
            seen.Clear();
            stale.Clear();
        }

        internal void AddReady(Action<ModNetworkEnemy> callback) { ready += callback; nextScan = 0f; }
        internal void RemoveReady(Action<ModNetworkEnemy> callback) { ready -= callback; }
        internal void AddAuthorityChanged(Action<ModNetworkEnemy> callback) { changed += callback; }
        internal void RemoveAuthorityChanged(Action<ModNetworkEnemy> callback) { changed -= callback; }
        internal void AddRemoved(Action<ModNetworkEnemy> callback) { removed += callback; }
        internal void RemoveRemoved(Action<ModNetworkEnemy> callback) { removed -= callback; }

        internal bool Register(string id, Func<ModNetworkEnemy, bool> filter,
            Func<ModNetworkEnemy, IDisposable> attach, Action<ModNetworkEnemy> authorityChanged)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 || attach == null || registrations.ContainsKey(id))
                return false;
            registrations.Add(id, new Registration { Id = id, Filter = filter,
                Attach = attach, AuthorityChanged = authorityChanged });
            // Apply to enemies which were already discovered before registration.
            foreach (Tracked item in tracked.Values) AttachRegistration(item, registrations[id]);
            nextScan = 0f;
            return true;
        }

        internal void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id) || !registrations.Remove(id)) return;
            foreach (Tracked item in tracked.Values)
            {
                IDisposable handle;
                if (item.Handles.TryGetValue(id, out handle)) SafeDispose(handle);
                item.Handles.Remove(id);
                item.Attempted.Remove(id);
                item.Matched.Remove(id);
            }
        }

        internal bool TryGet(uint id, out ModNetworkEnemy result)
        {
            result = default(ModNetworkEnemy);
            Tracked item;
            if (tracked.TryGetValue(id, out item) && ValidCurrent(item.Identity, id))
            {
                result = Snapshot(item.Identity);
                return true;
            }
            // Public lookup can happen before the first scheduled discovery scan
            // (for example, a mod starts an attack in the first network-ready frame).
            NetworkIdentity identity = null;
            if (NetworkClient.active) NetworkClient.spawned.TryGetValue(id, out identity);
            if (identity == null && NetworkServer.active) NetworkServer.spawned.TryGetValue(id, out identity);
            if (!Ready(identity)) return false;
            result = Snapshot(identity);
            return true;
        }

        static bool ValidCurrent(NetworkIdentity identity, uint id)
        {
            if (identity == null || id == 0 || identity.netId != id || !identity.gameObject.activeInHierarchy)
                return false;
            NetworkIdentity current;
            if (NetworkClient.active && NetworkClient.spawned.TryGetValue(id, out current) && current == identity)
                return true;
            return NetworkServer.active && NetworkServer.spawned.TryGetValue(id, out current) && current == identity;
        }

        static bool Ready(NetworkIdentity identity)
        {
            if (identity == null || identity.netId == 0 || !identity.gameObject.activeInHierarchy ||
                identity.GetComponentInParent<global::PlayerMultiplayer>() != null ||
                identity.GetComponent<DaggerfallEnemy>() == null)
                return false;
            DaggerfallEntityBehaviour entity = identity.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity nativeEnemy = entity == null ? null : entity.Entity as EnemyEntity;
            EnemyMotor motor = identity.GetComponent<EnemyMotor>();
            if (nativeEnemy == null || nativeEnemy.CurrentHealth <= 0 ||
                motor == null || motor.TakeActionHandler == null) return false;
            // The co-op initialization RPC runs AFTER the Mirror spawn on remote clients.
            // Avoid running installed mod Start() with an uninitialized EnemyEntity/visual.
            if (!NetworkServer.active)
            {
                SetupDemoEnemy setup = identity.GetComponent<SetupDemoEnemy>();
                if (setup == null || !setup.HasReceivedInitialServerSettings() ||
                    !setup.IsClientEnemyVisualReadyForAnimation()) return false;
            }
            return true;
        }

        internal static bool CanSimulate(GameObject root)
        {
            if (root == null) return false;
            NetworkIdentity identity = root.GetComponent<NetworkIdentity>();
            if (identity == null || identity.netId == 0) return false;
            if (NetworkServer.active)
                return identity.connectionToClient == null ||
                    identity.connectionToClient == NetworkServer.localConnection;
            if (!NetworkClient.active) return false;
            EnemyMotor motor = root.GetComponent<EnemyMotor>();
            return motor != null && motor.hasAuthority;
        }

        static ModNetworkEnemy Snapshot(NetworkIdentity identity)
        {
            uint owner = 0;
            if (NetworkServer.active && identity.connectionToClient != null &&
                identity.connectionToClient.identity != null)
                owner = identity.connectionToClient.identity.netId;
            else if (NetworkClient.active && identity.hasAuthority)
                owner = MultiplayerAPI.LocalPlayerId;
            return new ModNetworkEnemy {
                NetId = identity.netId, GameObject = identity.gameObject,
                IsSimulationOwner = CanSimulate(identity.gameObject), OwnerPlayerId = owner
            };
        }

        void Update()
        {
            if (!MultiplayerAPI.IsActive)
            {
                if (tracked.Count != 0) Clear();
                return;
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + ScanInterval;
            seen.Clear();
            // Work against the actual spawned network identities, not a scene-wide scan
            // or object names. On host, the server and client dictionaries overlap.
            if (NetworkClient.active)
                foreach (NetworkIdentity identity in NetworkClient.spawned.Values) Examine(identity);
            if (NetworkServer.active)
                foreach (NetworkIdentity identity in NetworkServer.spawned.Values) Examine(identity);
            stale.Clear();
            foreach (var pair in tracked)
                if (!seen.Contains(pair.Key) || !ValidCurrent(pair.Value.Identity, pair.Key))
                    stale.Add(pair.Key);
            foreach (uint id in stale)
            {
                Tracked old;
                if (!tracked.TryGetValue(id, out old)) continue;
                RemoveTracked(old);
                tracked.Remove(id);
            }
        }

        void Examine(NetworkIdentity identity)
        {
            if (!Ready(identity) || !seen.Add(identity.netId)) return;
            Tracked item;
            if (tracked.TryGetValue(identity.netId, out item) && item.Identity != identity)
            {
                RemoveTracked(item);
                tracked.Remove(identity.netId);
                item = null;
            }
            if (item == null)
            {
                item = new Tracked { Identity = identity, Snapshot = Snapshot(identity) };
                tracked[identity.netId] = item;
                Invoke(ready, item.Snapshot);
            }
            else
            {
                ModNetworkEnemy current = Snapshot(identity);
                if (item.Snapshot.IsSimulationOwner != current.IsSimulationOwner ||
                    item.Snapshot.OwnerPlayerId != current.OwnerPlayerId)
                {
                    item.Snapshot = current;
                    Invoke(changed, current);
                    foreach (Registration registration in new List<Registration>(registrations.Values))
                    {
                        if (!item.Matched.Contains(registration.Id)) continue;
                        try { if (registration.AuthorityChanged != null) registration.AuthorityChanged(current); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                }
            }
            foreach (Registration registration in new List<Registration>(registrations.Values)) AttachRegistration(item, registration);
        }

        void AttachRegistration(Tracked item, Registration registration)
        {
            if (item.Attempted.Contains(registration.Id)) return;
            item.Attempted.Add(registration.Id);
            try
            {
                if (registration.Filter != null && !registration.Filter(item.Snapshot)) return;
                IDisposable handle = registration.Attach(item.Snapshot);
                item.Matched.Add(registration.Id);
                if (handle != null) item.Handles.Add(registration.Id, handle);
            }
            catch (Exception error)
            {
                Debug.LogError("[MPAPI] Existing enemy behavior '" + registration.Id +
                    "' failed for enemy " + item.Snapshot.NetId + ": " + error);
            }
        }

        void RemoveTracked(Tracked item)
        {
            // Give listeners the last valid identity and let them perform any bookkeeping.
            Invoke(removed, item.Snapshot);
            Dispose(item);
        }

        static void Dispose(Tracked item)
        {
            foreach (IDisposable handle in item.Handles.Values) SafeDispose(handle);
            item.Handles.Clear();
            item.Attempted.Clear();
            item.Matched.Clear();
        }

        static void SafeDispose(IDisposable handle)
        {
            if (handle == null) return;
            try { handle.Dispose(); }
            catch (Exception error) { Debug.LogException(error); }
        }

        static void Invoke(Action<ModNetworkEnemy> callback, ModNetworkEnemy enemy)
        {
            if (callback == null) return;
            foreach (Delegate listener in callback.GetInvocationList())
            {
                try { ((Action<ModNetworkEnemy>)listener)(enemy); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
