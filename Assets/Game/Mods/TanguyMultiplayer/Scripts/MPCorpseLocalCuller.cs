using System.Collections;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;

/// <summary>
/// Local-only MP visibility/interaction culler for corpse loot markers created from network enemies.
/// Corpse markers are not networked enemies, so DynamicEnemyAuthority cannot hide them after the
/// original enemy dies. This component copies the dead enemy's EnemyWorldPosition data at corpse
/// creation time and hides/ghosts the local corpse when this local player is far away.
/// </summary>
public class MPCorpseLocalCuller : MonoBehaviour
{
    private const float CullDistanceUnity = 200f;
    private const float UnityPerDF = 1f / 40f;
    private const float ActualNetworkDungeonY = -300f;
    private const float CheckInterval = 0.5f;

    [SerializeField] private int worldX;
    [SerializeField] private int worldZ;
    [SerializeField] private bool hasWorldPosition;
    [SerializeField] private bool isDungeonSpawn;

    private string dungeonInstanceId;
    private string replayArea;
    private double replayWorldX, replayWorldZ;
    private float replayExteriorY;
    // Keep the server's original height separate from the locally grounded height.
    // Repeated pages contain that original value, not our corrected floor placement.
    private float replaySourceY;
    private bool replayGroundingComplete;
    private bool replayOwnsPosition;
    private bool replayNeedsGrounding;
    private float replayGroundProbeY;
    private int replayGroundAttempts;

    public void RequestReplayGrounding(float compensatedEnemyY)
    {
        if ((replayNeedsGrounding || replayGroundingComplete) &&
            Mathf.Approximately(replayGroundProbeY, compensatedEnemyY)) return;
        replayGroundingComplete = false;
        replayGroundProbeY = compensatedEnemyY;
        replayNeedsGrounding = true;
        replayGroundAttempts = 0;
    }

