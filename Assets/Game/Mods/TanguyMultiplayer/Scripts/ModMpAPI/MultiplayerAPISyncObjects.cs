// Declarative synchronization of mod-created world objects.
// Gameplay rules and replica assets remain with the registering mod.
using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;

namespace DaggerfallCoop.API
{
    [Flags]
    public enum ModSyncFlags
    {
        None = 0,
        Pickable = 1,
        AutoPickupOnClick = 2,
        KeepOriginalParent = 4,
        CarryLocalPlayer = 8,
    }

    [Serializable]
    public sealed class ModSyncOptions
    {
        public ModSyncFlags Flags = ModSyncFlags.None;
        public ModWorldMotion Motion = ModWorldMotion.Fixed;
        public float LifetimeSeconds = 1800f;
        public float VisibilityMetres = 250f;
    }

    // Add to a particular COLLIDER on a replica prefab to identify the walkable deck.
    // A single collider on the prefab root can also be used without this marker.
    public sealed class ModSyncCarrySurface : MonoBehaviour { }

    [Serializable]
    internal sealed class ModSyncEnvelope
    {
        public int Version = 1;
        public int Flags;
        public string Payload;
    }

    // Optional Unity component for mods that prefer configuring sync on their objects.
    // Configure() before the object begins gameplay; the marker waits for MP readiness.
    public sealed class ModSyncMarker : MonoBehaviour
    {
        // Public fields can be assigned in the Unity Inspector on an authored object.
        // Dynamic spawns must set a UNIQUE key for each instance before enabling it.
        public string WorldGuid;
        public string Key;
        public string Kind;
        public string CustomData;
        public ModSyncOptions Options = new ModSyncOptions();
        ModSyncWorld world;
        bool submitted;
        float nextAttempt;

        public void Configure(ModSyncWorld syncWorld, string objectKey, string objectKind,
            ModSyncOptions syncOptions, string customData = null)
        {
            if (submitted) throw new InvalidOperationException("This sync marker was already submitted.");
            if (syncWorld == null) throw new ArgumentNullException("syncWorld");
            world = syncWorld; WorldGuid = syncWorld.Guid;
            Key = objectKey; Kind = objectKind; Options = syncOptions; CustomData = customData;
        }

        void Update()
        {
            if (submitted || !MultiplayerAPI.IsActive || Time.unscaledTime < nextAttempt) return;
            // The marker is for SOURCE objects only. A prefab replica must never
            // publish itself again as another source object.
            var binding = GetComponentInParent<ModWorldBinding>();
            if (binding != null && !binding.Source) { enabled = false; return; }
            if (world == null) world = ModSyncWorld.Find(WorldGuid);
            if (world == null || !world.Ready) return;
            submitted = true;
            try
            {
                if (!world.Register(gameObject, Key, Kind, Options, CustomData, (ok, record) => {
                    if (!ok) Debug.LogWarning("[MPAPI sync] Registration failed for " + Key);
                })) Debug.LogWarning("[MPAPI sync] Cannot submit marked object " + Key);
            }
            catch (InvalidOperationException error)
            {
                // A freshly loaded exterior/interior may not yet have a location frame.
                if (error.Message == "World location is not ready.")
                { submitted = false; nextAttempt = Time.unscaledTime + 1f; return; }
                Debug.LogError("[MPAPI sync] " + Key + ": " + error);
            }
            catch (Exception error) { Debug.LogError("[MPAPI sync] " + Key + ": " + error); }
        }
    }

    // Links are local-only: never serialized or sent to the server.
    public sealed class ModSyncVisualLinks : MonoBehaviour
    {
        internal ModSyncWorld World;
        internal string Key;
        internal ModSyncFlags Flags;
        internal string LastPayload;
        internal bool Applied;
        internal ModMovingFrameHandle CarryHandle;

        void OnDestroy()
        {
            if (CarryHandle != null) { CarryHandle.Dispose(); CarryHandle = null; }
        }
    }

    // The stock DFU activation code searches the exact collider object, not its parent.
    // Only attach on colliders which do not already implement IPlayerActivable.
    public sealed class ModSyncPickupClick : MonoBehaviour, IPlayerActivable
    {
        internal ModSyncVisualLinks Links;
        public void Activate(RaycastHit hit)
        {
            if (Links == null || Links.World == null || hit.distance > 3.5f || GameManager.IsGamePaused) return;
            Links.World.Take(Links.Key);
        }
    }

