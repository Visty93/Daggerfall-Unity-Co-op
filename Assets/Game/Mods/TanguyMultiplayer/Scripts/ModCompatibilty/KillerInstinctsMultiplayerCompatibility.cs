// Independently written multiplayer adapter. No Killer Instincts implementation,
// assets, or damage/movement formulas are included in this file.
// Requires the installed Killer Instincts 1.2.1 mod at runtime.
// Add to Assets/Game/Mods/TanguyMultiplayer/Scripts and rebuild the co-op game.
// Install the same build and Killer Instincts version on host and all clients.
// No prefab edits, Harmony dependency, or reference to the mod assembly required.
// v2: initialize the installed mod on Mirror client copies after authoritative
// enemy setup, retain early hit/state messages until Start completes, and refresh
// cached entity references after a network setup rebuild. Observer dust calls
// invoke the installed mod's effect method; no particle assets are included.
//
// Integration contract: uses reflection to call the installed mod's damage
// methods and to supervise its existing coroutines. A private API mismatch is
// logged explicitly. This is an adapter for the inspected 1.2.1 implementation,
// not a guarantee of compatibility with future releases or all other AI mods.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;

// Plain Mirror messages avoid adding NetworkBehaviours to spawned prefabs.
public struct KillerInstinctsAttackRequest : NetworkMessage
{
    public uint enemy;
    public uint target;
    public uint sequence;
    public byte operation; // 0 begin, 1 hit, 2 end, 3 cleave, 4 launch, 5 charge dust
    public byte maneuver; // 1 leap, 2 charge
    public bool completed;
}

public struct KillerInstinctsPlayerHit : NetworkMessage
{
    public uint enemy;
    public uint target;
    public bool cleave;
}

public struct KillerInstinctsAttackState : NetworkMessage
{
    public uint enemy;
    public bool active;
    public byte maneuver;
    public byte phase; // 0 begin, 1 launch, 2 charge dust, 3 normal landing, 4 cancel/end
}

[DefaultExecutionOrder(-20000)]
public sealed class KillerInstinctsMultiplayerCompatibility : MonoBehaviour
{
    const string Prefix = "[KillerInstinctsMP] ";
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const float MaximumRoutineSeconds = 60f;
    const float NetworkRangeTolerance = 2f;
    const float InitializationGraceSeconds = 3f;

    static KillerInstinctsMultiplayerCompatibility instance;
    readonly Dictionary<int, Actor> actors = new Dictionary<int, Actor>();
    readonly Dictionary<uint, ServerSwing> swings = new Dictionary<uint, ServerSwing>();
    readonly Dictionary<uint, float> lastCleave = new Dictionary<uint, float>();
    readonly List<int> deadActors = new List<int>();
    readonly HashSet<string> warnings = new HashSet<string>();
    readonly HashSet<string> diagnostics = new HashSet<string>();
    readonly List<PendingHit> pendingHits = new List<PendingHit>();
    readonly Dictionary<uint, PendingState> pendingStates = new Dictionary<uint, PendingState>();
    readonly List<uint> expiredStates = new List<uint>();
    Type modMotorType;
    FieldInfo routineField, attackedField, cleaveField, initializedMotorField, lastAttackField;
    FieldInfo settingsInstance, cleaveReach, cleaveAngle, cleaveExclude;
    FieldInfo cachedEntityField, cachedMobileField, cachedDfMobileField;
    MethodInfo damagePlayer, damageEnemy, cleavePlayer, cleaveEnemy, resetSkill, playDust;
    readonly Dictionary<Type, FieldInfo> hitFlags = new Dictionary<Type, FieldInfo>();
    float nextScan, nextHandlers;
    bool serverRegistered, clientRegistered, contractFailed;
    Mod subscribedCombatEvents;
    int insideDamage;
    uint nextSequence;

    sealed class Actor
    {
        public MonoBehaviour mod;
        public NetworkIdentity identity;
        public EnemyMotor motor;
        public EnemyAttack attack;
        public EnemySenses senses;
        public DaggerfallEntityBehaviour entity;
        public MobileUnit mobile;
        public EntityEffectManager effects;
        public EnemyMotor.TakeActionCallback original, wrapper;
        public bool originalEnabled, originalCleave, muted, attackWasEnabled;
        public bool remoteSpecial;
        public float remoteSince;
        public IEnumerator native;
        public Coroutine supervised;
        public uint sequence;
        public bool special, effectsWereEnabled, completed;
        public byte maneuver;
        public bool visualStarted;
        public float nextDust;
    }

    sealed class ServerSwing
    {
        public uint sequence;
        public NetworkConnection owner;
        public float started;
        public bool hit;
        public byte maneuver;
        public bool visualStarted;
        public float nextDust;
    }

    sealed class PendingHit
    {
        public KillerInstinctsPlayerHit message;
        public float received;
    }

    sealed class PendingState
    {
        public KillerInstinctsAttackState message;
        public float received;
    }

