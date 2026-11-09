using System;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using Mirror;
using UnityEngine;

namespace DaggerfallCoop.API
{
    public enum ModEnemyParenting
    {
        Default,
        Detach,
        Preserve
    }

    public static partial class MultiplayerAPI
    {
        static readonly HashSet<Transform> companionRoots = new HashSet<Transform>();

        // Register on the local player before hosting or joining. Membership follows the hierarchy.
        public static bool RegisterCompanionRoot(Transform root)
        {
            companionRoots.RemoveWhere(registered => registered == null);
            return root != null && companionRoots.Add(root);
        }

        public static bool UnregisterCompanionRoot(Transform root)
        {
            return companionRoots.Remove(root);
        }

        internal static Transform FindRegisteredCompanionRoot(GameObject actor, bool requireLocalOwner)
        {
            if (actor == null || companionRoots.Count == 0) return null;

            for (Transform parent = actor.transform.parent; parent != null; parent = parent.parent)
            {
                if (!companionRoots.Contains(parent)) continue;

                var identity = actor.GetComponent<NetworkIdentity>();
                var authority = actor.GetComponent<DynamicEnemyAuthority>();
                var behaviour = actor.GetComponent<DaggerfallEntityBehaviour>();
                var enemy = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (identity == null || authority == null || enemy == null || enemy.CurrentHealth <= 0 ||
                    actor.GetComponent<DaggerfallEnemy>() == null || actor.GetComponent<SerializableEnemy>() == null ||
                    actor.GetComponent<EnemyWorldPosition>() == null || actor.GetComponent<SetupDemoEnemy>() == null ||
                    actor.GetComponentInParent<PlayerMultiplayer>() != null ||
                    actor.GetComponent<ModWorldBinding>() != null)
                    return null;

                // Only the recruiting peer may retain the original on shutdown.
                uint owner = authority.PersistentOwnerPlayerNetId;
                if (identity.netId != 0 && (owner == 0 ? requireLocalOwner : owner != LocalPlayerId))
                    return null;
                return parent;
            }
            return null;
        }

        // Zero means this enemy has no persistent owner. This is independent of current Mirror authority.
        public static bool TryGetPersistentEnemyOwner(GameObject enemy, out uint playerId)
        {
            playerId = 0;
            DynamicEnemyAuthority authority;
            if (!TryGetRegisteredEnemyAuthority(enemy, out authority)) return false;
            playerId = authority.PersistentOwnerPlayerNetId;
            return true;
        }

        // A true return means the request was sent or the local player already owns the pin.
        // Confirm a new claim through TryGetPersistentEnemyOwner and IsAuthoritativeFor.
        public static bool ClaimEnemyOwnership(GameObject enemy)
        {
            DynamicEnemyAuthority authority;
            if (!TryGetLocalEnemyOwnershipTarget(enemy, out authority)) return false;
            uint owner = authority.PersistentOwnerPlayerNetId;
            if (owner != 0) return owner == LocalPlayerId;

            var behaviour = enemy.GetComponent<DaggerfallEntityBehaviour>();
            var entity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (entity == null || entity.CurrentHealth <= 0) return false;
            authority.CmdSetPersistentOwner(true);
            return true;
        }

        // Publish after changing locations, once the owning player's destination is ready.
        public static bool PublishOwnedEnemyContext(GameObject enemy)
        {
            return SendOwnedEnemyContext(enemy, false);
        }

        // Commit the current pose and location before the server resumes proximity authority.
        public static bool ReleaseEnemyOwnership(GameObject enemy)
        {
            return SendOwnedEnemyContext(enemy, true);
        }

        static bool SendOwnedEnemyContext(GameObject enemy, bool release)
        {
            DynamicEnemyAuthority authority;
            if (!TryGetLocalEnemyOwnershipTarget(enemy, out authority) ||
                authority.PersistentOwnerPlayerNetId != LocalPlayerId ||
                !enemy.GetComponent<NetworkIdentity>().hasAuthority)
                return false;

            var world = enemy.GetComponent<EnemyWorldPosition>();
            bool dungeon, interior;
            int baseX, baseZ, offsetX, offsetZ;
            if (!world.TryCaptureLocalActorContext(out dungeon, out interior, out baseX, out baseZ, out offsetX, out offsetZ))
                return false;

            if (release)
                authority.CmdReleasePersistentOwnerAtPose(enemy.transform.position, enemy.transform.rotation,
                    dungeon, interior, baseX, baseZ, offsetX, offsetZ);
            else
                world.CmdPublishBoundActorContext(dungeon, interior, baseX, baseZ, offsetX, offsetZ);
            return true;
        }