    public static partial class MultiplayerAPI
    {
        /// <summary>
        /// Simple object registration. Each mod calls this once using its own GUID,
        /// then registers its shared prefabs (or custom factories) on all peers.
        /// Requires co-op to be active when objects are registered.
        /// </summary>
        public static ModSyncWorld OpenSyncWorld(string modGuid)
        {
            return new ModSyncWorld(modGuid);
        }
    }

    public sealed class ModSyncWorld : IDisposable
    {
        static readonly Dictionary<string, ModSyncWorld> open = new Dictionary<string, ModSyncWorld>(StringComparer.Ordinal);
        public string Guid { get; private set; }
        internal static ModSyncWorld Find(string guid)
        {
            ModSyncWorld result;
            return !string.IsNullOrEmpty(guid) && open.TryGetValue(guid, out result) ? result : null;
        }

        sealed class VisualType
        {
            public GameObject Prefab;
            public Func<ModWorldRecord, GameObject, GameObject> Factory;
            public Action<GameObject, ModWorldRecord, string> ApplyData;
            public Action<ModWorldRecord> OnPickedUp;
        }

        readonly ModWorld world;
        readonly Dictionary<string, VisualType> visuals = new Dictionary<string, VisualType>(StringComparer.Ordinal);
        readonly HashSet<string> pendingTakes = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> pendingCreates = new HashSet<string>(StringComparer.Ordinal);
        bool disposed;

        internal ModSyncWorld(string guid)
        {
            world = MultiplayerAPI.OpenWorld(guid, BuildReplica);
            Guid = guid;
            open.Add(guid, this);
            MultiplayerAPI.SessionEnded += SessionEnded;
        }

        public bool Ready { get { return !disposed && world.Ready; } }
        public ModWorldRecord[] Snapshot { get { return world.Snapshot; } }
        public bool TryGetObject(string key, out GameObject obj) { return world.TryGetObject(key, out obj); }

        /// <summary>
        /// The prefab is a local template, not a networked prefab. Each client must
        /// have the same mod assets and register this kind before replicas arrive.
        /// onPickedUp grants the appropriate mod inventory item on the winning peer.
        /// </summary>
        public void RegisterPrefab(string kind, GameObject prefab, Action<ModWorldRecord> onPickedUp = null,
            Action<GameObject, ModWorldRecord, string> applyData = null)
        {
            if (prefab == null) throw new ArgumentNullException("prefab");
            AddVisual(kind, new VisualType { Prefab = prefab, OnPickedUp = onPickedUp, ApplyData = applyData });
        }

        /// <summary>
        /// For procedural objects. A provided existing object must be updated in place.
        /// </summary>
        public void RegisterFactory(string kind, Func<ModWorldRecord, GameObject, GameObject> factory,
            Action<ModWorldRecord> onPickedUp = null,
            Action<GameObject, ModWorldRecord, string> applyData = null)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            AddVisual(kind, new VisualType { Factory = factory, OnPickedUp = onPickedUp, ApplyData = applyData });
        }

        void AddVisual(string kind, VisualType entry)
        {
            if (disposed) throw new ObjectDisposedException("ModSyncWorld");
            if (string.IsNullOrEmpty(kind) || kind.Length > 48 || kind.IndexOfAny(new []{'\r','\n'}) >= 0)
                throw new ArgumentException("Object kind must be 1-48 characters without newlines.");
            if (visuals.ContainsKey(kind)) throw new InvalidOperationException("Object kind already registered: " + kind);
            visuals.Add(kind, entry);
        }