    static bool Multiplayer { get { return NetworkServer.active || NetworkClient.active; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        GameObject go = new GameObject("Killer Instincts Multiplayer Compatibility");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<KillerInstinctsMultiplayerCompatibility>();
    }

    void Update()
    {
        MaintainHandlers();
        if (!Multiplayer)
        {
            if (actors.Count != 0) RestoreAll();
            swings.Clear();
            lastCleave.Clear();
            pendingHits.Clear();
            pendingStates.Clear();
            return;
        }

        if (Time.realtimeSinceStartup >= nextScan)
        {
            nextScan = Time.realtimeSinceStartup + 0.1f;
            if (ResolveContract())
            {
                InitializeClientCopies();
                foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(modMotorType))
                    Track(found as MonoBehaviour);
                SubscribeCombatEvents();
            }
        }

        deadActors.Clear();
        foreach (KeyValuePair<int, Actor> pair in actors)
        {
            Actor a = pair.Value;
            if (a.mod == null || a.identity == null || a.motor == null)
            {
                Abort(a);
                deadActors.Add(pair.Key);
                continue;
            }
            bool simulate = Simulates(a);
            RefreshReferences(a);
            if (simulate) Info(NetworkServer.active ? "host-simulator" : "client-simulator",
                "v2 maneuver simulator ready: enemy=" + a.identity.netId + "; server=" + NetworkServer.active + ".");
            cleaveField.SetValue(a.mod, false); // Only our validated dispatcher performs cleave in MP.
            a.mod.enabled = a.originalEnabled && simulate;
            if (!simulate && a.native != null) Abort(a);
            if (a.native == null) Capture(a, simulate);
            if (a.remoteSpecial && Time.realtimeSinceStartup - a.remoteSince > MaximumRoutineSeconds)
            {
                a.remoteSpecial = false;
                Warn("phase-timeout", "Expired a missing attack-end notification; restored ordinary attacks.");
            }
            SetMuted(a, a.special || a.remoteSpecial);
        }
        foreach (int id in deadActors) actors.Remove(id);
        RetryPendingMessages();
    }

    void OnDestroy()
    {
        RestoreAll();
        if (instance == this) instance = null;
    }

    void MaintainHandlers()
    {
        bool refresh = Time.realtimeSinceStartup >= nextHandlers;
        if (refresh) nextHandlers = Time.realtimeSinceStartup + 0.5f;
        if (NetworkServer.active && (!serverRegistered || refresh))
            NetworkServer.ReplaceHandler<KillerInstinctsAttackRequest>(ReceiveRequest);
        if (NetworkClient.active && (!clientRegistered || refresh))
        {
            NetworkClient.ReplaceHandler<KillerInstinctsPlayerHit>(ReceivePlayerHit);
            NetworkClient.ReplaceHandler<KillerInstinctsAttackState>(ReceiveState);
        }
        serverRegistered = NetworkServer.active;
        clientRegistered = NetworkClient.active;
    }

    bool ResolveContract()
    {
        if (contractFailed) return false;
        if (modMotorType != null) return true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type candidate = assembly.GetType("DynamicEnemiesMod.DynamicEnemyMotor", false);
            if (candidate == null) continue;
            try
            {
                routineField = RequiredField(candidate, "attacking", typeof(IEnumerator));
                attackedField = RequiredField(candidate, "attacked", typeof(List<DaggerfallEntityBehaviour>));
                cleaveField = RequiredField(candidate, "canCleave", typeof(bool));
                initializedMotorField = RequiredField(candidate, "motor", typeof(EnemyMotor));
                lastAttackField = RequiredField(candidate, "lastAttack", typeof(float));
                cachedEntityField = RequiredField(candidate, "entity", typeof(EnemyEntity));
                cachedMobileField = RequiredField(candidate, "mobile", typeof(MobileUnit));
                cachedDfMobileField = RequiredField(candidate, "dfMobile", typeof(DaggerfallMobileUnit));
                resetSkill = RequiredMethod(candidate, "ResetSkillTimer", new[] { typeof(bool) }, typeof(void));
                playDust = RequiredMethod(candidate, "PlayDustVFX", new[] { typeof(Vector3), typeof(float), typeof(int) }, typeof(void));
                Type[] playerArgs = { typeof(DaggerfallUnityItem) };
                Type[] enemyArgs = { typeof(DaggerfallEntityBehaviour), typeof(DaggerfallUnityItem), typeof(Vector3), typeof(bool) };
                damagePlayer = RequiredMethod(candidate, "ApplyDamageToPlayer", playerArgs, typeof(int));
                cleavePlayer = RequiredMethod(candidate, "ApplyCleaveDamageToPlayer", playerArgs, typeof(int));
                damageEnemy = RequiredMethod(candidate, "ApplyDamageToNonPlayer", enemyArgs, typeof(int));
                cleaveEnemy = RequiredMethod(candidate, "ApplyCleaveDamageToNonPlayer", enemyArgs, typeof(int));
                foreach (string name in new[] { "LeapAttack", "ChargeAttack" })
                {
                    Type iterator = null;
                    foreach (Type nested in candidate.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        if (nested.Name.StartsWith("<" + name + ">", StringComparison.Ordinal)) iterator = nested;
                    FieldInfo flag = null;
                    if (iterator != null)
                        foreach (FieldInfo f in iterator.GetFields(Members))
                            if (f.FieldType == typeof(bool) && f.Name.Contains("hasAttacked")) flag = f;
                    if (flag == null) throw new MissingFieldException(name + " hit marker");
                    hitFlags[iterator] = flag;
                }
                Type settings = assembly.GetType("DynamicEnemiesMod.DynamicEnemies", true);
                settingsInstance = settings.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (settingsInstance == null) throw new MissingFieldException("DynamicEnemies.Instance");
                cleaveReach = RequiredField(settings, "cleaveReach", typeof(float));
                cleaveAngle = RequiredField(settings, "cleaveAngle", typeof(float));
                cleaveExclude = RequiredField(settings, "cleaveExcludeTarget", typeof(bool));
                modMotorType = candidate;
                Debug.Log(Prefix + "v2: Killer Instincts detected; reflection contract verified. Client initialization and damage bridge active.");
                return true;
            }
            catch (Exception e)
            {
                contractFailed = true;
                Debug.LogError(Prefix + "Unsupported Killer Instincts build. Adapter was NOT installed: " + e.Message);
                return false;
            }
        }
        return false;
    }

