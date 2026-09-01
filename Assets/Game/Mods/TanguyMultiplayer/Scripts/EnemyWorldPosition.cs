using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game;
using Mirror;

public class EnemyWorldPosition : NetworkBehaviour
{
    [SyncVar] public int worldX;

    [SyncVar] public Vector3 intendedSpawnPos = Vector3.zero; // CreateFoe suggested spawn position
    [SyncVar] public bool isCreateFoeWaveSpawn = false;             // mark for resnap-on-authority
    // Separate from the one-shot CreateFoe floor-settle marker above.
    // Only a single marker quest foe whose Foe resource is actually restrained
    // may be held at its spawn point while passive. This is cleared permanently
    // by DynamicEnemyAuthority as soon as the foe becomes hostile/released.
    [SyncVar] public bool isFixedQuestFoeRestrained = false;
    [SyncVar] public int worldZ;

    // NEW: who asked for this spawn & whether it was in an interior/dungeon
    [SyncVar] public bool isInteriorSpawn;
    [SyncVar] public uint requesterNetId;
    [SyncVar] public bool worldBakedFromRequester; // optional visibility/debug

    // Dungeon enemies are spawned in an artificial underground Unity space.
    // Their DF world X/Z should be anchored to the dungeon requester/entrance,
    // not calculated from the enemy's underground local X/Z offset.
    [SyncVar] public bool isDungeonSpawn;
    [SyncVar] public bool hasDungeonWorldAnchor;
    [SyncVar] public int dungeonAnchorWorldX;
    [SyncVar] public int dungeonAnchorWorldZ;

    // Once a generated DaggerfallDungeon has stamped imported enemies with its exact
    // entrance anchor, do not allow later requester-position based paths to overwrite
    // that anchor back to a stale tavern/house coordinate. This is metadata only and
    // does not move the enemy or affect authority.
    [SyncVar] public bool dungeonAnchorLocked;

    public DFPosition mapPixel;

    [Header("Debug Spawn Info")]
    public int playerWorldX;
    public int playerWorldZ;
    public Vector3 playerUnityPosition;
    public Vector3 spawnEnemyUnityPosition;

    public bool initialized { get; private set; } = false;

    // Track last known Unity position so we can update DF coords incrementally.
    // This avoids problems when enemies are hard-teleported after spawn (quest wave reposition, root reparent, etc.).
    private Vector3 lastUnityPos;

    // Treat sudden large deltas as a teleport and re-bake world coords from the requester/closest player.
    // This prevents DF world coords from being "wrong" after a server-side reposition.
    private const float TeleportRebakeThresholdUnity = 12f;

    // (duplicate block removed)

    // Correct conversion: 1 Unity unit = 40 Daggerfall meters
    private const float UnityToWorldUnit = 40f;

    // For client-owned exterior enemies, the server's Unity transform can pass through
    // intermediate 0<->819 seam frames because NetworkTransform interpolates raw Unity
    // coordinates. Never rebake logical DF worldX/worldZ from that server-side observer
    // transform. The owning client computes the DF position in its own local terrain
    // frame and publishes it here.
    private const float OwnerWorldPublishInterval = 0.10f;
    private float nextOwnerWorldPublishTime = 0f;

    // One exterior map-pixel terrain frame is 32768 DF units = 819.2 Unity units.
    // When baking DF coords from Unity offsets, normalize to the nearest terrain frame
    // so 817 units across a seam is treated like -2 units, not a 32700+ DF-unit jump.
    private const float TerrainFrameUnitySize = 819.2f;
    private const float TerrainFrameHalfUnitySize = TerrainFrameUnitySize * 0.5f;

    private float NormalizeTerrainFrameDelta(float delta)
    {
        if (isInteriorSpawn || isDungeonSpawn)
            return delta;

        while (delta > TerrainFrameHalfUnitySize)
            delta -= TerrainFrameUnitySize;

        while (delta < -TerrainFrameHalfUnitySize)
            delta += TerrainFrameUnitySize;

        return delta;
    }

    private Vector3 NormalizeExteriorUnityOffset(Vector3 offset)
    {
        offset.x = NormalizeTerrainFrameDelta(offset.x);
        offset.z = NormalizeTerrainFrameDelta(offset.z);
        return offset;
    }