        public bool Register(GameObject source, string key, string kind, ModSyncOptions options = null,
            string customData = null, Action<bool, ModWorldRecord> completed = null)
        {
            if (!Ready || source == null || string.IsNullOrEmpty(key) || key.Length > 64 ||
                string.IsNullOrEmpty(kind) || pendingCreates.Contains(key)) return false;
            if (source.GetComponent<ModWorldBinding>() != null)
                throw new InvalidOperationException("This GameObject is already registered with a ModWorld.");
            if (customData != null && customData.Length > 7000)
                throw new ArgumentOutOfRangeException("customData", "Payload exceeds the registration limit.");
            VisualType entry;
            if (!visuals.TryGetValue(kind, out entry)) throw new InvalidOperationException("RegisterPrefab or RegisterFactory first: " + kind);
            var settings = Validate(options);
            if ((settings.Flags & ModSyncFlags.AutoPickupOnClick) != 0 && entry.OnPickedUp == null)
                throw new InvalidOperationException("AutoPickupOnClick requires a pickup handler registered for this kind.");
            string data = Pack(settings.Flags, customData);
            if (data.Length > 8192)
                throw new ArgumentOutOfRangeException("customData", "Serialized sync data exceeds 8192 characters.");
            pendingCreates.Add(key);
            try
            {
                world.Publish(source, key, kind, data,
                    (settings.Flags & ModSyncFlags.KeepOriginalParent) != 0,
                    (settings.Flags & ModSyncFlags.Pickable) != 0,
                    settings.LifetimeSeconds, (ok, record) => {
                        pendingCreates.Remove(key);
                        if (ok && record != null)
                        {
                            if (source != null) ConfigureLinks(source, record, settings.Flags, customData, true);
                            else world.Remove(key); // Source disappeared before the create reply.
                        }
                        if (completed != null) completed(ok, record);
                    }, settings.Motion, settings.VisibilityMetres);
            }
            catch { pendingCreates.Remove(key); throw; }
            return true;
        }

        public void UpdateData(string key, string data)
        {
            ModWorldRecord record = null;
            foreach (var item in world.Snapshot)
                if (item.Key == key) { record = item; break; }
            if (record == null || record.Type != ModWorldObjectType.Visual) return;
            ModSyncEnvelope old = Unpack(record.Data);
            if (data != null && data.Length > 7000)
                throw new ArgumentOutOfRangeException("data", "Payload exceeds the update limit.");
            string packed = Pack((ModSyncFlags)old.Flags, data);
            if (packed.Length > 8192)
                throw new ArgumentOutOfRangeException("data", "Serialized sync data exceeds 8192 characters.");
            world.UpdateData(key, packed);
        }

        public void Remove(string key) { world.Remove(key); }
        public void Claim(string key, Action<bool, ModWorldRecord> complete) { world.Claim(key, complete); }
        // The underlying record's Data contains the sync metadata envelope.
        // Call this to obtain the mod's original payload (for pickup, state, etc.).
        public static string GetCustomData(ModWorldRecord record)
        {
            return record == null ? null : Unpack(record.Data).Payload;
        }

        // Returns false for an unregistered kind or an already pending local pickup.
        // The underlying ModWorld.Take performs one-winner server validation.
        public bool Take(string key)
        {
            if (!Ready || string.IsNullOrEmpty(key) || !pendingTakes.Add(key)) return false;
            ModWorldRecord candidate = null;
            foreach (var record in world.Snapshot)
                if (record.Key == key && !record.Deleted) { candidate = record; break; }
            VisualType entry;
            if (candidate == null || !candidate.Pickable || candidate.Type != ModWorldObjectType.Visual ||
                !visuals.TryGetValue(candidate.Kind, out entry) || entry.OnPickedUp == null)
            { pendingTakes.Remove(key); return false; }
            world.Take(key, (ok, record) => {
                pendingTakes.Remove(key);
                if (!ok || record == null) return;
                try { entry.OnPickedUp(record); }
                catch (Exception e) { Debug.LogError("[MPAPI sync] Pickup callback failed for " + key + ": " + e); }
            });
            return true;
        }

        GameObject BuildReplica(ModWorldRecord record, GameObject existing)
        {
            if (record == null || record.Type != ModWorldObjectType.Visual) return existing;
            VisualType entry;
            if (!visuals.TryGetValue(record.Kind, out entry)) return null;
            GameObject obj = existing;
            bool created = false;
            try
            {
                if (entry.Factory != null) obj = entry.Factory(record, existing);
                else if (obj == null && entry.Prefab != null)
                {
                    obj = UnityEngine.Object.Instantiate(entry.Prefab);
                    // In case the local prefab itself is marked for source sync,
                    // keep that marker disabled on its receiver-side visual copy.
                    foreach (var marker in obj.GetComponentsInChildren<ModSyncMarker>(true))
                        marker.enabled = false;
                    obj.name = record.Kind + " (MP replica)";
                    obj.SetActive(true);
                }
                if (obj == null) return null;
                created = existing == null;
                if (existing != null && obj != existing)
                    throw new InvalidOperationException("Factory must reuse the existing replica GameObject.");
                var info = Unpack(record.Data);
                ConfigureLinks(obj, record, (ModSyncFlags)info.Flags, info.Payload, false);
                if (entry.ApplyData != null) entry.ApplyData(obj, record, info.Payload);
                return obj;
            }
            catch
            {
                if (created && obj != null) UnityEngine.Object.Destroy(obj);
                throw;
            }
        }

