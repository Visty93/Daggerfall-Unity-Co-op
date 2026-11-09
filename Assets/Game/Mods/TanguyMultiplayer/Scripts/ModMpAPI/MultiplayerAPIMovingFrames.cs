// Moving-frame support for independent scene roots.
// No Transform parenting, source-mod dependency, network messages, or GameObject serialization.
// The moving reference must already exist locally (e.g., a ModWorld Smooth replica).
using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;

namespace DaggerfallCoop.API
{
    public sealed class ModMovingFrameOptions
    {
        // Maximum allowed displacement caused by the platform in one rendered frame.
        // Zero disables the limit. Use a larger value for fast ships than for wagons.
        public float MaxStepMetres = 3f;
        // Use the support collider for enemy deck tests and player support detection.
        public float GroundProbeMetres = .45f;
        public float LostSupportGraceSeconds = .12f;
        // Rotate actor heading with platform; keep the local player's up direction upright.
        public bool CarryRotation = true;
        public bool KeepPlayerUpright = true;
        // Only meaningful for FollowEnemyOnMovingFrame. Temporary pin belongs to this handle.
        public bool RequestEnemyOwnership = true;
        public float OwnershipRetrySeconds = .75f;
        public float EnemyContextIntervalSeconds = .10f;
        // Correct claim latency to initial deck-relative location (bounded by this distance).
        // Zero disables catch-up. This is not an interpolation/network transform.
        public float MaxEnemyClaimCatchupMetres = 20f;
    }

    public sealed class ModMovingFrameHandle : IDisposable
    {
        internal int Id;
        internal bool Closed;
        internal ModMovingFrameHandle(int id) { Id = id; }
        public bool IsActive { get { return !Closed; } }
        public void Dispose()
        {
            if (Closed) return;
            Closed = true;
            ModMovingFrameDriver.Remove(Id);
        }
    }

    public static partial class MultiplayerAPI
    {
        /// <summary>
        /// Moves an independent local root by the reference frame delta, AFTER independent
        /// movement for the frame. Does not parent, change authority or send any positions.
        /// For a networked root, only the current authority may apply the delta.
        /// The caller must dispose this handle when support ends.
        /// </summary>
        public static ModMovingFrameHandle FollowMovingFrame(Transform actorRoot, Transform movingFrame,
            ModMovingFrameOptions options = null)
        {
            return ModMovingFrameDriver.RegisterRoot(actorRoot, movingFrame, options);
        }

        /// <summary>
        /// Native network enemy support: optional temporary persistent owner pin, then carry
        /// only the authoritative network-enemy root while grounded on the supplied deck.
        /// A pin requested by this handle is released at its current pose when detached.
        /// Existing pins owned by another system are never released by this handle.
        /// </summary>
        public static ModMovingFrameHandle FollowEnemyOnMovingFrame(GameObject enemyRoot,
            Transform movingFrame, Collider deck, ModMovingFrameOptions options = null)
        {
            return ModMovingFrameDriver.RegisterEnemy(enemyRoot, movingFrame, deck, options);
        }

        /// <summary>
        /// Local player support on a replicated moving collider.
        /// Call on the receiving client after creating a replica with a solid deck.
        /// The player remains at scene root; native walking remains enabled.
        /// Never register the same surface in parallel with another passenger adapter.
        /// </summary>
        public static ModMovingFrameHandle RegisterLocalPlayerMovingSurface(Collider surface,
            Transform movingFrame, ModMovingFrameOptions options = null)
        {
            return ModMovingFrameDriver.RegisterSurface(surface, movingFrame, options);
        }
    }

