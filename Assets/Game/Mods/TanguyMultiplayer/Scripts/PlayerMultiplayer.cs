using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Utility;
using System.Reflection;
using DaggerfallConnect;
using DaggerfallConnect.Utility;
using DaggerfallConnect.Arena2;
using System.Linq;
using System; // (for completeness; not strictly required if you don't use Exception)
using DaggerfallWorkshop.Game.Questing;  // QuestMachine, Quest, Foe, Symbol
using DaggerfallWorkshop.Game.MagicAndEffects;

public class PlayerMultiplayer : NetworkBehaviour
{

    // ─────────────────────────────────────────────────────────────────────────────
    // Quest foe spawn queue (server-side)
    // If a client requests quest-wave spawns but server doesn't have that quest instance yet,
    // we request the quest StartPacket from the requester and delay the spawn until quest exists.
    // ─────────────────────────────────────────────────────────────────────────────
    private struct QueuedQuestFoeSpawn
    {
        public Vector3[] positions;
        public MobileTypes foeType;
        public int spawnCount;
        public MobileReactions reaction;
        public bool alliedToPlayer;
        public int requesterLevel;
        public ulong questUID;
        public string foeSymbolName;
        public bool isInteriorAtRequest;
    }

    private struct QueuedSingleQuestFoeSpawn
    {
        public Vector3 worldPosition;
        public ulong questUID;
        public string foeSymbolOriginal;
        public MobileTypes foeType;
        public int mobileGenderInt;
        public int siteTypeInt;
        public bool isInteriorAtRequest;
        public MobileReactions reaction;
    }
    private readonly Dictionary<ulong, List<QueuedQuestFoeSpawn>> _pendingQuestFoeSpawns = new Dictionary<ulong, List<QueuedQuestFoeSpawn>>();
    private readonly Dictionary<ulong, List<QueuedSingleQuestFoeSpawn>> _pendingSingleQuestFoeSpawns = new Dictionary<ulong, List<QueuedSingleQuestFoeSpawn>>();
    private readonly HashSet<ulong> _pendingQuestRequests = new HashSet<ulong>();

	[Header("References")]
	public GameObject[] toEnable;
	public NetworkBehaviour[] toDisable;
	public Sprite8dir sprite8dir;
	public string[] messages;
	public DaggerfallDungeon.DungeonNetworkData dungeonData;
	
	public static GameObject playerObject;
	
	public static PlayerMultiplayer localPlayer;


    // Multiplayer-safe local player lookup.
    // Do NOT use FindObjectOfType<PlayerMultiplayer>() for Commands. In a multi-client
    // session a client has one local PlayerMultiplayer and several remote clones, and
    // FindObjectOfType can return the last/first remote clone. Commands called on that
    // wrong clone are rejected by Mirror because this client has no authority over it.
    public static PlayerMultiplayer GetLocalPlayer()
    {
        if (localPlayer != null && localPlayer.isLocalPlayer && localPlayer.isActiveAndEnabled)
            return localPlayer;

        try
        {
            if (NetworkClient.active && NetworkClient.localPlayer != null)
            {
                PlayerMultiplayer pm = NetworkClient.localPlayer.GetComponent<PlayerMultiplayer>();
                if (pm != null && pm.isLocalPlayer)
                {
                    localPlayer = pm;
                    return pm;
                }
            }
        }
        catch { }

        PlayerMultiplayer[] players = UnityEngine.Object.FindObjectsOfType<PlayerMultiplayer>();
        for (int i = 0; i < players.Length; i++)
        {
            PlayerMultiplayer pm = players[i];
            if (pm != null && pm.isLocalPlayer)
            {
                localPlayer = pm;
                return pm;
            }
        }

        return null;
    }

    public static PlayerMultiplayer GetLocalPlayerForCommand(string reason)
    {
        PlayerMultiplayer pm = GetLocalPlayer();
        if (pm == null)
        {
            Debug.LogWarning("[PlayerMultiplayer] No local PlayerMultiplayer command owner found for " + reason);
            return null;
        }

        if (!pm.isLocalPlayer)
        {
            Debug.LogWarning("[PlayerMultiplayer] Refusing to use non-local PlayerMultiplayer for command " + reason + " netId=" + pm.netId);
            return null;
        }

        return pm;
    }

    public static PlayerMultiplayer GetAnyPlayerForCommandFallback(string reason)
    {
        // Cosmetic Commands below use requiresAuthority=false and the server derives the
        // real source player from the sender connection. This fallback lets Client2/late
        // clients still send cosmetic reports even if the static localPlayer cache is stale.
        PlayerMultiplayer pm = GetLocalPlayer();
        if (pm != null)
            return pm;

        PlayerMultiplayer[] players = UnityEngine.Object.FindObjectsOfType<PlayerMultiplayer>();
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].isActiveAndEnabled)
                return players[i];
        }

        Debug.LogWarning("[PlayerMultiplayer] No PlayerMultiplayer fallback object found for cosmetic command " + reason);
        return null;
    }

    public static uint GetLocalPlayerNetIdSafe()
    {
        PlayerMultiplayer pm = GetLocalPlayer();
        if (pm != null)
            return pm.netId;

        try
        {
            if (NetworkClient.active && NetworkClient.localPlayer != null)
                return NetworkClient.localPlayer.netId;
        }
        catch { }

        try
        {
            if (NetworkServer.active && NetworkServer.localConnection != null &&
                NetworkServer.localConnection.identity != null)
                return NetworkServer.localConnection.identity.netId;
        }
        catch { }

        return 0U;
    }

	
	public static string id;
	public static int state = 0;
	public static int random = 0;
	static float randomTime = 0;
static PlayerMultiplayer serverGuardOverflowWatchdogOwner = null;
	
[SyncVar] public int syncedActiveGuards = 0;

// Network mirror of the real local PlayerAdvanced / PlayerEntity health.
// This is the canonical MP player health for UI/healthbar compatibility.
// Do not use the child fake enemy shell as the source of truth.
[SyncVar(hook = nameof(OnPlayerMPCurrentHealthChanged))] public int PlayerMPCurrentHealth = 0;
[SyncVar] public int PlayerMPMaxHealth = 0;
[SyncVar] public int PlayerMPCurrentMagicka = 0;
[SyncVar] public int PlayerMPMaxMagicka = 0;
[SyncVar] public int PlayerMPCurrentFatigue = 0;
[SyncVar] public int PlayerMPMaxFatigue = 0;
[SyncVar] public int PlayerMPLevel = 1;

// Network mirror of the real local PlayerAdvanced / PlayerEntity stealth and
// magical concealment state. EnemySenses targets the PlayerMultiplayer proxy in
// MP, so the proxy must expose the same values enemies would read from the real
// local player in SP.
[SyncVar] public int PlayerMPStealthSkill = 0;
[SyncVar] public bool PlayerMPIsStealthModeActive = false;
[SyncVar(hook = nameof(OnPlayerMPConcealmentFlagsChanged))]
public int PlayerMPMagicalConcealmentFlags = 0;

public enum MultiplayerLifeState
{
    Alive = 0,
    Downed = 1,
    Respawning = 2,
}

[SyncVar(hook = nameof(OnMultiplayerLifeStateChanged))]
public MultiplayerLifeState LifeState = MultiplayerLifeState.Alive;

[SyncVar] public float LifeStateServerTime = 0f;

public const float ReviveInteractDistance = 3.25f;

// Raycast can be slightly longer than the actual interaction distance so the player gets
// a proper "too far away" message instead of the click being swallowed by normal activation.
public const float ReviveRaycastDistance = 6.0f;
public const int DefaultReviveHealthPercent = 30;

private SpriteMultiplayer cachedLifeStateSprite;
private GameObject cachedDownedCorpseVisualObject;
private bool lifeStateVisualInitialized = false;
private bool lastAppliedDownedVisual = false;
private float nextDownedCorpseVisibilityCheckTime = 0f;
private const float DOWNED_CORPSE_VISIBILITY_CHECK_INTERVAL = 0.10f;
private const string DOWNED_CORPSE_VISUAL_OBJECT_NAME = "MP Downed Corpse Visual";

private void OnPlayerMPCurrentHealthChanged(int oldValue, int newValue)
{
    // Existing health synchronization doubles as the cosmetic hurt trigger.
    // SpriteMultiplayer itself ignores this unless the remote player is currently
    // displayed as a transformed werewolf/wereboar, so normal player visuals are unchanged.
    if (isLocalPlayer || oldValue <= 0 || newValue <= 0 || newValue >= oldValue)
        return;

    SpriteMultiplayer spriteMultiplayer = GetLifeStateSpriteMultiplayer();
    if (spriteMultiplayer != null)
        spriteMultiplayer.playHurt();
}

public bool IsDownedForRevive
{
    get
    {
        if (LifeState == MultiplayerLifeState.Downed)
            return true;

        // Fallback for one-frame/order problems where health reached zero before the
        // explicit Downed SyncVar arrives on another client. The server validates this too.
        return PlayerMPMaxHealth > 0 && PlayerMPCurrentHealth <= 0 && LifeState != MultiplayerLifeState.Respawning;
    }
}

public float PlayerMPHealthPercent
{
    get
    {
        if (PlayerMPMaxHealth <= 0)
            return 0f;

        return Mathf.Clamp01(PlayerMPCurrentHealth / (float)PlayerMPMaxHealth);
    }
}

public float PlayerMPMagickaPercent
{
    get
    {
        if (PlayerMPMaxMagicka <= 0)
            return 0f;

        return Mathf.Clamp01(PlayerMPCurrentMagicka / (float)PlayerMPMaxMagicka);
    }
}

public float PlayerMPFatiguePercent
{
    get
    {
        if (PlayerMPMaxFatigue <= 0)
            return 0f;

        return Mathf.Clamp01(PlayerMPCurrentFatigue / (float)PlayerMPMaxFatigue);
    }
}

private int lastSentPlayerMPCurrentHealth = -1;
private int lastSentPlayerMPMaxHealth = -1;
private int lastSentPlayerMPCurrentMagicka = -1;
private int lastSentPlayerMPMaxMagicka = -1;
private int lastSentPlayerMPCurrentFatigue = -1;
private int lastSentPlayerMPMaxFatigue = -1;
private int lastSentPlayerMPLevel = -1;
private float nextPlayerMPHealthSyncTime = 0f;
private const float PLAYER_MP_HEALTH_SYNC_INTERVAL = 0.10f;
private bool heldPlayerMPStateDuringSaveLoad = false;

private int lastSentPlayerMPStealthSkill = -1;
private bool lastSentPlayerMPIsStealthModeActive = false;
private bool hasSentPlayerMPStealthState = false;
private int lastSentPlayerMPMagicalConcealmentFlags = -1;
private float nextPlayerMPStealthSyncTime = 0f;
private const float PLAYER_MP_STEALTH_SYNC_INTERVAL = 0.10f;

// Prevent duplicate application of the same forwarded friendly spell on the
// target owner. Key is sourceNetId:clientCastId.
private readonly HashSet<string> appliedFriendlyPlayerSpellCasts = new HashSet<string>();


private const float GUARD_SPAWN_COOLDOWN = 5f;

