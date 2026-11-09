// Example of the existing-network-enemy API. No third-party mod types are used.
// Your adapter supplies its own rules for which enemies participate and how
// the installed gameplay mod implements attacks, damage and visual effects.
using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallCoop.API;

public sealed class MultiplayerAPIExistingEnemyCombatExample : MonoBehaviour
{
    const string ModGuid = "example.enemycombat";
    const string Registration = "example.enemycombat.behavior";
    const string CombatChannel = "special";
    readonly Dictionary<uint, uint> sequences = new Dictionary<uint, uint>();

    void OnEnable()
    {
        // Register on host, clients and dedicated server. The filter is based
        // on data which exists on remote enemies (not on a local mod component).
        MultiplayerAPI.RegisterExistingEnemyBehavior(Registration,
            enemy => enemy.GameObject.GetComponent<EnemyMotor>() != null,
            AttachToExistingEnemy, OnEnemyAuthorityChanged);
        MultiplayerAPI.NetworkEnemyRemoved += OnEnemyRemoved;
        MultiplayerAPI.RegisterEnemyCombatChannel(ModGuid, CombatChannel, 4.5f,
            OnValidatedServerCombat, OnObserverPhase);
        MultiplayerAPI.SessionEnded += OnSessionEnded;
    }

    IDisposable AttachToExistingEnemy(ModNetworkEnemy enemy)
    {
        // Called once after co-op's native enemy setup is ready on this peer.
        // Add only YOUR adapter's components here. Avoid repeating mod spawn events.
        var marker = enemy.GameObject.GetComponent<ExampleEnemyPresentation>();
        bool created = marker == null;
        if (created) marker = enemy.GameObject.AddComponent<ExampleEnemyPresentation>();
        marker.IsSimulator = enemy.IsSimulationOwner;
        return new MarkerLease(marker, created);
    }

    void OnEnemyAuthorityChanged(ModNetworkEnemy enemy)
    {
        var marker = enemy.GameObject.GetComponent<ExampleEnemyPresentation>();
        if (marker != null) marker.IsSimulator = enemy.IsSimulationOwner;
        // An installed mod's active attack coroutine could be cancelled here
        // when its owner loses authority. That is specific to that mod.
    }

    void OnEnemyRemoved(ModNetworkEnemy enemy)
    {
        sequences.Remove(enemy.NetId);
    }

    // Call from a mod-controlled attack start, ONLY on the enemy simulator.
    public uint BeginSpecialAttack(GameObject enemyObject, byte attackType)
    {
        uint id;
        if (!MultiplayerAPI.TryGetNetworkId(enemyObject, out id) ||
            !MultiplayerAPI.CanSimulateNetworkEnemy(enemyObject)) return 0;
        uint current;
        sequences.TryGetValue(id, out current);
        uint next = current + 1;
        if (next == 0) next = 1;
        var message = new ModEnemyCombatEvent {
            EnemyNetId = id, Sequence = next, Attack = attackType,
            Phase = ModEnemyCombatPhase.Begin
        };
        if (!MultiplayerAPI.SendEnemyCombatEvent(ModGuid, CombatChannel, message)) return 0;
        sequences[id] = next;
        return next;
    }

    // Send Launch, Effect or End with the SAME sequence; never replay damage
    // on remote observers. Hit must supply an existing network target ID.
    public void SendPhase(uint enemyId, uint sequence, byte attack, ModEnemyCombatPhase phase,
        uint targetId = 0)
    {
        MultiplayerAPI.SendEnemyCombatEvent(ModGuid, CombatChannel, new ModEnemyCombatEvent {
            EnemyNetId = enemyId, TargetNetId = targetId,
            Sequence = sequence, Attack = attack, Phase = phase
        });
    }

    void OnValidatedServerCombat(ModEnemyCombatContext context)
    {
        if (context.Event.Phase != ModEnemyCombatPhase.Hit) return;
        // The API has checked enemy simulation ownership, an active attack,
        // duplicate target, and a maximum range. YOUR mod still validates
        // attack-specific reach, team, target life, and actual damage rules.
        // If context.Target is a player, send the validated hit to that
        // player's client using MultiplayerAPI.SendToPlayer, where its installed
        // gameplay mod should apply the correct damage. Never use a visual event
        // to damage remote players.
        Debug.Log("[EnemyCombatExample] Server validated target " +
            context.Event.TargetNetId + " for attack " + context.Event.Sequence);
    }

    void OnObserverPhase(ModEnemyCombatEvent phase)
    {
        // Runs only on non-simulating clients; display animation/particles here.
        Debug.Log("[EnemyCombatExample] Observer phase " + phase.Phase +
            " enemy=" + phase.EnemyNetId);
    }

    void OnSessionEnded() { sequences.Clear(); }

    void OnDisable()
    {
        MultiplayerAPI.UnregisterExistingEnemyBehavior(Registration);
        MultiplayerAPI.NetworkEnemyRemoved -= OnEnemyRemoved;
        MultiplayerAPI.UnregisterEnemyCombatChannel(ModGuid, CombatChannel);
        MultiplayerAPI.SessionEnded -= OnSessionEnded;
        sequences.Clear();
    }

    sealed class MarkerLease : IDisposable
    {
        ExampleEnemyPresentation marker;
        readonly bool created;
        public MarkerLease(ExampleEnemyPresentation marker, bool created)
        {
            this.marker = marker; this.created = created;
        }
        public void Dispose()
        {
            if (marker == null) return;
            if (created) UnityEngine.Object.Destroy(marker);
            marker = null;
        }
    }
}

public sealed class ExampleEnemyPresentation : MonoBehaviour
{
    public bool IsSimulator;
}
