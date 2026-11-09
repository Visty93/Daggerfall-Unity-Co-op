// Generic, opt-in mod object lifecycle. No mod names, quest hooks or asset-specific code.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallConnect;

namespace DaggerfallCoop.API
{
    public enum ModWorldMotion { Default, Fixed, Smooth }

    public enum ModWorldObjectType { Visual, NativeEnemy, Container, Claim }

    [Serializable]
    public sealed class ModWorldPose
    {
        public string Area;
        public MultiplayerWorldPosition Exterior;
        public Vector3 Interior;
        public Vector3 ReferenceInterior, ServerInteriorPoint;
        public Quaternion FrameRotation;
        public float ReferenceHeight;
        public Quaternion Rotation;
        public static ModWorldPose Capture(Transform obj)
        {
            Transform frame;
            string area = LootCatcher.CurrentCorpseArea(out frame);
            if (area == null || frame == null) throw new InvalidOperationException("World location is not ready.");
            var gm = GameManager.Instance;
            return new ModWorldPose { Area = area, Rotation = area == "E" ? obj.rotation : Quaternion.Inverse(frame.rotation)*obj.rotation,
                Interior = area == "E" ? Vector3.zero : frame.InverseTransformPoint(obj.position),
                ReferenceInterior = frame.InverseTransformPoint(gm.PlayerObject.transform.position), FrameRotation = frame.rotation,
                ReferenceHeight = gm.PlayerObject.transform.position.y-gm.StreamingWorld.WorldCompensation.y,
                Exterior = MultiplayerWorldPosition.FromLocal(obj.position, gm.PlayerObject.transform.position,
                    gm.PlayerGPS.WorldX, gm.PlayerGPS.WorldZ, gm.StreamingWorld.WorldCompensation.y) };
        }
        public bool TryLocal(out Vector3 point, out Quaternion rotation)
        {
            point = Vector3.zero; rotation = Rotation;
            Transform frame;
            if (LootCatcher.CurrentCorpseArea(out frame) != Area || frame == null) return false;
            var gm = GameManager.Instance;
            if (Area == "E") point = Exterior.ToLocal(gm.PlayerObject.transform.position,
                gm.PlayerGPS.WorldX, gm.PlayerGPS.WorldZ, gm.StreamingWorld.WorldCompensation.y);
            else { point = frame.TransformPoint(Interior); rotation = frame.rotation*Rotation; }
            return true;
        }
    }

    [Serializable]
    public sealed class ModWorldRecord
    {
        public string BehaviorId, BehaviorData;
        public ModEnemyParenting Parenting;
        public string Key, Kind, Data;
        public ModWorldObjectType Type;
        public ModWorldPose Pose;
        public uint OwnerPlayerId, NetworkId, ContainerId, OwnerEpoch;
        public int ContainerVersion;
        public string ContainerItems;
        public bool RemoveContainerWhenEmpty;
        public float VisibilityMetres = 250f;
        public float LifetimeSeconds = 1800f;
        public ModWorldMotion Motion;
        public bool Pickable, Deleted, ScriptedEnemy, KeepParent, TransientEnemy;
    }

    [Serializable]
    public sealed class ModEnemyOptions
    {
        public string BehaviorId, BehaviorData;
        public ModEnemyParenting Parenting;
        public MobileTypes Mobile;
        public MobileReactions Reaction = MobileReactions.Passive;
        public MobileGender Gender = MobileGender.Male;
        public bool AlliedToPlayer = true;
        public int Team = -1;
        public int CurrentHealth;
        // Zero uses the default 100 m enemy request limit. Set 1–400 m to override.
        public float ExteriorRequestMetres;
        // Zero uses the normal enemy distance policy. Exterior only; metres.
        public float ExteriorRetentionMetres;
        // Bind scripted routes to the spawner, then ReleaseEnemy() to resume normal AI authority.
        public bool ScriptedOwner = true;
        public bool Transient = true;
    }

    public static partial class MultiplayerAPI
    {
        // Install the same factories on every participant, including a dedicated server.
        // Factory runs only when a visual is locally relevant; native enemy factories decorate
        // an existing network enemy and must return that same GameObject.
        public static ModWorld OpenWorld(string modGuid, Func<ModWorldRecord, GameObject, GameObject> factory)
        { return ModWorld.Open(modGuid, factory); }
    }

    [Serializable]
    internal sealed class ModWorldRequest
    {
        public string Request, Operation, Key;
        public ModWorldRecord Record;
        public ModEnemyOptions Enemy;
        public bool Converted;
        public uint Epoch;
    }
    [Serializable]
    internal sealed class ModWorldReply
    {
        public string Request, Error;
        public uint Epoch;
        public bool Success;
        public ModWorldRecord Record;
    }

    // The source binding stays alive while only its renderers/colliders are hidden.
    // Destroying a source removes shared state; hiding/unloading a replica never does.
    public sealed class ModWorldBinding : MonoBehaviour
    {
        internal ModWorld World;
        internal bool Source, Removing;
        internal ModWorldRecord State;
        internal Vector3 LastCompensation;
        internal Vector3 ReplicaFrom, ReplicaTo;
        internal Quaternion ReplicaFromRotation, ReplicaToRotation;
        internal float ReplicaStarted;
        internal ModWorldPose ReplicaPose;
        internal float SendAt;
        internal ModWorldPose LastPose;
        internal Renderer[] Renderers;
        internal Collider[] Colliders;
        internal bool[] RendererStates, ColliderStates;
        internal bool Hidden, ContainerReady;
        internal bool AreaAvailable = true;
        internal Vector3 AreaOrigin;
        internal Quaternion AreaRotation;
        public string Key { get { return State == null ? null : State.Key; } }
        public bool IsOwner { get { return Source; } }
        public ModWorldRecord Record { get { return State; } }
        internal IDisposable RunningBehavior;
        internal bool BehaviorAttempted;
        internal void StopEnemyBehavior()
        {
            var running = RunningBehavior;
            RunningBehavior = null;
            BehaviorAttempted = false;
            try { if (running != null) running.Dispose(); }
            catch (Exception error) { Debug.LogException(error); }
        }
        void OnDestroy()
        {
            StopEnemyBehavior();
            if (World != null) World.BindingDestroyed(this);
        }
    }

    public sealed class ModWorldContainerGate : MonoBehaviour
    {
        Collider[] colliders;
        bool[] enabledStates;
        public void Hold()
        {
            if (colliders != null) return;
            colliders = GetComponentsInChildren<Collider>(true); enabledStates = new bool[colliders.Length];
            for (int i=0;i<colliders.Length;i++) { enabledStates[i] = colliders[i].enabled; colliders[i].enabled = false; }
        }
        public void Release()
        {
            if (colliders == null) return;
            for (int i=0;i<colliders.Length;i++) if(colliders[i] != null) colliders[i].enabled = enabledStates[i];
            colliders = null; enabledStates = null;
        }
    }

    [DefaultExecutionOrder(-24000)]
    internal sealed class ModWorldDriver : MonoBehaviour
    {
        internal ModWorld World;
        void Update() { if (World != null) World.Tick(); }
        void LateUpdate() { if (World != null) World.PublishPoses(); }
    }