private static readonly Dictionary<uint, float> playerGuardSpawnCooldowns = new Dictionary<uint, float>();

	
	List<GameObject> refered = new List<GameObject>();
	
	void Start()
	{
		setupLocal();
        StartCoroutine(ApplyLifeStateVisualAfterSpawn());

        // Server-only safety net: if guard request spam ever slips past the normal
        // cooldown/cap checks, this periodically destroys excess networked city guards.
        if (isServer && serverGuardOverflowWatchdogOwner == null)
        {
            serverGuardOverflowWatchdogOwner = this;
            StartCoroutine(Server_GuardOverflowWatchdog());
        }
	}

    void Update()
    {
        SyncLocalPlayerHealthToMP();
        SyncLocalPlayerStealthAndConcealmentToMP();
        ApplyLifeStateVisualIfChanged(false);
        MaintainSeparateDownedCorpseVisibility();
        MaintainPureClientSavedDungeonAnchor();
    }

    private IEnumerator ApplyLifeStateVisualAfterSpawn()
    {
        // Let PlayerAssets/SpriteMultiplayer finish their initial profile application first.
        yield return null;
        yield return new WaitForSeconds(0.15f);
        ApplyLifeStateVisualIfChanged(true);
        ApplyPlayerMPConcealmentFlagsToProxyEntity(PlayerMPMagicalConcealmentFlags);
    }

    private SpriteMultiplayer GetLifeStateSpriteMultiplayer()
    {
        if (cachedLifeStateSprite != null)
            return cachedLifeStateSprite;

        PlayerAssets assets = GetComponent<PlayerAssets>();
        if (assets != null && assets.spriteMultiplayer != null)
        {
            cachedLifeStateSprite = assets.spriteMultiplayer;
            return cachedLifeStateSprite;
        }

        cachedLifeStateSprite = GetComponentInChildren<SpriteMultiplayer>(true);
        return cachedLifeStateSprite;
    }

    private void ApplyLifeStateVisualIfChanged(bool force)
    {
        // The real local player already has the black-screen/death camera. Only remote
        // PlayerMultiplayer shells need the fake-enemy/corpse visual swap.
        bool shouldShowDownedVisual = !isLocalPlayer && IsDownedForRevive;

        if (!force && lifeStateVisualInitialized && shouldShowDownedVisual == lastAppliedDownedVisual)
            return;

        lifeStateVisualInitialized = true;
        lastAppliedDownedVisual = shouldShowDownedVisual;

        SpriteMultiplayer spriteMultiplayer = GetLifeStateSpriteMultiplayer();
        if (spriteMultiplayer != null)
            spriteMultiplayer.SetDownedVisual(shouldShowDownedVisual);

        // Never enable or disable the ordinary remote visual during spawn or life-state
        // setup. That interrupts SpriteMultiplayer's animation lifecycle. The corpse is
        // a separate runtime child and is culled independently below.
        if (shouldShowDownedVisual)
            ApplySeparateDownedCorpseVisibility();
        else
            cachedDownedCorpseVisualObject = null;
    }

    private void MaintainSeparateDownedCorpseVisibility()
    {
        if (isLocalPlayer || !IsDownedForRevive)
            return;

        float now = Time.realtimeSinceStartup;
        if (now < nextDownedCorpseVisibilityCheckTime)
            return;

        nextDownedCorpseVisibilityCheckTime =
            now + DOWNED_CORPSE_VISIBILITY_CHECK_INTERVAL;

        ApplySeparateDownedCorpseVisibility();
    }

    private void ApplySeparateDownedCorpseVisibility()
    {
        if (isLocalPlayer || !IsDownedForRevive)
            return;

        PositionMultiplayer positionMultiplayer = GetComponent<PositionMultiplayer>();
        if (positionMultiplayer == null)
            return;

        // This is a read-only coordinate test. It must never toggle the normal player
        // visual or the SpriteMultiplayer GameObject.
        bool shouldShowCorpse =
            positionMultiplayer.ShouldShowRemoteVisualForCurrentCoordinates();

        GameObject corpseVisual = GetSeparateDownedCorpseVisualObject();
        if (corpseVisual != null && corpseVisual.activeSelf != shouldShowCorpse)
            corpseVisual.SetActive(shouldShowCorpse);
    }

    private GameObject GetSeparateDownedCorpseVisualObject()
    {
        if (cachedDownedCorpseVisualObject != null)
            return cachedDownedCorpseVisualObject;

        // SetDownedVisual creates this as its own child under PlayerMultiplayer. Search
        // inactive descendants too, because the corpse remains the same object while
        // distance-culled.
        Transform[] childTransforms = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < childTransforms.Length; i++)
        {
            Transform child = childTransforms[i];
            if (child == null || child == transform)
                continue;

            if (string.Equals(
                child.name,
                DOWNED_CORPSE_VISUAL_OBJECT_NAME,
                StringComparison.Ordinal))
            {
                cachedDownedCorpseVisualObject = child.gameObject;
                return cachedDownedCorpseVisualObject;
            }
        }

        return null;
    }

    private void SyncLocalPlayerHealthToMP()
    {
        if (!isLocalPlayer)
            return;

        // SaveLoadManager resets PlayerEntity before restoring the saved character.
        // Never publish that temporary 0-health/level-0/placeholder-vitals window as
        // real multiplayer state. On a pure client the host dungeon request can keep
        // this window open long enough to trigger the MP death/respawn system.
        if (IsLocalSaveLoadInProgress())
        {
            heldPlayerMPStateDuringSaveLoad = true;
            return;
        }

        bool releasedSaveLoadHold = heldPlayerMPStateDuringSaveLoad;
        if (releasedSaveLoadHold)
        {
            heldPlayerMPStateDuringSaveLoad = false;
            lastSentPlayerMPCurrentHealth = -1;
            lastSentPlayerMPMaxHealth = -1;
            lastSentPlayerMPCurrentMagicka = -1;
            lastSentPlayerMPMaxMagicka = -1;
            lastSentPlayerMPCurrentFatigue = -1;
            lastSentPlayerMPMaxFatigue = -1;
            lastSentPlayerMPLevel = -1;
            nextPlayerMPHealthSyncTime = 0f;
        }

        if (Time.time < nextPlayerMPHealthSyncTime)
            return;

        nextPlayerMPHealthSyncTime = Time.time + PLAYER_MP_HEALTH_SYNC_INTERVAL;

        if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
            return;

        PlayerEntity player = GameManager.Instance.PlayerEntity;

        int maxHealth = Mathf.Max(1, player.MaxHealth);
        int currentHealth = Mathf.Clamp(player.CurrentHealth, 0, maxHealth);

        // Some character builds legitimately have no magicka pool. Preserve 0/0 rather
        // than manufacturing a fake maximum of 1 just for the party HUD.
        int maxMagicka = Mathf.Max(0, player.MaxMagicka);
        int currentMagicka = maxMagicka > 0 ? Mathf.Clamp(player.CurrentMagicka, 0, maxMagicka) : 0;

        int maxFatigue = Mathf.Max(1, player.MaxFatigue);
        int currentFatigue = Mathf.Clamp(player.CurrentFatigue, 0, maxFatigue);
        int level = Mathf.Clamp(player.Level, 1, 100);

        if (currentHealth == lastSentPlayerMPCurrentHealth &&
            maxHealth == lastSentPlayerMPMaxHealth &&
            currentMagicka == lastSentPlayerMPCurrentMagicka &&
            maxMagicka == lastSentPlayerMPMaxMagicka &&
            currentFatigue == lastSentPlayerMPCurrentFatigue &&
            maxFatigue == lastSentPlayerMPMaxFatigue &&
            level == lastSentPlayerMPLevel)
            return;

        lastSentPlayerMPCurrentHealth = currentHealth;
        lastSentPlayerMPMaxHealth = maxHealth;
        lastSentPlayerMPCurrentMagicka = currentMagicka;
        lastSentPlayerMPMaxMagicka = maxMagicka;
        lastSentPlayerMPCurrentFatigue = currentFatigue;
        lastSentPlayerMPMaxFatigue = maxFatigue;
        lastSentPlayerMPLevel = level;

        if (isServer)
        {
            ServerSetPlayerMPVitals(
                currentHealth, maxHealth,
                currentMagicka, maxMagicka,
                currentFatigue, maxFatigue,
                level);
        }
        else
        {
            CmdSetPlayerMPVitals(
                currentHealth, maxHealth,
                currentMagicka, maxMagicka,
                currentFatigue, maxFatigue,
                level);
        }

        // If an earlier callback marked this player downed just before LoadInProgress
        // became visible, repair life state only after the real saved positive health
        // has been published.
        if (releasedSaveLoadHold && currentHealth > 0 && LifeState != MultiplayerLifeState.Alive)
            ReportLocalLifeState(MultiplayerLifeState.Alive, "save-load-complete-positive-health");
    }

    private bool IsLocalSaveLoadInProgress()
    {
        try
        {
            DaggerfallWorkshop.Game.Serialization.SaveLoadManager manager =
                DaggerfallWorkshop.Game.Serialization.SaveLoadManager.Instance;
            return manager != null && manager.LoadInProgress;
        }
        catch
        {
            return false;
        }
    }

    [Command]
    public void CmdSetPlayerMPVitals(
        int currentHealth, int maxHealth,
        int currentMagicka, int maxMagicka,
        int currentFatigue, int maxFatigue,
        int level)
    {
        ServerSetPlayerMPVitals(
            currentHealth, maxHealth,
            currentMagicka, maxMagicka,
            currentFatigue, maxFatigue,
            level);
    }

    // Compatibility wrapper for older callers which only know about health/level.
    // The normal local sync path above sends the complete vital set.
    [Command]
    public void CmdSetPlayerMPHealth(int current, int max, int level)
    {
        ServerSetPlayerMPVitals(
            current, max,
            PlayerMPCurrentMagicka, PlayerMPMaxMagicka,
            PlayerMPCurrentFatigue, PlayerMPMaxFatigue,
            level);
    }

    [Server]
    private void ServerSetPlayerMPVitals(
        int currentHealth, int maxHealth,
        int currentMagicka, int maxMagicka,
        int currentFatigue, int maxFatigue,
        int level)
    {
        maxHealth = Mathf.Max(1, maxHealth);
        currentHealth = Mathf.Clamp(currentHealth, 0, maxHealth);

        maxMagicka = Mathf.Max(0, maxMagicka);
        currentMagicka = maxMagicka > 0 ? Mathf.Clamp(currentMagicka, 0, maxMagicka) : 0;

        maxFatigue = Mathf.Max(1, maxFatigue);
        currentFatigue = Mathf.Clamp(currentFatigue, 0, maxFatigue);
        level = Mathf.Clamp(level, 1, 100);

        PlayerMPMaxHealth = maxHealth;
        PlayerMPCurrentHealth = currentHealth;
        PlayerMPMaxMagicka = maxMagicka;
        PlayerMPCurrentMagicka = currentMagicka;
        PlayerMPMaxFatigue = maxFatigue;
        PlayerMPCurrentFatigue = currentFatigue;
        PlayerMPLevel = level;
    }

    public void ForceSyncLocalPlayerHealthToMPNow(string reason)
    {
        if (!isLocalPlayer)
            return;

        lastSentPlayerMPCurrentHealth = -1;
        lastSentPlayerMPMaxHealth = -1;
        lastSentPlayerMPCurrentMagicka = -1;
        lastSentPlayerMPMaxMagicka = -1;
        lastSentPlayerMPCurrentFatigue = -1;
        lastSentPlayerMPMaxFatigue = -1;
        lastSentPlayerMPLevel = -1;
        nextPlayerMPHealthSyncTime = 0f;
        SyncLocalPlayerHealthToMP();
    }

    private void SyncLocalPlayerStealthAndConcealmentToMP()
    {
        if (!isLocalPlayer)
            return;

        if (IsLocalSaveLoadInProgress())
            return;

        if (Time.time < nextPlayerMPStealthSyncTime)
            return;

        nextPlayerMPStealthSyncTime = Time.time + PLAYER_MP_STEALTH_SYNC_INTERVAL;

        if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
            return;

        PlayerEntity player = GameManager.Instance.PlayerEntity;

        int stealthSkill = 0;
        if (player.Skills != null)
            stealthSkill = Mathf.Clamp(player.Skills.GetLiveSkillValue(DFCareer.Skills.Stealth), 0, 200);

        bool stealthModeActive = false;
        try
        {
            PlayerMotor playerMotor = GameManager.Instance.PlayerMotor;
            if (playerMotor != null)
                stealthModeActive = playerMotor.IsMovingLessThanHalfSpeed;
        }
        catch { }

        int concealmentFlags = SanitizePlayerMPConcealmentFlags((int)player.MagicalConcealmentFlags);

        if (hasSentPlayerMPStealthState &&
            stealthSkill == lastSentPlayerMPStealthSkill &&
            stealthModeActive == lastSentPlayerMPIsStealthModeActive &&
            concealmentFlags == lastSentPlayerMPMagicalConcealmentFlags)
            return;

        hasSentPlayerMPStealthState = true;
        lastSentPlayerMPStealthSkill = stealthSkill;
        lastSentPlayerMPIsStealthModeActive = stealthModeActive;
        lastSentPlayerMPMagicalConcealmentFlags = concealmentFlags;

        // Update the local owner copy immediately too. This avoids client-owned enemy AI
        // reading stale stealth/concealment values while waiting for the server echo.
        PlayerMPStealthSkill = stealthSkill;
        PlayerMPIsStealthModeActive = stealthModeActive;
        PlayerMPMagicalConcealmentFlags = concealmentFlags;
        ApplyPlayerMPConcealmentFlagsToProxyEntity(concealmentFlags);

        if (isServer)
            ServerSetPlayerMPStealthAndConcealment(stealthSkill, stealthModeActive, concealmentFlags);
        else
            CmdSetPlayerMPStealthAndConcealment(stealthSkill, stealthModeActive, concealmentFlags);
    }

    [Command]
    public void CmdSetPlayerMPStealthAndConcealment(int stealthSkill, bool stealthModeActive, int concealmentFlags)
    {
        ServerSetPlayerMPStealthAndConcealment(stealthSkill, stealthModeActive, concealmentFlags);
    }

    [Server]
    private void ServerSetPlayerMPStealthAndConcealment(int stealthSkill, bool stealthModeActive, int concealmentFlags)
    {
        PlayerMPStealthSkill = Mathf.Clamp(stealthSkill, 0, 200);
        PlayerMPIsStealthModeActive = stealthModeActive;
        PlayerMPMagicalConcealmentFlags = SanitizePlayerMPConcealmentFlags(concealmentFlags);

        // Mirror immediately on the server object too. SyncVar hooks are not a reliable
        // substitute for direct local application on the host/server instance.
        ApplyPlayerMPConcealmentFlagsToProxyEntity(PlayerMPMagicalConcealmentFlags);
    }

    private static int SanitizePlayerMPConcealmentFlags(int flags)
    {
        MagicalConcealmentFlags allowed =
            MagicalConcealmentFlags.InvisibleNormal |
            MagicalConcealmentFlags.InvisibleTrue |
            MagicalConcealmentFlags.BlendingNormal |
            MagicalConcealmentFlags.BlendingTrue |
            MagicalConcealmentFlags.ShadeNormal |
            MagicalConcealmentFlags.ShadeTrue;

        return flags & (int)allowed;
    }

    private void OnPlayerMPConcealmentFlagsChanged(int oldFlags, int newFlags)
    {
        ApplyPlayerMPConcealmentFlagsToProxyEntity(newFlags);
    }

    private void ApplyPlayerMPConcealmentFlagsToProxyEntity(int flags)
    {
        DaggerfallEntityBehaviour entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
        if (entityBehaviour == null || entityBehaviour.Entity == null)
            return;

        entityBehaviour.Entity.MagicalConcealmentFlags = (MagicalConcealmentFlags)SanitizePlayerMPConcealmentFlags(flags);
    }

    public void ForceSyncLocalPlayerStealthAndConcealmentToMPNow(string reason)
    {
        if (!isLocalPlayer)
            return;

        hasSentPlayerMPStealthState = false;
        lastSentPlayerMPStealthSkill = -1;
        lastSentPlayerMPMagicalConcealmentFlags = -1;
        nextPlayerMPStealthSyncTime = 0f;
        SyncLocalPlayerStealthAndConcealmentToMP();
    }

    public void ReportLocalLifeState(MultiplayerLifeState newState, string reason)
    {
        if (!isLocalPlayer)
            return;

        if (IsLocalSaveLoadInProgress())
        {
            heldPlayerMPStateDuringSaveLoad = true;
            if (Debug.isDebugBuild)
                Debug.Log($"[PlayerLifeState] Suppressed transient save-load state={newState} reason={reason}");
            return;
        }

        if (LifeState == newState)
            return;

        if (isServer)
            ServerSetLifeState(newState, reason);
        else
            CmdSetLifeState(newState, reason);
    }

    [Command]
    public void CmdSetLifeState(MultiplayerLifeState newState, string reason)
    {
        ServerSetLifeState(newState, reason);
    }

    [Server]
    private void ServerSetLifeState(MultiplayerLifeState newState, string reason)
    {
        LifeState = newState;
        LifeStateServerTime = Time.time;

        if (Debug.isDebugBuild)
            Debug.Log("[PlayerLifeState][Server] netId=" + netId + " state=" + newState + " reason=" + reason);
    }

    private void OnMultiplayerLifeStateChanged(MultiplayerLifeState oldState, MultiplayerLifeState newState)
    {
        if (Debug.isDebugBuild)
            Debug.Log("[PlayerLifeState][Client] netId=" + netId + " " + oldState + " -> " + newState);

        ApplyLifeStateVisualIfChanged(true);
    }

    [Command]
    public void CmdRequestRevivePlayer(uint targetPlayerNetId)
    {
        if (!isServer)
            return;

        if (targetPlayerNetId == 0 || targetPlayerNetId == netId)
            return;

        NetworkIdentity targetIdentity;
        if (!NetworkServer.spawned.TryGetValue(targetPlayerNetId, out targetIdentity) || targetIdentity == null)
        {
            Debug.LogWarning("[PlayerRevive][Server] Target netId not spawned: " + targetPlayerNetId);
            return;
        }

        PlayerMultiplayer targetPlayer = targetIdentity.GetComponent<PlayerMultiplayer>();
        if (targetPlayer == null)
        {
            Debug.LogWarning("[PlayerRevive][Server] Target has no PlayerMultiplayer: " + targetPlayerNetId);
            return;
        }

        if (!targetPlayer.IsDownedForRevive)
        {
            if (Debug.isDebugBuild)
                Debug.Log("[PlayerRevive][ServerReject] target=" + targetPlayerNetId + " state=" + targetPlayer.LifeState + " health=" + targetPlayer.PlayerMPCurrentHealth + "/" + targetPlayer.PlayerMPMaxHealth);
            return;
        }

        if (LifeState != MultiplayerLifeState.Alive)
        {
            if (Debug.isDebugBuild)
                Debug.Log("[PlayerRevive][ServerReject] reviver=" + netId + " state=" + LifeState);
            return;
        }

        float allowedDistance = ReviveInteractDistance + 1.25f;
        float distanceSqr = (targetPlayer.transform.position - transform.position).sqrMagnitude;
        if (distanceSqr > allowedDistance * allowedDistance)
        {
            Debug.LogWarning("[PlayerRevive][ServerReject] Too far. reviver=" + netId + " target=" + targetPlayerNetId + " distance=" + Mathf.Sqrt(distanceSqr));
            return;
        }

        NetworkConnection targetConnection = targetPlayer.connectionToClient;
        if (targetConnection == null && NetworkServer.localConnection != null && targetPlayer.isLocalPlayer)
            targetConnection = NetworkServer.localConnection;

        if (targetConnection == null)
        {
            Debug.LogWarning("[PlayerRevive][ServerReject] Target has no owner connection. target=" + targetPlayerNetId);
            return;
        }

        // Temporarily leave Downed so two players cannot spam multiple revive TargetRpcs.
        targetPlayer.ServerSetLifeState(MultiplayerLifeState.Respawning, "revive-request-from-" + netId);
        targetPlayer.TargetReviveDownedPlayer(targetConnection, netId, DefaultReviveHealthPercent);

        Debug.Log("[PlayerRevive][ServerAccept] reviver=" + netId + " target=" + targetPlayerNetId + " healthPercent=" + DefaultReviveHealthPercent);
    }

    [TargetRpc]
    public void TargetReviveDownedPlayer(NetworkConnection target, uint reviverNetId, int reviveHealthPercent)
    {
        if (!isLocalPlayer)
            return;

        bool success = false;
        try
        {
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
            {
                MultiplayerRespawnManager respawnManager = GameManager.Instance.PlayerObject.GetComponent<MultiplayerRespawnManager>();
                if (respawnManager == null)
                    respawnManager = GameManager.Instance.PlayerObject.AddComponent<MultiplayerRespawnManager>();

                success = respawnManager.ReviveLocalPlayerFromNetwork(reviveHealthPercent, reviverNetId);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayerRevive][Target] Revive failed. reviver=" + reviverNetId + " error=" + ex.Message);
        }

        ReportLocalLifeState(success ? MultiplayerLifeState.Alive : MultiplayerLifeState.Downed,
            success ? "target-revived-by-" + reviverNetId : "target-revive-failed-" + reviverNetId);
        ForceSyncLocalPlayerHealthToMPNow("target-revive-rpc");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Friendly player spell forwarding
    //
    // Remote PlayerMultiplayer shells are only collision/target markers. The actual
    // effect must be applied on the target owner's local PlayerAdvanced because that
    // is where the real PlayerEntity and health/effect state lives.
    // ─────────────────────────────────────────────────────────────────────────────

    [Command]
    public void CmdRequestFriendlyPlayerSpell(uint targetPlayerNetId, string spellData, int sourcePlayerLevel, Vector3 impactPosition, uint clientCastId)
    {
        if (!isServer)
            return;

        if (targetPlayerNetId == 0 || string.IsNullOrEmpty(spellData))
            return;

        EffectBundleSettings settings;
        try
        {
            settings = JsonUtility.FromJson<EffectBundleSettings>(spellData);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FriendlyPlayerSpell][Server] Failed to deserialize spell data: " + ex.Message);
            return;
        }

        string reason;
        if (!PlayerSpellMultiplayerBridge.IsFriendlyPlayerSpellBundle(settings, out reason))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[FriendlyPlayerSpell][ServerReject] source={netId} target={targetPlayerNetId} reason={reason}");
            return;
        }

        NetworkIdentity targetIdentity;
        if (!NetworkServer.spawned.TryGetValue(targetPlayerNetId, out targetIdentity) || targetIdentity == null)
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[FriendlyPlayerSpell][ServerReject] target netId not spawned: {targetPlayerNetId}");
            return;
        }

        PlayerMultiplayer targetPlayer = targetIdentity.GetComponent<PlayerMultiplayer>();
        if (targetPlayer == null)
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[FriendlyPlayerSpell][ServerReject] target has no PlayerMultiplayer: {targetPlayerNetId}");
            return;
        }

        if (!PlayerSpellMultiplayerBridge.ServerValidateFriendlyPlayerSpell(this, targetPlayer, settings, out reason))
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[FriendlyPlayerSpell][ServerReject] source={netId} target={targetPlayerNetId} reason={reason}");
            return;
        }

        NetworkConnection targetConnection = targetPlayer.connectionToClient;
        if (targetConnection == null && NetworkServer.localConnection != null && targetPlayer.isLocalPlayer)
            targetConnection = NetworkServer.localConnection;

        if (targetConnection == null)
        {
            Debug.LogWarning($"[FriendlyPlayerSpell][ServerReject] No target connection for player netId={targetPlayerNetId}");
            return;
        }

        sourcePlayerLevel = Mathf.Clamp(sourcePlayerLevel, 1, 100);

        if (Debug.isDebugBuild)
            Debug.Log($"[FriendlyPlayerSpell][ServerAccept] source={netId} target={targetPlayerNetId} castId={clientCastId} effects={(settings.Effects != null ? settings.Effects.Length : 0)}");

        targetPlayer.TargetApplyFriendlyPlayerSpell(targetConnection, netId, targetPlayerNetId, spellData, sourcePlayerLevel, clientCastId);
        RpcPlayFriendlyPlayerSpellCosmetics(netId, targetPlayerNetId, impactPosition, settings.ElementType);
    }

    [TargetRpc]
    public void TargetApplyFriendlyPlayerSpell(NetworkConnection target, uint sourcePlayerNetId, uint targetPlayerNetId, string spellData, int sourcePlayerLevel, uint clientCastId)
    {
        if (!isLocalPlayer)
            return;

        string applyKey = sourcePlayerNetId.ToString() + ":" + clientCastId.ToString();
        if (appliedFriendlyPlayerSpellCasts.Contains(applyKey))
            return;
        appliedFriendlyPlayerSpellCasts.Add(applyKey);

        EffectBundleSettings settings;
        try
        {
            settings = JsonUtility.FromJson<EffectBundleSettings>(spellData);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FriendlyPlayerSpell][Target] Failed to deserialize spell data: " + ex.Message);
            return;
        }

        string reason;
        if (!PlayerSpellMultiplayerBridge.IsFriendlyPlayerSpellBundle(settings, out reason))
        {
            Debug.LogWarning($"[FriendlyPlayerSpell][TargetReject] source={sourcePlayerNetId} target={targetPlayerNetId} reason={reason}");
            return;
        }

        DaggerfallEntityBehaviour localPlayerBehaviour = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
        if (localPlayerBehaviour == null)
        {
            Debug.LogWarning("[FriendlyPlayerSpell][Target] Local PlayerAdvanced EntityBehaviour is null.");
            return;
        }

        EntityEffectManager localEffectManager = localPlayerBehaviour.GetComponent<EntityEffectManager>();
        if (localEffectManager == null)
        {
            Debug.LogWarning("[FriendlyPlayerSpell][Target] Local PlayerAdvanced has no EntityEffectManager.");
            return;
        }

        // The target owner must apply the spell to its own real PlayerAdvanced.
        // For cooperative player-to-player support spells, apply the received bundle as
        // a local self/party effect on the target PlayerAdvanced. This is important
        // because some DFU effects still perform external-target save/chance logic when
        // the bundle TargetType remains ByTouch/SingleTargetAtRange, even if AssignBundle
        // receives BypassSavingThrows. Converting only this already-whitelisted forwarded
        // copy to CasterOnly makes the target resolve it like a friendly self-buff/heal.
        EffectBundleSettings targetSettings = settings;
        targetSettings.TargetType = TargetTypes.CasterOnly;
        EntityEffectBundle bundle = new EntityEffectBundle(targetSettings, localPlayerBehaviour);

        // Instant one-shot friendly effects (Heal/Restore/Cure) should affect only the
        // real local PlayerAdvanced and should not leave a persistent active-effect icon.
        // AssignBundle() is still used so DFU applies the effect normally, but any newly
        // created one-shot bundle is removed immediately on the target client after the
        // initial MagicRound has executed.
        bool removeInstantFriendlyIcon = PlayerSpellMultiplayerBridge.IsInstantOneShotFriendlyPlayerBundle(settings);
        HashSet<LiveEffectBundle> bundlesBeforeFriendlyApply = removeInstantFriendlyIcon
            ? new HashSet<LiveEffectBundle>(localEffectManager.EffectBundles)
            : null;

        // Friendly player support spells are party-style cooperative effects.
        // They should not be resisted/saved against by the receiving player, and
        // chance-based friendly effects should not fail on party members. This is
        // intentionally only used after the forwarded bundle passed the friendly
        // whitelist and only on the target owner's real local PlayerAdvanced.
        localEffectManager.AssignBundle(bundle, PlayerSpellMultiplayerBridge.GetFriendlyPlayerAssignBundleFlags());


        if (removeInstantFriendlyIcon)
            RemoveNewFriendlyInstantBundles(localEffectManager, bundlesBeforeFriendlyApply);

        // Force a quick health SyncVar refresh for immediate healthbar updates after
        // instant effects like Heal-Health. Buffs/cures continue through the normal
        // EntityEffectManager magic-round system.
        nextPlayerMPHealthSyncTime = 0f;
        SyncLocalPlayerHealthToMP();

        if (Debug.isDebugBuild)
            Debug.Log($"[FriendlyPlayerSpell][TargetApplied] source={sourcePlayerNetId} target={targetPlayerNetId} castId={clientCastId} sourceLevel={sourcePlayerLevel}");
    }

    private void RemoveNewFriendlyInstantBundles(EntityEffectManager manager, HashSet<LiveEffectBundle> bundlesBefore)
    {
        if (manager == null)
            return;

        LiveEffectBundle[] bundlesAfter = manager.EffectBundles;
        if (bundlesAfter == null || bundlesAfter.Length == 0)
            return;

        for (int i = 0; i < bundlesAfter.Length; i++)
        {
            LiveEffectBundle liveBundle = bundlesAfter[i];
            if (liveBundle == null)
                continue;

            if (bundlesBefore != null && bundlesBefore.Contains(liveBundle))
                continue;

            try
            {
                manager.RemoveBundle(liveBundle);
                if (Debug.isDebugBuild)
                    Debug.Log($"[FriendlyPlayerSpell][InstantCleanup] Removed one-shot bundle '{liveBundle.name}' from target PlayerAdvanced after immediate application.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FriendlyPlayerSpell][InstantCleanup] Failed to remove one-shot bundle: " + ex.Message);
            }
        }
    }

    [ClientRpc]
    private void RpcPlayFriendlyPlayerSpellCosmetics(uint sourcePlayerNetId, uint targetPlayerNetId, Vector3 impactPosition, ElementTypes elementType)
    {
        uint localNetId = GetLocalPlayerNetIdSafe();

        // The caster already sees their local cast/missile. The target applies the real
        // effect locally. Observers get a simple sparkle on the target shell for now.
        if (localNetId == sourcePlayerNetId || localNetId == targetPlayerNetId)
            return;

        NetworkIdentity targetIdentity;
        if (!NetworkClient.spawned.TryGetValue(targetPlayerNetId, out targetIdentity) || targetIdentity == null)
            return;

        GameObject targetObject = targetIdentity.gameObject;
        Vector3 sparklesPos = impactPosition;
        if (sparklesPos == Vector3.zero)
        {
            sparklesPos = targetObject.transform.position;
            CharacterController controller = targetObject.GetComponent<CharacterController>();
            if (controller != null)
            {
                sparklesPos += controller.center;
                sparklesPos.y += controller.height / 8f;
            }
        }

        PlayRemotePlayerSpellImpactBillboard(elementType, sparklesPos);
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // Player spell cast cosmetics
    //
    // This is visual/audio only. It does not apply damage, healing, or effects.
    // Every local player cast reports element + target type + aim direction so
    // observers can see a visual-only missile/sparkle from the remote shell.
    // ─────────────────────────────────────────────────────────────────────────────

    [Command(requiresAuthority = false)]
    public void CmdReportPlayerSpellCastVisual(int elementTypeInt, int targetTypeInt, Vector3 aimDirection, int castSoundID, NetworkConnectionToClient sender = null)
    {
        if (!isServer)
            return;

        uint sourceNetId = netId;
        if (sender != null && sender.identity != null)
            sourceNetId = sender.identity.netId;

        if (sourceNetId == 0)
            return;

        if (aimDirection == Vector3.zero)
            aimDirection = Vector3.forward;

        RpcPlayPlayerSpellCastVisual(sourceNetId, elementTypeInt, targetTypeInt, aimDirection.normalized, castSoundID);
    }

    [ClientRpc]
    private void RpcPlayPlayerSpellCastVisual(uint sourcePlayerNetId, int elementTypeInt, int targetTypeInt, Vector3 aimDirection, int castSoundID)
    {
        uint localNetId = GetLocalPlayerNetIdSafe();

        // The caster already sees the real local spell animation/missile/sound.
        if (localNetId == sourcePlayerNetId)
            return;

        NetworkIdentity sourceIdentity;
        if (!NetworkClient.spawned.TryGetValue(sourcePlayerNetId, out sourceIdentity) || sourceIdentity == null)
            return;

        GameObject sourceObject = sourceIdentity.gameObject;
        if (sourceObject == null)
            return;

        ElementTypes elementType = (ElementTypes)elementTypeInt;
        TargetTypes targetType = (TargetTypes)targetTypeInt;

        Vector3 visualOrigin = GetRemotePlayerSpellVisualOrigin(sourceObject);
        Vector3 direction = aimDirection != Vector3.zero ? aimDirection.normalized : sourceObject.transform.forward;
        if (direction == Vector3.zero)
            direction = Vector3.forward;

        // Replay the same element-based cast sound from a neutral temporary audio
        // object near the remote player shell. Do not play through the shell/visual
        // child itself, because the child is an enemy-style prefab and can route to
        // creature sounds on some PlayerMultiplayer visuals.
        PlayRemotePlayerSpellCastSoundNeutral(visualOrigin, castSoundID);

        // Ranged spells get a real visual-only missile. It can collide and play its
        // impact animation locally, but DaggerfallMissile.VisualOnly prevents payloads.
        if (targetType == TargetTypes.SingleTargetAtRange || targetType == TargetTypes.AreaAtRange)
        {
            EntityEffectManager localEffectManager = null;
            if (GameManager.Instance != null && GameManager.Instance.PlayerEntityBehaviour != null)
                localEffectManager = GameManager.Instance.PlayerEntityBehaviour.GetComponent<EntityEffectManager>();

            if (localEffectManager != null)
            {
                DaggerfallMissile missile = localEffectManager.InstantiateSpellMissile(elementType);
                if (missile != null)
                {
                    missile.VisualOnly = true;
                    missile.TargetType = targetType;
                    missile.ElementType = elementType;
                    missile.CustomAimPosition = visualOrigin;
                    missile.CustomAimDirection = direction;
                    return;
                }
            }
        }

        // Touch, caster-only, and area-around-caster spells do not have a travelling
        // projectile in DFU, so observers get an element-correct one-shot impact
        // billboard on the caster shell instead of the old generic magic sparkles.
        PlayRemotePlayerSpellImpactBillboard(elementType, visualOrigin);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Player bow-shot cosmetics
    //
    // The firing client keeps its normal authoritative/local arrow. Observers create
    // their own local DaggerfallMissile arrow with VisualOnly enabled, so it can fly
    // and collide visually without ever applying bow damage.
    // ─────────────────────────────────────────────────────────────────────────────

    [Command(requiresAuthority = false)]
    public void CmdReportPlayerArrowShotVisual(Vector3 aimDirection, NetworkConnectionToClient sender = null)
    {
        if (!isServer)
            return;

        // Never trust the PlayerMultiplayer object the caller happened to invoke the
        // authority-free cosmetic Command on. Derive the actual shooter from Mirror's
        // sender connection, matching the player spell cosmetic path above.
        uint sourceNetId = netId;
        if (sender != null && sender.identity != null)
            sourceNetId = sender.identity.netId;

        if (sourceNetId == 0)
            return;

        if (aimDirection == Vector3.zero)
            aimDirection = Vector3.forward;

        RpcPlayPlayerArrowShotVisual(sourceNetId, aimDirection.normalized);
    }

    [ClientRpc]
    private void RpcPlayPlayerArrowShotVisual(uint sourcePlayerNetId, Vector3 aimDirection)
    {
        // A cosmetic RPC must fail soft. An exception escaping a Mirror RPC handler can
        // disconnect the receiving client, while a missing arrow visual is non-critical.
        try
        {
            uint localNetId = GetLocalPlayerNetIdSafe();

            // The shooter already sees the real local arrow and must not receive a
            // duplicate visual-only copy.
            if (localNetId == sourcePlayerNetId)
                return;

            NetworkIdentity sourceIdentity;
            if (!NetworkClient.spawned.TryGetValue(sourcePlayerNetId, out sourceIdentity) || sourceIdentity == null)
                return;

            GameObject sourceObject = sourceIdentity.gameObject;
            if (sourceObject == null || GameManager.Instance == null || GameManager.Instance.WeaponManager == null)
                return;

            DaggerfallMissile arrowPrefab = GameManager.Instance.WeaponManager.ArrowMissilePrefab;
            if (arrowPrefab == null)
                return;

            Vector3 direction = aimDirection != Vector3.zero ? aimDirection.normalized : sourceObject.transform.forward;
            if (direction == Vector3.zero)
                direction = Vector3.forward;

            Vector3 visualOrigin = GetRemotePlayerSpellVisualOrigin(sourceObject);
            DaggerfallMissile missile = Instantiate(arrowPrefab);
            if (missile == null)
                return;

            // Set every property before Unity invokes Start() on the new missile.
            // VisualOnly also makes the projectile collision-safe; DaggerfallMissile's
            // bow-damage path has an explicit VisualOnly guard as a second safety layer.
            missile.VisualOnly = true;
            DaggerfallEntityBehaviour sourceCaster = sourceObject.GetComponent<DaggerfallEntityBehaviour>();
            if (sourceCaster == null)
                sourceCaster = sourceObject.GetComponentInChildren<DaggerfallEntityBehaviour>();
            missile.Caster = sourceCaster;
            missile.TargetType = TargetTypes.SingleTargetAtRange;
            missile.ElementType = ElementTypes.None;
            missile.IsArrow = true;
            missile.IsArrowSummoned = false;
            missile.CustomAimPosition = visualOrigin;
            missile.CustomAimDirection = direction;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PlayerArrowVisuals] Failed to create remote visual-only arrow: " + ex.Message);
        }
    }

    private Vector3 GetRemotePlayerSpellVisualOrigin(GameObject sourceObject)
    {
        Vector3 pos = sourceObject.transform.position;
        CharacterController controller = sourceObject.GetComponent<CharacterController>();
        if (controller != null)
        {
            pos += controller.center;
            pos.y += controller.height / 8f;
        }
        else
        {
            pos += Vector3.up * 1.4f;
        }

        return pos;
    }

    private void PlayRemotePlayerSpellSparkles(GameObject sourceObject, Vector3 position)
    {
        EnemyBlood sparkles = sourceObject.GetComponent<EnemyBlood>();
        if (sparkles == null)
            sparkles = sourceObject.GetComponentInChildren<EnemyBlood>();

        if (sparkles != null)
            sparkles.ShowMagicSparkles(position);
    }

    private void PlayRemotePlayerSpellImpactBillboard(ElementTypes elementType, Vector3 position)
    {
        int archive = GetRemotePlayerSpellTextureArchive(elementType);
        if (archive < 0)
        {
            // Fallback to the existing sparkle helper only if the element cannot be mapped.
            return;
        }

        GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(archive, 1, null);
        if (go == null)
            return;

        go.transform.position = position;
        go.layer = gameObject.layer;

        Billboard billboard = go.GetComponent<Billboard>();
        if (billboard != null)
        {
            billboard.FramesPerSecond = 15;
            billboard.FaceY = true;
            billboard.OneShot = true;
        }

        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.receiveShadows = false;

        StartCoroutine(DestroyRemotePlayerSpellImpactBillboard(go, 0.75f));
    }

    private IEnumerator DestroyRemotePlayerSpellImpactBillboard(GameObject go, float delay)
    {
        // Use realtime so cosmetic spell billboards still clear if this client is in
        // a menu/popup or otherwise has scaled game time paused.
        yield return new WaitForSecondsRealtime(delay);
        if (go != null)
            Destroy(go);
    }

    private int GetRemotePlayerSpellTextureArchive(ElementTypes elementType)
    {
        switch (elementType)
        {
            case ElementTypes.Cold:
                return 376;
            case ElementTypes.Fire:
                return 375;
            case ElementTypes.Magic:
                return 379;
            case ElementTypes.Poison:
                return 377;
            case ElementTypes.Shock:
                return 378;
            default:
                return -1;
        }
    }

    private void PlayRemotePlayerSpellCastSoundNeutral(Vector3 position, int castSoundID)
    {
        if (castSoundID < 0)
            return;

        GameObject soundObject = null;

        try
        {
            if (DaggerfallUnity.Instance == null || !DaggerfallUnity.Instance.IsReady || DaggerfallUnity.Instance.SoundReader == null)
                return;

            // EntityEffectManager.PlayCastSound() uses these values as classic sound IDs
            // through the uint DaggerfallAudioSource overload. Do the same lookup here,
            // but play the resolved AudioClip directly through a neutral Unity AudioSource.
            // This avoids routing sound through the PlayerMultiplayer enemy visual child and
            // avoids accidentally using the SoundClips index overload, which maps 349-353 to
            // unrelated sounds such as monsters/weather.
            int soundIndex = DaggerfallUnity.Instance.SoundReader.GetSoundIndex((uint)castSoundID);
            if (soundIndex < 0)
            {
                Debug.LogWarning($"[PlayerSpellVisuals] Could not resolve remote spell cast soundID={castSoundID} to a sound index.");
                return;
            }

            AudioClip clip = DaggerfallUnity.Instance.SoundReader.GetAudioClip(soundIndex);
            if (clip == null)
            {
                Debug.LogWarning($"[PlayerSpellVisuals] Could not load remote spell cast sound clip index={soundIndex} soundID={castSoundID}.");
                return;
            }

            soundObject = new GameObject("Remote Player Spell Cast Sound");
            soundObject.transform.position = position;

            AudioSource unityAudio = soundObject.AddComponent<AudioSource>();
            if (unityAudio == null)
                return;

            unityAudio.spatialBlend = 1f;
            unityAudio.rolloffMode = AudioRolloffMode.Linear;
            unityAudio.minDistance = 1f;
            unityAudio.maxDistance = 25f;
            unityAudio.playOnAwake = false;
            unityAudio.volume = DaggerfallUnity.Settings.SoundVolume;
            unityAudio.PlayOneShot(clip, 1f);
        }
        catch (System.Exception ex)
        {
            // Never let cosmetic spell audio break a Mirror RPC. A thrown exception
            // in an RPC handler makes Mirror disconnect the client as an exploit
            // protection. Visual spell sync should fail soft.
            Debug.LogWarning("[PlayerSpellVisuals] Remote spell cast sound failed: " + ex.Message);
        }

        if (soundObject != null)
            StartCoroutine(DestroyRemotePlayerSpellSoundObject(soundObject, 3.0f));
    }

    private IEnumerator DestroyRemotePlayerSpellSoundObject(GameObject soundObject, float delay)
    {
        yield return new WaitForSecondsRealtime(delay);
        if (soundObject != null)
            Destroy(soundObject);
    }

    // Returns DFU SoundClips enum index, not raw Daggerfall sound ID.
    // Use the int PlayOneShot overload, not the uint soundID overload.
    private int GetRemotePlayerSpellCastSoundID(ElementTypes elementType)
    {
        switch (elementType)
        {
            case ElementTypes.Poison:
                return 350;
            case ElementTypes.Shock:
                return 351;
            case ElementTypes.Fire:
                return 352;
            case ElementTypes.Cold:
                return 353;
            case ElementTypes.Magic:
            default:
                return 349;
        }
    }
	
	void init()
	{
		playerObject = GameManager.Instance.PlayerObject;
		localPlayer = this;
		id = "" + GetComponent<NetworkIdentity>().netId;
		state = isServer ? 1 : 2;
		if (!isServer)
			importOptions();
	}
	
	void setupLocal()
	{
		if (isLocalPlayer){
			enableAll(false);
			init();
		}else{
			enableAll(true);

            // Do NOT disable PlayerMultiplayer on remote player objects.
            // EnemySenses finds valid multiplayer targets through FindObjectsOfType<PlayerMultiplayer>()
            // and filters by isActiveAndEnabled. If this component is disabled, enemies cannot
            // select this remote player as a target.
            //
            // The local-only helper components are still disabled through toDisable above.
            // EntityCatcher should simply not be assigned to toDisable, so the legacy enemy sync
            // system stays inactive without breaking this target marker component.
			Invoke("sendMessage", 1.5f);
		}
	}
	

	
	
	
	void sendMessage()
	{
		if (OptionsMultiplayer.sendMessage){
			PositionMultiplayer pos = GetComponent<PositionMultiplayer>();
			PlayerGPS gps = GameManager.Instance.PlayerGPS;
			PlayerAssets assets = GetComponent<PlayerAssets>();
			
			float distance = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(gps.WorldX, gps.WorldZ));
			string message = string.Format(messages[UnityEngine.Random.Range(3, 5)], assets.playerName);
			if (distance < 7500){
				message = string.Format(messages[0], assets.playerName);
			}else if (distance < 25000){
				message = string.Format(messages[1], assets.playerName);
			}else if (distance < 80000){
				message = string.Format(messages[2], assets.playerName);
			}
			DaggerfallUI.MessageBox(message);
		}
	}
	
	
	public void importOptions()
	{
		cmdImportOptions();
	}
	
	[Command]
	public void cmdImportOptions()
	{
		rpcImportOptions(OptionsMultiplayer.Export());
	}
	
	[ClientRpc]
	public void rpcImportOptions(string s)
	{
		if (!isServer && localPlayer){
			OptionsMultiplayer.Import(s);
		}
	}
	