    static FieldInfo RequiredField(Type type, string name, Type fieldType)
    {
        FieldInfo f = type.GetField(name, Members);
        if (f == null || f.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
        return f;
    }

    static MethodInfo RequiredMethod(Type type, string name, Type[] args, Type result)
    {
        MethodInfo m = type.GetMethod(name, Members, null, args, null);
        if (m == null || m.ReturnType != result) throw new MissingMethodException(type.FullName, name);
        return m;
    }

    Actor Track(MonoBehaviour component)
    {
        if (component == null || modMotorType == null) return null;
        Actor existing;
        if (actors.TryGetValue(component.GetInstanceID(), out existing)) return existing;
        NetworkIdentity identity = component.GetComponent<NetworkIdentity>();
        if (!ClientCopyReady(identity)) return null;
        EnemyMotor motor = initializedMotorField.GetValue(component) as EnemyMotor;
        if (identity == null || identity.netId == 0 || motor == null || motor.TakeActionHandler == null)
            return null; // Wait for both the mod's Start() and network spawn.
        if (component.GetComponentInParent<PlayerMultiplayer>() != null) return null;
        DaggerfallEntityBehaviour entity = component.GetComponent<DaggerfallEntityBehaviour>();
        if (entity == null || !(entity.Entity is EnemyEntity)) return null;
        EnemyAttack attack = component.GetComponent<EnemyAttack>();
        EnemySenses senses = component.GetComponent<EnemySenses>();
        if (attack == null || senses == null) return null;

        Actor a = new Actor
        {
            mod = component, identity = identity, motor = motor, attack = attack,
            senses = senses, entity = entity, mobile = component.GetComponentInChildren<MobileUnit>(),
            effects = component.GetComponent<EntityEffectManager>(),
            originalEnabled = component.enabled, originalCleave = (bool)cleaveField.GetValue(component),
            original = motor.TakeActionHandler
        };
        a.wrapper = delegate
        {
            if (!Multiplayer) { a.original(); return; }
            if (!Simulates(a)) return;
            try { a.original(); }
            catch (Exception e)
            {
                Capture(a, false);
                Warn("action:" + e.GetType().Name, "Mod action interrupted: " + e.Message);
            }
            Capture(a, true);
        };
        actors.Add(component.GetInstanceID(), a);
        cleaveField.SetValue(component, false);
        motor.TakeActionHandler = a.wrapper;
        RefreshReferences(a);
        PendingState pending;
        if (pendingStates.TryGetValue(identity.netId, out pending))
        {
            pendingStates.Remove(identity.netId);
            if (Time.realtimeSinceStartup - pending.received <= InitializationGraceSeconds)
                ApplyState(a, pending.message, pending.received);
        }
        Info("attached", "Attached to networked enemy " + identity.netId + ". Native damage calls are bridged in multiplayer.");
        return a;
    }

    bool ClientCopyReady(NetworkIdentity identity)
    {
        if (identity == null || identity.netId == 0 || !identity.gameObject.activeInHierarchy) return false;
        if (NetworkServer.active) return true;
        if (!NetworkClient.active) return false;
        SetupDemoEnemy setup = identity.GetComponent<SetupDemoEnemy>();
        // The visual fallback can still be replaced by the full settings RPC.
        // Wait for that RPC before the mod caches its EnemyEntity in Start().
        return setup != null && setup.HasReceivedInitialServerSettings() && setup.IsClientEnemyVisualReadyForAnimation();
    }

    void InitializeClientCopies()
    {
        if (NetworkServer.active || !NetworkClient.active) return;
        foreach (NetworkIdentity identity in new List<NetworkIdentity>(NetworkClient.spawned.Values))
            EnsureClientMod(identity);
    }

    MonoBehaviour EnsureClientMod(NetworkIdentity identity)
    {
        if (identity == null || modMotorType == null) return null;
        MonoBehaviour existing = identity.GetComponent(modMotorType) as MonoBehaviour;
        if (existing != null || NetworkServer.active) return existing;
        if (!ClientCopyReady(identity) || settingsInstance.GetValue(null) == null || GameManager.Instance == null) return null;
        if (identity.GetComponentInParent<PlayerMultiplayer>() != null) return null;
        DaggerfallEntityBehaviour body = identity.GetComponent<DaggerfallEntityBehaviour>();
        EnemyMotor motor = identity.GetComponent<EnemyMotor>();
        MobileUnit mobile = identity.GetComponentInChildren<MobileUnit>();
        if (!Alive(body) || !(body.Entity is EnemyEntity) || motor == null || motor.TakeActionHandler == null ||
            mobile == null || !mobile.IsSetup || identity.GetComponent<EnemyAttack>() == null ||
            identity.GetComponent<EnemySenses>() == null || identity.GetComponent<EnemySounds>() == null ||
            identity.GetComponent<CharacterController>() == null || identity.GetComponent<EntityEffectManager>() == null ||
            identity.GetComponent<DaggerfallAudioSource>() == null) return null;

        // Mirror does not serialize components added by the host's spawn event.
        // Add ONLY this installed mod's component; do not replay OnEnemySpawn for
        // every mod and do not manually call Start (which would double subscribe).
        MonoBehaviour added = identity.gameObject.AddComponent(modMotorType) as MonoBehaviour;
        Info("client-component-added", "v2 added installed Killer Instincts component to client enemy=" + identity.netId + "; waiting for its Start().");
        return added;
    }

    void RefreshReferences(Actor a)
    {
        EnemyEntity current = a.entity != null ? a.entity.Entity as EnemyEntity : null;
        if (current == null) return;
        DaggerfallEnemy enemy = a.mod.GetComponent<DaggerfallEnemy>();
        MobileUnit visual = enemy != null ? enemy.MobileUnit : a.mod.GetComponentInChildren<MobileUnit>();
        bool entityChanged = !ReferenceEquals(cachedEntityField.GetValue(a.mod), current);
        bool visualChanged = visual != null && !ReferenceEquals(cachedMobileField.GetValue(a.mod), visual);
        if (!entityChanged && !visualChanged) return;
        if (a.native != null) Abort(a);
        cachedEntityField.SetValue(a.mod, current);
        if (visual != null)
        {
            cachedMobileField.SetValue(a.mod, visual);
            cachedDfMobileField.SetValue(a.mod, visual as DaggerfallMobileUnit);
            a.mobile = visual;
        }
        Info("cache-rebound", "v2 refreshed Killer Instincts entity/visual cache after network setup.");
    }

    bool Simulates(Actor a)
    {
        if (a.identity == null) return false;
        if (NetworkServer.active)
            return a.identity.connectionToClient == null || a.identity.connectionToClient == NetworkServer.localConnection;
        return a.motor.hasAuthority;
    }

    void Capture(Actor a, bool allow)
    {
        if (a.mod == null || a.native != null) return;
        IEnumerator native = routineField.GetValue(a.mod) as IEnumerator;
        if (native == null) return;
        // StartCoroutine has already executed the initial step. Preserve Current
        // when transferring ownership, otherwise the mod's initial wait is lost.
        a.mod.StopCoroutine(native);
        a.native = native;
        a.completed = false;
        a.effectsWereEnabled = a.effects == null || a.effects.enabled;
        // Spell routines disable their effect manager in their initial step.
        // These are not damage-bridged; restore that component if interrupted.
        if (native.GetType().Name.StartsWith("<Spell", StringComparison.Ordinal)) a.effectsWereEnabled = true;
        a.special = hitFlags.ContainsKey(native.GetType());
        a.maneuver = a.special ? (byte)(native.GetType().Name.StartsWith("<LeapAttack>", StringComparison.Ordinal) ? 1 : 2) : (byte)0;
        a.visualStarted = false;
        a.nextDust = 0f;
        if (!allow || !Alive(a.entity)) { Finish(a); return; }
        if (a.special)
        {
            a.sequence = ++nextSequence;
            SetMuted(a, true);
            Info("maneuver", "Supervising native " + native.GetType().Name + " on enemy " + a.identity.netId + ".");
            Request(a, 0, 0);
        }
        a.supervised = StartCoroutine(Supervise(a, native));
    }

    IEnumerator Supervise(Actor a, IEnumerator native)
    {
        float began = Time.realtimeSinceStartup;
        try
        {
            yield return native.Current;
            while (a.mod != null && a.mod.gameObject.activeInHierarchy && Multiplayer && Simulates(a) && Alive(a.entity))
            {
                if (Time.realtimeSinceStartup - began > MaximumRoutineSeconds)
                {
                    Warn("routine-timeout", "Cancelled an overlong mod maneuver and restored movement.");
                    break;
                }
                object wait;
                if (!Advance(a, native, out wait)) break;
                yield return wait;
            }
        }
        finally { Finish(a); }
    }

    bool Advance(Actor a, IEnumerator native, out object wait)
    {
        wait = null;
        List<DaggerfallEntityBehaviour> attacked = null;
        DaggerfallEntityBehaviour target = a.senses.Target;
        FieldInfo flag;
        bool special = hitFlags.TryGetValue(native.GetType(), out flag);
        bool before = special && (bool)flag.GetValue(native);
        bool inserted = false;
        bool result;
        Vector3 previousPosition = a.mod.transform.position;
        try
        {
            if (special)
            {
                attacked = (List<DaggerfallEntityBehaviour>)attackedField.GetValue(a.mod);
                // Suppress only the mod's direct hit for this one synchronous
                // MoveNext. Never replace global senses.Target or a player's entity.
                // The native iterator still decides whether contact occurred.
                if (target != null && !attacked.Contains(target))
                {
                    attacked.Add(target);
                    inserted = true;
                }
            }
            result = native.MoveNext();
            a.completed = !result;
            if (result) wait = native.Current;
        }
        catch (Exception e)
        {
            Warn("iterator:" + e.GetType().Name, "Mod coroutine interrupted; restoring movement: " + e.Message);
            return false;
        }
        finally
        {
            if (inserted) attacked.Remove(target);
        }

        if (special)
        {
            bool moved = (a.mod.transform.position - previousPosition).sqrMagnitude > 0.000001f;
            if (!a.visualStarted && (moved || !result))
            {
                a.visualStarted = true;
                Request(a, 4, 0);
            }
            if (a.maneuver == 2 && moved && result && Time.realtimeSinceStartup >= a.nextDust)
            {
                a.nextDust = Time.realtimeSinceStartup + 0.12f;
                Request(a, 5, 0);
            }
        }

        // Check even when MoveNext returned false: a contact can finish the
        // coroutine in the very same step.
        if (special && !before && (bool)flag.GetValue(native) && inserted && target != null &&
            a.senses.DistanceToTarget <= a.attack.MeleeDistance * 2f)
        {
            attacked.Add(target);
            lastAttackField.SetValue(a.mod, Time.time);
            NetworkIdentity victim = TargetIdentity(target);
            if (victim != null) Request(a, 1, victim.netId);
            else if (NetworkServer.active)
            {
                InvokeEnemyDamage(a, target, false);
                DispatchCleave(a, target, true);
            }
        }
        return result;
    }

    void Abort(Actor a)
    {
        Coroutine running = a.supervised;
        a.supervised = null;
        if (running != null) StopCoroutine(running);
        if (a.native != null) Finish(a);
    }

    void Finish(Actor a)
    {
        if (a.native == null) return;
        IEnumerator native = a.native;
        a.native = null;
        a.supervised = null;
        if (a.special && Multiplayer && a.identity != null) Request(a, 2, 0);
        a.special = false;
        if (a.mod != null)
        {
            if (ReferenceEquals(routineField.GetValue(a.mod), native)) routineField.SetValue(a.mod, null);
            if (!a.completed)
            {
                try { resetSkill.Invoke(a.mod, new object[] { false }); }
                catch (Exception e) { Warn("reset", "Could not reset mod cooldown: " + e.Message); }
            }
        }
        if (a.mobile != null) a.mobile.FreezeAnims = false;
        if (a.effects != null && a.effectsWereEnabled) a.effects.enabled = true;
        if (a.motor != null && Alive(a.entity)) a.motor.enabled = true;
        if (a.attack != null && !a.completed) a.attack.ResetMeleeTimer();
        SetMuted(a, a.remoteSpecial && Multiplayer);
        IDisposable disposable = native as IDisposable;
        if (disposable != null)
        {
            try { disposable.Dispose(); }
            catch (Exception e) { Warn("dispose", "Mod iterator cleanup failed: " + e.Message); }
        }
    }

    void SetMuted(Actor a, bool mute)
    {
        if (a.attack == null || a.muted == mute) return;
        if (mute)
        {
            a.attackWasEnabled = a.attack.enabled;
            a.attack.enabled = false;
        }
        else
        {
            // Do not replay a stale ordinary melee frame after a special hit.
            if (a.mobile != null) a.mobile.DoMeleeDamage = false;
            a.attack.enabled = a.attackWasEnabled;
        }
        a.muted = mute;
    }

    void RestoreAll()
    {
        foreach (Actor a in actors.Values)
        {
            a.remoteSpecial = false;
            Abort(a);
            SetMuted(a, false);
            if (a.mod != null)
            {
                cleaveField.SetValue(a.mod, a.originalCleave);
                a.mod.enabled = a.originalEnabled;
            }
            if (a.motor != null && a.motor.TakeActionHandler == a.wrapper)
                a.motor.TakeActionHandler = a.original;
        }
        actors.Clear();
    }

    static bool Alive(DaggerfallEntityBehaviour entity)
    {
        return entity != null && entity.Entity != null && entity.Entity.CurrentHealth > 0;
    }

    static bool PlayerAlive(PlayerMultiplayer player)
    {
        return player != null && player.LifeState == PlayerMultiplayer.MultiplayerLifeState.Alive &&
            !player.IsDownedForRevive;
    }

    static PlayerMultiplayer PlayerFor(DaggerfallEntityBehaviour target)
    {
        if (target == null) return null;
        PlayerMultiplayer player = target.GetComponentInParent<PlayerMultiplayer>();
        if (player == null && GameManager.Instance != null && target == GameManager.Instance.PlayerEntityBehaviour &&
            NetworkClient.localPlayer != null)
            player = NetworkClient.localPlayer.GetComponent<PlayerMultiplayer>();
        return player;
    }

    static NetworkIdentity TargetIdentity(DaggerfallEntityBehaviour target)
    {
        if (target == null) return null;
        PlayerMultiplayer player = PlayerFor(target);
        return player != null ? player.GetComponent<NetworkIdentity>() : target.GetComponent<NetworkIdentity>();
    }

    Actor FindActor(uint id, bool server)
    {
        if (!ResolveContract()) return null;
        NetworkIdentity identity;
        if (!(server ? NetworkServer.spawned : NetworkClient.spawned).TryGetValue(id, out identity) || identity == null)
            return null;
        return Track(EnsureClientMod(identity));
    }

    void Request(Actor a, byte operation, uint target)
    {
        KillerInstinctsAttackRequest message = new KillerInstinctsAttackRequest
        {
            enemy = a.identity.netId, target = target, sequence = a.sequence, operation = operation,
            maneuver = a.maneuver, completed = a.completed
        };
        if (NetworkServer.active) ReceiveRequest(null, message);
        else if (NetworkClient.isConnected) NetworkClient.Send(message);
    }

    void ReceiveRequest(NetworkConnection sender, KillerInstinctsAttackRequest message)
    {
        Actor a = FindActor(message.enemy, true);
        if (a == null || !Alive(a.entity) || message.operation > 5) return;
        NetworkConnection owner = a.identity.connectionToClient;
        bool authority = sender == null ? Simulates(a) : owner == sender;

        if (message.operation == 0)
        {
            if (!authority || message.maneuver < 1 || message.maneuver > 2) return;
            ServerSwing old;
            if (swings.TryGetValue(message.enemy, out old) && Time.realtimeSinceStartup - old.started < MaximumRoutineSeconds)
                return;
            swings[message.enemy] = new ServerSwing { sequence = message.sequence, owner = owner,
                started = Time.realtimeSinceStartup, maneuver = message.maneuver };
            BroadcastState(a, true, message.maneuver, 0);
            return;
        }
        if (message.operation == 2)
        {
            ServerSwing old;
            if (authority && swings.TryGetValue(message.enemy, out old) && old.sequence == message.sequence && old.owner == owner)
            {
                swings.Remove(message.enemy);
                BroadcastState(a, false, old.maneuver, message.completed && old.visualStarted ? (byte)3 : (byte)4);
            }
            return;
        }

        if (message.operation == 4 || message.operation == 5)
        {
            ServerSwing visual;
            if (!authority || !swings.TryGetValue(message.enemy, out visual) || visual.sequence != message.sequence ||
                visual.owner != owner || Time.realtimeSinceStartup - visual.started > MaximumRoutineSeconds) return;
            if (message.operation == 4)
            {
                if (visual.visualStarted) return;
                visual.visualStarted = true;
                BroadcastState(a, true, visual.maneuver, 1);
            }
            else if (visual.visualStarted && visual.maneuver == 2 && Time.realtimeSinceStartup >= visual.nextDust)
            {
                visual.nextDust = Time.realtimeSinceStartup + 0.08f;
                BroadcastState(a, true, visual.maneuver, 2);
            }
            return;
        }

        NetworkIdentity victim;
        if (!NetworkServer.spawned.TryGetValue(message.target, out victim) || victim == null || victim == a.identity) return;
        DaggerfallEntityBehaviour target = victim.GetComponent<DaggerfallEntityBehaviour>();
        PlayerMultiplayer player = victim.GetComponent<PlayerMultiplayer>();
        if (player != null ? !PlayerAlive(player) : !Alive(target)) return;
        float distance = Vector3.Distance(a.mod.transform.position, victim.transform.position);
        if (distance > a.attack.MeleeDistance * 2f + NetworkRangeTolerance) return;

        if (message.operation == 3)
        {
            // Ordinary melee is calculated on the victim's client in this fork.
            // Permit only the simulator or that specific victim to report cleave.
            bool victimReporting = sender != null && victim.connectionToClient == sender && player != null;
            bool hostVictim = sender == null && player != null && player.isLocalPlayer;
            if (authority || victimReporting || hostVictim) DispatchCleave(a, target, false);
            return;
        }

        ServerSwing swing;
        if (!authority || !swings.TryGetValue(message.enemy, out swing) || swing.sequence != message.sequence ||
            swing.owner != owner || swing.hit || Time.realtimeSinceStartup - swing.started > MaximumRoutineSeconds) return;
        swing.hit = true;
        Info("forwarded", "Validated maneuver hit: enemy=" + message.enemy + " target=" + message.target + ".");
        if (player != null) SendPlayerHit(a, player, false);
        else InvokeEnemyDamage(a, target, false);
        DispatchCleave(a, target, true);
    }

    void BroadcastState(Actor a, bool active, byte maneuver, byte phase)
    {
        KillerInstinctsAttackState message = new KillerInstinctsAttackState
        {
            enemy = a.identity.netId, active = active, maneuver = maneuver, phase = phase
        };
        // Also update a dedicated server or the host's observer copy immediately.
        ApplyState(a, message, Time.realtimeSinceStartup);
        NetworkServer.SendToAll(message);
    }

    void ReceiveState(KillerInstinctsAttackState message)
    {
        if (NetworkServer.active) return; // Host already applied the broadcast locally.
        Actor a = FindActor(message.enemy, false);
        if (a == null)
        {
            pendingStates[message.enemy] = new PendingState { message = message, received = Time.realtimeSinceStartup };
            return;
        }
        ApplyState(a, message, Time.realtimeSinceStartup);
    }

    void ApplyState(Actor a, KillerInstinctsAttackState message, float received)
    {
        bool starting = message.active && !a.remoteSpecial;
        a.remoteSpecial = message.active;
        a.remoteSince = received;
        SetMuted(a, a.special || message.active);
        if (Simulates(a) || a.mobile == null) return; // The simulator already runs the native effects.
        if (starting) a.mobile.ChangeEnemyState(MobileStates.PrimaryAttack);
        bool dust = (message.maneuver == 1 && (message.phase == 1 || message.phase == 3)) ||
            (message.maneuver == 2 && message.phase == 2);
        if (!dust) return;
        if ((message.phase == 3 || message.maneuver == 2) &&
            (a.motor.IsLevitating || a.mobile.Enemy.Behaviour == MobileBehaviour.Flying ||
             a.mobile.Enemy.Behaviour == MobileBehaviour.Spectral || a.mobile.Enemy.Behaviour == MobileBehaviour.Aquatic)) return;
        CharacterController controller = a.mod.GetComponent<CharacterController>();
        Vector3 point = a.mod.transform.position;
        if (controller != null) point.y -= controller.height * 0.5f;
        try
        {
            playDust.Invoke(a.mod, new object[] { point, message.maneuver == 1 ? 2f : 1f, message.maneuver == 1 ? 10 : 15 });
            Info("observer-dust", "v2 replayed maneuver dust through the installed mod on an observer.");
        }
        catch (Exception e) { Warn("observer-dust-failed", "Could not replay native dust: " + e.Message); }
    }

    void SendPlayerHit(Actor a, PlayerMultiplayer target, bool cleave)
    {
        if (!PlayerAlive(target)) return;
        KillerInstinctsPlayerHit message = new KillerInstinctsPlayerHit
        {
            enemy = a.identity.netId, target = target.netId, cleave = cleave
        };
        if (target.isLocalPlayer) ReceivePlayerHit(message);
        else if (target.connectionToClient != null) target.connectionToClient.Send(message);
    }

    void ReceivePlayerHit(KillerInstinctsPlayerHit message)
    {
        Info("hit-received", "v2 received player hit: enemy=" + message.enemy + "; target=" + message.target + ".");
        if (pendingHits.Count == 0 && TryApplyPlayerHit(message)) return;
        pendingHits.Add(new PendingHit { message = message, received = Time.realtimeSinceStartup });
        Info("hit-queued", "v2 retained an early hit while the client enemy/mod finishes initialization.");
    }

    // true means consumed or deliberately rejected; false means startup is pending.
    bool TryApplyPlayerHit(KillerInstinctsPlayerHit message)
    {
        if (NetworkClient.localPlayer == null) return false;
        if (NetworkClient.localPlayer.netId != message.target) return true;
        PlayerMultiplayer player = NetworkClient.localPlayer.GetComponent<PlayerMultiplayer>();
        if (GameManager.Instance == null || GameManager.Instance.PlayerEntityBehaviour == null) return false;
        if (!PlayerAlive(player) || !Alive(GameManager.Instance.PlayerEntityBehaviour)) return true;
        Actor a = FindActor(message.enemy, NetworkServer.active);
        if (a == null) return false;
        RefreshReferences(a);
        InvokeDamage(a, message.cleave ? cleavePlayer : damagePlayer, new object[] { Weapon(a, null) });
        return true;
    }

    void RetryPendingMessages()
    {
        // Keep receipt order: an older hit must not be applied after a newer one
        // has already killed/downed the player.
        int index = 0;
        while (index < pendingHits.Count)
        {
            PendingHit pending = pendingHits[index];
            if (Time.realtimeSinceStartup - pending.received > InitializationGraceSeconds)
            {
                Warn("hit-initialization-timeout", "v2 hit initialization timed out: enemy=" + pending.message.enemy + "; target=" + pending.message.target + ". Please include this client's Player.log.");
                pendingHits.RemoveAt(index);
            }
            else if (TryApplyPlayerHit(pending.message)) pendingHits.RemoveAt(index);
            else break;
        }
        expiredStates.Clear();
        foreach (KeyValuePair<uint, PendingState> entry in pendingStates)
            if (Time.realtimeSinceStartup - entry.Value.received > InitializationGraceSeconds) expiredStates.Add(entry.Key);
        foreach (uint id in expiredStates) pendingStates.Remove(id);
    }

    DaggerfallUnityItem Weapon(Actor a, DaggerfallEntityBehaviour target)
    {
        EnemyEntity attacker = a.entity.Entity as EnemyEntity;
        if (attacker == null) return null;
        DaggerfallUnityItem weapon = attacker.ItemEquipTable.GetItem(EquipSlots.RightHand);
        EnemyEntity enemy = target != null ? target.Entity as EnemyEntity : null;
        if (weapon != null && enemy != null && enemy.MobileEnemy.MinMetalToHit > (WeaponMaterialTypes)weapon.NativeMaterialValue)
            return null;
        return weapon;
    }

    void InvokeEnemyDamage(Actor a, DaggerfallEntityBehaviour target, bool cleave)
    {
        if (!NetworkServer.active || !Alive(target) || !(target.Entity is EnemyEntity) || PlayerFor(target) != null) return;
        // The native NPC routine assumes these components. Never pass a proxy or
        // an incomplete/despawning entity to it.
        if (target.GetComponent<EnemySounds>() == null || target.GetComponent<CharacterController>() == null) return;
        InvokeDamage(a, cleave ? cleaveEnemy : damageEnemy,
            new object[] { target, Weapon(a, target), a.mod.transform.forward, false });
    }

    void InvokeDamage(Actor a, MethodInfo method, object[] arguments)
    {
        insideDamage++;
        try
        {
            object result = method.Invoke(a.mod, arguments);
            Info("damage:" + method.Name, "Executed native " + method.Name + " for enemy " + a.identity.netId + "; damage=" + result + ".");
        }
        catch (Exception e)
        {
            Exception cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            Warn("damage:" + method.Name + ":" + cause.GetType().Name, "Native " + method.Name + " failed: " + cause.Message);
        }
        finally { insideDamage--; }
    }

    void SubscribeCombatEvents()
    {
        if (ModManager.Instance == null) return;
        Mod events = ModManager.Instance.GetModFromGUID("fb086c76-38e7-4d83-91dc-f29e6f1bb17e");
        if (events == null || ReferenceEquals(events, subscribedCombatEvents)) return;
        ModManager.Instance.SendModMessage(events.Title, "onAttackDamageCalculated",
            (Action<DaggerfallEntity, DaggerfallEntity, DaggerfallUnityItem, int, int>)OnCombatDamage);
        subscribedCombatEvents = events;
    }

    void OnCombatDamage(DaggerfallEntity attacker, DaggerfallEntity target, DaggerfallUnityItem weapon, int bodyPart, int damage)
    {
        if (!Multiplayer || insideDamage != 0 || attacker == null || target == null || modMotorType == null) return;
        DaggerfallEntityBehaviour body = attacker.EntityBehaviour;
        if (body == null) return;
        Actor a = Track(body.GetComponent(modMotorType) as MonoBehaviour);
        if (a == null || !a.originalCleave || a.special || a.remoteSpecial) return;
        NetworkIdentity victim = TargetIdentity(target.EntityBehaviour);
        if (victim == null)
        {
            if (NetworkServer.active && Simulates(a)) DispatchCleave(a, target.EntityBehaviour, false);
            return;
        }
        PlayerMultiplayer player = PlayerFor(target.EntityBehaviour);
        if (Simulates(a) || (player != null && player.isLocalPlayer)) Request(a, 3, victim.netId);
    }

    void DispatchCleave(Actor a, DaggerfallEntityBehaviour primary, bool special)
    {
        if (!NetworkServer.active || !a.originalCleave || !Alive(a.entity)) return;
        object settings = settingsInstance.GetValue(null);
        if (settings == null) return;
        float last;
        if (lastCleave.TryGetValue(a.identity.netId, out last) && Time.time - last <= 0.1f) return;
        lastCleave[a.identity.netId] = Time.time;
        float range = a.attack.MeleeDistance * (float)cleaveReach.GetValue(settings);
        float angle = (float)cleaveAngle.GetValue(settings);
        bool exclude = special || (bool)cleaveExclude.GetValue(settings);
        PlayerMultiplayer primaryPlayer = PlayerFor(primary);
        List<DaggerfallEntityBehaviour> candidates = new List<DaggerfallEntityBehaviour>(ActiveGameObjectDatabase.GetActiveEnemyBehaviours());
        if (a.motor.IsHostile)
        {
            // Enumerate players once by their network root, never their enemy-like visuals.
            foreach (NetworkIdentity identity in new List<NetworkIdentity>(NetworkServer.spawned.Values))
            {
                if (identity == null) continue;
                PlayerMultiplayer player = identity.GetComponent<PlayerMultiplayer>();
                if (!PlayerAlive(player) || (exclude && player == primaryPlayer)) continue;
                if (InCleave(a, player.transform.position, range, angle)) SendPlayerHit(a, player, true);
            }
        }
        foreach (DaggerfallEntityBehaviour enemy in candidates)
        {
            if (!Alive(enemy) || enemy == a.entity || PlayerFor(enemy) != null || (exclude && enemy == primary)) continue;
            EnemyEntity entity = enemy.Entity as EnemyEntity;
            if (entity == null || entity.Team == a.entity.Entity.Team) continue;
            EnemyMotor other = enemy.GetComponent<EnemyMotor>();
            if (!a.motor.IsHostile && (other == null || !other.IsHostile)) continue;
            if (InCleave(a, enemy.transform.position, range, angle)) InvokeEnemyDamage(a, enemy, true);
        }
    }

    static bool InCleave(Actor a, Vector3 position, float range, float angle)
    {
        return Vector3.Distance(a.mod.transform.position, position) <= range &&
            a.senses.TargetIsWithinYawAngle(angle, position);
    }

    void Warn(string key, string message)
    {
        if (warnings.Add(key)) Debug.LogWarning(Prefix + message);
    }

    void Info(string key, string message)
    {
        if (diagnostics.Add(key)) Debug.Log(Prefix + message);
    }
}