    public sealed partial class ModWorld : IDisposable
    {
        const string RequestChannel = "world-request-v3", ReplyChannel = "world-reply-v3";
        const int MaxObjects = 512, MaxReplies = 4096;
        static readonly Dictionary<string, ModWorld> worlds = new Dictionary<string, ModWorld>();
        readonly string mod;
        readonly Func<ModWorldRecord, GameObject, GameObject> factory;
        readonly Dictionary<string, ModWorldRecord> records = new Dictionary<string, ModWorldRecord>();
        readonly Dictionary<string, ModWorldRecord> server = new Dictionary<string, ModWorldRecord>();
        readonly Dictionary<string, float> expires = new Dictionary<string, float>();
        readonly Dictionary<string, ModWorldBinding> bindings = new Dictionary<string, ModWorldBinding>();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly Dictionary<string, ModWorldReply> replies = new Dictionary<string, ModWorldReply>();
        readonly Queue<string> replyOrder = new Queue<string>();
        readonly Dictionary<uint, uint> serverEpochs = new Dictionary<uint, uint>();
        uint localEpoch;
        bool resetNeeded, resetRequestPending;
        readonly Dictionary<string, ulong> revisions = new Dictionary<string, ulong>();
        readonly Dictionary<string, GameObject> staged = new Dictionary<string, GameObject>();
        readonly ModWorldDriver driver;
        bool disposed, loading;
        float nextServerPoll;
        sealed class Pending
        {
            public ModWorldRequest Message;
            public Action<bool, ModWorldRecord> Callback;
            public float Retry;
            public GameObject Source;
            public bool KeepParent;
        }
        internal static ModWorld Open(string guid, Func<ModWorldRecord, GameObject, GameObject> factory)
        {
            if (string.IsNullOrEmpty(guid) || guid.Length > MultiplayerAPI.MaxGuidLength || guid.IndexOfAny(new[]{'\r','\n'}) >= 0 || factory == null)
                throw new ArgumentException("A valid unique mod GUID and factory are required.");
            if (worlds.ContainsKey(guid)) throw new InvalidOperationException("Mod world already registered: " + guid);
            var world = new ModWorld(guid, factory); worlds.Add(guid, world); return world;
        }
        ModWorld(string guid, Func<ModWorldRecord, GameObject, GameObject> create)
        {
            mod = guid; factory = create;
            if (!MultiplayerAPI.RegisterJsonServerHandler<ModWorldRequest>(mod, RequestChannel, HandleRequest) ||
                !MultiplayerAPI.RegisterJsonClientHandler<ModWorldReply>(mod, ReplyChannel, HandleReply) ||
                !MultiplayerAPI.SubscribeState(mod, StateChanged, SnapshotComplete))
                throw new InvalidOperationException("Mod GUID already has world/state handlers: " + mod);
            var go = new GameObject("MP API World " + mod);
            UnityEngine.Object.DontDestroyOnLoad(go);
            driver = go.AddComponent<ModWorldDriver>(); driver.World = this;
            MultiplayerAPI.SessionEnded += EndSession;
        }
        bool TransportReady { get { return !disposed && NetworkClient.active && NetworkClient.ready && MultiplayerAPI.LocalPlayerId != 0; } }
        public bool Ready { get { return TransportReady && !resetNeeded; } }
        public ModWorldRecord[] Snapshot { get { return records.Values.Where(r => !r.Deleted).ToArray(); } }
        public bool TryGetObject(string key, out GameObject obj)
        {
            obj = null; ModWorldBinding binding;
            if (!bindings.TryGetValue(key, out binding) || binding == null) return false;
            obj = binding.gameObject; return true;
        }
        public void Claim(string key, Action<bool, ModWorldRecord> complete)
        {
            Submit(new ModWorldRequest { Operation = "claim", Key = key }, null, false, complete);
        }
        public void Publish(GameObject source, string key, string kind, string data, bool keepParent,
            bool pickable, float lifetimeSeconds, Action<bool, ModWorldRecord> complete)
        {
            Publish(source, key, kind, data, keepParent, pickable, lifetimeSeconds, complete, ModWorldMotion.Default);
        }

        public void Publish(GameObject source, string key, string kind, string data, bool keepParent,
            bool pickable, float lifetimeSeconds, Action<bool, ModWorldRecord> complete, ModWorldMotion motion)
        {
            if (source == null) { if (complete != null) complete(false, null); return; }
            Publish(source, key, kind, data, keepParent, pickable, lifetimeSeconds, complete, motion, 250f);
        }
        // Allows direct registration and declarative synchronization to choose an
        // interest radius. The server validates the requested radius (1-3000m).
        public void Publish(GameObject source, string key, string kind, string data, bool keepParent,
            bool pickable, float lifetimeSeconds, Action<bool, ModWorldRecord> complete,
            ModWorldMotion motion, float visibilityMetres)
        {
            if (source == null) { if (complete != null) complete(false, null); return; }
            var record = new ModWorldRecord { Key = key, Kind = kind, Data = data, Motion = motion, Pose = ModWorldPose.Capture(source.transform),
                Pickable = pickable, LifetimeSeconds = lifetimeSeconds, KeepParent = keepParent, VisibilityMetres = visibilityMetres };
            Submit(new ModWorldRequest { Operation = "create", Key = key, Record = record }, source, keepParent, complete);
        }
        public static void PrepareContainer(DaggerfallLoot source)
        {
            var serializable = source.GetComponent<SerializableLootContainer>();
            if (serializable != null && source.LoadID != 0) SaveLoadManager.DeregisterSerializableGameObject(serializable);
            source.LoadID = 0; // Session objects must not be restored as independent vanilla piles.
            source.ContainerType = LootContainerTypes.RandomTreasure;
            if (source.GetComponent<WorldContainerReplica>() == null) source.gameObject.AddComponent<WorldContainerReplica>();
            var gate = source.GetComponent<ModWorldContainerGate>();
            if (gate == null) gate = source.gameObject.AddComponent<ModWorldContainerGate>();
            gate.Hold();
        }
        public void PublishContainer(DaggerfallLoot source, string key, string kind, string data,
            float lifetimeSeconds, Action<bool, ModWorldRecord> complete)
        {
            PublishContainer(source, key, kind, data, lifetimeSeconds, complete, false);
        }

        public void PublishContainer(DaggerfallLoot source, string key, string kind, string data,
            float lifetimeSeconds, Action<bool, ModWorldRecord> complete, bool removeWhenEmpty)
        {
            PublishContainer(source, key, kind, data, lifetimeSeconds, complete, removeWhenEmpty, ModWorldMotion.Default);
        }