void enableAll(bool b)
{
    if (toEnable != null)
    {
        foreach (GameObject g in toEnable)
        {
            if (g != null)
                g.SetActive(b);
        }
    }

    if (toDisable != null)
    {
        foreach (NetworkBehaviour n in toDisable)
        {
            if (n != null)
                n.enabled = !b;
        }
    }
}
	
	
	void OnDestroy()
	{
        if (serverGuardOverflowWatchdogOwner == this)
            serverGuardOverflowWatchdogOwner = null;

		for (int i = 0; i < refered.Count; i++){
			Destroy(refered[i]);
		}
		
		if (isLocalPlayer){
			state = 0;
            if (localPlayer == this)
                localPlayer = null;
		}
			
	}
	
	
	public static int getRandom(float min, float max)
	{
		if (randomTime + 1 < Time.time){
			randomTime = Time.time;
			random = 0;
		}
		random++;
		return (int)(min + (max-min) * Mathf.Pow(Mathf.Sin(random*4.132f), 2)-1);
	}

    // ─────────────────────────────────────────────────────────────────────────────
    // Networked combat cosmetics
    // These do not apply damage. They only replay local-only hit sounds, blood splashes,
    // and optional pain voices for observers after another player/enemy already applied
    // the actual local damage + health sync path.
    // ─────────────────────────────────────────────────────────────────────────────

    [Command(requiresAuthority = false)]
    public void CmdReportPlayerHitEnemyCosmetics(uint enemyNetId, Vector3 impactPosition, bool weaponHit, bool playPainVoice, bool heavyDamage, NetworkConnectionToClient sender = null)
    {
        if (!isServer)
            return;

        uint sourceNetId = netId;
        if (sender != null && sender.identity != null)
            sourceNetId = sender.identity.netId;

        Debug.Log($"[CombatCosmetics][Server] PlayerHitEnemy source={sourceNetId} enemy={enemyNetId} weaponHit={weaponHit} pain={playPainVoice}");
        RpcPlayPlayerHitEnemyCosmetics(sourceNetId, enemyNetId, impactPosition, weaponHit, playPainVoice, heavyDamage);
    }

    [ClientRpc]
    private void RpcPlayPlayerHitEnemyCosmetics(uint sourcePlayerNetId, uint enemyNetId, Vector3 impactPosition, bool weaponHit, bool playPainVoice, bool heavyDamage)
    {
        // The attacker already played these locally in WeaponManager. Do not duplicate them.
        if (GetLocalPlayerNetIdSafe() == sourcePlayerNetId)
            return;

        NetworkIdentity enemyIdentity;
        if (!NetworkClient.spawned.TryGetValue(enemyNetId, out enemyIdentity) || enemyIdentity == null)
            return;

        GameObject enemyObject = enemyIdentity.gameObject;
        DaggerfallEntityBehaviour enemyBehaviour = enemyObject.GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity enemyEntity = enemyBehaviour != null ? enemyBehaviour.Entity as EnemyEntity : null;
        if (enemyEntity == null)
            return;

        EnemySounds enemySounds = enemyObject.GetComponent<EnemySounds>();
        if (enemySounds != null)
            enemySounds.PlayGenericHitSound(weaponHit);

        EnemyBlood blood = enemyObject.GetComponent<EnemyBlood>();
        if (blood != null)
            blood.ShowBloodSplash(enemyEntity.MobileEnemy.BloodIndex, impactPosition);

        if (playPainVoice && enemySounds != null && DaggerfallUnity.Settings.CombatVoices &&
            enemyBehaviour.EntityType == EntityTypes.EnemyClass)
        {
            MobileUnit mobileUnit = enemyObject.GetComponentInChildren<MobileUnit>();
            if (mobileUnit != null && mobileUnit.IsSetup)
            {
                Genders gender = (mobileUnit.Enemy.Gender == MobileGender.Male ||
                                  enemyEntity.MobileEnemy.ID == (int)MobileTypes.Knight_CityWatch)
                    ? Genders.Male
                    : Genders.Female;

                enemySounds.PlayCombatVoice(gender, false, heavyDamage);
            }
        }
    }

    [Command(requiresAuthority = false)]
    public void CmdReportLocalPlayerHitByEnemyCosmetics(uint enemyNetId, bool weaponHit, int damageAmount, bool playEnemyAttackSound, NetworkConnectionToClient sender = null)
    {
        if (!isServer)
            return;

        uint targetNetId = netId;
        if (sender != null && sender.identity != null)
            targetNetId = sender.identity.netId;

        Debug.Log($"[CombatCosmetics][Server] EnemyHitPlayer target={targetNetId} enemy={enemyNetId} weaponHit={weaponHit} attackSound={playEnemyAttackSound} damage={damageAmount}");
        RpcPlayRemotePlayerHitByEnemyCosmetics(targetNetId, enemyNetId, weaponHit, damageAmount, playEnemyAttackSound);
    }

    [ClientRpc]
    private void RpcPlayRemotePlayerHitByEnemyCosmetics(uint targetPlayerNetId, uint enemyNetId, bool weaponHit, int damageAmount, bool playEnemyAttackSound)
    {
        // The player who was hit already heard this locally through PlayerFootsteps.
        if (GetLocalPlayerNetIdSafe() == targetPlayerNetId)
            return;

        GameObject soundObject = null;

        NetworkIdentity enemyIdentity;
        if (NetworkClient.spawned.TryGetValue(enemyNetId, out enemyIdentity) && enemyIdentity != null)
            soundObject = enemyIdentity.gameObject;

        if (soundObject == null)
        {
            NetworkIdentity targetIdentity;
            if (NetworkClient.spawned.TryGetValue(targetPlayerNetId, out targetIdentity) && targetIdentity != null)
                soundObject = targetIdentity.gameObject;
        }

        if (soundObject == null)
            return;

        EnemySounds enemySoundsForReplay = soundObject.GetComponent<EnemySounds>();

        if (playEnemyAttackSound)
        {
            if (enemySoundsForReplay != null)
                enemySoundsForReplay.PlayAttackSoundForced();
        }

        // damageAmount <= 0 is an enemy swing/miss. Recreate the same local miss/swing
        // sound near the enemy instead of playing a hit-impact sound.
        if (damageAmount <= 0)
        {
            if (enemySoundsForReplay != null)
            {
                DaggerfallWorkshop.Game.Items.DaggerfallUnityItem missWeapon = null;
                DaggerfallEntityBehaviour enemyBehaviour = soundObject.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = enemyBehaviour != null ? enemyBehaviour.Entity as EnemyEntity : null;
                if (enemyEntity != null)
                {
                    missWeapon = enemyEntity.ItemEquipTable.GetItem(DaggerfallWorkshop.Game.Items.EquipSlots.RightHand);
                    if (missWeapon == null)
                        missWeapon = enemyEntity.ItemEquipTable.GetItem(DaggerfallWorkshop.Game.Items.EquipSlots.LeftHand);
                }

                enemySoundsForReplay.PlayMissSound(missWeapon);
            }

            return;
        }

        DaggerfallAudioSource audio = soundObject.GetComponent<DaggerfallAudioSource>();
        if (audio == null)
            return;

        int sound = weaponHit
            ? (int)SoundClips.Hit1 + UnityEngine.Random.Range(0, 5)
            : (int)SoundClips.Hit1 + UnityEngine.Random.Range(2, 4);

        audio.PlayOneShot(sound, 1, 1f);
    }
	

    [Command(requiresAuthority = false)]
    public void CmdReportLocalPlayerSpellEffectCosmetics(uint enemyNetId, NetworkConnectionToClient sender = null)
    {
        if (!isServer)
            return;

        uint targetNetId = netId;
        if (sender != null && sender.identity != null)
            targetNetId = sender.identity.netId;

        RpcPlayRemotePlayerSpellEffectCosmetics(targetNetId, enemyNetId);
    }

    [ClientRpc]
    private void RpcPlayRemotePlayerSpellEffectCosmetics(uint targetPlayerNetId, uint enemyNetId)
    {
        // The target already sees/receives the real local PlayerAdvanced effect.
        if (GetLocalPlayerNetIdSafe() == targetPlayerNetId)
            return;

        NetworkIdentity targetIdentity;
        if (!NetworkClient.spawned.TryGetValue(targetPlayerNetId, out targetIdentity) || targetIdentity == null)
            return;

        GameObject targetObject = targetIdentity.gameObject;
        Vector3 sparklesPos = targetObject.transform.position;

        CharacterController controller = targetObject.GetComponent<CharacterController>();
        if (controller != null)
        {
            sparklesPos += controller.center;
            sparklesPos.y += controller.height / 8f;
        }

        // Enemy spell cosmetics do not carry an element here, so keep the old generic
        // sparkle path for this existing enemy-spell observer feedback.
        PlayRemotePlayerSpellSparkles(targetObject, sparklesPos);
    }


    [TargetRpc]
    public void TargetApplyEnemyTouchSpellPayload(NetworkConnection target, uint enemyNetId, int spellIndex, string spellData)
    {
        Debug.Log($"[MPTouchSpellForward][TargetRpcReceived] objectNetId={netId} local={isLocalPlayer} enemyNetId={enemyNetId} spellIndex={spellIndex} hasSpellData={!string.IsNullOrEmpty(spellData)}");

        // This TargetRpc is sent to the PlayerMultiplayer that represents the real local
        // player on this client. Only that local copy may apply the spell to PlayerAdvanced.
        if (!isLocalPlayer)
        {
            Debug.LogWarning($"[MPTouchSpellForward][TargetRpcIgnored] Received on non-local PlayerMultiplayer objectNetId={netId} enemyNetId={enemyNetId}.");
            return;
        }

        DaggerfallEntityBehaviour casterBehaviour = null;
        if (enemyNetId != 0)
        {
            NetworkIdentity enemyIdentity;
            if (NetworkClient.spawned.TryGetValue(enemyNetId, out enemyIdentity) && enemyIdentity != null)
                casterBehaviour = enemyIdentity.GetComponent<DaggerfallEntityBehaviour>();
        }

        if (casterBehaviour == null)
        {
            Debug.LogWarning($"[MPTouchSpellForward][TargetRpc] Could not resolve enemy caster netId={enemyNetId} on targeted client. Will try JSON payload fallback.");
        }

        DaggerfallEntityBehaviour localPlayerBehaviour = GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null;
        if (localPlayerBehaviour == null)
        {
            Debug.LogWarning("[MPTouchSpellForward][TargetRpc] GameManager.PlayerEntityBehaviour is null on targeted client.");
            return;
        }

        EntityEffectManager localEffectManager = localPlayerBehaviour.GetComponent<EntityEffectManager>();
        if (localEffectManager == null)
        {
            Debug.LogWarning("[MPTouchSpellForward][TargetRpc] Local PlayerAdvanced has no EntityEffectManager. Cannot apply enemy touch spell payload.");
            return;
        }

        EntityEffectBundle bundle = ResolveEnemySpellBundleForClient(casterBehaviour, enemyNetId, spellIndex, spellData);
        if (bundle == null || bundle.Settings.Effects == null || bundle.Settings.Effects.Length == 0)
        {
            Debug.LogWarning($"[MPTouchSpellForward][TargetRpc] Could not reconstruct spell payload enemyNetId={enemyNetId} spellIndex={spellIndex}. No effect assigned.");
            return;
        }

        Debug.Log($"[MPTouchSpellForward][TargetRpcAssign] Applying enemy touch spell to local PlayerAdvanced enemyNetId={enemyNetId} spellIndex={spellIndex} effects={bundle.Settings.Effects.Length} targetType={bundle.Settings.TargetType}");
        localEffectManager.AssignBundle(bundle, AssignBundleFlags.ShowNonPlayerFailures);

        // Tell the server/observers to replay the visible magic sparkle on this remote
        // PlayerMultiplayer shell. The real target client already received the actual effect.
        CmdReportLocalPlayerSpellEffectCosmetics(enemyNetId);
    }

    private EntityEffectBundle ResolveEnemySpellBundleForClient(DaggerfallEntityBehaviour casterBehaviour, uint enemyNetId, int spellIndex, string spellData)
    {
        if (casterBehaviour != null && spellIndex >= 0 && casterBehaviour.Entity is EnemyEntity enemyEntity)
        {
            EffectBundleSettings[] spells = enemyEntity.GetSpells();
            if (spells != null && spellIndex < spells.Length)
            {
                Debug.Log($"[MPTouchSpellForward][ResolveByIndex] enemyNetId={enemyNetId} spellIndex={spellIndex} effects={(spells[spellIndex].Effects != null ? spells[spellIndex].Effects.Length : -1)}");
                return new EntityEffectBundle(spells[spellIndex], casterBehaviour);
            }
        }

        if (!string.IsNullOrEmpty(spellData))
        {
            try
            {
                EffectBundleSettings settings = JsonUtility.FromJson<EffectBundleSettings>(spellData);
                Debug.Log($"[MPTouchSpellForward][ResolveByJson] enemyNetId={enemyNetId} spellIndex={spellIndex} effects={(settings.Effects != null ? settings.Effects.Length : -1)}");
                DaggerfallEntityBehaviour fallbackCaster = casterBehaviour != null ? casterBehaviour : (GameManager.Instance != null ? GameManager.Instance.PlayerEntityBehaviour : null);
                return new EntityEffectBundle(settings, fallbackCaster);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MPTouchSpellForward][ResolveByJsonFailed] {ex.Message}");
            }
        }

        return null;
    }

//enemy related stuff
	
[Command]
public void CmdCreateFoes(
    Vector3 position,
    MobileTypes foeType,
    int spawnCount,
    MobileReactions reaction,
    bool alliedToPlayer,
    ulong questUID,
    string foeSymbolName,
    Vector3[] positions,
    bool isInteriorFromClient, // buildings only
    bool isDungeonFromClient,
    int requesterLevel
)
{
    if (!isServer) return;

    int spawnScalingLevel = ResolveServerPlayerLevelForSpawn(this.netId, requesterLevel, "CmdCreateFoes");
    Debug.Log($"[CmdCreateFoes] Spawning {spawnCount} x {foeType} at {position} | questUID={questUID} foeSymbol='{foeSymbolName}' spawnScalingLevel={spawnScalingLevel}");

    // Rebuild quest resource if provided so these become TRUE quest wave spawns
    Foe foeResource = null;
    if (questUID != 0UL && !string.IsNullOrEmpty(foeSymbolName))
    {
        Quest quest = QuestMachine.Instance.GetQuest(questUID);
        if (quest != null)
        {
            foeResource = quest.GetFoe(new Symbol(foeSymbolName));
            if (foeResource == null)
                Debug.LogWarning($"[CmdCreateFoes] Quest {questUID} found, but no Foe '{foeSymbolName}'. Spawning as non-quest enemy.");
        }
        else
        {
            Debug.LogWarning($"[CmdCreateFoes] No active quest with UID {questUID}. Spawning as non-quest enemy.");
        }
    }

    // Spawn normally (your method will NetworkServer.Spawn each)
    GameObject[] spawned = GameObjectHelper.CreateFoeGameObjectsInternal(
        position, foeType, spawnCount, reaction, foeResource, alliedToPlayer, spawnScalingLevel);

    // Immediately move each enemy to the client-computed world positions (no server physics required)
    int c = Mathf.Min(spawned.Length, positions != null ? positions.Length : 0);
    for (int i = 0; i < spawned.Length; i++)
    {
        GameObject enemy = spawned[i];
        if (!enemy) continue;

        if (i < c)
            enemy.transform.position = positions[i]; // hard snap to client-picked spot

        // Mark only enemies whose actual requester was inside a dungeon.
        // Do not infer this from a generic Dungeon object existing in the scene.
        var setupEnemy = enemy.GetComponent<SetupDemoEnemy>();
        if (setupEnemy != null && isDungeonFromClient)
            setupEnemy.isDungeonEnemy = true;

        // Stamp requester context. Dungeon mode uses the requester's synced dungeon
        // world anchor; normal building/exterior behaviour remains unchanged.
        var ewp = enemy.GetComponent<EnemyWorldPosition>();
        if (ewp != null)
        {
            if (isDungeonFromClient)
                ewp.SetDungeonSpawnContext(this.netId, 0, 0, false);
            else
                ewp.SetSpawnContext(isInteriorFromClient, this.netId);

            ewp.intendedSpawnPos = (i < c) ? positions[i] : enemy.transform.position;
            ewp.isCreateFoeWaveSpawn = true;
        }

        // If these are quest wave foes, bind on clients too so their QM tracks kills correctly
        if (foeResource != null)
        {
            var ni = enemy.GetComponent<NetworkIdentity>();
            if (ni != null)
                RpcBindQuestFoe(ni.netId, questUID, foeSymbolName);
        }
    }

    // Keep your existing RPC so clients run their local visual/setup pass
    RpcCreateFoes(position, foeType, spawnCount, reaction, alliedToPlayer);
}



// Compatibility wrapper for older call sites that did not pass a reaction.
// Do not mark this overload as [Command] and do not use optional parameters: Mirror Commands cannot have default arguments.
public void CmdSpawnQuestFoe(
    UnityEngine.Vector3 worldPosition,
    ulong questUID,
    string foeSymbolOriginal,
    MobileTypes foeType,
    int mobileGenderInt,
    int siteTypeInt,
    bool isInteriorAtRequest)
{
    CmdSpawnQuestFoe(
        worldPosition,
        questUID,
        foeSymbolOriginal,
        foeType,
        mobileGenderInt,
        siteTypeInt,
        isInteriorAtRequest,
        MobileReactions.Passive);
}

[Server]
private void Srv_QueueSingleQuestFoeSpawn(
    UnityEngine.Vector3 worldPosition,
    ulong questUID,
    string foeSymbolOriginal,
    MobileTypes foeType,
    int mobileGenderInt,
    int siteTypeInt,
    bool isInteriorAtRequest,
    MobileReactions reaction)
{
    if (!_pendingSingleQuestFoeSpawns.TryGetValue(questUID, out var list))
    {
        list = new List<QueuedSingleQuestFoeSpawn>();
        _pendingSingleQuestFoeSpawns[questUID] = list;
    }

    list.Add(new QueuedSingleQuestFoeSpawn
    {
        worldPosition = worldPosition,
        questUID = questUID,
        foeSymbolOriginal = foeSymbolOriginal,
        foeType = foeType,
        mobileGenderInt = mobileGenderInt,
        siteTypeInt = siteTypeInt,
        isInteriorAtRequest = isInteriorAtRequest,
        reaction = reaction
    });

    if (_pendingQuestRequests.Add(questUID))
    {
        var qns = GetComponent<QuestNetSync>() ?? (connectionToClient != null && connectionToClient.identity ? connectionToClient.identity.GetComponent<QuestNetSync>() : null);
        if (qns != null)
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[CmdSpawnQuestFoe] Server missing quest uid={questUID}. Requesting StartPacket from requester and queuing single quest foe spawn.");
            qns.TargetRequestQuestStartPacket(connectionToClient, questUID);
        }
        else
        {
            Debug.LogWarning($"[CmdSpawnQuestFoe] Server missing quest uid={questUID} but QuestNetSync component not found on requester. Single quest foe spawn will be queued until quest exists.");
        }

        StartCoroutine(Srv_WaitForQuestThenReplaySpawns(questUID));
    }
    else if (Debug.isDebugBuild)
    {
        Debug.Log($"[CmdSpawnQuestFoe] Server missing quest uid={questUID}. Single quest foe spawn queued behind existing StartPacket request.");
    }
}


[Server]
private bool SrvTryGetDungeonAnchorForQuestFoePosition(UnityEngine.Vector3 worldPosition, out int anchorWorldX, out int anchorWorldZ)
{
    anchorWorldX = 0;
    anchorWorldZ = 0;

    DaggerfallDungeon best = null;
    float bestYDistance = float.MaxValue;

    DaggerfallDungeon[] dungeons = GameObject.FindObjectsOfType<DaggerfallDungeon>();
    for (int i = 0; i < dungeons.Length; i++)
    {
        DaggerfallDungeon candidate = dungeons[i];
        if (candidate == null)
            continue;

        float candidateY = Mathf.Abs(candidate.PositionY) > 0.01f ? candidate.PositionY : candidate.transform.position.y;
        float yDistance = Mathf.Abs(worldPosition.y - candidateY);

        if (yDistance < bestYDistance)
        {
            bestYDistance = yDistance;
            best = candidate;
        }
    }

    if (best != null && best.HasDungeonWorldAnchor)
    {
        anchorWorldX = best.DungeonAnchorWorldX;
        anchorWorldZ = best.DungeonAnchorWorldZ;
        return true;
    }

    return false;
}

[Server]
private bool SrvLooksLikeNetworkDungeonPosition(UnityEngine.Vector3 worldPosition)
{
    DaggerfallDungeon[] dungeons = GameObject.FindObjectsOfType<DaggerfallDungeon>();
    for (int i = 0; i < dungeons.Length; i++)
    {
        DaggerfallDungeon candidate = dungeons[i];
        if (candidate == null)
            continue;

        float candidateY = Mathf.Abs(candidate.PositionY) > 0.01f ? candidate.PositionY : candidate.transform.position.y;

        // Network dungeon enemies are usually within the same vertical slot as the dungeon root,
        // but can be tens of Unity units above it depending on block/floor. Keep the band wide.
        if (Mathf.Abs(worldPosition.y - candidateY) <= 120f)
            return true;
    }

    return false;
}

[Server]
private void Srv_SpawnSingleQuestFoeInternal(
    UnityEngine.Vector3 worldPosition,
    ulong questUID,
    string foeSymbolOriginal,
    MobileTypes foeType,
    int mobileGenderInt,
    int siteTypeInt,
    bool isInteriorAtRequest,
    MobileReactions reaction)
{
    try
    {
        // Rebuild quest Foe from UID+symbol (true quest foe on server)
        Foe foeRes = null;
        Quest q = QuestMachine.Instance.GetQuest(questUID);
        if (q != null && !string.IsNullOrEmpty(foeSymbolOriginal))
        {
            foeRes = q.GetFoe(new Symbol(foeSymbolOriginal));
            if (foeRes == null)
                Debug.LogWarning($"[CmdSpawnQuestFoe] Foe '{foeSymbolOriginal}' not found in quest {questUID}. Spawning as non-quest enemy.");
        }
        else
        {
            Debug.LogWarning($"[CmdSpawnQuestFoe] Quest {questUID} not found. Spawning as non-quest enemy.");
        }

        // Spawn at SCENE ROOT, at the PASSED WORLD POSITION (no parenting)
        string displayName = $"Quest Foe [{foeType}]";
        GameObject go = GameObjectHelper.InstantiatePrefab(
            DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject,
            displayName,
            null,
            worldPosition);
        go.transform.SetParent(null, false);
        go.transform.position = worldPosition; // ensure exact world pos

        // Apply enemy settings from the client request. Do not force house-entry quest foes hostile.
        var setupEnemy = go.GetComponent<SetupDemoEnemy>();
        if (setupEnemy != null)
        {
            var gender = (MobileGender)mobileGenderInt;
            setupEnemy.ApplyEnemySettings(foeType, reaction, gender);

            // Make the server's spawn-time motor state match the requested reaction before NetworkServer.Spawn().
            // Otherwise EnemyMotor/hostility SyncVars can copy a stale Hostile value and clients receive hostile=true.
            bool shouldBeHostile = reaction == MobileReactions.Hostile;
            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            if (motor != null)
                motor.IsHostile = shouldBeHostile;

            setupEnemy.SyncedMotorIsHostile = shouldBeHostile;
            setupEnemy.SpawnedMotorIsHostile = shouldBeHostile;
            setupEnemy.CurrentMotorIsHostile = shouldBeHostile;
            setupEnemy.LastAppliedMotorIsHostile = shouldBeHostile;

            Debug.Log($"[CmdSpawnQuestFoe] Applied requested reaction={reaction}, shouldBeHostile={shouldBeHostile} for {displayName}");

            // Capture host-authored health before NetworkServer.Spawn(), same as wave quest foes.
            setupEnemy.ServerCaptureAuthoritativeSpawnHealth();

            var mobileUnit = setupEnemy.GetMobileBillboardChild();
            if (mobileUnit != null && mobileUnit.Enemy.Behaviour != MobileBehaviour.Flying)
                GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
        }

        // Ensure NI exists before spawning
        var ni = go.GetComponent<NetworkIdentity>();
        if (!ni) ni = go.AddComponent<NetworkIdentity>();

        // Mark as quest-spawned & attach quest tracking so quest system recognizes it
        var dfEnemy = go.GetComponent<DaggerfallEnemy>();
        if (dfEnemy)
        {
            dfEnemy.LoadID = DaggerfallUnity.NextUID;
            if (foeRes != null)
                dfEnemy.QuestSpawn = true;
        }

        if (foeRes != null)
        {
            var qrb = go.GetComponent<QuestResourceBehaviour>() ?? go.AddComponent<QuestResourceBehaviour>();
            qrb.AssignResource(foeRes);

            // Multiplayer listen-host fix:
            // A remote/client-requested fixed-marker quest foe must not occupy the shared
            // Foe.QuestResourceBehaviour back-reference on the host. Vanilla QuestResource
            // hot-remove uses that back-reference when the LOCAL player is not at the site.
            // If the host is outside but the client is inside, that immediately destroys
            // the remote client's networked quest foe. QuestResourceBehaviour still has
            // questUID/targetSymbol and will process injured/death/item queues on the enemy.
            bool suppressSharedQuestResourceBackReference = false;
            try
            {
                uint hostLocalNetId = 0U;
                if (NetworkServer.localConnection != null && NetworkServer.localConnection.identity != null)
                    hostLocalNetId = NetworkServer.localConnection.identity.netId;

                suppressSharedQuestResourceBackReference =
                    NetworkServer.active &&
                    hostLocalNetId != 0U &&
                    this.netId != 0U &&
                    this.netId != hostLocalNetId;
            }
            catch { suppressSharedQuestResourceBackReference = false; }

            if (suppressSharedQuestResourceBackReference)
            {
                qrb.SuppressQuestResourceBackReferenceForMultiplayerRemoteFoe(
                    $"remote requester={this.netId}, host local player not authoritative for site cleanup");

                if (foeRes.QuestResourceBehaviour == qrb)
                    foeRes.QuestResourceBehaviour = null;

                Debug.Log($"[QuestFoeMP] Bound remote/client quest foe without shared QuestResourceBehaviour back-reference. questUID={questUID} symbol='{foeSymbolOriginal}' requester={this.netId} enemy='{go.name}'");
            }
            else
            {
                foeRes.QuestResourceBehaviour = qrb;
            }

            foeRes.RearmInjured();
        }

        // World-position metadata from the REQUESTING CLIENT (not host's local state).
        // Building interiors use normal requester+Unity-offset math. Dungeon marker foes
        // must use the dungeon's DF anchor instead of converting underground/local XZ
        // offsets into world distance.
        var ewp = go.GetComponent<EnemyWorldPosition>();
        if (ewp != null)
        {
            if (siteTypeInt == (int)SiteTypes.Dungeon)
            {
                int anchorX, anchorZ;
                bool hasAnchor = SrvTryGetDungeonAnchorForQuestFoePosition(worldPosition, out anchorX, out anchorZ);
                ewp.SetDungeonSpawnContext(this.netId, anchorX, anchorZ, hasAnchor);
                Debug.Log($"[CmdSpawnQuestFoe][DungeonWorldAnchor] requester={this.netId} hasAnchor={hasAnchor} anchorDF={anchorX}/{anchorZ} pos={worldPosition}");
            }
            else
            {
                ewp.SetSpawnContext(isInteriorAtRequest, this.netId);   // stamp BEFORE Spawn()
            }

            ewp.intendedSpawnPos = worldPosition;

            // Badly named flag, but DynamicEnemyAuthority uses this as the "fixed spawn
            // needs settle/resnap" marker. Keep it true for single marker quest foes so
            // host/client copies are held at intendedSpawnPos for a few frames before
            // physics/motor can pull them through stacked interior floors.
            //
            // The reaction/hostility payload above decides whether it is passive or
            // aggressive; this flag should only control spawn settling/resnap.
            ewp.isCreateFoeWaveSpawn = true;

            // Preserve the actual Foe restraint separately from the generic settle
            // marker. Ordinary passive enemies and unrestrained quest waves must not
            // be pinned by the host after their finite spawn settle completes.
            ewp.isFixedQuestFoeRestrained = foeRes != null && foeRes.IsRestrained;
        }

        // Optional: dungeon flagging you already use
        var demo = go.GetComponent<SetupDemoEnemy>();
        if (demo != null)
            demo.isDungeonEnemy = (siteTypeInt == (int)SiteTypes.Dungeon);

        // Network it
        NetworkServer.Spawn(go);
        go.SetActive(true);

        GameManager.Instance?.RaiseOnEnemySpawnEvent(go);

        // Ensure clients bind the quest on their local side too
        var niAfterSpawn = go.GetComponent<NetworkIdentity>();
        if (niAfterSpawn != null && foeRes != null)
            RpcBindQuestFoe(niAfterSpawn.netId, questUID, foeSymbolOriginal);

        Debug.Log($"[CmdSpawnQuestFoe] Spawned networked quest foe ({foeType}) at {worldPosition} | questUID={questUID}, foe={foeSymbolOriginal}, interiorAtRequest={isInteriorAtRequest}, reaction={reaction}");
    }
    catch (System.Exception e)
    {
        Debug.LogError($"[CmdSpawnQuestFoe] Exception: {e}");
    }
}