    private bool IsRemoteClientOwnedOnServer()
    {
        if (!isServer)
            return false;

        NetworkIdentity ni = GetComponent<NetworkIdentity>();
        if (ni == null || ni.connectionToClient == null)
            return false;

        NetworkConnectionToClient hostConnection = NetworkServer.localConnection as NetworkConnectionToClient;
        return hostConnection == null || ni.connectionToClient != hostConnection;
    }

    void Update()
    {
        PublishAuthorityWorldPositionIfNeeded();
    }

    private void PublishAuthorityWorldPositionIfNeeded()
    {
        // Only the pure client owner should publish exterior logical world coords.
        // Host-owned enemies are already on the server and use the normal server routine.
        if (!NetworkClient.active || isServer || !hasAuthority)
            return;

        if (isInteriorSpawn || isDungeonSpawn)
            return;

        if (Time.unscaledTime < nextOwnerWorldPublishTime)
            return;

        if (GameManager.Instance == null ||
            GameManager.Instance.PlayerObject == null ||
            GameManager.Instance.PlayerGPS == null)
            return;

        if (GameManager.Instance.PlayerEnterExit != null &&
            GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
            return;

        nextOwnerWorldPublishTime = Time.unscaledTime + OwnerWorldPublishInterval;

        int baseWorldX = GameManager.Instance.PlayerGPS.WorldX;
        int baseWorldZ = GameManager.Instance.PlayerGPS.WorldZ;

        Vector3 offset = NormalizeExteriorUnityOffset(transform.position - GameManager.Instance.PlayerObject.transform.position);
        int offsetX = Mathf.RoundToInt(offset.x * UnityToWorldUnit);
        int offsetZ = Mathf.RoundToInt(offset.z * UnityToWorldUnit);

        CmdPublishAuthorityWorldPosition(baseWorldX, baseWorldZ, offsetX, offsetZ);
    }

    [Command(requiresAuthority = true)]
    private void CmdPublishAuthorityWorldPosition(int baseWorldX, int baseWorldZ, int offsetX, int offsetZ)
    {
        if (isInteriorSpawn || isDungeonSpawn)
            return;

        playerWorldX = baseWorldX;
        playerWorldZ = baseWorldZ;
        playerUnityPosition = Vector3.zero;
        spawnEnemyUnityPosition = transform.position;

        worldX = baseWorldX + offsetX;
        worldZ = baseWorldZ + offsetZ;
        mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
        worldBakedFromRequester = true;

        // The server-side transform may be mid-interpolation across the seam. Do not
        // allow UpdateRoutine to convert that raw Unity delta into another rebake.
        lastUnityPos = transform.position;
    }

    // Server-only setter used by host right before Spawn()
    [Server]
    public void SetSpawnContext(bool isInterior, uint requester)
    {
        isInteriorSpawn = isInterior;
        requesterNetId = requester;
        // worldBakedFromRequester stays false until we actually use requester to bake.
    }

    // Server-only setter for RDB/dungeon enemies.
    // Metadata only: this does NOT move, parent, or resize the enemy.
    // The anchor should normally be the requester player's PositionMultiplayer.x/z
    // at dungeon entry time. Do not apply enemy-player Unity X/Z offsets for dungeons.
    [Server]
    public void SetDungeonSpawnContext(uint requester, int anchorWorldX, int anchorWorldZ, bool hasAnchor)
    {
        isInteriorSpawn = true;
        isDungeonSpawn = true;
        requesterNetId = requester;

        // If DaggerfallDungeon already stamped this imported enemy with an exact entrance
        // anchor, do not let a later path overwrite it with requester PositionMultiplayer.
        // This is the popup/menu timing failure case: the requester can still publish the
        // old tavern/house DF coordinate while the host is paused in a message box.
        if (dungeonAnchorLocked && hasDungeonWorldAnchor)
        {
            if (!hasAnchor || anchorWorldX != dungeonAnchorWorldX || anchorWorldZ != dungeonAnchorWorldZ)
            {
                Debug.LogWarning($"[EnemyWorldPosition][DungeonAnchorLock] Preserved locked dungeon anchor on '{name}'. incomingHasAnchor={hasAnchor} incoming={anchorWorldX}/{anchorWorldZ} kept={dungeonAnchorWorldX}/{dungeonAnchorWorldZ} requester={requester}");

                // Keep worldX/worldZ aligned with the locked anchor if this was already initialized.
                if (initialized)
                    ApplyDungeonAnchorNow(FindRequesterPosition(), "locked-preserve");

                return;
            }
        }

        hasDungeonWorldAnchor = hasAnchor;
        dungeonAnchorWorldX = anchorWorldX;
        dungeonAnchorWorldZ = anchorWorldZ;
        worldBakedFromRequester = requester != 0;

        PositionMultiplayer requesterPos = null;

        // If the caller only knows the requester netId, resolve the DF X/Z anchor here.
        // This is for dungeon quest foes and late-stamped imported dungeon enemies.
        if (!hasDungeonWorldAnchor)
        {
            requesterPos = FindRequesterPosition();
            if (requesterPos != null)
            {
                dungeonAnchorWorldX = requesterPos.x;
                dungeonAnchorWorldZ = requesterPos.z;
                hasDungeonWorldAnchor = true;
                // requesterNetId == 0 is valid host requester in this project.
                worldBakedFromRequester = true;
            }
        }

        // If EnemyWorldPosition already initialized with the old normal requester/closest-player math,
        // repair only the DF X/Z metadata now. Do not touch transform/placement/Y.
        if (initialized && hasDungeonWorldAnchor)
            ApplyDungeonAnchorNow(requesterPos != null ? requesterPos : FindRequesterPosition(), "SetDungeonSpawnContext");
    }

    // Server-only locked setter for enemies imported directly by DaggerfallDungeon.GenerateDungeon().
    // Use this when the dungeon already has the exact exterior entrance anchor. This prevents
    // later requester-position rebakes from replacing it with a stale coordinate while the host
    // is paused by quest/message popups.
    [Server]
    public void SetDungeonSpawnContextLocked(uint requester, int anchorWorldX, int anchorWorldZ, bool hasAnchor, string reason)
    {
        SetDungeonSpawnContext(requester, anchorWorldX, anchorWorldZ, hasAnchor);

        if (hasAnchor)
        {
            dungeonAnchorLocked = true;
            hasDungeonWorldAnchor = true;
            dungeonAnchorWorldX = anchorWorldX;
            dungeonAnchorWorldZ = anchorWorldZ;

            // If this component already initialized before the lock was applied, repair
            // only the DF X/Z metadata now. Do not move the enemy.
            if (initialized)
                ApplyDungeonAnchorNow(FindRequesterPosition(), "locked-" + reason);

            Debug.Log($"[EnemyWorldPosition][DungeonAnchorLock] Locked dungeon anchor on '{name}' requester={requester} anchor={anchorWorldX}/{anchorWorldZ} reason={reason}");
        }
    }

    [Server]
    private void PromoteSetupDungeonFlagIfPresent()
    {
        if (isDungeonSpawn)
            return;

        SetupDemoEnemy setup = GetComponent<SetupDemoEnemy>();
        if (setup != null && setup.isDungeonEnemy)
        {
            isInteriorSpawn = true;
            isDungeonSpawn = true;
            Debug.Log($"[EnemyWorldPosition] Promoted SetupDemoEnemy.isDungeonEnemy to dungeon-anchor mode on '{name}' requester={requesterNetId}");
        }
    }

    void Start()
    {
        if (!isServer)
            return;

        // Safety: some spawn paths set SetupDemoEnemy.isDungeonEnemy but do not call
        // SetDungeonSpawnContext(). Promote those to dungeon-anchor DF X/Z math.
        // This changes only worldX/worldZ metadata, never transform placement.
        PromoteSetupDungeonFlagIfPresent();

        PositionMultiplayer chosen = FindRequesterPosition();

        // Dungeon enemies are in artificial underground Unity space. Their X/Z DF coords
        // should start at the dungeon requester/entrance anchor, not at requester + local dungeon offset.
        if (isDungeonSpawn)
        {
            if (!hasDungeonWorldAnchor && chosen != null)
            {
                dungeonAnchorWorldX = chosen.x;
                dungeonAnchorWorldZ = chosen.z;
                hasDungeonWorldAnchor = true;
                worldBakedFromRequester = true;
            }

            if (!hasDungeonWorldAnchor)
            {
                chosen = FindClosestPosition();
                if (chosen != null)
                {
                    dungeonAnchorWorldX = chosen.x;
                    dungeonAnchorWorldZ = chosen.z;
                    hasDungeonWorldAnchor = true;
                    worldBakedFromRequester = false;
                }
            }

            if (hasDungeonWorldAnchor)
            {
                InitializeDungeonFromAnchor(chosen);
                StartCoroutine(UpdateRoutine());
            }
            else
            {
                Debug.LogWarning("[EnemyWorldPosition] No dungeon world anchor or PositionMultiplayer found to bake dungeon enemy world coords.");
            }

            return;
        }

        // Normal exterior/interior/quest enemy path: prefer requester, then closest player,
        // and include the Unity X/Z offset in DF world coordinates.
        if (chosen == null)
            chosen = FindClosestPosition();

        if (chosen != null)
        {
            InitializeFromPlayer(chosen);
            StartCoroutine(UpdateRoutine());
        }
        else
        {
            Debug.LogWarning("[EnemyWorldPosition] No PositionMultiplayer found to bake world coords.");
        }
    }

    private PositionMultiplayer FindRequesterPosition()
    {
        // IMPORTANT: In this multiplayer branch, requesterNetId == 0 is the host player.
        // Do not treat 0 as "missing requester" for dungeon enemies, or host-created
        // dungeons can incorrectly anchor to a remote client via closest-player fallback.
        if (requesterNetId == 0U)
            return FindHostRequesterPosition();

        foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
        {
            if (pm == null)
                continue;

            var ni = pm.GetComponent<NetworkIdentity>();
            if (ni != null && ni.netId == requesterNetId)
            {
                worldBakedFromRequester = true;
                return pm;
            }

            PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
            if (player != null && player.netId == requesterNetId)
            {
                worldBakedFromRequester = true;
                return pm;
            }
        }

        return null;
    }

    private PositionMultiplayer FindHostRequesterPosition()
    {
        // 1) In host mode, PlayerMultiplayer.localPlayer is the host player's MP object.
        if (PlayerMultiplayer.localPlayer != null)
        {
            PositionMultiplayer pm = PlayerMultiplayer.localPlayer.GetComponent<PositionMultiplayer>();
            if (pm != null)
            {
                worldBakedFromRequester = true;
                return pm;
            }
        }

        // 2) Mirror host-mode local connection identity, when available.
        if (NetworkServer.localConnection != null && NetworkServer.localConnection.identity != null)
        {
            PositionMultiplayer pm = NetworkServer.localConnection.identity.GetComponent<PositionMultiplayer>();
            if (pm != null)
            {
                worldBakedFromRequester = true;
                return pm;
            }
        }

        // 3) Local player object on the server.
        foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
        {
            if (pm == null)
                continue;

            PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
            if (player != null && player.isLocalPlayer)
            {
                worldBakedFromRequester = true;
                return pm;
            }
        }

        // 4) Explicit netId 0, for branches where host PlayerMultiplayer reports netId 0.
        foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
        {
            if (pm == null)
                continue;

            var ni = pm.GetComponent<NetworkIdentity>();
            if (ni != null && ni.netId == 0U)
            {
                worldBakedFromRequester = true;
                return pm;
            }

            PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
            if (player != null && player.netId == 0U)
            {
                worldBakedFromRequester = true;
                return pm;
            }
        }

        // 5) Last host-only fallback: PositionMultiplayer closest to the real local PlayerObject.
        // This is only used for requesterNetId == 0. Remote client requesters never use this path.
        if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
        {
            Vector3 hostUnityPos = GameManager.Instance.PlayerObject.transform.position;
            PositionMultiplayer best = null;
            float bestDistance = float.MaxValue;

            foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
            {
                if (pm == null)
                    continue;

                float d = Vector3.Distance(hostUnityPos, pm.transform.position);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = pm;
                }
            }

            if (best != null)
            {
                worldBakedFromRequester = true;
                return best;
            }
        }

        return null;
    }

