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
        hasWorldPosition = true;

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

        if (!hasWorldPosition)
            return false;

        GameManager gm = GameManager.Instance;
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