[Command]
public void CmdSpawnQuestFoe(
    UnityEngine.Vector3 worldPosition,
    ulong questUID,
    string foeSymbolOriginal,
    MobileTypes foeType,
    int mobileGenderInt,
    int siteTypeInt,
    bool isInteriorAtRequest,  // from client, buildings-only
    MobileReactions reaction)
{
    if (!isServer) return;

    // If the host/server does not have the client's quest yet, request the StartPacket
    // and delay spawning. This lets the server rebuild the Foe resource first, so the
    // single quest foe gets QuestResourceBehaviour while preserving the original spawn body.
    if (questUID != 0UL && !string.IsNullOrEmpty(foeSymbolOriginal))
    {
        Quest qCheck = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (qCheck == null)
        {
            Srv_QueueSingleQuestFoeSpawn(worldPosition, questUID, foeSymbolOriginal, foeType, mobileGenderInt, siteTypeInt, isInteriorAtRequest, reaction);
            return;
        }
    }

    Srv_SpawnSingleQuestFoeInternal(worldPosition, questUID, foeSymbolOriginal, foeType, mobileGenderInt, siteTypeInt, isInteriorAtRequest, reaction);
}




[Command]
public void CmdCreateFoesWithPositions(
    Vector3[] positions,
    MobileTypes foeType,
    int spawnCount,
    MobileReactions reaction,
    bool alliedToPlayer,
    ulong questUID,
    string foeSymbolName,
    bool isInteriorAtRequest)
{
    if (!isServer) return;

    int spawnScalingLevel = ResolveServerPlayerLevelForSpawn(this.netId, 0, "CmdCreateFoesWithPositions");

    // If this is a quest wave and server doesn't have the quest yet, request it from this client and queue the spawn.
    if (questUID != 0UL && !string.IsNullOrEmpty(foeSymbolName))
    {
        Quest qCheck = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (qCheck == null)
        {
            // Queue this spawn request
            if (!_pendingQuestFoeSpawns.TryGetValue(questUID, out var list))
            {
                list = new List<QueuedQuestFoeSpawn>();
                _pendingQuestFoeSpawns[questUID] = list;
            }
            list.Add(new QueuedQuestFoeSpawn
            {
                positions = positions,
                foeType = foeType,
                spawnCount = spawnCount,
                reaction = reaction,
                alliedToPlayer = alliedToPlayer,
                requesterLevel = spawnScalingLevel,
                questUID = questUID,
                foeSymbolName = foeSymbolName,
                isInteriorAtRequest = isInteriorAtRequest
            });

            // Ask requester to send StartPacket once per questUID
            if (_pendingQuestRequests.Add(questUID))
            {
                var qns = GetComponent<QuestNetSync>() ?? (connectionToClient != null && connectionToClient.identity ? connectionToClient.identity.GetComponent<QuestNetSync>() : null);
                if (qns != null)
                {
                    if (Debug.isDebugBuild)
                        Debug.Log($"[CmdCreateFoesWithPositions] Server missing quest uid={questUID}. Requesting StartPacket from client netId={netId} and queuing spawn.");
                    qns.TargetRequestQuestStartPacket(connectionToClient, questUID);
                }
                else
                {
                    Debug.LogWarning($"[CmdCreateFoesWithPositions] Server missing quest uid={questUID} but QuestNetSync component not found on requester. Spawns will be queued until quest exists.");
                }

                StartCoroutine(Srv_WaitForQuestThenReplaySpawns(questUID));
            }
            return;
        }
    }


    // Spawn now (quest exists or not a quest wave)
    Srv_SpawnFoesInternal(positions, foeType, spawnCount, reaction, alliedToPlayer, questUID, foeSymbolName, isInteriorAtRequest, spawnScalingLevel);
}









    [Server]
    private IEnumerator Srv_WaitForQuestThenReplaySpawns(ulong questUID)
    {
        // Wait until server has reconstructed quest.
        float timeout = 6f;
        float t = 0f;
        while (t < timeout)
        {
            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
            if (q != null)
                break;
            yield return null;
            t += Time.deltaTime;
        }

        _pendingQuestRequests.Remove(questUID);

        Quest qNow = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
        if (qNow == null)
        {
            Debug.LogWarning($"[Srv_WaitForQuestThenReplaySpawns] Timed out waiting for quest uid={questUID}. Dropping queued quest spawns.");
            _pendingQuestFoeSpawns.Remove(questUID);
            _pendingSingleQuestFoeSpawns.Remove(questUID);
            yield break;
        }

        if (_pendingQuestFoeSpawns.TryGetValue(questUID, out var list) && list != null && list.Count > 0)
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[Srv_WaitForQuestThenReplaySpawns] Quest uid={questUID} now exists. Replaying {list.Count} queued spawn request(s).");

            // Replay in order
            foreach (var req in list)
                Srv_SpawnFoesInternal(req.positions, req.foeType, req.spawnCount, req.reaction, req.alliedToPlayer, req.questUID, req.foeSymbolName, req.isInteriorAtRequest, req.requesterLevel);

            _pendingQuestFoeSpawns.Remove(questUID);
        }

        if (_pendingSingleQuestFoeSpawns.TryGetValue(questUID, out var singleList) && singleList != null && singleList.Count > 0)
        {
            if (Debug.isDebugBuild)
                Debug.Log($"[Srv_WaitForQuestThenReplaySpawns] Quest uid={questUID} now exists. Replaying {singleList.Count} queued single quest foe spawn request(s).");

            foreach (var req in singleList)
            {
                Srv_SpawnSingleQuestFoeInternal(
                    req.worldPosition,
                    req.questUID,
                    req.foeSymbolOriginal,
                    req.foeType,
                    req.mobileGenderInt,
                    req.siteTypeInt,
                    req.isInteriorAtRequest,
                    req.reaction);
            }

            _pendingSingleQuestFoeSpawns.Remove(questUID);
        }
    }

    [Server]
    private void Srv_SpawnFoesInternal(
        Vector3[] positions,
        MobileTypes foeType,
        int spawnCount,
        MobileReactions reaction,
        bool alliedToPlayer,
        ulong questUID,
        string foeSymbolName,
        bool isInteriorAtRequest,
        int spawnScalingLevel)
    {
        // Rebuild quest foe resource if provided (quest should exist now if UID was non-zero)
        Foe foeResource = null;
        if (questUID != 0UL && !string.IsNullOrEmpty(foeSymbolName))
        {
            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
            if (q != null)
                foeResource = q.GetFoe(new Symbol(foeSymbolName));
        }

        int total = Mathf.Clamp(spawnCount, 1, 8);

        for (int i = 0; i < total; i++)
        {
            Vector3 pos = (positions != null && i < positions.Length) ? positions[i] : transform.position;

            string name = $"DaggerfallEnemy [{foeType}]";
            GameObject go = GameObjectHelper.InstantiatePrefab(
                DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject,
                name,
                null,
                pos
            );

            var setupEnemy = go.GetComponent<SetupDemoEnemy>();
            if (setupEnemy != null)
            {
                MobileGender gender = (UnityEngine.Random.value < 0.55f) ? MobileGender.Male : MobileGender.Female;
                setupEnemy.ApplyEnemySettings(foeType, reaction, gender, (byte)(alliedToPlayer ? 1 : 0), alliedToPlayer, MobileTeams.CityWatch, spawnScalingLevel);

                // CreateFoe / send-foe waves are explicitly requested with their reaction.
                // Force the live motor flag before NetworkServer.Spawn(), because EnemyMotor.Start()
                // may not have run yet and the bool default is false/passive. DynamicEnemyAuthority's
                // settle code and CmdRequestApplyEnemySettings both read this live motor flag.
                //
                // This is only in Srv_SpawnFoesInternal(), not the single quest foe path, so passive
                // fixed-marker quest NPCs remain controlled by CmdSpawnQuestFoe's reaction.
                bool shouldBeHostile = reaction == MobileReactions.Hostile;
                EnemyMotor motor = go.GetComponent<EnemyMotor>();
                if (motor != null)
                    motor.IsHostile = shouldBeHostile;

                setupEnemy.SyncedMotorIsHostile = shouldBeHostile;
                setupEnemy.SpawnedMotorIsHostile = shouldBeHostile;
                setupEnemy.CurrentMotorIsHostile = shouldBeHostile;
                setupEnemy.LastAppliedMotorIsHostile = shouldBeHostile;

                Debug.Log($"[CreateFoeHostilityFix][Srv_SpawnFoesInternal] enemy='{go.name}' reaction={reaction} shouldBeHostile={shouldBeHostile}");

                // Wave questfoes must use the host's rolled health as both CurrentHealth and MaxHealth.
                // Capture it before NetworkServer.Spawn() so clients receive it as a spawn SyncVar.
                setupEnemy.ServerCaptureAuthoritativeSpawnHealth();

                var mu = setupEnemy.GetMobileBillboardChild();
                if (mu != null && mu.Enemy.Behaviour != MobileBehaviour.Flying)
                    GameObjectHelper.AlignControllerToGround(go.GetComponent<CharacterController>());
            }

            // ApplyEnemySettings/AlignControllerToGround above can move the enemy's Y.
            // From this point onward, use the ACTUAL post-alignment world position as the
            // authoritative spawn target. Stamping the original client suggestion into
            // EnemyWorldPosition.intendedSpawnPos made later authority resnaps pull the
            // enemy back toward a stale pre-alignment height.
            Vector3 alignedSpawnPos = go.transform.position;
            if ((alignedSpawnPos - pos).sqrMagnitude > 0.0001f)
            {
                Debug.Log(
                    $"[CreateFoeMP][AlignedSpawn] requester={this.netId} " +
                    $"requested={pos} aligned={alignedSpawnPos} foeType={foeType}");
            }

            var ni = go.GetComponent<NetworkIdentity>();
            if (!ni) ni = go.AddComponent<NetworkIdentity>();

            var dfEnemy = go.GetComponent<DaggerfallEnemy>();
            if (dfEnemy)
            {
                dfEnemy.LoadID = DaggerfallUnity.NextUID;
                if (foeResource != null)
                    dfEnemy.QuestSpawn = true;
            }
            if (foeResource != null)
            {
                var qrb = go.GetComponent<QuestResourceBehaviour>() ?? go.AddComponent<QuestResourceBehaviour>();
                qrb.AssignResource(foeResource);
                foeResource.QuestResourceBehaviour = qrb;
                foeResource.RearmInjured();
            }

            var ewp = go.GetComponent<EnemyWorldPosition>();
            if (ewp != null)
            {
                // Wave CreateFoe requests do not carry siteType, so infer dungeon-anchor
                // mode from the spawn Y slot. This protects dungeon CreateFoe waves from
                // the same fake DF-distance/deactivation bug as single marker quest foes.
                if (!isInteriorAtRequest && SrvLooksLikeNetworkDungeonPosition(alignedSpawnPos))
                {
                    int anchorX, anchorZ;
                    bool hasAnchor = SrvTryGetDungeonAnchorForQuestFoePosition(alignedSpawnPos, out anchorX, out anchorZ);
                    ewp.SetDungeonSpawnContext(this.netId, anchorX, anchorZ, hasAnchor);
                    Debug.Log($"[CmdCreateFoesWithPositions][DungeonWorldAnchor] requester={this.netId} hasAnchor={hasAnchor} anchorDF={anchorX}/{anchorZ} pos={alignedSpawnPos}");
                }
                else
                {
                    ewp.SetSpawnContext(isInteriorAtRequest, this.netId);
                }

                ewp.intendedSpawnPos = alignedSpawnPos;
                ewp.isCreateFoeWaveSpawn = true;
            }

            NetworkServer.Spawn(go);
            go.SetActive(true);

            GameManager.Instance?.RaiseOnEnemySpawnEvent(go);

            if (foeResource != null)
                RpcBindQuestFoe(ni.netId, questUID, foeSymbolName);

            // Do not re-apply enemy settings after spawn. That rerolls health/gender and can create client max-health mismatches.
        }

        RpcCreateFoes(Vector3.zero, foeType, spawnCount, reaction, alliedToPlayer);
    }
[ClientRpc]
public void RpcBindQuestFoe(uint enemyNetId, ulong questUID, string foeSymbolName)
{
    if (isServer) return;

    if (!NetworkClient.spawned.TryGetValue(enemyNetId, out NetworkIdentity ni) || ni == null)
    {
        Debug.LogWarning($"[RpcBindQuestFoe] Could not find spawned enemy with netId={enemyNetId} on client.");
        return;
    }

    GameObject go = ni.gameObject;

    // 1) Mark it “quest spawn” on client too (keeps parity with host/SP)
    var dfEnemy = go.GetComponent<DaggerfallEnemy>();
    if (dfEnemy != null)
        dfEnemy.QuestSpawn = true;

    // 2) Attach QuestResourceBehaviour locally and bind to client’s own quest+foe
    var quest = QuestMachine.Instance?.GetQuest(questUID);
    if (quest == null)
    {
        Debug.LogWarning($"[RpcBindQuestFoe] No quest with UID={questUID} on client.");
        return;
    }

    var foeRes = quest.GetFoe(new Symbol(foeSymbolName));
    if (foeRes == null)
    {
        Debug.LogWarning($"[RpcBindQuestFoe] Foe '{foeSymbolName}' not found in quest {questUID} on client.");
        return;
    }

    var qrb = go.GetComponent<QuestResourceBehaviour>();
    if (qrb == null) qrb = go.AddComponent<QuestResourceBehaviour>();

    qrb.AssignResource(foeRes);
    foeRes.QuestResourceBehaviour = qrb;
    foeRes.RearmInjured();

    Debug.Log($"[RpcBindQuestFoe] Bound quest foe on client: netId={enemyNetId}, questUID={questUID}, symbol='{foeSymbolName}'.");
}



private IEnumerator ApplySettingsWithDelay(SetupDemoEnemy setupEnemy, MobileTypes foeType, MobileReactions reaction, bool alliedToPlayer)
{
    // Obsolete no-op. Wave questfoes are configured once on the server before NetworkServer.Spawn().
    // Re-applying settings later would reroll health/gender and can cause client-side max-health mismatches.
    yield break;
}


[ClientRpc]
public void RpcCreateFoes(Vector3 position, MobileTypes foeType, int spawnCount, MobileReactions reaction, bool alliedToPlayer)
{
    if (isServer) return;

    // Legacy notification only. Do not call ApplyEnemySettings() here.
    // Network-spawned enemies request full authoritative settings through SetupDemoEnemy.OnStartClient() -> CmdRequestApplyEnemySettings().
    // Calling ApplyEnemySettings() from this RPC lets the client roll its own health/max.
    Debug.Log($"[RpcCreateFoes] Legacy notification received for {spawnCount} x {foeType}; waiting for authoritative enemy settings RPC.");
}


[Command]
public void CmdRequestApplyEnemySettings(uint enemyNetId)
{
    if (!isServer) return;

    if (NetworkServer.spawned.TryGetValue(enemyNetId, out NetworkIdentity enemyIdentity) &&
        enemyIdentity.TryGetComponent(out SetupDemoEnemy enemy) &&
        enemy.TryGetComponent(out DaggerfallEntityBehaviour entityBehaviour) &&
        entityBehaviour.Entity is EnemyEntity enemyEntity)
    {
        MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;

        Debug.Log($"[SpawnHealthDbg][ServerSend] enemy='{enemy.name}' netId={enemyNetId} foeType={enemy.EnemyType} reaction={enemy.EnemyReaction} gender={enemy.EnemyGender} cur={enemyEntity.CurrentHealth} min={mobileEnemy.MinHealth} max={mobileEnemy.MaxHealth}");

        EnemyMotor sendMotor = enemy.GetComponent<EnemyMotor>();
        bool initialIsHostile = (sendMotor != null) ? sendMotor.IsHostile : (enemy.EnemyReaction == MobileReactions.Hostile);

        TargetApplyEnemySettings(
            connectionToClient,
            enemyNetId,
            enemy.EnemyType,
            enemy.EnemyReaction,
            enemy.AlliedToPlayer,
            enemy.EnemyGender,
            enemyEntity.CurrentHealth,
            mobileEnemy.Team,
            mobileEnemy.ID,
            mobileEnemy.Level,
            mobileEnemy.MinHealth,
            mobileEnemy.MaxHealth,
            mobileEnemy.HasRangedAttack1,
            mobileEnemy.HasRangedAttack2,
            mobileEnemy.CastsMagic,
            initialIsHostile
        );
    }
}




[TargetRpc]
public void TargetApplyEnemySettings(
    NetworkConnection target,
    uint netId,
    MobileTypes foeType,
    MobileReactions reaction,
    bool alliedToPlayer,
    MobileGender gender,
    int currentHealth,
    MobileTeams team,
    int enemyID,
    int level,
    int minHealth,
    int maxHealth,
    bool rangedAttack1,
    bool rangedAttack2,
    bool castsMagic,
    bool isHostile
)
{
    if (isServer) return;
    ApplyEnemySettingsPayload(netId, foeType, reaction, alliedToPlayer, gender, currentHealth, team, enemyID, level, minHealth, maxHealth, rangedAttack1, rangedAttack2, castsMagic, isHostile, "TargetRpc");
}

[ClientRpc]
public void RpcApplyEnemySettings(
    uint netId,
    MobileTypes foeType,
    MobileReactions reaction,
    bool alliedToPlayer,
    MobileGender gender,
    int currentHealth,
    MobileTeams team,
    int enemyID,
    int level,
    int minHealth,
    int maxHealth,
    bool rangedAttack1,
    bool rangedAttack2,
    bool castsMagic,
    bool isHostile
)
{
    if (isServer) return;
    ApplyEnemySettingsPayload(netId, foeType, reaction, alliedToPlayer, gender, currentHealth, team, enemyID, level, minHealth, maxHealth, rangedAttack1, rangedAttack2, castsMagic, isHostile, "ClientRpc");
}

private void ApplyEnemySettingsPayload(
    uint netId,
    MobileTypes foeType,
    MobileReactions reaction,
    bool alliedToPlayer,
    MobileGender gender,
    int currentHealth,
    MobileTeams team,
    int enemyID,
    int level,
    int minHealth,
    int maxHealth,
    bool rangedAttack1,
    bool rangedAttack2,
    bool castsMagic,
    bool isHostile,
    string source
)
{
    bool found = false;

    foreach (var enemy in FindObjectsOfType<SetupDemoEnemy>())
    {
        var networkIdentity = enemy.GetComponent<NetworkIdentity>();
        if (networkIdentity == null || networkIdentity.netId != netId)
            continue;

        found = true;

        int authoritativeMax = Mathf.Max(maxHealth, currentHealth, 1);
        int authoritativeCurrent = currentHealth;
        if (authoritativeCurrent <= 0 && authoritativeMax > 0)
        {
            Debug.LogWarning($"[SpawnHealthDbg][ClientApplyBadCurrent] source={source} enemy='{enemy.name}' netId={netId} serverCurrent={currentHealth} serverMax={maxHealth}; using max as spawn current to avoid local 0 HP init.");
            authoritativeCurrent = authoritativeMax;
        }
        authoritativeCurrent = Mathf.Clamp(authoritativeCurrent, 1, authoritativeMax);

        if (enemy.HasReceivedInitialServerSettings())
        {
            if (enemy.TryGetComponent(out DaggerfallEntityBehaviour initializedEntityBehaviour) &&
                initializedEntityBehaviour.Entity is EnemyEntity initializedEnemyEntity)
            {
                if (initializedEnemyEntity.CurrentHealth <= 0 && authoritativeCurrent > 0)
                {
                    Debug.LogWarning($"[SpawnHealthDbg][ClientApplyRepairAlreadyInitialized] source={source} enemy='{enemy.name}' netId={netId} localCur={initializedEnemyEntity.CurrentHealth} authoritativeCur={authoritativeCurrent} authoritativeMax={authoritativeMax}");
                    initializedEntityBehaviour.ApplyAuthoritativeHealthCurrentAndMax(authoritativeCurrent, authoritativeMax);
                }
                else
                {
                    Debug.Log($"[SpawnHealthDbg][ClientApplySkipped] source={source} enemy='{enemy.name}' netId={netId} reason=already-initialized localCur={initializedEnemyEntity.CurrentHealth}");
                }
            }
            break;
        }

        if (enemy.TryGetComponent(out DaggerfallEntityBehaviour entityBehaviourBefore) &&
            entityBehaviourBefore.Entity is EnemyEntity enemyEntityBefore)
        {
            MobileEnemy beforeMe = enemyEntityBefore.MobileEnemy;
            Debug.Log($"[SpawnHealthDbg][ClientBeforeApply] source={source} enemy='{enemy.name}' netId={netId} cur={enemyEntityBefore.CurrentHealth} min={beforeMe.MinHealth} max={beforeMe.MaxHealth} gender={enemy.EnemyGender} reaction={enemy.EnemyReaction}");
        }
        else
        {
            Debug.Log($"[SpawnHealthDbg][ClientBeforeApply] source={source} enemy='{enemy.name}' netId={netId} entity=missing");
        }

        enemy.SetPendingAuthoritativeSpawnHealth(authoritativeCurrent);
        enemy.CaptureOwnerHostilityBeforeInitialSettings();
        enemy.ApplyEnemySettings(foeType, reaction, gender, (byte)(alliedToPlayer ? 1 : 0), alliedToPlayer, team);

        if (enemy.TryGetComponent(out DaggerfallEntityBehaviour entityBehaviourAfter) &&
            entityBehaviourAfter.Entity is EnemyEntity enemyEntityAfter)
        {
            MobileEnemy mobileEnemy = enemyEntityAfter.MobileEnemy;
            mobileEnemy.Team = team;
            mobileEnemy.ID = enemyID;
            mobileEnemy.Level = level;
            mobileEnemy.MinHealth = Mathf.Min(minHealth, authoritativeMax);
            mobileEnemy.MaxHealth = authoritativeMax;
            mobileEnemy.HasRangedAttack1 = rangedAttack1;
            mobileEnemy.HasRangedAttack2 = rangedAttack2;
            mobileEnemy.CastsMagic = castsMagic;

            enemyEntityAfter.SetMobileEnemy(mobileEnemy);
            enemyEntityAfter.MaxHealth = authoritativeMax;
            enemyEntityAfter.CurrentHealth = authoritativeCurrent;
            entityBehaviourAfter.ApplyAuthoritativeHealthCurrentAndMax(authoritativeCurrent, authoritativeMax);
            enemy.MarkInitialServerSettingsApplied();

            EnemyMotor motor = enemy.GetComponent<EnemyMotor>();
            if (motor != null)
                motor.hasBowAttack = (rangedAttack1 && (!castsMagic || rangedAttack2));

            enemy.ApplyInitialSyncedHostility(isHostile);

            MobileEnemy afterMe = enemyEntityAfter.MobileEnemy;
            Debug.Log($"[SpawnHealthDbg][ClientAfterApply] source={source} enemy='{enemy.name}' netId={netId} foeType={foeType} reaction={reaction} gender={gender} cur={enemyEntityAfter.CurrentHealth} min={afterMe.MinHealth} max={afterMe.MaxHealth} authoritativeCur={authoritativeCurrent} authoritativeMax={authoritativeMax} hostile={isHostile}");
        }
        else
        {
            Debug.LogWarning($"[SpawnHealthDbg][ClientApplyEntityMissingRetryWillContinue] source={source} enemy='{enemy.name}' netId={netId}. RequestEnemySettingsWithRetry will keep requesting until entity exists.");
        }

        break;
    }

    if (!found)
    {
        Debug.LogWarning($"[SpawnHealthDbg][ClientApplyEnemyNotFound] source={source} netId={netId}. Enemy object may not have spawned on this client yet; retry coroutine should request again.");
    }
}



// MP guardspawning 2.0
// ==================================================
// ==================================================
// Server-side anti-spam & per-requester cap
// ==================================================
static readonly Dictionary<uint, float> guardCooldownUntil = new Dictionary<uint, float>(); // requesterNetId -> next allowed time
public const float SERVER_GUARD_COOLDOWN_SECONDS = 3f;

public const int SERVER_MAX_GUARDS_PER_REQUESTER = 6;   // hard cap per requesting player
public const int SERVER_MAX_GUARDS_NEAR_RADIUS   = 30;  // optional local area cap (meters)
public const int SERVER_MAX_GUARDS_NEAR_COUNT    = 8;   // optional: also limit per area