    // Runs after typical gameplay movement and the late visual interpolation pass.
    [DefaultExecutionOrder(32000)]
    internal sealed class ModMovingFrameDriver : MonoBehaviour
    {
        enum Mode { Root, Enemy, Surface }
        sealed class Entry
        {
            public int Id;
            public Mode Kind;
            public Transform Root, Frame;
            public GameObject Enemy;
            public Collider Deck;
            public ModMovingFrameOptions Options;
            public Vector3 OldFramePosition, OldCompensation, JoinLocalPosition;
            public Quaternion OldFrameRotation, JoinLocalRotation;
            public bool Sampled, JoinAnchor, RequestedPin;
            public ModMovingFrameHandle Handle;
            public float NextClaim, NextContext, LostSince = -1f;
        }
        sealed class SurfaceState
        {
            public Entry Surface;
            public Matrix4x4 PreviousInverse;
            public Quaternion PreviousRotation;
            public Vector3 PreviousCompensation;
            public PlayerMotor Motor;
            public PlayerGroundMotor Ground;
            public float SavedFollower, AppliedFollower;
            public bool FollowerBoosted;
        }
        static ModMovingFrameDriver instance;
        static int nextId;
        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly List<Entry> snapshot = new List<Entry>();
        readonly SurfaceState carried = new SurfaceState();