        public void PublishContainer(DaggerfallLoot source, string key, string kind, string data,
            float lifetimeSeconds, Action<bool, ModWorldRecord> complete, bool removeWhenEmpty, ModWorldMotion motion)
        {
            // Mark immediately, before any broad exterior discovery can register this pile.
            PrepareContainer(source);
            var record = new ModWorldRecord { Key = key, Kind = kind, Data = data, Type = ModWorldObjectType.Container,
                Pose = ModWorldPose.Capture(source.transform), ContainerItems = LootCatcher.SerializeLootItems(source), LifetimeSeconds = lifetimeSeconds,
                RemoveContainerWhenEmpty = removeWhenEmpty, Motion = motion };
            Submit(new ModWorldRequest { Operation = "create", Key = key, Record = record }, source.gameObject, false, complete);
        }
        public void SpawnEnemy(string key, string kind, string data, ModWorldPose pose, ModEnemyOptions options,
            Action<bool, ModWorldRecord> complete)
        {
            var record = new ModWorldRecord { Key = key, Kind = kind, Data = data, Type = ModWorldObjectType.NativeEnemy,
                Pose = pose, LifetimeSeconds = 0, ScriptedEnemy = options.ScriptedOwner, TransientEnemy = options.Transient };
            Submit(new ModWorldRequest { Operation = "enemy", Key = key, Record = record, Enemy = options }, null, false, complete);
        }
        // Explicitly creates an unnetworked DFU enemy, even while connected. Customization
        // can happen before ConvertEnemy; no CreateFoe client command is fired here.
        public static GameObject CreateLocalEnemy(Vector3 position, ModEnemyOptions options)
        {
            var go = GameObjectHelper.InstantiatePrefab(DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject,
                "Mod local enemy staging", null, position);
            try
            {
                go.GetComponent<SetupDemoEnemy>().ApplyEnemySettings(options.Mobile, options.Reaction, options.Gender, 0, options.AlliedToPlayer);
                var enemy = go.GetComponent<DaggerfallEnemy>();
                if (enemy != null) enemy.LoadID = DaggerfallUnity.NextUID;
                return go;
            }
            catch { UnityEngine.Object.Destroy(go); throw; }
        }
        public void ConvertEnemy(GameObject localEnemy, string key, string kind, string data,
            ModEnemyOptions options, Action<bool, ModWorldRecord> complete)
        {
            uint ignored;
            if (localEnemy == null || MultiplayerAPI.TryGetNetworkId(localEnemy, out ignored))
            { if (complete != null) complete(false, null); return; }
            var entity = localEnemy.GetComponent<DaggerfallEntityBehaviour>();
            var enemy = entity != null ? entity.Entity as EnemyEntity : null;
            var setup = localEnemy.GetComponent<SetupDemoEnemy>();
            if (enemy == null || setup == null || enemy.CurrentHealth <= 0) { if (complete != null) complete(false, null); return; }
            options.Mobile = setup.EnemyType; options.Reaction = setup.EnemyReaction; options.Gender = setup.EnemyGender;
            options.AlliedToPlayer = setup.AlliedToPlayer; options.Team = (int)enemy.Team; options.CurrentHealth = enemy.CurrentHealth;
            var record = new ModWorldRecord { Key = key, Kind = kind, Data = data, Type = ModWorldObjectType.NativeEnemy,
                Pose = ModWorldPose.Capture(localEnemy.transform), LifetimeSeconds = 0, ScriptedEnemy = options.ScriptedOwner, TransientEnemy = options.Transient };
            staged[key] = localEnemy;
            localEnemy.SetActive(false); // No invisible second simulator while the server responds.
            Submit(new ModWorldRequest { Operation = "enemy", Key = key, Record = record, Enemy = options, Converted = true },
                null, false, delegate(bool ok, ModWorldRecord result)
                {
                    GameObject original;
                    if (staged.TryGetValue(key, out original))
                    {
                        staged.Remove(key);
                        if (original != null)
                        {
                            // A server promotes the same original; a pure client replaces its staging
                            // object with the authoritative network prefab returned by the server.
                            if (ok) { uint id; if (!MultiplayerAPI.TryGetNetworkId(original, out id)) UnityEngine.Object.Destroy(original); }
                            else original.SetActive(true);
                        }
                    }
                    if (complete != null) complete(ok, result);
                });
        }
        public void UpdateData(string key, string data)
        {
            ModWorldRecord record;
            if (!records.TryGetValue(key, out record) || record.OwnerPlayerId != MultiplayerAPI.LocalPlayerId) return;
            var updated = Clone(record); updated.Data = data;
            Submit(new ModWorldRequest { Operation = "data", Key = key, Record = updated }, null, false, null);
        }
        public void Remove(string key) { Submit(new ModWorldRequest { Operation = "remove", Key = key }, null, false, null); }
        public void ReleaseEnemy(string key) { Submit(new ModWorldRequest { Operation = "release", Key = key }, null, false, null); }
        // Server atomically chooses one winner. Only that caller's completion receives the item data.
        public void Take(string key, Action<bool, ModWorldRecord> complete)
        { Submit(new ModWorldRequest { Operation = "take", Key = key }, null, false, complete); }
        public void ResetOwnedObjects()
        {
            foreach (var binding in bindings.Values.ToArray())
                if (binding != null && binding.Source) DestroyBinding(binding, true);
            resetNeeded = true;
            SendResetWhenReady();
        }
        void SendResetWhenReady()
        {
            if (!resetNeeded || resetRequestPending || !TransportReady) return;
            resetRequestPending = true;
            Submit(new ModWorldRequest { Operation = "reset", Key = "reset" }, null, false,
                delegate(bool ok, ModWorldRecord ignored)
                {
                    resetRequestPending = false;
                    if (ok) { resetNeeded = false; MultiplayerAPI.RequestStateSnapshot(mod); }
                });
        }
        void Submit(ModWorldRequest message, GameObject source, bool keepParent, Action<bool, ModWorldRecord> complete)
        {
            if ((!Ready && !(message.Operation == "reset" && TransportReady)) || pending.Count >= 128 || !ValidKey(message.Key))
            { if (complete != null) complete(false, null); return; }
            message.Request = Guid.NewGuid().ToString("N"); message.Epoch = localEpoch;
            var item = new Pending { Message = message, Source = source, KeepParent = keepParent, Callback = complete, Retry = Time.unscaledTime + 3 };
            pending.Add(message.Request, item);
            // Also remember the source before the retained create message arrives.
            MultiplayerAPI.SendJsonToServer(mod, RequestChannel, message);
        }
        static bool ValidKey(string key)
        { return !string.IsNullOrEmpty(key) && key.Length <= 64 && key.IndexOfAny(new[]{'\r','\n'}) < 0; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool ValidPose(ModWorldPose pose)
        {
            if (pose == null || string.IsNullOrEmpty(pose.Area) || pose.Area.Length > 512) return false;
            var p = pose.Interior; var q = pose.Rotation; var e = pose.Exterior;
            var f = pose.FrameRotation; var reference = pose.ReferenceInterior;
            float frameNorm = f.x*f.x+f.y*f.y+f.z*f.z+f.w*f.w;
            float norm = q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
            return Finite(pose.ReferenceHeight) && Finite(reference.x) && Finite(reference.y) && Finite(reference.z) &&
                Finite(frameNorm) && frameNorm > .5f && frameNorm < 1.5f && Finite(p.x) && Finite(p.y) && Finite(p.z) && Finite(norm) && norm > .5f && norm < 1.5f &&
                Finite(e.WorldX) && Finite(e.WorldZ) && Finite(e.Height) && Math.Abs(e.WorldX) <= int.MaxValue && Math.Abs(e.WorldZ) <= int.MaxValue;
        }
        static string ServerArea(PositionMultiplayer p)
        {
            if (p == null || !p.EnemyInterestLocationKnown) return null;
            switch (p.EnemyInterestState)
            {
                case PositionMultiplayer.PartyLocationState.Wilderness:
                case PositionMultiplayer.PartyLocationState.ExteriorLocation: return "E";
                case PositionMultiplayer.PartyLocationState.BuildingInterior:
                    return LootCatcher.CorpseBuildingArea(p.EnemyInterestRegion, p.EnemyInterestLocation, p.EnemyInterestBuildingKey);
                case PositionMultiplayer.PartyLocationState.DungeonInterior:
                    return string.IsNullOrEmpty(p.EnemyInterestDungeonId) ? null : "D:" + p.EnemyInterestDungeonId;
                default: return null;
            }
        }
        static bool Near(NetworkIdentity player, ModWorldPose pose, float distance, bool reported = false)
        {
            if (!ValidPose(pose) || player == null) return false;
            var p = player.GetComponent<PositionMultiplayer>();
            if (ServerArea(p) != pose.Area) return false;
            if (pose.Area == "E")
            {
                double dx = (pose.Exterior.WorldX-p.x)/40.0, dz = (pose.Exterior.WorldZ-p.z)/40.0;
                return dx*dx+dz*dz <= distance*distance && (!reported || Math.Abs(pose.Exterior.Height-pose.ReferenceHeight) <= distance);
            }
            if (reported)
            {
                Vector3 offset = pose.FrameRotation*(pose.Interior-pose.ReferenceInterior);
                if (!Finite(offset.x) || !Finite(offset.y) || !Finite(offset.z) || offset.sqrMagnitude > distance*distance) return false;
                pose.ServerInteriorPoint = player.transform.position + offset;
                return true;
            }
            return Vector3.Distance(pose.ServerInteriorPoint, player.transform.position) <= distance;
        }

        void HandleRequest(ModMessageContext context, ModWorldRequest request)
        {
            if (!MultiplayerAPI.IsServer || request == null || request.Request == null || request.Request.Length != 32 ||
                !ValidKey(request.Key)) return;
            NetworkIdentity player;
            if (!NetworkServer.spawned.TryGetValue(context.SenderPlayerId, out player) || player == null || player.connectionToClient == null) return;
            string replyKey = context.SenderPlayerId + ":" + request.Request;
            ModWorldReply reply;
            if (replies.TryGetValue(replyKey, out reply)) { MultiplayerAPI.SendJsonToPlayer(context.SenderPlayerId, mod, ReplyChannel, reply); return; }
            uint epoch; serverEpochs.TryGetValue(context.SenderPlayerId, out epoch);
            reply = new ModWorldReply { Request = request.Request, Error = "Request rejected", Epoch = epoch };
            ModWorldRecord old;
            server.TryGetValue(request.Key, out old);
            try
            {
                if (request.Operation != "reset" && request.Epoch != epoch)
                    throw new InvalidOperationException("Stale owner generation; await world reset acknowledgement.");
                if (request.Operation == "reset")
                {
                    if (epoch == uint.MaxValue) throw new InvalidOperationException("Owner generation exhausted.");
                    serverEpochs[context.SenderPlayerId] = reply.Epoch = epoch + 1;
                    foreach (var record in server.Values.Where(r => r.OwnerPlayerId == context.SenderPlayerId && !r.Deleted && r.Type != ModWorldObjectType.Claim).ToArray())
                        Delete(record, true);
                    reply.Success = true;
                }
                else if (request.Operation == "claim")
                {
                    if (old == null && server.Count < MaxObjects)
                    {
                        var claim = new ModWorldRecord { Key = request.Key, Kind = "claim", OwnerPlayerId = context.SenderPlayerId,
                            Type = ModWorldObjectType.Claim, LifetimeSeconds = 0 };
                        reply.Success = Commit(claim); reply.Record = claim;
                    }
                }
                else if (request.Operation == "create" || request.Operation == "enemy")
                {
                    var record = request.Record;
                    bool enemyRequest = request.Operation == "enemy";
                    if (old == null && server.Count < MaxObjects && record != null && record.Key == request.Key &&
                        !string.IsNullOrEmpty(record.Kind) && record.Kind.Length <= 48 && (record.Data == null || record.Data.Length <= 8192) &&
                        Finite(record.LifetimeSeconds) && record.LifetimeSeconds >= 0 && record.LifetimeSeconds <= 86400 &&
                        Finite(record.VisibilityMetres) && record.VisibilityMetres >= 1 && record.VisibilityMetres <= 3000 &&
                        (enemyRequest || Near(player, record.Pose, ObjectCreateRequestMetres, true)))
                    {
                        if (enemyRequest)
                        {
                            if (record.Type != ModWorldObjectType.NativeEnemy || record.Pose == null ||
                                record.Pose.Area != "E" || !ValidEnemy(request.Enemy))
                                throw new InvalidOperationException("Invalid native exterior enemy options.");
                            float range = request.Enemy.ExteriorRequestMetres == 0
                                ? DefaultEnemyRequestMetres : request.Enemy.ExteriorRequestMetres;
                            if (!Near(player, record.Pose, range, true))
                                throw new InvalidOperationException("Enemy spawn is outside the requester's current area or configured " + range + " m spawn range.");
                        }
                        if (!Enum.IsDefined(typeof(ModWorldMotion), record.Motion))
                            throw new InvalidOperationException("Invalid world motion mode.");
                        record.OwnerPlayerId = context.SenderPlayerId; record.OwnerEpoch = epoch; record.Deleted = false;
                        record.NetworkId = record.ContainerId = 0; record.ContainerVersion = 0;
                        record.Parenting = ModEnemyParenting.Default;
                        record.BehaviorId = record.BehaviorData = null;
                        if (enemyRequest)
                        {
                            if (!ConfigureEnemyBehavior(record, request.Enemy))
                                throw new InvalidOperationException("Enemy behavior or its parameters failed server validation.");
                            GameObject enemy = SpawnNative(record, request.Enemy, request.Converted, player.connectionToClient);
                            record.NetworkId = enemy.GetComponent<NetworkIdentity>().netId; record.TransientEnemy = request.Enemy.Transient;
                        }
                        else if (record.Type == ModWorldObjectType.Container)
                        {
                            if (record.ContainerItems == null || record.ContainerItems.Length > 8192) throw new InvalidOperationException("Container payload too large.");
                            record.ContainerId = LootCatcher.ServerRegisterWorldContainer(record.ContainerItems);
                            if (record.ContainerId == 0) throw new InvalidOperationException("Container registration failed.");
                        }
                        else if (record.Type != ModWorldObjectType.Visual) throw new InvalidOperationException("Invalid object type.");
                        reply.Success = Commit(record); reply.Record = record;
                        if (reply.Success && record.LifetimeSeconds > 0) expires[record.Key] = Time.unscaledTime + record.LifetimeSeconds;
                        if (!reply.Success) CleanupNative(record, true);
                    }
                }
                else if (old != null && !old.Deleted)
                {
                    bool owner = old.OwnerPlayerId == context.SenderPlayerId;
                    if (request.Operation == "pose" && owner && request.Record != null && Near(player, request.Record.Pose, 3000, true) &&
                        old.Type != ModWorldObjectType.NativeEnemy && old.Motion != ModWorldMotion.Fixed && old.Pose.Area == request.Record.Pose.Area)
                    { old.Pose = request.Record.Pose; reply.Success = Commit(old); }
                    else if (request.Operation == "data" && owner && request.Record != null &&
                        request.Record.Data != null && request.Record.Data.Length <= 8192)
                    { old.Data = request.Record.Data; reply.Success = Commit(old); }
                    else if (request.Operation == "remove" && owner) reply.Success = Delete(old, true);
                    else if (request.Operation == "release" && owner && old.Type == ModWorldObjectType.NativeEnemy)
                    {
                        GameObject enemy;
                        if (MultiplayerAPI.TryGetNetworkObject(old.NetworkId, out enemy))
                            enemy.GetComponent<DynamicEnemyAuthority>().SetPersistentOwner(false, player.connectionToClient as NetworkConnectionToClient);
                        old.ScriptedEnemy = false; reply.Success = Commit(old);
                    }
                    else if (request.Operation == "take" && old.Pickable && old.Type == ModWorldObjectType.Visual && Near(player, old.Pose, 4))
                    { reply.Record = Clone(old); reply.Success = Delete(old, true); }
                }
            }
            catch (Exception error) { reply.Error = error.Message; Debug.LogWarning("[MPAPI world] " + error.Message); }
            if (reply.Success) reply.Error = null;
            replies[replyKey] = reply; replyOrder.Enqueue(replyKey);
            while (replyOrder.Count > MaxReplies) replies.Remove(replyOrder.Dequeue());
            MultiplayerAPI.SendJsonToPlayer(context.SenderPlayerId, mod, ReplyChannel, reply);
        }
        static ModWorldRecord Clone(ModWorldRecord record)
        { return JsonUtility.FromJson<ModWorldRecord>(JsonUtility.ToJson(record)); }
        bool Commit(ModWorldRecord record)
        {
            ulong revision;
            if (!MultiplayerAPI.SetState(mod, record.Key, MultiplayerAPI.ToJsonPayload(record), record.OwnerPlayerId, ulong.MaxValue, out revision)) return false;
            server[record.Key] = record; return true;
        }
        bool Delete(ModWorldRecord record, bool destroyEnemy)
        {
            var deleted = Clone(record); deleted.Deleted = true;
            // Tombstones retain consumed keys for this session: delayed create/pose retries cannot resurrect them.
            if (!Commit(deleted)) return false;
            CleanupNative(record, destroyEnemy); expires.Remove(record.Key); return true;
        }
        static void CleanupNative(ModWorldRecord record, bool destroyEnemy)
        {
            if (record.ContainerId != 0) LootCatcher.ServerForgetWorldContainer(record.ContainerId);
            GameObject enemy;
            if (destroyEnemy && record.Type == ModWorldObjectType.NativeEnemy && MultiplayerAPI.TryGetNetworkObject(record.NetworkId, out enemy))
                NetworkServer.Destroy(enemy);
        }
        const float ObjectCreateRequestMetres = 80f;
        const float DefaultEnemyRequestMetres = 100f;
        // Keep the request within half the 819.2 m exterior terrain frame.
        const float MaxEnemyRequestMetres = 400f;

        static bool ValidEnemy(ModEnemyOptions option)
        {
            return option != null && Enum.IsDefined(typeof(MobileTypes), option.Mobile) && Enum.IsDefined(typeof(MobileGender), option.Gender) &&
                Enum.IsDefined(typeof(MobileReactions), option.Reaction) &&
                Finite(option.ExteriorRequestMetres) && (option.ExteriorRequestMetres == 0 ||
                    (option.ExteriorRequestMetres >= 1 && option.ExteriorRequestMetres <= MaxEnemyRequestMetres)) &&
                Finite(option.ExteriorRetentionMetres) &&
                option.ExteriorRetentionMetres >= 0 && option.ExteriorRetentionMetres <= 3000 && option.CurrentHealth >= 0 && option.CurrentHealth <= 1000000 &&
                (option.Team == -1 || Enum.IsDefined(typeof(MobileTeams), option.Team));
        }
        GameObject SpawnNative(ModWorldRecord record, ModEnemyOptions option, bool converted, NetworkConnection owner)
        {
            var p = owner.identity.GetComponent<PositionMultiplayer>();
            // Server Unity coordinates use the requester's frame, never the host player's GPS.
            Vector3 point = owner.identity.transform.position + new Vector3((float)((record.Pose.Exterior.WorldX-p.x)/40.0), 0,
                (float)((record.Pose.Exterior.WorldZ-p.z)/40.0));
            point.y = owner.identity.transform.position.y + record.Pose.Exterior.Height-record.Pose.ReferenceHeight;
            GameObject go = null;
            Transform originalParent = null;
            try
            {
                if (converted && record.OwnerPlayerId == MultiplayerAPI.LocalPlayerId && staged.TryGetValue(record.Key, out go))
                {
                    originalParent = go.transform.parent;
                    go.transform.SetParent(null, true);
                    go.SetActive(true);
                }
                else if (converted || record.ScriptedEnemy) go = CreateLocalEnemy(point, option);
                else
                {
                    var enemies = GameObjectHelper.CreateFoeGameObjectsInternal(point, option.Mobile, 1, option.Reaction, null, option.AlliedToPlayer);
                    if (enemies == null || enemies.Length == 0) throw new InvalidOperationException("Native spawn failed.");
                    go = enemies[0];
                }
                go.transform.SetParent(null, true); go.transform.SetPositionAndRotation(point, record.Pose.Rotation);
                var identity = go.GetComponent<NetworkIdentity>(); var setup = go.GetComponent<SetupDemoEnemy>();
                var authority = go.GetComponent<DynamicEnemyAuthority>(); var world = go.GetComponent<EnemyWorldPosition>();
                if (identity == null || setup == null || authority == null || world == null) throw new InvalidOperationException("Enemy prefab lacks co-op components.");
                if (option.Transient) MakeEnemyTransient(go);
                setup.isDungeonEnemy = false; setup.syncedInitialY = point.y;
                setup.SpawnTeamOverride = option.Team; setup.ApplySpawnTeamOverride();
                var entity = go.GetComponent<DaggerfallEntityBehaviour>().Entity as EnemyEntity;
                if (entity != null && option.CurrentHealth > 0)
                {
                    go.GetComponent<DaggerfallEntityBehaviour>().ApplyAuthoritativeSpawnHealthAndMax(option.CurrentHealth);
                    setup.SyncedSpawnHealth = option.CurrentHealth;
                }
                authority.HorizontalRetentionDistanceDF = option.ExteriorRetentionMetres*40f;
                world.SetSpawnContext(false, record.OwnerPlayerId);
                if (entity == null) throw new InvalidOperationException("Native enemy entity did not initialize.");
                entity.WorldContext = WorldContext.Exterior;
                if (identity.netId == 0)
                {
                    if (option.ExteriorRetentionMetres > 0) authority.PreserveInitialExteriorOwner(owner);
                    if (option.ExteriorRetentionMetres > 0) world.SetDistantExteriorCoordinates((int)Math.Round(record.Pose.Exterior.WorldX), (int)Math.Round(record.Pose.Exterior.WorldZ));
                    else world.InitializeFromPlayer(p);
                    GameManager.Instance.RaiseOnEnemySpawnEvent(go);
                    NetworkServer.Spawn(go, owner);
                }
                if (identity.netId == 0)
                    throw new InvalidOperationException("Mirror did not assign a network ID to the enemy.");
                ApplyEnemyParenting(go, record.Parenting);
                if (record.Parenting == ModEnemyParenting.Preserve && originalParent != null)
                    go.transform.SetParent(originalParent, true);
                if (record.ScriptedEnemy) authority.SetPersistentOwner(true, owner as NetworkConnectionToClient);
                world.ResumeRetainedActorNetworking();
                if (option.ExteriorRetentionMetres > 0) world.SetDistantExteriorCoordinates((int)Math.Round(record.Pose.Exterior.WorldX), (int)Math.Round(record.Pose.Exterior.WorldZ));
                return go;
            }
            catch { if (go != null) { var ni = go.GetComponent<NetworkIdentity>(); if (ni != null && ni.netId != 0) NetworkServer.Destroy(go); else UnityEngine.Object.Destroy(go); } throw; }
        }
        void HandleReply(ModMessageContext context, ModWorldReply reply)
        {
            Pending item;
            if (reply == null || reply.Request == null || !pending.TryGetValue(reply.Request, out item)) return;
            pending.Remove(reply.Request);
            if (!reply.Success && item.Message.Operation == "enemy")
                Debug.LogWarning("[MPAPI world] Enemy request " + item.Message.Key + ": " + reply.Error);
            if (reply.Success && item.Message.Operation == "reset")
            {
                localEpoch = reply.Epoch;
                foreach (var record in records.Values.Where(r => r.Type != ModWorldObjectType.Claim && r.OwnerPlayerId == MultiplayerAPI.LocalPlayerId && r.OwnerEpoch < localEpoch).ToArray())
                    Forget(record.Key);
            }
            if (reply.Success && reply.Record != null && item.Source != null)
            {
                // Retained delivery can precede the acknowledgement: replace a speculative
                // local visual replica with its original source, without issuing a removal.
                ModWorldRecord latest;
                if (reply.Record.Type == ModWorldObjectType.Container &&
                    records.TryGetValue(reply.Record.Key, out latest) && latest.Deleted)
                {
                    UnityEngine.Object.Destroy(item.Source);
                    if (item.Callback != null) item.Callback(true, latest);
                    return;
                }
                ModWorldBinding previous;
                if (bindings.TryGetValue(reply.Record.Key, out previous) && previous != null && previous.gameObject != item.Source)
                    DestroyBinding(previous, true);
                Bind(reply.Record, item.Source, true, item.KeepParent);
            }
            if (item.Callback != null) { try { item.Callback(reply.Success, reply.Record); } catch (Exception error) { Debug.LogException(error); } }
        }
        void StateChanged(ModStateChange change)
        {
            ulong known;
            if (revisions.TryGetValue(change.Record.Key, out known) && known >= change.Record.Revision) return;
            revisions[change.Record.Key] = change.Record.Revision;
            if (change.Removed) { Forget(change.Record.Key); return; }
            var record = MultiplayerAPI.FromJsonPayload<ModWorldRecord>(change.Record.Payload);
            if (record == null || record.Key != change.Record.Key) return;
            if (record.Type != ModWorldObjectType.Claim && record.OwnerPlayerId == MultiplayerAPI.LocalPlayerId && record.OwnerEpoch < localEpoch)
            { Forget(record.Key); return; }
            if (record.Type != ModWorldObjectType.Claim && !ValidPose(record.Pose)) return;
            records[record.Key] = record;
            ModWorldBinding binding;
            if (record.Deleted)
            {
                if (bindings.TryGetValue(record.Key, out binding) && binding != null) DestroyBinding(binding, true);
                return;
            }
            if (bindings.TryGetValue(record.Key, out binding) && binding != null)
            {
                bool dataChanged = binding.State.Data != record.Data;
                binding.State = record;
                if (record.Type == ModWorldObjectType.NativeEnemy || (dataChanged && !binding.Source)) factory(record, binding.gameObject);
            }
        }
        void SnapshotComplete(ModStateSnapshot snapshot)
        {
            var keys = new HashSet<string>(snapshot.Keys);
            foreach (string key in records.Keys.Where(k => !keys.Contains(k)).ToArray()) Forget(key);
            Debug.Log("[MPAPI world] Snapshot " + mod + " peer=" + MultiplayerAPI.LocalPlayerId +
                " keys=" + keys.Count + " accepted=" + records.Count);
        }
        void Forget(string key)
        {
            records.Remove(key); revisions.Remove(key);
            ModWorldBinding binding;
            if (bindings.TryGetValue(key, out binding) && binding != null) DestroyBinding(binding, true);
        }
        static void MakeEnemyTransient(GameObject enemy)
        {
            var model = enemy.GetComponent<DaggerfallEnemy>();
            if (model == null || model.LoadID == 0) return;
            var serializer = enemy.GetComponent<SerializableEnemy>();
            if (serializer != null) SaveLoadManager.DeregisterSerializableGameObject(serializer);
            model.LoadID = 0;
        }
        void Bind(ModWorldRecord record, GameObject obj, bool source, bool keepParent)
        {
            if (obj == null) return;
            if (!keepParent && record.Parenting != ModEnemyParenting.Preserve)
                obj.transform.SetParent(null, true);
            if (record.Type == ModWorldObjectType.NativeEnemy && record.TransientEnemy) MakeEnemyTransient(obj);
            if (record.Type == ModWorldObjectType.Container) PrepareContainer(obj.GetComponent<DaggerfallLoot>());
            if (record.Type == ModWorldObjectType.Container)
            {
                var gate = obj.GetComponent<ModWorldContainerGate>();
                if (gate == null) gate = obj.AddComponent<ModWorldContainerGate>();
                gate.Hold();
            }
            var binding = obj.GetComponent<ModWorldBinding>();
            if (binding == null) binding = obj.AddComponent<ModWorldBinding>();
            binding.World = this; binding.State = record; binding.Source = source;
            binding.LastPose = record.Pose;
            Transform root; LootCatcher.CurrentCorpseArea(out root);
            if (root != null) { binding.AreaOrigin = root.position; binding.AreaRotation = root.rotation; }
            RememberFrame(binding);
            if (source && record.Motion == ModWorldMotion.Fixed)
            {
                binding.ReplicaPose = record.Pose;
                binding.ReplicaFrom = binding.ReplicaTo = obj.transform.position;
                binding.ReplicaFromRotation = binding.ReplicaToRotation = obj.transform.rotation;
            }
            bindings[record.Key] = binding;
        }
        static void RememberFrame(ModWorldBinding binding)
        {
            binding.LastCompensation = GameManager.Instance.StreamingWorld.WorldCompensation;
        }

        const float SmoothPoseInterval = .125f;

        static void PlaceMotionReplica(ModWorldBinding binding, ModWorldPose pose, Vector3 target, Quaternion rotation)
        {
            if (!binding.AreaAvailable || binding.ReplicaPose == null)
            {
                binding.ReplicaPose = pose;
                binding.ReplicaFrom = binding.ReplicaTo = target;
                binding.ReplicaFromRotation = binding.ReplicaToRotation = rotation;
            }
            else
            {
                var shift = GameManager.Instance.StreamingWorld.WorldCompensation - binding.LastCompensation;
                if (pose.Area == "E")
                {
                    binding.ReplicaFrom += shift;
                    binding.ReplicaTo += shift;
                }
                if (pose.Area != "E")
                {
                    Transform root;
                    LootCatcher.CurrentCorpseArea(out root);
                    var turn = root.rotation * Quaternion.Inverse(binding.AreaRotation);
                    binding.ReplicaFrom = root.position + turn * (binding.ReplicaFrom - binding.AreaOrigin);
                    binding.ReplicaTo = root.position + turn * (binding.ReplicaTo - binding.AreaOrigin);
                    binding.ReplicaFromRotation = turn * binding.ReplicaFromRotation;
                    binding.ReplicaToRotation = turn * binding.ReplicaToRotation;
                }
                if (binding.State.Motion == ModWorldMotion.Smooth && pose != binding.ReplicaPose)
                {
                    var previous = binding.ReplicaPose;
                    float previousBlend = MotionBlend(binding);
                    binding.ReplicaFrom = Vector3.Lerp(binding.ReplicaFrom, binding.ReplicaTo, previousBlend);
                    binding.ReplicaFromRotation = Quaternion.Slerp(binding.ReplicaFromRotation, binding.ReplicaToRotation, previousBlend);
                    binding.ReplicaTo = pose.Area == "E" ? pose.Exterior.ToLocal(binding.ReplicaTo,
                        previous.Exterior.WorldX, previous.Exterior.WorldZ, GameManager.Instance.StreamingWorld.WorldCompensation.y) : target;
                    binding.ReplicaToRotation = rotation;
                    binding.ReplicaStarted = Time.unscaledTime;
                    binding.ReplicaPose = pose;
                }
            }
            RememberFrame(binding);
            if (pose.Area != "E")
            {
                Transform root;
                LootCatcher.CurrentCorpseArea(out root);
                binding.AreaOrigin = root.position;
                binding.AreaRotation = root.rotation;
            }
            float blend = MotionBlend(binding);
            binding.transform.SetPositionAndRotation(
                Vector3.Lerp(binding.ReplicaFrom, binding.ReplicaTo, blend),
                Quaternion.Slerp(binding.ReplicaFromRotation, binding.ReplicaToRotation, blend));
        }
        static float MotionBlend(ModWorldBinding binding)
        {
            return Mathf.Clamp01((Time.unscaledTime-binding.ReplicaStarted)/SmoothPoseInterval);
        }

        internal void BindingDestroyed(ModWorldBinding binding)
        {
            ModWorldBinding current;
            if (binding.State == null) return;
            if (bindings.TryGetValue(binding.Key, out current) && current == binding) bindings.Remove(binding.Key);
            if (!disposed && !binding.Removing && binding.Source && binding.State.Type != ModWorldObjectType.NativeEnemy && Ready) Remove(binding.Key);
            if (binding.State.ContainerId != 0) LootCatcher.UnbindWorldContainer(binding.State.ContainerId, binding.GetComponent<DaggerfallLoot>());
        }
        void DestroyBinding(ModWorldBinding binding, bool silent)
        {
            if (binding == null) return;
            binding.Removing = silent; bindings.Remove(binding.Key);
            if (binding.State.Type == ModWorldObjectType.NativeEnemy)
            {
                binding.StopEnemyBehavior();
                binding.World = null; UnityEngine.Object.Destroy(binding); // Mirror owns this GameObject.
            }
            else UnityEngine.Object.Destroy(binding.gameObject);
        }
        static void Show(ModWorldBinding binding, bool visible)
        {
            if (binding.Hidden == !visible) return;
            if (!visible)
            {
                binding.Renderers = binding.GetComponentsInChildren<Renderer>(true); binding.RendererStates = new bool[binding.Renderers.Length];
                binding.Colliders = binding.GetComponentsInChildren<Collider>(true); binding.ColliderStates = new bool[binding.Colliders.Length];
                for (int i=0;i<binding.Renderers.Length;i++) { binding.RendererStates[i] = binding.Renderers[i].enabled; binding.Renderers[i].enabled = false; }
                for (int i=0;i<binding.Colliders.Length;i++) { binding.ColliderStates[i] = binding.Colliders[i].enabled; binding.Colliders[i].enabled = false; }
            }
            else
            {
                for (int i=0;i<binding.Renderers.Length;i++) if(binding.Renderers[i] != null) binding.Renderers[i].enabled = binding.RendererStates[i];
                for (int i=0;i<binding.Colliders.Length;i++) if(binding.Colliders[i] != null) binding.Colliders[i].enabled = binding.ColliderStates[i];
            }
            binding.Hidden = !visible;
        }
        static bool OwnerConnected(uint owner)
        {
            NetworkIdentity identity;
            return NetworkServer.spawned.TryGetValue(owner, out identity) && identity != null && identity.connectionToClient != null &&
                NetworkServer.connections.Values.Contains(identity.connectionToClient);
        }
        internal void Tick()
        {
            if (disposed) return;
            bool load = SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
            if (load && !loading)
            {
                // Cancel stale callbacks before inventory from another save can be touched.
                foreach (var item in pending.Values) if (item.Source != null) UnityEngine.Object.Destroy(item.Source);
                foreach (var original in staged.Values) if (original != null) UnityEngine.Object.Destroy(original);
                staged.Clear(); pending.Clear(); resetRequestPending = false; ResetOwnedObjects();
            }
            if (!load && loading) MultiplayerAPI.RequestStateSnapshot(mod);
            loading = load;
            if (MultiplayerAPI.IsServer && Time.unscaledTime >= nextServerPoll)
            {
                nextServerPoll = Time.unscaledTime + .25f;
                foreach (var record in server.Values.Where(r => !r.Deleted && r.Type != ModWorldObjectType.Claim).ToArray())
                {
                    float until;
                    if (!OwnerConnected(record.OwnerPlayerId) || (expires.TryGetValue(record.Key, out until) && Time.unscaledTime >= until))
                    { Delete(record, true); continue; }
                    if (record.Type == ModWorldObjectType.NativeEnemy)
                    {
                        GameObject enemy;
                        if (!MultiplayerAPI.TryGetNetworkObject(record.NetworkId, out enemy)) Delete(record, false);
                        // Native replicas use Mirror interest, not the retained spawn pose for visibility.
                    }
                    if (record.ContainerId != 0)
                    {
                        int version; string items;
                        bool exists = LootCatcher.ServerReadWorldContainer(record.ContainerId, out version, out items);
                        if (record.RemoveContainerWhenEmpty && (!exists || string.IsNullOrEmpty(items)))
                        {
                            Delete(record, true);
                            continue;
                        }
                        if (exists && version != record.ContainerVersion)
                        {
                            record.ContainerVersion = version;
                            record.ContainerItems = items;
                            Commit(record);
                        }
                    }
                }
            }
            SendResetWhenReady();
            if (!TransportReady || load) return;
            foreach (var item in pending.Values.ToArray())
                if (Time.unscaledTime >= item.Retry) { item.Retry = Time.unscaledTime + 3; MultiplayerAPI.SendJsonToServer(mod, RequestChannel, item.Message); }
            foreach (var record in records.Values.Where(r => !r.Deleted && r.Type != ModWorldObjectType.Claim).ToArray())
            {
                if (record.OwnerPlayerId == MultiplayerAPI.LocalPlayerId && (resetNeeded || record.OwnerEpoch < localEpoch)) continue;
                Vector3 target; Quaternion rotation;
                bool sameArea = record.Pose.TryLocal(out target, out rotation);
                bool visible = sameArea && IsWithinVisibility(record, target);
                ModWorldBinding binding;
                if (!bindings.TryGetValue(record.Key, out binding) || binding == null)
                {
                    // Wait for the original source acknowledgement rather than creating a duplicate.
                    if (pending.Values.Any(p => p.Source != null && p.Message.Key == record.Key)) continue;
                    GameObject obj = null;
                    if (record.Type == ModWorldObjectType.NativeEnemy)
                    {
                        if (!MultiplayerAPI.TryGetNetworkObject(record.NetworkId, out obj)) continue;
                    }
                    else if (!visible) continue;
                    try
                    {
                        GameObject existing = obj;
                        if (record.Type == ModWorldObjectType.NativeEnemy)
                            ApplyEnemyParenting(existing, record.Parenting);
                        obj = factory(record, existing);
                        if (obj == null) continue;
                        if (record.Type == ModWorldObjectType.NativeEnemy && obj != existing)
                            throw new InvalidOperationException("A native enemy factory must return the supplied network GameObject.");
                        if (record.Type != ModWorldObjectType.NativeEnemy) obj.transform.SetPositionAndRotation(target, rotation);
                        Bind(record, obj, record.OwnerPlayerId == MultiplayerAPI.LocalPlayerId, record.KeepParent);
                        binding = bindings[record.Key];
                        if (!binding.Source && record.Type != ModWorldObjectType.NativeEnemy)
                            Debug.Log("[MPAPI world] Replica " + mod + "/" + record.Kind + " owner=" +
                                record.OwnerPlayerId + " peer=" + MultiplayerAPI.LocalPlayerId);
                    }
                    catch (Exception error) { Debug.LogException(error); continue; }
                }
                if (record.Type == ModWorldObjectType.NativeEnemy)
                {
                    TickEnemyBehavior(binding);
                    if (record.Parenting != ModEnemyParenting.Default)
                        ApplyEnemyParenting(binding.gameObject, record.Parenting);
                    continue;
                }
                if (binding.Source)
                {
                    if (sameArea)
                    {
                        // Apply only floating-origin delta; preserve the owner's scripted motion.
                        if (record.Motion == ModWorldMotion.Fixed)
                            PlaceMotionReplica(binding, record.Pose, target, rotation);
                        else if (record.Pose.Area == "E") binding.transform.position += GameManager.Instance.StreamingWorld.WorldCompensation - binding.LastCompensation;
                        else
                        {
                            Transform root; LootCatcher.CurrentCorpseArea(out root);
                            if (!binding.AreaAvailable) binding.transform.SetPositionAndRotation(target, rotation);
                            else
                            {
                                binding.transform.position = root.position + root.rotation*(Quaternion.Inverse(binding.AreaRotation)*(binding.transform.position-binding.AreaOrigin));
                                binding.transform.rotation = root.rotation*Quaternion.Inverse(binding.AreaRotation)*binding.transform.rotation;
                            }
                            binding.AreaOrigin = root.position; binding.AreaRotation = root.rotation;
                        }
                        RememberFrame(binding);
                        target = binding.transform.position;
                        visible = IsWithinVisibility(record, target);
                    }
                    binding.AreaAvailable = sameArea;
                    Show(binding, visible);
                }
                else
                {
                    Show(binding, visible);
                    if (record.Motion != ModWorldMotion.Default)
                    {
                        if (sameArea) PlaceMotionReplica(binding, record.Pose, target, rotation);
                    }
                    else if (sameArea)
                    {
                        if (record.Pose.Area == "E") binding.transform.position += GameManager.Instance.StreamingWorld.WorldCompensation - binding.LastCompensation;
                        else if (!binding.AreaAvailable) binding.transform.SetPositionAndRotation(target, rotation);
                        else
                        {
                            Transform root; LootCatcher.CurrentCorpseArea(out root);
                            binding.transform.position = root.position + root.rotation*(Quaternion.Inverse(binding.AreaRotation)*(binding.transform.position-binding.AreaOrigin));
                            binding.transform.rotation = root.rotation*Quaternion.Inverse(binding.AreaRotation)*binding.transform.rotation;
                            binding.AreaOrigin = root.position; binding.AreaRotation = root.rotation;
                        }
                        RememberFrame(binding);
                    }
                    binding.AreaAvailable = sameArea;
                    if (sameArea && record.Motion == ModWorldMotion.Default) binding.transform.SetPositionAndRotation(Vector3.Lerp(binding.transform.position, target, 1f-Mathf.Exp(-Time.unscaledDeltaTime*8f)), rotation);
                }
                if (record.ContainerId != 0 && !binding.ContainerReady && visible &&
                    LootCatcher.BindWorldContainer(record.ContainerId, record.ContainerVersion, binding.GetComponent<DaggerfallLoot>(), record.ContainerItems))
                {
                    // Show() may have cached the gate's disabled colliders. Restore the intended
                    // states before the next cull rather than permanently remembering 'disabled'.
                    Show(binding, true);
                    binding.GetComponent<ModWorldContainerGate>().Release(); binding.ContainerReady = true;
                }
            }
        }
        static bool IsWithinVisibility(ModWorldRecord record, Vector3 position)
        {
            var game = GameManager.Instance;
            var offset = position - game.PlayerObject.transform.position;
            if (record.Pose.Area == "E") offset.y = 0;
            return offset.sqrMagnitude <= record.VisibilityMetres * record.VisibilityMetres;
        }

        internal void PublishPoses()
        {
            if (!Ready || loading) return;
            foreach (var binding in bindings.Values.ToArray())
            {
                if (binding == null || !binding.Source || binding.State.Type == ModWorldObjectType.NativeEnemy || binding.State.Motion == ModWorldMotion.Fixed || Time.unscaledTime < binding.SendAt) continue;
                binding.SendAt = Time.unscaledTime + (binding.State.Motion == ModWorldMotion.Smooth ? SmoothPoseInterval : .25f);
                ModWorldPose pose;
                try { pose = ModWorldPose.Capture(binding.transform); } catch { continue; }
                if (pose.Area != binding.State.Pose.Area) continue;
                var previous = binding.LastPose;
                double distance = pose.Area == "E" ? pose.Exterior.HorizontalDistance(previous.Exterior) : Vector3.Distance(pose.Interior, previous.Interior);
                if (distance < .035 && Math.Abs(pose.Exterior.Height-previous.Exterior.Height) < .035f && Quaternion.Angle(pose.Rotation, previous.Rotation) < .5f) continue;
                binding.LastPose = pose; binding.State.Pose = pose;
                // Pose messages have no reply callback/retry; the latest pose supersedes older ones.
                MultiplayerAPI.SendJsonToServer(mod, RequestChannel, new ModWorldRequest { Request = Guid.NewGuid().ToString("N"),
                    Operation = "pose", Key = binding.Key, Record = binding.State, Epoch = localEpoch });
            }
        }
        void EndSession()
        {
            foreach (var binding in bindings.Values.ToArray()) DestroyBinding(binding, true);
            foreach (var original in staged.Values) if (original != null) UnityEngine.Object.Destroy(original);
            bindings.Clear(); staged.Clear(); pending.Clear(); server.Clear(); serverEpochs.Clear(); localEpoch = 0; resetNeeded = resetRequestPending = false; records.Clear(); revisions.Clear(); expires.Clear(); replies.Clear(); replyOrder.Clear();
        }
        public void Dispose()
        {
            if (disposed) return;
            if (MultiplayerAPI.IsServer)
            {
                foreach (var record in server.Values.ToArray())
                {
                    if (!record.Deleted) CleanupNative(record, true);
                    MultiplayerAPI.RemoveState(mod, record.Key, ulong.MaxValue);
                }
            }
            else ResetOwnedObjects();
            disposed = true; EndSession();
            MultiplayerAPI.SessionEnded -= EndSession;
            MultiplayerAPI.UnsubscribeState(mod); MultiplayerAPI.UnregisterClientHandler(mod, ReplyChannel); MultiplayerAPI.UnregisterServerHandler(mod, RequestChannel);
            worlds.Remove(mod); if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
        }
    }
}