// Extra failsafe layer. These do not allow extra guards; they delete excess guards if
// a rare request-spam/timing issue already created too many.
const int SERVER_GUARD_GLOBAL_BUFFER = 4;
const float SERVER_GUARD_OVERFLOW_WATCHDOG_SECONDS = 1.0f;

// ---------- helpers ----------
static bool IsCityWatchGuard(GameObject go)
{
    var setup = go.GetComponent<SetupDemoEnemy>();
    if (setup != null)
        return setup.EnemyType == MobileTypes.Knight_CityWatch;

    var beh = go.GetComponent<DaggerfallEntityBehaviour>();
    if (beh && beh.Entity is EnemyEntity ee)
        return (MobileTypes)ee.MobileEnemy.ID == MobileTypes.Knight_CityWatch;

    return go.name.Contains("Knight_CityWatch");
}

static int Server_CountGuardsForRequester(uint requesterNetId)
{
    if (!NetworkServer.active) return 0;
    int count = 0;

    foreach (var kv in NetworkServer.spawned)
    {
        var go = kv.Value ? kv.Value.gameObject : null;
        if (!go || !go.activeInHierarchy) continue;
        if (!IsCityWatchGuard(go)) continue;

        var ewp = go.GetComponent<EnemyWorldPosition>();
        if (ewp != null && ewp.requesterNetId == requesterNetId)
            count++;
    }

    Debug.Log($"[GUARDS] Server_CountGuardsForRequester requester={requesterNetId} => {count}");
    return count;
}

static int Server_CountGuardsNear(Vector3 center, float radius)
{
    if (!NetworkServer.active) return 0;

    float r2 = radius * radius;
    int count = 0;

    foreach (var kv in NetworkServer.spawned)
    {
        var go = kv.Value ? kv.Value.gameObject : null;
        if (!go || !go.activeInHierarchy) continue;
        if (!IsCityWatchGuard(go)) continue;

        Vector3 d = go.transform.position - center;
        if (d.sqrMagnitude <= r2)
            count++;
    }

    Debug.Log($"[GUARDS] Server_CountGuardsNear center={center} radius={radius} => {count}");
    return count;
}


static List<GameObject> Server_GetNetworkCityWatchGuards()
{
    List<GameObject> guards = new List<GameObject>();

    if (!NetworkServer.active)
        return guards;

    foreach (var kv in NetworkServer.spawned)
    {
        NetworkIdentity identity = kv.Value;
        GameObject go = identity != null ? identity.gameObject : null;

        if (go == null || !go.activeInHierarchy)
            continue;

        if (!IsCityWatchGuard(go))
            continue;

        guards.Add(go);
    }

    return guards;
}

static bool Server_TryGetGuardRequester(GameObject guard, out uint requesterNetId)
{
    requesterNetId = 0;

    if (guard == null)
        return false;

    EnemyWorldPosition ewp = guard.GetComponent<EnemyWorldPosition>();
    if (ewp == null)
        return false;

    // requesterNetId 0 is a valid host/legacy requester in some MP paths.
    // Do not treat it as "no requester" here, otherwise host-spawned guards can
    // escape the per-requester overflow culler and survive until the global cap.
    requesterNetId = ewp.requesterNetId;
    return true;
}

static int Server_DestroyGuard(GameObject guard, string reason)
{
    if (!NetworkServer.active || guard == null)
        return 0;

    NetworkIdentity ni = guard.GetComponent<NetworkIdentity>();
    if (ni == null)
        return 0;

    Debug.Log($"[GUARDS][Cull] Destroying excess guard '{guard.name}' netId={ni.netId} reason={reason} pos={guard.transform.position}");
    NetworkServer.Destroy(guard);
    return 1;
}

static int Server_CullExcessGuardsForRequester(uint requesterNetId, Vector3 keepNear, string reason)
{
    if (!NetworkServer.active)
        return 0;

    List<GameObject> guards = Server_GetNetworkCityWatchGuards();
    guards.RemoveAll(g =>
    {
        uint owner;
        return !Server_TryGetGuardRequester(g, out owner) || owner != requesterNetId;
    });

    if (guards.Count <= SERVER_MAX_GUARDS_PER_REQUESTER)
        return 0;

    // Keep the guards closest to the requester/current request position.
    guards.Sort((a, b) =>
    {
        float da = (a.transform.position - keepNear).sqrMagnitude;
        float db = (b.transform.position - keepNear).sqrMagnitude;
        return da.CompareTo(db);
    });

    int removed = 0;
    for (int i = SERVER_MAX_GUARDS_PER_REQUESTER; i < guards.Count; i++)
        removed += Server_DestroyGuard(guards[i], $"{reason}: requester {requesterNetId} over cap {SERVER_MAX_GUARDS_PER_REQUESTER}");

    return removed;
}

static int Server_CullExcessGuardsNear(Vector3 center, string reason)
{
    if (!NetworkServer.active)
        return 0;

    float r2 = SERVER_MAX_GUARDS_NEAR_RADIUS * SERVER_MAX_GUARDS_NEAR_RADIUS;
    List<GameObject> guards = Server_GetNetworkCityWatchGuards();
    guards.RemoveAll(g => (g.transform.position - center).sqrMagnitude > r2);

    if (guards.Count <= SERVER_MAX_GUARDS_NEAR_COUNT)
        return 0;

    // Keep the closest guards near the request/player. Remove farthest overflow.
    guards.Sort((a, b) =>
    {
        float da = (a.transform.position - center).sqrMagnitude;
        float db = (b.transform.position - center).sqrMagnitude;
        return da.CompareTo(db);
    });

    int removed = 0;
    for (int i = SERVER_MAX_GUARDS_NEAR_COUNT; i < guards.Count; i++)
        removed += Server_DestroyGuard(guards[i], $"{reason}: area over cap {SERVER_MAX_GUARDS_NEAR_COUNT}");

    return removed;
}

static int Server_CullGlobalExcessGuards(Vector3 keepNear, string reason)
{
    if (!NetworkServer.active)
        return 0;

    List<GameObject> guards = Server_GetNetworkCityWatchGuards();

    int activePlayers = 0;
    foreach (var conn in NetworkServer.connections.Values)
    {
        if (conn != null && conn.identity != null)
            activePlayers++;
    }

    activePlayers = Mathf.Max(1, activePlayers);
    int maxGlobalGuards = SERVER_MAX_GUARDS_PER_REQUESTER * activePlayers + SERVER_GUARD_GLOBAL_BUFFER;

    if (guards.Count <= maxGlobalGuards)
        return 0;

    // Keep requester-owned guards first and keep guards closest to a player/request.
    // Orphan/unowned guards are most likely from a bad timing path, so they are removed first.
    guards.Sort((a, b) =>
    {
        uint requesterA;
        uint requesterB;
        bool hasRequesterA = Server_TryGetGuardRequester(a, out requesterA);
        bool hasRequesterB = Server_TryGetGuardRequester(b, out requesterB);

        if (hasRequesterA != hasRequesterB)
            return hasRequesterA ? -1 : 1;

        float da = (a.transform.position - keepNear).sqrMagnitude;
        float db = (b.transform.position - keepNear).sqrMagnitude;
        return da.CompareTo(db);
    });

    int removed = 0;
    for (int i = maxGlobalGuards; i < guards.Count; i++)
        removed += Server_DestroyGuard(guards[i], $"{reason}: global over cap {maxGlobalGuards}");

    return removed;
}

static void Server_CullGuardOverflow(uint requesterNetId, Vector3 center, string reason)
{
    if (!NetworkServer.active)
        return;

    int removed = 0;
    removed += Server_CullExcessGuardsForRequester(requesterNetId, center, reason);
    removed += Server_CullExcessGuardsNear(center, reason);
    removed += Server_CullGlobalExcessGuards(center, reason);

    if (removed > 0)
    {
        Debug.Log($"[GUARDS][Cull] Removed {removed} excess guard(s). requester={requesterNetId} reason={reason}");

        if (requesterNetId != 0)
            guardCooldownUntil[requesterNetId] = Time.unscaledTime + SERVER_GUARD_COOLDOWN_SECONDS;
    }
}

IEnumerator Server_GuardOverflowWatchdog()
{
    yield return new WaitForSeconds(2f);

    while (isServer && NetworkServer.active)
    {
        bool checkedAnyPlayer = false;

        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn == null || conn.identity == null)
                continue;

            PlayerMultiplayer pm = conn.identity.GetComponent<PlayerMultiplayer>();
            if (pm == null)
                continue;

            checkedAnyPlayer = true;
            Server_CullGuardOverflow(pm.netId, pm.transform.position, "watchdog");
        }

        // Host-originated guards from older/local paths can have requesterNetId 0.
        // Cull that bucket too, using the host player as the keep-near point when present.
        if (NetworkServer.localConnection != null && NetworkServer.localConnection.identity != null)
            Server_CullExcessGuardsForRequester(0, NetworkServer.localConnection.identity.transform.position, "watchdog-host-zero-requester");

        // Fallback if there are spawned guards but no valid player identity was found yet.
        if (!checkedAnyPlayer)
            Server_CullGuardOverflow(0, Vector3.zero, "watchdog-no-player");

        yield return new WaitForSeconds(SERVER_GUARD_OVERFLOW_WATCHDOG_SECONDS);
    }

    if (serverGuardOverflowWatchdogOwner == this)
        serverGuardOverflowWatchdogOwner = null;
}

// Spawn exactly one guard at pos/forward and stamp requester/interior BEFORE Spawn()
GameObject Server_SpawnOneGuard(Vector3 pos, Vector3 forward, bool isInteriorAtRequest, uint requesterNetId)
{
    Debug.Log($"[GUARDS] Server_SpawnOneGuard requested by={requesterNetId} pos={pos} interior={isInteriorAtRequest}");

    GameObject[] guards = GameObjectHelper.CreateFoeGameObjects(pos, MobileTypes.Knight_CityWatch, 1);
    if (guards == null || guards.Length == 0 || guards[0] == null)
    {
        Debug.LogWarning("[GUARDS] Server_SpawnOneGuard FAILED: CreateFoeGameObjects returned null/empty.");
        return null;
    }

    GameObject g = guards[0];
    g.transform.forward = forward;

    var ewp = g.GetComponent<EnemyWorldPosition>();
    if (ewp != null)
        ewp.SetSpawnContext(isInteriorAtRequest, requesterNetId); // SyncVars set before spawn

    var motor = g.GetComponent<EnemyMotor>();
    if (motor != null)
        motor.GiveUpTimer *= 3;

    NetworkIdentity ni = g.GetComponent<NetworkIdentity>();
    if (ni == null)
        ni = g.AddComponent<NetworkIdentity>();

    // GameObjectHelper.CreateFoeGameObjects() can already network-spawn enemies in MP.
    // Only spawn here if this object has not received a netId yet.
    if (ni.netId == 0)
        NetworkServer.Spawn(g);

    g.SetActive(true);

    Debug.Log($"[GUARDS] Server_SpawnOneGuard SPAWNED guard netId={ni.netId} requester={requesterNetId} at {g.transform.position}");

    // Visual/setup parity (optional): your existing RPC
    RpcCreateFoes(pos, MobileTypes.Knight_CityWatch, 1, MobileReactions.Hostile, false);

    return g;
}

// ============= client provides exact positions (SP-identical placement) =============
[Command]
public void CmdRequestGuardSpawnAtPositions(Vector3[] positions, Vector3[] forwards, bool isInteriorAtRequest)
{
    if (!isServer) return;

    uint who = netId;
    float now = Time.unscaledTime;

    Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions from={who} interior={isInteriorAtRequest} count={positions?.Length ?? 0}");

    Vector3 requestCenter = (positions != null && positions.Length > 0) ? positions[0] : transform.position;
    Server_CullGuardOverflow(who, requestCenter, "pre CmdRequestGuardSpawnAtPositions");

    // per-requester cooldown
    if (guardCooldownUntil.TryGetValue(who, out float until) && now < until)
    {
        Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions BLOCKED by cooldown (now={now}, until={until}) for={who}");
        return;
    }

    int perRequesterExisting = Server_CountGuardsForRequester(who);
    int perRequesterRemaining = Mathf.Max(0, SERVER_MAX_GUARDS_PER_REQUESTER - perRequesterExisting);
    Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions per-requester existing={perRequesterExisting}, remaining={perRequesterRemaining}");
    if (perRequesterRemaining <= 0)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions ABORT: per-requester cap reached for={who}");
        return;
    }

    int ask = Mathf.Min(
        positions != null ? positions.Length : 0,
        forwards  != null ? forwards.Length  : 0,
        perRequesterRemaining
    );
    if (ask <= 0)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions ABORT: ask={ask} (positions/forwards null or cap 0).");
        return;
    }

    // Optional area cap around request center
    Vector3 center = requestCenter;
    int areaExisting = Server_CountGuardsNear(center, SERVER_MAX_GUARDS_NEAR_RADIUS);
    int areaRemaining = Mathf.Max(0, SERVER_MAX_GUARDS_NEAR_COUNT - areaExisting);
    int toSpawn = Mathf.Min(ask, areaRemaining > 0 ? areaRemaining : ask);

    Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions areaExisting={areaExisting}, areaRemaining={areaRemaining}, toSpawn={toSpawn}");

    for (int i = 0; i < toSpawn; i++)
    {
        Vector3 pos = positions[i];
        Vector3 fwd = forwards[i].sqrMagnitude < 1e-6f ? Vector3.forward : forwards[i].normalized;

        // drop slightly to floor if possible (robustness)
        if (Physics.Raycast(new Ray(pos + Vector3.up * 3f, Vector3.down), out RaycastHit hit, 6f))
            pos = hit.point + Vector3.up * 0.1f;

        Server_SpawnOneGuard(pos, fwd, isInteriorAtRequest, who);
    }

    Server_CullGuardOverflow(who, requestCenter, "post CmdRequestGuardSpawnAtPositions");

    guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS;
    Debug.Log($"[GUARDS] CmdRequestGuardSpawnAtPositions DONE: spawned={toSpawn} for requester={who}, next allowed at={guardCooldownUntil[who]}");
}

// ============= Legacy: “wave near requester” (kept; now enforces per-requester cap) =============
[Command]
public void CmdRequestGuardSpawn(bool immediateSpawn, Vector3 requesterPos, Vector3 requesterForward, bool isInteriorAtRequest)
{
    if (!isServer) return;

    uint who = netId;
    float now = Time.unscaledTime;

    Debug.Log($"[GUARDS] CmdRequestGuardSpawn from={who} immediate={immediateSpawn} pos={requesterPos} interior={isInteriorAtRequest}");

    Server_CullGuardOverflow(who, requesterPos, "pre CmdRequestGuardSpawn");

    // per-requester cooldown
    if (guardCooldownUntil.TryGetValue(who, out float until) && now < until)
    {
        Debug.Log($"[GUARDS] CmdRequestGuardSpawn BLOCKED by cooldown (now={now}, until={until}) for={who}");
        return;
    }

    // per-requester cap
    int perRequesterExisting  = Server_CountGuardsForRequester(who);
    int perRequesterRemaining = Mathf.Max(0, SERVER_MAX_GUARDS_PER_REQUESTER - perRequesterExisting);
    Debug.Log($"[GUARDS] CmdRequestGuardSpawn per-requester existing={perRequesterExisting}, remaining={perRequesterRemaining}");
    if (perRequesterRemaining <= 0)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdRequestGuardSpawn ABORT: per-requester cap reached for={who}");
        return;
    }

    // optional area cap
    int areaExisting  = Server_CountGuardsNear(requesterPos, SERVER_MAX_GUARDS_NEAR_RADIUS);
    int areaRemaining = Mathf.Max(0, SERVER_MAX_GUARDS_NEAR_COUNT - areaExisting);
    Debug.Log($"[GUARDS] CmdRequestGuardSpawn areaExisting={areaExisting}, areaRemaining={areaRemaining}");

    // roughly match classic counts but clamp by caps
    int want   = immediateSpawn ? UnityEngine.Random.Range(2, 6) : UnityEngine.Random.Range(2, 4);
    int budget = Mathf.Max(0, Mathf.Min(want, perRequesterRemaining, areaRemaining > 0 ? areaRemaining : want));
    Debug.Log($"[GUARDS] CmdRequestGuardSpawn want={want}, budget={budget}");
    if (budget <= 0)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdRequestGuardSpawn ABORT: budget=0 for={who}");
        return;
    }

    // Build a small ring so they don't stack
    Vector3[] positions = Server_BuildGuardRingPositions(requesterPos, requesterForward.normalized, budget, isInteriorAtRequest ? 3.5f : 5.5f);
    for (int i = 0; i < budget; i++)
    {
        Vector3 pos = positions[i];
        Vector3 fwd = (requesterPos - pos).normalized;
        Server_SpawnOneGuard(pos, fwd, isInteriorAtRequest, who);
    }

    Server_CullGuardOverflow(who, requesterPos, "post CmdRequestGuardSpawn");

    guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS;
    Debug.Log($"[GUARDS] CmdRequestGuardSpawn DONE: spawned={budget} for requester={who}, next allowed at={guardCooldownUntil[who]}");
}

static Vector3[] Server_BuildGuardRingPositions(Vector3 center, Vector3 forward, int count, float radius)
{
    var result = new Vector3[count];
    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
    if (right.sqrMagnitude < 1e-5f) right = Vector3.right;

    float step = 360f / Mathf.Max(1, count);
    for (int i = 0; i < count; i++)
    {
        float angle = step * i * Mathf.Deg2Rad;
        Vector3 dir = (Mathf.Cos(angle) * right + Mathf.Sin(angle) * forward).normalized;
        Vector3 p = center + dir * radius;

        if (Physics.Raycast(new Ray(p + Vector3.up * 3f, Vector3.down), out RaycastHit hit, 6f))
            p = hit.point + Vector3.up * 0.1f;

        result[i] = p;
    }
    return result;
}



// ============= Single-guard exact spawn =============
[Command]
public void CmdSpawnCityGuard(Vector3 position, Vector3 forward, bool isInteriorAtRequest)
{
    if (!isServer) return;

    uint who = netId;
    float now = Time.unscaledTime;

    Debug.Log($"[GUARDS] CmdSpawnCityGuard from={who} pos={position} interior={isInteriorAtRequest}");

    Server_CullGuardOverflow(who, position, "pre CmdSpawnCityGuard");

    // short per-requester cooldown
    if (guardCooldownUntil.TryGetValue(who, out float until) && now < until)
    {
        Debug.Log($"[GUARDS] CmdSpawnCityGuard BLOCKED by cooldown (now={now}, until={until}) for={who}");
        return;
    }

    // per-requester cap
    int perRequesterExisting  = Server_CountGuardsForRequester(who);
    if (perRequesterExisting >= SERVER_MAX_GUARDS_PER_REQUESTER)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdSpawnCityGuard ABORT: per-requester cap reached (existing={perRequesterExisting}) for={who}");
        return;
    }

    // optional area cap
    int areaExisting = Server_CountGuardsNear(position, SERVER_MAX_GUARDS_NEAR_RADIUS);
    if (areaExisting >= SERVER_MAX_GUARDS_NEAR_COUNT)
    {
        guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.5f;
        Debug.Log($"[GUARDS] CmdSpawnCityGuard ABORT: area cap reached (existing={areaExisting}) at pos={position} for={who}");
        return;
    }

    // drop to floor a bit for robustness
    if (Physics.Raycast(new Ray(position + Vector3.up * 3f, Vector3.down), out RaycastHit hit, 6f))
        position = hit.point + Vector3.up * 0.1f;

    Server_SpawnOneGuard(position, forward.sqrMagnitude < 1e-6f ? Vector3.forward : forward.normalized, isInteriorAtRequest, who);

    Server_CullGuardOverflow(who, position, "post CmdSpawnCityGuard");

    guardCooldownUntil[who] = now + SERVER_GUARD_COOLDOWN_SECONDS * 0.75f;
    Debug.Log($"[GUARDS] CmdSpawnCityGuard DONE for requester={who}, next allowed at={guardCooldownUntil[who]}");
}





//dungeon sync related line----------------------------------------------------------------------------------


// A pure client loading a dungeon save can retain the save's SP PlayerGPS world
// coordinates even after its Unity player has entered the host dungeon. Keep only
// that saved-dungeon conversion pinned to the host-authored dungeon entrance until
// the player leaves. This never changes the player's Unity dungeon-local position.
private bool pureClientSavedDungeonAnchorActive = false;
private bool pureClientSavedDungeonAnchorObservedInside = false;
private int pureClientSavedDungeonAnchorWorldX = 0;
private int pureClientSavedDungeonAnchorWorldZ = 0;
private float pureClientSavedDungeonAnchorStartedAt = 0f;
private float nextPureClientSavedDungeonAnchorCheck = 0f;

private void ActivatePureClientSavedDungeonAnchor(
    int anchorWorldX,
    int anchorWorldZ,
    string reason)
{
    if (!isLocalPlayer || !NetworkClient.active || NetworkServer.active)
        return;

    pureClientSavedDungeonAnchorActive = true;
    pureClientSavedDungeonAnchorObservedInside = false;
    pureClientSavedDungeonAnchorWorldX = anchorWorldX;
    pureClientSavedDungeonAnchorWorldZ = anchorWorldZ;
    pureClientSavedDungeonAnchorStartedAt = Time.realtimeSinceStartup;
    nextPureClientSavedDungeonAnchorCheck = 0f;

    PositionMultiplayer positionMultiplayer = GetComponent<PositionMultiplayer>();
    if (positionMultiplayer != null)
    {
        positionMultiplayer.SetNetworkDungeonCoordinateOverride(
            true,
            anchorWorldX,
            anchorWorldZ,
            reason + "-activate");
    }

    PreparePureClientSavedDungeonAnchor(
        anchorWorldX,
        anchorWorldZ,
        reason + "-activate",
        true);
}

// Party rendezvous reaches the same state as saved-dungeon conversion: the pure
// client is already inside a host-authored dungeon, but PlayerGPS may still publish
// the exterior/dungeon coordinate it travelled from. Reuse the proven maintained
// anchor hold so the server and both clients see the traveler at the destination.
public void ActivatePureClientPartyDungeonAnchor(
    int anchorWorldX,
    int anchorWorldZ,
    string reason)
{
    if (!isLocalPlayer || !NetworkClient.active || NetworkServer.active)
        return;

    ActivatePureClientSavedDungeonAnchor(
        anchorWorldX,
        anchorWorldZ,
        reason + "-party-rendezvous");
}

private void MaintainPureClientSavedDungeonAnchor()
{
    if (!pureClientSavedDungeonAnchorActive ||
        !isLocalPlayer ||
        !NetworkClient.active ||
        NetworkServer.active)
        return;

    float now = Time.realtimeSinceStartup;
    if (now < nextPureClientSavedDungeonAnchorCheck)
        return;

    nextPureClientSavedDungeonAnchorCheck = now + 0.10f;

    PlayerEnterExit playerEnterExit = GameManager.Instance != null
        ? GameManager.Instance.PlayerEnterExit
        : null;

    bool insideDungeon = playerEnterExit != null && playerEnterExit.IsPlayerInsideDungeon;
    if (insideDungeon)
    {
        pureClientSavedDungeonAnchorObservedInside = true;

        DaggerfallDungeon currentDungeon = playerEnterExit.Dungeon;
        if (currentDungeon != null && currentDungeon.HasDungeonWorldAnchor)
        {
            pureClientSavedDungeonAnchorWorldX = currentDungeon.DungeonAnchorWorldX;
            pureClientSavedDungeonAnchorWorldZ = currentDungeon.DungeonAnchorWorldZ;
        }

        PreparePureClientSavedDungeonAnchor(
            pureClientSavedDungeonAnchorWorldX,
            pureClientSavedDungeonAnchorWorldZ,
            "saved-dungeon-anchor-hold",
            false);
        return;
    }

    // Before TargetEnterSavedDungeon completes, keep the anchor alive through the
    // load transition. Once the client has actually occupied the dungeon, leaving it
    // ends the override immediately. The timeout prevents a rejected request from
    // pinning exterior coordinates forever.
    if (pureClientSavedDungeonAnchorObservedInside ||
        now - pureClientSavedDungeonAnchorStartedAt > 30f)
    {
        Debug.Log($"[NetworkDungeonConversion][ClientAnchor] Released saved-dungeon anchor after exit/timeout. anchor={pureClientSavedDungeonAnchorWorldX}/{pureClientSavedDungeonAnchorWorldZ}");

        PositionMultiplayer positionMultiplayer = GetComponent<PositionMultiplayer>();
        if (positionMultiplayer != null)
        {
            positionMultiplayer.SetNetworkDungeonCoordinateOverride(
                false,
                0,
                0,
                "saved-dungeon-anchor-exit");
        }

        pureClientSavedDungeonAnchorActive = false;
        pureClientSavedDungeonAnchorObservedInside = false;
    }
    else
    {
        PreparePureClientSavedDungeonAnchor(
            pureClientSavedDungeonAnchorWorldX,
            pureClientSavedDungeonAnchorWorldZ,
            "saved-dungeon-anchor-pending",
            false);
    }
}


private static bool TryBuildTeleportPcDungeonWorldAnchor(DFLocation location, out int anchorWorldX, out int anchorWorldZ)
{
    anchorWorldX = 0;
    anchorWorldZ = 0;

    try
    {
        if (!location.Loaded)
            return false;

        Vector3 exactEntranceLocal;
        if (StreamingWorld.TryGetDungeonEntranceWorldCoordinates(location, out anchorWorldX, out anchorWorldZ, out exactEntranceLocal))
        {
            Debug.Log($"[TeleportPcMP][ExactEntrance] Built exact dungeon entrance anchor for '{location.RegionName}/{location.Name}' anchor={anchorWorldX}/{anchorWorldZ} localEntrance={exactEntranceLocal}");
            return true;
        }

        // Last-resort fallback only. This keeps the quest from softlocking if a rare
        // location cannot be probed, but the normal expected path is the exact entrance
        // coordinate above.
        DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel(
            (int)location.MapTableData.Longitude,
            location.MapTableData.Latitude);
        DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);

        anchorWorldX = worldPos.X;
        anchorWorldZ = worldPos.Y;
        Debug.LogWarning($"[TeleportPcMP][ExactEntrance] Exact entrance probe failed for '{location.RegionName}/{location.Name}'. Falling back to coarse anchor={anchorWorldX}/{anchorWorldZ}.");
        return true;
    }
    catch (System.Exception ex)
    {
        Debug.LogWarning($"[TeleportPcMP][ExactEntrance] Failed to build explicit dungeon anchor for '{location.Name}'. error={ex}");
        return false;
    }
}

