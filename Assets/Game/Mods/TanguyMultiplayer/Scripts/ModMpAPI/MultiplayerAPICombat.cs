// Daggerfall Unity Co-op - validated, mod-defined enemy combat events.
// Does not perform damage, determine a mod's attack pattern, or ship mod assets.
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace DaggerfallCoop.API
{
    public enum ModEnemyCombatPhase : byte
    {
        Begin = 0,
        Launch = 1,
        Effect = 2,
        Hit = 3,
        End = 4,
        Cancel = 5
    }

    [Serializable]
    public sealed class ModEnemyCombatEvent
    {
        public uint EnemyNetId;
        public uint TargetNetId;       // Required only for Hit; may be another network enemy.
        public uint Sequence;          // Unique/increasing per enemy, per adapter, per session.
        public byte Attack;            // Mod-defined maneuver identifier.
        public ModEnemyCombatPhase Phase;
    }

    public struct ModEnemyCombatContext
    {
        public ModEnemyCombatEvent Event;
        public uint SenderPlayerId;   // 0 for a server-simulated enemy.
        public GameObject Enemy;
        public GameObject Target;      // Null for non-Hit phases.
    }

    public static partial class MultiplayerAPI
    {
        // Register on the server and every client running the adapter. The server
        // callback decides how to apply the installed mod's actual gameplay rules.
        // The client callback is for remote presentation, never damage.
        public static bool RegisterEnemyCombatChannel(string modGuid, string channel,
            float maximumHitDistance, Action<ModEnemyCombatContext> validatedServerEvent,
            Action<ModEnemyCombatEvent> observerEvent)
        {
            return MultiplayerAPICombat.Service.Register(modGuid, channel,
                maximumHitDistance, validatedServerEvent, observerEvent);
        }

        public static void UnregisterEnemyCombatChannel(string modGuid, string channel)
        {
            MultiplayerAPICombat.Service.Unregister(modGuid, channel);
        }

        // Call only on the peer SIMULATING this network enemy. The server verifies
        // actual ownership, sequences, target and distance before accepting Hit.
        public static bool SendEnemyCombatEvent(string modGuid, string channel, ModEnemyCombatEvent attack)
        {
            return MultiplayerAPICombat.Service.Send(modGuid, channel, attack);
        }
    }

    [DefaultExecutionOrder(-24490)]
    internal sealed class MultiplayerAPICombat : MonoBehaviour
    {
        const float AttackTimeout = 45f;
        const float PendingTimeout = 3f;
        const int MaxPending = 128;
        static MultiplayerAPICombat instance;

        sealed class AttackState
        {
            public uint Sequence;
            public uint Sender;
            public float Last;
            public readonly HashSet<uint> Targets = new HashSet<uint>();
        }
        sealed class Registration
        {
            public string Guid, Channel;
            public float HitRange;
            public Action<ModEnemyCombatContext> Server;
            public Action<ModEnemyCombatEvent> Observer;
            public readonly Dictionary<uint, AttackState> Active = new Dictionary<uint, AttackState>();
            public readonly Dictionary<ulong, uint> LastSequence = new Dictionary<ulong, uint>();
        }
        sealed class Pending
        {
            public Registration Registration;
            public ModEnemyCombatEvent Event;
            public float Received;
        }

        readonly Dictionary<string, Registration> registrations =
            new Dictionary<string, Registration>(StringComparer.Ordinal);
        readonly List<Pending> pending = new List<Pending>();
        readonly List<uint> expired = new List<uint>();
        float nextPrune;

        internal static MultiplayerAPICombat Service
        {
            get
            {
                if (instance != null) return instance;
                var existing = UnityEngine.Object.FindObjectOfType<MultiplayerAPICombat>();
                if (existing != null) return instance = existing;
                var go = new GameObject("Co-op Enemy Combat API");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<MultiplayerAPICombat>();
                return instance;
            }
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            MultiplayerAPI.SessionEnded += Reset;
        }

        void OnDestroy()
        {
            if (instance != this) return;
            MultiplayerAPI.SessionEnded -= Reset;
            foreach (Registration reg in registrations.Values) RemoveHandlers(reg);
            registrations.Clear();
            Reset();
            instance = null;
        }

        static string Key(string guid, string channel) { return guid + "\n" + channel; }
        static string RequestChannel(Registration reg) { return reg.Channel + "/request"; }
        static string ObserverChannel(Registration reg) { return reg.Channel + "/state"; }

        internal bool Register(string guid, string channel, float range,
            Action<ModEnemyCombatContext> server, Action<ModEnemyCombatEvent> observer)
        {
            // Reserve suffix space for request/state subchannels.
            if (string.IsNullOrEmpty(guid) || guid.Length > MultiplayerAPI.MaxGuidLength ||
                string.IsNullOrEmpty(channel) || channel.Length > 48 ||
                range <= 0f || range > 40f || float.IsNaN(range) ||
                float.IsInfinity(range) || server == null ||
                registrations.ContainsKey(Key(guid, channel))) return false;
            var reg = new Registration { Guid = guid, Channel = channel,
                HitRange = range, Server = server, Observer = observer };
            if (!MultiplayerAPI.RegisterJsonServerHandler<ModEnemyCombatEvent>(guid, RequestChannel(reg),
                (context, message) => Process(reg, context.SenderPlayerId, message, false))) return false;
            if (!MultiplayerAPI.RegisterJsonClientHandler<ModEnemyCombatEvent>(guid, ObserverChannel(reg),
                (context, message) => ReceiveVisual(reg, message)))
            {
                MultiplayerAPI.UnregisterServerHandler(guid, RequestChannel(reg));
                return false;
            }
            registrations.Add(Key(guid, channel), reg);
            return true;
        }

        internal void Unregister(string guid, string channel)
        {
            Registration reg;
            if (!registrations.TryGetValue(Key(guid, channel), out reg)) return;
            RemoveHandlers(reg);
            registrations.Remove(Key(guid, channel));
            pending.RemoveAll(p => p.Registration == reg);
        }

        static void RemoveHandlers(Registration reg)
        {
            MultiplayerAPI.UnregisterServerHandler(reg.Guid, RequestChannel(reg));
            MultiplayerAPI.UnregisterClientHandler(reg.Guid, ObserverChannel(reg));
        }

        void Reset()
        {
            pending.Clear();
            foreach (Registration reg in registrations.Values)
            {
                reg.Active.Clear(); reg.LastSequence.Clear();
            }
        }

        static bool Valid(ModEnemyCombatEvent message)
        {
            return message != null && message.EnemyNetId != 0 && message.Sequence != 0 &&
                Enum.IsDefined(typeof(ModEnemyCombatPhase), message.Phase) &&
                (message.Phase == ModEnemyCombatPhase.Hit ? message.TargetNetId != 0 : message.TargetNetId == 0);
        }

        internal bool Send(string guid, string channel, ModEnemyCombatEvent message)
        {
            Registration reg;
            if (!registrations.TryGetValue(Key(guid, channel), out reg) || !Valid(message)) return false;
            ModNetworkEnemy enemy;
            if (!MultiplayerAPI.TryGetNetworkEnemy(message.EnemyNetId, out enemy) ||
                !enemy.IsSimulationOwner) return false;
            if (NetworkServer.active)
            {
                // Server-side simulator (dedicated or host) uses the same checks as client messages.
                Process(reg, 0, message, true);
                return true;
            }
            return MultiplayerAPI.SendJsonToServer(guid, RequestChannel(reg), message);
        }

        // senderId is supplied by the co-op transport; never read it from a mod packet.
        void Process(Registration reg, uint senderId, ModEnemyCombatEvent message, bool serverLocal)
        {
            if (!NetworkServer.active || !Valid(message)) return;
            NetworkIdentity source;
            if (!NetworkServer.spawned.TryGetValue(message.EnemyNetId, out source) || source == null ||
                source.GetComponent<DaggerfallWorkshop.DaggerfallEnemy>() == null) return;
            NetworkConnection owner = source.connectionToClient;
            if (serverLocal)
            {
                if (owner != null && owner != NetworkServer.localConnection) return;
            }
            else
            {
                if (senderId == 0 || owner == null || owner.identity == null ||
                    owner.identity.netId != senderId) return;
            }

            float now = Time.unscaledTime;
            AttackState attack;
            if (message.Phase == ModEnemyCombatPhase.Begin)
            {
                // Each owner has its own sequence counter. After authority handoff
                // the new owner can start at 1 without colliding with the prior one.
                ulong ownerKey = ((ulong)message.EnemyNetId << 32) | senderId;
                uint previous;
                reg.LastSequence.TryGetValue(ownerKey, out previous);
                if (message.Sequence <= previous) return;
                reg.LastSequence[ownerKey] = message.Sequence;
                attack = new AttackState { Sequence = message.Sequence,
                    Sender = senderId, Last = now };
                reg.Active[message.EnemyNetId] = attack;
            }
            else
            {
                if (!reg.Active.TryGetValue(message.EnemyNetId, out attack) ||
                    attack.Sequence != message.Sequence || attack.Sender != senderId ||
                    now - attack.Last > AttackTimeout) return;
                attack.Last = now;
            }

            GameObject target = null;
            if (message.Phase == ModEnemyCombatPhase.Hit)
            {
                if (message.TargetNetId == message.EnemyNetId ||
                    !attack.Targets.Add(message.TargetNetId)) return;
                NetworkIdentity victim;
                if (!NetworkServer.spawned.TryGetValue(message.TargetNetId, out victim) || victim == null ||
                    Vector3.Distance(source.transform.position, victim.transform.position) > reg.HitRange)
                {
                    attack.Targets.Remove(message.TargetNetId);
                    return;
                }
                // The gameplay mod must still check alive state, teams, attack-specific
                // reach, visibility and apply the correct authoritative damage itself.
                target = victim.gameObject;
            }
            if (message.Phase == ModEnemyCombatPhase.End ||
                message.Phase == ModEnemyCombatPhase.Cancel)
                reg.Active.Remove(message.EnemyNetId);

            if (reg.Server != null)
            {
                try { reg.Server(new ModEnemyCombatContext { Event = message,
                    SenderPlayerId = senderId, Enemy = source.gameObject, Target = target }); }
                catch (Exception e) { Debug.LogError("[MPAPI] Server combat callback failed: " + e); }
            }
            // Only visual phases are broadcast. Damage must never be replayed by observers.
            if (message.Phase != ModEnemyCombatPhase.Hit)
                MultiplayerAPI.SendJsonToAll(reg.Guid, ObserverChannel(reg), message);
        }

        void ReceiveVisual(Registration reg, ModEnemyCombatEvent message)
        {
            if (!Valid(message) || message.Phase == ModEnemyCombatPhase.Hit || reg.Observer == null) return;
            if (TryDeliver(reg, message)) return;
            // Mirror's spawn/initial settings may arrive after the animation phase.
            if (pending.Count >= MaxPending) pending.RemoveAt(0);
            pending.Add(new Pending { Registration = reg, Event = message,
                Received = Time.unscaledTime });
        }

        bool TryDeliver(Registration reg, ModEnemyCombatEvent message)
        {
            ModNetworkEnemy enemy;
            if (!MultiplayerAPI.TryGetNetworkEnemy(message.EnemyNetId, out enemy)) return false;
            if (enemy.IsSimulationOwner) return true; // Already runs its own effect.
            try { reg.Observer(message); }
            catch (Exception e) { Debug.LogError("[MPAPI] Client combat visual callback failed: " + e); }
            return true;
        }

        void Update()
        {
            if (!MultiplayerAPI.IsActive)
            {
                if (pending.Count > 0) pending.Clear();
                return;
            }
            for (int i = 0; i < pending.Count; )
            {
                Pending item = pending[i];
                if (Time.unscaledTime - item.Received > PendingTimeout ||
                    TryDeliver(item.Registration, item.Event)) pending.RemoveAt(i);
                else ++i;
            }
            if (!NetworkServer.active || Time.unscaledTime < nextPrune) return;
            nextPrune = Time.unscaledTime + 3f;
            foreach (Registration reg in registrations.Values)
            {
                expired.Clear();
                foreach (var pair in reg.Active)
                {
                    NetworkIdentity identity;
                    if (Time.unscaledTime - pair.Value.Last > AttackTimeout ||
                        !NetworkServer.spawned.TryGetValue(pair.Key, out identity) || identity == null)
                        expired.Add(pair.Key);
                }
                foreach (uint id in expired) reg.Active.Remove(id);
                // LastSequence is retained for the session to reject replayed old requests.
            }
        }
    }
}