        static ModMovingFrameDriver Ensure()
        {
            if (instance != null) return instance;
            var obj = new GameObject("Multiplayer API Moving Frames");
            DontDestroyOnLoad(obj);
            return obj.AddComponent<ModMovingFrameDriver>();
        }
        static ModMovingFrameOptions Validate(ModMovingFrameOptions input)
        {
            var o = input ?? new ModMovingFrameOptions();
            if (!Good(o.MaxStepMetres) || o.MaxStepMetres < 0 ||
                !Good(o.GroundProbeMetres) || o.GroundProbeMetres < .05f || o.GroundProbeMetres > 5f ||
                !Good(o.LostSupportGraceSeconds) || o.LostSupportGraceSeconds < 0 || o.LostSupportGraceSeconds > 5f ||
                !Good(o.OwnershipRetrySeconds) || o.OwnershipRetrySeconds < .1f ||
                !Good(o.EnemyContextIntervalSeconds) || o.EnemyContextIntervalSeconds < .02f ||
                !Good(o.MaxEnemyClaimCatchupMetres) || o.MaxEnemyClaimCatchupMetres < 0)
                throw new ArgumentOutOfRangeException("input", "Invalid moving-frame limits.");
            // Copy options so later caller edits do not change the running controller.
            return new ModMovingFrameOptions {
                MaxStepMetres=o.MaxStepMetres, GroundProbeMetres=o.GroundProbeMetres,
                LostSupportGraceSeconds=o.LostSupportGraceSeconds, CarryRotation=o.CarryRotation,
                KeepPlayerUpright=o.KeepPlayerUpright, RequestEnemyOwnership=o.RequestEnemyOwnership,
                OwnershipRetrySeconds=o.OwnershipRetrySeconds,
                EnemyContextIntervalSeconds=o.EnemyContextIntervalSeconds,
                MaxEnemyClaimCatchupMetres=o.MaxEnemyClaimCatchupMetres
            };
        }
        static bool Good(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        static bool IsDescendantOrSame(Transform node, Transform ancestor)
        { return node != null && ancestor != null && (node == ancestor || node.IsChildOf(ancestor)); }
        static ModMovingFrameHandle Register(Mode kind, Transform root, Transform frame, Collider deck, ModMovingFrameOptions options)
        {
            if (frame == null || (kind != Mode.Surface && root == null) ||
                (kind != Mode.Root && deck == null) || (kind == Mode.Root && root == frame) ||
                (root != null && (IsDescendantOrSame(root, frame) || IsDescendantOrSame(frame, root))))
                throw new ArgumentException("Expected an independent root, moving frame, and applicable support collider.");
            var runner = Ensure();
            // One controller per root; don't let two different boats carry the same actor.
            foreach (var existing in runner.entries.Values)
                if ((kind != Mode.Surface && existing.Kind != Mode.Surface && existing.Root == root) ||
                    (kind == Mode.Surface && existing.Kind == Mode.Surface && existing.Deck == deck))
                    throw new InvalidOperationException("This actor root already has a moving-frame controller.");
            int id = ++nextId;
            var e = new Entry { Id=id, Kind=kind, Root=root, Frame=frame, Deck=deck, Options=Validate(options),
                Enemy=kind==Mode.Enemy ? root.gameObject : null };
            e.Handle = new ModMovingFrameHandle(id);
            runner.entries.Add(id,e);
            return e.Handle;
        }
        internal static ModMovingFrameHandle RegisterRoot(Transform root, Transform frame, ModMovingFrameOptions options)
        { return Register(Mode.Root, root, frame, null, options); }
        internal static ModMovingFrameHandle RegisterEnemy(GameObject enemy, Transform frame, Collider deck, ModMovingFrameOptions options)
        {
            if (enemy == null || enemy.GetComponent<DynamicEnemyAuthority>() == null ||
                enemy.GetComponent<EnemyWorldPosition>() == null ||
                enemy.GetComponent<DaggerfallEnemy>() == null ||
                enemy.GetComponent<NetworkIdentity>() == null)
                throw new ArgumentException("Expected a registered native network enemy root.");
            return Register(Mode.Enemy, enemy.transform, frame, deck, options);
        }
        internal static ModMovingFrameHandle RegisterSurface(Collider surface, Transform frame, ModMovingFrameOptions options)
        {
            if (surface == null || !IsDescendantOrSame(surface.transform, frame))
                throw new ArgumentException("The support collider must belong to the moving frame hierarchy.");
            return Register(Mode.Surface, null, frame, surface, options);
        }
        internal static void Remove(int id)
        {
            if (instance == null) return;
            Entry e;
            if (!instance.entries.TryGetValue(id,out e)) return;
            instance.entries.Remove(id);
            instance.EndEntry(e);
        }
        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance=this; MultiplayerAPI.SessionEnded += Reset;
        }
        void OnDestroy()
        {
            if (instance != this) return;
            MultiplayerAPI.SessionEnded -= Reset;
            Reset(); instance = null;
        }
        void Reset()
        {
            // During session teardown transport may already be closed. Do not issue commands.
            ReleaseSurface();
            foreach (var e in entries.Values) if (e.Handle != null) e.Handle.Closed=true;
            entries.Clear(); snapshot.Clear();
        }
        void EndEntry(Entry e)
        {
            if (e.Handle != null) e.Handle.Closed=true;
            if (e == carried.Surface) ReleaseSurface();
            if (e.Kind == Mode.Enemy) ReleaseEnemyPin(e);
        }
        void ReleaseEnemyPin(Entry e)
        {
            if (!e.RequestedPin || e.Enemy == null || !NetworkClient.active || !NetworkClient.isConnected) return;
            uint owner;
            if (!MultiplayerAPI.TryGetPersistentEnemyOwner(e.Enemy, out owner)) return;
            if (owner == MultiplayerAPI.LocalPlayerId && MultiplayerAPI.IsAuthoritativeFor(e.Enemy))
            {
                if (MultiplayerAPI.ReleaseEnemyOwnership(e.Enemy)) return;
            }
            // Cancellation of an in-flight claim or fallback if actor context
            // is unavailable. Only handles that requested a pin may send this command.
            var authority = e.Enemy.GetComponent<DynamicEnemyAuthority>();
            if (authority != null && (owner == 0 || owner == MultiplayerAPI.LocalPlayerId))
                authority.CmdSetPersistentOwner(false);
        }
        static Vector3 Compensation()
        {
            return GameManager.HasInstance && GameManager.Instance.StreamingWorld != null ?
                GameManager.Instance.StreamingWorld.WorldCompensation : Vector3.zero;
        }
        static bool IsValid(Entry e)
        { return e != null && e.Frame != null && e.Frame.gameObject.activeInHierarchy &&
            (e.Kind==Mode.Surface || e.Root != null && e.Root.gameObject.activeInHierarchy); }
        static bool OnDeck(Entry e)
        {
            if (e.Deck==null || !e.Deck.enabled || e.Deck.isTrigger || !e.Deck.gameObject.activeInHierarchy ||
                e.Enemy==null) return false;
            var controller=e.Enemy.GetComponent<CharacterController>();
            if (controller==null || !controller.enabled || !controller.isGrounded) return false;
            RaycastHit hit;
            float distance = Mathf.Max(controller.height, e.Options.GroundProbeMetres);
            return Physics.Raycast(e.Root.position + Vector3.up*.1f, Vector3.down, out hit, distance+.2f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                hit.collider!=null && IsDescendantOrSame(hit.collider.transform, e.Deck.transform);
        }
        static bool IsNetworkOwner(Entry e)
        {
            // A NetworkIdentity that is not yet spawned is NOT an ordinary local prop.
            // Never start moving its root until this client has real authority.
            if (e.Root.GetComponent<NetworkIdentity>() != null)
                return MultiplayerAPI.IsAuthoritativeFor(e.Root.gameObject);
            return true;
        }
        void Remember(Entry e)
        {
            e.OldFramePosition=e.Frame.position;
            e.OldFrameRotation=e.Frame.rotation;
            e.OldCompensation=Compensation();
            e.Sampled=true;
        }
        bool ApplyDelta(Entry e)
        {
            // The actor's CURRENT pose is expressed in the previous frame. Thus local
            // movement made by EnemyMotor or by a mod this frame is not overwritten.
            Vector3 originShift=Compensation()-e.OldCompensation;
            Vector3 local=Quaternion.Inverse(e.OldFrameRotation)*(e.Root.position-originShift-e.OldFramePosition);
            Vector3 target=e.Frame.position+e.Frame.rotation*local;
            Quaternion rot=e.Frame.rotation*Quaternion.Inverse(e.OldFrameRotation);
            if (e.Options.MaxStepMetres>0 && (target-e.Root.position).sqrMagnitude >
                e.Options.MaxStepMetres*e.Options.MaxStepMetres)
            { Remember(e); return false; }
            e.Root.SetPositionAndRotation(target, e.Options.CarryRotation ? rot*e.Root.rotation : e.Root.rotation);
            Remember(e);
            return true;
        }
        void TickActor(Entry e)
        {
            if (!IsValid(e) || !MultiplayerAPI.IsActive) { Remove(e.Id); return; }
            if (e.Kind==Mode.Root)
            {
                if (!IsNetworkOwner(e)) { e.Sampled=false; return; }
                if (!e.Sampled) Remember(e); else ApplyDelta(e);
                return;
            }
            if (!NetworkClient.active || !NetworkClient.ready || MultiplayerAPI.LocalPlayerId==0)
            { e.Sampled=false; return; }
            if (e.Enemy==null) { Remove(e.Id); return; }
            var entity=e.Enemy.GetComponent<DaggerfallEntityBehaviour>();
            if (entity==null || entity.Entity==null || entity.Entity.CurrentHealth<=0)
            { Remove(e.Id); return; }
            bool deck=OnDeck(e);
            if (!deck)
            {
                if (e.LostSince<0) e.LostSince=Time.unscaledTime;
                if (Time.unscaledTime-e.LostSince>=e.Options.LostSupportGraceSeconds) { Remove(e.Id); return; }
            }
            else e.LostSince=-1;
            uint owner;
            if (!MultiplayerAPI.TryGetPersistentEnemyOwner(e.Enemy,out owner)) { Remove(e.Id); return; }
            uint local=MultiplayerAPI.LocalPlayerId;
            if (owner!=0 && owner!=local) { Remove(e.Id); return; }
            if (owner==0 && e.Options.RequestEnemyOwnership && deck && Time.unscaledTime>=e.NextClaim)
            {
                e.NextClaim=Time.unscaledTime+e.Options.OwnershipRetrySeconds;
                if (!e.JoinAnchor)
                {
                    e.JoinLocalPosition=e.Frame.InverseTransformPoint(e.Root.position);
                    e.JoinLocalRotation=Quaternion.Inverse(e.Frame.rotation)*e.Root.rotation;
                    e.JoinAnchor=true;
                }
                if (MultiplayerAPI.ClaimEnemyOwnership(e.Enemy)) e.RequestedPin=true;
            }
            if (owner!=local || !MultiplayerAPI.IsAuthoritativeFor(e.Enemy))
            { e.Sampled=false; return; }
            if (!e.Sampled)
            {
                // Ownership can arrive after the boat moved several frames.
                if (e.RequestedPin && e.JoinAnchor && e.Options.MaxEnemyClaimCatchupMetres>0)
                {
                    Vector3 target=e.Frame.TransformPoint(e.JoinLocalPosition);
                    if ((target-e.Root.position).sqrMagnitude <=
                        e.Options.MaxEnemyClaimCatchupMetres*e.Options.MaxEnemyClaimCatchupMetres)
                        e.Root.SetPositionAndRotation(target,e.Frame.rotation*e.JoinLocalRotation);
                }
                Remember(e);
            }
            else ApplyDelta(e);
            if (e.RequestedPin && Time.unscaledTime>=e.NextContext)
            {
                e.NextContext=Time.unscaledTime+e.Options.EnemyContextIntervalSeconds;
                MultiplayerAPI.PublishOwnedEnemyContext(e.Enemy);
            }
        }
        static bool Supports(Entry surface, Collider actual)
        {
            return surface!=null && actual!=null && IsValid(surface) &&
                surface.Deck!=null && surface.Deck.enabled && !surface.Deck.isTrigger &&
                IsDescendantOrSame(actual.transform,surface.Deck.transform);
        }
        static bool Probe(Collider controller, Vector3 foot, float length, int excludedLayer, out RaycastHit hit)
        {
            int mask=Physics.DefaultRaycastLayers & ~(1<<excludedLayer);
            return Physics.Raycast(foot+Vector3.up*.18f,Vector3.down,out hit,length+.18f,mask,
                QueryTriggerInteraction.Ignore) && hit.collider!=controller && hit.normal.y>.5f;
        }
        void ReleaseSurface()
        {
            var state=carried;
            if (state.Surface!=null && state.Ground!=null && state.Ground.ActivePlatform!=null &&
                IsDescendantOrSame(state.Ground.ActivePlatform,state.Surface.Frame))
                state.Ground.ClearActivePlatform();
            if (state.FollowerBoosted && state.Motor!=null &&
                Mathf.Abs(state.Motor.smoothFollowerLerpSpeed-state.AppliedFollower)<.01f)
                state.Motor.smoothFollowerLerpSpeed=state.SavedFollower;
            state.Surface=null; state.Ground=null; state.Motor=null; state.FollowerBoosted=false;
        }
        static bool PlayerReady(out Transform root, out CharacterController controller, out PlayerMotor motor, out PlayerGroundMotor ground)
        {
            root=null; controller=null;motor=null;ground=null;
            if (!NetworkClient.active || !NetworkClient.ready || MultiplayerAPI.LocalPlayerId==0 ||
                !GameManager.HasInstance || !SaveLoadManager.HasInstance || SaveLoadManager.Instance.LoadInProgress) return false;
            if (!GameManager.Instance.IsPlayingGame() && Time.timeScale>0.0001f) return false;
            var player=GameManager.Instance.PlayerObject;
            if (player==null || !player.activeInHierarchy) return false;
            root=player.transform;controller=player.GetComponent<CharacterController>();
            motor=player.GetComponent<PlayerMotor>();ground=player.GetComponent<PlayerGroundMotor>();
            return controller!=null && controller.enabled && controller.detectCollisions &&
                motor!=null && !motor.IsLevitating && !(motor.IsJumping && motor.MoveDirection.y>0);
        }
        void CacheSurface(Entry e, PlayerMotor motor, PlayerGroundMotor ground)
        {
            carried.Surface=e;
            carried.PreviousInverse=e.Deck.transform.worldToLocalMatrix;
            carried.PreviousRotation=e.Deck.transform.rotation;
            carried.PreviousCompensation=Compensation();
            carried.Motor=motor;carried.Ground=ground;
            if (ground!=null && ground.ActivePlatform!=null && IsDescendantOrSame(ground.ActivePlatform,e.Frame))
                ground.ClearActivePlatform();
            if (!carried.FollowerBoosted)
            {
                carried.SavedFollower=motor.smoothFollowerLerpSpeed;
                carried.AppliedFollower=Mathf.Max(carried.SavedFollower,250f);
                carried.FollowerBoosted=true;
            }
            motor.smoothFollowerLerpSpeed=carried.AppliedFollower;
        }
        void TickPlayer()
        {
            if (entries.Count==0) { ReleaseSurface();return; }
            Transform root; CharacterController controller;PlayerMotor motor;PlayerGroundMotor ground;
            if (!PlayerReady(out root,out controller,out motor,out ground)) { ReleaseSurface();return; }
            if (carried.Surface==null && !HasSurfaceNearby(root.position)) return;
            Physics.SyncTransforms();
            var bounds=controller.bounds;
            var foot=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            RaycastHit hit;
            Entry active=carried.Surface;
            if (active!=null)
            {
                if (!Supports(active,active.Deck)) { ReleaseSurface();return; }
                var floor=active.Deck.transform;
                Vector3 shift=Compensation()-carried.PreviousCompensation;
                Vector3 point=floor.TransformPoint(carried.PreviousInverse.MultiplyPoint3x4(root.position-shift));
                Vector3 projectedFoot=floor.TransformPoint(carried.PreviousInverse.MultiplyPoint3x4(foot-shift));
                if ((active.Options.MaxStepMetres>0 && (point-root.position).sqrMagnitude >
                     active.Options.MaxStepMetres*active.Options.MaxStepMetres) ||
                    !Probe(controller,projectedFoot,active.Options.GroundProbeMetres,root.gameObject.layer,out hit) ||
                    !Supports(active,hit.collider)) { ReleaseSurface();return; }
                var rotation=floor.rotation*Quaternion.Inverse(carried.PreviousRotation);
                if (active.Options.CarryRotation && active.Options.KeepPlayerUpright)
                    rotation=Quaternion.FromToRotation(rotation*root.up,root.up)*rotation;
                if (ground!=null && ground.ActivePlatform!=null && IsDescendantOrSame(ground.ActivePlatform,active.Frame))
                    ground.ClearActivePlatform();
                root.SetPositionAndRotation(point,active.Options.CarryRotation?rotation*root.rotation:root.rotation);
                Physics.SyncTransforms();
                CacheSurface(active,motor,ground);
            }
            else
            {
                float maxDistance=5f;
                if (!Probe(controller,foot,maxDistance,root.gameObject.layer,out hit)) return;
                foreach (var entry in entries.Values)
                    if (entry.Kind==Mode.Surface && Supports(entry,hit.collider) &&
                        hit.distance <= entry.Options.GroundProbeMetres+.18f)
                    { CacheSurface(entry,motor,ground); break; }
            }
        }
        bool HasSurfaceNearby(Vector3 point)
        {
            foreach (var e in entries.Values)
                if (e.Kind==Mode.Surface && IsValid(e) && e.Deck!=null && e.Deck.enabled &&
                    (e.Frame.position-point).sqrMagnitude < 32f*32f) return true;
            return false;
        }
        void LateUpdate()
        {
            if (entries.Count==0) return;
            snapshot.Clear();snapshot.AddRange(entries.Values);
            // Refresh moved support colliders: bring moved boat/wagon colliders up to date
            // before the deck-contact raycast.
            Physics.SyncTransforms();
            bool moved=false;
            foreach (var e in snapshot)
            {
                if (!entries.ContainsKey(e.Id) || e.Kind==Mode.Surface) continue;
                Vector3 before=e.Root!=null?e.Root.position:Vector3.zero;
                TickActor(e);
                if (e.Root!=null && e.Root.position!=before) moved=true;
            }
            if (moved) Physics.SyncTransforms();
            TickPlayer();
        }
    }
}