// Saved-dungeon conversion needs the same request-time coordinate guarantee as
// TeleportPc, but must not touch TeleportPc's pending marker/world-context state.
// LoadGame and the normal PositionMultiplayer publisher can retain the SP save's
// coordinate, so publish the explicit host dungeon anchor without changing PlayerGPS.
private void PreparePureClientSavedDungeonAnchor(
    int anchorWorldX,
    int anchorWorldZ,
    string reason,
    bool forcePublish)
{
    if (!isLocalPlayer || !NetworkClient.active || NetworkServer.active)
        return;

    try
    {
        if (GameManager.Instance == null)
            return;

        // Do not write PlayerGPS or StreamingWorld here. DFU deliberately keeps exterior
        // world context while the player is inside, and repeatedly replacing that saved
        // context with the dungeon entrance triggers exterior terrain-description messages
        // such as "A loose paving stone...". Enemy distance and remote-player visibility
        // now use the network dungeon anchor directly, so only PositionMultiplayer needs
        // this saved-conversion hold.

        GameObject localPlayerObject = GameManager.Instance.PlayerObject;
        if (localPlayerObject != null)
        {
            transform.position = localPlayerObject.transform.position;
            transform.rotation = localPlayerObject.transform.rotation;
        }

        PositionMultiplayer positionMultiplayer = GetComponent<PositionMultiplayer>();
        if (positionMultiplayer != null)
        {
            // Make the anchor the normal publisher's source of truth. This avoids two
            // coroutines alternately sending the dungeon anchor and stale PlayerGPS.
            positionMultiplayer.SetNetworkDungeonCoordinateOverride(
                true,
                anchorWorldX,
                anchorWorldZ,
                reason);

            if (forcePublish)
                positionMultiplayer.ForceSendCurrentCoordinatesNow(reason + "-force");
        }
        else if (forcePublish)
            Debug.LogWarning($"[NetworkDungeonConversion][ClientAnchor] PositionMultiplayer was not found. reason={reason}");

        if (forcePublish)
            Debug.Log($"[NetworkDungeonConversion][ClientAnchor] Applied exact saved-dungeon anchor={anchorWorldX}/{anchorWorldZ} forcePublish={forcePublish} reason={reason}");
    }
    catch (System.Exception ex)
    {
        Debug.LogWarning($"[NetworkDungeonConversion][ClientAnchor] Failed to prepare anchor={anchorWorldX}/{anchorWorldZ}. reason={reason} error={ex}");
    }
}

[Server]
private bool SrvForceRequesterPositionToTeleportPcAnchor(uint requesterNetId, int anchorWorldX, int anchorWorldZ, string reason)
{
    PositionMultiplayer targetPosition = null;

    PlayerMultiplayer[] players = FindObjectsOfType<PlayerMultiplayer>();
    for (int i = 0; i < players.Length; i++)
    {
        PlayerMultiplayer player = players[i];
        if (player == null || player.netId != requesterNetId)
            continue;

        targetPosition = player.GetComponent<PositionMultiplayer>();
        break;
    }

    if (targetPosition == null)
    {
        PositionMultiplayer[] positions = FindObjectsOfType<PositionMultiplayer>();
        for (int i = 0; i < positions.Length; i++)
        {
            PositionMultiplayer pos = positions[i];
            if (pos == null)
                continue;

            NetworkIdentity ni = pos.GetComponent<NetworkIdentity>();
            if (ni != null && ni.netId == requesterNetId)
            {
                targetPosition = pos;
                break;
            }
        }
    }

    if (targetPosition == null)
    {
        Debug.LogWarning($"[TeleportPcMP][Anchor] Could not force requester PositionMultiplayer. requester={requesterNetId} anchor={anchorWorldX}/{anchorWorldZ} reason={reason}");
        return false;
    }

    int oldX = targetPosition.x;
    int oldZ = targetPosition.z;
    targetPosition.x = anchorWorldX;
    targetPosition.z = anchorWorldZ;

    Debug.Log($"[TeleportPcMP][Anchor] Forced requester PositionMultiplayer before dungeon generation. requester={requesterNetId} old={oldX}/{oldZ} new={anchorWorldX}/{anchorWorldZ} reason={reason}");
    return true;
}

[Server]
private void SrvApplyTeleportPcDungeonAnchor(DaggerfallDungeon dungeon, uint requesterNetId, int anchorWorldX, int anchorWorldZ, string reason)
{
    if (dungeon == null)
        return;

    dungeon.RequesterNetId = requesterNetId;
    dungeon.HasDungeonWorldAnchor = true;
    dungeon.DungeonAnchorWorldX = anchorWorldX;
    dungeon.DungeonAnchorWorldZ = anchorWorldZ;

    Debug.Log($"[TeleportPcMP][Anchor] Applied explicit TeleportPc dungeon anchor. dungeon='{dungeon.name}' requester={requesterNetId} anchor={anchorWorldX}/{anchorWorldZ} reason={reason}");
}

[Server]
private int SrvRebindDungeonEnemiesToTeleportPcAnchor(DaggerfallDungeon dungeon, uint requesterNetId, int anchorWorldX, int anchorWorldZ, string reason)
{
    if (dungeon == null)
        return 0;

    float dungeonY = Mathf.Abs(dungeon.PositionY) > 0.01f ? dungeon.PositionY : dungeon.transform.position.y;
    const float sameDungeonYBand = 145f;

    int count = 0;
    EnemyWorldPosition[] worldPositions = FindObjectsOfType<EnemyWorldPosition>();
    for (int i = 0; i < worldPositions.Length; i++)
    {
        EnemyWorldPosition ewp = worldPositions[i];
        if (ewp == null)
            continue;

        // Never touch player/avatar objects even if the visual prefab shares enemy components.
        if (ewp.GetComponentInParent<PlayerMultiplayer>() != null)
            continue;

        Transform enemyTransform = ewp.transform;
        bool childOfDungeon = enemyTransform.IsChildOf(dungeon.transform);
        bool sameDungeonYSlot = enemyTransform.position.y <= -250f && Mathf.Abs(enemyTransform.position.y - dungeonY) <= sameDungeonYBand;

        SetupDemoEnemy setup = ewp.GetComponent<SetupDemoEnemy>();
        bool markedDungeonEnemy = ewp.isDungeonSpawn || (setup != null && setup.isDungeonEnemy);
        bool hasEnemyComponent = ewp.GetComponent<DaggerfallEnemy>() != null;

        // Imported RDB enemies are normally children of the generated dungeon blocks.
        // Some MP paths move enemies to scene root, so also accept enemies in the same network dungeon Y slot.
        if (!childOfDungeon && !(sameDungeonYSlot && (markedDungeonEnemy || hasEnemyComponent)))
            continue;

        int oldX = ewp.worldX;
        int oldZ = ewp.worldZ;

        // Metadata-only repair: do not move the enemy, do not change authority, do not touch DynamicEnemyAuthority.
        ewp.SetDungeonSpawnContext(requesterNetId, anchorWorldX, anchorWorldZ, true);
        count++;

        if (oldX != anchorWorldX || oldZ != anchorWorldZ)
        {
            Debug.Log($"[TeleportPcMP][AnchorRebind] Enemy '{ewp.name}' DF anchor {oldX}/{oldZ} -> {anchorWorldX}/{anchorWorldZ}. requester={requesterNetId} reason={reason} childOfDungeon={childOfDungeon} sameYSlot={sameDungeonYSlot} y={enemyTransform.position.y:F1} dungeonY={dungeonY:F1}");
        }
    }

    Debug.Log($"[TeleportPcMP][AnchorRebind] Rebound {count} dungeon enemy anchor(s). dungeon='{dungeon.name}' requester={requesterNetId} anchor={anchorWorldX}/{anchorWorldZ} reason={reason}");
    return count;
}

[Command(requiresAuthority = false)]
public void CmdRequestDungeonFromHost(string regionName, string locationName, uint requesterNetId, NetworkConnectionToClient sender = null)
{
    uint resolvedRequesterNetId = ResolveDungeonRequesterNetId(requesterNetId, sender, "CmdRequestDungeonFromHost");

    HandleDungeonRequestFromHost(
        regionName,
        locationName,
        resolvedRequesterNetId,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false,
        Vector3.zero,
        false,
        0,
        0,
        false,
        Vector3.zero);
}

[Command(requiresAuthority = false)]
public void CmdRequestDungeonFromHostWithGenerationSpec(
    string regionName,
    string locationName,
    uint requesterNetId,
    int requesterLevel,
    int monsterSeed,
    int texture0,
    int texture1,
    int texture2,
    int texture3,
    int texture4,
    int texture5,
    NetworkConnectionToClient sender = null)
{
    uint resolvedRequesterNetId = ResolveDungeonRequesterNetId(requesterNetId, sender, "CmdRequestDungeonFromHostWithGenerationSpec");

    HandleDungeonRequestFromHost(
        regionName,
        locationName,
        resolvedRequesterNetId,
        true,
        requesterLevel,
        monsterSeed,
        texture0,
        texture1,
        texture2,
        texture3,
        texture4,
        texture5,
        false,
        Vector3.zero,
        false,
        0,
        0,
        false,
        Vector3.zero);
}

// Saved/local dungeon conversion request. This carries the requesting player's
// dungeon-local position plus an optional saved door/action snapshot. The host
// accepts that snapshot only if this request creates the dungeon; later loaders
// cannot replace the live first-creator state. Enemies and loot remain freshly
// generated/host-owned exactly as before.
public void RequestSavedDungeonFromHost(
    DFLocation location,
    Vector3 dungeonLocalPosition,
    int anchorWorldX,
    int anchorWorldZ,
    string reason,
    int savedPlayerLevel = 0,
    string initialSavedActionState = null)
{
    if (!isLocalPlayer)
    {
        Debug.LogWarning($"[NetworkDungeonConversion] Refusing saved dungeon request on non-local PlayerMultiplayer netId={netId}. reason={reason}");
        return;
    }

    try
    {
        int requestAnchorWorldX = anchorWorldX;
        int requestAnchorWorldZ = anchorWorldZ;

        // Only the pure-client saved-load path needs this repair. Preserve the host
        // branch exactly as it was because host conversion already has authoritative
        // PlayerGPS/StreamingWorld state at generation time.
        if (NetworkClient.active && !NetworkServer.active)
        {
            int exactAnchorWorldX;
            int exactAnchorWorldZ;
            if (TryBuildTeleportPcDungeonWorldAnchor(
                location,
                out exactAnchorWorldX,
                out exactAnchorWorldZ))
            {
                if (exactAnchorWorldX != anchorWorldX || exactAnchorWorldZ != anchorWorldZ)
                {
                    Debug.Log($"[NetworkDungeonConversion][ClientAnchor] Replaced stale saved anchor={anchorWorldX}/{anchorWorldZ} with exact entrance={exactAnchorWorldX}/{exactAnchorWorldZ}. reason={reason}");
                }

                requestAnchorWorldX = exactAnchorWorldX;
                requestAnchorWorldZ = exactAnchorWorldZ;
            }

            // This is intentionally the last coordinate operation before the reliable
            // saved-dungeon Command. It fixes both inputs used during first generation:
            // the requester's PositionMultiplayer x/z on the server and the explicit
            // dungeon anchor carried by the Command itself.
            ActivatePureClientSavedDungeonAnchor(
                requestAnchorWorldX,
                requestAnchorWorldZ,
                reason + "-immediately-before-command");
        }

        // During LoadGame(), PlayerEntity has been reset but its saved data is not restored
        // until after the world/dungeon respawn completes. Use the saved level supplied by
        // SaveLoadManager so a first-time recreation gets the same normal enemy tier.
        int requesterLevel = Mathf.Clamp(
            savedPlayerLevel > 0 ? savedPlayerLevel : DaggerfallDungeon.GetLocalPlayerLevelFallback(),
            1,
            100);
        int[] requesterTextureTable = DaggerfallDungeon.BuildLocationDungeonTextureTable(location);
        int monsterSeed = DaggerfallDungeon.BuildStableDungeonMonsterSeed(location);

        Debug.Log($"[NetworkDungeonConversion] Requesting host dungeon '{location.RegionName}/{location.Name}' requester={netId} local={dungeonLocalPosition} anchor={requestAnchorWorldX}/{requestAnchorWorldZ} level={requesterLevel} reason={reason}");

        CmdRequestSavedDungeonFromHostWithGenerationSpec(
            location.RegionName,
            location.Name,
            netId,
            dungeonLocalPosition,
            requestAnchorWorldX,
            requestAnchorWorldZ,
            requesterLevel,
            monsterSeed,
            requesterTextureTable[0],
            requesterTextureTable[1],
            requesterTextureTable[2],
            requesterTextureTable[3],
            requesterTextureTable[4],
            requesterTextureTable[5],
            initialSavedActionState ?? string.Empty);
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"[NetworkDungeonConversion] Failed to build saved dungeon generation spec. error={ex}");
        PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
        if (playerEnterExit != null)
            playerEnterExit.FailPendingNetworkDungeonConversion("request-generation-spec-failed");
    }
}

[Command(requiresAuthority = false)]
public void CmdRequestSavedDungeonFromHostWithGenerationSpec(
    string regionName,
    string locationName,
    uint requesterNetId,
    Vector3 dungeonLocalPosition,
    int anchorWorldX,
    int anchorWorldZ,
    int requesterLevel,
    int monsterSeed,
    int texture0,
    int texture1,
    int texture2,
    int texture3,
    int texture4,
    int texture5,
    string initialSavedActionState,
    NetworkConnectionToClient sender = null)
{
    uint resolvedRequesterNetId = ResolveDungeonRequesterNetId(requesterNetId, sender, "CmdRequestSavedDungeonFromHostWithGenerationSpec");

    HandleDungeonRequestFromHost(
        regionName,
        locationName,
        resolvedRequesterNetId,
        true,
        requesterLevel,
        monsterSeed,
        texture0,
        texture1,
        texture2,
        texture3,
        texture4,
        texture5,
        false,
        Vector3.zero,
        true,
        anchorWorldX,
        anchorWorldZ,
        true,
        dungeonLocalPosition,
        initialSavedActionState);
}


private static bool IsTeleportPcStartMarkerSentinel(Vector3 marker)
{
    return float.IsPositiveInfinity(marker.x) &&
           float.IsPositiveInfinity(marker.y) &&
           float.IsPositiveInfinity(marker.z);
}

// TeleportPc-specific dungeon request. This is deliberately separate from the normal
// clicked-door request because the client must be auto-entered through the client
// TargetRpc dungeon path and then moved to the quest marker after the dungeon has
// been generated at the host-authored Y slot. Do not let the client create/use a
// local SP dungeon path here.
public void RequestTeleportPcDungeonFromHost(DFLocation location, Vector3 markerLocalPosition, string reason)
{
    int anchorWorldX;
    int anchorWorldZ;
    bool hasExplicitAnchor = TryBuildTeleportPcDungeonWorldAnchor(location, out anchorWorldX, out anchorWorldZ);
    RequestTeleportPcDungeonFromHost(location, markerLocalPosition, hasExplicitAnchor, anchorWorldX, anchorWorldZ, reason);
}

public void RequestTeleportPcDungeonFromHost(DFLocation location, Vector3 markerLocalPosition, int anchorWorldX, int anchorWorldZ, string reason)
{
    // TeleportPc.cs already calculated the same destination world coordinates it uses for the host path.
    // Send those exact coordinates to the host instead of making the server read the requester's still-stale
    // PositionMultiplayer value from the tavern/house where the quest action fired.
    RequestTeleportPcDungeonFromHost(location, markerLocalPosition, true, anchorWorldX, anchorWorldZ, reason);
}

private void RequestTeleportPcDungeonFromHost(DFLocation location, Vector3 markerLocalPosition, bool hasExplicitAnchor, int anchorWorldX, int anchorWorldZ, string reason)
{
    if (!isLocalPlayer)
    {
        Debug.LogWarning($"[TeleportPcMP][ClientRequest] Refusing request on non-local PlayerMultiplayer netId={netId} reason={reason}");
        return;
    }

    try
    {
        int requesterLevel = DaggerfallDungeon.GetLocalPlayerLevelFallback();
        int[] requesterTextureTable = DaggerfallDungeon.BuildLocationDungeonTextureTable(location);
        int monsterSeed = DaggerfallDungeon.BuildStableDungeonMonsterSeed(location);

        Debug.Log($"[TeleportPcMP][ClientRequest] Requesting network dungeon '{location.RegionName}/{location.Name}' markerLocal={markerLocalPosition} requester={netId} level={requesterLevel} seed={monsterSeed} explicitAnchor={anchorWorldX}/{anchorWorldZ} hasAnchor={hasExplicitAnchor} reason={reason}");

        CmdRequestTeleportPcDungeonFromHostWithGenerationSpec(
            location.RegionName,
            location.Name,
            netId,
            markerLocalPosition,
            requesterLevel,
            monsterSeed,
            requesterTextureTable[0],
            requesterTextureTable[1],
            requesterTextureTable[2],
            requesterTextureTable[3],
            requesterTextureTable[4],
            requesterTextureTable[5],
            hasExplicitAnchor,
            anchorWorldX,
            anchorWorldZ);
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"[TeleportPcMP][ClientRequest] Failed to build dungeon generation spec. Falling back to legacy TeleportPc request. error={ex}");
        CmdRequestTeleportPcDungeonFromHost(location.RegionName, location.Name, netId, markerLocalPosition, hasExplicitAnchor, anchorWorldX, anchorWorldZ);
    }
}

[Command(requiresAuthority = false)]
public void CmdRequestTeleportPcDungeonFromHost(
    string regionName,
    string locationName,
    uint requesterNetId,
    Vector3 markerLocalPosition,
    bool hasExplicitAnchor,
    int anchorWorldX,
    int anchorWorldZ,
    NetworkConnectionToClient sender = null)
{
    uint resolvedRequesterNetId = ResolveDungeonRequesterNetId(requesterNetId, sender, "CmdRequestTeleportPcDungeonFromHost");

    HandleDungeonRequestFromHost(
        regionName,
        locationName,
        resolvedRequesterNetId,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        true,
        markerLocalPosition,
        hasExplicitAnchor,
        anchorWorldX,
        anchorWorldZ,
        false,
        Vector3.zero);
}

[Command(requiresAuthority = false)]
public void CmdRequestTeleportPcDungeonFromHostWithGenerationSpec(
    string regionName,
    string locationName,
    uint requesterNetId,
    Vector3 markerLocalPosition,
    int requesterLevel,
    int monsterSeed,
    int texture0,
    int texture1,
    int texture2,
    int texture3,
    int texture4,
    int texture5,
    bool hasExplicitAnchor,
    int anchorWorldX,
    int anchorWorldZ,
    NetworkConnectionToClient sender = null)
{
    uint resolvedRequesterNetId = ResolveDungeonRequesterNetId(requesterNetId, sender, "CmdRequestTeleportPcDungeonFromHostWithGenerationSpec");

    HandleDungeonRequestFromHost(
        regionName,
        locationName,
        resolvedRequesterNetId,
        true,
        requesterLevel,
        monsterSeed,
        texture0,
        texture1,
        texture2,
        texture3,
        texture4,
        texture5,
        true,
        markerLocalPosition,
        hasExplicitAnchor,
        anchorWorldX,
        anchorWorldZ,
        false,
        Vector3.zero);
}

private uint ResolveDungeonRequesterNetId(uint suppliedRequesterNetId, NetworkConnectionToClient sender, string caller)
{
    uint senderNetId = 0U;

    if (sender != null && sender.identity != null)
        senderNetId = sender.identity.netId;

    // With requiresAuthority=false, a build client can accidentally call this Command on
    // the host player's PlayerMultiplayer if FindObjectOfType() returns the wrong object.
    // In that case, the only reliable requester is the command sender's identity.
    if (senderNetId != 0U)
    {
        if (suppliedRequesterNetId != 0U && suppliedRequesterNetId != senderNetId)
        {
            Debug.LogWarning($"[DungeonRequesterResolve] {caller}: supplied requester={suppliedRequesterNetId} did not match sender={senderNetId}. Using sender identity.");
        }
        else
        {
            Debug.Log($"[DungeonRequesterResolve] {caller}: using sender requester={senderNetId}.");
        }

        return senderNetId;
    }

    if (suppliedRequesterNetId != 0U)
    {
        Debug.Log($"[DungeonRequesterResolve] {caller}: sender unavailable, using supplied requester={suppliedRequesterNetId}.");
        return suppliedRequesterNetId;
    }

    // Host/local direct fallback only. Avoid silently converting a remote client request
    // into host requester when Mirror supplied a sender identity.
    if (connectionToClient != null && connectionToClient.identity != null && connectionToClient.identity.netId != 0U)
    {
        uint connectionNetId = connectionToClient.identity.netId;
        Debug.LogWarning($"[DungeonRequesterResolve] {caller}: supplied requester was 0 and sender unavailable. Falling back to connection identity={connectionNetId}.");
        return connectionNetId;
    }

    if (netId != 0U)
    {
        Debug.LogWarning($"[DungeonRequesterResolve] {caller}: supplied requester was 0 and no sender/connection identity was available. Falling back to this.netId={netId}.");
        return netId;
    }

    Debug.LogError($"[DungeonRequesterResolve] {caller}: could not resolve dungeon requester netId. Dungeon enemies may fallback to closest player.");
    return 0U;
}

[Server]
private bool TryGetRequesterPlayerAndConnection(uint requesterNetId, out PlayerMultiplayer requesterPlayer, out NetworkConnection requesterConn)
{
    requesterPlayer = null;
    requesterConn = null;

    PlayerMultiplayer[] players = FindObjectsOfType<PlayerMultiplayer>();
    for (int i = 0; i < players.Length; i++)
    {
        PlayerMultiplayer player = players[i];
        if (player == null || player.netId != requesterNetId)
            continue;

        requesterPlayer = player;
        requesterConn = player.connectionToClient;
        return requesterConn != null;
    }

    return false;
}



[Server]
private int ResolveServerPlayerLevelForSpawn(uint requesterNetId, int suppliedLevel, string reason)
{
    int syncedLevel = 0;

    if (requesterNetId != 0U)
    {
        PlayerMultiplayer[] players = FindObjectsOfType<PlayerMultiplayer>();
        for (int i = 0; i < players.Length; i++)
        {
            PlayerMultiplayer player = players[i];
            if (player == null || player.netId != requesterNetId)
                continue;

            if (player.PlayerMPLevel > 0)
                syncedLevel = player.PlayerMPLevel;
            break;
        }
    }

    // Prefer an explicit level carried by the current request. PlayerMPLevel is still useful
    // for legacy/no-spec paths, but it can be at its default value briefly after connect.
    int level = suppliedLevel > 0 ? suppliedLevel : syncedLevel;

    if (level <= 0 && GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
        level = GameManager.Instance.PlayerEntity.Level;

    level = Mathf.Clamp(level > 0 ? level : 1, 1, 100);
    Debug.Log($"[RequesterLevelResolve] {reason}: requesterNetId={requesterNetId} suppliedLevel={suppliedLevel} syncedLevel={syncedLevel} resolvedLevel={level}");
    return level;
}

[Server]
private bool IsUsableHostNetworkDungeon(DaggerfallDungeon dungeon)
{
    if (dungeon == null || !dungeon.IsNetworkDungeonInstance)
        return false;

    NetworkIdentity identity = dungeon.GetComponent<NetworkIdentity>();
    return identity != null && identity.netId != 0U;
}

[Server]
private DaggerfallDungeon.DungeonNetworkData BuildDungeonNetworkDataForRequester(
    DaggerfallDungeon dungeon,
    uint requesterNetId)
{
    DaggerfallDungeon.DungeonNetworkData data = new DaggerfallDungeon.DungeonNetworkData
    {
        ID = dungeon.Summary.ID,
        RegionName = dungeon.Summary.RegionName,
        LocationName = dungeon.Summary.LocationName,
        LocationType = dungeon.Summary.LocationType,
        DungeonType = dungeon.Summary.DungeonType,
        PositionY = dungeon.PositionY,
        RequesterNetId = requesterNetId,
        DungeonNetId = dungeon.netId,
        IsNetworkDungeonInstance = dungeon.IsNetworkDungeonInstance,
        DungeonInstanceId = dungeon.DungeonInstanceId
    };

    dungeon.WriteGenerationSpecToNetworkData(ref data);

    // The server dungeon and its enemies already use this anchor, but clients also
    // need it on their local DaggerfallDungeon copy. Without this field transfer,
    // ApplyNetworkDungeonIdentityFromData() receives HasDungeonWorldAnchor=false and
    // PositionMultiplayer falls back to the SP save's frozen PlayerGPS coordinates.
    // That makes the client logically far from the host/enemies even though everyone
    // is physically inside the same network dungeon.
    dungeon.WriteWorldAnchorToNetworkData(ref data);

    return data;
}

[Server]
private bool SendDungeonEntryToRequester(
    uint requesterNetId,
    DaggerfallDungeon.DungeonNetworkData dungeonData,
    bool teleportPcRequest,
    Vector3 teleportPcLocalMarker,
    bool savedLocalPositionRequest,
    Vector3 savedDungeonLocalPosition)
{
    PlayerMultiplayer requesterPlayer;
    NetworkConnection requesterConn;
    if (!TryGetRequesterPlayerAndConnection(requesterNetId, out requesterPlayer, out requesterConn))
    {
        Debug.LogError("[CmdRequestDungeonFromHost] ERROR: Could not find matching requester player/connection for netId: " + requesterNetId);
        return false;
    }

    if (savedLocalPositionRequest)
        requesterPlayer.TargetEnterSavedDungeon(requesterConn, dungeonData, savedDungeonLocalPosition);
    else if (teleportPcRequest)
        requesterPlayer.TargetEnterTeleportPcDungeon(requesterConn, dungeonData, teleportPcLocalMarker);
    else
        requesterPlayer.TargetEnterDungeon(requesterConn, dungeonData);

    return true;
}

[Server]
private IEnumerator WaitForExistingDungeonGeneration(
    DaggerfallDungeon dungeon,
    uint requesterNetId,
    bool teleportPcRequest,
    Vector3 teleportPcLocalMarker,
    bool savedLocalPositionRequest,
    Vector3 savedDungeonLocalPosition)
{
    float started = Time.realtimeSinceStartup;
    while (dungeon != null && (!dungeon.isSet || !dungeon.InitialSavedActionStateReady))
    {
        if (Time.realtimeSinceStartup - started > 20f)
        {
            Debug.LogError($"[CmdRequestDungeonFromHost] Timed out waiting for existing dungeon generation. requester={requesterNetId}");
            PlayerMultiplayer requesterPlayer;
            NetworkConnection requesterConn;
            if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out requesterPlayer, out requesterConn))
                requesterPlayer.TargetSavedDungeonRequestFailed(requesterConn, "existing-dungeon-generation-timeout");
            yield break;
        }

        yield return null;
    }

    if (dungeon == null || !IsUsableHostNetworkDungeon(dungeon))
    {
        PlayerMultiplayer requesterPlayer;
        NetworkConnection requesterConn;
        if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out requesterPlayer, out requesterConn))
            requesterPlayer.TargetSavedDungeonRequestFailed(requesterConn, "existing-dungeon-disappeared");
        yield break;
    }

    if (savedLocalPositionRequest && dungeon.HasDungeonWorldAnchor)
    {
        // Same rule as the already-generated existing-dungeon branch: once the host's
        // first-created dungeon is ready, its world anchor replaces the requester's
        // pre-conversion/local-dungeon coordinate on the server.
        SrvForceRequesterPositionToTeleportPcAnchor(
            requesterNetId,
            dungeon.DungeonAnchorWorldX,
            dungeon.DungeonAnchorWorldZ,
            "existing-dungeon-generated-authoritative-anchor-before-target");
    }

    DaggerfallDungeon.DungeonNetworkData data = BuildDungeonNetworkDataForRequester(dungeon, requesterNetId);
    SendDungeonEntryToRequester(
        requesterNetId,
        data,
        teleportPcRequest,
        teleportPcLocalMarker,
        savedLocalPositionRequest,
        savedDungeonLocalPosition);
}