    private PositionMultiplayer FindClosestPosition()
    {
        PositionMultiplayer chosen = null;
        float closestDistance = float.MaxValue;

        foreach (var p in FindObjectsOfType<PositionMultiplayer>())
        {
            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < closestDistance)
            {
                closestDistance = d;
                chosen = p;
            }
        }

        return chosen;
    }

    public void InitializeFromPlayer(PositionMultiplayer position)
    {
        if (initialized || position == null)
            return;

        playerWorldX = position.x;
        playerWorldZ = position.z;

        playerUnityPosition = position.transform.position;
        spawnEnemyUnityPosition = transform.position;

        Vector3 offset = NormalizeExteriorUnityOffset(spawnEnemyUnityPosition - playerUnityPosition);

        int offsetX = Mathf.RoundToInt(offset.x * UnityToWorldUnit);
        int offsetZ = Mathf.RoundToInt(offset.z * UnityToWorldUnit);

        worldX = playerWorldX + offsetX;
        worldZ = playerWorldZ + offsetZ;

        mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);

        // Use incremental updates from this point onward. This is resilient to later
        // hard teleports / quest-driven repositioning and does not depend on the
        // original spawn position remaining fixed.
        lastUnityPos = transform.position;

        Debug.Log($"[EnemyWorldPosition] Bake from {(worldBakedFromRequester ? "requester" : "closest")} " +
                  $"X={playerWorldX},Z={playerWorldZ} + d({offsetX},{offsetZ}) => enemy X={worldX},Z={worldZ} mp={mapPixel.X}/{mapPixel.Y} (isInterior={isInteriorSpawn}, requester={requesterNetId})");

        initialized = true;
    }

    public void InitializeDungeonFromAnchor(PositionMultiplayer debugPosition)
    {
        if (initialized)
            return;

        ApplyDungeonAnchorNow(debugPosition, "initial bake");
    }

    [Server]
    private void ApplyDungeonAnchorNow(PositionMultiplayer debugPosition, string reason)
    {
        playerWorldX = dungeonAnchorWorldX;
        playerWorldZ = dungeonAnchorWorldZ;

        playerUnityPosition = debugPosition != null ? debugPosition.transform.position : Vector3.zero;
        spawnEnemyUnityPosition = transform.position;

        // Critical dungeon rule: do NOT add the underground/local Unity X/Z offset.
        // All dungeon enemies start at the dungeon entrance/requester DF X/Z, then only
        // their own actual Unity movement inside the dungeon adjusts worldX/worldZ.
        worldX = dungeonAnchorWorldX;
        worldZ = dungeonAnchorWorldZ;

        mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
        lastUnityPos = transform.position;

        Debug.Log($"[EnemyWorldPosition] Dungeon anchor {reason} from {(worldBakedFromRequester ? "requester" : "fallback")} " +
                  $"anchor X={worldX},Z={worldZ} mp={mapPixel.X}/{mapPixel.Y} " +
                  $"enemyUnity={spawnEnemyUnityPosition} requester={requesterNetId}");

        initialized = true;
    }

    public void NoteExternalSeamFrameCorrection()
    {
        lastUnityPos = transform.position;

        // On the server/host, immediately repair logical DF coordinates after a
        // scene-root terrain-frame wrap. But do not rebake remote-client-owned
        // exterior enemies from the server observer transform; the owner publishes
        // those logical DF coordinates directly.
        if (isServer && initialized && !IsRemoteClientOwnedOnServer())
            ReBakeFromRequesterOrClosest();
    }

    System.Collections.IEnumerator UpdateRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);

            if (!initialized)
                continue;

            // For remote-client-owned exterior enemies, the server transform is an
            // observer/interpolation artifact around terrain seams. The owner publishes
            // worldX/worldZ via CmdPublishAuthorityWorldPosition(), so never rebake or
            // increment logical DF coordinates from this server-side Unity transform.
            if (IsRemoteClientOwnedOnServer() && !isInteriorSpawn && !isDungeonSpawn)
            {
                lastUnityPos = transform.position;
                continue;
            }

            Vector3 cur = transform.position;
            Vector3 delta = cur - lastUnityPos;

            // If the enemy was hard-teleported (common for quest wave spawns), re-bake from requester/closest.
            // IMPORTANT: ignore Y so falling does not trigger a re-bake.
            float jumpXZ2 = delta.x * delta.x + delta.z * delta.z;
            if (jumpXZ2 >= TeleportRebakeThresholdUnity * TeleportRebakeThresholdUnity)
            {
                ReBakeFromRequesterOrClosest();
                lastUnityPos = cur;
                continue;
            }

            // Incremental DF update based on actual movement since last tick.
            int dx = Mathf.RoundToInt(delta.x * UnityToWorldUnit);
            int dz = Mathf.RoundToInt(delta.z * UnityToWorldUnit);

            if (dx != 0 || dz != 0)
            {
                worldX += dx;
                worldZ += dz;
                mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
            }

            lastUnityPos = cur;
        }
    }

    // Server-only: re-bake world coords using the requester (if present) or closest player.
    // This is used when a quest spawner teleports the enemy after creation.
    [Server]
    private void ReBakeFromRequesterOrClosest()
    {
        if (IsRemoteClientOwnedOnServer() && !isInteriorSpawn && !isDungeonSpawn)
        {
            lastUnityPos = transform.position;
            return;
        }

        if (isDungeonSpawn)
        {
            PositionMultiplayer requester = FindRequesterPosition();
            if (!hasDungeonWorldAnchor && requester != null)
            {
                dungeonAnchorWorldX = requester.x;
                dungeonAnchorWorldZ = requester.z;
                hasDungeonWorldAnchor = true;
                worldBakedFromRequester = true;
            }

            if (!hasDungeonWorldAnchor)
            {
                PositionMultiplayer closest = FindClosestPosition();
                if (closest == null)
                    return;

                dungeonAnchorWorldX = closest.x;
                dungeonAnchorWorldZ = closest.z;
                hasDungeonWorldAnchor = true;
                requester = closest;
                worldBakedFromRequester = false;
            }

            playerWorldX = dungeonAnchorWorldX;
            playerWorldZ = dungeonAnchorWorldZ;
            playerUnityPosition = requester != null ? requester.transform.position : Vector3.zero;
            spawnEnemyUnityPosition = transform.position;

            // Dungeon re-bake also ignores Unity X/Z offset. This prevents root moves or
            // dungeon-local teleports from converting fake underground offsets into DF distance.
            worldX = dungeonAnchorWorldX;
            worldZ = dungeonAnchorWorldZ;
            mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);

            Debug.Log($"[EnemyWorldPosition] Dungeon anchor re-bake after teleport " +
                      $"anchor X={worldX},Z={worldZ} mp={mapPixel.X}/{mapPixel.Y} requester={requesterNetId}");
            return;
        }

        PositionMultiplayer chosen = FindRequesterPosition();
        if (chosen == null)
            chosen = FindClosestPosition();

        if (chosen == null)
            return;

        playerWorldX = chosen.x;
        playerWorldZ = chosen.z;
        playerUnityPosition = chosen.transform.position;
        spawnEnemyUnityPosition = transform.position; // debug

        // Exterior terrain seam fix: server copies of client-owned enemies can receive
        // Unity poses in a different 0<->819.2 terrain frame than the requester player.
        // Normalize the player-relative offset before converting it to DF world coords,
        // otherwise a harmless seam wrap becomes a fake 32768 DF-unit jump and the
        // authority/destroy logic thinks the enemy is far away.
        Vector3 offset = NormalizeExteriorUnityOffset(spawnEnemyUnityPosition - playerUnityPosition);
        int offsetX = Mathf.RoundToInt(offset.x * UnityToWorldUnit);
        int offsetZ = Mathf.RoundToInt(offset.z * UnityToWorldUnit);

        worldX = playerWorldX + offsetX;
        worldZ = playerWorldZ + offsetZ;
        mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);

        Debug.Log($"[EnemyWorldPosition] Re-bake after teleport from {(worldBakedFromRequester ? "requester" : "closest")} " +
                  $"X={playerWorldX},Z={playerWorldZ} + d({offsetX},{offsetZ}) => enemy X={worldX},Z={worldZ} (isInterior={isInteriorSpawn}, requester={requesterNetId})");
    }
}