        void ConfigureLinks(GameObject obj, ModWorldRecord record, ModSyncFlags flags, string payload, bool isSource)
        {
            var links = obj.GetComponent<ModSyncVisualLinks>();
            if (links == null) links = obj.AddComponent<ModSyncVisualLinks>();
            links.World = this; links.Key = record.Key; links.Flags = flags;
            links.LastPayload = payload;
            if ((flags & ModSyncFlags.AutoPickupOnClick) != 0 && !links.Applied)
            {
                foreach (Collider collider in obj.GetComponentsInChildren<Collider>(true))
                {
                    if (!collider || !collider.enabled || collider.isTrigger) continue;
                    if (collider.GetComponent<IPlayerActivable>() != null) continue;
                    collider.gameObject.AddComponent<ModSyncPickupClick>().Links = links;
                }
            }
            if (!isSource && (flags & ModSyncFlags.CarryLocalPlayer) != 0 && links.CarryHandle == null)
            {
                Collider surface = null;
                var surfaces = obj.GetComponentsInChildren<ModSyncCarrySurface>(true);
                foreach (var marker in surfaces)
                {
                    var candidate = marker.GetComponent<Collider>();
                    if (candidate != null && candidate.enabled && !candidate.isTrigger) { surface = candidate; break; }
                }
                // A simple, single-root-collider platform needs no extra component.
                if (surface == null && surfaces.Length == 0) surface = obj.GetComponent<Collider>();
                if (surface != null && surface.enabled && !surface.isTrigger)
                    links.CarryHandle = MultiplayerAPI.RegisterLocalPlayerMovingSurface(surface, obj.transform);
                else Debug.LogWarning("[MPAPI sync] CarryLocalPlayer requires a marked deck collider: " + record.Kind);
            }
            links.Applied = true;
        }

        static string Pack(ModSyncFlags flags, string customData)
        {
            return JsonUtility.ToJson(new ModSyncEnvelope { Flags = (int)flags, Payload = customData });
        }

        static ModSyncEnvelope Unpack(string data)
        {
            try
            {
                var envelope = JsonUtility.FromJson<ModSyncEnvelope>(data);
                if (envelope != null && envelope.Version == 1) return envelope;
            }
            catch (Exception) { }
            return new ModSyncEnvelope();
        }

        static ModSyncOptions Validate(ModSyncOptions original)
        {
            var x = original ?? new ModSyncOptions();
            const ModSyncFlags all = ModSyncFlags.Pickable | ModSyncFlags.AutoPickupOnClick |
                ModSyncFlags.KeepOriginalParent | ModSyncFlags.CarryLocalPlayer;
            if ((x.Flags & ~all) != 0 ||
                ((x.Flags & ModSyncFlags.AutoPickupOnClick) != 0 && (x.Flags & ModSyncFlags.Pickable) == 0) ||
                ((x.Flags & ModSyncFlags.CarryLocalPlayer) != 0 && x.Motion == ModWorldMotion.Fixed) ||
                !Enum.IsDefined(typeof(ModWorldMotion), x.Motion) ||
                float.IsNaN(x.LifetimeSeconds) || float.IsInfinity(x.LifetimeSeconds) || x.LifetimeSeconds < 0 || x.LifetimeSeconds > 86400 ||
                float.IsNaN(x.VisibilityMetres) || float.IsInfinity(x.VisibilityMetres) || x.VisibilityMetres < 1 || x.VisibilityMetres > 3000)
                throw new ArgumentOutOfRangeException("options", "Invalid synchronization options.");
            return new ModSyncOptions { Flags = x.Flags, Motion = x.Motion,
                LifetimeSeconds = x.LifetimeSeconds, VisibilityMetres = x.VisibilityMetres };
        }

        void SessionEnded() { pendingTakes.Clear(); pendingCreates.Clear(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            MultiplayerAPI.SessionEnded -= SessionEnded;
            pendingTakes.Clear(); pendingCreates.Clear(); visuals.Clear();
            open.Remove(Guid);
            world.Dispose();
        }
    }
}