private void HandleDungeonRequestFromHost(
    string regionName,
    string locationName,
    uint requesterNetId,
    bool hasGenerationSpec,
    int requesterLevel,
    int monsterSeed,
    int texture0,
    int texture1,
    int texture2,
    int texture3,
    int texture4,
    int texture5,
    bool teleportPcRequest,
    Vector3 teleportPcLocalMarker,
    bool hasExplicitTeleportPcAnchor,
    int teleportPcAnchorWorldX,
    int teleportPcAnchorWorldZ,
    bool savedLocalPositionRequest,
    Vector3 savedDungeonLocalPosition,
    string initialSavedActionState = null)
{
    string requestKind = savedLocalPositionRequest ? "SavedLocalPosition" : (teleportPcRequest ? "TeleportPc" : "NormalEntry");
    int resolvedRequesterLevel = ResolveServerPlayerLevelForSpawn(requesterNetId, requesterLevel, "HandleDungeonRequestFromHost-" + requestKind);
    bool vampireCemeteryWakeRequest = teleportPcRequest && IsTeleportPcStartMarkerSentinel(teleportPcLocalMarker);
    Debug.Log($"[CmdRequestDungeonFromHost] Host received dungeon request: {regionName} - {locationName}. kind={requestKind}, hasGenerationSpec={hasGenerationSpec}, requesterNetId={requesterNetId}, suppliedRequesterLevel={requesterLevel}, resolvedRequesterLevel={resolvedRequesterLevel}, monsterSeed={monsterSeed}, textures=[{texture0},{texture1},{texture2},{texture3},{texture4},{texture5}], vampireWake={vampireCemeteryWakeRequest}");

    DFLocation sceneLocation = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(regionName, locationName);
    if (!sceneLocation.Loaded || !sceneLocation.HasDungeon)
    {
        Debug.LogError($"[CmdRequestDungeonFromHost] ERROR: Dungeon {regionName} - {locationName} could not be found.");

        PlayerMultiplayer failedRequester;
        NetworkConnection failedConnection;
        if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out failedRequester, out failedConnection))
            failedRequester.TargetSavedDungeonRequestFailed(failedConnection, "dungeon-location-not-found");
        return;
    }

    // Saved-dungeon conversion must reproduce the same location texture table as a
    // normal DFU SetDungeon()/UseLocationDungeonTextureTable() call on the host. Do not
    // let a requester-side stale/current-location context choose the visual table for a
    // newly recreated saved dungeon. This does not alter an already existing dungeon;
    // the first creator remains authoritative and the existing-dungeon branch below
    // continues to return its original generation spec unchanged.
    if (savedLocalPositionRequest)
    {
        int[] hostLocationTextureTable =
            DaggerfallDungeon.BuildLocationDungeonTextureTable(sceneLocation);
        int hostLocationMonsterSeed =
            DaggerfallDungeon.BuildStableDungeonMonsterSeed(sceneLocation);

        bool requesterTextureMismatch =
            !hasGenerationSpec ||
            texture0 != hostLocationTextureTable[0] ||
            texture1 != hostLocationTextureTable[1] ||
            texture2 != hostLocationTextureTable[2] ||
            texture3 != hostLocationTextureTable[3] ||
            texture4 != hostLocationTextureTable[4] ||
            texture5 != hostLocationTextureTable[5];

        if (requesterTextureMismatch || monsterSeed != hostLocationMonsterSeed)
        {
            Debug.LogWarning($"[NetworkDungeonConversion][GenerationSpec] Replacing requester saved-dungeon spec with host location spec. dungeon='{sceneLocation.RegionName}/{sceneLocation.Name}' mapId={sceneLocation.MapTableData.MapId} locationId={sceneLocation.Dungeon.RecordElement.Header.LocationId} requesterTextures=[{texture0},{texture1},{texture2},{texture3},{texture4},{texture5}] hostTextures=[{hostLocationTextureTable[0]},{hostLocationTextureTable[1]},{hostLocationTextureTable[2]},{hostLocationTextureTable[3]},{hostLocationTextureTable[4]},{hostLocationTextureTable[5]}] requesterSeed={monsterSeed} hostSeed={hostLocationMonsterSeed}");
        }

        texture0 = hostLocationTextureTable[0];
        texture1 = hostLocationTextureTable[1];
        texture2 = hostLocationTextureTable[2];
        texture3 = hostLocationTextureTable[3];
        texture4 = hostLocationTextureTable[4];
        texture5 = hostLocationTextureTable[5];
        monsterSeed = hostLocationMonsterSeed;
        hasGenerationSpec = true;

        Debug.Log($"[NetworkDungeonConversion][GenerationSpec] Host location spec selected. dungeon='{sceneLocation.RegionName}/{sceneLocation.Name}' mapId={sceneLocation.MapTableData.MapId} locationId={sceneLocation.Dungeon.RecordElement.Header.LocationId} textures=[{texture0},{texture1},{texture2},{texture3},{texture4},{texture5}] seed={monsterSeed}");
    }

    string dungeonSceneName = DaggerfallDungeon.GetSceneName(sceneLocation);
    bool hasExplicitDungeonAnchor = (teleportPcRequest || savedLocalPositionRequest) && hasExplicitTeleportPcAnchor;

    // A saved-dungeon load can arrive before the requester's normal PositionMultiplayer
    // update publishes the save's dungeon entrance. Correct only that requester here.
    // Existing dungeon anchor/enemy metadata remains owned by the first creator.
    if (savedLocalPositionRequest && hasExplicitDungeonAnchor)
    {
        SrvForceRequesterPositionToTeleportPcAnchor(
            requesterNetId,
            teleportPcAnchorWorldX,
            teleportPcAnchorWorldZ,
            "saved-dungeon-request-before-host-lookup");
    }

    // Check if dungeon already exists.
    // If it exists, do not regenerate it. Send the existing host-authored generation spec to the requester.
    DaggerfallDungeon[] allDungeons = FindObjectsOfType<DaggerfallDungeon>();
    foreach (var existing in allDungeons)
    {
        if (!IsUsableHostNetworkDungeon(existing))
            continue;

        if (string.Equals(existing.Summary.RegionName, regionName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Summary.LocationName, locationName, StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log($"[CmdRequestDungeonFromHost] Dungeon already exists: {dungeonSceneName}. Syncing existing spec with requester.");

            if (existing.isSet)
            {
                if (teleportPcRequest && hasExplicitTeleportPcAnchor)
                {
                    SrvForceRequesterPositionToTeleportPcAnchor(requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "existing-dungeon-before-target");
                    SrvApplyTeleportPcDungeonAnchor(existing, requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "existing-dungeon-before-target");
                    SrvRebindDungeonEnemiesToTeleportPcAnchor(existing, requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "existing-dungeon-before-target");
                }
                else if (savedLocalPositionRequest && existing.HasDungeonWorldAnchor)
                {
                    // A client that starts MP while already inside its own local copy of this
                    // dungeon can publish that local copy's stale/pre-conversion GPS anchor.
                    // Once the host has an existing network dungeon, the first creator's dungeon
                    // anchor is authoritative. Correct the server-side player coordinates now,
                    // before remote visibility can cull this requester as being far away.
                    SrvForceRequesterPositionToTeleportPcAnchor(
                        requesterNetId,
                        existing.DungeonAnchorWorldX,
                        existing.DungeonAnchorWorldZ,
                        "existing-dungeon-authoritative-anchor-before-target");
                }

                DaggerfallDungeon.DungeonNetworkData existingData = BuildDungeonNetworkDataForRequester(existing, requesterNetId);
                Debug.Log($"[CmdRequestDungeonFromHost] Existing dungeon spec: level={existingData.RequesterLevel}, seed={existingData.MonsterSeed}, textures=[{existingData.Texture0},{existingData.Texture1},{existingData.Texture2},{existingData.Texture3},{existingData.Texture4},{existingData.Texture5}]");

                SendDungeonEntryToRequester(
                    requesterNetId,
                    existingData,
                    teleportPcRequest,
                    teleportPcLocalMarker,
                    savedLocalPositionRequest,
                    savedDungeonLocalPosition);
            }
            else
            {
                Debug.Log($"[CmdRequestDungeonFromHost] Matching host dungeon is still generating. Queueing requester={requesterNetId} instead of creating a duplicate.");
                StartCoroutine(WaitForExistingDungeonGeneration(
                    existing,
                    requesterNetId,
                    teleportPcRequest,
                    teleportPcLocalMarker,
                    savedLocalPositionRequest,
                    savedDungeonLocalPosition));
            }
            return;
        }
    }

    // Load location on the host. This can work even when the host is nowhere near the exterior entrance.
    DFLocation location = sceneLocation;

    // Host-authoritative Y allocation. Do not use object count; destroyed dungeons make count-based slots unsafe.
    float assignedY = DaggerfallDungeon.GetNextAvailableDungeonY();

    GameObject prefab = NetworkManager.singleton.spawnPrefabs
        .FirstOrDefault(p => p.GetComponent<DaggerfallDungeon>() != null);

    if (prefab == null)
    {
        Debug.LogError("[CmdRequestDungeonFromHost] ERROR: DaggerfallDungeon prefab not found in spawnable prefabs!");
        PlayerMultiplayer failedRequester;
        NetworkConnection failedConnection;
        if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out failedRequester, out failedConnection))
            failedRequester.TargetSavedDungeonRequestFailed(failedConnection, "network-dungeon-prefab-not-found");
        return;
    }

    GameObject dungeonObject = Instantiate(prefab);
    dungeonObject.name = dungeonSceneName;
    dungeonObject.transform.position = new Vector3(0, assignedY, 0);

    DaggerfallDungeon dungeonComponentNew = dungeonObject.GetComponent<DaggerfallDungeon>();
    dungeonComponentNew.PositionY = assignedY;

    if (hasExplicitDungeonAnchor)
    {
        // TeleportPc and saved-dungeon conversion both carry the exact destination
        // dungeon entrance. Do not read a stale requester PositionMultiplayer value.
        SrvForceRequesterPositionToTeleportPcAnchor(requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "new-dungeon-before-generation-" + requestKind);
        SrvApplyTeleportPcDungeonAnchor(dungeonComponentNew, requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "new-dungeon-before-generation-explicit-" + requestKind);
    }
    else
    {
        dungeonComponentNew.SetDungeonRequesterContext(requesterNetId);
    }

    dungeonComponentNew.EnsureNetworkDungeonIdentity();

    // Only this branch creates a new dungeon, so only here may a saved snapshot
    // become authoritative. The existing-dungeon branch returned above without
    // reading it, preserving the first creator's live state.
    if (savedLocalPositionRequest && !string.IsNullOrEmpty(initialSavedActionState))
    {
        dungeonComponentNew.ConfigureInitialSavedActionState(
            initialSavedActionState,
            "server-first-saved-dungeon-creator");
    }

    NetworkServer.Spawn(dungeonObject);

    if (!hasGenerationSpec)
    {
        int[] generatedTextureTable = DaggerfallDungeon.BuildLocationDungeonTextureTable(location);
        monsterSeed = DaggerfallDungeon.BuildStableDungeonMonsterSeed(location);
        texture0 = generatedTextureTable[0];
        texture1 = generatedTextureTable[1];
        texture2 = generatedTextureTable[2];
        texture3 = generatedTextureTable[3];
        texture4 = generatedTextureTable[4];
        texture5 = generatedTextureTable[5];
        hasGenerationSpec = true;
        Debug.Log($"[CmdRequestDungeonFromHost] Built missing requester generation spec on host from requester level={resolvedRequesterLevel}, seed={monsterSeed}, textures=[{texture0},{texture1},{texture2},{texture3},{texture4},{texture5}].");
    }

    if (hasGenerationSpec)
    {
        dungeonComponentNew.ApplyAuthoritativeGenerationSpec(
            resolvedRequesterLevel,
            monsterSeed,
            texture0,
            texture1,
            texture2,
            texture3,
            texture4,
            texture5);
    }
    else
    {
        Debug.Log("[CmdRequestDungeonFromHost] No requester generation spec supplied. Host will generate spec locally as fallback.");
    }

    dungeonComponentNew.GenerateDungeon(location, !vampireCemeteryWakeRequest, assignedY);
    if (vampireCemeteryWakeRequest)
        Debug.Log($"[VampireMP] Host generated cemetery network dungeon without random enemies for requester={requesterNetId}: '{regionName}/{locationName}'");

    // TeleportPc safety: GenerateDungeon()/LayoutDungeon() imports random dungeon enemies immediately.
    // For pure-client TeleportPc the server-side requester PositionMultiplayer can still be the
    // tavern/house coordinate at import time, so imported enemies can bake the wrong DF X/Z.
    // Rebind their EnemyWorldPosition metadata directly to the same exact entrance anchor sent
    // by the client. This is the same kind of metadata repair used by CreateFoe dungeon spawns;
    // it does not move enemies and does not touch DynamicEnemyAuthority.
    if (hasExplicitDungeonAnchor)
    {
        SrvApplyTeleportPcDungeonAnchor(dungeonComponentNew, requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "new-dungeon-after-generation-" + requestKind);
        SrvRebindDungeonEnemiesToTeleportPcAnchor(dungeonComponentNew, requesterNetId, teleportPcAnchorWorldX, teleportPcAnchorWorldZ, "new-dungeon-after-generation-" + requestKind);
    }

    // Wait until ready and then send auto-enter to requester + sync-only to everyone else.
    StartCoroutine(WaitForDungeonGeneration(
        dungeonComponentNew,
        requesterNetId,
        location,
        assignedY,
        dungeonComponentNew.netId,
        teleportPcRequest,
        teleportPcLocalMarker,
        savedLocalPositionRequest,
        savedDungeonLocalPosition));
}




private IEnumerator WaitForDungeonGeneration(
    DaggerfallDungeon dungeon,
    uint requesterNetId,
    DFLocation location,
    float assignedY,
    uint dungeonNetId,
    bool teleportPcRequest,
    Vector3 teleportPcLocalMarker,
    bool savedLocalPositionRequest,
    Vector3 savedDungeonLocalPosition)
{
    float started = Time.realtimeSinceStartup;
    while (dungeon != null && (!dungeon.isSet || !dungeon.InitialSavedActionStateReady))
    {
        if (Time.realtimeSinceStartup - started > 20f)
        {
            PlayerMultiplayer failedRequester;
            NetworkConnection failedConnection;
            if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out failedRequester, out failedConnection))
                failedRequester.TargetSavedDungeonRequestFailed(failedConnection, "new-dungeon-generation-timeout");
            yield break;
        }

        yield return null;
    }

    if (dungeon == null)
    {
        PlayerMultiplayer failedRequester;
        NetworkConnection failedConnection;
        if (savedLocalPositionRequest && TryGetRequesterPlayerAndConnection(requesterNetId, out failedRequester, out failedConnection))
            failedRequester.TargetSavedDungeonRequestFailed(failedConnection, "new-dungeon-disappeared");
        yield break;
    }

    // IMPORTANT:
    // GenerateDungeon() is allowed to choose/override the final Y on the host.
    // Always send the actual host-authored value from dungeon.PositionY, not the earlier local assignedY variable.
    float authoritativeY = dungeon.PositionY;

    var dungeonData = new DaggerfallDungeon.DungeonNetworkData
    {
        ID = location.MapTableData.MapId,
        RegionName = location.RegionName,
        LocationName = location.Name,
        LocationType = location.MapTableData.LocationType,
        DungeonType = location.MapTableData.DungeonType,
        PositionY = authoritativeY,
        RequesterNetId = requesterNetId,
        DungeonNetId = dungeonNetId,
        IsNetworkDungeonInstance = dungeon.IsNetworkDungeonInstance,
        DungeonInstanceId = dungeon.DungeonInstanceId
    };
    dungeon.WriteGenerationSpecToNetworkData(ref dungeonData);
    // World-anchor data is just as authoritative as Y/generation data. Without it,
    // a client can generate the correct dungeon locally but PositionMultiplayer falls
    // back to that client's old SP PlayerGPS coordinates until another dungeon message
    // happens to repair the anchor.
    dungeon.WriteWorldAnchorToNetworkData(ref dungeonData);

    Debug.Log($"[CmdRequestDungeonFromHost] Dungeon ready. Sending auto-enter to requester netId={requesterNetId}, dungeonNetId={dungeonNetId}, hostY={authoritativeY}, level={dungeonData.RequesterLevel}, seed={dungeonData.MonsterSeed}, textures=[{dungeonData.Texture0},{dungeonData.Texture1},{dungeonData.Texture2},{dungeonData.Texture3},{dungeonData.Texture4},{dungeonData.Texture5}].");

    SendDungeonEntryToRequester(
        requesterNetId,
        dungeonData,
        teleportPcRequest,
        teleportPcLocalMarker,
        savedLocalPositionRequest,
        savedDungeonLocalPosition);

    // Sync to all other clients, but do not auto-enter them.
    foreach (var player in FindObjectsOfType<PlayerMultiplayer>())
    {
        if (player != null && player.connectionToClient != null && player.netId != requesterNetId)
            player.RpcSyncDungeon(dungeonData);
    }
}



private IEnumerator DelayedDungeonSpawn(
    DaggerfallDungeon.DungeonNetworkData dungeonData,
    bool enterAfterReady,
    bool teleportPcEnter,
    bool savedLocalPositionEnter,
    Vector3 savedDungeonLocalPosition)
{
    Debug.Log($"[DelayedDungeonSpawn] Waiting for network-spawned dungeon prefab. dungeonNetId={dungeonData.DungeonNetId}, hostY={dungeonData.PositionY}, enterAfterReady={enterAfterReady}, teleportPcEnter={teleportPcEnter}, savedLocalEnter={savedLocalPositionEnter}, requester={dungeonData.RequesterNetId}, thisNetId={netId}, isLocalPlayer={isLocalPlayer}");

    DaggerfallDungeon dungeon = null;

    for (int i = 0; i < 100; i++)
    {
        dungeon = FindObjectsOfType<DaggerfallDungeon>()
            .FirstOrDefault(d => d != null && d.netId == dungeonData.DungeonNetId);

        if (dungeon != null)
            break;

        yield return new WaitForSeconds(0.02f);
    }

    if (dungeon == null)
    {
        Debug.LogError($"[DelayedDungeonSpawn] ERROR: Dungeon prefab not found for {dungeonData.RegionName} - {dungeonData.LocationName}, dungeonNetId={dungeonData.DungeonNetId}");
        if (savedLocalPositionEnter)
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (playerEnterExit != null)
                playerEnterExit.FailPendingNetworkDungeonConversion("network-dungeon-prefab-spawn-timeout");
        }
        yield break;
    }

    Debug.Log($"[DelayedDungeonSpawn] Found prefab. Applying host data for {dungeonData.LocationName}. hostY={dungeonData.PositionY}");

    DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader
        .GetLocation(dungeonData.RegionName, dungeonData.LocationName);

    if (!location.Loaded)
    {
        Debug.LogError($"[DelayedDungeonSpawn] ERROR: Could not load location for {dungeonData.RegionName} - {dungeonData.LocationName}");
        if (savedLocalPositionEnter)
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (playerEnterExit != null)
                playerEnterExit.FailPendingNetworkDungeonConversion("local-dungeon-location-not-found");
        }
        yield break;
    }

    var summary = new DaggerfallDungeon.DungeonSummary
    {
        ID = dungeonData.ID,
        RegionName = dungeonData.RegionName,
        LocationName = dungeonData.LocationName,
        LocationType = dungeonData.LocationType,
        DungeonType = dungeonData.DungeonType,
        LocationData = location
    };

    dungeon.summary = summary;

    // Use the host-authored Y from the RPC immediately.
    // Do not wait for the PositionY SyncVar here; on a late/pending spawn it can still be the prefab/default value for a frame.
    dungeon.PositionY = dungeonData.PositionY;
    dungeon.transform.position = new Vector3(0, dungeonData.PositionY, 0);
    dungeon.ApplyNetworkDungeonIdentityFromData(dungeonData);

    dungeon.ApplyAuthoritativeGenerationSpec(dungeonData);
    if (dungeonData.HasGenerationSpec)
    {
        Debug.Log($"[DelayedDungeonSpawn] Applied authoritative dungeon spec before client generation: level={dungeonData.RequesterLevel}, seed={dungeonData.MonsterSeed}, textures=[{dungeonData.Texture0},{dungeonData.Texture1},{dungeonData.Texture2},{dungeonData.Texture3},{dungeonData.Texture4},{dungeonData.Texture5}]");
    }
    else
    {
        Debug.LogWarning("[DelayedDungeonSpawn] WARNING: Dungeon sync did not contain a generation spec. Client will use local fallback generation.");
    }

    dungeon.ScheduleDeferredGeneration(dungeonData.PositionY);

    Debug.Log($"[DelayedDungeonSpawn] Blocks count = {summary.LocationData.Dungeon.Blocks?.Length}");
    Debug.Log($"[DelayedDungeonSpawn] Dungeon sync applied: {dungeonData.LocationName} at hostY={dungeonData.PositionY}");

    bool shouldAutoEnter = enterAfterReady && isLocalPlayer && netId == dungeonData.RequesterNetId;

    if (shouldAutoEnter)
    {
        Debug.Log($"[DelayedDungeonSpawn] Auto-enter approved for requester only. playerNetId={netId}, dungeonNetId={dungeonData.DungeonNetId}, teleportPcEnter={teleportPcEnter}, savedLocalEnter={savedLocalPositionEnter}");
        if (savedLocalPositionEnter)
            StartCoroutine(WaitForSavedDungeonReady(dungeon, location, savedDungeonLocalPosition));
        else if (teleportPcEnter)
            StartCoroutine(WaitForTeleportPcDungeonReady(dungeon, location));
        else
            StartCoroutine(WaitForDungeonReady(dungeon, location));
    }
    else
    {
        Debug.Log($"[DelayedDungeonSpawn] Sync-only. enterAfterReady={enterAfterReady}, teleportPcEnter={teleportPcEnter}, isLocalPlayer={isLocalPlayer}, thisNetId={netId}, requesterNetId={dungeonData.RequesterNetId}");
    }
}







private IEnumerator WaitForSavedDungeonReady(DaggerfallDungeon dungeon, DFLocation location, Vector3 dungeonLocalPosition)
{
    float started = Time.realtimeSinceStartup;
    while (dungeon != null && (!dungeon.isSet || dungeon.StartMarker == null || !dungeon.InitialSavedActionStateReady))
    {
        if (Time.realtimeSinceStartup - started > 20f)
        {
            PlayerEnterExit timedOutPlayer = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (timedOutPlayer != null)
                timedOutPlayer.FailPendingNetworkDungeonConversion("local-dungeon-generation-timeout");
            yield break;
        }

        yield return null;
    }

    PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
    if (playerEnterExit == null || dungeon == null)
        yield break;

    // A late TargetRpc after a timeout/fallback must remain sync-only and must not
    // pull the player back into the dungeon.
    if (!playerEnterExit.HasPendingNetworkDungeonConversionFor(location))
    {
        Debug.LogWarning($"[NetworkDungeonConversion] Saved dungeon became ready after the local conversion was no longer pending. Syncing only: '{location.RegionName}/{location.Name}'.");
        yield break;
    }

    yield return new WaitForSeconds(0.1f);

    // The host-authored dungeon is now generated locally. Reassert its own anchor
    // before the normal transition injects this client's quest resources. This also
    // repairs any late PlayerGPS write performed while the save finished loading.
    if (dungeon.HasDungeonWorldAnchor)
    {
        PreparePureClientSavedDungeonAnchor(
            dungeon.DungeonAnchorWorldX,
            dungeon.DungeonAnchorWorldZ,
            "saved-dungeon-ready-before-transition",
            false);
    }

    // Bind the exact network dungeon object supplied by the host. Do not route saved
    // loads through TransitionDungeonInterior(): that generic method searches by
    // region/name and can pick a stale local Y=0 dungeon if both copies coexist for
    // a frame on a pure client. Preserve the normal transition side effects through
    // PlayerEnterExit's saved-load-specific exact-object preparation instead.
    bool exactBindPrepared = false;
    try
    {
        exactBindPrepared = playerEnterExit.PrepareSavedNetworkDungeonTransition(
            dungeon,
            location,
            "saved-dungeon-target-entry");
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"[NetworkDungeonConversion] Exact saved-dungeon entry preparation threw. dungeon='{location.RegionName}/{location.Name}' netId={dungeon.netId} error={ex}");
    }

    if (!exactBindPrepared)
    {
        playerEnterExit.FailPendingNetworkDungeonConversion("exact-network-dungeon-bind-failed");
        yield break;
    }

    if (!playerEnterExit.TryCompleteNetworkDungeonConversion(
        dungeon,
        location,
        dungeonLocalPosition,
        "saved-dungeon-target-entry"))
    {
        playerEnterExit.FailPendingNetworkDungeonConversion("final-local-position-entry-failed");
    }
    else if (dungeon.HasDungeonWorldAnchor)
    {
        // TryCompleteNetworkDungeonConversion has now bound IsPlayerInsideDungeon and
        // snapped the saved local position. Publish once more from that final state so
        // the server and the remote-player visibility check cannot retain pre-load x/z.
        PreparePureClientSavedDungeonAnchor(
            dungeon.DungeonAnchorWorldX,
            dungeon.DungeonAnchorWorldZ,
            "saved-dungeon-bound-final",
            true);
    }
}

private IEnumerator WaitForTeleportPcDungeonReady(DaggerfallDungeon dungeon, DFLocation location)
{
    // TeleportPc is not a clicked exterior-dungeon-door transition. On a pure client,
    // the exterior dungeon entrance door may not exist in the currently loaded exterior/interior scene,
    // so do NOT call dungeon.GetDungeonEntryDoor() here. The host already created/synced the
    // network dungeon; once the local dungeon is generated, enter it with a dummy StaticDoor and
    // let PlayerEnterExit apply the registered TeleportPc quest marker after its normal entry move.
    float started = Time.realtimeSinceStartup;
    while (dungeon != null && (!dungeon.isSet || dungeon.StartMarker == null || !dungeon.InitialSavedActionStateReady))
    {
        if (Time.realtimeSinceStartup - started > 20f)
        {
            Debug.LogError($"[TeleportPcMP][WaitForDungeonReady] Timed out waiting for generated dungeon '{location.Name}'. isSet={(dungeon != null && dungeon.isSet)}, startMarker={(dungeon != null && dungeon.StartMarker != null)}");
            yield break;
        }
        yield return null;
    }

    Debug.Log($"[TeleportPcMP][WaitForDungeonReady] Dungeon is generated. Entering without exterior entry-door lookup: {location.Name}");

    PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
    if (playerEnterExit == null)
    {
        Debug.LogError("[TeleportPcMP][WaitForDungeonReady] ERROR: Could not find PlayerEnterExit!");
        yield break;
    }

    yield return new WaitForSeconds(0.1f);
    playerEnterExit.TransitionDungeonInterior(null, new StaticDoor(), location, true);
}

private IEnumerator WaitForDungeonReady(DaggerfallDungeon dungeon, DFLocation location)
{
    while (!dungeon.isSet || dungeon.StartMarker == null || !dungeon.InitialSavedActionStateReady) // Wait for full generation and first-creator state
    {
        yield return null;
    }

    Debug.Log($"[WaitForDungeonReady] Dungeon is fully generated. Moving player inside.");

    PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
    if (playerEnterExit != null)
    {
        StaticDoor? entryDoorNullable = dungeon.GetDungeonEntryDoor();
        if (!entryDoorNullable.HasValue)
        {
            Debug.LogError($"[WaitForDungeonReady] ERROR: No valid entry door found for {location.Name}.");
            yield break;
        }
        StaticDoor entryDoor = entryDoorNullable.Value;
        yield return new WaitForSeconds(0.1f); // brief buffer
        playerEnterExit.TransitionDungeonInterior(null, entryDoor, location, true);
    }
    else
    {
        Debug.LogError("[WaitForDungeonReady] ERROR: Could not find PlayerEnterExit!");
    }
}