    private void TryGroundReplay(GameManager gm)
    {
        if (!replayNeedsGrounding || !replayOwnsPosition) return;
        float compensation = gm.StreamingWorld.WorldCompensation.y;
        Vector3 probe = transform.position;
        probe.y = replayGroundProbeY + compensation + 0.5f;
        RaycastHit[] hits = Physics.RaycastAll(probe, Vector3.down, 16.5f, ~0, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        Vector3 ground = Vector3.zero;
        bool found = false;
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.normal.y <= 0.1f || hit.distance >= nearest) continue;
            var target = hit.collider.transform;
            if (target == transform || target.IsChildOf(transform) ||
                target.GetComponentInParent<DaggerfallWorkshop.DaggerfallLoot>() != null ||
                target.GetComponentInParent<DaggerfallWorkshop.Game.Entity.DaggerfallEntityBehaviour>() != null ||
                target.GetComponentInParent<NetworkIdentity>()?.GetComponent<PlayerMultiplayer>() != null) continue;
            ground = hit.point; nearest = hit.distance; found = true;
        }
        if (found)
        {
            // Normal DFU ground placement: billboard centre is half its own height
            // above the floor. Imported 3D loot uses its original ground pivot.
            var billboard = GetComponent<DaggerfallWorkshop.Billboard>();
            float offset = billboard != null ? billboard.Summary.Size.y * 0.5f : 0f;
            Vector3 placed = transform.position;
            placed.y = ground.y + offset;
            transform.position = placed;
            replayExteriorY = placed.y - compensation;
            replayNeedsGrounding = false;
            replayGroundingComplete = true;
        }
        else if (++replayGroundAttempts >= 20)
        {
            replayNeedsGrounding = false;
            Debug.LogWarning("[CorpseReplay] Ground unavailable for " + gameObject.name + "; preserving recorded height.");
        }
    }

    private Renderer[] cachedRenderers;
    private bool[] rendererInitialEnabled;
    private Collider[] cachedColliders;
    private bool[] colliderInitialEnabled;
    private AudioSource[] cachedAudioSources;
    private bool[] audioInitialEnabled;

    private bool isCulled;
    private Coroutine checkCoroutine;

    public void InitializeFromEnemyWorldPosition(EnemyWorldPosition enemyWorldPosition)
    {
        if (enemyWorldPosition == null)
            return;

        worldX = enemyWorldPosition.worldX;
        worldZ = enemyWorldPosition.worldZ;
        isDungeonSpawn = enemyWorldPosition.isDungeonSpawn;
        dungeonInstanceId = enemyWorldPosition.PresentationDungeonInstanceId;
        hasWorldPosition = true;

        RefreshCaches();
        ApplyCullState(ShouldCullForLocalPlayer());
    }

    // Replay has no living EnemyWorldPosition component to copy from.
    public void InitializeForDungeon(string instanceId)
    {
        dungeonInstanceId = instanceId;
        isDungeonSpawn = true;
        RefreshCaches();
        ApplyCullState(ShouldCullForLocalPlayer());
    }

    public void InitializeForReplay(string area, double x, double z, float exteriorY, bool positionFromWorld)
    {
        // Restore cached enabled states before recaching an already-culled object.
        // Otherwise a later snapshot could remember "hidden" as its visible state.
        ApplyCullState(false);
        bool samePlacement = replayArea == area && replayWorldX == x && replayWorldZ == z &&
            Mathf.Approximately(replaySourceY, exteriorY);
        replayOwnsPosition = positionFromWorld || (replayOwnsPosition && replayArea == area);
        replayArea = area; replayWorldX = x; replayWorldZ = z;
        if (!samePlacement)
        {
            replaySourceY = exteriorY;
            replayExteriorY = exteriorY;
            replayGroundingComplete = false;
            replayNeedsGrounding = false;
            replayGroundAttempts = 0;
        }
        // For the same record retain the grounded Y and any in-progress retry.
        // Floating-origin shifts still apply through WorldCompensation in placement.
        RefreshCaches();
        ApplyCullState(ShouldCullForLocalPlayer());
    }

    private void Awake()
    {
        RefreshCaches();
    }

    private void OnEnable()
    {
        if (checkCoroutine == null)
            checkCoroutine = StartCoroutine(CheckRoutine());
    }

    private void OnDisable()
    {
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
            checkCoroutine = null;
        }

        // Do not leave colliders/renderers disabled when this object is pooled/re-enabled by other code.
        ApplyCullState(false);
    }

    private IEnumerator CheckRoutine()
    {
        // Let the loot billboard/collider finish initializing for one frame.
        yield return null;

        while (true)
        {
            ApplyCullState(ShouldCullForLocalPlayer());
            yield return new WaitForSeconds(CheckInterval);
        }
    }

    private void RefreshCaches()
    {
        cachedRenderers = GetComponentsInChildren<Renderer>(true);
        rendererInitialEnabled = new bool[cachedRenderers.Length];
        for (int i = 0; i < cachedRenderers.Length; i++)
            rendererInitialEnabled[i] = cachedRenderers[i] != null && cachedRenderers[i].enabled;

        cachedColliders = GetComponentsInChildren<Collider>(true);
        colliderInitialEnabled = new bool[cachedColliders.Length];
        for (int i = 0; i < cachedColliders.Length; i++)
            colliderInitialEnabled[i] = cachedColliders[i] != null && cachedColliders[i].enabled;

        cachedAudioSources = GetComponentsInChildren<AudioSource>(true);
        audioInitialEnabled = new bool[cachedAudioSources.Length];
        for (int i = 0; i < cachedAudioSources.Length; i++)
            audioInitialEnabled[i] = cachedAudioSources[i] != null && cachedAudioSources[i].enabled;
    }

    private bool ShouldCullForLocalPlayer()
    {
        // Only MP needs this. In SP, corpse behaviour should remain original DFU behaviour.
        if (!NetworkClient.active && !NetworkServer.active)
            return false;

        GameManager gm = GameManager.Instance;
        if (!string.IsNullOrEmpty(replayArea))
        {
            if (NetworkServer.active && !NetworkClient.active) return true;
            Transform root;
            if (LootCatcher.CurrentCorpseArea(out root) != replayArea || gm == null || gm.PlayerObject == null) return true;
            if (replayArea == "E" || replayArea.StartsWith("B:", System.StringComparison.Ordinal))
            {
                double dx = (replayWorldX - gm.PlayerGPS.WorldX) * UnityPerDF;
                double dz = (replayWorldZ - gm.PlayerGPS.WorldZ) * UnityPerDF;
                // World-coordinate corpse records cover exteriors and exact buildings.
                // Keep their logical world position correct across local terrain seams.
                if (replayOwnsPosition && dx * dx + dz * dz <= 250.0 * 250.0)
                {
                    Vector3 player = gm.PlayerObject.transform.position;
                    transform.position = new Vector3(player.x + (float)dx,
                        gm.StreamingWorld.WorldCompensation.y + replayExteriorY, player.z + (float)dz);
                }
                if (dx * dx + dz * dz <= CullDistanceUnity * CullDistanceUnity) TryGroundReplay(gm);
                return dx * dx + dz * dz > CullDistanceUnity * CullDistanceUnity;
            }
            return Vector3.Distance(gm.PlayerObject.transform.position, transform.position) > CullDistanceUnity;
        }
        if (!string.IsNullOrEmpty(dungeonInstanceId))
        {
            if (NetworkServer.active && !NetworkClient.active) return true;
            var enter = gm != null ? gm.PlayerEnterExit : null;
            if (enter == null || !enter.IsPlayerInsideDungeon || enter.Dungeon == null ||
                enter.Dungeon.DungeonInstanceId != dungeonInstanceId) return true;
            return gm.PlayerObject != null &&
                Vector3.Distance(gm.PlayerObject.transform.position, transform.position) > CullDistanceUnity;
        }
        if (!hasWorldPosition) return false;

        if (gm == null || gm.PlayerObject == null || gm.PlayerGPS == null)
            return false;

        Vector3 localPlayerPos = gm.PlayerObject.transform.position;

        // Real underground network dungeon corpses should use Unity space, because dungeon DF X/Z
        // is the entrance anchor and player DF X/Z does not change inside the dungeon.
        if (isDungeonSpawn && transform.position.y <= ActualNetworkDungeonY)
            return Vector3.Distance(localPlayerPos, transform.position) > CullDistanceUnity;

        // Exterior/building-interior corpses use DF X/Z + Unity Y, same idea as enemy culling.
        // This prevents a player in a different DF world area but similar Unity-local X/Z from seeing/interacting.
        if (worldX == 0 && worldZ == 0)
            return false;

        int localWorldX = gm.PlayerGPS.WorldX;
        int localWorldZ = gm.PlayerGPS.WorldZ;
        if (localWorldX == 0 && localWorldZ == 0)
            return false;

        float dxU = (localWorldX - worldX) * UnityPerDF;
        float dzU = (localWorldZ - worldZ) * UnityPerDF;
        float dyU = localPlayerPos.y - transform.position.y;
        float distU = Mathf.Sqrt(dxU * dxU + dzU * dzU + dyU * dyU);

        return distU > CullDistanceUnity;
    }

    private void ApplyCullState(bool cull)
    {
        if (isCulled == cull)
            return;

        isCulled = cull;

        if (cachedRenderers == null || cachedColliders == null || cachedAudioSources == null)
            RefreshCaches();

        for (int i = 0; i < cachedRenderers.Length; i++)
        {
            if (cachedRenderers[i] != null)
                cachedRenderers[i].enabled = rendererInitialEnabled[i] && !cull;
        }

        for (int i = 0; i < cachedColliders.Length; i++)
        {
            if (cachedColliders[i] != null)
                cachedColliders[i].enabled = colliderInitialEnabled[i] && !cull;
        }

        for (int i = 0; i < cachedAudioSources.Length; i++)
        {
            if (cachedAudioSources[i] != null)
                cachedAudioSources[i].enabled = audioInitialEnabled[i] && !cull;
        }
    }
}