        static bool TryGetLocalEnemyOwnershipTarget(GameObject enemy, out DynamicEnemyAuthority authority)
        {
            authority = null;
            if (!NetworkClient.active || !NetworkClient.isConnected || !NetworkClient.ready || LocalPlayerId == 0 ||
                !TryGetRegisteredEnemyAuthority(enemy, out authority))
                return false;

            var identity = enemy.GetComponent<NetworkIdentity>();
            NetworkIdentity clientIdentity;
            if (!NetworkClient.spawned.TryGetValue(identity.netId, out clientIdentity) || clientIdentity != identity ||
                enemy.GetComponent<EnemyWorldPosition>() == null)
                return false;

            // The world service manages its own scripted pins and controller cleanup by key.
            var binding = enemy.GetComponent<ModWorldBinding>();
            return binding == null || binding.Record == null || !binding.Record.ScriptedEnemy;
        }

        static bool TryGetRegisteredEnemyAuthority(GameObject enemy, out DynamicEnemyAuthority authority)
        {
            authority = null;
            if (enemy == null || enemy.GetComponentInParent<PlayerMultiplayer>() != null)
                return false;
            var identity = enemy.GetComponent<NetworkIdentity>();
            GameObject registered;
            if (identity == null || !TryGetNetworkObject(identity.netId, out registered) || registered != enemy)
                return false;
            authority = enemy.GetComponent<DynamicEnemyAuthority>();
            return authority != null;
        }
    }

    public sealed partial class ModWorld
    {
        sealed class EnemyBehavior
        {
            public Func<uint, string, bool> Validate;
            public Func<GameObject, string, IDisposable> Start;
        }

        readonly Dictionary<string, EnemyBehavior> enemyBehaviors =
            new Dictionary<string, EnemyBehavior>(StringComparer.Ordinal);

        // Register on the server and every client that can own these enemies.
        // Start attaches local code and returns a handle that stops and removes it.
        public bool RegisterEnemyBehavior(string id, Func<uint, string, bool> validate,
            Func<GameObject, string, IDisposable> start)
        {
            if (disposed || string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
                validate == null || start == null || enemyBehaviors.ContainsKey(id)) return false;
            enemyBehaviors.Add(id, new EnemyBehavior { Validate = validate, Start = start });
            return true;
        }

        bool ConfigureEnemyBehavior(ModWorldRecord record, ModEnemyOptions options)
        {
            if (!Enum.IsDefined(typeof(ModEnemyParenting), options.Parenting)) return false;
            record.Parenting = options.Parenting;
            record.ScriptedEnemy = options.ScriptedOwner;
            record.BehaviorId = options.BehaviorId;
            record.BehaviorData = options.BehaviorData;
            if (string.IsNullOrEmpty(record.BehaviorId)) return string.IsNullOrEmpty(record.BehaviorData);
            EnemyBehavior behavior;
            return options.ScriptedOwner && record.BehaviorId.Length <= 64 &&
                (record.BehaviorData == null || record.BehaviorData.Length <= 4096) &&
                enemyBehaviors.TryGetValue(record.BehaviorId, out behavior) &&
                behavior.Validate(record.OwnerPlayerId, record.BehaviorData);
        }

        public static bool ApplyEnemyParenting(GameObject enemy, ModEnemyParenting policy)
        {
            GameObject registered;
            if (enemy == null || enemy.GetComponentInParent<PlayerMultiplayer>() != null ||
                enemy.GetComponent<DynamicEnemyAuthority>() == null ||
                !MultiplayerAPI.TryGetNetworkObject(enemy.GetComponent<Mirror.NetworkIdentity>()?.netId ?? 0, out registered) ||
                registered != enemy ||
                !Enum.IsDefined(typeof(ModEnemyParenting), policy)) return false;

            var guard = enemy.GetComponent<NetworkEnemyRootGuard>();
            if (policy == ModEnemyParenting.Preserve && guard == null)
                guard = enemy.AddComponent<NetworkEnemyRootGuard>();
            if (guard != null) guard.ApiPreserveHierarchy = policy == ModEnemyParenting.Preserve;
            if (policy == ModEnemyParenting.Detach && enemy.transform.parent != null)
                enemy.transform.SetParent(null, true);
            return true;
        }

        void TickEnemyBehavior(ModWorldBinding binding)
        {
            var record = binding.State;
            bool run = record.ScriptedEnemy && !string.IsNullOrEmpty(record.BehaviorId) &&
                record.OwnerPlayerId == MultiplayerAPI.LocalPlayerId &&
                MultiplayerAPI.IsAuthoritativeFor(binding.gameObject);
            if (!run)
            {
                binding.StopEnemyBehavior();
                return;
            }

            if (binding.BehaviorAttempted) return;
            binding.BehaviorAttempted = true;
            try
            {
                EnemyBehavior behavior;
                if (!enemyBehaviors.TryGetValue(record.BehaviorId, out behavior))
                    throw new InvalidOperationException("Missing enemy behavior registration: " + record.BehaviorId);
                binding.RunningBehavior = behavior.Start(binding.gameObject, record.BehaviorData);
                if (binding.RunningBehavior == null)
                    throw new InvalidOperationException("Enemy behavior must return a cleanup handle.");
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                ReleaseEnemy(record.Key);
            }
        }
    }
}