[ClientRpc]
public void RpcSyncDungeon(DaggerfallDungeon.DungeonNetworkData data)
{
    // The host already owns the fully generated authoritative dungeon.
    // Do not run the remote-client reconstruction path on the host client.
    // DelayedDungeonSpawn() assigns a freshly loaded DFLocation to dungeon.summary;
    // on an already-generated host dungeon this replaces the per-block WaterLevel values
    // populated by LayoutDungeon() with the raw/default map values (usually 0).
    // Pure clients still need this RPC so they can generate their local dungeon copy.
    if (NetworkServer.active)
    {
        Debug.Log($"[RpcSyncDungeon] Host skipped client reconstruction for authoritative dungeon {data.LocationName}, dungeonNetId={data.DungeonNetId}");
        return;
    }

    if (data.RequesterNetId != 0 && this.netId == data.RequesterNetId)
    {
        Debug.Log($"[RpcSyncDungeon] Skipping sync for requester (netId={netId})");
        return;
    }

    Debug.Log($"[RpcSyncDungeon] Syncing dungeon {data.LocationName} at Y={data.PositionY}, level={data.RequesterLevel}, seed={data.MonsterSeed}, textures=[{data.Texture0},{data.Texture1},{data.Texture2},{data.Texture3},{data.Texture4},{data.Texture5}], hasSpec={data.HasGenerationSpec}");

    StartCoroutine(DelayedDungeonSpawn(data, false, false, false, Vector3.zero));
}



// late join client stuff

[TargetRpc]
public void TargetEnterTeleportPcDungeon(NetworkConnection target, DaggerfallDungeon.DungeonNetworkData dungeonData, Vector3 markerLocalPosition)
{
    Debug.Log($"[TeleportPcMP][TargetEnter] RECEIVED: {dungeonData.LocationName} at Y={dungeonData.PositionY}, requesterNetId={dungeonData.RequesterNetId}, thisNetId={netId}, isLocalPlayer={isLocalPlayer}, markerLocal={markerLocalPosition}");

    // TargetRpc is sent only to the requesting connection, but keep the same requester guard
    // as TargetEnterDungeon so remote clones on that client never auto-enter.
    if (!isLocalPlayer || netId != dungeonData.RequesterNetId)
    {
        Debug.LogWarning($"[TeleportPcMP][TargetEnter] Ignored auto-enter on non-requester object. Syncing dungeon only. thisNetId={netId}, requesterNetId={dungeonData.RequesterNetId}, isLocalPlayer={isLocalPlayer}");
        StartCoroutine(DelayedDungeonSpawn(dungeonData, false, false, false, Vector3.zero));
        return;
    }

    try
    {
        DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader
            .GetLocation(dungeonData.RegionName, dungeonData.LocationName);

        PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
        if (playerEnterExit != null && location.Loaded)
        {
            playerEnterExit.RegisterMultiplayerQuestDungeonTeleportMarker(
                location,
                markerLocalPosition,
                dungeonData.PositionY,
                "TargetEnterTeleportPcDungeon-before-delayed-spawn");
        }
        else
        {
            Debug.LogWarning($"[TeleportPcMP][TargetEnter] Could not register quest marker before delayed spawn. playerEnterExit={(playerEnterExit != null)}, locationLoaded={location.Loaded}");
        }
    }
    catch (System.Exception ex)
    {
        Debug.LogWarning($"[TeleportPcMP][TargetEnter] Marker registration failed before delayed spawn: {ex.Message}");
    }

    StartCoroutine(DelayedDungeonSpawn(dungeonData, true, true, false, Vector3.zero));
}

[TargetRpc]
public void TargetEnterSavedDungeon(
    NetworkConnection target,
    DaggerfallDungeon.DungeonNetworkData dungeonData,
    Vector3 dungeonLocalPosition)
{
    Debug.Log($"[NetworkDungeonConversion][TargetEnter] RECEIVED: {dungeonData.LocationName} at Y={dungeonData.PositionY}, requesterNetId={dungeonData.RequesterNetId}, thisNetId={netId}, isLocalPlayer={isLocalPlayer}, local={dungeonLocalPosition}");

    PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
    DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(
        dungeonData.RegionName,
        dungeonData.LocationName);

    bool requesterMatches = isLocalPlayer && netId == dungeonData.RequesterNetId;
    bool conversionPending = playerEnterExit != null &&
                             location.Loaded &&
                             playerEnterExit.HasPendingNetworkDungeonConversionFor(location);

    // Replace the locally calculated pending value with the host dungeon's own
    // authoritative anchor as soon as the TargetRpc arrives. The hold remains active
    // only for this saved-dungeon conversion and is released after dungeon exit.
    if (requesterMatches && dungeonData.HasDungeonWorldAnchor)
    {
        pureClientSavedDungeonAnchorWorldX = dungeonData.DungeonAnchorWorldX;
        pureClientSavedDungeonAnchorWorldZ = dungeonData.DungeonAnchorWorldZ;
        PreparePureClientSavedDungeonAnchor(
            pureClientSavedDungeonAnchorWorldX,
            pureClientSavedDungeonAnchorWorldZ,
            "saved-dungeon-target-authoritative-anchor",
            true);
    }

    if (!requesterMatches || !conversionPending)
    {
        Debug.LogWarning($"[NetworkDungeonConversion][TargetEnter] Ignored saved-position auto-enter; syncing dungeon only. requesterMatches={requesterMatches} conversionPending={conversionPending}");
        StartCoroutine(DelayedDungeonSpawn(dungeonData, false, false, false, Vector3.zero));
        return;
    }

    // The host already owns the fully generated authoritative object. Do not run
    // the client reconstruction path on it, which would replace its populated
    // Summary/WaterLevel data with a freshly loaded raw DFLocation.
    if (NetworkServer.active)
    {
        DaggerfallDungeon hostDungeon = FindObjectsOfType<DaggerfallDungeon>()
            .FirstOrDefault(d => d != null && d.netId == dungeonData.DungeonNetId);

        if (hostDungeon == null)
        {
            playerEnterExit.FailPendingNetworkDungeonConversion("host-dungeon-object-not-found");
            return;
        }

        StartCoroutine(WaitForSavedDungeonReady(hostDungeon, location, dungeonLocalPosition));
        return;
    }

    StartCoroutine(DelayedDungeonSpawn(dungeonData, true, false, true, dungeonLocalPosition));
}

[TargetRpc]
public void TargetSavedDungeonRequestFailed(NetworkConnection target, string reason)
{
    if (!isLocalPlayer)
        return;

    PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
    if (playerEnterExit != null)
        playerEnterExit.FailPendingNetworkDungeonConversion("host-rejected-" + reason);
}

[TargetRpc]
public void TargetEnterDungeon(NetworkConnection target, DaggerfallDungeon.DungeonNetworkData dungeonData)
{
    Debug.Log($"[TargetEnterDungeon] RECEIVED: {dungeonData.LocationName} at Y={dungeonData.PositionY}, requesterNetId={dungeonData.RequesterNetId}, thisNetId={netId}, isLocalPlayer={isLocalPlayer}");

    // This TargetRpc is sent only to the requesting connection, but every NetworkIdentity still exists on that client.
    // Only the requesting client's own local PlayerMultiplayer object may auto-enter.
    if (!isLocalPlayer || netId != dungeonData.RequesterNetId)
    {
        Debug.LogWarning($"[TargetEnterDungeon] Ignored auto-enter on non-requester object. Syncing dungeon only. thisNetId={netId}, requesterNetId={dungeonData.RequesterNetId}, isLocalPlayer={isLocalPlayer}");
        StartCoroutine(DelayedDungeonSpawn(dungeonData, false, false, false, Vector3.zero));
        return;
    }

    StartCoroutine(DelayedDungeonSpawn(dungeonData, true, false, false, Vector3.zero));
}


public override void OnStartLocalPlayer()
{
    base.OnStartLocalPlayer();

    // Set this as early as Mirror marks ownership, before arbitrary spawner/quest
    // code tries to send Commands through PlayerMultiplayer.localPlayer.
    localPlayer = this;
    playerObject = GameManager.Instance != null ? GameManager.Instance.PlayerObject : playerObject;
    id = "" + GetComponent<NetworkIdentity>().netId;

    // When starting/joining multiplayer from an existing singleplayer scene, remove old local enemies.
    // Networked enemies will be spawned/synced by the host afterwards.
    GameObjectHelper.DestroyNonNetworkedEnemiesForMultiplayerStart();

    // ask host for all dungeons that already exist so we generate them locally
    StartCoroutine(RequestActiveDungeonsNextFrame());
}

private System.Collections.IEnumerator RequestActiveDungeonsNextFrame()
{
    // wait one frame to ensure identity/connection is fully ready
    yield return null;
    CmdRequestActiveDungeons();
}

[Command]
private void CmdRequestActiveDungeons()
{
    // enumerate all server-side dungeons that are already generated
    var dungeons = FindObjectsOfType<DaggerfallDungeon>();
    foreach (var d in dungeons)
    {
        if (d == null || !d.isSet) continue;

        var data = new DaggerfallDungeon.DungeonNetworkData
        {
            ID           = d.Summary.ID,
            RegionName   = d.Summary.RegionName,
            LocationName = d.Summary.LocationName,
            LocationType = d.Summary.LocationType,
            DungeonType  = d.Summary.DungeonType,
            PositionY    = d.PositionY,
            RequesterNetId = d.RequesterNetId,
            DungeonNetId   = d.netId,     // so client can find the prefab
            IsNetworkDungeonInstance = d.IsNetworkDungeonInstance,
            DungeonInstanceId = d.DungeonInstanceId
        };
        d.WriteGenerationSpecToNetworkData(ref data);
        // Late/joining clients must receive the stable dungeon world anchor too. Applying
        // DungeonNetworkData without these fields clears HasDungeonWorldAnchor locally and
        // makes PositionMultiplayer fall back to unrelated SP/startup PlayerGPS coordinates.
        d.WriteWorldAnchorToNetworkData(ref data);
        Debug.Log($"[CmdRequestActiveDungeons] Sending existing dungeon spec to late/joining client. dungeon={data.LocationName}, level={data.RequesterLevel}, seed={data.MonsterSeed}, textures=[{data.Texture0},{data.Texture1},{data.Texture2},{data.Texture3},{data.Texture4},{data.Texture5}]");

        // push to just this client; do NOT auto-enter
        TargetSyncExistingDungeon(connectionToClient, data);
    }
}

[TargetRpc]
private void TargetSyncExistingDungeon(NetworkConnection target, DaggerfallDungeon.DungeonNetworkData data)
{
    // A joining client can already be converting its own local/SP copy of this same
    // dungeon when the generic active-dungeon sync arrives. If so, adopt the host's
    // anchor immediately so PositionMultiplayer cannot keep publishing the old local
    // dungeon GPS value while waiting for the dedicated saved-dungeon TargetRpc.
    if (isLocalPlayer && data.HasDungeonWorldAnchor)
    {
        try
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance != null
                ? GameManager.Instance.PlayerEnterExit
                : null;

            if (playerEnterExit != null)
            {
                DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader
                    .GetLocation(data.RegionName, data.LocationName);

                if (location.Loaded && playerEnterExit.HasPendingNetworkDungeonConversionFor(location))
                {
                    ActivatePureClientSavedDungeonAnchor(
                        data.DungeonAnchorWorldX,
                        data.DungeonAnchorWorldZ,
                        "active-dungeon-sync-pending-conversion");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[CmdRequestActiveDungeons] Could not apply pending-conversion dungeon anchor for '{data.RegionName}/{data.LocationName}'. error={ex.Message}");
        }
    }

    // generate the dungeon locally but do not enter it
    StartCoroutine(DelayedDungeonSpawn(data, false, false, false, Vector3.zero));
}


	

	
	
	
	[Command]
public void CmdRequestInteriorData(uint interiorNetId)
{
    if (NetworkServer.spawned.TryGetValue(interiorNetId, out NetworkIdentity identity))
    {
        DaggerfallInteriorNetwork interior = identity.GetComponent<DaggerfallInteriorNetwork>();
        if (interior != null)
        {
            Debug.Log($"[InteriorNet] 🔁 Host received CmdRequestInteriorData for netId={interiorNetId}");
            interior.TargetSendInteriorData(connectionToClient, interior.CreateNetworkData());
        }
    }
}

	
[Command]
public void CmdRequestInteriorFromHostFull(DaggerfallInteriorNetwork.InteriorNetworkData data)
{
    if (!NetworkServer.active) return;

    GameObject prefab = NetworkManager.singleton.spawnPrefabs
        .FirstOrDefault(p => p.GetComponent<DaggerfallInteriorNetwork>() != null);

    if (!prefab)
    {
        Debug.LogError("[InteriorNet] ❌ No interior prefab found in spawnPrefabs.");
        return;
    }

    // Instantiate the interior prefab
    GameObject instance = Instantiate(prefab);
    var interiorNet = instance.GetComponent<DaggerfallInteriorNetwork>();

    // Assign SyncVar and other host-only values
    interiorNet.regionName = data.regionName;
    interiorNet.locationName = data.locationName;
    interiorNet.regionIndex = data.regionIndex;
    interiorNet.locationIndex = data.locationIndex;
    interiorNet.buildingKey = data.buildingKey;
    interiorNet.posX = data.posX;
    interiorNet.posY = data.posY;
    interiorNet.posZ = data.posZ;
    interiorNet.climateBase = data.climate;
    interiorNet.blockName = data.blockName;
    interiorNet.blockIndex = data.blockIndex;
    interiorNet.doorRecordIndex = data.recordIndex;
    interiorNet.doorIndex = data.doorIndex;
    interiorNet.doorPosition = data.doorPosition;
    interiorNet.doorNormal = data.doorNormal;
    interiorNet.doorOwnerPosition = data.doorOwnerPosition;
    interiorNet.doorOwnerRotation = data.doorOwnerRotation;
    interiorNet.buildingMatrixOffset = data.buildingMatrix.GetColumn(3);
    interiorNet.buildingMatrixRotation = GameObjectHelper.QuaternionFromMatrix(data.buildingMatrix);
    interiorNet.discoveredBuilding = data.discoveredBuilding;

    // Initialize state
    interiorNet.originalDoorOwner = null;
    interiorNet.staticDoor = new StaticDoor
    {
        buildingKey = data.buildingKey,
        recordIndex = data.recordIndex,
        doorIndex = data.doorIndex,
        blockIndex = data.blockIndex,
        centre = data.doorPosition,
        normal = data.doorNormal,
        ownerPosition = data.doorOwnerPosition,
        ownerRotation = data.doorOwnerRotation,
        buildingMatrix = data.buildingMatrix,
    };

    instance.transform.position = Vector3.zero;
    instance.transform.rotation = Quaternion.identity;

    // Host spawns interior locally
    interiorNet.StartCoroutine(interiorNet.DeferredSpawnInterior(data));

    // Spawn on network
    NetworkServer.Spawn(instance);

    Debug.Log($"[InteriorNet] ✅ Host spawned interior from client request (buildingKey={data.buildingKey})");

    // 🔁 Send interior data back to the requesting client (so it runs DeferredSpawnInterior too)
    interiorNet.TargetSendInteriorData(connectionToClient, data);
}















public static class BuildingDiscoveryCache
{
    private static Dictionary<int, PlayerGPS.DiscoveredBuilding> discoveredBuildings = new Dictionary<int, PlayerGPS.DiscoveredBuilding>();


    public static void AddOrUpdate(PlayerGPS.DiscoveredBuilding building)
    {
        discoveredBuildings[building.buildingKey] = building;
    }

    public static bool TryGet(int key, out PlayerGPS.DiscoveredBuilding building)
    {
        return discoveredBuildings.TryGetValue(key, out building);
    }
}


	
	

// === Saved MP-offset local-interior enemy network recreation ===
// These methods are used by SaveLoadManager when a save made inside an MP-offset local
// building interior is loaded while still connected to multiplayer. The saved enemies
// must not be restored as local/SP-only enemies; the host recreates them as real
// NetworkServer-spawned enemies instead.
[Command]
public void CmdSpawnSavedInteriorEnemy(
    Vector3 worldPosition,
    int entityTypeInt,
    int careerIndex,
    int mobileGenderInt,
    bool isHostile,
    bool alliedToPlayer,
    int startingHealth,
    int currentHealth,
    int team,
    bool questSpawn,
    ulong questUID,
    string foeSymbolName)
{
    if (!isServer)
        return;

    ServerSpawnSavedInteriorEnemy(
        worldPosition,
        entityTypeInt,
        careerIndex,
        mobileGenderInt,
        isHostile,
        alliedToPlayer,
        startingHealth,
        currentHealth,
        team,
        questSpawn,
        questUID,
        foeSymbolName);
}

[Server]
public void ServerSpawnSavedInteriorEnemy(
    Vector3 worldPosition,
    int entityTypeInt,
    int careerIndex,
    int mobileGenderInt,
    bool isHostile,
    bool alliedToPlayer,
    int startingHealth,
    int currentHealth,
    int team,
    bool questSpawn,
    ulong questUID,
    string foeSymbolName)
{
    try
    {
        if (currentHealth <= 0)
            return;

        EntityTypes entityType = (EntityTypes)entityTypeInt;
        MobileGender gender = (MobileGender)mobileGenderInt;
        MobileReactions reaction = isHostile ? MobileReactions.Hostile : MobileReactions.Passive;
        MobileTypes mobileType = SavedEnemyMobileType(entityType, careerIndex);

        // If this was a quest foe but the host does not have the quest yet, use the
        // existing quest-spawn queue so QuestNetSync can request the quest packet from
        // the loading client, then spawn a proper quest foe after the quest exists.
        if (questSpawn && questUID != 0UL && !string.IsNullOrEmpty(foeSymbolName))
        {
            Quest qCheck = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
            if (qCheck == null)
            {
                Srv_QueueSingleQuestFoeSpawn(
                    worldPosition,
                    questUID,
                    foeSymbolName,
                    mobileType,
                    mobileGenderInt,
                    (int)SiteTypes.Building,
                    true,
                    reaction);

                Debug.Log($"[NetworkInteriorSave] Host missing quest uid={questUID}; queued saved interior quest foe '{foeSymbolName}' for network spawn after quest sync.");
                return;
            }
        }

        Foe foeRes = null;
        if (questSpawn && questUID != 0UL && !string.IsNullOrEmpty(foeSymbolName))
        {
            Quest q = QuestMachine.Instance != null ? QuestMachine.Instance.GetQuest(questUID) : null;
            if (q != null)
            {
                foeRes = q.GetFoe(new Symbol(foeSymbolName));
                if (foeRes == null)
                    Debug.LogWarning($"[NetworkInteriorSave] Quest uid={questUID} found, but foe '{foeSymbolName}' was not found. Saved interior enemy will spawn as a normal network enemy.");
            }
        }

        string displayName = questSpawn ? $"Saved Interior Quest Foe [{mobileType}]" : $"Saved Interior Enemy [{mobileType}]";
        GameObject go = GameObjectHelper.InstantiatePrefab(
            DaggerfallUnity.Instance.Option_EnemyPrefab.gameObject,
            displayName,
            null,
            worldPosition);

        go.transform.SetParent(null, false);
        go.transform.position = worldPosition;

        SetupDemoEnemy setupEnemy = go.GetComponent<SetupDemoEnemy>();
        if (setupEnemy != null)
        {
            setupEnemy.ApplyEnemySettings(entityType, careerIndex, gender, isHostile, alliedToPlayer);

            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            if (motor != null)
                motor.IsHostile = isHostile;

            setupEnemy.SyncedMotorIsHostile = isHostile;
            setupEnemy.SpawnedMotorIsHostile = isHostile;
            setupEnemy.CurrentMotorIsHostile = isHostile;
            setupEnemy.LastAppliedMotorIsHostile = isHostile;

            setupEnemy.isDungeonEnemy = false;
        }

        DaggerfallEnemy dfEnemy = go.GetComponent<DaggerfallEnemy>();
        if (dfEnemy != null)
        {
            dfEnemy.LoadID = DaggerfallUnity.NextUID;
            dfEnemy.QuestSpawn = (foeRes != null);
        }

        if (foeRes != null)
        {
            QuestResourceBehaviour qrb = go.GetComponent<QuestResourceBehaviour>() ?? go.AddComponent<QuestResourceBehaviour>();
            qrb.AssignResource(foeRes);
            foeRes.QuestResourceBehaviour = qrb;
            foeRes.RearmInjured();
        }

        DaggerfallEntityBehaviour entityBehaviour = go.GetComponent<DaggerfallEntityBehaviour>();
        EnemyEntity enemyEntity = entityBehaviour != null ? entityBehaviour.Entity as EnemyEntity : null;
        if (enemyEntity != null)
        {
            if (startingHealth > 0)
                enemyEntity.MaxHealth = startingHealth;

            int repairedHealth = Mathf.Clamp(currentHealth, 1, Mathf.Max(1, enemyEntity.MaxHealth));
            enemyEntity.SetHealth(repairedHealth, true);

            if (team > 0)
                enemyEntity.Team = (MobileTeams)(team - 1);
        }

        EnemyWorldPosition ewp = go.GetComponent<EnemyWorldPosition>();
        if (ewp != null)
        {
            ewp.SetSpawnContext(true, this.netId);
            ewp.intendedSpawnPos = worldPosition;
            ewp.isCreateFoeWaveSpawn = true;
        }

        if (setupEnemy != null)
            setupEnemy.ServerCaptureAuthoritativeSpawnHealth();

        NetworkIdentity ni = go.GetComponent<NetworkIdentity>();
        if (ni == null)
            ni = go.AddComponent<NetworkIdentity>();

        NetworkServer.Spawn(go);
        go.SetActive(true);

        GameManager.Instance?.RaiseOnEnemySpawnEvent(go);

        NetworkIdentity spawnedNi = go.GetComponent<NetworkIdentity>();
        if (spawnedNi != null && foeRes != null)
            RpcBindQuestFoe(spawnedNi.netId, questUID, foeSymbolName);

        Debug.Log($"[NetworkInteriorSave] Spawned saved MP interior enemy as network enemy type={mobileType} quest={questSpawn} questUID={questUID} foe='{foeSymbolName}' pos={worldPosition} hp={currentHealth}/{startingHealth}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"[NetworkInteriorSave] Failed to spawn saved MP interior enemy as network enemy. Exception={ex}");
    }
}

private static MobileTypes SavedEnemyMobileType(EntityTypes entityType, int careerIndex)
{
    if (careerIndex < 256)
    {
        if (entityType == EntityTypes.EnemyMonster)
            return (MobileTypes)careerIndex;

        if (entityType == EntityTypes.EnemyClass)
            return (MobileTypes)(careerIndex + 128);
    }

    return (MobileTypes)careerIndex;
}


	
	
	// dropped interior sync related line----------------------------------------------------------------------------------
	
	/*
	Dropped interior sync test
[Command]
public void CmdRequestInteriorFromHost(
    string region,
    string location,
    int buildingKey,
    StaticDoor door,
    uint myNetId)
{
    // Host finds player connection from netId
    if (NetworkServer.spawned.TryGetValue(myNetId, out NetworkIdentity identity))
    {
        var conn = identity.connectionToClient;

        // Proceed to spawn interior
        GameObject prefab = NetworkManager.singleton.spawnPrefabs
            .FirstOrDefault(p => p.GetComponent<DaggerfallInteriorNetwork>() != null);

        if (prefab != null)
        {
            GameObject netInterior = Instantiate(prefab);
            Vector3 spawnPos = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3);
            spawnPos.y -= 200f;
            netInterior.transform.position = spawnPos;
            netInterior.transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

            var netComp = netInterior.GetComponent<DaggerfallInteriorNetwork>();

            GameManager.Instance.PlayerGPS.GetDiscoveredBuilding(buildingKey, out PlayerGPS.DiscoveredBuilding discovery);

            netComp.SetInteriorData(region, location, buildingKey,
                door.ownerPosition.x, door.ownerPosition.y, door.ownerPosition.z,
                door, ClimateSwaps.FromAPIClimateBase(GameManager.Instance.PlayerGPS.ClimateSettings.ClimateType),
                discovery, null);

            NetworkServer.Spawn(netInterior);

            // Send only to the requesting client
            var data = new DaggerfallInteriorNetwork.InteriorNetworkData()
            {
                regionName = region,
                locationName = location,
                buildingKey = buildingKey,
                posX = door.ownerPosition.x,
                posY = door.ownerPosition.y,
                posZ = door.ownerPosition.z,
                recordIndex = door.recordIndex,
                doorPosition = door.centre,
                doorNormal = door.normal,
                doorOwnerPosition = door.ownerPosition,
                doorOwnerRotation = door.ownerRotation,
                buildingMatrix = door.buildingMatrix,
                climate = ClimateSwaps.FromAPIClimateBase(GameManager.Instance.PlayerGPS.ClimateSettings.ClimateType),
                discoveredBuilding = discovery,
            };

            netComp.TargetEnterInterior(conn, data);  // only for requester
        }
    }
}

	
	
[TargetRpc]
public void TargetEnterInterior(NetworkConnection target, uint interiorNetId)
{
    var interior = FindObjectsOfType<DaggerfallInteriorNetwork>()
        .FirstOrDefault(x => x.netId == interiorNetId);

    if (interior)
    {
        StartCoroutine(WaitAndEnterInterior(interior));
    }
    else
    {
        Debug.LogError("[TargetEnterInterior] Could not find interior object.");
    }
}

private IEnumerator WaitAndEnterInterior(DaggerfallInteriorNetwork interior)
{
    while (!interior.IsReady)
        yield return null;

    StaticDoor door = interior.RebuildStaticDoorFromSyncVars(); // ✅ Works now
    GameManager.Instance.PlayerEnterExit.TransitionInterior(null, door, true);
}





[ClientRpc]
public void RpcSyncInterior(uint interiorNetId)
{
    if (PlayerMultiplayer.GetLocalPlayerNetIdSafe() == (NetworkClient.localPlayer != null ? NetworkClient.localPlayer.netId : 0U))
    {
        Debug.Log("[RpcSyncInterior] Skipping sync for requester.");
        return;
    }

    Debug.Log($"[RpcSyncInterior] Client syncing interior with netId: {interiorNetId}");

    var interior = FindObjectsOfType<DaggerfallInteriorNetwork>()
        .FirstOrDefault(i => i.netId == interiorNetId);

    if (interior)
        StartCoroutine(WaitForInteriorData(interior));
    else
        Debug.LogError("[RpcSyncInterior] Could not find interior object.");
}

private IEnumerator WaitForInteriorData(DaggerfallInteriorNetwork interior)
{
    while (!interior.IsReady)
        yield return null;

    Debug.Log("[WaitForInteriorData] Interior is synced and ready on client.");
    // No enter transition — just keep it in the scene.
}

private Transform FindDoorOwnerFromScene(StaticDoor door)
{
    var allDoors = GameObject.FindObjectsOfType<DaggerfallStaticDoors>();
    foreach (var doors in allDoors)
    {
        foreach (var d in doors.Doors)
        {
            if (d.buildingKey == door.buildingKey && d.recordIndex == door.recordIndex)
                return doors.transform;
        }
    }
    return null;
}

*/

}	
